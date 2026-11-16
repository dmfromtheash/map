using SkiaSharp;

namespace Map.Presentation.Services;

public sealed partial class CatalogStampImageSource
{
    private enum FrameQuery { Image, Size, Pixels, Ready, Occlusion, Color, Missing }
    private readonly record struct FrameKey(FrameQuery Query, string Sha, double Width = 0, double Height = 0);
    private readonly record struct FrameAnswer(SKImage? Image = null, (int Width, int Height)? Size = null,
        SKColor? Color = null, bool Missing = false, long Revision = 0, long PinBytes = 0);
    private Dictionary<FrameKey, FrameAnswer> _previousFrame = [], _currentFrame = [];
    private Dictionary<string, FrameKey> _previousFallbacks = new(StringComparer.OrdinalIgnoreCase),
        _currentFallbacks = new(StringComparer.OrdinalIgnoreCase);
    private bool _readingFrame;
    private bool _frameContended;
    private bool _frameBeganUnderGate;
    private bool _frameAnswersRotated;
    private long _currentFramePinBytes, _previousFramePinBytes;
    private int _frameCleanupScheduled;
    private Task? _frameCleanup;
    internal long FrameSnapshotPinnedBytes => _currentFramePinBytes + _previousFramePinBytes;
    private readonly HashSet<FrameKey> _requestedFrameWork = [];
    private bool IsCanvasThread => Volatile.Read(ref _frameThread) == Environment.CurrentManagedThreadId;
    private bool IsFrameQuery => IsCanvasThread && !_readingFrame;

    // Only BeginFrame waits (at most 1 ms). A lookup never multiplies that wait by the visible object count.
    // Both dictionaries belong to the canvas thread. Their images are pinned under _gate until rotation;
    // a busy worker can therefore evict/replace an entry without freeing the frame's resident fallback.
    private FrameAnswer ReadFrame(FrameKey key)
    {
        if (!Monitor.TryEnter(_gate))
            return BusyFrameAnswer(key);
        var derivedTaken = false;
        try
        {
            if (_disposed) return default;
            if (_derived is not null && !(derivedTaken = _derived.TryEnterFrameGate())) return BusyFrameAnswer(key);
            if (!_frameAnswersRotated) RotateFrameAnswers();
            _readingFrame = true;
            var answer = key.Query switch
            {
                FrameQuery.Image => new FrameAnswer(Image: TryGetImage(key.Sha)),
                FrameQuery.Size => new FrameAnswer(Size: TryGetImageSize(key.Sha)),
                FrameQuery.Pixels => new FrameAnswer(Image: TryGetImageForPixelSize(key.Sha, key.Width, key.Height)),
                FrameQuery.Ready => new FrameAnswer(Image: TryGetReadyImageForPixelSize(key.Sha, key.Width, key.Height)),
                FrameQuery.Occlusion => new FrameAnswer(Image: TryGetImageForOcclusion(key.Sha)),
                FrameQuery.Color => new FrameAnswer(Color: TryGetOverviewColor(key.Sha)),
                FrameQuery.Missing => new FrameAnswer(Missing: IsMissingCacheable(key.Sha)),
                _ => default
            };
            answer = answer with { Revision = SourceRevision };
            if (_currentFrame.TryGetValue(key, out var old) && ReferenceEquals(old.Image, answer.Image))
            {
                _currentFrame[key] = answer with { PinBytes = old.PinBytes };
                RememberFrameFallback(key, answer);
                return answer;
            }
            if (old.Image is { } previous)
            { _cache.Unpin(previous); _samples.Unpin(previous); }
            _currentFramePinBytes -= old.PinBytes;
            _currentFrame.Remove(key);
            if (answer.Image is { } image)
            {
                var bytes = _cache.RetentionBytes(image) + _samples.RetentionBytes(image);
                // A dense view must not pin unbounded masters beyond the existing cache budget. Oversized
                // answers remain drawable in this frame; a busy next frame may use its ordinary fallback.
                if (bytes > FramePinByteBudget - _currentFramePinBytes) return answer;
                _cache.Pin(image); _samples.Pin(image);
                answer = answer with { PinBytes = bytes };
                _currentFramePinBytes += bytes;
            }
            _currentFrame[key] = answer;
            RememberFrameFallback(key, answer);
            return answer;
        }
        finally
        {
            _readingFrame = false;
            if (derivedTaken) _derived!.ExitFrameGate();
            Monitor.Exit(_gate);
        }
    }

    private FrameAnswer BusyFrameAnswer(FrameKey key)
    {
        _frameContended = true;
        LastImageRequestPending = true;
        _lastImageRequestCacheableMissing = false;
        Interlocked.Increment(ref _unresolvedRequestVersion);
        if (!_currentFrame.TryGetValue(key, out var answer)) _previousFrame.TryGetValue(key, out answer);
        if (key.Query == FrameQuery.Ready && answer.Image is null)
        {
            // The wheel changes pixel dimensions every frame. Keep a resident picture even when this exact
            // request was not made before; its pending bit prevents provisional pixels becoming a kept tile.
            if (_currentFallbacks.TryGetValue(key.Sha, out var current) && _currentFrame.TryGetValue(current, out var held)
                && held.Revision == SourceRevision) return ObserveBusySelection(key, held);
            if (_previousFallbacks.TryGetValue(key.Sha, out var previous) && _previousFrame.TryGetValue(previous, out held)
                && held.Revision == SourceRevision) return ObserveBusySelection(key, held);
        }
        return ObserveBusySelection(key, answer.Revision == SourceRevision ? answer : default);
    }

