using System.Globalization;
using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>The hexes the hex tool would touch, outlined under the pointer.</summary>
/// <param name="LayerId">The hex layer the tool works on.</param>
/// <param name="Hexes">The footprint.</param>
/// <param name="Erase">Outlined as an eraser instead of a placement.</param>
public sealed record HexCursorPreview(LayerId LayerId, IReadOnlyList<HexCoord> Hexes, bool Erase = false);

/// <summary>
/// Kept pictures of hex chunks. A chunk is drawn once into an image at a power-of-two scale and
/// redrawn only when that chunk itself changes: chunks are immutable and shared between snapshots,
/// so the chunk object is the key, and an edit invalidates exactly the chunks it copied. The far
/// look (flat colours) and the painted look are kept separately.
/// </summary>
public sealed class HexChunkCache : IDisposable
{
    /// <summary>Video memory the kept chunk images may hold.</summary>
    public const long BudgetBytes = 192L * 1024 * 1024;

    /// <summary>A chunk image larger than this on a side is not kept; the hexes are drawn directly.</summary>
    public const int MaxImageSide = 2048;

    private readonly Dictionary<(HexChunk Chunk, HexOrientation Orientation, int Level, bool Flat), Entry> _entries = new(new KeyComparer());
    private long _bytes;
    private long _clock;

    private sealed class Entry(SKImage image, long bytes)
    {
        public SKImage Image { get; } = image;
        public long Bytes { get; } = bytes;
        public long LastUse { get; set; }
    }

    private sealed class KeyComparer : IEqualityComparer<(HexChunk Chunk, HexOrientation Orientation, int Level, bool Flat)>
    {
        public bool Equals((HexChunk Chunk, HexOrientation Orientation, int Level, bool Flat) x, (HexChunk Chunk, HexOrientation Orientation, int Level, bool Flat) y) =>
            ReferenceEquals(x.Chunk, y.Chunk) && x.Orientation == y.Orientation && x.Level == y.Level && x.Flat == y.Flat;

