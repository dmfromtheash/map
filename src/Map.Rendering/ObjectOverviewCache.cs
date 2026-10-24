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
        public int ObjectCount { get; init; }
        public int CulledCount { get; init; }
        public bool Fallback { get; set; }
    }

    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Dictionary<LogicalKey, Key> _latest = [];
    private readonly int _itemBudget;
    private readonly long _byteBudget;
    private long _clock;
    private int? _ownerThread;
    private bool _frameOpen;
    private readonly HashSet<Key> _framePins = [];
    private readonly List<SKImage> _retired = [];
    private sealed record DetailView(int Zoom, int Left, int Top, int Right, int Bottom, long Frame);
    private readonly Dictionary<LayerId, DetailView> _refiningViews = [];
    private readonly Map.Core.Resources.WorkingMemoryBudget _memory;
    private long _frame;
    private bool _disposeRequested;
    private IntPtr _gpuContext;

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
        _framePins.Clear();
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
    }

    public ObjectOverviewCache(int itemBudget = 1_024, long byteBudget = 64L * 1024 * 1024,
        Map.Core.Resources.WorkingMemoryBudget? memoryBudget = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _itemBudget = itemBudget;
        _byteBudget = byteBudget;
        _memory = memoryBudget ?? Map.Core.Resources.WorkingMemoryBudget.Shared;
    }

    public int Count => _entries.Count;
    public long Bytes { get; private set; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Invalidations { get; private set; }
    public long Evictions { get; private set; }
    public long Disposals { get; private set; }

    private long CurrentByteBudget => _memory.IsUnderPressure
        ? Math.Min(_byteBudget, 16L * 1024 * 1024) : _byteBudget;

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
            && tiles * tileBytes + fallbacks.Sum(entry => entry.Bytes) <= _byteBudget;
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
            entry.LastUsed = ++_clock;
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
        bool fallback = false)
    {
        ArgumentNullException.ThrowIfNull(build);
        EnsureOwnerThread();
        var logical = new LogicalKey(layer, chunkX, chunkY, zoomBucket);
        var key = new Key(logical, contentHash);
        if (_entries.TryGetValue(key, out var existing))
        {
            Hits++;
            existing.LastUsed = ++_clock;
            existing.Fallback |= fallback;
            if (_frameOpen) _framePins.Add(key);
            return new ObjectOverviewCacheResult(existing.Image, Hit: true, InvalidatedPrevious: false,
                existing.ObjectCount, existing.CulledCount);
        }

        // Build first: an append may copy the previous tile, and a failed build must leave it usable.
        Misses++;
        var image = build();
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
        _entries.Add(key, new Entry { Image = image, Bytes = bytes, LastUsed = ++_clock,
            ObjectCount = counts.Objects, CulledCount = counts.Culled, Fallback = fallback });
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
            var retainView = Bytes <= _byteBudget && _entries.Count <= _itemBudget;
            var eligible = _entries.Where(pair => pair.Key != protectedKey && !_framePins.Contains(pair.Key)
                && !(retainView && (Refining(pair.Key) || _refiningViews.Count > 0 && pair.Value.Fallback))).ToArray();
            if (eligible.Length == 0)
            {
                break;
            }

            // Coarse page tiles cover cold areas while detail arrives. Evicting them before
            // ordinary detail forces another whole-page build in the next pan/zoom frame.
            // This is only an eviction preference: both limits still bound the entire cache.
            Remove(eligible.MinBy(pair => (pair.Value.Fallback, pair.Value.LastUsed)).Key, eviction: true);
        }
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
        _refiningViews.Clear();
        Bytes = 0;
        _ownerThread = null;
    }
}
