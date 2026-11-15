using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Map.Core.Resources;

namespace Map.Presentation.Services;

/// <summary>When the machine cache of asset preparations is cleaned (DEC-110 p.2, DEC-111 p.3-4).</summary>
public sealed record DerivedCacheMaintenancePolicy
{
    public static DerivedCacheMaintenancePolicy Default { get; } = new();
    /// <summary>The first cleanup waits until the opened map has drawn: startup reads compete with it otherwise.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Period { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>A cleanup also follows this many new preparations or this many bytes written since the last one.</summary>
    public int BuildsBetweenCleanups { get; init; } = 64;
    public long BytesBetweenCleanups { get; init; } = 256L * 1024 * 1024;
    /// <summary>A drive library unplugged longer than this gives up its preparations first (DEC-111 p.4).</summary>
    public TimeSpan UnmountedAging { get; init; } = TimeSpan.FromDays(14);
    /// <summary>Entries a reader held during a purge are tried again after this delay, at most this many times.</summary>
    public TimeSpan PurgeRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int PurgeRetryAttempts { get; init; } = 3;
    /// <summary>Startup, periodic and build-count cleanups. Exceeding the ceiling and explicit commands always clean.</summary>
    public bool Automatic { get; init; } = true;
    public PreparedDataBudget Budget { get; init; } = PreparedDataBudget.Shared;
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    /// <summary>Test seam; null measures the cache drive with DriveInfo.</summary>
    public Func<long>? AvailableFreeBytes { get; init; }
}

/// <summary>What one targeted cleanup removed from the machine cache; portable library files are never touched.</summary>
public sealed record DerivedCachePurgeResult(int Entries, long Files, long Bytes, int Skipped, bool Completed)
{
    public static DerivedCachePurgeResult Nothing { get; } = new(0, 0, 0, 0, true);
}

public sealed partial class StampDerivedAssetStore
{
    private enum JobKind { Ledger, Clean, Purge }
    private sealed record Job(JobKind Kind, Func<DerivedCacheEntry, bool>? Select = null,
        TaskCompletionSource<DerivedCacheJanitorReport>? Done = null);

    private readonly DerivedCacheMaintenancePolicy _policy;
    private readonly DerivedCacheJanitor _janitor;
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>(new() { SingleReader = true });
    private readonly ConcurrentDictionary<string, int> _purgeRetry = new(StringComparer.Ordinal);
    private DerivedMountLedger? _ledger;
    private volatile LedgerView? _ledgerView;
    private volatile DerivedCacheJanitorReport? _lastCleanup;
    private ITimer? _maintenanceTimer, _retryTimer;
    private int _cleanQueued;
    private long _buildsSinceClean, _bytesSinceClean, _ceilingKnown, _cleanups;

    private sealed record LedgerView(Dictionary<string, DateTimeOffset> Seen, HashSet<string> Mounted, DateTimeOffset Now);

    /// <summary>The last finished janitor pass (automatic or <see cref="CleanNowAsync"/>); null before the first.</summary>
    public DerivedCacheJanitorReport? LastCleanup => _lastCleanup;
    public long Cleanups => Interlocked.Read(ref _cleanups);
    public DerivedCacheMaintenancePolicy MaintenancePolicy => _policy;

    private DerivedCacheJanitor CreateJanitor(long? explicitBudget) => new(_root, new DerivedCacheJanitorOptions
    {
        CurrentVersion = Version,
        MaximumBytes = explicitBudget ?? PreparedDataBudget.DefaultTotalBytes,
        Budget = explicitBudget is null ? _policy.Budget : null,
        Share = PreparedDataBudget.DerivedAssets,
        Classify = ClassifyEntry,
    }, _policy.AvailableFreeBytes);

