using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Supplies decoded asset images to the renderer. Stage 4's catalog implements it.</summary>
public interface IStampImageSource
{
    /// <summary>The image for the content hash, or null when the asset is missing/not resolved.</summary>
    SKImage? TryGetImage(string sha256);
}

/// <summary>The live, uncommitted part of a raster gesture, drawn on top of its layer.</summary>
public sealed class StrokePreview
{
    public required LayerId LayerId { get; init; }

    /// <summary>Per-tile coverage 0..255 from the in-progress <c>RasterStrokeSession</c>.</summary>
    public required IReadOnlyDictionary<TileKey, byte[]> Coverage { get; init; }

    public string Color { get; init; } = "#FF000000";
    public double Opacity { get; init; } = 1.0;
    public bool Eraser { get; init; }

    /// <summary>Mask strokes preview as the red mask overlay, not as paint.</summary>
    public bool IsMask { get; init; }
}

/// <summary>Per-frame counters. Optional: the editor sets it only when the HUD or a test needs it.</summary>
public sealed class RenderStats
{
    public int LayersDrawn { get; set; }
    public int ObjectCandidates { get; set; }
    public int ObjectsDrawn { get; set; }
    public int TilesDrawn { get; set; }
    public int MaskTilesDrawn { get; set; }
    public int MipmappedTiles { get; set; }
    public int MissingAssets { get; set; }

    public void Reset()
    {
        LayersDrawn = 0;
        ObjectCandidates = 0;
        ObjectsDrawn = 0;
        TilesDrawn = 0;
        MaskTilesDrawn = 0;
        MipmappedTiles = 0;
        MissingAssets = 0;
    }
}

/// <summary>Options of one scene pass. Selection overlay is editor chrome and is opt-in only.</summary>
public sealed class RenderOptions
{
    /// <summary>Object to draw the selection gizmo around, or null. Never set on export paths.</summary>
    public ObjectId? SelectedObjectId { get; init; }

    /// <summary>Additional selected objects (group selection) drawn with a thin ring, no handles.</summary>
    public IReadOnlyCollection<ObjectId>? CoSelectedObjectIds { get; init; }

    /// <summary>
    /// Current view scale, used to keep the gizmo's ring, handles and rotation stem a constant
    /// on-screen size regardless of zoom. Irrelevant when no selection is drawn.
    /// </summary>
    public double ViewScale { get; init; } = 1.0;

    /// <summary>
    /// World rectangle actually on screen. When set, objects come from the layer's spatial index
    /// and raster layers upload only the intersecting tiles - the reason a 65 536-unit map draws
    /// in constant time regardless of how much content lives outside the viewport.
    /// </summary>
    public WorldBounds? VisibleWorld { get; init; }

    public TileImageCache? TileCache { get; init; }

    public IStampImageSource? Stamps { get; init; }

    public StrokePreview? Preview { get; init; }

    /// <summary>While a mask is being edited its hidden area is tinted red on this layer only.</summary>
    public LayerId? MaskEditLayerId { get; init; }

    public RenderStats? Stats { get; init; }
}

/// <summary>
/// The single scene evaluator: the editor canvas and the thumbnail exporter both go through it,
/// so what the user sees and what gets stored cannot drift apart. It never mutates the document.
/// </summary>
public static class SceneRenderer
{
    private static readonly SKColor SelectionColor = new(255, 214, 10, 255);

    /// <summary>Draws the document in world coordinates. The caller sets the world transform.</summary>
    public static void Render(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        options.Stats?.Reset();

        var view = options.VisibleWorld ?? new WorldBounds(
            0, 0, document.Canvas.Width, document.Canvas.Height);

        foreach (var layer in document.Layers)
        {
            if (!layer.Visible || layer.Opacity <= 0)
            {
                continue;
            }

            if (options.Stats is { } stats)
            {
                stats.LayersDrawn++;
            }

            DrawLayer(canvas, document, layer, view, options);
        }

        DrawSelectionChrome(canvas, document, options);
    }

