using Map.Assets;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>
/// Resolves project assets lazily and makes missing/hash-mismatched links visible. The canvas draws from it on the
/// render thread; style preparation, the pictures of «Вид» and the panels take images through a <see cref="Lease"/>.
/// </summary>
public sealed class CatalogStampImageSource(
    AssetCatalog catalog, Func<MapDocument> document, Func<string?> projectPath, int imageCapacity = 4096,
    long cacheBytes = 512L * 1024 * 1024, bool retainSamples = true, string? derivedRoot = null,
    TimeProvider? retryClock = null, Action<FileSystemWatcher>? startWatcher = null,
    DerivedCacheMaintenancePolicy? derivedMaintenance = null) : IStampImageSource, IDisposable, IAsyncDisposable
{
    private readonly StampDerivedAssetStore? _derived = derivedRoot is null ? null : new(catalog, derivedRoot,
        retryClock: retryClock, startWatcher: startWatcher, maintenance: derivedMaintenance);

    /// <summary>True when this source keeps the machine cache of preparations (and runs its janitor).</summary>
    public bool HasDerivedCache => _derived is not null;
    public DerivedCacheJanitorReport? LastDerivedCleanup => _derived?.LastCleanup;
    public long DerivedCleanups => _derived?.Cleanups ?? 0;
    // Targeted cleanup of the machine cache (DEC-111 p.4); without a cache there is nothing to remove.
    public Task<DerivedCachePurgeResult> PurgeDerived(IEnumerable<string> sha) =>
        _derived?.PurgeDerived(sha) ?? Task.FromResult(DerivedCachePurgeResult.Nothing);
    public Task<DerivedCachePurgeResult> PurgeDerivedByTag(string tag) =>
        _derived?.PurgeDerivedByTag(tag) ?? Task.FromResult(DerivedCachePurgeResult.Nothing);
    public Task<DerivedCachePurgeResult> PurgeUnused() =>
        _derived?.PurgeUnused() ?? Task.FromResult(DerivedCachePurgeResult.Nothing);
    public Task<DerivedCacheJanitorReport?> CleanDerivedNowAsync() =>
        _derived is null ? Task.FromResult<DerivedCacheJanitorReport?>(null) : CleanNow(_derived);
    private static async Task<DerivedCacheJanitorReport?> CleanNow(StampDerivedAssetStore store) => await store.CleanNowAsync().ConfigureAwait(false);
    private readonly TimeProvider _retryClock = retryClock ?? TimeProvider.System;
    private readonly CancellationTokenSource _retryStop = new();
    private Task? _missingRetry;
    private readonly HashSet<StampDerivedAssetStore.PixelKey> _cacheableMissing = [];
    // Per-SHA count of _cacheableMissing keys: the overview asks per asset inside a frame (no linear scan).
    private readonly Dictionary<string, int> _cacheableMissingShas = new(StringComparer.OrdinalIgnoreCase);
    private long _missingRevision;
    public long MissingRevision => Interlocked.Read(ref _missingRevision);
    [ThreadStatic] private static bool _lastImageRequestCacheableMissing;
    public bool LastImageRequestCacheableMissing => _lastImageRequestCacheableMissing;
    public bool IsMissingCacheable(string sha256)
    {
        lock (_gate) return _derived?.IsTerminalMissing(sha256) == true
            || _cacheableMissingShas.ContainsKey(sha256);
    }
    private int _overviewNotification;
    private int _previewNotification;
    private readonly HashSet<string> _derivedNotifications = new(StringComparer.OrdinalIgnoreCase);
    private Action? _derivedReady;
    public void SetFrameReady(Action ready)
    {
        lock (_gate)
        {
            _derivedReady = ready;
            if (_derived is not null) { _derived.SourceChanged = ready; _derived.OverviewChanged = ready; }
        }
    }
    private readonly HashSet<StampDerivedAssetStore.PixelKey> _wantedMips = [];
    private readonly HashSet<StampDerivedAssetStore.PixelKey> _latePixels = [];
    private readonly Dictionary<string, long> _pixelRetryAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pixelFailures = new(StringComparer.OrdinalIgnoreCase);
    private long _sourceRevision;
    public long SourceRevision => Interlocked.Read(ref _sourceRevision);
    public long? OverviewColorRevision => _derived?.OverviewColorRevision;
    // A background lease can resolve between the render-thread lookup and its state read.
    // This is the result of the immediately preceding lookup on this thread, not global state.
    [ThreadStatic] private static bool _lastImageRequestPending;
    public bool LastImageRequestPending { get => _lastImageRequestPending; private set => _lastImageRequestPending = value; }
    private long _unresolvedRequestVersion;
    public long UnresolvedRequestVersion => _unresolvedRequestVersion;
    public bool ImageRequestsPending { get { lock (_gate) return !_disposed && _latePixels.Count > 0; } }
    /// <summary>Never waits for a loader/lease. A false result means the journal should keep its previous snapshot.</summary>
    public bool TryReadPerformanceQueues(out PerformanceQueues queues)
    {
        queues = default;
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            var derived = _derived?.PerformanceQueueLengths ?? default;
            queues = new(derived.Metadata, derived.Builds, derived.Pixels, _decoding.Count, _latePixels.Count);
            return true;
        }
        finally { Monitor.Exit(_gate); }
    }
    public (long DiskHits, long Builds, long DiskBytes, long DiskEvictions, int MetadataEntries, int PendingMips, long DiscardedMips) DerivedStatistics
        => (_derived?.DiskHits ?? 0, _derived?.Builds ?? 0, _derived?.DiskBytes ?? 0,
            _derived?.DiskEvictions ?? 0, _derived?.MetadataEntries ?? 0, _derived?.PendingMips ?? 0, _derived?.DiscardedMips ?? 0);

    public StampDerivedAssetStore.IoSnapshot DerivedIoDiagnostics => _derived?.IoDiagnostics ?? default;

    public bool PrepareOverview(IEnumerable<string> assets, Action ready)
    {
        if (_derived is null) return true;
        _derived.SourceChanged = ready;
        _derived.OverviewChanged = ready;
        string[] shas;
        lock (_gate)
        {
            _derivedReady = ready;
            // Colours remembered from an earlier run are the overview at once; their headers are read only when
            // pixels are wanted (StampDerivedAssetStore.Colors).
            shas = assets.Where(sha => !TakeKnownColor(sha)).ToArray();
            foreach (var sha in shas) TrackMissingHeader(sha);
            shas = shas.Where(sha => !IsMissingCacheable(sha)).ToArray();
        }
        if (shas.Length == 0) return true;
        var generation = _derived.Generation;
        var tasks = shas.Select(_derived.Request).ToArray();
        var pending = tasks.Where(task => !task.IsCompleted).ToArray();
        if (pending.Length == 0)
        {
            RememberOverview(tasks, generation);
            return true;
        }
        var previews = shas.Select(_derived.Preview).Where(task => !task.IsCompleted).ToArray();
        if (previews.Length > 0 && Interlocked.CompareExchange(ref _previewNotification, 1, 0) == 0)
            _ = Task.WhenAll(previews).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _previewNotification, 0);
                if (!_disposed) ready();
            }, TaskScheduler.Default);
        if (Interlocked.CompareExchange(ref _overviewNotification, 1, 0) == 0)
            _ = Task.WhenAll(tasks).ContinueWith(_ =>
            {
                RememberOverview(tasks, generation);
                Interlocked.Exchange(ref _overviewNotification, 0);
                if (!_disposed) ready();
            }, TaskScheduler.Default);
        return false;
    }

    private void RememberOverview(IEnumerable<Task<StampDerivedAssetStore.Header?>> tasks, long generation)
    {
        lock (_gate)
        {
            if (_disposed || _derived?.Generation != generation) return;
            // Compact colors belong to this document's assets, independent of the bounded
            // heavy metadata/task cache. An overview with >8192 assets must still converge.
            foreach (var task in tasks)
                if (task.IsCompletedSuccessfully && task.Result is { } header)
                    _overviewColors[header.Sha] = new SKColor(header.Color);
        }
    }
    // Large owner art keeps its master plus a ladder of halved steps; the budget holds the
    // working steps of a whole set and the masters of the few objects a close view shows.
    // Many distinct small stamps must fit as well as a few large masters. The byte budget,
    // not a 64-image thumbnail limit, bounds their memory. Ladder steps count as entries too.
    private readonly AssetImageCache _cache = Tracked(new(imageCapacity, retainSamples || derivedRoot is not null ? cacheBytes * 3 / 4 : cacheBytes),
        "asset-images", Map.Core.Resources.CacheRebuildCost.Moderate);
    private static AssetImageCache Tracked(AssetImageCache cache, string name, Map.Core.Resources.CacheRebuildCost cost)
    {
        cache.TrackMemory(Map.Core.Resources.WorkingMemoryBudget.Shared, name, cost);
        return cache;
    }
    // The total remains 512 MiB. Copies of the exact sampled pixels survive source churn;
    // neither the resampling algorithm nor the chosen detail level changes.
    // One SHA can need several exact mips in the current view plus the fallback view.
    // A 4096-entry ceiling evicted tiny pixels below the byte limit before a 1804-asset
    // preflight could become resident. Keep the 128 MiB budget, allow all sixteen levels.
    private readonly AssetImageCache _samples = Tracked(new(Math.Max(imageCapacity, 4096) * 16, Math.Max(1, cacheBytes / 4)),
        "asset-samples", Map.Core.Resources.CacheRebuildCost.Cheap);
    private readonly Dictionary<string, (int Width, int Height)> _sampleDimensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Width, int Height)> _workingDimensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SKColor> _overviewColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _validatedThisFrame = new(StringComparer.OrdinalIgnoreCase);
    // Opt-in native-probe timings; normal editor frames do not read the clock for these counters.
    public bool MeasureTimings { get; set; }
    private long _loadTicks, _sampleTicks, _overviewTicks, _adoptTicks, _loadCalls, _sampleCalls;
    public (double LoadMs, double SampleMs, double OverviewMs, long LoadCalls, long SampleCalls, double AdoptMs) Timings
    {
        get
        {
            lock (_gate)
            {
                var ms = 1000d / System.Diagnostics.Stopwatch.Frequency;
                return (_loadTicks * ms, _sampleTicks * ms, _overviewTicks * ms, _loadCalls, _sampleCalls, _adoptTicks * ms);
            }
        }
    }

    public (int Entries, long ResidentBytes, long Hits, long Misses, long Evictions) CacheStatistics
    {
        get { lock (_gate) return (_cache.Count + _samples.Count, _cache.ResidentBytes + _samples.ResidentBytes,
            _cache.Hits + _samples.Hits, _cache.Misses + _samples.Misses, _cache.Evictions + _samples.Evictions); }
    }
    public (int Entries, long Bytes, long Evictions) SampleStatistics
    { get { lock (_gate) return (_samples.Count, _samples.ResidentBytes, _samples.Evictions); } }

    public sealed record MipDiagnostic(string Sha, int Step, int Level, bool RequestedLastFrame, bool Pending,
        bool Resident, int Width, int Height, bool CacheableMissing, bool DecodeFailed);

    public sealed record MipSelectionDiagnostic(string Sha, double RequestedWidth, double RequestedHeight,
        int ImageWidth, int ImageHeight, string Path, long Frame);
    private readonly Dictionary<string, MipSelectionDiagnostic> _diagnosticSelections = new(StringComparer.OrdinalIgnoreCase);
    private long _diagnosticFrame;
    public bool CaptureMipSelections { get; set; }
    public object ReadMipSelections()
    {
        lock (_gate) return new { Frame = _diagnosticFrame, LastPerSha = _diagnosticSelections.Values.ToArray() };
    }
    private SKImage? ObserveMipSelection(string sha, double width, double height, SKImage? image, string path)
    {
        if (CaptureMipSelections)
            _diagnosticSelections[sha] = new(sha, width, height, image?.Width ?? 0, image?.Height ?? 0, path, _diagnosticFrame);
        return image;
    }

    /// <summary>Explicit probe only: describes requested/pending mip keys without loading or touching LRU recency.
    /// Resident means accepted into a pixel cache, not necessarily the mip selected by the last draw.</summary>
    public IReadOnlyList<MipDiagnostic> ReadMipDiagnostics()
    {
        lock (_gate)
        {
            var resident = _samples.ReadResidentSizes().Concat(_cache.ReadResidentSizes())
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
                    group => group.First(), StringComparer.OrdinalIgnoreCase);
            var keys = _wantedMips.Concat(_latePixels).ToHashSet();
            foreach (var name in resident.Keys)
            {
                var parts = name.Split('#'); var source = parts[0].Split('@');
                if (source[0].Length != 64) continue;
                keys.Add(new(source[0], source.Length > 1 ? int.Parse(source[1], System.Globalization.CultureInfo.InvariantCulture) : 0,
                    parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0));
            }
            return keys.Select(key =>
            {
                var name = PixelName(key);
                var found = resident.TryGetValue(name, out var size);
                if (!found && key.Level == 0) found = resident.TryGetValue(SourceName(key), out size);
                return new MipDiagnostic(key.Sha, key.Step, key.Level, _wantedMips.Contains(key), _latePixels.Contains(key),
                    found, size.Width, size.Height, _cacheableMissing.Contains(key) || _cacheableMissingShas.ContainsKey(key.Sha),
                    _decodeFailed.Contains(name));
            }).ToArray();
        }
    }
    private readonly Dictionary<string, (string Path, long Length, DateTime LastWriteUtc)> _resolved =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingSteps = new(StringComparer.OrdinalIgnoreCase);

    // One lock guards all the state of this source. The audit of 2026-11-01 found style preparation reading this
    // cache on the thread pool while the canvas drew from it: no lock, and an eviction disposed an image that another
    // thread was still drawing (more than 64 different pictures were enough).
    private readonly object _gate = new();
    // The thread inside a canvas frame (0 between frames): what other threads evict meanwhile is freed when it ends.
    private int _frameThread;
    private int _leases;
    private bool _disposed;
    // While a lease plans its fetch, the first miss that needs a file read and decode is noted here instead of done:
    // the lease decodes outside the lock, so the canvas never waits for a background decode.
    private bool _planning;
    private Fetch? _plan;
    private HashSet<string>? _planFailed;
    private sealed record Fetch(string Key, string? Path, byte[]? Embedded, string? Sha256, string? MediaType, string? Extension);

    /// <summary>
    /// The working image of an asset. For an original with a detail ladder this is its first
    /// halved step: the same picture and proportions, at a size that keeps every object of a set
    /// resident. The full master is decoded only by <see cref="TryGetImageForPixelSize"/> when a
    /// close view needs more pixels than that step holds.
    /// </summary>
    /// <summary>
    /// A drive library was attached or removed: pictures that were missing while the drive was
    /// away are looked for again, and paths resolved on a previous drive are not reused.
    /// </summary>
    public void ForgetMissing()
    {
        _derived?.Forget();
        lock (_gate)
        {
            Interlocked.Increment(ref _sourceRevision);
            Enter();
            foreach (var key in _sampleDimensions.Keys)
                for (var level = 0; level <= 15; level++) { _samples.Remove(key + "#" + level); _cache.Remove(key + "#" + level); }
            _sampleDimensions.Clear();
            _workingDimensions.Clear();
            _overviewColors.Clear();
            _validatedThisFrame.Clear();
            _missingSteps.Clear();
            _decodeFailed.Clear();
            _latePixels.Clear(); _pixelRetryAt.Clear(); _pixelFailures.Clear();
            _cacheableMissing.Clear(); _cacheableMissingShas.Clear();
            _derivedNotifications.Clear();
            _resolved.Clear();
        }
    }

    public SKImage? TryGetImage(string sha256)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            Enter();
            return WorkingImage(sha256);
        }
    }

    public SKImage? TryGetImageForOcclusion(string sha256)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            Enter();
            return _cache.Get(sha256 + "@1", null) ?? _cache.Get(sha256, null);
        }
    }

    private SKImage? WorkingImage(string sha256)
    {
        LastImageRequestPending = false;
        _lastImageRequestCacheableMissing = false;
        if (!_planning && _derived is not null && _frameThread == Environment.CurrentManagedThreadId
            && catalog.Find(sha256) is { } record)
        {
            var step = AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0 ? 1 : 0;
            var key = new StampDerivedAssetStore.PixelKey(sha256.ToLowerInvariant(), step, 0);
            var ready = ReadyPixels(key);
            if (ready is null && !_decodeFailed.Contains(PixelName(key)))
            {
                _latePixels.Add(key);
                if (QueuePixels(key, _derivedReady ?? (() => { }))) _latePixels.Remove(key);
                LastImageRequestPending = !_decodeFailed.Contains(PixelName(key));
            }
            _lastImageRequestCacheableMissing = _cacheableMissing.Contains(key);
            if (ready is null && !_lastImageRequestCacheableMissing) _unresolvedRequestVersion++;
            return ready;
        }
        var image = TryGetLadderStep(sha256, 1) ?? (_plan is null ? TryGetOriginal(sha256) : null);
        if (image is not null) _workingDimensions[sha256] = (image.Width, image.Height);
        return image;
    }

    public (int Width, int Height)? TryGetImageSize(string sha256)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_derived?.TryReady(sha256) is { } prepared) return (prepared.Width, prepared.Height);
            if (_derived is not null && catalog.Find(sha256) is { } metadata)
            {
                var step = AssetCatalog.DetailLadderDepth(metadata.Width, metadata.Height) > 0 ? 1 : 0;
                return (Math.Max(1, metadata.Width >> step), Math.Max(1, metadata.Height >> step));
            }
            Enter(); ValidateRetained(sha256);
            if (retainSamples && _workingDimensions.TryGetValue(sha256, out var size)) return size;
            return WorkingImage(sha256) is { } image ? (image.Width, image.Height) : null;
        }
    }

    /// <summary>Under _gate: a colour the derived store remembers becomes this source's overview colour.</summary>
    private bool TakeKnownColor(string sha256)
    {
        if (_derived?.KnownColor(sha256) is not { } remembered)
        {
            _overviewColors.Remove(sha256);
            return false;
        }
        _overviewColors[sha256] = remembered;
        return true;
    }

    public SKColor? TryGetOverviewColor(string sha256)
    {
        if (_derived is not null && catalog.Find(sha256) is not null)
        {
            lock (_gate)
            {
                if (_disposed) return null;
                if (TakeKnownColor(sha256)) return _overviewColors[sha256];
                TrackMissingHeader(sha256);
                if (IsMissingCacheable(sha256)) return null;
                _derived.Request(sha256);
                if (_derived.TryReady(sha256) is { } prepared)
                    return _overviewColors[sha256] = new SKColor(prepared.Color);
            }
            // Pending is not missing: the catalog's existing thumbnail is the temporary tint.
            // Until even that is ready, leave its points transparent, never paint the red error glyph.
            return _derived.Request(sha256).IsCompleted ? null : _derived.TryPreview(sha256) ?? SKColors.Transparent;
        }
        lock (_gate)
        {
            var started = MeasureTimings ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                if (_disposed) return null;
                Enter(); ValidateRetained(sha256);
                if (retainSamples && _overviewColors.TryGetValue(sha256, out var color)) return color;
                if (WorkingImage(sha256) is not { } image) return null;
                color = SceneRenderer.StampOverviewColor(image);
                if (retainSamples) _overviewColors[sha256] = color;
                return color;
            }
            finally { if (started != 0) _overviewTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; }
        }
    }

    private void ValidateRetained(string sha256)
    {
        if (_frameThread != 0 && !_validatedThisFrame.Add(sha256)) return;
        if (!_resolved.TryGetValue(sha256, out var held)) return;
        var file = new FileInfo(held.Path);
        if (file.Exists && file.Length == held.Length && file.LastWriteTimeUtc == held.LastWriteUtc) return;
        _workingDimensions.Remove(sha256);
        _overviewColors.Remove(sha256);
        foreach (var key in _sampleDimensions.Keys.Where(k => k == sha256 || k.StartsWith(sha256 + "@", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            for (var level = 0; level <= 15; level++) _samples.Remove(key + "#" + level);
            _sampleDimensions.Remove(key);
        }
        _cache.Remove(sha256);
        _resolved.Remove(sha256);
    }

    /// <summary>Called under the lock by every entry: only a frame's own thread may free what it evicts at once.</summary>
    private void Enter() =>
        _samples.DeferDisposal = _cache.DeferDisposal = _frameThread != 0 && _frameThread != Environment.CurrentManagedThreadId;

    private SKImage? TryGetOriginal(string sha256)
    {
        var reference = document().Assets.FirstOrDefault(asset => asset.Sha256 == sha256);
        if (reference?.EmbeddedContent is { } embedded)
        {
            return Load(sha256, null, embedded, null, reference.MediaType, reference.Extension);
        }

        var record = catalog.Find(sha256);
        if (record is not null)
        {
            var resolution = ResolveCatalog(record);
            return resolution is null ? null : Load(
                sha256, resolution, null, sha256, record.MediaType, record.Extension);
        }

        if (reference is { Storage: ProjectAssetStorage.Linked, RelativePath: { } relative }
            && projectPath() is { } mapPath)
        {
            if (ProjectLinkedAssetPath.TryResolve(mapPath, relative, out var candidate)
                && ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(mapPath, candidate))
            {
                return Load(sha256, candidate, null, sha256, reference.MediaType, reference.Extension);
            }
        }

        return null;
    }

    /// <summary>
    /// A cached image, or — on the render thread — the image read and decoded now. While a lease plans, the miss is
    /// noted as its fetch and null returned.
    /// </summary>
    private SKImage? Load(string key, string? path, byte[]? embedded, string? sha256, string? mediaType, string? extension)
    {
        var started = MeasureTimings ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            if (!_planning)
                return embedded is not null ? _cache.GetEncoded(key, embedded, mediaType, extension)
                    : sha256 is not null ? _cache.GetVerified(key, path, sha256, mediaType, extension)
                    : _cache.Get(key, path);
            if (_cache.Get(key, null) is { } held) return held;
            if (_plan is null && _planFailed?.Contains(key) != true) _plan = new(key, path, embedded, sha256, mediaType, extension);
            return null;
        }
        finally
        {
            if (started != 0) { _loadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; _loadCalls++; }
        }
    }

    /// <summary>
    /// A large original is read through its detail ladder: a far view decodes only the halved
    /// step just above the size it draws, and the full master only when the view is close enough
    /// to need more pixels than the first step holds. Below the deepest step the cache's own mip
    /// pyramid of that step takes over. Assets without a ladder keep the original path.
    /// </summary>
    public SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            Enter();
            return ForPixelSize(sha256, width, height);
        }
    }

    public SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            Enter();
            if (_planning || _derived is null || _frameThread != Environment.CurrentManagedThreadId
                || catalog.Find(sha256) is not { } record) return WorkingImage(sha256);
            return ReadyFallback(record, sha256, width, height);
        }
    }

    /// <summary>The frame's resident fallback for an exact lease whose key will not arrive (failed / missing).</summary>
    private SKImage? LeaseReadyFallback(ExactImageLease lease, string sha256, double width, double height)
    {
        lock (_gate)
        {
            if (_disposed || lease.Closed || catalog.Find(sha256) is not { } record) return null;
            Enter();
            if (ReadyFallback(record, sha256, width, height) is not { } image) return null;
            _cache.Pin(image);
            _samples.Pin(image);
            lease.Held.Add(image);
            return image;
        }
    }

    private SKImage? ReadyFallback(AssetRecord record, string sha256, double width, double height)
    {
        var found = ResidentLevel(record, PixelKey(record, width, height));
        return found is { } hit && CaptureMipSelections
            ? ObserveMipSelection(sha256, width, height, hit.Image, "ready-fallback/" + hit.Name) : found?.Image;
    }

    /// <summary>The resident level nearest to <paramref name="wanted"/>: a smaller one first, then a larger one.</summary>
    private (SKImage Image, string Name)? ResidentLevel(AssetRecord record, StampDerivedAssetStore.PixelKey wanted)
    {
        var depth = AssetCatalog.DetailLadderDepth(record.Width, record.Height);
        var desired = wanted.Step + wanted.Level;
        // Only resident levels are looked at here (nothing is enqueued). The exact level, then up to two sharper ones
        // (a sharp picture scaled down beats a soft one stretched: the wheel blur, 2026-11-15), then the softer ones,
        // then any sharper one left. Beyond two levels a sharper texture costs more GPU than it is worth here.
        if (At(desired) is { } exact) return exact;
        for (var level = desired - 1; level >= Math.Max(0, desired - 2); level--)
            if (At(level) is { } sharper) return sharper;
        for (var level = desired + 1; level <= 15; level++)
            if (At(level) is { } softer) return softer;
        for (var level = desired - 3; level >= 0; level--)
            if (At(level) is { } larger) return larger;
        return null;

        (SKImage, string)? At(int level)
        {
            var step = Math.Min(depth, level);
            var key = new StampDerivedAssetStore.PixelKey(wanted.Sha, step, level - step);
            if (ReadyPixels(key) is not { } image) return null;
            var name = PixelName(key);
            return (_samples.GetFallbackImage(name) ?? _cache.GetFallbackImage(name) ?? image, name);
        }
    }

    private SKImage? ForPixelSize(string sha256, double width, double height)
    {
        LastImageRequestPending = false;
        _lastImageRequestCacheableMissing = false;
        if (!_planning && _derived is not null && _frameThread == Environment.CurrentManagedThreadId
            && catalog.Find(sha256) is { } recordForFrame)
        {
            var key = PixelKey(recordForFrame, width, height);
            var ready = ReadyPixels(key);
            if (ready is null && !_decodeFailed.Contains(PixelName(key)))
            {
                // Float geometry/rotation may cross a mip boundary that the double-sized
                // preflight did not request. Do not declare its resident fallback final.
                if (!_wantedMips.Contains(key)) _latePixels.Add(key);
                if (QueuePixels(key, _derivedReady ?? (() => { }))) _latePixels.Remove(key);
                LastImageRequestPending = !_decodeFailed.Contains(PixelName(key));
            }
            _lastImageRequestCacheableMissing = _cacheableMissing.Contains(key);
            if (ready is null && !_lastImageRequestCacheableMissing) _unresolvedRequestVersion++;
            return CaptureMipSelections ? ObserveMipSelection(sha256, width, height, ready, "exact-request/" + PixelName(key)) : ready;
        }
        // Prepared raw pixels have already passed the worker's source-stamp and SHA checks.
        // Do not reopen/stat the original merely to draw a resident derived sample.
        if (_derived is not null && CachedSample(sha256, width, height) is { } prepared) return prepared;
        ValidateRetained(sha256);
        if (catalog.Find(sha256) is { } record && AssetCatalog.DetailLadderDepth(record.Width, record.Height) is > 0 and var depth)
        {
            var ratio = Math.Min(record.Width / Math.Max(1, width), record.Height / Math.Max(1, height));
            var level = Math.Min(depth, (int)Math.Floor(Math.Log2(Math.Max(1, ratio))));
            if (level == 0) return TryGetOriginal(sha256) ?? (_plan is null ? TryGetLadderStep(sha256, 1) : null);
            var key = sha256 + "@" + level;
            if (CachedSample(key, width, height) is { } sampled) return sampled;
            if (TryGetLadderStep(sha256, level) is { } step)
                return Sample(key, step, width, height);
            if (_plan is not null) return null;
        }

        if (CachedSample(sha256, width, height) is { } cached) return cached;
        return TryGetOriginal(sha256) is { } original ? Sample(sha256, original, width, height) : null;
    }

    private static int SampleLevel((int Width, int Height) size, double width, double height) =>
        Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, Math.Min(size.Width / Math.Max(1, width), size.Height / Math.Max(1, height))))), 0, 15);

    private SKImage? CachedSample(string key, double width, double height) =>
        (retainSamples || _derived is not null) && _sampleDimensions.TryGetValue(key, out var size)
            ? _samples.Get(key + "#" + SampleLevel(size, width, height), null) : null;

    private SKImage Sample(string key, SKImage original, double width, double height)
    {
        var started = MeasureTimings ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            var image = _cache.GetForPixelSize(key, width, height) ?? original;
            if (!retainSamples && _derived is null) return image;
            var size = (Width: original.Width, Height: original.Height);
            _sampleDimensions[key] = size;
            var level = SampleLevel(size, width, height);
            // A memory-pressure fallback may return the original. Do not retain it under a mip key.
            if (image.Width != Math.Max(1, size.Width >> level) || image.Height != Math.Max(1, size.Height >> level)
                || (long)image.Width * image.Height * 4 > cacheBytes / 4) return image;
            using var pixels = SKBitmap.FromImage(image);
            return _samples.AdoptPixels(key + "#" + level, SKImage.FromBitmap(pixels)) ?? image;
        }
        finally
        {
            if (started != 0) { _sampleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started; _sampleCalls++; }
        }
    }

    /// <summary>
    /// A lease for work off the render thread or on the UI thread. It takes the lock only to look up and adopt; its
    /// decodes run on its own thread. What it hands out is pinned in the shared cache until it is disposed — the
    /// prepared form maps of a style belong to these very images, so preparation must use them, not copies.
    /// </summary>
    public IStampImageLease Lease()
    {
        lock (_gate)
        {
            _leases++;
            return new ImageLease(this);
        }
    }

    /// <summary>Prepared mip bytes a settled frame may take in (future texture uploads); zero or less — no limit.</summary>
    public long AdoptedMipBytesPerFrame { get; set; } = 8L * 1024 * 1024;
    /// <summary>The same while the camera moves (set by the canvas from its last frame).</summary>
    public long GestureAdoptedMipBytesPerFrame { get; set; } = 2L * 1024 * 1024;
    /// <summary>Whether the canvas's last frame moved the camera.</summary>
    public bool FrameMoving { get; set; }
    private long _byteLimitedFrames;
    /// <summary>Frames whose mip adoption stopped at the byte limit.</summary>
    public long ByteLimitedFrames => Interlocked.Read(ref _byteLimitedFrames);

    /// <summary>The canvas frame on the render thread: evictions by other threads wait until it ends.</summary>
    public IDisposable? BeginFrame()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            _frameThread = Environment.CurrentManagedThreadId;
            _derived?.ObserveCanvasThread();
            _validatedThisFrame.Clear();
            _wantedMips.Clear();
            if (CaptureMipSelections) _diagnosticFrame++;
            if (_derived is not null)
                foreach (var sha in _derived.TakeInvalidated()) InvalidateDerived(sha);
            // Consume completed work even if the camera moved away from its request. Otherwise
            // eight old completions can occupy every slot and starve the new view indefinitely.
            if (_derived is not null)
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                // Every adopted mip becomes a texture upload when it is first drawn: on an integrated GPU a frame
                // that takes many megabytes misses the display tick (PresentMon on the laptop, 14.11: GPU p99 24 ms,
                // max 55 ms). Bound the bytes per frame as well as the CPU time; one mip always passes.
                var byteBudget = FrameMoving ? GestureAdoptedMipBytesPerFrame : AdoptedMipBytesPerFrame;
                long adoptedBytes = 0;
                foreach (var key in _derived.ReadyPixels)
                {
                    if (_derived.TryTakePixels(key, out var mip))
                    {
                        AdoptDerivedMip(key, mip);
                        adoptedBytes += (long)(mip?.Width ?? 0) * (mip?.Height ?? 0) * 4;
                    }
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2
                        || byteBudget > 0 && adoptedBytes >= byteBudget)
                    {
                        if (adoptedBytes >= byteBudget) _byteLimitedFrames++;
                        if (_derived.ReadyPixels.Any()) _derivedReady?.Invoke();
                        break;
                    }
                }
                if (MeasureTimings) _adoptTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            }
            return new FrameScope(this);
        }
    }

    private void EndFrame()
    {
        lock (_gate)
        {
            _frameThread = 0;
            // A frame that asked for no stamp pixels at all (a moving camera shown from its kept frame, a scene
            // drawn from kept layer images) says nothing about what the view needs: cancelling then dropped every
            // sharp request of the gesture, and the sharp level started only ~135 ms after the camera stopped
            // (the blur the owner saw on wheel, 2026-11-15).
            if (_wantedMips.Count > 0)
            {
                // A late request can still be waiting for metadata when the camera leaves it;
                // then no pixel task exists to deliver a cancellation back to BeginFrame.
                _latePixels.RemoveWhere(key => !_wantedMips.Contains(key));
                _wantedMips.UnionWith(_cacheableMissing);
                // Kept overview rasters wait for these exact keys: a frame that did not draw them must not cancel them.
                _wantedMips.UnionWith(_leaseWanted.Keys);
                _derived?.CancelUnwantedPixels(_wantedMips);
            }
            if (_disposed) { DisposeIfUnused(); return; }
            _cache.DeferDisposal = false;
            _samples.DeferDisposal = false;
            _cache.DisposeRetired();
            _samples.DisposeRetired();
            // Only while Windows is short of memory: images nobody drew for two seconds, on the frame's own thread
            // after it, within 2 ms together. Sampled steps (cheap to remake) go before decoded sources.
            _samples.TrimForMemoryPressure(1);
            _cache.TrimForMemoryPressure(1);
        }
    }

    private void InvalidateDerived(string sha)
    {
        Interlocked.Increment(ref _sourceRevision);
        Enter();
        _latePixels.RemoveWhere(key => string.Equals(key.Sha, sha, StringComparison.OrdinalIgnoreCase));
        _overviewColors.Remove(sha); _workingDimensions.Remove(sha); _resolved.Remove(sha);
        foreach (var key in _pixelFailures.Keys.Where(key => key.StartsWith(sha + "#", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(sha + "@", StringComparison.OrdinalIgnoreCase)).ToArray())
        { _pixelFailures.Remove(key); _pixelRetryAt.Remove(key); _decodeFailed.Remove(key); }
        foreach (var key in _sampleDimensions.Keys.Where(key => key.Equals(sha, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(sha + "@", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            for (var level = 0; level <= 15; level++) { _samples.Remove(key + "#" + level); _cache.Remove(key + "#" + level); _decodeFailed.Remove(key + "#" + level); }
            _sampleDimensions.Remove(key);
        }
        for (var level = 0; level <= 15; level++) _cache.Remove(level == 0 ? sha : sha + "@" + level);
    }

    private SKImage? LeaseGet(ImageLease lease, Func<SKImage?> resolve)
    {
        // A few rounds at most: the working step, then the original if the step is missing, then its mip.
        for (var round = 0; round < 6; round++)
        {
            Fetch? fetch;
            lock (_gate)
            {
                if (_disposed || lease.Closed) return null;
                Enter();
                _planning = true; _plan = null; _planFailed = lease.Failed;
                SKImage? image;
                try { image = resolve(); }
                finally { fetch = _plan; _planning = false; _plan = null; _planFailed = null; }
                if (image is not null)
                {
                    _cache.Pin(image);
                    _samples.Pin(image);
                    lease.Held.Add(image);
                    return image;
                }
                if (fetch is null) return null;
            }
            var (encoded, decoded) = Decode(fetch);
            lock (_gate)
            {
                if (_disposed || lease.Closed) { decoded?.Dispose(); return null; }
                Enter();
                if (decoded is null || encoded is null
                    || _cache.Adopt(fetch.Key, encoded, decoded, fetch.MediaType, fetch.Extension) is null && !_cache.Contains(fetch.Key))
                    lease.Failed.Add(fetch.Key);
            }
        }
        return null;
    }

    /// <summary>The same checks as the cache's own read: size, content hash, format, then the decode.</summary>
    private static (byte[]? Encoded, SKImage? Image) Decode(Fetch fetch)
    {
        try
        {
            var encoded = fetch.Embedded;
            if (encoded is null)
            {
                if (string.IsNullOrWhiteSpace(fetch.Path)) return (null, null);
                var file = new FileInfo(fetch.Path);
                if (!file.Exists || file.Length is <= 0 or > AssetImagePolicy.MaxEncodedBytes) return (null, null);
                encoded = AssetImagePolicy.ReadEncodedFile(file.FullName);
            }
            if (fetch.Sha256 is not null && !string.Equals(Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(encoded)), fetch.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, null);
            _ = fetch.MediaType is null || fetch.Extension is null
                ? AssetImagePolicy.Inspect(encoded)
                : AssetImagePolicy.Inspect(encoded, fetch.MediaType, fetch.Extension);
            return (encoded, AssetImageCache.DecodeOwned(encoded));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, null);
        }
    }

    private void EndLease(ImageLease lease)
    {
        lock (_gate)
        {
            if (lease.Closed) return;
            lease.Closed = true;
            _leases--;
            if (_disposed) { DisposeIfUnused(); return; }
            foreach (var image in lease.Held) { _cache.Unpin(image); _samples.Unpin(image); }
            lease.Held.Clear();
            Enter();
            if (!_cache.DeferDisposal) { _cache.DisposeRetired(); _samples.DisposeRetired(); }
        }
    }

    /// <inheritdoc cref="IStampImageSource.ExactLease"/>
    public IStampImageLease ExactLease(CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (_derived is null) return Lease();
            _leases++;
            return new ExactImageLease(this, cancellation);
        }
    }

    // A kept raster (one exact lease) waits at most this long for all its images together; past it the lookup gives
    // up (counted, raster not kept) and the rest of that raster no longer waits.
    internal static TimeSpan ExactWaitLimit { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The pixels a settled frame draws for this request (<see cref="ForPixelSize"/> / <see cref="WorkingImage"/> on
    /// the frame thread), from a worker thread: ready ones are pinned to the lease; missing ones are requested from
    /// the derived store, kept wanted across frames, and waited for here (the caller is a background worker, as the
    /// lease that decoded synchronously was). False when the record has no prepared pixels: the lease then answers
    /// as an ordinary one. True with a null image when the key failed or is cacheable-missing (the frame draws its
    /// resident fallback then, and so does the lease) or when the wait was cancelled / timed out (counted).
    /// </summary>
    private bool TryExactPixels(ExactImageLease lease, string sha256, StampDerivedAssetStore.PixelKey? requested,
        out SKImage? image)
    {
        image = null;
        StampDerivedAssetStore.PixelKey? interest = null;
        try
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_disposed || lease.Closed) return true;
                    if (catalog.Find(sha256) is not { } record) return false;
                    Enter();
                    var key = requested ?? WorkingKey(record);
                    var name = PixelName(key);
                    var ready = ReadyPixels(name); // also ends an expired retry backoff
                    if (ready is null && _derived!.TryTakePixels(key, out var mip))
                    {
                        AdoptDerivedMip(key, mip);
                        ready = ReadyPixels(name);
                    }
                    if (ready is not null)
                    {
                        _cache.Pin(ready);
                        _samples.Pin(ready);
                        lease.Held.Add(ready);
                        image = ready;
                        return true;
                    }
                    // A key that will not arrive (cacheable-missing, or failed for good): the frame draws its
                    // resident fallback then, and so does the lease — a consistent, final picture.
                    if (_cacheableMissing.Contains(key)
                        || _pixelRetryAt.TryGetValue(name, out var final) && final == long.MaxValue) return true;
                    // This raster already gave up on an image (time limit / cancel): it will not be kept, so later
                    // lookups do not wait — the worker is free for the next chunk at once.
                    if (lease.Preparing > 0) return true;
                    if (interest is null) { interest = key; AddInterest(key); }
                    _wantedMips.Add(key);
                    if (_decodeFailed.Contains(name))
                    {
                        // A temporary failure in its backoff: wait for the retry instead of keeping a fallback.
                        var retry = _pixelRetryAt.GetValueOrDefault(name) - _retryClock.GetUtcNow().ToUnixTimeMilliseconds();
                        wait = Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(retry, 1, 60_000)), _retryClock);
                    }
                    else
                    {
                        var header = _derived!.Request(key.Sha);
                        if (!header.IsCompleted) wait = header;
                        else if (header.IsCompletedSuccessfully && header.Result is { } prepared)
                        {
                            // The held task when another request already asked; the capacity signal when the store is full.
                            _derived.TryRequestPixels(prepared, key.Step, key.Level, out var pixels);
                            wait = pixels;
                        }
                        else { FailedPixels(key); continue; }
                    }
                }
                var left = lease.Deadline - Environment.TickCount64;
                try
                {
                    if (left <= 0 || !wait.Wait(TimeSpan.FromMilliseconds(left), lease.Cancellation))
                    { lock (_gate) lease.Preparing++; return true; }
                }
                catch (OperationCanceledException) { lock (_gate) lease.Preparing++; return true; }
                catch (AggregateException) { /* a failed task: the next round sees the failure through the store */ }
            }
        }
        finally
        {
            if (interest is { } held) lock (_gate) RemoveInterest(held);
        }
    }

    private static StampDerivedAssetStore.PixelKey WorkingKey(AssetRecord record) =>
        new(record.Sha256.ToLowerInvariant(), AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0 ? 1 : 0, 0);

    /// <summary>Keys exact leases are waiting for, with how many lookups wait: kept wanted across frames.</summary>
    private readonly Dictionary<StampDerivedAssetStore.PixelKey, int> _leaseWanted = [];
    private void AddInterest(StampDerivedAssetStore.PixelKey key) => _leaseWanted[key] = _leaseWanted.GetValueOrDefault(key) + 1;
    private void RemoveInterest(StampDerivedAssetStore.PixelKey key)
    {
        if (!_leaseWanted.TryGetValue(key, out var count)) return;
        if (count <= 1) _leaseWanted.Remove(key); else _leaseWanted[key] = count - 1;
    }

    // Re-implements the interface so its own PreparingLookups / LastImageRequestPending are the ones mapped.
    private sealed class ExactImageLease(CatalogStampImageSource source, CancellationToken cancellation)
        : ImageLease(source), IStampImageLease
    {
        private readonly CatalogStampImageSource _source = source;
        internal readonly CancellationToken Cancellation = cancellation;
        internal readonly long Deadline = Environment.TickCount64 + (long)ExactWaitLimit.TotalMilliseconds;
        internal int Preparing;
        public int PreparingLookups { get { lock (_source._gate) return Preparing; } }

        public override SKImage? TryGetImage(string sha256) =>
            _source.TryExactPixels(this, sha256, null, out var image) ? image : base.TryGetImage(sha256);

        public override SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
        {
            var record = _source.FindRecord(sha256);
            if (record is null) return base.TryGetImageForPixelSize(sha256, width, height);
            return _source.TryExactPixels(this, sha256, PixelKey(record, width, height), out var image) ? image
                : base.TryGetImageForPixelSize(sha256, width, height);
        }

        public override SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height) =>
            _source.LeaseReadyFallback(this, sha256, width, height);
    }

    private AssetRecord? FindRecord(string sha256)
    {
        lock (_gate) return _disposed ? null : catalog.Find(sha256);
    }

    private class ImageLease(CatalogStampImageSource source) : IStampImageLease
    {
        public long SourceRevision => source.SourceRevision;
        internal readonly List<SKImage> Held = [];
        internal readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
        internal bool Closed;
        public virtual SKImage? TryGetImage(string sha256) => source.LeaseGet(this, () => source.WorkingImage(sha256));
        // Derived tints are cheap metadata. Legacy/embedded images decode through the lease,
        // outside the shared source gate, so background overview work cannot hold up a frame.
        public SKColor? TryGetOverviewColor(string sha256) => source.HasDerivedOverview(sha256)
            ? source.TryGetOverviewColor(sha256)
            : TryGetImage(sha256) is { } image ? SceneRenderer.StampOverviewColor(image) : null;
        public virtual SKImage? TryGetImageForPixelSize(string sha256, double width, double height) =>
            source.LeaseGet(this, () => source.ForPixelSize(sha256, width, height));
        public virtual SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height) => TryGetImage(sha256);
        // A lease draws what it asks for on its own thread, so every image is ready when asked.
        public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready) => true;
        public IStampImageLease Lease() => source.Lease();
        public void Dispose() => source.EndLease(this);
    }

    private bool HasDerivedOverview(string sha256) => _derived is not null && catalog.Find(sha256) is not null;

    private sealed class FrameScope(CatalogStampImageSource source) : IDisposable
    {
        private bool _ended;
        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            source.EndFrame();
        }
    }

    // Images decoded off the render thread wait here until the render thread takes them in:
    // the cache itself is used by one thread only.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Decoded> _decoded = new();
    private readonly HashSet<string> _decoding = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _decodeFailed = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Decoded(string Key, byte[]? Encoded, SKImage? Image, string? MediaType, string? Extension);

    /// <summary>
    /// The first frame of a closer zoom used to decode the larger ladder steps of every mountain
    /// in view on the render thread (~100 ms for six 4096 px originals). Each missing step or
    /// original is now read, verified and decoded on the thread pool, and the tile that needs it
    /// keeps showing the neighbouring zoom level until it arrives (R05, 2026-10-23).
    /// </summary>
    public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
    {
        // The frame never queues behind a background raster pinning its plan under the same gate: trace of the
        // wheel at 25 000 objects (PC, 2026-11-15) - 5 of 10 s of DrawObjects in Monitor.Enter here, 35-108 ms frames
        // that drew nothing. Busy: this frame draws what is resident (the "not yet ready" path) and asks again.
        var taken = false;
        try
        {
            Monitor.TryEnter(_gate, FrameGateWaitMilliseconds, ref taken);
            if (!taken)
            {
                Interlocked.Increment(ref _frameGateMisses);
                ready();
                return false;
            }
            if (_disposed) return true;
            Enter();
            return PrepareLocked(stamps, ready);
        }
        finally { if (taken) Monitor.Exit(_gate); }
    }

    /// <summary>How long a frame may wait for the source's gate before drawing what is resident instead.</summary>
    internal const int FrameGateWaitMilliseconds = 1;
    private long _frameGateMisses;
    /// <summary>Frames that did not wait for a busy gate (diagnostics).</summary>
    public long FrameGateMisses => Interlocked.Read(ref _frameGateMisses);

    private bool PrepareLocked(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
    {
        _derivedReady = ready;
        if (_derived is not null) _derived.SourceChanged = ready;
        AdoptDecoded();
        var resident = true;
        var seen = new HashSet<(string, int)>();
        var seenPixels = new HashSet<StampDerivedAssetStore.PixelKey>();
        AssetRecord? record = null;
        foreach (var (sha256, width, height) in stamps)
        {
            if (record?.Sha256 != sha256) record = catalog.Find(sha256);
            if (_derived is not null && record is not null)
            {
                var pixel = PixelKey(record, width, height);
                if (!seenPixels.Add(pixel)) continue;
                _wantedMips.Add(pixel);
                if (ReadyPixels(pixel) is null && !_decodeFailed.Contains(PixelName(pixel)))
                    resident &= QueuePixels(pixel, ready);
                continue;
            }
            if (record is null || AssetCatalog.DetailLadderDepth(record.Width, record.Height) is not (> 0 and var depth)) continue;
            var ratio = Math.Min(record.Width / Math.Max(1, width), record.Height / Math.Max(1, height));
            var level = Math.Min(depth, (int)Math.Floor(Math.Log2(Math.Max(1, ratio))));
            if (!seen.Add((sha256, level))) continue;
            var key = level == 0 ? sha256 : sha256 + "@" + level;
            ValidateRetained(sha256);
            if (CachedSample(key, width, height) is not null) continue;
            if (_cache.Contains(key) || _decodeFailed.Contains(key) || _missingSteps.Contains(key)) continue;
            if (_decoding.Contains(key)) { resident = false; continue; }
            if (level == 0)
            {
                // The original is hashed against its content id, as the synchronous path does.
                if (document().Assets.Any(asset => asset.Sha256 == sha256 && asset.EmbeddedContent is not null)
                    || ResolveCatalog(record) is not { } original) continue;
                resident = false;
                StartDecode(key, original, sha256, record.MediaType, record.Extension, ready);
            }
            else if (catalog.TryDetailLevelPath(record, level, out var step)) { resident = false; StartDecode(key, step, null, null, null, ready); }
            else _missingSteps.Add(key);
        }
        return resident;
    }

    /// <summary>The sampled-image shelf: a frame asks for at most this many bytes of tile images at once.</summary>
    public long PreparationBytes => Math.Max(1, cacheBytes / 4);

    /// <summary>
    /// A dense tile's stamps reduced once to their distinct pixel keys. A refining frame then checks a few
    /// thousand keys instead of walking a quarter of a million objects through <see cref="PrepareLocked"/>,
    /// and a tile whose keys are all resident answers at once until the shelf evicts something.
    /// </summary>
    public IStampPreparationPlan? CreatePreparationPlan()
    {
        lock (_gate) return _disposed || _derived is null ? null : new PreparationPlan(this, SourceRevision);
    }

    private sealed class PreparationPlan(CatalogStampImageSource source, long revision) : IStampPreparationPlan
    {
        internal readonly HashSet<StampDerivedAssetStore.PixelKey> Keys = [];
        internal (StampDerivedAssetStore.PixelKey Key, string Name)[]? Names;
        internal long ReadyAt = -1, CheckedAt = -1;
        internal (StampDerivedAssetStore.PixelKey Key, string Name)[]? Missing;
        public long Bytes { get; internal set; }
        public long SourceRevision => revision;
        internal readonly Dictionary<string, AssetRecord> Records = new(StringComparer.OrdinalIgnoreCase);
        public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps) => source.AddToPlan(this, stamps);
        public bool Prepare(Action ready) => source.PreparePlan(this, ready);
        public IStampImageLease? Pin() => source.PinPlan(this);
        public IStampImageLease? PinResident() => source.PinPlan(this, resident: true);
    }

    /// <summary>
    /// The plan's resident images, pinned for a background tile raster: the shelf may evict them meanwhile, but
    /// they are freed only when the raster lets go. Null while any of them is not resident.
    /// </summary>
    private IStampImageLease? PinPlan(PreparationPlan plan, bool resident = false)
    {
        lock (_gate)
        {
            if (_disposed || plan.SourceRevision != SourceRevision) return null;
            Enter();
            var images = new Dictionary<StampDerivedAssetStore.PixelKey, SKImage>();
            foreach (var (pixel, name) in plan.Names ??= [.. plan.Keys.Select(key => (key, PixelName(key)))])
            {
                if (ReadyPixels(name) is { } image) images[pixel] = image;
                // A stand-in: the resident level the frame's own fallback would draw, or nothing (pending) for now.
                else if (resident)
                {
                    if (plan.Records.TryGetValue(pixel.Sha, out var record) && ResidentLevel(record, pixel) is { } other) images[pixel] = other.Image;
                }
                // A failed key stays out: the raster comes back incomplete and the frame draws that tile as before.
                else if (!_decodeFailed.Contains(name)) return null;
            }
            var sizes = new Dictionary<string, (int Width, int Height)>(StringComparer.OrdinalIgnoreCase);
            var occlusion = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in plan.Records.Values)
            {
                if (TryGetImageSize(record.Sha256) is { } size) sizes[record.Sha256] = size;
                // The same resident shape TryGetImageForOcclusion gives the frame, so culling is unchanged.
                if ((_cache.Get(record.Sha256 + "@1", null) ?? _cache.Get(record.Sha256, null)) is { } shape) occlusion[record.Sha256] = shape;
            }
            var held = images.Values.Concat(occlusion.Values).Distinct().ToArray();
            foreach (var image in held) { _cache.Pin(image); _samples.Pin(image); }
            _leases++;
            return new PinnedPlan(this, plan.Records, images, sizes, occlusion, held);
        }
    }

    private void EndPinnedPlan(SKImage[] held)
    {
        lock (_gate)
        {
            _leases--;
            if (_disposed) { DisposeIfUnused(); return; }
            foreach (var image in held) { _cache.Unpin(image); _samples.Unpin(image); }
            Enter();
            if (!_cache.DeferDisposal) { _cache.DisposeRetired(); _samples.DisposeRetired(); }
        }
    }

    /// <summary>What a background tile draws from: a fixed snapshot of one plan, never a request or a decode.</summary>
    private sealed class PinnedPlan(CatalogStampImageSource source, IReadOnlyDictionary<string, AssetRecord> records,
        Dictionary<StampDerivedAssetStore.PixelKey, SKImage> images, Dictionary<string, (int Width, int Height)> sizes,
        Dictionary<string, SKImage> occlusion, SKImage[] held) : IStampImageLease
    {
        private int _disposed;
        public long SourceRevision => source.SourceRevision;
        public long UnresolvedRequestVersion { get; private set; }
        public bool LastImageRequestPending { get; private set; }
        public (int Width, int Height)? TryGetImageSize(string sha256) => sizes.TryGetValue(sha256, out var size) ? size : null;
        public SKImage? TryGetImage(string sha256) => Miss();
        public SKImage? TryGetImageForOcclusion(string sha256) => occlusion.GetValueOrDefault(sha256);
        public SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height) => null;
        public SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
        {
            LastImageRequestPending = false;
            return records.TryGetValue(sha256, out var record) && images.TryGetValue(PixelKey(record, width, height), out var image)
                ? image : Miss();
        }
        private SKImage? Miss() { LastImageRequestPending = true; UnresolvedRequestVersion++; return null; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) source.EndPinnedPlan(held);
        }
    }

    private void AddToPlan(PreparationPlan plan, IEnumerable<(string Sha256, double Width, double Height)> stamps)
    {
        lock (_gate)
        {
            AssetRecord? record = null;
            foreach (var (sha256, width, height) in stamps)
            {
                if (record?.Sha256 != sha256) record = catalog.Find(sha256);
                // As in PrepareLocked: a stamp without a catalog record has nothing to prepare.
                if (record is null) continue;
                var pixel = PixelKey(record, width, height);
                plan.Records.TryAdd(record.Sha256, record);
                if (plan.Keys.Add(pixel))
                    plan.Bytes += (long)Math.Max(1, Math.Max(1, record.Width >> pixel.Step) >> pixel.Level)
                        * Math.Max(1, Math.Max(1, record.Height >> pixel.Step) >> pixel.Level) * 4;
            }
            plan.Names = null; plan.Missing = null; plan.CheckedAt = -1;
        }
    }

    /// <summary>The <see cref="PrepareLocked"/> contract over a plan's distinct keys.</summary>
    private bool PreparePlan(PreparationPlan plan, Action ready)
    {
        lock (_gate)
        {
            if (_disposed) return true;
            Enter();
            _derivedReady = ready;
            if (_derived is not null) _derived.SourceChanged = ready;
            AdoptDecoded();
            // Resident keys stay resident until the shelf evicts something or a source changes.
            var epoch = _cache.Evictions + _samples.Evictions;
            if (plan.ReadyAt == epoch && plan.SourceRevision == SourceRevision) return true;
            // Nothing left the shelf since the last check: only the keys that were missing then can have changed.
            var keys = plan.CheckedAt == epoch && plan.Missing is { } missing ? missing
                : plan.Names ??= [.. plan.Keys.Select(key => (key, PixelName(key)))];
            var stillMissing = new List<(StampDerivedAssetStore.PixelKey Key, string Name)>();
            // At most 512 new requests per check: a cold tile asks for thousands, spread over a few frames.
            var asked = 0;
            foreach (var (pixel, name) in keys)
            {
                if (ReadyPixels(name) is not null) continue;
                // Still wanted, so a request already in flight is not cancelled at the end of the frame.
                _wantedMips.Add(pixel);
                if (asked >= 512) { stillMissing.Add((pixel, name)); continue; }
                asked++;
                if (!_decodeFailed.Contains(name) && !QueuePixels(pixel, ready)) stillMissing.Add((pixel, name));
            }
            plan.Missing = [.. stillMissing];
            plan.CheckedAt = _cache.Evictions + _samples.Evictions;
            if (stillMissing.Count == 0) plan.ReadyAt = plan.CheckedAt;
            return stillMissing.Count == 0;
        }
    }

    private void NotifyDerived(string key, Task task, Action ready)
    {
        if (!_derivedNotifications.Add(key)) return;
        _ = task.ContinueWith(_ =>
        {
            lock (_gate) _derivedNotifications.Remove(key);
            if (!_disposed) ready();
        }, TaskScheduler.Default);
    }

    private static StampDerivedAssetStore.PixelKey PixelKey(AssetRecord record, double width, double height)
    {
        var depth = AssetCatalog.DetailLadderDepth(record.Width, record.Height);
        var step = Math.Min(depth, SampleLevel((record.Width, record.Height), width, height));
        var level = depth > 0 && step == 0 ? 0
            : SampleLevel((Math.Max(1, record.Width >> step), Math.Max(1, record.Height >> step)), width, height);
        return new(record.Sha256.ToLowerInvariant(), step, level);
    }

    private static string SourceName(StampDerivedAssetStore.PixelKey key) => key.Step == 0 ? key.Sha : key.Sha + "@" + key.Step;
    private static string PixelName(StampDerivedAssetStore.PixelKey key) => SourceName(key) + "#" + key.Level;
    private SKImage? ReadyPixels(StampDerivedAssetStore.PixelKey key) => ReadyPixels(PixelName(key));

    private SKImage? ReadyPixels(string name)
    {
        if (_pixelRetryAt.TryGetValue(name, out var retry) && _retryClock.GetUtcNow().ToUnixTimeMilliseconds() >= retry)
        { _pixelRetryAt.Remove(name); _decodeFailed.Remove(name); }
        return _samples.Get(name, null) ?? _cache.Get(name, null);
    }

    private void FailedPixels(StampDerivedAssetStore.PixelKey pixel, bool permanent = false)
    {
        var name = PixelName(pixel);
        _decodeFailed.Add(name);
        if (!permanent && _pixelRetryAt.ContainsKey(name)) return;
        var attempts = _pixelFailures.GetValueOrDefault(name) + 1;
        _pixelFailures[name] = attempts;
        if (permanent || attempts >= 4)
        {
            _pixelRetryAt[name] = long.MaxValue;
            if (_cacheableMissing.Add(pixel))
                _cacheableMissingShas[pixel.Sha] = _cacheableMissingShas.GetValueOrDefault(pixel.Sha) + 1;
            _latePixels.Remove(pixel);
            _derived!.SetCacheableMissing(pixel.Sha, true);
            _missingRetry ??= Task.Run(RetryMissingLoop);
            return;
        }
        var delay = 2100 << (attempts - 1);
        _pixelRetryAt[name] = _retryClock.GetUtcNow().ToUnixTimeMilliseconds() + delay;
        _ = Task.Delay(TimeSpan.FromMilliseconds(delay), _retryClock, _retryStop.Token).ContinueWith(task =>
        { if (task.IsCompletedSuccessfully && !_disposed) _derivedReady?.Invoke(); }, TaskScheduler.Default);
    }

    private async Task RetryMissingLoop()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), _retryClock, _retryStop.Token).ConfigureAwait(false);
                StampDerivedAssetStore.PixelKey[] missing;
                lock (_gate) missing = _cacheableMissing.ToArray();
                foreach (var pixel in missing)
                {
                    var header = await _derived!.Request(pixel.Sha).ConfigureAwait(false);
                    if (header is null) continue;
                    Task<StampDerivedAssetStore.Mip> task;
                    lock (_gate)
                    {
                        if (_disposed || !_cacheableMissing.Contains(pixel)) continue;
                        if (!_derived.TryRequestPixels(header, pixel.Step, pixel.Level, out task)) continue;
                    }
                    await task.ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (_disposed || !_cacheableMissing.Contains(pixel)) continue;
                        // Same cache lock and lifetime deferral as background image leases.
                        // Failed retries release their slots silently; only usable pixels wake UI.
                        Enter();
                        if (_derived.TryTakePixels(pixel, out var mip)) AdoptDerivedMip(pixel, mip);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_retryStop.IsCancellationRequested) { }
    }

    private void RecoveredPixels(StampDerivedAssetStore.PixelKey pixel)
    {
        var name = PixelName(pixel);
        _pixelFailures.Remove(name); _pixelRetryAt.Remove(name); _decodeFailed.Remove(name);
        if (!_cacheableMissing.Remove(pixel)) return;
        // Distinct from SourceRevision: ordinary mip arrivals do not change any revision.
        Interlocked.Increment(ref _missingRevision);
        _overviewColors.Remove(pixel.Sha);
        var left = _cacheableMissingShas.GetValueOrDefault(pixel.Sha) - 1;
        if (left > 0) _cacheableMissingShas[pixel.Sha] = left;
        else { _cacheableMissingShas.Remove(pixel.Sha); _derived!.SetCacheableMissing(pixel.Sha, false); }
        _derivedReady?.Invoke();
    }

    private void TrackMissingHeader(string sha)
    {
        if (_derived?.IsTerminalMissing(sha) != true || catalog.Find(sha) is not { } record) return;
        var step = AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0 ? 1 : 0;
        var pixel = new StampDerivedAssetStore.PixelKey(sha.ToLowerInvariant(), step, 0);
        if (!_cacheableMissing.Contains(pixel)) FailedPixels(pixel, permanent: true);
    }

    // Normal completions are adopted by BeginFrame under its 2 ms budget. A preflight
    // may run several times per frame; draining here would multiply that budget.
    private bool QueuePixels(StampDerivedAssetStore.PixelKey key, Action ready)
    {
        _wantedMips.Add(key);
        var header = _derived!.Request(key.Sha);
        if (!header.IsCompleted) { NotifyDerived(key.Sha, header, ready); return false; }
        if (header.IsCompletedSuccessfully && header.Result is { } prepared)
        {
            var accepted = _derived.TryRequestPixels(prepared, key.Step, key.Level, out var pixels);
            NotifyDerived(accepted ? PixelName(key) : "queue-capacity", pixels, ready);
            return false;
        }
        FailedPixels(key);
        return true;
    }

    private void AdoptDerivedMip(StampDerivedAssetStore.PixelKey pixel, StampDerivedAssetStore.Mip? mip)
    {
        var key = PixelName(pixel);
        if (catalog.Find(pixel.Sha) is { } record)
            _sampleDimensions[SourceName(pixel)] = (Math.Max(1, record.Width >> pixel.Step), Math.Max(1, record.Height >> pixel.Step));
        if (mip?.Image is { } image)
        {
            var target = Math.Max(image.Width, image.Height) <= 256 ? _samples : _cache;
            if (target.AdoptPixels(key, image) is null && !target.Contains(key)) FailedPixels(pixel, permanent: true);
            else RecoveredPixels(pixel);
        }
        else if (mip?.Pixels is { } pixels)
        {
            using var bitmap = new SKBitmap(new SKImageInfo(mip.Width, mip.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            bitmap.SetImmutable();
            if (_samples.AdoptPixels(key, SKImage.FromBitmap(bitmap)) is null && !_samples.Contains(key)) FailedPixels(pixel, permanent: true);
            else RecoveredPixels(pixel);
        }
        else if (mip is not { Cancelled: true }) FailedPixels(pixel);
        // Ordinary mip arrivals leave SourceRevision and MissingRevision unchanged.
        _latePixels.Remove(pixel);
        _derivedNotifications.Remove(key);
    }

    private void StartDecode(string key, string path, string? sha256, string? mediaType, string? extension, Action ready)
    {
        _decoding.Add(key);
        _ = Task.Run(() =>
        {
            byte[]? encoded = null; SKImage? image = null;
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length is > 0 and <= AssetImagePolicy.MaxEncodedBytes)
                {
                    encoded = AssetImagePolicy.ReadEncodedFile(file.FullName);
                    if (sha256 is null || string.Equals(Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(encoded)), sha256, StringComparison.OrdinalIgnoreCase))
                        image = AssetImageCache.DecodeOwned(encoded);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            _decoded.Enqueue(new(key, encoded, image, mediaType, extension));
            ready();
        });
    }

    private void AdoptDecoded()
    {
        while (_decoded.TryDequeue(out var item))
        {
            _decoding.Remove(item.Key);
            if (item.Image is null || item.Encoded is null
                || _cache.Adopt(item.Key, item.Encoded, item.Image, item.MediaType, item.Extension) is null && !_cache.Contains(item.Key))
                _decodeFailed.Add(item.Key);
        }
    }

    private SKImage? TryGetLadderStep(string sha256, int level)
    {
        var key = sha256 + "@" + level;
        if (_cache.Get(key, null) is { } held) return held;
        if (_missingSteps.Contains(key) || catalog.Find(sha256) is not { } record) return null;
        if (catalog.TryDetailLevelPath(record, level, out var path) && Load(key, path, null, null, null, null) is { } image)
            return image;
        // A lease that is about to read the step has not found it missing.
        if (_plan is not null) return null;
        _missingSteps.Add(key);
        return null;
    }

    private string? ResolveCatalog(AssetRecord record)
    {
        if (_resolved.TryGetValue(record.Sha256, out var cached) && File.Exists(cached.Path))
        {
            var info = new FileInfo(cached.Path);
            if (info.Length == cached.Length && info.LastWriteTimeUtc == cached.LastWriteUtc)
            {
                return cached.Path;
            }

            _cache.Remove(record.Sha256);
            _resolved.Remove(record.Sha256);
        }

        AssetResolution resolution;
        try
        {
            resolution = catalog.Resolve(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            return null;
        }

        var resolvedInfo = new FileInfo(resolution.Path);
        _resolved[record.Sha256] =
            (resolution.Path, resolvedInfo.Length, resolvedInfo.LastWriteTimeUtc);
        return resolution.Path;
    }

    /// <summary>The editor closed: images still held by a lease or a frame in progress are freed when those end.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _retryStop.Cancel();
            _derived?.Dispose();
            DisposeIfUnused();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_derived is not null) await _derived.Completion.ConfigureAwait(false);
        if (_missingRetry is not null) await _missingRetry.ConfigureAwait(false);
    }

    private void DisposeIfUnused()
    {
        if (_leases > 0 || _frameThread != 0) return;
        while (_decoded.TryDequeue(out var item)) item.Image?.Dispose();
        _cache.Dispose();
        _samples.Dispose();
    }
}
