using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>What a cached tile image is used for; the same tile can back several of them.</summary>
public enum TileImageKind
{
    /// <summary>The layer's own RGBA pixels.</summary>
    Content = 0,

    /// <summary>White alpha image used to apply a mask with DstIn.</summary>
    MaskAlpha = 1,

    /// <summary>Red overlay showing the hidden part of a mask while it is being edited.</summary>
    MaskOverlay = 2,

    /// <summary>White-alpha source coverage tinted by the current non-destructive land style.</summary>
    LandCoverage = 3,
}

/// <summary>
/// GPU/CPU images for raster tiles, kept between frames. Tiles are immutable byte arrays shared
/// structurally between document snapshots, so a cache entry stays valid exactly as long as the
/// layer still points at the same array - that reference is the invalidation signal, which is why
/// an edit to one tile costs one re-upload and not a full re-upload of the visible map.
///
/// Bounded by entry count with plain LRU eviction; the counters feed the benchmark report.
/// </summary>
public sealed class TileImageCache : IDisposable
{
    private readonly record struct Key(LayerId Layer, TileKey Tile, TileImageKind Kind);

    private sealed class Entry
    {
        public required byte[] Source { get; init; }
        public required SKImage Image { get; init; }
        public long LastUsed { get; set; }
    }

    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly int _capacity;
    private long _clock;

    public TileImageCache(int capacity = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public int Count => _entries.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    public void ResetCounters()
    {
        Hits = 0;
        Misses = 0;
        Evictions = 0;
    }

    /// <summary>The image for this tile, decoded on first use and reused while the tile is unchanged.</summary>
    public SKImage Get(LayerId layer, TileKey tile, TileImageKind kind, byte[] source)
    {
        var key = new Key(layer, tile, kind);
        if (_entries.TryGetValue(key, out var entry))
        {
            if (ReferenceEquals(entry.Source, source))
            {
                Hits++;
                entry.LastUsed = ++_clock;
                return entry.Image;
            }

            entry.Image.Dispose();
            _entries.Remove(key);
        }

        Misses++;
        var image = Build(kind, source);
        _entries[key] = new Entry { Source = source, Image = image, LastUsed = ++_clock };
        Trim();
        return image;
    }

    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            var oldest = _entries.OrderBy(e => e.Value.LastUsed).First();
            oldest.Value.Image.Dispose();
            _entries.Remove(oldest.Key);
            Evictions++;
        }
    }

    private static SKImage Build(TileImageKind kind, byte[] source)
    {
        var info = new SKImageInfo(
            TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul);

        if (kind == TileImageKind.Content)
        {
            return SKImage.FromPixelCopy(info, source);
        }

        // Coverage tiles are single channel; expand them into a premultiplied RGBA image so the
        // blend below works the same on every backend (an A8 image would depend on paint colour).
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var i = 0; i < source.Length; i++)
        {
            var offset = i * 4;
            if (kind is TileImageKind.MaskAlpha or TileImageKind.LandCoverage)
            {
                var v = source[i];
                pixels[offset] = v;
                pixels[offset + 1] = v;
                pixels[offset + 2] = v;
                pixels[offset + 3] = v;
            }
            else
            {
                // Hidden area (mask 0) shows as red; visible area stays clear.
                var hidden = (byte)(255 - source[i]);
                pixels[offset] = hidden;
                pixels[offset + 3] = hidden;
            }
        }

        return SKImage.FromPixelCopy(info, pixels);
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Image.Dispose();
        }

        _entries.Clear();
    }
}