    // One low-priority thread per store: the janitor never runs on the canvas, UI or preparation workers.
    private Task StartMaintenance()
    {
        Volatile.Write(ref _ceilingKnown, _diskBudget);
        _ledger = new DerivedMountLedger(_root);
        _catalog.RecordRemoved += OnRecordRemoved;
        _catalog.MountsChanged += OnMountsChanged;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { MaintenanceLoop(); finished.TrySetResult(); }
            catch (Exception ex) { finished.TrySetException(ex); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Map derived-cache janitor" };
        thread.Start();
        _jobs.Writer.TryWrite(new Job(JobKind.Ledger));
        if (_policy.Automatic)
            _maintenanceTimer = _policy.Clock.CreateTimer(_ => RequestCleanup(), null, _policy.StartupDelay,
                _policy.Period > TimeSpan.Zero ? _policy.Period : Timeout.InfiniteTimeSpan);
        return finished.Task;
    }

    private void StopMaintenance()
    {
        _catalog.RecordRemoved -= OnRecordRemoved;
        _catalog.MountsChanged -= OnMountsChanged;
        _maintenanceTimer?.Dispose();
        _retryTimer?.Dispose();
        _jobs.Writer.TryComplete();
    }

    private void MaintenanceLoop()
    {
        var reader = _jobs.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (reader.TryRead(out var job)) Execute(job);
    }

    /// <summary>Coalesced: any number of triggers while one pass is queued make one pass.</summary>
    public void RequestCleanup()
    {
        if (_disposed || Interlocked.Exchange(ref _cleanQueued, 1) != 0) return;
        if (!_jobs.Writer.TryWrite(new Job(JobKind.Clean))) Volatile.Write(ref _cleanQueued, 0);
    }

    /// <summary>Runs one full pass now (after queued work) and returns its report.</summary>
    public Task<DerivedCacheJanitorReport> CleanNowAsync() => Enqueue(JobKind.Clean, null);

    /// <summary>Deletes the machine preparations of exactly these contents; they are rebuilt when shown again.</summary>
    public Task<DerivedCachePurgeResult> PurgeDerived(IEnumerable<string> sha)
    {
        ArgumentNullException.ThrowIfNull(sha);
        var wanted = sha.Where(value => value is { Length: 64 } && value.All(Uri.IsHexDigit))
            .Select(value => value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return wanted.Count == 0 ? Task.FromResult(DerivedCachePurgeResult.Nothing) : Purge(entry => wanted.Contains(entry.Sha));
    }

    /// <summary>Deletes the preparations of every content carrying this tag (e.g. «Тестовое»).</summary>
    public Task<DerivedCachePurgeResult> PurgeDerivedByTag(string tag) => PurgeDerived(_catalog.ContentWithTag(tag));

    /// <summary>Deletes the preparations of contents no attached catalog lists (removed, or of unplugged libraries).</summary>
    public Task<DerivedCachePurgeResult> PurgeUnused() => Purge(entry => _catalog.Find(entry.Sha) is null);

    private async Task<DerivedCachePurgeResult> Purge(Func<DerivedCacheEntry, bool> select)
    {
        var report = await Enqueue(JobKind.Purge, select).ConfigureAwait(false);
        return new(report.PurgedEntries, report.FilesDeleted, report.BytesDeleted, report.PurgeSkipped,
            report.LockAcquired && report.PurgeSkipped == 0 && report.Errors.Count == 0);
    }

    private Task<DerivedCacheJanitorReport> Enqueue(JobKind kind, Func<DerivedCacheEntry, bool>? select)
    {
        var done = new TaskCompletionSource<DerivedCacheJanitorReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed || !_jobs.Writer.TryWrite(new Job(kind, select, done))) done.TrySetCanceled();
        return done.Task;
    }

    private void AfterBuild(long written)
    {
        var builds = Interlocked.Increment(ref _buildsSinceClean);
        var bytes = Interlocked.Add(ref _bytesSinceClean, Math.Max(0, written));
        // Over 90 % of the last known ceiling is the janitor's own trigger line: always clean then.
        if (DiskBytes > Volatile.Read(ref _ceilingKnown) * 0.9
            || _policy.Automatic && (builds >= _policy.BuildsBetweenCleanups || bytes >= _policy.BytesBetweenCleanups))
            RequestCleanup();
    }

    private void OnRecordRemoved(string sha)
    {
        // The same content may still be listed by another library: then its preparation stays.
        if (_disposed || _catalog.Find(sha) is not null) return;
        var key = sha.ToLowerInvariant();
        _jobs.Writer.TryWrite(new Job(JobKind.Purge, entry => entry.Sha == key && _catalog.Find(key) is null));
    }

    private void OnMountsChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _jobs.Writer.TryWrite(new Job(JobKind.Ledger));
    }