    private static void DrawLayer(
        SKCanvas canvas, MapDocument document, MapLayer layer, WorldBounds view, RenderOptions options)
    {
        var mask = layer.Mask is { Enabled: true } m && m.Tiles.TileCount > 0 ? m : null;
        var maskEdit = options.MaskEditLayerId == layer.Id && layer.Mask is not null;
        var needsGroup = mask is not null || layer.Opacity < 1.0;

        if (needsGroup)
        {
            using var groupPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(layer.Opacity * 255), 0, 255)),
            };

            canvas.SaveLayer(ToRect(view), groupPaint);
        }
        else
        {
            canvas.Save();
        }

        switch (layer)
        {
            case BaseColorLayer baseColor:
                using (var page = new SKPaint { Color = ParseColor(baseColor.Color), IsAntialias = false })
                {
                    canvas.DrawRect(0, 0, document.Canvas.Width, document.Canvas.Height, page);
                }

                break;

            case ObjectLayer objectLayer:
                DrawObjects(canvas, objectLayer, view, options);
                break;

            case IRasterContentLayer raster:
                DrawTiles(canvas, layer.Id, raster.ContentTiles, view, options, TileImageKind.Content);
                break;
        }

        if (options.Preview is { } preview && preview.LayerId == layer.Id && !preview.IsMask)
        {
            DrawStrokePreview(canvas, preview, view, options);
        }

        if (mask is not null)
        {
            ApplyMask(canvas, layer.Id, mask, view, options);
        }

        if (maskEdit)
        {
            DrawMaskOverlay(canvas, layer, view, options);
        }

        canvas.Restore();
    }

    private static void DrawObjects(
        SKCanvas canvas, ObjectLayer layer, WorldBounds view, RenderOptions options)
    {
        IReadOnlyList<SceneObject> candidates = options.VisibleWorld is null
            ? layer.Objects
            : ObjectSpatialIndex.For(layer).Query(view);

        if (options.Stats is { } stats)
        {
            stats.ObjectCandidates += candidates.Count;
        }

        var ordered = candidates.Count > 1 && candidates.Any(o => o.Sublayer != 0)
            ? [.. candidates.OrderBy(o => o.Sublayer)]
            : candidates;

        foreach (var sceneObject in ordered)
        {
            if (!sceneObject.Visible)
            {
                continue;
            }

            DrawObject(canvas, sceneObject, options);
            if (options.Stats is { } s)
            {
                s.ObjectsDrawn++;
            }
        }
    }

    private static void DrawTiles(
        SKCanvas canvas, LayerId layerId, TileMap tiles, WorldBounds view,
        RenderOptions options, TileImageKind kind)
    {
        if (tiles.TileCount == 0)
        {
            return;
        }

        // Below 1:1 the tiles are minified; mip sampling keeps a fully zoomed-out map from
        // shimmering and is what makes the deep-zoom pass cheap.
        var minified = options.ViewScale < 0.9;
        var sampling = minified
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        using var paint = new SKPaint { IsAntialias = false };
        var cache = options.TileCache;

        foreach (var key in tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
        {
            var pixels = tiles.TryGet(key);
            if (pixels is null)
            {
                continue;
            }

            var image = cache is not null
                ? cache.Get(layerId, key, kind, pixels)
                : SKImage.FromPixelCopy(
                    new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                    pixels);

            canvas.DrawImage(image, TileRect(key), sampling, paint);

            if (cache is null)
            {
                image.Dispose();
            }

            if (options.Stats is { } stats)
            {
                if (kind == TileImageKind.Content)
                {
                    stats.TilesDrawn++;
                }
                else
                {
                    stats.MaskTilesDrawn++;
                }

                if (minified)
                {
                    stats.MipmappedTiles++;
                }
            }
        }
    }

    /// <summary>
    /// Multiplies the group by the mask's coverage. Only the tiles the mask actually has are
    /// composited, so an area with no mask tile keeps its pixels - "missing tile = fully visible".
    /// </summary>
    private static void ApplyMask(
        SKCanvas canvas, LayerId layerId, LayerMask mask, WorldBounds view, RenderOptions options)
    {
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn, IsAntialias = false };
        var cache = options.TileCache;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        foreach (var key in mask.Tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
        {
            var coverage = mask.Tiles.TryGet(key);
            if (coverage is null)
            {
                continue;
            }

            var image = cache is not null
                ? cache.Get(layerId, key, TileImageKind.MaskAlpha, coverage)
                : BuildCoverageImage(coverage, TileImageKind.MaskAlpha);

            canvas.DrawImage(image, TileRect(key), sampling, paint);

            if (cache is null)
            {
                image.Dispose();
            }

            if (options.Stats is { } stats)
            {
                stats.MaskTilesDrawn++;
            }
        }
    }

    private static void DrawMaskOverlay(
        SKCanvas canvas, MapLayer layer, WorldBounds view, RenderOptions options)
    {
        var mask = layer.Mask!;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(110), IsAntialias = false };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var cache = options.TileCache;

        foreach (var key in mask.Tiles.KeysIntersecting(view.Left, view.Top, view.Right, view.Bottom))
        {
            var coverage = mask.Tiles.TryGet(key);
            if (coverage is null)
            {
                continue;
            }

            var image = cache is not null
                ? cache.Get(layer.Id, key, TileImageKind.MaskOverlay, coverage)
                : BuildCoverageImage(coverage, TileImageKind.MaskOverlay);

            canvas.DrawImage(image, TileRect(key), sampling, paint);

            if (cache is null)
            {
                image.Dispose();
            }
        }

        // The live part of the mask gesture, tinted the same red.
        if (options.Preview is { IsMask: true } preview && preview.LayerId == layer.Id)
        {
            DrawStrokePreview(canvas, preview, view, options);
        }
    }

    private static void DrawStrokePreview(
        SKCanvas canvas, StrokePreview preview, WorldBounds view, RenderOptions options)
    {
        var (r, g, b, a) = preview.IsMask
            ? ((byte)255, (byte)0, (byte)0, (byte)110)
            : ParseChannels(preview.Color);

        var alpha = Math.Clamp(preview.Opacity, 0, 1) * (a / 255.0);
        using var paint = new SKPaint
        {
            IsAntialias = false,
            BlendMode = preview.Eraser ? SKBlendMode.DstOut : SKBlendMode.SrcOver,
        };

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        foreach (var (key, coverage) in preview.Coverage)
        {
            var rect = TileRect(key);
            if (rect.Right < view.Left || rect.Left > view.Right
                || rect.Bottom < view.Top || rect.Top > view.Bottom)
            {
                continue;
            }

            // Preview tiles change every mouse move, so they are built fresh, never cached.
            var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
            for (var i = 0; i < coverage.Length; i++)
            {
                if (coverage[i] == 0)
                {
                    continue;
                }

                var value = coverage[i] / 255.0 * alpha;
                var offset = i * 4;
                pixels[offset] = (byte)Math.Round(r * value);
                pixels[offset + 1] = (byte)Math.Round(g * value);
                pixels[offset + 2] = (byte)Math.Round(b * value);
                pixels[offset + 3] = (byte)Math.Round(255 * value);
            }

            using var image = SKImage.FromPixelCopy(
                new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                pixels);

            canvas.DrawImage(image, rect, sampling, paint);
        }
    }

    private static SKImage BuildCoverageImage(byte[] coverage, TileImageKind kind)
    {
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var i = 0; i < coverage.Length; i++)
        {
            var offset = i * 4;
            if (kind == TileImageKind.MaskAlpha)
            {
                var v = coverage[i];
                pixels[offset] = v;
                pixels[offset + 1] = v;
                pixels[offset + 2] = v;
                pixels[offset + 3] = v;
            }
            else
            {
                var hidden = (byte)(255 - coverage[i]);
                pixels[offset] = hidden;
                pixels[offset + 3] = hidden;
            }
        }

        return SKImage.FromPixelCopy(
            new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Rgba8888, SKAlphaType.Premul),
            pixels);
    }

    private static void DrawObject(SKCanvas canvas, SceneObject sceneObject, RenderOptions options)
    {
        canvas.Save();
        ApplyRotation(canvas, sceneObject);

        var rect = new SKRect(
            (float)sceneObject.X,
            (float)sceneObject.Y,
            (float)(sceneObject.X + sceneObject.Width),
            (float)(sceneObject.Y + sceneObject.Height));

        var objectAlpha = (byte)Math.Clamp(Math.Round(sceneObject.ObjectOpacity * 255), 0, 255);

        if (sceneObject.Kind == SceneObjectKind.Stamp && sceneObject.Stamp is { } stamp)
        {
            DrawStamp(canvas, sceneObject, stamp, rect, objectAlpha, options);
            canvas.Restore();
            return;
        }

        using (var fill = new SKPaint
        {
            Color = ParseColor(sceneObject.FillColor).WithScaledAlpha(objectAlpha),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        })
        {
            DrawShape(canvas, sceneObject.Kind, rect, fill);
        }

        if (sceneObject.StrokeWidth > 0)
        {
            using var stroke = new SKPaint
            {
                Color = ParseColor(sceneObject.StrokeColor).WithScaledAlpha(objectAlpha),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)sceneObject.StrokeWidth,
            };

            DrawShape(canvas, sceneObject.Kind, rect, stroke);
        }

        canvas.Restore();
    }

    private static void DrawStamp(
        SKCanvas canvas, SceneObject sceneObject, StampStyle stamp, SKRect rect,
        byte objectAlpha, RenderOptions options)
    {
        var image = options.Stamps?.TryGetImage(stamp.AssetSha256);
        if (image is null)
        {
            // A linked asset that is not on this machine must be visible, not silently absent.
            if (options.Stats is { } stats)
            {
                stats.MissingAssets++;
            }

            using var placeholderFill = new SKPaint
            {
                Color = new SKColor(255, 90, 90, (byte)(objectAlpha / 4)),
                IsAntialias = true,
            };
            using var placeholderEdge = new SKPaint
            {
                Color = new SKColor(255, 90, 90, objectAlpha),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, rect.Width / 64),
                PathEffect = SKPathEffect.CreateDash([8, 6], 0),
            };

            canvas.DrawRect(rect, placeholderFill);
            canvas.DrawRect(rect, placeholderEdge);
            canvas.DrawLine(rect.Left, rect.Top, rect.Right, rect.Bottom, placeholderEdge);
            canvas.DrawLine(rect.Right, rect.Top, rect.Left, rect.Bottom, placeholderEdge);
            return;
        }

        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(objectAlpha),
            IsAntialias = true,
            BlendMode = ParseBlend(stamp.Blend),
            ColorFilter = BuildColorFilter(stamp),
        };

        SKImageFilter? filter = null;
        if (stamp.Blur > 0)
        {
            filter = SKImageFilter.CreateBlur((float)stamp.Blur, (float)stamp.Blur);
        }

        if (stamp.ShadowOpacity > 0)
        {
            var shadow = SKColors.Black.WithAlpha(
                (byte)Math.Clamp(Math.Round(stamp.ShadowOpacity * 255), 0, 255));
            filter = SKImageFilter.CreateDropShadow(
                (float)stamp.ShadowOffsetX, (float)stamp.ShadowOffsetY,
                (float)stamp.ShadowBlur, (float)stamp.ShadowBlur, shadow, filter);
        }

        paint.ImageFilter = filter;

        canvas.Save();
        if (stamp.FlipHorizontal || stamp.FlipVertical)
        {
            canvas.Translate(rect.MidX, rect.MidY);
            canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
            canvas.Translate(-rect.MidX, -rect.MidY);
        }

        canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();

        filter?.Dispose();
        paint.ColorFilter?.Dispose();
    }

    /// <summary>Hue / saturation / brightness / contrast as one colour matrix, or null when neutral.</summary>
    private static SKColorFilter? BuildColorFilter(StampStyle stamp)
    {
        if (stamp is { Hue: 0, Saturation: 0, Brightness: 0, Contrast: 0 })
        {
            return null;
        }

        var matrix = Identity();

        if (stamp.Hue != 0)
        {
            var radians = stamp.Hue * Math.PI / 180.0;
            var cos = (float)Math.Cos(radians);
            var sin = (float)Math.Sin(radians);
            matrix = Multiply(
            [
                0.213f + (cos * 0.787f) - (sin * 0.213f), 0.715f - (cos * 0.715f) - (sin * 0.715f),
                0.072f - (cos * 0.072f) + (sin * 0.928f), 0, 0,
                0.213f - (cos * 0.213f) + (sin * 0.143f), 0.715f + (cos * 0.285f) + (sin * 0.140f),
                0.072f - (cos * 0.072f) - (sin * 0.283f), 0, 0,
                0.213f - (cos * 0.213f) - (sin * 0.787f), 0.715f - (cos * 0.715f) + (sin * 0.715f),
                0.072f + (cos * 0.928f) + (sin * 0.072f), 0, 0,
                0, 0, 0, 1, 0,
            ], matrix);
        }

        if (stamp.Saturation != 0)
        {
            var s = (float)(1 + Math.Clamp(stamp.Saturation, -1, 1));
            var inv = 1 - s;
            var r = 0.213f * inv;
            var g = 0.715f * inv;
            var b = 0.072f * inv;
            matrix = Multiply(
            [
                r + s, g, b, 0, 0,
                r, g + s, b, 0, 0,
                r, g, b + s, 0, 0,
                0, 0, 0, 1, 0,
            ], matrix);
        }

        if (stamp.Contrast != 0)
        {
            var c = (float)(1 + Math.Clamp(stamp.Contrast, -1, 1));
            var t = 127.5f * (1 - c);
            matrix = Multiply([c, 0, 0, 0, t, 0, c, 0, 0, t, 0, 0, c, 0, t, 0, 0, 0, 1, 0], matrix);
        }

        if (stamp.Brightness != 0)
        {
            var offset = (float)(Math.Clamp(stamp.Brightness, -1, 1) * 255);
            matrix = Multiply(
                [1, 0, 0, 0, offset, 0, 1, 0, 0, offset, 0, 0, 1, 0, offset, 0, 0, 0, 1, 0], matrix);
        }

        return SKColorFilter.CreateColorMatrix(matrix);
    }

    private static float[] Identity() =>
        [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];

    private static float[] Multiply(float[] a, float[] b)
    {
        var result = new float[20];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                {
                    sum += a[(row * 5) + k] * b[(k * 5) + col];
                }

                if (col == 4)
                {
                    sum += a[(row * 5) + 4];
                }

                result[(row * 5) + col] = sum;
            }
        }

        return result;
    }

    private static SKBlendMode ParseBlend(string blend) => blend switch
    {
        "Multiply" => SKBlendMode.Multiply,
        "Screen" => SKBlendMode.Screen,
        "Overlay" => SKBlendMode.Overlay,
        "Darken" => SKBlendMode.Darken,
        "Lighten" => SKBlendMode.Lighten,
        "SoftLight" => SKBlendMode.SoftLight,
        "HardLight" => SKBlendMode.HardLight,
        "Difference" => SKBlendMode.Difference,
        "Add" => SKBlendMode.Plus,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>Blend modes offered in the stamp editor, in menu order.</summary>
    public static IReadOnlyList<string> BlendModes { get; } =
        ["Normal", "Multiply", "Screen", "Overlay", "Darken", "Lighten", "SoftLight", "HardLight",
         "Difference", "Add"];

    private static void DrawSelectionChrome(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        if (options.CoSelectedObjectIds is { Count: > 0 } coSelected)
        {
            foreach (var id in coSelected)
            {
                if (id != options.SelectedObjectId
                    && document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
                        .FirstOrDefault(o => o.Id == id) is { } member)
                {
                    DrawRing(canvas, member, Math.Max(options.ViewScale, 0.01));
                }
            }
        }

        if (options.SelectedObjectId is { } selectedId)
        {
            var selected = document.Layers.OfType<ObjectLayer>()
                .SelectMany(l => l.Objects)
                .FirstOrDefault(o => o.Id == selectedId);

            if (selected is not null)
            {
                DrawSelection(canvas, selected, Math.Max(options.ViewScale, 0.01));
            }
        }
    }

    private static void DrawRing(SKCanvas canvas, SceneObject sceneObject, double viewScale)
    {
        var px = (float)(1.0 / viewScale);
        canvas.Save();
        ApplyRotation(canvas, sceneObject);

        using var ring = new SKPaint
        {
            Color = SelectionColor.WithAlpha(150),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * px,
        };

        canvas.DrawRect(
            (float)sceneObject.X, (float)sceneObject.Y,
            (float)sceneObject.Width, (float)sceneObject.Height, ring);
        canvas.Restore();
    }

    /// <summary>
    /// The selection gizmo: ring, eight square resize handles and the round rotation handle on a
    /// stem above the top edge. All sizes are divided by the view scale so the gizmo keeps a
    /// constant on-screen size; positions must match <c>SelectionHandles.Compute</c>, which the
    /// input side uses for hit-testing.
    /// </summary>
    private static void DrawSelection(SKCanvas canvas, SceneObject selected, double viewScale)
    {
        var px = (float)(1.0 / viewScale);

        canvas.Save();
        ApplyRotation(canvas, selected);

        var left = (float)selected.X;
        var top = (float)selected.Y;
        var right = (float)(selected.X + selected.Width);
        var bottom = (float)(selected.Y + selected.Height);
        var centreX = (left + right) / 2;

        using (var ring = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * px,
        })
        {
            canvas.DrawRect(left, top, right - left, bottom - top, ring);

            // Stem to the rotation handle (28 screen px above the top edge, matching the VM).
            canvas.DrawLine(centreX, top, centreX, top - (28 * px), ring);
        }

        using var handleFill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var handleEdge = new SKPaint
        {
            Color = SelectionColor,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f * px,
        };

        var half = 4 * px;
        foreach (var (x, y) in new (float X, float Y)[]
                 {
                     (left, top), (centreX, top), (right, top), (right, (top + bottom) / 2),
                     (right, bottom), (centreX, bottom), (left, bottom), (left, (top + bottom) / 2),
                 })
        {
            var rect = new SKRect(x - half, y - half, x + half, y + half);
            canvas.DrawRect(rect, handleFill);
            canvas.DrawRect(rect, handleEdge);
        }

        // Round rotation handle.
        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleFill);
        canvas.DrawCircle(centreX, top - (28 * px), 5 * px, handleEdge);

        canvas.Restore();
    }

    private static void DrawShape(SKCanvas canvas, SceneObjectKind kind, SKRect rect, SKPaint paint)
    {
        if (kind == SceneObjectKind.Ellipse)
        {
            canvas.DrawOval(rect, paint);
        }
        else
        {
            canvas.DrawRect(rect, paint);
        }
    }

    private static void ApplyRotation(SKCanvas canvas, SceneObject sceneObject)
    {
        if (sceneObject.RotationDegrees != 0)
        {
            canvas.RotateDegrees(
                (float)sceneObject.RotationDegrees,
                (float)(sceneObject.X + (sceneObject.Width / 2)),
                (float)(sceneObject.Y + (sceneObject.Height / 2)));
        }
    }

    private static SKRect TileRect(TileKey key) => new(
        key.X * TileMap.TileSize,
        key.Y * TileMap.TileSize,
        (key.X + 1) * TileMap.TileSize,
        (key.Y + 1) * TileMap.TileSize);

    private static SKRect ToRect(WorldBounds bounds) => new(
        (float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);

    private static SKColor WithScaledAlpha(this SKColor color, byte layerAlpha) =>
        layerAlpha == 255 ? color : color.WithAlpha((byte)(color.Alpha * layerAlpha / 255));

    private static (byte R, byte G, byte B, byte A) ParseChannels(string color)
    {
        var c = ParseColor(color);
        return (c.Red, c.Green, c.Blue, c.Alpha);
    }

    /// <summary>Parses "#AARRGGBB" (or "#RRGGBB" as opaque). The domain stores colours as strings.</summary>
    internal static SKColor ParseColor(string color)
    {
        var span = color.AsSpan().TrimStart('#');
        var value = uint.Parse(span, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);

        if (span.Length == 6)
        {
            value |= 0xFF000000;
        }

        return new SKColor(
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF),
            (byte)((value >> 24) & 0xFF));
    }
}
