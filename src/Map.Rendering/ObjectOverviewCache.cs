using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public readonly record struct ObjectOverviewCacheResult(
    SKImage Image,
    bool Hit,
    bool InvalidatedPrevious,
    int ObjectCount = 0,
    int CulledCount = 0);

/// <summary>
/// Render-context-local LRU of small rasterized overview chunks. Content hashes make unchanged
/// chunks survive immutable document revisions; requesting a changed hash disposes only that
/// logical chunk's stale image. Exact/editor export never receives this cache.
/// </summary>
public sealed class ObjectOverviewCache : IDisposable
{
    private readonly record struct LogicalKey(
        LayerId Layer,
        int ChunkX,
        int ChunkY,
        int ZoomBucket);

    private readonly record struct Key(LogicalKey Logical, ulong ContentHash);

    private sealed class Entry
    {
        public required SKImage Image { get; init; }
        public required long Bytes { get; init; }
        public long LastUsed { get; set; }
        public long LastFrame { get; set; }
        public long LastTick { get; set; }
        public int ObjectCount { get; init; }
        public int CulledCount { get; init; }
        public bool Fallback { get; set; }
        public SceneRenderer.OverviewVariant? Overview { get; set; }
    }

    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Dictionary<LogicalKey, Key> _latest = [];
    private readonly Dictionary<(LayerId Layer, int X, int Y), HashSet<int>> _overviewZooms = [];
    private readonly int _itemBudget;
    private readonly long _byteBudget;
    private long _clock;
    private int? _ownerThread;
    private bool _frameOpen;
    private readonly HashSet<Key> _framePins = [];
    /// <summary>Older-zoom chunks drawn in the last frame in place of a current chunk that was not ready.</summary>
    private readonly HashSet<Key> _standIns = [];
    private readonly List<SKImage> _retired = [];
    private sealed record DetailView(int Zoom, int Left, int Top, int Right, int Bottom, long Frame);
    private readonly Dictionary<LayerId, DetailView> _refiningViews = [];
    private readonly Map.Core.Resources.WorkingMemoryBudget _memory;
    private long _frame;
    private bool _disposeRequested;
    private IntPtr _gpuContext;
    private readonly double _overviewBuildBudgetMilliseconds;
    private double _overviewBuildMilliseconds;
    private int _overviewBuilds;

    private readonly Map.Core.Resources.CacheMemoryAccount? _account;
    private long _pressureRetryFrame;

    /// <summary>Under memory pressure a tile drawn in this many recent frames is never trimmed.</summary>
    public int PressureProtectedFrames { get; init; } = 120;
    /// <summary>... nor one drawn within this time, so a fast frame rate does not shorten the protection.</summary>
    public long PressureProtectedMilliseconds { get; init; } = 2000;
    public long PressureTrims { get; private set; }

    private void Touch(Entry entry)
    {
        entry.LastUsed = ++_clock;
        entry.LastFrame = _frame;
        entry.LastTick = Environment.TickCount64;
    }

