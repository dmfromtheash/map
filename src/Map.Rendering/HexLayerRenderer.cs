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
/// so the chunk object is the key, and an edit invalidates exactly the chunks it copied.
/// </summary>
public sealed class HexChunkCache : IDisposable
{
    /// <summary>Video memory the kept chunk images may hold.</summary>
    public const long BudgetBytes = 192L * 1024 * 1024;

    /// <summary>A chunk image larger than this on a side is not kept; the hexes are drawn directly.</summary>
    public const int MaxImageSide = 2048;

    private readonly Dictionary<(HexChunk Chunk, HexOrientation Orientation, int Level), Entry> _entries = new(new KeyComparer());
    private long _bytes;
    private long _clock;

    private sealed class Entry(SKImage image, long bytes)
    {
        public SKImage Image { get; } = image;
        public long Bytes { get; } = bytes;
        public long LastUse { get; set; }
    }

    private sealed class KeyComparer : IEqualityComparer<(HexChunk Chunk, HexOrientation Orientation, int Level)>
    {
        public bool Equals((HexChunk Chunk, HexOrientation Orientation, int Level) x, (HexChunk Chunk, HexOrientation Orientation, int Level) y) =>
            ReferenceEquals(x.Chunk, y.Chunk) && x.Orientation == y.Orientation && x.Level == y.Level;

        public int GetHashCode((HexChunk Chunk, HexOrientation Orientation, int Level) key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Chunk), key.Orientation, key.Level);
    }

    public int Count => _entries.Count;

    public long Bytes => _bytes;

    internal SKImage? TryGet(HexChunk chunk, HexOrientation orientation, int level)
    {
        if (!_entries.TryGetValue((chunk, orientation, level), out var entry)) return null;
        entry.LastUse = ++_clock;
        return entry.Image;
    }

    internal void Add(HexChunk chunk, HexOrientation orientation, int level, SKImage image)
    {
        var bytes = (long)image.Width * image.Height * 4;
        if (_entries.Remove((chunk, orientation, level), out var old))
        {
            _bytes -= old.Bytes;
            old.Image.Dispose();
        }

        _entries[(chunk, orientation, level)] = new Entry(image, bytes) { LastUse = ++_clock };
        _bytes += bytes;
        while (_bytes > BudgetBytes && _entries.Count > 1)
        {
            var oldest = _entries.MinBy(pair => pair.Value.LastUse);
            _entries.Remove(oldest.Key);
            _bytes -= oldest.Value.Bytes;
            oldest.Value.Image.Dispose();
        }
    }

    public void Clear()
    {
        foreach (var entry in _entries.Values) entry.Image.Dispose();
        _entries.Clear();
        _bytes = 0;
    }

    public void Dispose() => Clear();
}

/// <summary>
/// Draws a hex layer: terrain tiles, feature tiles, the grid and the numbers. Rows are drawn top
/// to bottom so a feature never hides under the row in front of it. Far away a hex is its tile's
/// average colour; at any other distance it is the painted tile, kept per chunk in
/// <see cref="HexChunkCache"/> when the renderer has one.
/// </summary>
public static class HexLayerRenderer
{
    /// <summary>Below this many screen pixels per hex the tile is drawn as its average colour.</summary>
    public const double FlatColourBelowPixels = 10;

    /// <summary>Below this many screen pixels per hex the grid lines are left out.</summary>
    public const double GridBelowPixels = 7;

