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
        public required long Bytes { get; init; }
        public long LastUsed { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly HashSet<Key> _framePins = [];
    private readonly List<SKImage> _retired = [];
    private readonly int _capacity;
    private readonly long _byteCapacity;
    private long _clock;
    private bool _frameOpen;
    private bool _disposeRequested;

    public TileImageCache(int capacity = 512, long byteCapacity = 256L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCapacity);
        _capacity = capacity;
        _byteCapacity = byteCapacity;
    }

    public int Count => _entries.Count;
    public long Bytes { get; private set; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }
    public long Disposals { get; private set; }

    /// <summary>
    /// Pins every image returned during one leased Skia render. Eviction is postponed until the
    /// lease is released, so a large frame never disposes native images that the same frame used.
    /// </summary>
    public void BeginFrame()
    {
        lock (_gate)
        {
            if (_frameOpen)
            {
                throw new InvalidOperationException("A tile-cache frame is already open.");
            }

            _frameOpen = true;
            _framePins.Clear();
        }
    }

    /// <summary>Releases frame pins and performs deferred native disposal after the Skia lease.</summary>
    public void EndFrame()
    {
        lock (_gate)
        {
            if (!_frameOpen)
            {
                return;
            }

            _frameOpen = false;
            _framePins.Clear();
            if (_disposeRequested)
            {
                DisposeEntries();
                _disposeRequested = false;
            }
            else
            {
                Trim(protectedKey: null);
                DisposeRetired();
            }
        }
    }

    public void ResetCounters()
    {
        lock (_gate)
        {
            Hits = 0;
            Misses = 0;
            Evictions = 0;
        }
    }

    /// <summary>The image for this tile, decoded on first use and reused while the tile is unchanged.</summary>
    public SKImage Get(LayerId layer, TileKey tile, TileImageKind kind, byte[] source)
    {
        lock (_gate)
        {
            var key = new Key(layer, tile, kind);
            if (_entries.TryGetValue(key, out var entry))
            {
                if (ReferenceEquals(entry.Source, source))
                {
                    Hits++;
                    entry.LastUsed = ++_clock;
                    if (_frameOpen)
                    {
                        _framePins.Add(key);
                    }
                    return entry.Image;
                }

                Remove(key, entry, eviction: false);
            }

            Misses++;
            var image = Build(kind, source);
            var bytes = ImageBytes(kind);
            _entries[key] = new Entry
            {
                Source = source,
                Image = image,
                Bytes = bytes,
                LastUsed = ++_clock,
            };
            Bytes += bytes;
            if (_frameOpen)
            {
                _framePins.Add(key);
            }
            Trim(key);
            return image;
        }
    }

    private void Trim(Key? protectedKey)
    {
        while (_entries.Count > _capacity || Bytes > _byteCapacity)
        {
            Key oldestKey = default;
            Entry? oldestEntry = null;
            foreach (var pair in _entries)
            {
                if ((protectedKey is not null && pair.Key == protectedKey.Value)
                    || _framePins.Contains(pair.Key)
                    || oldestEntry is not null && pair.Value.LastUsed >= oldestEntry.LastUsed)
                {
                    continue;
                }

                oldestKey = pair.Key;
                oldestEntry = pair.Value;
            }
            if (oldestEntry is null)
            {
                // One oversized image, or a frame whose visible working set is larger than the
                // steady-state cache. It stays alive until the frame lease is released.
                break;
            }

            Remove(oldestKey, oldestEntry, eviction: true);
        }
    }

    private void Remove(Key key, Entry entry, bool eviction)
    {
        _entries.Remove(key);
        Bytes -= entry.Bytes;
        if (_frameOpen)
        {
            _retired.Add(entry.Image);
        }
        else
        {
            entry.Image.Dispose();
            Disposals++;
        }
        if (eviction)
        {
            Evictions++;
        }
    }

    private static long ImageBytes(TileImageKind kind) => kind is
        TileImageKind.MaskAlpha or TileImageKind.LandCoverage
            ? (long)TileMap.TileSize * TileMap.TileSize
            : (long)TileMap.TileSize * TileMap.TileSize * 4;

    private static SKImage Build(TileImageKind kind, byte[] source)
    {
        if (kind is TileImageKind.MaskAlpha or TileImageKind.LandCoverage)
        {
            var alphaInfo = new SKImageInfo(
                TileMap.TileSize, TileMap.TileSize, SKColorType.Alpha8, SKAlphaType.Premul);
            return SKImage.FromPixelCopy(alphaInfo, source);
        }

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
            // Hidden area (mask 0) shows as red; visible area stays clear.
            var hidden = (byte)(255 - source[i]);
            pixels[offset] = hidden;
            pixels[offset + 3] = hidden;
        }

        return SKImage.FromPixelCopy(info, pixels);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_frameOpen)
            {
                _disposeRequested = true;
                return;
            }

            DisposeEntries();
        }
    }

    private void DisposeEntries()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Image.Dispose();
            Disposals++;
        }
        _entries.Clear();
        Bytes = 0;
        DisposeRetired();
    }

    private void DisposeRetired()
    {
        foreach (var image in _retired)
        {
            image.Dispose();
            Disposals++;
        }
        _retired.Clear();
    }
}