    private FrameAnswer ObserveBusySelection(FrameKey key, FrameAnswer answer)
    {
        if (CaptureMipSelections && key.Query is FrameQuery.Pixels or FrameQuery.Ready)
        {
            // The source gate is busy: publish diagnostics without touching its guarded dictionary.
            // Orders prevent this deferred observation overwriting a later ordinary lookup when read.
            var path = key.Query == FrameQuery.Pixels ? "exact-request/frame-resident"
                : answer.Image is null ? "ready-missing/frame-resident" : "ready-fallback/frame-resident";
            var selection = new MipSelectionDiagnostic(key.Sha, key.Width, key.Height, answer.Image?.Width ?? 0,
                answer.Image?.Height ?? 0, path, Interlocked.Read(ref _diagnosticFrame));
            _busySelections.Enqueue((selection, Interlocked.Increment(ref _diagnosticSelectionSerial)));
        }
        return answer;
    }

    private void RememberFrameFallback(FrameKey key, FrameAnswer answer)
    {
        if (answer.Image is not null && key.Query is FrameQuery.Pixels or FrameQuery.Ready)
            _currentFallbacks[key.Sha] = key;
    }

    private void RotateFrameAnswers()
    {
        ReleaseFrameAnswers(_previousFrame);
        (_previousFrame, _currentFrame) = (_currentFrame, _previousFrame);
        (_previousFallbacks, _currentFallbacks) = (_currentFallbacks, _previousFallbacks);
        _currentFallbacks.Clear();
        _previousFramePinBytes = _currentFramePinBytes;
        _currentFramePinBytes = 0;
        _frameAnswersRotated = true;
    }

    private void ReleaseFrameAnswers(Dictionary<FrameKey, FrameAnswer> answers)
    {
        foreach (var answer in answers.Values)
            if (answer.Image is { } image) { _cache.Unpin(image); _samples.Unpin(image); }
        answers.Clear();
    }

    private void QueueFrameCleanup()
    {
        if (Interlocked.CompareExchange(ref _frameCleanupScheduled, 1, 0) != 0) return;
        _frameCleanup = Task.Run(() =>
        {
            try { lock (_gate) { if (_frameThread == 0) FinishFrame(cancelRequests: false); } }
            finally { Volatile.Write(ref _frameCleanupScheduled, 0); }
        });
    }

    // Counts source filesystem boundaries, including calls into the catalog/cache which can stat/read.
    // Kept internal for the disk guard; derived-store reads have their own thread-aware diagnostics.
    internal Action<string>? FileAccess { get; set; }
    private void TouchFile(string operation) => FileAccess?.Invoke(operation);

    // The non-derived/embedded route uses the existing lease decoder on a worker too. Deduplication is per
    // visible request, not per object. No path resolution, stat, read, hash or decode runs on the canvas thread.
    private readonly HashSet<FrameKey> _frameWork = [];
    private readonly HashSet<Task> _frameTasks = [];
    private long _frameWorkSchedules;
    internal long FrameWorkSchedules => Interlocked.Read(ref _frameWorkSchedules);
    private readonly Dictionary<FrameKey, long> _frameFailedUntil = [];
    private SKImage? ExactResidentFrameImage(string sha, double width, double height, bool working)
    {
        var image = working ? _cache.Get(sha + "@1", null) ?? _cache.Get(sha, null)
            : CachedSample(sha, width, height) ?? _cache.GetReadyForPixelSize(sha, width, height);
        if (!working && image is null && catalog.Find(sha) is { } record)
        {
            var depth = Map.Assets.AssetCatalog.DetailLadderDepth(record.Width, record.Height);
            var level = Math.Min(depth, SampleLevel((record.Width, record.Height), width, height));
            var name = level == 0 ? sha : sha + "@" + level;
            image = CachedSample(name, width, height) ?? _cache.GetReadyForPixelSize(name, width, height);
        }
        return image;
    }

    private SKImage? ResidentFrameImage(string sha, double width = 0, double height = 0, bool working = false)
    {
        var image = ExactResidentFrameImage(sha, width, height, working);
        var exact = image is not null;
        image ??= _cache.Get(sha, null);
        LastImageRequestPending = !exact && QueueFrameWork(new(working ? FrameQuery.Image : FrameQuery.Pixels, sha, width, height));
        if (LastImageRequestPending) Interlocked.Increment(ref _unresolvedRequestVersion);
        return image;
    }

