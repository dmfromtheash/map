using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Documents;

/// <summary>
/// Single-channel (8-bit coverage) mask bound to exactly one raster layer.
/// 255 = fully visible, 0 = fully hidden. Stored tiled with the same grid as its target.
/// </summary>
public sealed class MaskLayer(LayerId id, LayerId targetLayerId, string name, int tileSize = RasterLayer.DefaultTileSize)
{
    private readonly Dictionary<TileKey, byte[]> _tiles = [];
    private readonly Dictionary<TileKey, long> _tileRevisions = [];

    public LayerId Id { get; } = id;

    /// <summary>The one raster layer this mask may affect. Enforced by the renderer.</summary>
    public LayerId TargetLayerId { get; } = targetLayerId;

    public string Name { get; } = name;
    public int TileSize { get; } = tileSize;
    public bool Enabled { get; set; } = true;

    public int TileByteLength => TileSize * TileSize;
    public IReadOnlyCollection<TileKey> TileKeys => _tiles.Keys;
    public int TileCount => _tiles.Count;
    public long TotalTileBytes => (long)_tiles.Count * TileByteLength;

    public byte[]? TryGetTile(TileKey key) => _tiles.TryGetValue(key, out var t) ? t : null;

    public long RevisionOf(TileKey key) => _tileRevisions.TryGetValue(key, out var r) ? r : 0;

    public void BumpRevision(TileKey key) => _tileRevisions[key] = RevisionOf(key) + 1;

    /// <summary>New mask tiles start fully opaque (255) so an untouched area stays visible.</summary>
    public byte[] GetOrCreateTile(TileKey key)
    {
        if (_tiles.TryGetValue(key, out var existing)) return existing;
        var buffer = new byte[TileByteLength];
        buffer.AsSpan().Fill(255);
        _tiles[key] = buffer;
        BumpRevision(key);
        return buffer;
    }

    public void SetTile(TileKey key, byte[]? pixels)
    {
        if (pixels is null)
        {
            if (_tiles.Remove(key)) BumpRevision(key);
            return;
        }

        if (pixels.Length != TileByteLength)
            throw new ArgumentException($"Mask tile buffer must be {TileByteLength} bytes.", nameof(pixels));
        _tiles[key] = pixels;
        BumpRevision(key);
    }

    public Rect2 TileWorldBounds(TileKey key) =>
        new(key.X * (double)TileSize, key.Y * (double)TileSize, TileSize, TileSize);

    public TileKey TileAt(double worldX, double worldY) =>
        new((int)Math.Floor(worldX / TileSize), (int)Math.Floor(worldY / TileSize));
}