    /// <summary>Below this many screen pixels per hex the numbers are left out.</summary>
    public const double NumbersBelowPixels = 44;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static void Draw(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale,
        HexChunkCache? cache, GRContext? gpu, HexCursorPreview? cursor, bool isExport)
    {
        var board = layer.Board;
        var hexPixels = board.Layout.HexWidth * viewScale;
        var flat = hexPixels < FlatColourBelowPixels && !isExport;
        foreach (var key in board.ChunksIn(view))
        {
            var chunk = board.Chunk(key);
            if (chunk is null) continue;
            if (flat)
            {
                DrawChunkFlat(canvas, board, key, chunk);
                continue;
            }

            if (cache is not null && !isExport && TryDrawCached(canvas, board, key, chunk, viewScale, cache, gpu))
            {
                continue;
            }

            DrawChunkTiles(canvas, board, key, chunk, view);
        }

        if (layer.Grid.Visible && (hexPixels >= GridBelowPixels || isExport))
        {
            DrawGrid(canvas, layer, view, viewScale, hexPixels);
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

    private static void DrawChunkTiles(SKCanvas canvas, HexBoard board, HexChunkKey key, HexChunk chunk, WorldBounds? clip)
    {
        // Terrain overlaps its neighbours by a hair so antialiased edges never show the page between hexes.
        var bleed = (float)(board.Layout.Size * 0.012);
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
        using var paint = new SKPaint { IsAntialias = false };
        for (var row = 0; row < HexBoard.ChunkSize; row++)
        {
            for (var column = 0; column < HexBoard.ChunkSize; column++)
            {
                if (chunk[column, row] is not { } cell) continue;
                var hex = board.CoordOf(key, column, row);
                var tile = HexStarterTiles.Find(cell.Feature ?? cell.Terrain) ?? HexStarterTiles.Find(cell.Terrain);
                paint.Color = tile?.Average ?? new SKColor(0xD0, 0x40, 0xA0);
                using var path = HexOutline(board.Layout, hex, board.Layout.Size * 0.04);
                canvas.DrawPath(path, paint);
            }
        }
    }

    private static bool TryDrawCached(SKCanvas canvas, HexBoard board, HexChunkKey key, HexChunk chunk,
        double viewScale, HexChunkCache cache, GRContext? gpu)
    {
        // Level n keeps the chunk at scale 2^n: the nearest level at or above the view scale.
        var level = (int)Math.Ceiling(Math.Log2(Math.Max(viewScale, 1.0 / 64)));
        var scale = Math.Pow(2, level);
        var bounds = board.ChunkBounds(key);
        var width = (int)Math.Ceiling((bounds.Right - bounds.Left) * scale);
        var height = (int)Math.Ceiling((bounds.Bottom - bounds.Top) * scale);
        if (width <= 0 || height <= 0 || width > HexChunkCache.MaxImageSide || height > HexChunkCache.MaxImageSide)
        {
            return false;
        }

        var image = cache.TryGet(chunk, board.Orientation, level);
        if (image is null)
        {
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = (gpu is not null ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null) return false;
            var target = surface.Canvas;
            target.Clear(SKColors.Transparent);
            target.Scale((float)scale);
            target.Translate((float)-bounds.Left, (float)-bounds.Top);
            DrawChunkTiles(target, board, key, chunk, clip: null);
            image = surface.Snapshot();
            cache.Add(chunk, board.Orientation, level, image);
        }

        canvas.DrawImage(image,
            new SKRect((float)bounds.Left, (float)bounds.Top,
                (float)(bounds.Left + image.Width / scale), (float)(bounds.Top + image.Height / scale)),
            Sampling);
        return true;
    }

    private static void DrawGrid(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale, double hexPixels)
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
            for (var i = 0; i < 6; i++)
            {
                var (x, y) = board.Layout.Corner(hex, i);
                if (i == 0) path.MoveTo((float)x, (float)y);
                else path.LineTo((float)x, (float)y);
            }

            path.Close();
            if (numbers)
            {
                var offset = board.Layout.ToOffset(hex);
                var (cx, cy) = board.Layout.Center(hex);
                var label = (offset.Column + 1).ToString("00", CultureInfo.InvariantCulture)
                    + (offset.Row + 1).ToString("00", CultureInfo.InvariantCulture);
                canvas.DrawText(label, (float)cx, (float)(cy - board.Layout.HexHeight * 0.32), SKTextAlign.Center, font!, text!);
            }
        }

        canvas.DrawPath(path, line);
    }

    private static void DrawCursor(SKCanvas canvas, HexBoard board, HexCursorPreview cursor, double viewScale)
    {
        using var path = new SKPath();
        foreach (var hex in cursor.Hexes)
        {
            using var outline = HexOutline(board.Layout, hex, 0);
            path.AddPath(outline);
        }

        var screen = (float)(1 / Math.Max(viewScale, 1e-6));
        using var fill = new SKPaint { IsAntialias = true, Color = cursor.Erase ? new SKColor(0xC8, 0x30, 0x30, 44) : new SKColor(0xF0, 0xC0, 0x50, 44) };
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 * screen, Color = new SKColor(0, 0, 0, 120) };
        using var ring = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * screen,
            Color = cursor.Erase ? new SKColor(0xF0, 0x70, 0x60) : new SKColor(0xF4, 0xD0, 0x70),
        };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, halo);
        canvas.DrawPath(path, ring);
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
