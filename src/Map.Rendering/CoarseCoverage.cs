using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Land coverage for far views: one image per block of 16×16 tiles, every coarse pixel the mean
/// of 16×16 source pixels, kept as long as the block's tiles are the very same arrays. A land
/// edit replaces only the tiles it touched, so only their blocks are rebuilt. Recomputable
/// pixels: a bounded LRU shared by every renderer, images handed out under the lock.
/// </summary>
internal static class CoarseCoverage
{
    public const int Block = 16;
    public const int Reduction = 16;
    private const int Capacity = 256; // at most 64 MiB of RGBA overview pixels

    // An overview must not keep entire old documents alive: one block can refer to 16 MiB
    // of source coverage, while its derived image is only 256 KiB.
    private sealed record Entry(WeakReference<byte[]>?[] Tiles, SKImage Image);
    private static readonly object Gate = new();
    private static readonly Dictionary<(LayerId Layer, int X, int Y), Entry> Entries = [];
    private static readonly List<(LayerId Layer, int X, int Y)> Order = [];

    static CoarseCoverage() => Map.Core.Resources.WorkingMemoryBudget.ReleaseDerivedCaches += Clear;

    private static void Clear()
    {
        lock (Gate)
        {
            foreach (var entry in Entries.Values) entry.Image.Dispose();
            Entries.Clear(); Order.Clear();
        }
    }

    public static void Draw(SKCanvas canvas, LayerId layer, TileMap tiles, WorldBounds view, SKSamplingOptions sampling,
        SKPaint? paint, Func<byte[], TileImageKind, SKImage> buildImage)
    {
        var blockSize = (double)Block * TileMap.TileSize;
        var minX = (int)Math.Floor(view.Left / blockSize); var maxX = (int)Math.Floor(view.Right / blockSize);
        var minY = (int)Math.Floor(view.Top / blockSize); var maxY = (int)Math.Floor(view.Bottom / blockSize);
        for (var by = minY; by <= maxY; by++)
        for (var bx = minX; bx <= maxX; bx++)
        {
            var refs = new byte[]?[Block * Block];
            var any = false;
            for (var ty = 0; ty < Block; ty++)
            for (var tx = 0; tx < Block; tx++)
            {
                var tile = tiles.TryGet(new TileKey(bx * Block + tx, by * Block + ty));
                refs[ty * Block + tx] = tile;
                any |= tile is not null;
            }
            if (!any) continue;
            var rect = new SKRect((float)(bx * blockSize), (float)(by * blockSize), (float)((bx + 1) * blockSize), (float)((by + 1) * blockSize));
            lock (Gate)
            {
                var image = Get(layer, bx, by, refs, buildImage);
                canvas.DrawImage(image, rect, sampling, paint);
            }
        }
    }

    private static SKImage Get(LayerId layer, int bx, int by, byte[]?[] refs, Func<byte[], TileImageKind, SKImage> buildImage)
    {
        var key = (layer, bx, by);
        if (Entries.TryGetValue(key, out var entry) && Same(entry.Tiles, refs))
        {
            Order.Remove(key); Order.Add(key);
            return entry.Image;
        }
        var image = buildImage(Reduce(refs), TileImageKind.LandCoverage);
        if (Entries.Remove(key, out var stale)) { stale.Image.Dispose(); Order.Remove(key); }
        Entries[key] = new Entry(refs.Select(tile => tile is null ? null : new WeakReference<byte[]>(tile)).ToArray(), image); Order.Add(key);
        while (Order.Count > Capacity)
        {
            var oldest = Order[0]; Order.RemoveAt(0);
            if (Entries.Remove(oldest, out var evicted)) evicted.Image.Dispose();
        }
        return image;
    }

    private static bool Same(WeakReference<byte[]>?[] a, byte[]?[] b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] is null) { if (b[i] is not null) return false; }
            else if (!a[i]!.TryGetTarget(out var tile) || !ReferenceEquals(tile, b[i])) return false;
        }
        return true;
    }

    /// <summary>The block reduced to one tile-sized coverage: each coarse pixel is the mean of a 16×16 square of source pixels.</summary>
    private static byte[] Reduce(byte[]?[] refs)
    {
        const int size = TileMap.TileSize;
        const int cell = size / Reduction; // coarse pixels per tile side
        var sums = new int[size * size];
        for (var ty = 0; ty < Block; ty++)
        for (var tx = 0; tx < Block; tx++)
        {
            var tile = refs[ty * Block + tx];
            if (tile is null) continue;
            var baseX = tx * cell; var baseY = ty * cell;
            for (var y = 0; y < size; y++)
            {
                var row = (baseY + y / Reduction) * size + baseX;
                var source = y * size;
                for (var x = 0; x < size; x++) sums[row + x / Reduction] += tile[source + x];
            }
        }
        var result = new byte[size * size];
        for (var i = 0; i < result.Length; i++) result[i] = (byte)(sums[i] / (Reduction * Reduction));
        return result;
    }
}
