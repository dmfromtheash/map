using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;
using Map.Assets;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>
/// Disposable preparation queue, persistent content-addressed results. No file operations,
/// decode, resampling or waits are performed by Request/TryReady on the canvas thread.
/// One worker bounds native decode memory and avoids competing with the frame for all cores.
/// </summary>
public sealed partial class StampDerivedAssetStore : IDisposable, IAsyncDisposable
{
    // Bump when the working-image choice, overview evaluator or mip sampling changes.
    private const int Version = 1;
    private readonly AssetCatalog _catalog;
    private readonly string _root;
    private readonly long _diskBudget;
    private readonly object _requestsGate = new();
    private readonly object _diskGate = new();
    private readonly object _memoryGate = new();
    private readonly Dictionary<string, byte[]> _memoryPixels = new(StringComparer.OrdinalIgnoreCase);
    private long _memoryBytes, _failures;
    public long Failures => Interlocked.Read(ref _failures);
    private void ReportFailure(string operation, Exception error)
    {
        Interlocked.Increment(ref _failures);
        System.Diagnostics.Trace.TraceError("Stamp preparation {0}: {1}", operation, error);
    }
    private readonly ConcurrentQueue<(string Sha, TaskCompletionSource<Header?> Completion)> _completedHeaders = new();
    private readonly int _metadataCapacity;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Header?>> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Attempts, long RetryAt)> _failedHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _quietMissing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SKColor> _deliveredPreviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _retryClock;
    internal void SetCacheableMissing(string sha, bool missing)
    {
        if (missing) _quietMissing[sha] = 0;
        else _quietMissing.TryRemove(sha, out _);
    }
    private readonly Channel<(string Sha, TaskCompletionSource<Header?> Completion, TaskCompletionSource<SKColor?> Preview)> _queue =
        Channel.CreateUnbounded<(string, TaskCompletionSource<Header?>, TaskCompletionSource<SKColor?>)>(new() { SingleReader = true });
    private readonly Channel<(string Sha, TaskCompletionSource<Header?> Completion)> _buildQueue =
        Channel.CreateUnbounded<(string, TaskCompletionSource<Header?>)>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SKColor?>> _previews = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _watchedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<FileSystemWatcher> _startWatcher;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _invalidated = new(StringComparer.OrdinalIgnoreCase);
    public Action? SourceChanged { get; set; }
    public Action? OverviewChanged { get; set; }
    public string[] TakeInvalidated()
    {
        var keys = _invalidated.Keys.ToArray();
        foreach (var key in keys) _invalidated.TryRemove(key, out _);
        return keys;
    }
    private volatile bool _disposed;
    private long _generation;
    public long Generation => Interlocked.Read(ref _generation);
    private long _overviewColorRevision;
    public long OverviewColorRevision => Interlocked.Read(ref _overviewColorRevision);

    // A miss leaves the colour unknown, so the canvas has nothing new to draw: it only bumps the
    // revision (the overview re-reads which misses are now final) without waking a frame.
    private void OverviewColorChanged(bool colorArrived = true)
    {
        Interlocked.Increment(ref _overviewColorRevision);
        if (colorArrived) OverviewChanged?.Invoke();
    }
    public long DiskHits => Interlocked.Read(ref _diskHits);
    public long Builds => Interlocked.Read(ref _builds);
    public long DiskBytes => Interlocked.Read(ref _diskBytes);
    public long DiskEvictions => Interlocked.Read(ref _diskEvictions);
    public int MetadataEntries => _requests.Count;
    public int PendingMips => _mips.Count;
    private int _journalMetadata, _journalBuilds, _journalPixels;
    // Lock-free observation for the frame journal; metadata/build counts include active work.
    public (int Metadata, int Builds, int Pixels) PerformanceQueueLengths =>
        (Volatile.Read(ref _journalMetadata), Volatile.Read(ref _journalBuilds), Volatile.Read(ref _journalPixels));
    private long _diskBytes, _diskEvictions;
    private long _diskHits, _builds;
    public readonly record struct PixelKey(string Sha, int Step, int Level);
    private readonly ConcurrentDictionary<PixelKey, MipRequest> _mips = new();
    private long _discardedMips;
    private readonly int _pixelCapacity;
    private TaskCompletionSource<Mip> _capacityAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long DiscardedMips => Interlocked.Read(ref _discardedMips);
    private sealed class MipRequest(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public bool Started { get; set; }
        public Task<Mip> Task { get; set; } = null!;
    }
    // Finished small results waiting for the canvas: at most 64 × 256² × 4 B = 16 MiB. The canvas adopts them
    // under its 2 ms budget, about a hundred per frame; eight slots let a dense view take only eight per frame.
    private const int CompactMipSide = 256;
    private readonly SemaphoreSlim _mipSlots = new(64);
    private readonly SemaphoreSlim _mipReader = new(1);
    // At most one completed large raster can wait for the canvas. Small results retain
    // their own slots, so a large upload never fills memory with many 128 MiB masters.
    private readonly SemaphoreSlim _largeSlot = new(1);
    private SKImage? _preparedSource;
    private readonly long _preparedSourceByteCapacity;
    private long _preparedSourceBytes;
    public long PreparedSourceBytes => Interlocked.Read(ref _preparedSourceBytes);
    private PixelKey _preparedKey;
    private FileStamp? _preparedOriginal, _preparedWorking;
    private readonly CancellationTokenSource _stop = new();
    public sealed record Mip(byte[]? Pixels, int Width, int Height, bool Reserved, bool Cancelled = false,
        SKImage? Image = null, bool LargeReserved = false);
    private readonly Task _workers;
    private Task? _shutdown;
    private readonly ConcurrentQueue<Task> _retirements = new();
    public Task Completion => _shutdown ?? _workers;

    public sealed record FileStamp(string Path, long Length, long WrittenTicks)
    {
        public static FileStamp? Read(string? path)
        {
            if (path is null) return null;
            var info = new FileInfo(path);
            return info.Exists ? new(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        public bool Matches() => this == Read(Path);
    }

    public sealed record Header(int Version, string Sha, int Width, int Height, uint Color,
        FileStamp Original, FileStamp Working, int FirstMip, int MipCount, string PixelHash,
        string? PortablePath = null);

    public StampDerivedAssetStore(AssetCatalog catalog, string root, long diskBudgetBytes = 2L * 1024 * 1024 * 1024,
        int metadataCapacity = 8192, int pixelCapacity = 4096, long preparedSourceByteCapacity = 64L * 1024 * 1024,
        TimeProvider? retryClock = null, Action<FileSystemWatcher>? startWatcher = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(diskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(metadataCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(preparedSourceByteCapacity);
        _catalog = catalog;
        _retryClock = retryClock ?? TimeProvider.System;
        _startWatcher = startWatcher ?? (watcher => watcher.EnableRaisingEvents = true);
        _root = Path.GetFullPath(root);
        _diskBudget = diskBudgetBytes;
        _metadataCapacity = metadataCapacity;
        _pixelCapacity = pixelCapacity;
        _preparedSourceByteCapacity = Math.Min(preparedSourceByteCapacity, AssetImagePolicy.MaxDecodedBytes);
        _workers = Task.WhenAll(Task.Run(async () => { TrimDisk(); await Work().ConfigureAwait(false); }), Task.Run(BuildWork));
    }

    public Task<Header?> Request(string sha)
    {
        if (_disposed || sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return Task.FromResult<Header?>(null);
        lock (_requestsGate)
        {
            if (_disposed) return Task.FromResult<Header?>(null);
            if (_requests.TryGetValue(sha, out var existing))
            {
                if (!_failedHeaders.TryGetValue(sha, out var failed) || _retryClock.GetUtcNow().ToUnixTimeMilliseconds() < failed.RetryAt) return existing.Task;
                _requests.TryRemove(sha, out _); _previews.TryRemove(sha, out _);
            }
            // Metadata eviction must not reset a missing asset's backoff/attempt count.
            if (_failedHeaders.TryGetValue(sha, out var failure) && _retryClock.GetUtcNow().ToUnixTimeMilliseconds() < failure.RetryAt)
                return Task.FromResult<Header?>(null);
            if (_catalog.Find(sha) is null) return Task.FromResult<Header?>(null);
            var completion = new TaskCompletionSource<Header?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests[sha] = completion;
            var preview = new TaskCompletionSource<SKColor?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _previews[sha] = preview;
            Interlocked.Increment(ref _journalMetadata);
            if (!_queue.Writer.TryWrite((sha, completion, preview)))
            { Interlocked.Decrement(ref _journalMetadata); completion.TrySetResult(null); preview.TrySetResult(null); }
            return completion.Task;
        }
    }

    public Header? TryReady(string sha) => _requests.TryGetValue(sha, out var task)
        && task.Task.IsCompletedSuccessfully ? task.Task.Result : null;

    internal bool IsTerminalMissing(string sha) => _failedHeaders.TryGetValue(sha, out var failure) && failure.Attempts >= 4;

    public Task<SKColor?> Preview(string sha)
    {
        Request(sha);
        return _previews.TryGetValue(sha, out var preview) ? preview.Task : Task.FromResult<SKColor?>(null);
    }

    public SKColor? TryPreview(string sha) => _previews.TryGetValue(sha, out var preview)
        && preview.Task.IsCompletedSuccessfully ? preview.Task.Result : null;

    public void Forget()
    {
        lock (_requestsGate)
        {
            Interlocked.Increment(ref _generation);
            _requests.Clear(); _previews.Clear(); _completedHeaders.Clear(); _failedHeaders.Clear();
            _quietMissing.Clear(); _deliveredPreviews.Clear();
            _portableTrust.Clear(); _rejectedPortable.Clear();
            Interlocked.Increment(ref _overviewColorRevision);
            foreach (var request in _mips.Values) Retire(request);
            _mips.Clear();
            Volatile.Write(ref _journalPixels, 0);
            SignalCapacity();
        }
    }

    private void Retire(MipRequest request)
    {
        request.Stop.Cancel();
        var retirement = request.Task.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result.Reserved) _mipSlots.Release();
            if (task.IsCompletedSuccessfully && task.Result.LargeReserved) _largeSlot.Release();
            if (task.IsCompletedSuccessfully) task.Result.Image?.Dispose();
            request.Stop.Dispose();
        }, TaskScheduler.Default);
        _retirements.Enqueue(retirement);
        while (_retirements.TryPeek(out var completed) && completed.IsCompleted) _retirements.TryDequeue(out _);
    }

    private void CompleteHeader(string sha, TaskCompletionSource<Header?> completion, Header? header)
    {
        // A recovered miss is announced once by the image source's missing revision; waking the
        // canvas here as well would draw the same recovery twice.
        var recovered = false;
        lock (_requestsGate)
        {
            if (_requests.TryGetValue(sha, out var active) && ReferenceEquals(active, completion))
            {
                recovered = header is not null && _failedHeaders.ContainsKey(sha);
                if (header is null)
                {
                    var attempts = _failedHeaders.GetValueOrDefault(sha).Attempts + 1;
                    _failedHeaders[sha] = (attempts, _retryClock.GetUtcNow().ToUnixTimeMilliseconds()
                        + (attempts >= 4 ? 60_000 : 2000L << (attempts - 1)));
                }
                else _failedHeaders.TryRemove(sha, out _);
                _completedHeaders.Enqueue((sha, completion));
                while ((_requests.Count > _metadataCapacity || _completedHeaders.Count > _metadataCapacity)
                    && _completedHeaders.TryDequeue(out var oldest))
                    if (_requests.TryGetValue(oldest.Sha, out var current) && ReferenceEquals(current, oldest.Completion))
                    { _requests.TryRemove(oldest.Sha, out _); _previews.TryRemove(oldest.Sha, out _); }
            }
        }
        completion.TrySetResult(header);
        OverviewColorChanged(header is not null && !recovered);
    }

    public Task<Mip> RequestMip(Header header, int level) => RequestPixels(header, 0, level);

    public Task<Mip> RequestPixels(Header header, int step, int level)
    {
        TryRequestPixels(header, step, level, out var task);
        return task;
    }

    /// <summary>Refuses new keys when full; callers share one capacity notification, never evict current demand.</summary>
    public bool TryRequestPixels(Header header, int step, int level, out Task<Mip> task)
    {
        var key = new PixelKey(header.Sha.ToLowerInvariant(), step, level);
        lock (_requestsGate)
        {
            if (_disposed) { task = Task.FromResult(new Mip(null, 0, 0, false, Cancelled: true)); return false; }
            if (_mips.TryGetValue(key, out var held)) { task = held.Task; return true; }
            if (_mips.Count >= _pixelCapacity)
            {
                task = _capacityAvailable.Task;
                return false;
            }
            var request = new MipRequest(CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
            request.Task = Task.Run(() => ReadRequestedMip(header, key, request));
            _mips[key] = request;
            Interlocked.Increment(ref _journalPixels);
            task = request.Task;
            return true;
        }
    }

    // Called under _requestsGate after removing one or more entries.
    private void SignalCapacity()
    {
        if (_mips.Count >= _pixelCapacity) return;
        var waiting = _capacityAvailable;
        _capacityAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.TrySetResult(new Mip(null, 0, 0, false, Cancelled: true));
    }

    public void CancelUnwantedMips(IReadOnlySet<(string Sha, int Level)> wanted)
        => CancelUnwantedPixels(wanted.Select(key => new PixelKey(key.Sha, 0, key.Level)).ToHashSet());

    public void CancelUnwantedPixels(IReadOnlySet<PixelKey> wanted)
    {
        lock (_requestsGate)
            foreach (var pair in _mips)
                if (!pair.Value.Started && !pair.Value.Stop.IsCancellationRequested && !wanted.Contains(pair.Key))
                { pair.Value.Stop.Cancel(); Interlocked.Increment(ref _discardedMips); }
    }

    private async Task<Mip> ReadRequestedMip(Header header, PixelKey key, MipRequest request)
    {
        var reserved = false;
        var largeReserved = false;
        try
        {
            var record = _catalog.Find(header.Sha);
            var small = record is not null && AssetCatalog.DetailLadderDepth(record.Width, record.Height) == 0
                && key.Step == 0 && key.Level >= header.FirstMip && key.Level < header.FirstMip + header.MipCount;
            // The single large slot bounds the memory of finished rasters waiting for the canvas. A small mip of a
            // ladder asset is prepared from its source like a master, but it is as small as any thumbnail: it waits
            // in a small slot. Holding every 8 px mip of a big asset to one per frame starved a dense view.
            var compact = small || record is not null
                && Math.Max(Math.Max(1, record.Width >> key.Step) >> key.Level, Math.Max(1, record.Height >> key.Step) >> key.Level) <= CompactMipSide;
            if (compact) { await _mipSlots.WaitAsync(request.Stop.Token).ConfigureAwait(false); reserved = true; }
            else { await _largeSlot.WaitAsync(request.Stop.Token).ConfigureAwait(false); largeReserved = true; }
            await _mipReader.WaitAsync(request.Stop.Token).ConfigureAwait(false);
            try
            {
                lock (_requestsGate) { request.Stop.Token.ThrowIfCancellationRequested(); request.Started = true; }
                if (header.PortablePath is not null && TryReadPortablePixels(header, key) is { } portable)
                {
                    using var bitmap = new SKBitmap(new SKImageInfo(portable.Width, portable.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    System.Runtime.InteropServices.Marshal.Copy(portable.Pixels, 0, bitmap.GetPixels(), portable.Pixels.Length);
                    bitmap.SetImmutable();
                    return new Mip(null, portable.Width, portable.Height, reserved, Image: SKImage.FromBitmap(bitmap), LargeReserved: largeReserved);
                }
                if (header.PortablePath is not null && IsRejectedPortable(header.Sha, header.PortablePath))
                {
                    var repaired = record is null ? null : TryReadHeader(header.Sha, record) ?? LoadOrBuild(header.Sha);
                    if (repaired is null) return new Mip(null, 0, 0, reserved, LargeReserved: largeReserved);
                    header = repaired;
                }
                // A valid portable asset may omit a large raster; prepare that raster only.
                if (!small || header.PortablePath is not null)
                {
                    var image = PrepareRaster(header, key, request.Stop.Token);
                    return new Mip(null, image?.Width ?? 0, image?.Height ?? 0, reserved,
                        Image: image, LargeReserved: largeReserved);
                }
                byte[]? pixels; int width, height;
                try { pixels = ReadMip(header, key.Level, out width, out height); }
                catch (Exception ex)
                {
                    ReportFailure("read/rebuild " + header.Sha, ex);
                    // A crash, interrupted write or eviction loses only derived data. Rebuild
                    // from verified owner bytes here, never fall back to frame-thread decoding.
                    var repaired = LoadOrBuild(header.Sha);
                    if (repaired is null) return new Mip(null, 0, 0, reserved);
                    pixels = ReadMip(repaired, key.Level, out width, out height);
                }
                return new Mip(pixels, width, height, reserved);
            }
            finally { _mipReader.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (reserved) _mipSlots.Release();
            if (largeReserved) _largeSlot.Release();
            return new Mip(null, 0, 0, false, Cancelled: true);
        }
        catch (Exception ex)
        { ReportFailure("pixels " + header.Sha, ex); return new Mip(null, 0, 0, reserved, LargeReserved: largeReserved); }
    }

    public bool TryTakeMip(string sha, int level, out Mip? mip)
        => TryTakePixels(new PixelKey(sha.ToLowerInvariant(), 0, level), out mip);

    public bool TryTakePixels(PixelKey key, out Mip? mip)
    {
        lock (_requestsGate)
        {
            mip = null;
            if (!_mips.TryGetValue(key, out var request) || !request.Task.IsCompletedSuccessfully) return false;
            if (!_mips.TryRemove(key, out request)) return false;
            Interlocked.Decrement(ref _journalPixels);
            SignalCapacity();
            mip = request.Task.Result;
            if (mip.Reserved) _mipSlots.Release();
            if (mip.LargeReserved) _largeSlot.Release();
            request.Stop.Dispose();
            return true;
        }
    }

    public IEnumerable<PixelKey> ReadyPixels => _mips
        .Where(pair => pair.Value.Task.IsCompletedSuccessfully).Select(pair => pair.Key);

    private async Task Work()
    {
        await foreach (var (sha, completion, previewCompletion) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
            if (!IsCurrent(sha, completion)) { completion.TrySetResult(null); previewCompletion.TrySetResult(null); continue; }
            Header? held = null; SKColor? preview = null;
            try
            {
                if (!_disposed && _catalog.Find(sha) is { } record)
                {
                    held = TryReadHeader(sha, record);
                    if (held is not null) { preview = new SKColor(held.Color); Interlocked.Increment(ref _diskHits); }
                    else
                    {
                        using var bitmap = SKBitmap.Decode(_catalog.ThumbnailPath(record));
                        if (bitmap is not null)
                        {
                            using var image = SKImage.FromBitmap(bitmap);
                            preview = SceneRenderer.StampOverviewColor(image);
                        }
                    }
                }
            }
            catch (Exception ex) { ReportFailure("metadata " + sha, ex); }
            previewCompletion.TrySetResult(preview);
            // A slow retry of a missing source re-reads the same thumbnail colour: nothing to redraw.
            var arrived = preview is { } shown && (!_deliveredPreviews.TryGetValue(sha, out var known) || known != shown);
            if (preview is { } delivered) _deliveredPreviews[sha] = delivered;
            OverviewColorChanged(arrived);
            if (_disposed || held is not null) CompleteHeader(sha, completion, held);
            else
            {
                Interlocked.Increment(ref _journalBuilds);
                if (!_buildQueue.Writer.TryWrite((sha, completion)))
                { Interlocked.Decrement(ref _journalBuilds); CompleteHeader(sha, completion, null); }
            }
            }
            finally { Interlocked.Decrement(ref _journalMetadata); }
        }
        _buildQueue.Writer.TryComplete();
    }

    private async Task BuildWork()
    {
        await foreach (var (sha, completion) in _buildQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
            if (!IsCurrent(sha, completion)) { completion.TrySetResult(null); continue; }
            Header? result = null;
            try { if (!_disposed) result = LoadOrBuild(sha); }
            catch (Exception ex) { ReportFailure("build " + sha, ex); }
            CompleteHeader(sha, completion, result);
            }
            finally { Interlocked.Decrement(ref _journalBuilds); }
        }
    }

    private bool IsCurrent(string sha, TaskCompletionSource<Header?> completion) => !_disposed
        && _requests.TryGetValue(sha, out var active) && ReferenceEquals(active, completion);

    private string Stem(string sha) => Path.Combine(_root, "v" + Version, sha.ToLowerInvariant());

    // Watching happens on workers, never inside a canvas getter. Catalog roots cover original
    // and ladder files with one handle per library; external linked files use their directory.
    private void Watch(AssetRecord record, string path)
    {
        _watchedFiles[path] = record.Sha256;
        var root = Path.GetFullPath(_catalog.RootFor(record));
        var recursive = path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!recursive)
            root = Path.GetDirectoryName(path)!;
        // Never widen a missing library/link into an ancestor or drive-wide watcher.
        // The slow missing-asset retry will register this exact directory on recovery.
        if (!Directory.Exists(root) || string.Equals(root.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        lock (_watchers)
        {
            if (_disposed || _watchers.ContainsKey(root)) return;
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = recursive,
                InternalBufferSize = 32 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, args) => FileChanged(args.FullPath);
            watcher.Created += (_, args) => FileChanged(args.FullPath);
            watcher.Deleted += (_, args) => FileChanged(args.FullPath);
            watcher.Renamed += (_, args) => { FileChanged(args.OldFullPath); FileChanged(args.FullPath); };
            watcher.Error += (_, _) =>
            {
                // A disconnected drive or overflow loses the notification history. Drop the
                // broken watcher so the next catalog reconnect/request can register it again.
                lock (_watchers)
                    if (_watchers.TryGetValue(root, out var current) && ReferenceEquals(current, watcher)) _watchers.Remove(root);
                watcher.Dispose();
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (var path in _watchedFiles.Keys.Where(path => recursive
                    ? path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))) FileChanged(path);
            };
            _watchers[root] = watcher;
            _startWatcher(watcher);
        }
    }

    private void FileChanged(string path) => InvalidateFile(path, cancelPixels: true);

    private void InvalidateFile(string path, bool cancelPixels)
    {
        if (_disposed || !_watchedFiles.TryGetValue(path, out var sha)) return;
        lock (_requestsGate)
        {
            // File events are only hints for terminal misses. A successful read by the
            // slow retry confirms recovery; repeated events must not wake the canvas.
            var quiet = _quietMissing.ContainsKey(sha)
                || _failedHeaders.TryGetValue(sha, out var failure) && failure.Attempts >= 4;
            Interlocked.Increment(ref _generation);
            _requests.TryRemove(sha, out _); _previews.TryRemove(sha, out _);
            if (!quiet) _failedHeaders.TryRemove(sha, out _);
            if (cancelPixels)
                foreach (var key in _mips.Keys.Where(key => string.Equals(key.Sha, sha, StringComparison.OrdinalIgnoreCase)))
                    if (_mips.TryRemove(key, out var request))
                    { Interlocked.Decrement(ref _journalPixels); Retire(request); }
            SignalCapacity();
            if (quiet) return;
            _deliveredPreviews.TryRemove(sha, out _);
            _invalidated[sha] = 0;
        }
        Interlocked.Increment(ref _overviewColorRevision);
        SourceChanged?.Invoke();
    }

    private Header? TryReadHeader(string sha, AssetRecord record)
    {
        var stem = Stem(sha);
        try
        {
            if (_catalog.SourcePath(record) is { } source) Watch(record, source);
            var currentWorking = _catalog.SourcePath(record);
            if (AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0)
                Watch(record, Path.Combine(_catalog.RootFor(record), "ladder", record.Sha256.ToLowerInvariant(), "1.png"));
            if (_catalog.TryDetailLevelPath(record, 1, out var step)) currentWorking = step;
            if (TryReadPortableHeader(record) is { } portable) return portable;
            var info = new FileInfo(stem + ".json");
            if (info.Exists && info.Length <= 16 * 1024)
            {
                var held = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(info.FullName));
                if (held is { Version: Version, Width: > 0 and <= 32768, Height: > 0 and <= 32768,
                        MipCount: >= 0 and <= 16, FirstMip: >= 0 and <= 15, PixelHash.Length: 64 }
                    && string.Equals(held.Sha, sha, StringComparison.OrdinalIgnoreCase)
                    && held.Original is not null && held.Working is not null
                    && string.Equals(held.Original.Path, _catalog.SourcePath(record), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(held.Working.Path, currentWorking, StringComparison.OrdinalIgnoreCase)
                    && held.Original.Matches() && held.Working.Matches()
                    && File.Exists(stem + ".pixels"))
                {
                    File.SetLastWriteTimeUtc(info.FullName, DateTime.UtcNow);
                    return held;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return null;
    }

    private Header? LoadOrBuild(string sha)
    {
        var record = _catalog.Find(sha);
        if (record is null) return null;
        var stem = Stem(sha);
        using var image = ReadSource(record, 1, null, out var original, out var working);
        if (image is null || original is null || working is null) return null;
        var color = SceneRenderer.StampOverviewColor(image);
        using var raw = new MemoryStream();
        using var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true);
        // Raw premultiplied bytes avoid the unpremultiply/re-premultiply rounding of PNG.
        // Every halving is the same Linear path used by AssetImageCache.GetForPixelSize.
        var first = 0;
        while (Math.Max(image.Width >> first, image.Height >> first) > 256) first++;
        var count = 0;
        SKImage current = image;
        try
        {
            var last = Math.Min(15, (int)Math.Floor(Math.Log2(Math.Min(image.Width, image.Height))));
            for (var level = 0; level <= last; level++)
            {
                var width = Math.Max(1, image.Width >> level);
                var height = Math.Max(1, image.Height >> level);
                if (level > 0)
                {
                    using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    using var pixels = bitmap.PeekPixels();
                    if (!current.ScalePixels(pixels, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))) return null;
                    bitmap.SetImmutable();
                    var next = SKImage.FromBitmap(bitmap);
                    if (!ReferenceEquals(current, image)) current.Dispose();
                    current = next;
                }
                if (level >= first)
                {
                    using var pixels = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    if (!current.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0)) return null;
                    var bytes = pixels.Bytes;
                    writer.Write(level); writer.Write(width); writer.Write(height); writer.Write(bytes.Length); writer.Write(bytes);
                    count++;
                }
                if (width == 1 && height == 1) break;
            }
        }
        finally { if (!ReferenceEquals(current, image)) current.Dispose(); }
        var content = raw.ToArray();
        var header = new Header(Version, sha, image.Width, image.Height, (uint)color, original, working,
            first, count, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)));
        if (!original.Matches() || !working.Matches()) return null;
        var temporary = stem + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var previousBytes = FileBytes(stem + ".pixels") + FileBytes(stem + ".json");
        lock (_memoryGate)
            if (_memoryPixels.Remove(sha, out var replaced)) _memoryBytes -= replaced.Length;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
            using (var file = File.Create(temporary))
            using (var compressed = new BrotliStream(file, CompressionLevel.Fastest)) compressed.Write(content);
            File.Move(temporary, stem + ".pixels", overwrite: true);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(header));
            File.Move(temporary, stem + ".json", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportFailure("persist; using memory " + sha, ex);
            lock (_memoryGate)
            {
                if (_memoryPixels.Remove(sha, out var previous)) _memoryBytes -= previous.Length;
                while (_memoryBytes + content.Length > 32L * 1024 * 1024 && _memoryPixels.Count > 0)
                {
                    var oldest = _memoryPixels.First(); _memoryPixels.Remove(oldest.Key); _memoryBytes -= oldest.Value.Length;
                }
                _memoryPixels[sha] = content; _memoryBytes += content.Length;
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportFailure("temporary cleanup", ex); }
        }
        Interlocked.Add(ref _diskBytes, FileBytes(stem + ".pixels") + FileBytes(stem + ".json") - previousBytes);
        if (Interlocked.Increment(ref _builds) % 64 == 0 || DiskBytes > _diskBudget) TrimDisk();
        return header;
    }

    // Shared source preparation for persisted derivatives and requested large rasters.
    // A matching header proves these exact source bytes were already SHA-checked; changed
    // size/time/path goes through ReadVerified again. All path checks run on the worker.
    private SKImage? ReadSource(AssetRecord record, int step, Header? trusted,
        out FileStamp? original, out FileStamp? working)
    {
        CountIo(ref _sourcePreparations);
        original = FileStamp.Read(_catalog.SourcePath(record));
        working = original;
        if (original is null) return null;
        Watch(record, original.Path);
        if (step > 0 && step <= AssetCatalog.DetailLadderDepth(record.Width, record.Height))
            Watch(record, Path.Combine(_catalog.RootFor(record), "ladder", record.Sha256.ToLowerInvariant(), step + ".png"));
        byte[] encoded;
        if (_catalog.TryDetailLevelPath(record, step, out var ladder))
        {
            working = FileStamp.Read(ladder);
            if (working is null) return null;
            Watch(record, working.Path);
            // Even when drawing a ladder, a new or changed master must pass its content id.
            if (trusted?.Original != original && _catalog.ReadVerified(record).Status != AssetResolutionStatus.Available) return null;
            encoded = AssetImagePolicy.ReadEncodedFile(working.Path);
        }
        else if (trusted?.Original == original) encoded = AssetImagePolicy.ReadEncodedFile(original.Path);
        else
        {
            var payload = _catalog.ReadVerified(record);
            if (payload is not { Status: AssetResolutionStatus.Available, Content: { } bytes, Path: { } path }
                || !string.Equals(original.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return null;
            encoded = bytes;
        }
        _ = AssetImagePolicy.Inspect(encoded);
        var decodeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var image = AssetImageCache.DecodeOwned(encoded);
        Interlocked.Add(ref _sourceDecodeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - decodeStarted);
        if (!original.Matches() || !working.Matches()) { image?.Dispose(); return null; }
        return image;
    }

    private SKImage? PrepareRaster(Header header, PixelKey key, CancellationToken cancellation)
    {
        var record = _catalog.Find(key.Sha);
        if (record is null) return null;
        var sourceKey = key with { Level = 0 };
        var workingPath = _catalog.TryDetailLevelPath(record, key.Step, out var currentStep)
            ? currentStep : _catalog.SourcePath(record);
        if (_preparedSource is null || _preparedKey != sourceKey
            || _preparedOriginal?.Matches() != true || _preparedWorking?.Matches() != true
            || !string.Equals(_preparedWorking.Path, workingPath, StringComparison.OrdinalIgnoreCase))
        {
            ClearPreparedSource();
            _preparedSource = ReadSource(record, key.Step, header, out _preparedOriginal, out _preparedWorking);
            Interlocked.Exchange(ref _preparedSourceBytes, _preparedSource is { } decoded ? (long)decoded.Width * decoded.Height * 4 : 0);
            _preparedKey = sourceKey;
        }
        if (_preparedSource is not { } source) return null;
        var requestedWidth = Math.Max(1, record.Width >> key.Step) / Math.Pow(2, key.Level);
        var requestedHeight = Math.Max(1, record.Height >> key.Step) / Math.Pow(2, key.Level);
        var level = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1,
            Math.Min(source.Width / Math.Max(1, requestedWidth), source.Height / Math.Max(1, requestedHeight))))), 0, 15);
        // Missing ladder steps use the original's same linear mip path as ForPixelSize.
        // Each halving uses the previous pixels, never a direct downscale of the master.
        SKImage current = source;
        try
        {
            for (var n = 1; n <= level; n++)
            {
                cancellation.ThrowIfCancellationRequested();
                using var bitmap = new SKBitmap(new SKImageInfo(Math.Max(1, source.Width >> n),
                    Math.Max(1, source.Height >> n), SKColorType.Rgba8888, SKAlphaType.Premul));
                using var pixels = bitmap.PeekPixels();
                if (!current.ScalePixels(pixels, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))) return null;
                bitmap.SetImmutable();
                var next = SKImage.FromBitmap(bitmap);
                if (!ReferenceEquals(current, source)) current.Dispose();
                current = next;
            }
            cancellation.ThrowIfCancellationRequested();
            // Transfer ownership rather than copying a full master (or the last mip).
            // The result owns its pixels; the source cache never aliases a transferred image.
            var result = current;
            if (ReferenceEquals(current, source))
            {
                _preparedSource = null;
                Interlocked.Exchange(ref _preparedSourceBytes, 0);
            }
            current = source; // result is now owned by the caller, not the finally block
            return result;
        }
        finally
        {
            if (!ReferenceEquals(current, source)) current.Dispose();
            if (PreparedSourceBytes > _preparedSourceByteCapacity) ClearPreparedSource();
        }
    }

    private void ClearPreparedSource()
    {
        _preparedSource?.Dispose(); _preparedSource = null;
        Interlocked.Exchange(ref _preparedSourceBytes, 0);
    }

    private static long FileBytes(string path)
    {
        try { return new FileInfo(path) is { Exists: true } file ? file.Length : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private void TrimDisk()
    {
        lock (_diskGate)
        {
            var folder = Path.Combine(_root, "v" + Version);
            if (!Directory.Exists(folder)) return;
            try
            {
                var files = new DirectoryInfo(folder).GetFiles();
                foreach (var file in files.Where(f => f.Name.EndsWith(".tmp", StringComparison.Ordinal)
                    && f.Name.Length == 101 && f.Name[..64].All(Uri.IsHexDigit)
                    && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
                    try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                var entries = files.Where(f => f.Name.Length is 69 or 70 or 71 && f.Name[..64].All(Uri.IsHexDigit)
                    && (f.Extension == ".json" || f.Extension == ".pixels" || f.Extension == ".trust"))
                    .GroupBy(f => f.Name[..64], StringComparer.OrdinalIgnoreCase)
                    .Select(group => new { Files = group.ToArray(), Used = group.Max(f => f.LastWriteTimeUtc), Bytes = group.Sum(f => f.Length) })
                    .OrderBy(entry => entry.Used).ToArray();
                var bytes = entries.Sum(entry => entry.Bytes);
                foreach (var entry in entries)
                {
                    if (bytes <= _diskBudget) break;
                    var removed = false;
                    foreach (var file in entry.Files)
                        try { var length = file.Length; file.Delete(); bytes -= length; removed = true; }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
                    if (removed) Interlocked.Increment(ref _diskEvictions);
                }
                Interlocked.Exchange(ref _diskBytes, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Background/test reader. Never called by a canvas getter.</summary>
    public byte[]? ReadMip(Header header, int level, out int width, out int height)
    {
        width = height = 0;
        byte[]? bytes;
        lock (_memoryGate) _memoryPixels.TryGetValue(header.Sha, out bytes);
        bytes ??= ReadCompressedPixels(header);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != header.PixelHash)
            throw new InvalidDataException("Derived mip checksum mismatch.");
        using var reader = new BinaryReader(new MemoryStream(bytes));
        for (var i = 0; i < header.MipCount; i++)
        {
            var current = reader.ReadInt32(); var w = reader.ReadInt32(); var h = reader.ReadInt32(); var length = reader.ReadInt32();
            if (w is < 1 or > 256 || h is < 1 or > 256 || length != w * h * 4) throw new InvalidDataException("Invalid derived mip.");
            var pixels = reader.ReadBytes(length);
            if (pixels.Length != length) throw new InvalidDataException("Truncated derived mip.");
            if (current == level) { width = w; height = h; return pixels; }
        }
        return null;
    }

    private byte[] ReadCompressedPixels(Header header)
    {
        CountIo(ref _localMipReads);
        using var file = new FileStream(Stem(header.Sha) + ".pixels", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var compressed = new BrotliStream(file, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = compressed.Read(buffer)) > 0)
        {
            if (raw.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("Oversized derived mip data.");
            raw.Write(buffer, 0, read);
        }
        return raw.ToArray();
    }

    public void Dispose()
    {
        lock (_requestsGate)
        {
            if (_disposed) return;
            _disposed = true; _stop.Cancel(); _queue.Writer.TryComplete();
            Forget();
            lock (_watchers) { foreach (var watcher in _watchers.Values) watcher.Dispose(); _watchers.Clear(); }
            _watchedFiles.Clear(); SourceChanged = null; OverviewChanged = null;
            var retiring = _retirements.ToArray();
            // Dispose is nonblocking for the UI. Async disposal/Completion lets tests and
            // profile cleanup await all file handles and native buffers before removing them.
            _shutdown = Task.Run(async () =>
            {
                await Task.WhenAll(retiring.Append(_workers)).ConfigureAwait(false);
                ClearPreparedSource();
                lock (_memoryGate) { _memoryPixels.Clear(); _memoryBytes = 0; }
                _mipReader.Dispose(); _mipSlots.Dispose(); _largeSlot.Dispose(); _stop.Dispose();
            });
        }
    }

    public ValueTask DisposeAsync() { Dispose(); return new(Completion); }
}
