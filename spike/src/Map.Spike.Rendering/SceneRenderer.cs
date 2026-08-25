using Map.Spike.Core.Documents;
using Map.Spike.Core.Geometry;
using SkiaSharp;

namespace Map.Spike.Rendering;

public sealed class RenderSettings
{
    /// <summary>Apply the mask to its target raster layer.</summary>
    public bool MaskEnabled { get; set; } = true;

    /// <summary>
    /// Draw the red mask-edit tint. This is editor chrome: <see cref="SceneRenderer"/> refuses to
    /// draw it unless <see cref="IncludeOverlay"/> is also set, and export never sets either.
    /// </summary>
    public bool ShowMaskEditOverlay { get; set; }

    /// <summary>Selection highlight, mask tint and canvas frame. Never true for export.</summary>
    public bool IncludeOverlay { get; set; } = true;

    public ObjectId? SelectedObjectId { get; set; }

    /// <summary>Opaque page colour, or null to leave the surface transparent (export default).</summary>
    public SKColor? BackgroundColor { get; set; }
}

public struct RenderStats
{
    public int ChunkBucketsDrawn;
    public int ObjectsInDrawnBuckets;
    public int RasterTilesDrawn;
    public int MaskTilesDrawn;
    public int DrawCalls;

    public override readonly string ToString() =>
        $"buckets={ChunkBucketsDrawn} objects={ObjectsInDrawnBuckets} " +
        $"rasterTiles={RasterTilesDrawn} maskTiles={MaskTilesDrawn} drawCalls={DrawCalls}";
}

/// <summary>
/// The single scene evaluator. The window and the offscreen exporter both go through it, so what
/// the user sees and what gets exported cannot drift apart (docs/04 "export uses the same scene
/// evaluator, different target and scale").
///
/// It never mutates the document.
/// </summary>
public sealed class SceneRenderer
{
    private static readonly SKColor MaskOverlayColor = new(220, 50, 47, 110);
    private static readonly SKColor SelectionColor = new(255, 214, 10, 255);
    private static readonly SKColor CanvasFrameColor = new(120, 120, 130, 160);

    public static void Render(
        SKCanvas canvas,
        SpikeDocument document,
        Viewport viewport,
        RenderSettings settings,
        TileImageCache tileCache,
        ObjectGeometryCache geometryCache,
        ref RenderStats stats)
    {
        if (settings.BackgroundColor is { } background)
        {
            canvas.Clear(background);
        }
        else
        {
            canvas.Clear(SKColors.Transparent);
        }

        var visibleWorld = viewport.VisibleWorldBounds();

        canvas.Save();
        canvas.Scale((float)viewport.Scale);
        canvas.Translate((float)-viewport.Offset.X, (float)-viewport.Offset.Y);

        // Order is fixed: decor raster, masked brush raster, objects, then overlay.
        DrawRasterLayer(canvas, document.Decor, visibleWorld, tileCache, ref stats);
        DrawMaskedBrushLayer(canvas, document, visibleWorld, settings, tileCache, ref stats);
        DrawObjects(canvas, visibleWorld, geometryCache, ref stats);

        if (settings.IncludeOverlay)
        {
            DrawOverlay(canvas, document, viewport, visibleWorld, settings, tileCache, ref stats);
        }

        canvas.Restore();
    }

    private static void DrawRasterLayer(
        SKCanvas canvas,
        RasterLayer layer,
        Rect2 visibleWorld,
        TileImageCache tileCache,
        ref RenderStats stats)
    {
        if (!layer.Visible)
        {
            return;
        }

        using var paint = new SKPaint { IsAntialias = false };

        foreach (var key in VisibleTiles(layer.TileKeys, layer.TileSize, visibleWorld))
        {
            var image = tileCache.GetRasterTile(layer, key);
            if (image is null)
            {
                continue;
            }

            canvas.DrawImage(image, TileRect(key, layer.TileSize), paint);
            stats.RasterTilesDrawn++;
            stats.DrawCalls++;
        }
    }