        public int GetHashCode((HexChunk Chunk, HexOrientation Orientation, int Level, bool Flat) key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Chunk), key.Orientation, key.Level, key.Flat);
    }

    private (object Key, SKImage Image)? _gridTile;

    /// <summary>Far look of many chunks in one picture: a whole board far away is a few dozen images, not thousands.</summary>
    internal sealed class FlatBlock(HexChunk?[] chunks, HexLayout layout, WorldBounds bounds, SKImage image)
    {
        public HexChunk?[] Chunks { get; } = chunks;
        public HexLayout Layout { get; } = layout;
        public WorldBounds Bounds { get; } = bounds;
        public SKImage Image { get; } = image;
        public long LastUse { get; set; }
    }

    /// <summary>Memory the far-look blocks may hold.</summary>
    public const long BlockBudgetBytes = 64L * 1024 * 1024;

    private readonly Dictionary<(LayerId Layer, HexOrientation Orientation, int X, int Y, int Span, int Level), FlatBlock> _blocks = [];
    private long _blockBytes;

    internal FlatBlock? Block((LayerId Layer, HexOrientation Orientation, int X, int Y, int Span, int Level) key)
    {
        if (!_blocks.TryGetValue(key, out var block)) return null;
        block.LastUse = ++_clock;
        return block;
    }

    internal void PutBlock((LayerId Layer, HexOrientation Orientation, int X, int Y, int Span, int Level) key, FlatBlock block)
    {
        if (_blocks.Remove(key, out var old))
        {
            _blockBytes -= (long)old.Image.Width * old.Image.Height * 4;
            old.Image.Dispose();
        }

        block.LastUse = ++_clock;
        _blocks[key] = block;
        _blockBytes += (long)block.Image.Width * block.Image.Height * 4;
        if (_blockBytes <= BlockBudgetBytes) return;
        foreach (var (oldKey, entry) in _blocks.OrderBy(pair => pair.Value.LastUse).ToList())
        {
            if (_blockBytes <= BlockBudgetBytes * 3 / 4 || _blocks.Count <= 1) break;
            _blocks.Remove(oldKey);
            _blockBytes -= (long)entry.Image.Width * entry.Image.Height * 4;
            entry.Image.Dispose();
        }
    }

    public int Count => _entries.Count;

    public long Bytes => _bytes;

    /// <summary>The one kept tile of the repeating grid pattern (a grid rarely changes style or level).</summary>
    internal SKImage GridTile(object key, Func<SKImage> draw)
    {
        if (_gridTile is { } kept && kept.Key.Equals(key)) return kept.Image;
        _gridTile?.Image.Dispose();
        var image = draw();
        _gridTile = (key, image);
        return image;
    }

    internal SKImage? TryGet(HexChunk chunk, HexOrientation orientation, int level, bool flat)
    {
        if (!_entries.TryGetValue((chunk, orientation, level, flat), out var entry)) return null;
        entry.LastUse = ++_clock;
        return entry.Image;
    }

    /// <summary>A kept image of the chunk at the nearest other level (either look), to stand in while the right one is drawn.</summary>
    internal (SKImage Image, int Level)? NearestOther(HexChunk chunk, HexOrientation orientation, int level, bool flat)
    {
        if (TryGet(chunk, orientation, level, !flat) is { } look) return (look, level);
        for (var distance = 1; distance <= 8; distance++)
        {
            foreach (var candidate in (ReadOnlySpan<int>)[level - distance, level + distance])
            {
                if (TryGet(chunk, orientation, candidate, flat) is { } same) return (same, candidate);
                if (TryGet(chunk, orientation, candidate, !flat) is { } other) return (other, candidate);
            }
        }

        return null;
    }

    internal void Add(HexChunk chunk, HexOrientation orientation, int level, bool flat, SKImage image)
    {
        var bytes = (long)image.Width * image.Height * 4;
        if (_entries.Remove((chunk, orientation, level, flat), out var old))
        {
            _bytes -= old.Bytes;
            old.Image.Dispose();
        }

        _entries[(chunk, orientation, level, flat)] = new Entry(image, bytes) { LastUse = ++_clock };
        _bytes += bytes;
        if (_bytes <= BudgetBytes) return;

        // Over budget: drop the least recently used quarter at once instead of scanning per image.
        var target = BudgetBytes * 3 / 4;
        foreach (var (key, entry) in _entries.OrderBy(pair => pair.Value.LastUse).ToList())
        {
            if (_bytes <= target || _entries.Count <= 1) break;
            _entries.Remove(key);
            _bytes -= entry.Bytes;
            entry.Image.Dispose();
        }
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values) entry.Image.Dispose();
        _entries.Clear();
        _bytes = 0;
        _gridTile?.Image.Dispose();
        _gridTile = null;
        foreach (var block in _blocks.Values) block.Image.Dispose();
        _blocks.Clear();
        _blockBytes = 0;
    }

    public void Dispose() => Clear();
}

/// <summary>
/// Draws a hex layer: terrain tiles, feature tiles, the grid and the numbers. Rows are drawn top
/// to bottom so a feature never hides under the row in front of it. Far away a hex is its tile's
/// average colour; at any other distance it is the painted tile. With a <see cref="HexChunkCache"/>
/// both looks are kept per chunk, and a frame draws only as many new chunk images as its budget
/// allows: the others stand in from a neighbouring level and follow in the next frames.
/// </summary>
public static class HexLayerRenderer
{
    /// <summary>Below this many screen pixels per hex the tile is drawn as its average colour.</summary>
    public const double FlatColourBelowPixels = 10;

    /// <summary>Below this many screen pixels per hex the grid lines are left out.</summary>
    public const double GridBelowPixels = 7;

    /// <summary>Below this many screen pixels per hex the numbers are left out.</summary>
    public const double NumbersBelowPixels = 44;

    /// <summary>The repeating grid picture is used while its tile (two hexes across) stays this small.</summary>
    private const int MaxGridTilePixels = 1024;