    private bool QueueFrameWork(FrameKey key)
    {
        if (_frameFailedUntil.TryGetValue(key, out var retry) && _retryClock.GetUtcNow().ToUnixTimeMilliseconds() < retry) return false;
        if (IsCanvasThread && !_requestedFrameWork.Add(key)) return true;
        if (!_frameWork.Add(key)) return true;
        Interlocked.Increment(ref _frameWorkSchedules);
        var revision = SourceRevision;
        var task = Task.Run(() =>
        {
            SKImage? result = null;
            try
            {
                using var lease = Lease();
                lock (_gate)
                {
                    if (_disposed) return;
                    Enter();
                    ValidateRetained(key.Sha);
                }
                result = key.Query == FrameQuery.Image ? lease.TryGetImage(key.Sha)
                    : lease.TryGetImageForPixelSize(key.Sha, key.Width, key.Height);
            }
            finally
            {
                lock (_gate)
                {
                    _frameWork.Remove(key);
                    if (!_disposed && revision == SourceRevision)
                    {
                        // A tight cache can hold the original but not its mip pyramid. Preserve the same
                        // original fallback as the synchronous policy and retry later, rather than refining
                        // and queueing a worker on every frame forever.
                        if (result is null || ExactResidentFrameImage(key.Sha, key.Width, key.Height, key.Query == FrameQuery.Image) is null)
                        {
                            _frameFailedUntil[key] = _retryClock.GetUtcNow().ToUnixTimeMilliseconds() + 2100;
                            _ = Task.Delay(TimeSpan.FromMilliseconds(2100), _retryClock, _retryStop.Token).ContinueWith(completed =>
                            { if (completed.IsCompletedSuccessfully && !_disposed) _derivedReady?.Invoke(); }, TaskScheduler.Default);
                        }
                        else _frameFailedUntil.Remove(key);
                    }
                }
                if (!_disposed) _derivedReady?.Invoke();
            }
        });
        _frameTasks.Add(task);
        _ = task.ContinueWith(completed => { lock (_gate) _frameTasks.Remove(completed); }, TaskScheduler.Default);
        return true;
    }

    private readonly Dictionary<string, FileSystemWatcher> _sourceWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _watchedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime Write)?> _sourceStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedSourceWatchers = new(StringComparer.OrdinalIgnoreCase);
    private Task? _sourceValidation;

    // Registered only on a resolving worker (or a non-frame caller). Events perform no filesystem checks;
    // they invalidate resident samples and wake the canvas, including while the document is idle.
    private void WatchSource(string sha, string path)
    {
        if (!_watchedSources.TryGetValue(path, out var shas))
        {
            _watchedSources[path] = shas = new(StringComparer.OrdinalIgnoreCase);
            _sourceStamps[path] = ReadSourceStamp(path);
        }
        shas.Add(sha);
        var directory = Path.GetDirectoryName(path)!;
        if (_sourceWatchers.ContainsKey(directory) || _failedSourceWatchers.Contains(directory)) return;
        TouchFile("source/watch");
        var watcher = new FileSystemWatcher
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += (_, args) => SourceFileChanged(args.FullPath);
        watcher.Created += (_, args) => SourceFileChanged(args.FullPath);
        watcher.Deleted += (_, args) => SourceFileChanged(args.FullPath);
        watcher.Renamed += (_, args) => { SourceFileChanged(args.OldFullPath); SourceFileChanged(args.FullPath); };
        watcher.Error += (_, _) =>
        {
            lock (_gate)
            {
                if (_disposed) return;
                foreach (var sha256 in _watchedSources.Values.SelectMany(items => items).Distinct().ToArray()) InvalidateDerived(sha256);
                _sourceValidation ??= Task.Run(ValidateSourceFiles);
            }
            _derivedReady?.Invoke();
        };
        try
        {
            watcher.Path = directory;
            if (startWatcher is null) watcher.EnableRaisingEvents = true;
            else startWatcher(watcher);
            _sourceWatchers[directory] = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            watcher.Dispose();
            _failedSourceWatchers.Add(directory);
            _sourceValidation ??= Task.Run(ValidateSourceFiles);
        }
    }

    private void SourceFileChanged(string path)
    {
        lock (_gate)
        {
            if (_disposed || !_watchedSources.TryGetValue(path, out var shas)) return;
            foreach (var sha in shas) InvalidateDerived(sha);
        }
        _derivedReady?.Invoke();
    }

    private (long Length, DateTime Write)? ReadSourceStamp(string path)
    {
        TouchFile("source/stat");
        try { var file = new FileInfo(path); return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task ValidateSourceFiles()
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), _retryStop.Token).ConfigureAwait(false);
                KeyValuePair<string, (long Length, DateTime Write)?>[] files;
                lock (_gate) { if (_disposed) return; files = [.. _sourceStamps]; }
                foreach (var file in files)
                {
                    var stamp = ReadSourceStamp(file.Key);
                    if (stamp == file.Value) continue;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _sourceStamps[file.Key] = stamp;
                        foreach (var sha in _watchedSources[file.Key]) InvalidateDerived(sha);
                    }
                    _derivedReady?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) when (_retryStop.IsCancellationRequested) { }
    }
}