    /// <summary>
    /// Draws the brush layer inside an isolated Skia layer and multiplies its alpha by the mask.
    /// Isolation is what confines the mask to this one raster layer: the decor layer is already
    /// composited underneath and is outside the saveLayer, so DstIn cannot reach it.
    /// </summary>
    private static void DrawMaskedBrushLayer(
        SKCanvas canvas,
        SpikeDocument document,
        Rect2 visibleWorld,
        RenderSettings settings,
        TileImageCache tileCache,
        ref RenderStats stats)
    {
        var layer = document.Brush;
        if (!layer.Visible)
        {
            return;
        }

        var brushTiles = VisibleTiles(layer.TileKeys, layer.TileSize, visibleWorld).ToList();
        if (brushTiles.Count == 0)
        {
            return;
        }

        var mask = document.Mask;
        var applyMask = settings.MaskEnabled
            && mask.Enabled
            && mask.TargetLayerId == layer.Id;

        canvas.SaveLayer();

        using (var paint = new SKPaint { IsAntialias = false })
        {
            foreach (var key in brushTiles)
            {
                var image = tileCache.GetRasterTile(layer, key);
                if (image is null)
                {
                    continue;
                }

                canvas.DrawImage(image, TileRect(key, layer.TileSize), paint);
                stats.RasterTilesDrawn++;
                stats.DrawCalls++;
            }
        }

        if (applyMask)
        {
            // DstIn keeps the destination scaled by source alpha. An Alpha8 image supplies exactly
            // that coverage. Tiles with no mask entry are left untouched, which is the same as a
            // fully opaque mask - so an unpainted mask cannot silently erase the layer.
            using var maskPaint = new SKPaint
            {
                BlendMode = SKBlendMode.DstIn,
                Color = SKColors.Black,
                IsAntialias = false,
            };

            foreach (var key in brushTiles)
            {
                var maskImage = tileCache.GetMaskTile(mask, key);
                if (maskImage is null)
                {
                    continue;
                }

                canvas.DrawImage(maskImage, TileRect(key, mask.TileSize), maskPaint);
                stats.MaskTilesDrawn++;
                stats.DrawCalls++;
            }
        }

        canvas.Restore();
    }

    private static void DrawObjects(
        SKCanvas canvas,
        Rect2 visibleWorld,
        ObjectGeometryCache geometryCache,
        ref RenderStats stats)
    {
        // White paint with Modulate: the result is srcColor * white == the per-vertex colour.
        // Without a shader on the paint this is the well-defined way to let vertex colours through.
        using var paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };

        foreach (var chunk in geometryCache.VisibleChunks(visibleWorld))
        {
            canvas.DrawVertices(chunk.Vertices, SKBlendMode.Modulate, paint);
            stats.ChunkBucketsDrawn++;
            stats.ObjectsInDrawnBuckets += chunk.ObjectCount;
            stats.DrawCalls++;
        }
    }

    private static void DrawOverlay(
        SKCanvas canvas,
        SpikeDocument document,
        Viewport viewport,
        Rect2 visibleWorld,
        RenderSettings settings,
        TileImageCache tileCache,
        ref RenderStats stats)
    {
        if (settings.ShowMaskEditOverlay && document.Mask.Enabled)
        {
            using var tint = new SKPaint
            {
                Color = MaskOverlayColor,
                IsAntialias = false,
            };

            foreach (var key in VisibleTiles(document.Mask.TileKeys, document.Mask.TileSize, visibleWorld))
            {
                var inverted = tileCache.GetInvertedMaskTile(document.Mask, key);
                if (inverted is null)
                {
                    continue;
                }

                canvas.DrawImage(inverted, TileRect(key, document.Mask.TileSize), tint);
                stats.DrawCalls++;
            }
        }

        if (settings.SelectedObjectId is { } selectedId)
        {
            foreach (var o in document.Objects.Objects)
            {
                if (o.Id != selectedId)
                {
                    continue;
                }

                using var stroke = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = SelectionColor,
                    StrokeWidth = (float)(2.0 / viewport.Scale),
                    IsAntialias = true,
                };

                var b = o.Bounds.Inflate(2.0 / viewport.Scale);
                canvas.DrawRect(new SKRect((float)b.X, (float)b.Y, (float)b.Right, (float)b.Bottom), stroke);
                stats.DrawCalls++;
                break;
            }
        }

        using var frame = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = CanvasFrameColor,
            StrokeWidth = (float)(1.0 / viewport.Scale),
            IsAntialias = true,
        };

        canvas.DrawRect(new SKRect(0, 0, document.CanvasWidth, document.CanvasHeight), frame);
        stats.DrawCalls++;
    }

    private static IEnumerable<TileKey> VisibleTiles(
        IReadOnlyCollection<TileKey> keys, int tileSize, Rect2 visibleWorld)
    {
        foreach (var key in keys)
        {
            var bounds = new Rect2(key.X * (double)tileSize, key.Y * (double)tileSize, tileSize, tileSize);
            if (bounds.Intersects(visibleWorld))
            {
                yield return key;
            }
        }
    }

    private static SKRect TileRect(TileKey key, int tileSize) => new(
        key.X * tileSize,
        key.Y * tileSize,
        (key.X * tileSize) + tileSize,
        (key.Y * tileSize) + tileSize);
}