    /// <summary>Time one frame may spend drawing chunk images that are not kept yet.</summary>
    public const double BuildBudgetMilliseconds = 8;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // Kept chunk images are made at the level at or just above the view scale, so they never shrink
    // by more than half: no mipmaps, which a texture made from a GPU surface does not have (asking
    // for them would copy the texture on every draw).
    private static readonly SKSamplingOptions ChunkSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private static readonly ConditionalWeakTable<HexChunk, StrongBox<SKColor>> ChunkAverages = new();

    /// <param name="requestFrame">Asks for another frame when some chunk images are still to be drawn.</param>
    /// <param name="stats">Counts the chunks that stood in this frame.</param>
    public static void Draw(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale,
        HexChunkCache? cache, GRContext? gpu, HexCursorPreview? cursor, bool isExport,
        Action? requestFrame = null, RenderStats? stats = null, bool live = false)
    {
        var board = layer.Board;
        var hexPixels = board.Layout.HexWidth * viewScale;
        var flat = hexPixels < FlatColourBelowPixels && !isExport;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var built = 0;
        var pending = 0;
        if (flat && cache is not null)
        {
            DrawFlatBlocks(canvas, layer, view, viewScale, cache, started, ref pending);
        }
        else foreach (var key in board.ChunksIn(view))
        {
            var chunk = board.Chunk(key);
            if (chunk is null) continue;
            if (cache is not null && !isExport)
            {
                // During a drag a chunk changes on every move: drawing its hexes directly is cheaper
                // than a new image (a GPU surface) per move; the image is made once the drag ends.
                var mayBuild = !live && (built == 0
                    || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < BuildBudgetMilliseconds);
                if (live && !flat && cache.TryGet(chunk, board.Orientation, LevelFor(viewScale), flat: false) is null)
                {
                    DrawChunkTiles(canvas, board, key, chunk, view, viewScale);
                    continue;
                }

                switch (TryDrawCached(canvas, board, key, chunk, viewScale, cache, gpu, flat, mayBuild))
                {
                    case CachedDraw.Kept:
                        continue;
                    case CachedDraw.Built:
                        built++;
                        continue;
                    case CachedDraw.Deferred:
                        pending++;
                        continue;
                }
            }

            if (flat) DrawChunkFlat(canvas, board, key, chunk);
            else DrawChunkTiles(canvas, board, key, chunk, view, viewScale);
        }

        if (pending > 0)
        {
            if (stats is not null) stats.HexChunksPending += pending;
            requestFrame?.Invoke();
        }

        if (layer.Grid.Visible && (hexPixels >= GridBelowPixels || isExport))
        {
            if (cache is null || isExport || !TryDrawGridPattern(canvas, layer, view, viewScale, hexPixels, cache))
            {
                DrawGrid(canvas, layer, view, viewScale, hexPixels, outside: null);
            }
        }

        if (cursor is not null && cursor.LayerId == layer.Id && cursor.Hexes.Count > 0)
        {
            DrawCursor(canvas, board, cursor, viewScale);
        }
    }

    /// <summary>Draws one tile picture into a hex; used by the canvas and by the palette thumbnails.</summary>
    public static void DrawTile(SKCanvas canvas, HexLayout layout, HexCoord hex, string tileId, int variant, float bleed = 0)
    {
        var image = HexTileArt.Image(tileId, variant, layout.Orientation);
        if (image is null)
        {
            DrawMissing(canvas, layout, hex);
            return;
        }

        var bounds = layout.Bounds(hex);
        var rect = new SKRect((float)bounds.Left - bleed, (float)bounds.Top - bleed, (float)bounds.Right + bleed, (float)bounds.Bottom + bleed);
        canvas.DrawImage(image, rect, Sampling);
    }