    private void Execute(Job job)
    {
        if (_disposed) { job.Done?.TrySetCanceled(); return; }
        using var load = BackgroundLoad.Enter(BackgroundProducer.AssetJanitor);
        try
        {
            Directory.CreateDirectory(_root);
            switch (job.Kind)
            {
                case JobKind.Ledger:
                    TouchLedger();
                    break;
                case JobKind.Clean:
                {
                    Volatile.Write(ref _cleanQueued, 0);
                    Interlocked.Exchange(ref _buildsSinceClean, 0);
                    Interlocked.Exchange(ref _bytesSinceClean, 0);
                    TouchLedger();
                    RetryPurges();
                    var report = _janitor.Clean(_stop.Token);
                    if (report.LockAcquired)
                    {
                        Interlocked.Exchange(ref _diskBytes, report.BytesKept);
                        Interlocked.Add(ref _diskEvictions, report.EvictedUnreferenced + report.EvictedUnmounted + report.EvictedLive);
                        Volatile.Write(ref _ceilingKnown, report.CeilingBytes);
                    }
                    _lastCleanup = report;
                    Interlocked.Increment(ref _cleanups);
                    job.Done?.TrySetResult(report);
                    break;
                }
                case JobKind.Purge:
                {
                    TouchLedger();
                    var report = _janitor.Purge(job.Select!, _stop.Token);
                    if (report.LockAcquired) Interlocked.Exchange(ref _diskBytes, report.BytesKept);
                    foreach (var sha in report.SkippedShas) _purgeRetry.AddOrUpdate(sha, 0, (_, attempts) => attempts);
                    if (report.SkippedShas.Count > 0 || !report.LockAcquired && job.Done is null) ScheduleRetry(job);
                    job.Done?.TrySetResult(report);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { job.Done?.TrySetCanceled(); }
        catch (Exception ex)
        {
            ReportFailure("derived cache maintenance", ex);
            job.Done?.TrySetException(ex);
        }
    }

    // A reader held an entry, or another window's janitor held the lock: try again shortly, a few times.
    private void ScheduleRetry(Job job)
    {
        if (_disposed) return;
        if (job.Done is null && job.Select is { } select && _purgeRetry.IsEmpty)
        {
            // An automatic purge that found the lock busy has no SHA list: requeue it once after the delay.
            var timer = _policy.Clock.CreateTimer(_ => { if (!_disposed) _jobs.Writer.TryWrite(new Job(JobKind.Purge, select)); },
                null, _policy.PurgeRetryDelay, Timeout.InfiniteTimeSpan);
            Interlocked.Exchange(ref _retryTimer, timer)?.Dispose();
            return;
        }
        var next = _policy.Clock.CreateTimer(_ => RequestCleanup(), null, _policy.PurgeRetryDelay, Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _retryTimer, next)?.Dispose();
    }

    private void RetryPurges()
    {
        if (_purgeRetry.IsEmpty) return;
        var wanted = _purgeRetry.Keys.ToHashSet(StringComparer.Ordinal);
        var report = _janitor.Purge(entry => wanted.Contains(entry.Sha), _stop.Token);
        if (!report.LockAcquired) return;
        var skipped = report.SkippedShas.ToHashSet(StringComparer.Ordinal);
        foreach (var sha in wanted)
        {
            if (!skipped.Contains(sha)) { _purgeRetry.TryRemove(sha, out _); continue; }
            var attempts = _purgeRetry.AddOrUpdate(sha, 1, (_, value) => value + 1);
            if (attempts >= _policy.PurgeRetryAttempts) _purgeRetry.TryRemove(sha, out _);
        }
        if (!_purgeRetry.IsEmpty) ScheduleRetry(new Job(JobKind.Clean));
    }

    private void TouchLedger()
    {
        if (_ledger is null) return;
        var now = _policy.Clock.GetUtcNow();
        var mounted = _catalog.Mounts.Select(mount => mount.Root).Append(_catalog.Root)
            .Select(DerivedMountLedger.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ledgerView = new(_ledger.Touch(mounted, now), mounted, now);
    }

    /// <summary>
    /// Listed content is live. Unlisted content whose library is attached was removed. Content of an unplugged
    /// library is live for <see cref="DerivedCacheMaintenancePolicy.UnmountedAging"/> after the library was last seen,
    /// then it goes first; a library never seen is aged by the entry's own last use.
    /// </summary>
    private DerivedCacheEntryClass ClassifyEntry(DerivedCacheEntry entry)
    {
        if (_catalog.Find(entry.Sha) is not null) return DerivedCacheEntryClass.Live;
        var view = _ledgerView;
        if (view is null || entry.OriginalPath is not { } original) return DerivedCacheEntryClass.Unreferenced;
        string full;
        try { full = Path.GetFullPath(original); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return DerivedCacheEntryClass.Unreferenced; }
        var owner = view.Seen.Keys.Concat(view.Mounted)
            .Where(root => full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .MaxBy(root => root.Length);
        if (owner is not null && view.Mounted.Contains(owner)) return DerivedCacheEntryClass.Unreferenced;
        var since = owner is not null && view.Seen.TryGetValue(owner, out var seen) ? seen.UtcDateTime : entry.LastUsedUtc;
        return view.Now.UtcDateTime - since > _policy.UnmountedAging
            ? DerivedCacheEntryClass.UnmountedStale : DerivedCacheEntryClass.Live;
    }
}

/// <summary>
/// When each catalog folder was last attached (cache/derived/mounts.json). Drive letters change, but the store's
/// headers name the exact original path, so the folder path is the right key: a library back under another letter
/// is a different folder whose old preparations no longer match and age out.
/// </summary>
internal sealed class DerivedMountLedger(string root)
{
    public const string FileName = "mounts.json";
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(6);
    private readonly string _path = Path.Combine(root, FileName);
    private Dictionary<string, DateTimeOffset>? _seen;

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public Dictionary<string, DateTimeOffset> Touch(IEnumerable<string> mounted, DateTimeOffset now)
    {
        _seen ??= Load();
        var changed = false;
        foreach (var folder in mounted)
            if (!_seen.TryGetValue(folder, out var seen) || now - seen >= Refresh || seen > now)
            { _seen[folder] = now; changed = true; }
        if (changed) Save();
        return new(_seen, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, DateTimeOffset> Load()
    {
        try
        {
            var file = new FileInfo(_path);
            if (file.Exists && file.Length <= 1024 * 1024
                && JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllBytes(_path)) is { } stored)
                return new(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        // Another window may have written newer times: keep the later of each.
        foreach (var (folder, seen) in Load())
            if (!_seen!.TryGetValue(folder, out var mine) || seen > mine) _seen[folder] = seen;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_seen));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
