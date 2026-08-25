using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Documents;

/// <summary>
/// Tile-backed 8-bit-per-channel RGBA raster layer.
/// Pixels are stored PREMULTIPLIED so source-over compositing is a plain
/// dst = src + dst*(1-src.a) and no conversion is needed on the render path.
/// </summary>
public sealed class RasterLayer
{
    public const int DefaultTileSize = 256;
    public const int BytesPerPixel = 4;

    private readonly Dictionary<TileKey, byte[]> _tiles = [];
    private readonly Dictionary<TileKey, long> _tileRevisions = [];

    public RasterLayer(LayerId id, string name, int tileSize = DefaultTileSize)
    {
        if (tileSize <= 0 || (tileSize & (tileSize - 1)) != 0)
            throw new ArgumentException("Tile size must be a positive power of two.", nameof(tileSize));

        Id = id;
        Name = name;
        TileSize = tileSize;
    }

    public LayerId Id { get; }
    public string Name { get; }
    public int TileSize { get; }
    public bool Visible { get; set; } = true;

    public int TileByteLength => TileSize * TileSize * BytesPerPixel;

    /// <summary>Materialized tiles only. Fully transparent tiles are never allocated or saved.</summary>
    public IReadOnlyCollection<TileKey> TileKeys => _tiles.Keys;

    public int TileCount => _tiles.Count;

    public long TotalTileBytes => (long)_tiles.Count * TileByteLength;

    public long RevisionOf(TileKey key) => _tileRevisions.TryGetValue(key, out var r) ? r : 0;

    public byte[]? TryGetTile(TileKey key) => _tiles.TryGetValue(key, out var t) ? t : null;

    /// <summary>Allocates the tile if it does not exist yet. Returns the mutable buffer.</summary>
    public byte[] GetOrCreateTile(TileKey key)
    {
        if (_tiles.TryGetValue(key, out var existing)) return existing;
        var buffer = new byte[TileByteLength];
        _tiles[key] = buffer;
        _tileRevisions[key] = 1;
        return buffer;
    }

    /// <summary>Replaces tile content. A null buffer removes the tile (used by undo of a first-touch stroke).</summary>
    public void SetTile(TileKey key, byte[]? pixels)
    {
        if (pixels is null)
        {
            if (_tiles.Remove(key)) _tileRevisions[key] = RevisionOf(key) + 1;
            return;
        }

        if (pixels.Length != TileByteLength)
            throw new ArgumentException($"Tile buffer must be {TileByteLength} bytes.", nameof(pixels));

        _tiles[key] = pixels;
        _tileRevisions[key] = RevisionOf(key) + 1;
    }

    public void BumpRevision(TileKey key) => _tileRevisions[key] = RevisionOf(key) + 1;

    public Rect2 TileWorldBounds(TileKey key) =>
        new(key.X * (double)TileSize, key.Y * (double)TileSize, TileSize, TileSize);

    public TileKey TileAt(double worldX, double worldY) =>
        new((int)Math.Floor(worldX / TileSize), (int)Math.Floor(worldY / TileSize));
}