    private static void DrawMissing(SKCanvas canvas, HexLayout layout, HexCoord hex)
    {
        using var path = HexOutline(layout, hex, 0);
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xD0, 0x40, 0xA0, 160) };
        canvas.DrawPath(path, fill);
    }

    private static void DrawChunkTiles(SKCanvas canvas, HexBoard board, HexChunkKey key, HexChunk chunk, WorldBounds? clip, double pixelScale)
    {
        // Terrain overlaps its neighbours by a hair, at least one pixel of the target, so antialiased
        // edges never show the page between hexes - also where two kept chunk images meet.
        var bleed = (float)Math.Min(board.Layout.Size * 0.1, Math.Max(board.Layout.Size * 0.012, 2.0 / Math.Max(pixelScale, 1e-6)));
        for (var pass = 0; pass < 2; pass++)
        {
            for (var row = 0; row < HexBoard.ChunkSize; row++)
            {
                for (var column = 0; column < HexBoard.ChunkSize; column++)
                {
                    if (chunk[column, row] is not { } cell) continue;
                    var hex = board.CoordOf(key, column, row);
                    if (clip is { } view && !Intersects(board.Layout.Bounds(hex), view)) continue;
                    if (pass == 0) DrawTile(canvas, board.Layout, hex, cell.Terrain, cell.Variant, bleed);
                    else if (cell.Feature is { } feature) DrawTile(canvas, board.Layout, hex, feature, cell.FeatureVariant);
                }
            }
        }
    }

    private static void DrawChunkFlat(SKCanvas canvas, HexBoard board, HexChunkKey key, HexChunk chunk)
    {
        // One path per colour: a chunk holds a few tile colours, and many hexes go in one draw call.
        var paths = new Dictionary<SKColor, SKPath>();
        var corners = new SKPoint[6];
        const double grow = 1.04;
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (chunk[column, row] is not { } cell) continue;
                var hex = board.CoordOf(key, column, row);
                var color = AverageOf(cell);
                if (!paths.TryGetValue(color, out var path)) paths[color] = path = new SKPath();
                var (cx, cy) = board.Layout.Center(hex);
                for (var i = 0; i < 6; i++)
                {
                    var (x, y) = board.Layout.Corner(hex, i);
                    corners[i] = new SKPoint((float)(cx + (x - cx) * grow), (float)(cy + (y - cy) * grow));
                }

                path.AddPoly(corners, close: true);
            }
        }

        using var paint = new SKPaint { IsAntialias = false };
        foreach (var (color, path) in paths)
        {
            paint.Color = color;
            canvas.DrawPath(path, paint);
            path.Dispose();
        }
    }

    private static SKColor AverageOf(HexCell cell) =>
        (HexStarterTiles.Find(cell.Feature ?? cell.Terrain) ?? HexStarterTiles.Find(cell.Terrain))?.Average
        ?? new SKColor(0xD0, 0x40, 0xA0);

    /// <summary>The colour of a whole chunk seen from very far: the mean of its hexes' colours.</summary>
    private static SKColor ChunkAverage(HexChunk chunk) => ChunkAverages.GetValue(chunk, static c =>
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (c[column, row] is not { } cell) continue;
                var color = AverageOf(cell);
                r += color.Red; g += color.Green; b += color.Blue; n++;
            }
        }

        return new StrongBox<SKColor>(n == 0 ? SKColors.Transparent : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n)));
    }).Value;

    private enum CachedDraw { Direct, Kept, Built, Deferred }

    private static int LevelFor(double viewScale) => (int)Math.Ceiling(Math.Log2(Math.Max(viewScale, 1.0 / 1024)));

    /// <summary>
    /// The far look in blocks of span × span chunks (span grows until a block is about 128 px), drawn
    /// on the CPU: a GPU surface per small chunk cost the GPU 100-250 ms when a 512 × 512 board came
    /// into view. A block whose chunks changed keeps showing its old picture until it is redrawn.
    /// </summary>
    private static void DrawFlatBlocks(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale,
        HexChunkCache cache, long started, ref int pending)
    {
        var board = layer.Board;
        var level = LevelFor(viewScale);
        var scale = Math.Pow(2, level);
        var first = board.ChunkBounds(new HexChunkKey(0, 0));
        var span = 1;
        while (span < 16 && span * (first.Right - first.Left) * scale < 128) span *= 2;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        foreach (var key in board.ChunksIn(view))
        {
            minX = Math.Min(minX, key.X); minY = Math.Min(minY, key.Y);
            maxX = Math.Max(maxX, key.X); maxY = Math.Max(maxY, key.Y);
        }

        if (maxX < 0) return;
        var lastChunkX = (board.Columns - 1) / HexBoard.ChunkSize;
        var lastChunkY = (board.Rows - 1) / HexBoard.ChunkSize;
        var built = 0;
        for (var by = minY / span; by <= maxY / span; by++)
        {
            for (var bx = minX / span; bx <= maxX / span; bx++)
            {
                // The block's chunks and the ring of chunks around it: the ring is drawn into the
                // block's picture too, so where two blocks meet each picture is solid across the seam
                // (shrunk on screen, a picture's edge pixels would otherwise let the page show through).
                var side = span + 2;
                var chunks = new HexChunk?[side * side];
                var any = false;
                for (var j = -1; j <= span; j++)
                {
                    for (var i = -1; i <= span; i++)
                    {
                        var x = bx * span + i;
                        var y = by * span + j;
                        var chunk = x < 0 || y < 0 ? null : board.Chunk(new HexChunkKey(x, y));
                        chunks[(j + 1) * side + i + 1] = chunk;
                        if (i >= 0 && j >= 0 && i < span && j < span) any |= chunk is not null;
                    }
                }

                if (!any) continue;
                var blockKey = (layer.Id, board.Orientation, bx, by, span, level);
                var kept = cache.Block(blockKey);
                if (kept is not null && kept.Layout != board.Layout) kept = null;
                var current = kept is not null && kept.Chunks.AsSpan().SequenceEqual(chunks, ReferenceComparer.Instance);
                if (!current && (built == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < BuildBudgetMilliseconds))
                {
                    var topLeft = board.ChunkBounds(new HexChunkKey(bx * span, by * span));
                    var bottomRight = board.ChunkBounds(new HexChunkKey(Math.Min(bx * span + span - 1, lastChunkX), Math.Min(by * span + span - 1, lastChunkY)));
                    var bounds = new WorldBounds(topLeft.Left, topLeft.Top, bottomRight.Right, bottomRight.Bottom);
                    var width = Math.Max(1, (int)Math.Ceiling((bounds.Right - bounds.Left) * scale));
                    var height = Math.Max(1, (int)Math.Ceiling((bounds.Bottom - bounds.Top) * scale));
                    using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    if (surface is not null)
                    {
                        var target = surface.Canvas;
                        target.Clear(SKColors.Transparent);
                        target.Scale((float)scale);
                        target.Translate((float)-bounds.Left, (float)-bounds.Top);
                        for (var j = -1; j <= span; j++)
                        {
                            for (var i = -1; i <= span; i++)
                            {
                                if (chunks[(j + 1) * side + i + 1] is { } chunk)
                                {
                                    DrawChunkFlat(target, board, new HexChunkKey(bx * span + i, by * span + j), chunk);
                                }
                            }
                        }

                        kept = new HexChunkCache.FlatBlock(chunks, board.Layout, bounds, surface.Snapshot());
                        cache.PutBlock(blockKey, kept);
                        built++;
                        current = true;
                    }
                }

                if (kept is not null)
                {
                    var b = kept.Bounds;
                    canvas.DrawImage(kept.Image, new SKRect((float)b.Left, (float)b.Top,
                        (float)(b.Left + kept.Image.Width / scale), (float)(b.Top + kept.Image.Height / scale)), ChunkSampling);
                }
                else
                {
                    for (var j = 0; j < span; j++)
                    {
                        for (var i = 0; i < span; i++)
                        {
                            if (chunks[(j + 1) * side + i + 1] is not { } chunk) continue;
                            var bounds = board.ChunkBounds(new HexChunkKey(bx * span + i, by * span + j));
                            using var paint = new SKPaint { Color = ChunkAverage(chunk) };
                            canvas.DrawRect(new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom), paint);
                        }
                    }
                }

                if (!current) pending++;
            }
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<HexChunk?>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(HexChunk? x, HexChunk? y) => ReferenceEquals(x, y);

        public int GetHashCode(HexChunk? obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static CachedDraw TryDrawCached(SKCanvas canvas, HexBoard board, HexChunkKey key, HexChunk chunk,
        double viewScale, HexChunkCache cache, GRContext? gpu, bool flat, bool mayBuild)
    {
        // Level n keeps the chunk at scale 2^n: the nearest level at or above the view scale.
        var level = (int)Math.Ceiling(Math.Log2(Math.Max(viewScale, 1.0 / 1024)));
        var scale = Math.Pow(2, level);
        var bounds = board.ChunkBounds(key);
        var width = (int)Math.Ceiling((bounds.Right - bounds.Left) * scale);
        var height = (int)Math.Ceiling((bounds.Bottom - bounds.Top) * scale);
        if (width <= 0 || height <= 0 || width > HexChunkCache.MaxImageSide || height > HexChunkCache.MaxImageSide)
        {
            return CachedDraw.Direct;
        }

        var image = cache.TryGet(chunk, board.Orientation, level, flat);
        var result = CachedDraw.Kept;
        if (image is null && !mayBuild)
        {
            if (cache.NearestOther(chunk, board.Orientation, level, flat) is { } other)
            {
                var otherScale = Math.Pow(2, other.Level);
                canvas.DrawImage(other.Image,
                    new SKRect((float)bounds.Left, (float)bounds.Top,
                        (float)(bounds.Left + other.Image.Width / otherScale), (float)(bounds.Top + other.Image.Height / otherScale)),
                    ChunkSampling);
            }
            else if (chunk.Count < HexBoard.ChunkSize * HexBoard.ChunkSize * 3 / 4)
            {
                // A sparse chunk is cheap to draw flat, and a rectangle would paint its empty hexes.
                DrawChunkFlat(canvas, board, key, chunk);
            }
            else
            {
                using var paint = new SKPaint { Color = ChunkAverage(chunk) };
                canvas.DrawRect(new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom), paint);
            }

            return CachedDraw.Deferred;
        }

        if (image is null)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = (gpu is not null ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return CachedDraw.Direct;
            var target = surface.Canvas;
            target.Clear(SKColors.Transparent);
            target.Scale((float)scale);
            target.Translate((float)-bounds.Left, (float)-bounds.Top);
            if (flat) DrawChunkFlat(target, board, key, chunk);
            else DrawChunkTiles(target, board, key, chunk, clip: null, scale);
            image = surface.Snapshot();
            if (image is null) return CachedDraw.Direct;
            cache.Add(chunk, board.Orientation, level, flat, image);
            result = CachedDraw.Built;
        }

        canvas.DrawImage(image,
            new SKRect((float)bounds.Left, (float)bounds.Top,
                (float)(bounds.Left + image.Width / scale), (float)(bounds.Top + image.Height / scale)),
            ChunkSampling);
        return result;
    }

    /// <summary>
    /// Many small hexes on screen: the grid is one repeating picture instead of a path through every
    /// hex (a path of 15 000 hexes cost the GPU 10-16 ms a frame). The picture covers the inner part
    /// of the board, where every hex exists; the outer two rings, where the board's edge is jagged,
    /// are drawn as lines around the picture.
    /// </summary>
    private static bool TryDrawGridPattern(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale,
        double hexPixels, HexChunkCache cache)
    {
        var board = layer.Board;
        if (!layer.Grid.ShowEmpty || board.Columns < 5 || board.Rows < 5) return false;
        var layout = board.Layout;
        (double X, double Y) At(int column, int row) => layout.Center(layout.FromOffset(new HexOffset(column, row)));
        var inner = new SKRect(
            (float)Math.Max(At(1, 1).X, At(1, 2).X), (float)Math.Max(At(1, 1).Y, At(2, 1).Y),
            (float)Math.Min(At(board.Columns - 2, 1).X, At(board.Columns - 2, 2).X),
            (float)Math.Min(At(1, board.Rows - 2).Y, At(2, board.Rows - 2).Y));
        if (inner.Width <= 0 || inner.Height <= 0) return false;

        var level = (int)Math.Ceiling(Math.Log2(Math.Max(viewScale, 1.0 / 1024)));
        var scale = Math.Pow(2, level);
        var width = Math.Max(layer.Grid.Width, 0.5 / scale);
        var flatTop = layout.Orientation == HexOrientation.Flat;
        var periodX = flatTop ? 3 * layout.Size : HexLayout.Sqrt3 * layout.Size;
        var periodY = flatTop ? HexLayout.Sqrt3 * layout.Size : 3 * layout.Size;
        var pixelsX = Math.Max(1, (int)Math.Ceiling(periodX * scale));
        var pixelsY = Math.Max(1, (int)Math.Ceiling(periodY * scale));
        if (pixelsX > MaxGridTilePixels || pixelsY > MaxGridTilePixels) return false;
        var color = ParseColor(layer.Grid.Color);
        var tile = cache.GridTile((layout.Orientation, layout.Size, color, width, level), () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(pixelsX, pixelsY, SKColorType.Rgba8888, SKAlphaType.Premul));
            var target = surface.Canvas;
            target.Clear(SKColors.Transparent);
            target.Scale((float)(pixelsX / periodX), (float)(pixelsY / periodY));
            // Hex (0, 0) has its centre at the tile's corner; every hex touching the tile is outlined.
            var local = new HexLayout(layout.Orientation, layout.Size, 0, 0);
            using var path = new SKPath();
            for (var q = -3; q <= 4; q++)
            {
                for (var r = -4; r <= 4; r++)
                {
                    var hex = new HexCoord(q, r);
                    for (var i = 0; i < 6; i++)
                    {
                        var (x, y) = local.Corner(hex, i);
                        if (i == 0) path.MoveTo((float)x, (float)y);
                        else path.LineTo((float)x, (float)y);
                    }

                    path.Close();
                }
            }

            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color, StrokeWidth = (float)width };
            target.DrawPath(path, line);
            return surface.Snapshot();
        });

        var matrix = SKMatrix.CreateScale((float)(periodX / pixelsX), (float)(periodY / pixelsY))
            .PostConcat(SKMatrix.CreateTranslation((float)layout.OriginX, (float)layout.OriginY));
        using (var shader = SKShader.CreateImage(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, ChunkSampling, matrix))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
        {
            canvas.DrawRect(inner, paint);
        }

        // The outer rings' lines and the numbers (text, never part of the picture) go on top.
        if ((layer.Grid.Numbers && hexPixels >= NumbersBelowPixels)
            || view.Left < inner.Left || view.Top < inner.Top || view.Right > inner.Right || view.Bottom > inner.Bottom)
        {
            DrawGrid(canvas, layer, view, viewScale, hexPixels, outside: inner);
        }

        return true;
    }

    /// <param name="outside">When set, only the lines outside this rectangle are drawn (the pattern covers it).</param>
    private static void DrawGrid(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale, double hexPixels, SKRect? outside)
    {
        var board = layer.Board;
        var color = ParseColor(layer.Grid.Color);
        using var line = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color,
            // The grid stays at least half a screen pixel wide however far the view is zoomed out.
            StrokeWidth = (float)Math.Max(layer.Grid.Width, 0.5 / Math.Max(viewScale, 1e-6)),
        };
        using var path = new SKPath();
        var numbers = layer.Grid.Numbers && hexPixels >= NumbersBelowPixels;
        using var font = numbers ? new SKFont(SKTypeface.Default, (float)(board.Layout.Size * 0.2)) : null;
        using var text = numbers ? new SKPaint { IsAntialias = true, Color = color.WithAlpha((byte)Math.Min(255, color.Alpha * 2)) } : null;
        foreach (var hex in board.HexesIn(view))
        {
            if (!layer.Grid.ShowEmpty && board[hex] is null) continue;
            var offset = board.Layout.ToOffset(hex);
            // Under the repeating picture only the two outer rings need lines of their own.
            var lines = outside is null || offset.Column <= 1 || offset.Row <= 1
                || offset.Column >= board.Columns - 2 || offset.Row >= board.Rows - 2;
            if (lines)
            {
                for (var i = 0; i < 6; i++)
                {
                    var (x, y) = board.Layout.Corner(hex, i);
                    if (i == 0) path.MoveTo((float)x, (float)y);
                    else path.LineTo((float)x, (float)y);
                }

                path.Close();
            }

            if (numbers)
            {
                var (cx, cy) = board.Layout.Center(hex);
                canvas.DrawText(NumberOf(board, offset), (float)cx, (float)(cy - board.Layout.HexHeight * 0.32), SKTextAlign.Center, font!, text!);
            }
        }

        if (outside is { } inner)
        {
            canvas.Save();
            canvas.ClipRect(inner, SKClipOperation.Difference);
            canvas.DrawPath(path, line);
            canvas.Restore();
        }
        else
        {
            canvas.DrawPath(path, line);
        }
    }

    /// <summary>
    /// The hex's number, column then row from 1: «0101» while both fit in two digits; on larger
    /// boards the parts are as wide as the largest and split by a dot («251.251»), so they never run together.
    /// </summary>
    public static string NumberOf(HexBoard board, HexOffset offset)
    {
        var digits = Math.Max(2, Math.Max(board.Columns, board.Rows).ToString(CultureInfo.InvariantCulture).Length);
        var format = new string('0', digits);
        var column = (offset.Column + 1).ToString(format, CultureInfo.InvariantCulture);
        var row = (offset.Row + 1).ToString(format, CultureInfo.InvariantCulture);
        return digits == 2 ? column + row : column + "." + row;
    }

    private static void DrawCursor(SKCanvas canvas, HexBoard board, HexCursorPreview cursor, double viewScale)
    {
        using var path = new SKPath();
        foreach (var hex in cursor.Hexes)
        {
            using var outline = HexOutline(board.Layout, hex, 0);
            path.AddPath(outline);
        }

        // The ring goes round the footprint only: the edges between two hexes of it are left out
        // (a footprint of 217 hexes has 1 302 edges, its outline 96), which keeps the stroked
        // outline cheap for the GPU on every pointer move.
        var inside = cursor.Hexes.ToHashSet();
        using var border = new SKPath();
        foreach (var hex in cursor.Hexes)
        {
            var (cx, cy) = board.Layout.Center(hex);
            for (var i = 0; i < 6; i++)
            {
                var (ax, ay) = board.Layout.Corner(hex, i);
                var (bx, by) = board.Layout.Corner(hex, (i + 1) % 6);
                var across = board.Layout.HexAt(ax + bx - cx, ay + by - cy);
                if (inside.Contains(across)) continue;
                border.MoveTo((float)ax, (float)ay);
                border.LineTo((float)bx, (float)by);
            }
        }

        var screen = (float)(1 / Math.Max(viewScale, 1e-6));
        using var fill = new SKPaint { IsAntialias = true, Color = cursor.Erase ? new SKColor(0xC8, 0x30, 0x30, 44) : new SKColor(0xF0, 0xC0, 0x50, 44) };
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 4 * screen, Color = new SKColor(0, 0, 0, 120) };
        using var ring = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 2 * screen,
            Color = cursor.Erase ? new SKColor(0xF0, 0x70, 0x60) : new SKColor(0xF4, 0xD0, 0x70),
        };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(border, halo);
        canvas.DrawPath(border, ring);
    }

    private static SKPath HexOutline(HexLayout layout, HexCoord hex, double grow)
    {
        var path = new SKPath();
        var (cx, cy) = layout.Center(hex);
        var factor = (layout.Size + grow) / layout.Size;
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = layout.Corner(hex, i);
            var px = (float)(cx + (x - cx) * factor);
            var py = (float)(cy + (y - cy) * factor);
            if (i == 0) path.MoveTo(px, py);
            else path.LineTo(px, py);
        }

        path.Close();
        return path;
    }

    private static bool Intersects(WorldBounds a, WorldBounds b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static SKColor ParseColor(string value) =>
        SKColor.TryParse(value, out var color) ? color : new SKColor(0x20, 0x18, 0x10, 0x66);
}
