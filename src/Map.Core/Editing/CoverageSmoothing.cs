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

            byte[]? tile = null;
            for (var i = 0; i < blurred.Length; i++)
            {
                if (blurred[i] < 128)
                {
                    continue;
                }

                tile ??= new byte[Size * Size];
                tile[i] = 255;
            }

            if (tile is not null)
            {
                result[key] = tile;
            }
        }

        return result;
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
                        tile ??= new byte[Size * Size];
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
