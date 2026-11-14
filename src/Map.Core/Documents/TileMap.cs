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
/// 131 072-unit canvas never allocates anything proportional to its area.
/// </summary>
public sealed class TileMap
{
    /// <summary>Fixed tile edge in canvas units/pixels, proven by the Stage 1 spike.</summary>
    public const int TileSize = 256;

    /// <summary>Tile groups with an immutable change token, for derived coverage blocks.</summary>
    public const int RevisionBlockSize = 16;

    private sealed class BlockState(int count)
    {
        public int Count { get; } = count;
    }
    private readonly ImmutableDictionary<TileKey, BlockState> _blocks;

    private readonly ImmutableDictionary<TileKey, byte[]> _tiles;

    public static TileMap Empty(int bytesPerPixel) => new(
        [], [], bytesPerPixel);

    private TileMap(ImmutableDictionary<TileKey, byte[]> tiles, ImmutableDictionary<TileKey, BlockState> blocks, int bytesPerPixel)
    {
        if (bytesPerPixel is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
        }

        _tiles = tiles;
        _blocks = blocks;
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

    /// <summary>
    /// Identity changes only when a tile in this group changes. Null proves the group empty.
    /// Untouched groups share their token through edits and undo, with no scan of source tiles.
    /// Tokens contain no pixel arrays or references to older snapshots.
    /// </summary>
    public object? BlockRevision(int blockX, int blockY) =>
        _blocks.TryGetValue(new TileKey(blockX, blockY), out var block) ? block : null;

    private static TileKey BlockKey(TileKey key) => new(
        (int)Math.Floor((double)key.X / RevisionBlockSize), (int)Math.Floor((double)key.Y / RevisionBlockSize));

    /// <summary>New snapshot with the tile set or removed (null). Untouched tiles are shared.</summary>
    public TileMap With(TileKey key, byte[]? pixels)
    {
        if (pixels is null)
        {
            if (!_tiles.ContainsKey(key)) return this;
            var blockKey = BlockKey(key);
            var count = _blocks[blockKey].Count - 1;
            var blocks = count == 0 ? _blocks.Remove(blockKey) : _blocks.SetItem(blockKey, new BlockState(count));
            return new TileMap(_tiles.Remove(key), blocks, BytesPerPixel);
        }

        if (pixels.Length != TileByteLength)
        {
            throw new ArgumentException($"Tile buffer must be {TileByteLength} bytes.", nameof(pixels));
        }

        if (_tiles.TryGetValue(key, out var current) && ReferenceEquals(current, pixels)) return this;
        var groupKey = BlockKey(key);
        var groupCount = (_blocks.TryGetValue(groupKey, out var group) ? group.Count : 0) + (current is null ? 1 : 0);
        return new TileMap(_tiles.SetItem(key, pixels), _blocks.SetItem(groupKey, new BlockState(groupCount)), BytesPerPixel);
    }

    /// <summary>Applies many tile changes at once - one snapshot for a whole gesture.</summary>
    public TileMap WithMany(IEnumerable<KeyValuePair<TileKey, byte[]?>> changes)
    {
        var builder = _tiles.ToBuilder();
        var blocks = _blocks.ToBuilder();
        foreach (var (key, pixels) in changes)
        {
            builder.TryGetValue(key, out var current);
            if (ReferenceEquals(current, pixels)) continue;
            var blockKey = BlockKey(key);
            if (pixels is null)
            {
                builder.Remove(key);
                var count = blocks[blockKey].Count - 1;
                if (count == 0) blocks.Remove(blockKey);
                else blocks[blockKey] = new BlockState(count);
            }
            else
            {
                if (pixels.Length != TileByteLength)
                {
                    throw new ArgumentException($"Tile buffer must be {TileByteLength} bytes.");
                }

                builder[key] = pixels;
                var count = (blocks.TryGetValue(blockKey, out var block) ? block.Count : 0) + (current is null ? 1 : 0);
                blocks[blockKey] = new BlockState(count);
            }
        }

        return new TileMap(builder.ToImmutable(), blocks.ToImmutable(), BytesPerPixel);
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
