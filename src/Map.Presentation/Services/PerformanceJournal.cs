using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("Map.Presentation.Tests")]

namespace Map.Presentation.Services;

public enum PerformanceGesture { Idle, Pan, Zoom, Edit }

/// <summary>Outstanding metadata/build work includes the active worker; pixels include ready results awaiting adoption.</summary>
public readonly record struct PerformanceQueues(int Metadata, int Builds, int Pixels, int Decodes, int LatePixels);

/// <summary>Per-frame deltas, never cumulative cache totals. All values are copied; no renderer objects are retained.</summary>
public readonly record struct PerformanceFrame(
    double WorkMilliseconds, double IntervalMilliseconds, PerformanceGesture Gesture,
    long CacheHits = 0, long CacheMisses = 0, long CacheInvalidations = 0,
    int DetailTilesPending = 0, int UnresolvedImages = 0, int MissingAssets = 0,
    PerformanceQueues Queues = default, bool QueueSnapshotUnavailable = false,
    int TerrainRefinementsPending = 0, int HexChunksPending = 0);

/// <summary>
/// One render-thread producer, one background consumer. RecordFrame never locks, allocates or does IO.
/// Windows use monotonic time, anchored to session UTC; percentiles use nearest rank. 1% low FPS is
/// 1000 / mean of the slowest ceil(1% * N) positive frame intervals (including idle gaps).
/// Fixed storage bounds overload: dropped frames and percentile sample overflow are explicit in each row.
/// Dispose after rendering stops; shutdown flushes a partial window and exactly one session worstMoment.
/// Each window also carries its worst frame so a running/crashed session remains diagnosable.
/// </summary>
public sealed class PerformanceJournal : IDisposable
{
    internal const int Capacity = 16384;
    internal const long DiskLimitBytes = 20L * 1024 * 1024;
    private const int FileLimit = 7;
    private static readonly long WindowTicks = 10 * Stopwatch.Frequency;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly record struct Sample(long Timestamp, PerformanceFrame Frame);
    private readonly Sample[] _ring = new Sample[Capacity];
    private readonly double[] _work = new double[Capacity];
    private readonly double[] _intervals = new double[Capacity];
    private readonly string _directory;
    private readonly long _started;
    private readonly DateTimeOffset _startedUtc;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private readonly Task? _worker;
    private int _read, _write, _disposed;
    private long _dropped, _ioErrors;
    private long _windowStart, _frames, _hits, _misses, _invalidations, _over16, _over33, _intervalOver16, _intervalOver33;
    private long _lastDrain;
    private long _pan, _zoom, _idle, _edit, _queueUnavailable, _peakWorkingSet;
    private long _processPeakWorkingSet;
    private int _workCount, _intervalCount;
    private int _detail, _unresolved, _missing, _refinements, _hex;
    private PerformanceQueues _queues;
    private Sample _worstWindow, _worstSession;
    // A seqlock preserves even a dropped frame if it is the session's slowest. The consumer tries once.
    private int _worstVersion;
    private Sample _producerWorst;
    private bool _hasWorst;
    private double _maxWork;

    public PerformanceJournal(string profileFolder)
        : this(profileFolder, true, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow) { }

    internal PerformanceJournal(string profileFolder, bool startWorker, long started, DateTimeOffset startedUtc)
    {
        _directory = Path.Combine(profileFolder, "diagnostics");
        _started = _windowStart = started;
        _lastDrain = started;
        _startedUtc = startedUtc;
        if (startWorker) _worker = Task.Run(RunAsync);
    }

    public long IoErrors => Interlocked.Read(ref _ioErrors);
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    public void RecordFrame(in PerformanceFrame frame) => RecordFrame(frame, Stopwatch.GetTimestamp());

