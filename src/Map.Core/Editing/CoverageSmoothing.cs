using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Rounds the corners of a sparse coverage: the coverage is blurred by a box of the given radius
/// twice (a triangular kernel) and cut back to a crisp edge at half coverage. A convex corner
/// loses its tip and a concave one is filled, each with a rounding of about the radius, so the
/// jagged cells of a rough land stroke become the soft-cornered shapes a pen draws around them.
/// Only touched tiles and their neighbours are ever visited; the result holds no empty tile.
/// </summary>
public static class CoverageSmoothing
{
    private const int Size = TileMap.TileSize;

    /// <summary>The smoothed coverage; the input itself when the radius rounds nothing.</summary>
    public static IReadOnlyDictionary<TileKey, byte[]> Round(IReadOnlyDictionary<TileKey, byte[]> coverage, int radius)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (radius <= 0 || coverage.Count == 0)
        {
            return coverage;
        }

        radius = Math.Min(radius, Size - 1);
        var keys = new HashSet<TileKey>();
        foreach (var key in coverage.Keys)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    keys.Add(new TileKey(key.X + dx, key.Y + dy));
                }
            }
        }

        IReadOnlyDictionary<TileKey, byte[]> current = coverage;
        for (var pass = 0; pass < 2; pass++)
        {
            current = Blur(current, keys, radius, horizontal: true);
            current = Blur(current, keys, radius, horizontal: false);
        }

        var result = new Dictionary<TileKey, byte[]>();
        foreach (var key in keys)
        {
            if (!current.TryGetValue(key, out var blurred))
            {
                continue;
            }

            if (Threshold(blurred) is { } tile)
            {
                result[key] = tile;
            }
        }

        Recycle(current, coverage);
        return result;
    }

    /// <summary>The scratch tiles of the blur passes; a 64 KiB tile per touched key per pass would otherwise be garbage after every dab.</summary>
    private static readonly System.Buffers.ArrayPool<byte> Scratch = System.Buffers.ArrayPool<byte>.Shared;

    private static void Recycle(IReadOnlyDictionary<TileKey, byte[]> pass, IReadOnlyDictionary<TileKey, byte[]> source)
    {
        if (ReferenceEquals(pass, source))
        {
            return;
        }

        foreach (var tile in pass.Values)
        {
            Scratch.Return(tile);
        }
    }

    /// <summary>
    /// The rounded tiles that can differ from an earlier <see cref="Round"/> of the same stroke
    /// after the dabs since then changed <paramref name="dirty"/> tiles. The result holds every
    /// tile whose rounding may have changed: its new bytes, or null when it rounds to nothing;
    /// tiles not in it are untouched. Bit for bit what a full rounding of <paramref name="coverage"/>
    /// gives for those tiles: each of the four blur passes is run over exactly the tiles the next
    /// pass reads, shrinking by one ring per pass towards the tiles within the blur's total reach
    /// (four radii) of the dirty ones.
    /// </summary>
    public static Dictionary<TileKey, byte[]?> RoundIncremental(
        IReadOnlyDictionary<TileKey, byte[]> coverage, int radius, IReadOnlyCollection<TileKey> dirty,
        IReadOnlyDictionary<TileKey, byte[]>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(dirty);
        var result = new Dictionary<TileKey, byte[]?>();
        if (radius <= 0 || dirty.Count == 0)
        {
            return result;
        }

        radius = Math.Min(radius, Size - 1);
        var keys = new HashSet<TileKey>();
        foreach (var key in coverage.Keys)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    keys.Add(new TileKey(key.X + dx, key.Y + dy));
                }
            }
        }

        // Output changes within four blur radii of a changed pixel; each pass reads one tile
        // beyond the tiles it produces.
        var rings = ((4 * radius) + Size - 1) / Size;
        HashSet<TileKey> Around(int reach)
        {
            var set = new HashSet<TileKey>();
            foreach (var tile in dirty)
            {
                for (var dy = -reach; dy <= reach; dy++)
                {
                    for (var dx = -reach; dx <= reach; dx++)
                    {
                        var key = new TileKey(tile.X + dx, tile.Y + dy);
                        if (keys.Contains(key))
                        {
                            set.Add(key);
                        }
                    }
                }
            }

            return set;
        }

        var final = Around(rings);
        IReadOnlyDictionary<TileKey, byte[]> current = coverage;
        var first = Blur(current, Around(rings + 3), radius, horizontal: true);
        var second = Blur(first, Around(rings + 2), radius, horizontal: false);
        Recycle(first, coverage);
        var third = Blur(second, Around(rings + 1), radius, horizontal: true);
        Recycle(second, coverage);
        current = Blur(third, final, radius, horizontal: false);
        Recycle(third, coverage);
        var scratch = Scratch.Rent(Size * Size);
        foreach (var key in final)
        {
            if (!current.TryGetValue(key, out var blurred))
            {
                result[key] = null;
                continue;
            }

            // A tile whose rounding did not change keeps the very array the caller holds.
            var kept = previous is not null && previous.TryGetValue(key, out var before) ? before : null;
            if (kept is not null)
            {
                var any = false;
                for (var i = 0; i < Size * Size; i++)
                {
                    var value = blurred[i] < 128 ? (byte)0 : (byte)255;
                    scratch[i] = value;
                    any |= value != 0;
                }

                result[key] = !any ? null : scratch.AsSpan(0, Size * Size).SequenceEqual(kept) ? kept : scratch.AsSpan(0, Size * Size).ToArray();
                continue;
            }

            result[key] = Threshold(blurred);
        }

        Scratch.Return(scratch);
        Recycle(current, coverage);
        return result;
    }

    /// <summary>The crisp tile of a blurred one: 255 at and above half, else 0; null when empty.</summary>
    private static byte[]? Threshold(byte[] blurred)
    {
        byte[]? tile = null;
        for (var i = 0; i < Size * Size; i++)
        {
            if (blurred[i] < 128)
            {
                continue;
            }

            tile ??= new byte[Size * Size];
            tile[i] = 255;
        }

        return tile;
    }

    /// <summary>One box blur along one axis over the key set, reading across tile edges.</summary>
    private static Dictionary<TileKey, byte[]> Blur(
        IReadOnlyDictionary<TileKey, byte[]> source, HashSet<TileKey> keys, int radius, bool horizontal)
    {
        var window = (2 * radius) + 1;
        var output = new Dictionary<TileKey, byte[]>();
        var line = new int[Size + (2 * radius)];
        foreach (var key in keys)
        {
            var before = source.GetValueOrDefault(horizontal ? new TileKey(key.X - 1, key.Y) : new TileKey(key.X, key.Y - 1));
            var self = source.GetValueOrDefault(key);
            var after = source.GetValueOrDefault(horizontal ? new TileKey(key.X + 1, key.Y) : new TileKey(key.X, key.Y + 1));
            if (before is null && self is null && after is null)
            {
                continue;
            }

            byte[]? tile = null;
            for (var row = 0; row < Size; row++)
            {
                // The row (or column) with `radius` samples borrowed from each neighbour tile.
                for (var i = 0; i < line.Length; i++)
                {
                    var position = i - radius;
                    line[i] = position < 0 ? Sample(before, row, position + Size, horizontal)
                        : position >= Size ? Sample(after, row, position - Size, horizontal)
                        : Sample(self, row, position, horizontal);
                }

                var sum = 0;
                for (var i = 0; i < window - 1; i++)
                {
                    sum += line[i];
                }

                for (var x = 0; x < Size; x++)
                {
                    sum += line[x + window - 1];
                    if (sum != 0)
                    {
                        if (tile is null)
                        {
                            tile = Scratch.Rent(Size * Size);
                            Array.Clear(tile, 0, Size * Size);
                        }

                        tile[horizontal ? (row * Size) + x : (x * Size) + row] = (byte)((sum + (window / 2)) / window);
                    }

                    sum -= line[x];
                }
            }

            if (tile is not null)
            {
                output[key] = tile;
            }
        }

        return output;
    }

    private static int Sample(byte[]? tile, int row, int position, bool horizontal) =>
        tile is null ? 0 : tile[horizontal ? (row * Size) + position : (position * Size) + row];
}