    /// <summary>
    /// Owner thread, between frames: while Windows is short of memory, falls to the account's watermark by releasing
    /// tiles nobody drew recently, the oldest first and the page fallbacks last (a whole-page rebuild is the dearest).
    /// The visible set and the refining view stay; at most 2 ms per frame. With nothing left to free, the next scan
    /// waits until the oldest protected tile can leave (by frames), or 15 frames while only the time guard holds.
    /// </summary>
    private void TrimForMemoryPressure()
    {
        if (_account is null || _frame < _pressureRetryFrame) return;
        var target = _account.TargetBytes;
        if (Bytes <= target) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = Environment.TickCount64;
        var candidates = new List<KeyValuePair<Key, Entry>>();
        var retry = long.MaxValue;
        foreach (var pair in _entries)
        {
            if (Refining(pair.Key)) continue;
            if (_frame - pair.Value.LastFrame < PressureProtectedFrames)
                retry = Math.Min(retry, pair.Value.LastFrame + PressureProtectedFrames);
            else if (now - pair.Value.LastTick < PressureProtectedMilliseconds) retry = Math.Min(retry, _frame + 15);
            else candidates.Add(pair);
        }
        candidates.Sort(static (a, b) => a.Value.Fallback != b.Value.Fallback
            ? a.Value.Fallback.CompareTo(b.Value.Fallback) : a.Value.LastUsed.CompareTo(b.Value.LastUsed));
        var before = Bytes;
        var items = 0;
        var exhausted = true;
        foreach (var pair in candidates)
        {
            if (Bytes <= target) break;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 2) { exhausted = false; break; }
            if (Remove(pair.Key, eviction: true)) items++;
        }
        if (exhausted && Bytes > target) _pressureRetryFrame = retry == long.MaxValue ? _frame + 15 : retry;
        if (items > 0) { PressureTrims++; _account.RecordTrim(before - Bytes, items); }
    }

    internal void BeginOverviewPass()
    {
        if (!_frameOpen) { _overviewBuildMilliseconds = 0; _overviewBuilds = 0; _adoptedPixels = 0; _adoptions = 0; _adoptionMoving = false; }
    }

    // One indivisible chunk is allowed to make progress even if it alone exceeds the budget.
    internal bool CanBuildOverview => _overviewBuilds == 0 || _overviewBuildMilliseconds < _overviewBuildBudgetMilliseconds;
    // Taking a ready background raster costs microseconds here, but its first draw uploads it to
    // the GPU. Bound the new pixels per frame too (at least one picture always goes through).
    internal const long AdoptedPixelsPerFrame = 8L * 1024 * 1024;

    /// <summary>
    /// New raster pixels a frame of a moving camera may take (about four full 512-pixel chunks):
    /// on an integrated GPU a frame that uploads megapixels mid-gesture is a visible stall. The
    /// rest keep their previous or neighbouring-zoom picture and follow, centre first, in later
    /// frames; the settled frame takes up to <see cref="AdoptedPixelsPerFrame"/>. Zero or less
    /// restores the settled budget for gestures (the previous behaviour, for comparisons).
    /// </summary>
    public long GestureAdoptedPixelsPerFrame { get; set; } = 1L * 1024 * 1024;

    /// <summary>Ready rasters a moving frame may take at all, however small (each is its own upload).</summary>
    public int GestureAdoptionsPerFrame { get; set; } = 8;

    private long _adoptedPixels;
    private int _adoptions;
    private bool _adoptionMoving;

    /// <summary>The pixel budget of the current frame, for its counters.</summary>
    internal long AdoptionBudgetPixels => _adoptionMoving && GestureAdoptedPixelsPerFrame > 0
        ? GestureAdoptedPixelsPerFrame : AdoptedPixelsPerFrame;

    internal bool CanAcceptPreparedOverview(bool cameraMoving)
    {
        _adoptionMoving |= cameraMoving;
        if (_overviewBuildMilliseconds >= _overviewBuildBudgetMilliseconds) return false;
        if (_adoptions == 0) return true; // one indivisible picture always goes through
        if (_adoptionMoving && GestureAdoptedPixelsPerFrame > 0 && _adoptions >= GestureAdoptionsPerFrame) return false;
        return _adoptedPixels < AdoptionBudgetPixels;
    }

    internal void RecordAdoption(long pixels) { _adoptedPixels += pixels; _adoptions++; }

    /// <summary>Pixels and pictures adopted so far in the open frame (all overview layers together).</summary>
    internal (long Pixels, int Count) FrameAdoptions => (_adoptedPixels, _adoptions);

    internal double RecordOverviewBuild(long started)
    {
        _overviewBuilds++;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _overviewBuildMilliseconds += elapsed;
        return elapsed;
    }

    internal ObjectOverviewCacheResult GetOrCreateOverview(LayerId layer, int x, int y, int zoom, ulong hash,
        SceneRenderer.OverviewVariant variant, StampForegroundSurface? foreground, RenderOptions options, SKImage? prepared = null)
    {
        var adopted = false;
        ObjectOverviewCacheResult result;
        try
        {
            result = GetOrCreate(layer, x, y, zoom, hash, () =>
            {
                if (prepared is null) return variant.BuildImage(foreground, options);
                adopted = true;
                return prepared;
            });
        }
        finally { if (!adopted) prepared?.Dispose(); }
        if (_entries.TryGetValue(new(new(layer, x, y, zoom), hash), out var entry))
        {
            entry.Overview = variant;
            if (!_overviewZooms.TryGetValue((layer, x, y), out var levels)) _overviewZooms[(layer, x, y)] = levels = [];
            levels.Add(zoom);
        }
        return result;
    }

    internal bool TryGetPreviousOverview(LayerId layer, int x, int y, int zoom,
        out SceneRenderer.OverviewVariant variant, out ObjectOverviewCacheResult result)
    {
        EnsureOwnerThread();
        Key? retained = _latest.TryGetValue(new(layer, x, y, zoom), out var exact)
            && _entries[exact].Overview is not null ? exact : null;
        if (retained is null && _overviewZooms.TryGetValue((layer, x, y), out var levels))
        {
            // A zoom step must not erase a resident chunk while its new bucket waits.
            var distance = long.MaxValue;
            foreach (var level in levels)
            {
                var delta = Math.Abs((long)level - zoom);
                if (delta < distance) { retained = _latest[new(layer, x, y, level)]; distance = delta; }
            }
        }
        if (retained is { } key && _entries.TryGetValue(key, out var entry) && entry.Overview is { } previous)
        {
            Touch(entry);
            if (_frameOpen) _framePins.Add(key);
            if (key.Logical.ZoomBucket != zoom) _standIns.Add(key);
            variant = previous;
            result = new(entry.Image, true, false);
            return true;
        }
        variant = null!;
        result = default;
        return false;
    }

    /// <summary>Texture-backed entries belong to one Skia context, even on the same render thread.</summary>
    public void BindGpuContext(IntPtr context)
    {
        EnsureOwnerThread();
        if (_gpuContext == context) return;
        foreach (var key in _entries.Keys.ToArray()) Remove(key, eviction: false);
        _refiningViews.Clear();
        _gpuContext = context;
    }

    public void BeginFrame()
    {
        EnsureOwnerThread();
        if (_frameOpen) throw new InvalidOperationException("An overview frame is already open.");
        _frameOpen = true;
        _frame++;
        _overviewBuildMilliseconds = 0;
        _overviewBuilds = 0;
        _adoptedPixels = 0;
        _adoptions = 0;
        _adoptionMoving = false;
        _framePins.Clear();
        _standIns.Clear();
        // A neighbouring level can serve this frame and be evicted by the new detail it
        // builds. Keep the page fallback available too, even before that frame needs it.
        foreach (var pair in _entries)
            if (pair.Value.Fallback) _framePins.Add(pair.Key);
    }

    public void EndFrame()
    {
        _frameOpen = false;
        _framePins.Clear();
        foreach (var layer in _refiningViews.Where(pair => pair.Value.Frame != _frame).Select(pair => pair.Key).ToArray())
            _refiningViews.Remove(layer);
        if (_disposeRequested) { Dispose(); return; }
        Trim(null);
        foreach (var image in _retired) { image.Dispose(); Disposals++; }
        _retired.Clear();
        TrimForMemoryPressure();
        _account?.Report(Bytes);
    }

    public ObjectOverviewCache(int itemBudget = 1_024, long byteBudget = 64L * 1024 * 1024,
        Map.Core.Resources.WorkingMemoryBudget? memoryBudget = null, double overviewBuildBudgetMilliseconds = 4,
        string? memoryAccount = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        if (!double.IsFinite(overviewBuildBudgetMilliseconds) || overviewBuildBudgetMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(overviewBuildBudgetMilliseconds));
        _overviewBuildBudgetMilliseconds = overviewBuildBudgetMilliseconds;
        _itemBudget = itemBudget;
        _byteBudget = byteBudget;
        _memory = memoryBudget ?? Map.Core.Resources.WorkingMemoryBudget.Shared;
        if (memoryAccount is not null)
            _account = _memory.RegisterCache(memoryAccount, Map.Core.Resources.CacheRebuildCost.Expensive, byteBudget);
    }

    public int Count => _entries.Count;
    /// <summary>Entries kept as page/seed fallbacks and their bytes (diagnostics).</summary>
    internal (int Count, long Bytes) FallbackEntries =>
        (_entries.Values.Count(entry => entry.Fallback), _entries.Values.Where(entry => entry.Fallback).Sum(entry => entry.Bytes));
    /// <summary>Frames begun on this cache; background detail rasters nobody asked for in the last frame are cancelled.</summary>
    internal long FrameNumber => _frame;
    public long Bytes { get; private set; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Invalidations { get; private set; }
    public long Evictions { get; private set; }
    public long Disposals { get; private set; }

    private long CurrentByteBudget => _memory.IsUnderPressure
        ? Math.Min(ByteLimit, 16L * 1024 * 1024) : ByteLimit;

    /// <summary>The own limit, or the share of the working-memory budget when that is smaller.</summary>
    private long ByteLimit => _account is null ? _byteBudget : Math.Min(_byteBudget, _account.LimitBytes);

    /// <summary>
    /// Retains the current detail view between its bounded refinement frames, up to the normal
    /// cache limits. The smaller pressure working set resumes once it finishes or the view moves.
    /// Without this lease, a view larger than 16 MiB endlessly evicts its unfinished first tiles.
    /// </summary>
    public bool BeginDetailView(LayerId layer, int zoom, WorldBounds view, double worldSize, long tileBytes)
    {
        EnsureOwnerThread();
        var detail = new DetailView(zoom, (int)Math.Floor(view.Left / worldSize), (int)Math.Floor(view.Top / worldSize),
            (int)Math.Floor(view.Right / worldSize), (int)Math.Floor(view.Bottom / worldSize), _frame);
        var tiles = (long)(detail.Right - detail.Left + 1) * (detail.Bottom - detail.Top + 1);
        var fallbacks = _entries.Values.Where(entry => entry.Fallback).ToArray();
        var fits = tiles + fallbacks.Length <= _itemBudget
            && tiles * tileBytes + fallbacks.Sum(entry => entry.Bytes) <= ByteLimit;
        if (fits) _refiningViews[layer] = detail;
        else _refiningViews.Remove(layer);
        return fits;
    }

    /// <summary>Releases the detail lease as soon as its complete layer image can be retained.</summary>
    public void CompleteDetailView(LayerId layer)
    {
        EnsureOwnerThread();
        _refiningViews.Remove(layer);
    }

    private bool Refining(Key key) => _refiningViews.TryGetValue(key.Logical.Layer, out var view)
        && key.Logical.ZoomBucket == view.Zoom && key.Logical.ChunkX >= view.Left && key.Logical.ChunkX <= view.Right
        && key.Logical.ChunkY >= view.Top && key.Logical.ChunkY <= view.Bottom;

    public bool TryGet(LayerId layer, int x, int y, int zoom, ulong revision, out ObjectOverviewCacheResult result)
    {
        EnsureOwnerThread();
        var key = new Key(new(layer, x, y, zoom), revision);
        if (_entries.TryGetValue(key, out var entry))
        {
            Touch(entry);
            if (_frameOpen) _framePins.Add(key);
            result = new(entry.Image, true, false, entry.ObjectCount, entry.CulledCount);
            return true;
        }
        result = default;
        return false;
    }

    public ObjectOverviewCacheResult GetOrCreate(
        LayerId layer,
        int chunkX,
        int chunkY,
        int zoomBucket,
        ulong contentHash,
        Func<SKImage> build,
        Func<(int Objects, int Culled)>? objectCounts = null,
        bool fallback = false,
        Func<bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        EnsureOwnerThread();
        var logical = new LogicalKey(layer, chunkX, chunkY, zoomBucket);
        var key = new Key(logical, contentHash);
        if (_entries.TryGetValue(key, out var existing))
        {
            Hits++;
            Touch(existing);
            existing.Fallback |= fallback;
            if (_frameOpen) _framePins.Add(key);
            return new ObjectOverviewCacheResult(existing.Image, Hit: true, InvalidatedPrevious: false,
                existing.ObjectCount, existing.CulledCount);
        }

        // Build first: an append may copy the previous tile, and a failed build must leave it usable.
        Misses++;
        var image = build();
        if (keep?.Invoke() == false)
        {
            // Draw this provisional image once, then release it with the frame. It must not
            // become a completed tile, an append base, or a neighbouring zoom fallback.
            _retired.Add(image);
            var transientCounts = objectCounts?.Invoke() ?? default;
            return new(image, false, false, transientCounts.Objects, transientCounts.Culled);
        }
        var invalidated = false;
        if (_latest.TryGetValue(logical, out var staleKey) && staleKey != key)
        {
            invalidated = Remove(staleKey, eviction: false);
            if (invalidated)
            {
                Invalidations++;
            }
        }

        var bytes = Math.Max(1L, checked((long)image.Width * image.Height * 4));
        var counts = objectCounts?.Invoke() ?? default;
        var added = new Entry
        {
            Image = image,
            Bytes = bytes,
            ObjectCount = counts.Objects,
            CulledCount = counts.Culled,
            Fallback = fallback
        };
        Touch(added);
        _entries.Add(key, added);
        _latest[logical] = key;
        Bytes += bytes;
        if (_frameOpen) _framePins.Add(key);
        Trim(key);
        return new ObjectOverviewCacheResult(image, Hit: false, invalidated, counts.Objects, counts.Culled);
    }

    private void EnsureOwnerThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThread is null)
        {
            _ownerThread = current;
        }
        else if (_ownerThread != current)
        {
            throw new InvalidOperationException(
                "An overview cache cannot cross incompatible render threads/Skia contexts.");
        }
    }

    private void Trim(Key? protectedKey)
    {
        // Keep a small working set even below the cautious OS reserve. Dropping every
        // visible tile each frame makes the next frame rebuild it (and its atlas), causing
        // both allocation churn and severe camera stalls while memory stays scarce.
        var budget = CurrentByteBudget;
        while (_entries.Count > _itemBudget || Bytes > budget)
        {
            var retainView = Bytes <= ByteLimit && _entries.Count <= _itemBudget;
            var eligible = _entries.Where(pair => pair.Key != protectedKey && !_framePins.Contains(pair.Key)
                && !(retainView && (Refining(pair.Key) || _refiningViews.Count > 0 && pair.Value.Fallback))).ToArray();
            if (eligible.Length == 0)
            {
                break;
            }

            // Coarse page tiles cover cold areas while detail arrives. Evicting them before
            // ordinary detail forces another whole-page build in the next pan/zoom frame.
            // This is only an eviction preference: both limits still bound the entire cache.
            // But what the last frame drew comes first: its own chunks, then the older levels and
            // seeds that stood in for its gaps. Seeds of levels the view has left must never crowd
            // out the current view, or it evicts its own chunks every frame and never completes
            // (Chromium's tile manager frees lower-priority tiles for NOW tiles, never the reverse;
            // Leaflet keeps a parent tile only while a current tile under it is still loading).
            Remove(eligible.MinBy(pair => (EvictionRank(pair.Key, pair.Value), pair.Value.LastUsed)).Key, eviction: true);
        }
    }

    /// <summary>
    /// A page/seed fallback no frame has drawn or prepared for this many frames (about a second) belongs to
    /// a view the camera has left: it no longer outranks the current view's own pictures.
    /// </summary>
    internal const int StaleFallbackFrames = 60;

    /// <summary>
    /// Lower ranks go first. 0 - an ordinary entry the last frame did not use; 1 - a fallback
    /// the current frame did not use; 2 - an older overview level the last frame drew in place of a
    /// chunk still on its way; 3 - the other ordinary entries, the view's own tiles and chunks among them;
    /// 4 - a current fallback (it covers cold tiles while a view larger than the cache churns, and
    /// a refining view completes only with its seed kept). Without frames nothing is stale or a stand-in.
    /// </summary>
    private int EvictionRank(Key key, Entry entry)
    {
        if (entry.Fallback) return _frame == 0 || entry.LastFrame == _frame ? 4 : 1;
        if (_frame == 0 || entry.LastFrame != _frame) return 0;
        return _standIns.Contains(key) ? 2 : 3;
    }

    private bool Remove(Key key, bool eviction)
    {
        if (!_entries.Remove(key, out var entry))
        {
            return false;
        }

        if (_latest.TryGetValue(key.Logical, out var latest) && latest == key)
        {
            _latest.Remove(key.Logical);
            var location = (key.Logical.Layer, key.Logical.ChunkX, key.Logical.ChunkY);
            if (entry.Overview is not null && _overviewZooms.TryGetValue(location, out var levels))
            {
                levels.Remove(key.Logical.ZoomBucket);
                if (levels.Count == 0) _overviewZooms.Remove(location);
            }
        }
        Bytes -= entry.Bytes;
        if (_frameOpen) _retired.Add(entry.Image);
        else { entry.Image.Dispose(); Disposals++; }
        if (eviction)
        {
            Evictions++;
        }
        return true;
    }

    public void Dispose()
    {
        if (_frameOpen) { _disposeRequested = true; return; }
        _disposeRequested = false;
        foreach (var entry in _entries.Values)
        {
            entry.Image.Dispose();
            Disposals++;
        }

        _entries.Clear();
        foreach (var image in _retired) { image.Dispose(); Disposals++; }
        _retired.Clear();
        _latest.Clear();
        _overviewZooms.Clear();
        _refiningViews.Clear();
        Bytes = 0;
        _ownerThread = null;
        _account?.Dispose();
    }
}
