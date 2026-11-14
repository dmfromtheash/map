using Map.Core.Documents;
using Map.Core.Resources;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Exact 16 by 16 means, retained by block and immutable source tiles.</summary>
internal static class CoarseCoverage
{
    public const int Block = TileMap.RevisionBlockSize;
    public const int Reduction = 16;
    // RGBA pixels and LRU bookkeeping; never source pixels.
    private const long EntryBytes = TileMap.TileSize * TileMap.TileSize * 4L + 512;
    private static CacheMemoryAccount Account = Register(64L * WorkingMemoryBudget.MiB);
    private static CacheMemoryAccount Register(long bytes) => WorkingMemoryBudget.Shared.RegisterCache(
        "land-coarse-coverage", CacheRebuildCost.Expensive, bytes);
    private readonly record struct Key(LayerId Layer, int X, int Y);
    private sealed record Entry(Key Key, WeakReference<object> Revision, SKImage Image);
    private static readonly object Gate = new();
    private static readonly Dictionary<Key, LinkedListNode<Entry>> Entries = [];
    private static readonly LinkedList<Entry> Order = new();
    private static long _workingBytes = 64L * WorkingMemoryBudget.MiB;
    internal static long Builds { get; private set; }
    internal static long Bytes { get { lock (Gate) return Entries.Count * EntryBytes; } }

    static CoarseCoverage() => WorkingMemoryBudget.ReleaseDerivedCaches += Clear;

    // Keep the largest visited working set, within the shared allowance. Panning after fit
    // must not discard the overview needed when returning to fit.
    internal static void Prepare(MapDocument document, WorldBounds view)
    {
        var size = (double)Block * TileMap.TileSize;
        var all = Math.Ceiling(document.Canvas.Width / size) * Math.Ceiling(document.Canvas.Height / size);
        var visible = (Math.Ceiling((view.Right - view.Left) / size) + 2)
            * (Math.Ceiling((view.Bottom - view.Top) / size) + 2);
        var layers = document.Layers.OfType<LandLayer>().Count(l => l.Visible && l.Opacity > 0);
        lock (Gate)
        {
            _workingBytes = Math.Max(_workingBytes, (long)Math.Min(1536L * WorkingMemoryBudget.MiB,
                Math.Min(all, visible * 2) * layers * EntryBytes));
            if (_workingBytes > Account.RequestedBytes)
            {
                Account.Dispose(); Account = Register(_workingBytes);
            }
            Trim();
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            foreach (var node in Entries.Values) node.Value.Image.Dispose();
            Entries.Clear(); Order.Clear(); Account.Report(0);
            _workingBytes = 64L * WorkingMemoryBudget.MiB;
            if (Account.RequestedBytes != _workingBytes)
            {
                Account.Dispose(); Account = Register(_workingBytes);
            }
        }
    }

    private static void Trim()
    {
        var target = Math.Min(_workingBytes, Math.Min(Account.LimitBytes, Account.TargetBytes));
        long bytes = 0; var count = 0;
        while (Entries.Count * EntryBytes > target && Order.First is { } oldest)
        {
            Order.RemoveFirst(); Entries.Remove(oldest.Value.Key); oldest.Value.Image.Dispose();
            bytes += EntryBytes; count++;
        }
        Account.RecordTrim(bytes, count); Account.Report(Entries.Count * EntryBytes);
    }

    public static void Draw(SKCanvas canvas, LayerId layer, TileMap tiles, WorldBounds view, SKSamplingOptions sampling,
        SKPaint? paint, Func<byte[], TileImageKind, SKImage> buildImage)
    {
        var blockSize = (double)Block * TileMap.TileSize;
        var minX = (int)Math.Floor(view.Left / blockSize); var maxX = (int)Math.Floor(view.Right / blockSize);
        var minY = (int)Math.Floor(view.Top / blockSize); var maxY = (int)Math.Floor(view.Bottom / blockSize);
        lock (Gate)
        {
            Trim();
            for (var by = minY; by <= maxY; by++)
            for (var bx = minX; bx <= maxX; bx++)
            {
                var key = new Key(layer, bx, by);
                Entries.TryGetValue(key, out var node);
                var blockRevision = tiles.BlockRevision(bx, by);
                if (blockRevision is null)
                {
                    if (node is not null)
                    {
                        Order.Remove(node); Entries.Remove(key); node.Value.Image.Dispose();
                    }
                    continue;
                }
                // Tokens are shared by untouched blocks even in a newly edited snapshot.
                if (node is null || !node.Value.Revision.TryGetTarget(out var revision) || !ReferenceEquals(revision, blockRevision))
                {
                    var refs = new byte[]?[Block * Block];
                    for (var ty = 0; ty < Block; ty++)
                    for (var tx = 0; tx < Block; tx++)
                    {
                        var tile = tiles.TryGet(new TileKey(bx * Block + tx, by * Block + ty));
                        refs[ty * Block + tx] = tile;
                    }
                    if (node is not null)
                    {
                        Order.Remove(node); Entries.Remove(key); node.Value.Image.Dispose();
                    }
                    var image = buildImage(Reduce(refs), TileImageKind.LandCoverage);
                    Builds++;
                    node = Order.AddLast(new Entry(key, new(blockRevision), image));
                    Entries.Add(key, node);
                }
                Order.Remove(node!); Order.AddLast(node!);
                var rect = new SKRect((float)(bx * blockSize), (float)(by * blockSize), (float)((bx + 1) * blockSize), (float)((by + 1) * blockSize));
                canvas.DrawImage(node!.Value.Image, rect, sampling, paint);
                // Draw under the lock: release/eviction cannot dispose a borrowed image.
                Trim();
            }
            Trim();
        }
    }

    /// <summary>Each coarse pixel is the exact integer mean of a 16 by 16 source square.</summary>
    private static byte[] Reduce(byte[]?[] refs)
    {
        const int size = TileMap.TileSize;
        const int cell = size / Reduction;
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
