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
    private bool _disposeRequested;
    private IntPtr _gpuContext;

    /// <summary>Texture-backed entries belong to one Skia context, even on the same render thread.</summary>
    public void BindGpuContext(IntPtr context)
    {
        EnsureOwnerThread();
        if (_gpuContext == context) return;
        foreach (var key in _entries.Keys.ToArray()) Remove(key, eviction: false);
        _gpuContext = context;
    }

    public void BeginFrame()
    {
        EnsureOwnerThread();
        if (_frameOpen) throw new InvalidOperationException("An overview frame is already open.");
        _frameOpen = true;
        _framePins.Clear();
    }

    public void EndFrame()
    {
        _frameOpen = false;
        _framePins.Clear();
        if (_disposeRequested) { Dispose(); return; }
        Trim(null);
        foreach (var image in _retired) { image.Dispose(); Disposals++; }
        _retired.Clear();
    }

    public ObjectOverviewCache(int itemBudget = 1_024, long byteBudget = 64L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        _itemBudget = itemBudget;
        _byteBudget = byteBudget;
    }

    public int Count => _entries.Count;
    public long Bytes { get; private set; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Invalidations { get; private set; }
    public long Evictions { get; private set; }
    public long Disposals { get; private set; }

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
        Func<(int Objects, int Culled)>? objectCounts = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        EnsureOwnerThread();
        var logical = new LogicalKey(layer, chunkX, chunkY, zoomBucket);
        var key = new Key(logical, contentHash);
        if (_entries.TryGetValue(key, out var existing))
        {
            Hits++;
            existing.LastUsed = ++_clock;
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
            ObjectCount = counts.Objects, CulledCount = counts.Culled });
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
        var budget = Map.Core.Resources.WorkingMemoryBudget.Shared.IsUnderPressure
            ? Math.Min(_byteBudget, 16L * 1024 * 1024) : _byteBudget;
        while (_entries.Count > _itemBudget || Bytes > budget)
        {
            var eligible = _entries.Where(pair => pair.Key != protectedKey && !_framePins.Contains(pair.Key)).ToArray();
            if (eligible.Length == 0)
            {
                break;
            }

            Remove(eligible.MinBy(pair => pair.Value.LastUsed).Key, eviction: true);
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
        Bytes = 0;
        _ownerThread = null;
    }
}
