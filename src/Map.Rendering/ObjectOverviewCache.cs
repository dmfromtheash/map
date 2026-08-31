using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public readonly record struct ObjectOverviewCacheResult(
    SKImage Image,
    bool Hit,
    bool InvalidatedPrevious);

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
    }

    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Dictionary<LogicalKey, Key> _latest = [];
    private readonly int _itemBudget;
    private readonly long _byteBudget;
    private long _clock;
    private int? _ownerThread;

    public ObjectOverviewCache(int itemBudget = 512, long byteBudget = 64L * 1024 * 1024)
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

    public ObjectOverviewCacheResult GetOrCreate(
        LayerId layer,
        int chunkX,
        int chunkY,
        int zoomBucket,
        ulong contentHash,
        Func<SKImage> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        EnsureOwnerThread();
        var logical = new LogicalKey(layer, chunkX, chunkY, zoomBucket);
        var key = new Key(logical, contentHash);
        if (_entries.TryGetValue(key, out var existing))
        {
            Hits++;
            existing.LastUsed = ++_clock;
            return new ObjectOverviewCacheResult(existing.Image, Hit: true, InvalidatedPrevious: false);
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

        Misses++;
        var image = build();
        var bytes = Math.Max(1L, checked((long)image.Width * image.Height * 4));
        _entries.Add(key, new Entry { Image = image, Bytes = bytes, LastUsed = ++_clock });
        _latest[logical] = key;
        Bytes += bytes;
        Trim();
        return new ObjectOverviewCacheResult(image, Hit: false, invalidated);
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

    private void Trim()
    {
        while (_entries.Count > _itemBudget || Bytes > _byteBudget)
        {
            var oldest = _entries.MinBy(pair => pair.Value.LastUsed);
            if (_entries.Count == 0)
            {
                break;
            }

            Remove(oldest.Key, eviction: true);
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
        entry.Image.Dispose();
        Disposals++;
        if (eviction)
        {
            Evictions++;
        }
        return true;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Image.Dispose();
            Disposals++;
        }

        _entries.Clear();
        _latest.Clear();
        Bytes = 0;
        _ownerThread = null;
    }
}
