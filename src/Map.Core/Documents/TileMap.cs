using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>Tile coordinate in tile units at the layer's base resolution.</summary>
public readonly record struct TileKey(int X, int Y)
{
    public override string ToString() => $"{X}_{Y}";

    public static TileKey Parse(string s)
    {
        var parts = s.Split('_');
        if (parts.Length != 2)
        {
            throw new FormatException($"Bad tile key '{s}'.");
        }

        return new TileKey(int.Parse(parts[0]), int.Parse(parts[1]));
    }
}

/// <summary>
/// Immutable tiled raster: the persistent-map backbone of every raster surface in the document
/// (DEC-009). Only materialized, non-empty tiles exist; a snapshot edit shares every untouched
/// tile with its predecessor by reference, so undo and history cost only the touched tiles and a
/// 65 536-unit canvas never allocates anything proportional to its area.
/// </summary>
public sealed class TileMap
{
    /// <summary>Fixed tile edge in canvas units/pixels, proven by the Stage 1 spike.</summary>
    public const int TileSize = 256;

    private readonly ImmutableDictionary<TileKey, byte[]> _tiles;

    public static TileMap Empty(int bytesPerPixel) => new(
        [], bytesPerPixel);

    private TileMap(ImmutableDictionary<TileKey, byte[]> tiles, int bytesPerPixel)
    {
        if (bytesPerPixel is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
        }

        _tiles = tiles;
        BytesPerPixel = bytesPerPixel;
    }

    /// <summary>4 for premultiplied RGBA content, 1 for coverage (masks, land).</summary>
    public int BytesPerPixel { get; }

    public int TileByteLength => TileSize * TileSize * BytesPerPixel;

    public int TileCount => _tiles.Count;

    public long TotalBytes => (long)_tiles.Count * TileByteLength;

    public IEnumerable<TileKey> Keys => _tiles.Keys;

    public byte[]? TryGet(TileKey key) => _tiles.TryGetValue(key, out var tile) ? tile : null;

    public bool Contains(TileKey key) => _tiles.ContainsKey(key);

    /// <summary>New snapshot with the tile set or removed (null). Untouched tiles are shared.</summary>
    public TileMap With(TileKey key, byte[]? pixels)
    {
        if (pixels is null)
        {
            return _tiles.ContainsKey(key) ? new TileMap(_tiles.Remove(key), BytesPerPixel) : this;
        }

        if (pixels.Length != TileByteLength)
        {
            throw new ArgumentException($"Tile buffer must be {TileByteLength} bytes.", nameof(pixels));
        }

        return new TileMap(_tiles.SetItem(key, pixels), BytesPerPixel);
    }

    /// <summary>Applies many tile changes at once - one snapshot for a whole gesture.</summary>
    public TileMap WithMany(IEnumerable<KeyValuePair<TileKey, byte[]?>> changes)
    {
        var builder = _tiles.ToBuilder();
        foreach (var (key, pixels) in changes)
        {
            if (pixels is null)
            {
                builder.Remove(key);
            }
            else
            {
                if (pixels.Length != TileByteLength)
                {
                    throw new ArgumentException($"Tile buffer must be {TileByteLength} bytes.");
                }

                builder[key] = pixels;
            }
        }

        return new TileMap(builder.ToImmutable(), BytesPerPixel);
    }

    public static TileKey KeyAt(double worldX, double worldY) => new(
        (int)Math.Floor(worldX / TileSize), (int)Math.Floor(worldY / TileSize));

    /// <summary>Keys of materialized tiles intersecting the world rectangle - never a full scan
    /// of the canvas area, only of existing tiles or the visible range, whichever is smaller.</summary>
    public IEnumerable<TileKey> KeysIntersecting(double left, double top, double right, double bottom)
    {
        var minX = (int)Math.Floor(left / TileSize);
        var maxX = (int)Math.Floor(right / TileSize);
        var minY = (int)Math.Floor(top / TileSize);
        var maxY = (int)Math.Floor(bottom / TileSize);

        long rangeCount = ((long)(maxX - minX) + 1) * ((long)(maxY - minY) + 1);
        if (rangeCount < _tiles.Count)
        {
            // Zoomed in: enumerate the visible key range.
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var key = new TileKey(x, y);
                    if (_tiles.ContainsKey(key))
                    {
                        yield return key;
                    }
                }
            }
        }
        else
        {
            // Zoomed out: enumerate existing tiles and filter.
            foreach (var key in _tiles.Keys)
            {
                if (key.X >= minX && key.X <= maxX && key.Y >= minY && key.Y <= maxY)
                {
                    yield return key;
                }
            }
        }
    }

    /// <summary>True when every byte of the tile is zero (fully transparent / no coverage).</summary>
    public static bool IsEmptyTile(byte[] pixels)
    {
        foreach (var b in pixels)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }
}