    internal void RecordFrame(in PerformanceFrame frame, long timestamp)
    {
        if (Volatile.Read(ref _disposed) != 0 || !double.IsFinite(frame.WorkMilliseconds)
            || frame.WorkMilliseconds < 0 || !double.IsFinite(frame.IntervalMilliseconds)
            || frame.IntervalMilliseconds < 0) return;
        var sample = new Sample(timestamp, frame);
        if (!_hasWorst || frame.WorkMilliseconds > _producerWorst.Frame.WorkMilliseconds)
        {
            Interlocked.Increment(ref _worstVersion);
            _producerWorst = sample;
            _hasWorst = true;
            Interlocked.Increment(ref _worstVersion);
        }
        var write = _write;
        var next = (write + 1) & (Capacity - 1);
        if (next == Volatile.Read(ref _read)) { Interlocked.Increment(ref _dropped); return; }
        _ring[write] = sample;
        Volatile.Write(ref _write, next);
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                DrainWithMemory(finish: false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { DrainWithMemory(finish: true); }
    }

    private void DrainWithMemory(bool finish)
    {
        var (resident, peak) = ReadWorkingSet();
        _processPeakWorkingSet = Math.Max(_processPeakWorkingSet, peak);
        if (finish) Finish(Stopwatch.GetTimestamp(), resident);
        else Drain(Stopwatch.GetTimestamp(), resident);
    }

    private static (long Resident, long LifetimePeak) ReadWorkingSet()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            // Current resident set sampled once a second; this is a sampled window peak, not a per-frame OS query.
            return (process.WorkingSet64, process.PeakWorkingSet64);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException) { return default; }
    }

    internal void Drain(long now, long workingSet)
    {
        _lastDrain = now;
        _peakWorkingSet = Math.Max(_peakWorkingSet, workingSet);
        // Snapshot bounds work even if the producer is much faster than the consumer.
        var end = Volatile.Read(ref _write);
        var read = _read;
        while (read != end)
        {
            var sample = _ring[read];
            Advance(sample.Timestamp, workingSet);
            Add(sample);
            read = (read + 1) & (Capacity - 1);
            Volatile.Write(ref _read, read);
        }
        var version = Volatile.Read(ref _worstVersion);
        var worst = _producerWorst;
        var hasWorst = _hasWorst;
        Thread.MemoryBarrier();
        if ((version & 1) == 0 && version == Volatile.Read(ref _worstVersion) && hasWorst
            && (!_hasSessionWorst || worst.Frame.WorkMilliseconds > _worstSession.Frame.WorkMilliseconds))
        { _worstSession = worst; _hasSessionWorst = true; }
        // Do not close a window that still has queued frames from it.
        if (read == Volatile.Read(ref _write)) Advance(now, workingSet);
    }

    private bool _hasSessionWorst;

    private void Advance(long timestamp, long workingSet)
    {
        if (timestamp - _windowStart < WindowTicks) return;
        WriteWindow(_windowStart + WindowTicks, false);
        // Empty time needs no rows or catch-up loop (a sleeping machine may resume days later).
        _windowStart += ((timestamp - _windowStart) / WindowTicks) * WindowTicks;
        _peakWorkingSet = workingSet;
    }

    private void Add(Sample sample)
    {
        var frame = sample.Frame;
        _frames++;
        if (_workCount < Capacity) _work[_workCount++] = frame.WorkMilliseconds;
        // DEC-109 is about frames while the user acts: an interval that ends in an idle frame is a pause, not a slow
        // frame (an idle window would otherwise report «1 % low 0.5 FPS»).
        if (frame.IntervalMilliseconds > 0 && frame.Gesture != PerformanceGesture.Idle)
        {
            if (_intervalCount < Capacity) _intervals[_intervalCount++] = frame.IntervalMilliseconds;
            if (frame.IntervalMilliseconds > 16.7) _intervalOver16++;
            if (frame.IntervalMilliseconds > 33) _intervalOver33++;
        }
        if (_frames == 1 || frame.WorkMilliseconds > _maxWork) _worstWindow = sample;
        _maxWork = Math.Max(_maxWork, frame.WorkMilliseconds);
        if (!_hasSessionWorst || frame.WorkMilliseconds > _worstSession.Frame.WorkMilliseconds)
        { _worstSession = sample; _hasSessionWorst = true; }
        if (frame.WorkMilliseconds > 16.7) _over16++;
        if (frame.WorkMilliseconds > 33) _over33++;
        _hits += frame.CacheHits; _misses += frame.CacheMisses; _invalidations += frame.CacheInvalidations;
        switch (frame.Gesture)
        {
            case PerformanceGesture.Pan: _pan++; break;
            case PerformanceGesture.Zoom: _zoom++; break;
            case PerformanceGesture.Edit: _edit++; break;
            default: _idle++; break;
        }
        if (frame.QueueSnapshotUnavailable) _queueUnavailable++;
        _detail = Math.Max(_detail, frame.DetailTilesPending);
        _unresolved = Math.Max(_unresolved, frame.UnresolvedImages);
        _missing = Math.Max(_missing, frame.MissingAssets);
        _refinements = Math.Max(_refinements, frame.TerrainRefinementsPending);
        _hex = Math.Max(_hex, frame.HexChunksPending);
        _queues = new(Math.Max(_queues.Metadata, frame.Queues.Metadata), Math.Max(_queues.Builds, frame.Queues.Builds),
            Math.Max(_queues.Pixels, frame.Queues.Pixels), Math.Max(_queues.Decodes, frame.Queues.Decodes),
            Math.Max(_queues.LatePixels, frame.Queues.LatePixels));
    }

    private DateTimeOffset Utc(long timestamp) => _startedUtc + Stopwatch.GetElapsedTime(_started, timestamp);
    private object Moment(Sample sample) => new { utc = Utc(sample.Timestamp), frame = sample.Frame };

    private void WriteWindow(long end, bool partial)
    {
        if (_frames == 0) return;
        Array.Sort(_work, 0, _workCount);
        Array.Sort(_intervals, 0, _intervalCount);
        double? lowFps = null;
        if (_intervalCount > 0)
        {
            var tail = (int)Math.Ceiling(_intervalCount * .01);
            double sum = 0;
            for (var i = _intervalCount - tail; i < _intervalCount; i++) sum += _intervals[i];
            lowFps = 1000 * tail / sum;
        }
        Write(Utc(end), new
        {
            kind = "window", session = _session, startUtc = Utc(_windowStart), endUtc = Utc(end), partial,
            frames = _frames, p50WorkMs = Percentile(.50), p95WorkMs = Percentile(.95), p99WorkMs = Percentile(.99),
            maxWorkMs = _maxWork, onePercentLowFps = lowFps, intervalSamples = _intervalCount,
            over16_7Ms = _over16, over33Ms = _over33,
            gestureIntervalOver16_7Ms = _intervalOver16, gestureIntervalOver33Ms = _intervalOver33,
            p99GestureIntervalMs = _intervalCount == 0 ? (double?)null : _intervals[(int)Math.Ceiling(_intervalCount * .99) - 1],
            maxGestureIntervalMs = _intervalCount == 0 ? (double?)null : _intervals[_intervalCount - 1],
            cacheHits = _hits, cacheMisses = _misses, cacheInvalidations = _invalidations,
            cacheHitRate = _hits + _misses == 0 ? (double?)null : _hits / (double)(_hits + _misses),
            memory = MemorySnapshot(),
            gestures = new { pan = _pan, zoom = _zoom, idle = _idle, edit = _edit },
            maxQueues = _queues, maxDetailTilesPending = _detail, maxUnresolvedImages = _unresolved,
            maxMissingAssets = _missing, maxTerrainRefinementsPending = _refinements, maxHexChunksPending = _hex,
            peakWorkingSetBytes = _peakWorkingSet, workingSetSamplingMs = 1000,
            processLifetimePeakWorkingSetBytes = _processPeakWorkingSet,
            worstFrame = Moment(_worstWindow), queueSnapshotsUnavailable = _queueUnavailable,
            droppedFramesSession = DroppedFrames, percentileSamplesOmitted = _frames - _workCount, ioErrorsSession = IoErrors,
        });
        _frames = _hits = _misses = _invalidations = _over16 = _over33 = _intervalOver16 = _intervalOver33 = 0;
        _pan = _zoom = _idle = _edit = _queueUnavailable = _peakWorkingSet = 0;
        _workCount = _intervalCount = _detail = _unresolved = _missing = _refinements = _hex = 0;
        _maxWork = 0; _queues = default;
    }

    private double Percentile(double fraction) => _work[(int)Math.Ceiling(_workCount * fraction) - 1];

    // Background writer only: the shared derived-cache budget, its pressure state and each cache's bytes and trims.
    private static object MemorySnapshot()
    {
        var budget = Map.Core.Resources.WorkingMemoryBudget.Shared;
        return new
        {
            cacheBudgetBytes = budget.CacheBudgetBytes, pressure = budget.CachesUnderPressure,
            pressureStage = budget.CachePressureStage, pressureEventsSession = budget.CachePressureEvents,
            caches = budget.ReadCacheStatistics(),
        };
    }

    private void Write(DateTimeOffset utc, object record)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
            var bytes = Encoding.UTF8.GetByteCount(line);
            var path = Path.Combine(_directory, "perf-" + utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
            // Make room before appending, including an oversized current-day file. Never truncate a JSON line.
            Trim(path, bytes);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException)
        { Interlocked.Increment(ref _ioErrors); } // One attempt per record; no retry loop.
    }

    private void Trim(string path, int appendBytes)
    {
        var files = new DirectoryInfo(_directory).GetFiles("perf-*.jsonl")
            .Where(file => IsOwnFile(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        long total = files.Sum(file => file.Length);
        var count = files.Length;
        var exists = files.Any(file => file.FullName == path);
        foreach (var file in files)
        {
            if (count + (exists ? 0 : 1) <= FileLimit && total + appendBytes <= DiskLimitBytes) break;
            var length = file.Length;
            file.Delete();
            total -= length; count--;
            if (file.FullName == path) exists = false;
        }
        // A symlink matching today's file must never redirect a write outside diagnostics.
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Performance journal path is a symbolic link.");
    }

    private static bool IsOwnFile(string name) => name.Length == 19 && name.StartsWith("perf-", StringComparison.Ordinal)
        && name.EndsWith(".jsonl", StringComparison.Ordinal)
        && DateOnly.TryParseExact(name.AsSpan(5, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private void Finish(long now, long workingSet)
    {
        Drain(now, workingSet);
        WriteWindow(now, true);
        if (_hasSessionWorst)
            Write(Utc(now), new { kind = "worstMoment", session = _session, moment = Moment(_worstSession),
                droppedFramesSession = DroppedFrames, ioErrorsSession = IoErrors });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_worker is not null) _worker.GetAwaiter().GetResult();
        else Finish(_lastDrain, 0);
        _stop.Dispose();
    }
}
