using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// The style of a map (R09, DEC-059). The map is drawn without its names - lit when the Свет
/// section is on - into an off-screen image a little wider than the frame (the reach of the
/// medium's blurs, so export tiles and the canvas edge get the same picture as their middle);
/// the medium's buffers are made from it (structure tensor, soft and wide blurs) and one SkSL pass
/// paints the frame. The names are drawn over it as they are, sharp. The canvas keeps the painted
/// frame of its view while neither the map nor the view changes; the export paints every tile.
/// </summary>
public static class StyleRenderer
{
    internal static bool Applies(MapDocument document, RenderOptions options) =>
        document.Style is { Enabled: true, Strength: > 0 } && !options.DisableStyle;

    internal static void Render(SKCanvas canvas, MapDocument document, RenderOptions options,
        Action<SKCanvas, MapDocument, RenderOptions> drawScene, Action<SKCanvas, MapDocument, RenderOptions> drawNames)
    {
        var style = document.Style!.Normalized();
        var device = canvas.DeviceClipBounds;
        var m = canvas.TotalMatrix;
        if (device.Width <= 0 || device.Height <= 0 || m.SkewX != 0 || m.SkewY != 0 || m.ScaleX <= 0
            || Math.Abs(m.ScaleX - m.ScaleY) > 1e-4 * m.ScaleX || m.Persp0 != 0 || m.Persp1 != 0)
        {
            drawScene(canvas, document with { Style = null }, options);
            return;
        }

        var (bare, names) = Split(document);
        var gpu = options.IsExport ? null : options.GpuContext;
        var key = KeyFor(bare, style, options, gpu, m, device);
        SKImage? painted = null;
        if (key is not null && Kept is { } kept && kept.Key == key)
            painted = kept.Image;
        else
        {
            if (gpu is not null) DropKept();
            painted = Paint(bare, style, options, gpu, m, device, drawScene);
            if (painted is null) { drawScene(canvas, document with { Style = null }, options); return; }
            // A frame still waiting for refined terrain or detail tiles is drawn once and not kept.
            if (key is not null && options.Stats is { TerrainRefinementsPending: 0, DetailTilesPending: 0 })
                Kept = new KeptFrame(key, painted);
        }

        try
        {
            canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawImage(painted, device.Left, device.Top);
            canvas.Restore();
        }
        finally
        {
            if (!ReferenceEquals(painted, Kept?.Image)) painted.Dispose();
        }
        if (names is not null) drawNames(canvas, names, options.ForStyleNames());
    }

    /// <summary>The painted frame of the canvas's last view (render thread only).</summary>
    private sealed record KeptFrame(FrameKey Key, SKImage Image);

    /// <summary>What the painted frame depends on.</summary>
    private sealed record FrameKey(MapDocument Bare, MapStyle Style, SKMatrix Matrix, SKRectI Device, GRContext Gpu,
        IStampImageSource? Stamps, object? HexCursor, object? HexOverlay, int? HexLevel, int? HexGhostLevel,
        HexPresentation Presentation, bool HexPlayer);

    private static KeptFrame? Kept;

    internal static void DropKept()
    {
        var kept = Kept;
        Kept = null;
        kept?.Image.Dispose();
    }

    /// <summary>
    /// The key of a frame that may be kept, or null when it is painted afresh: the export, a gesture,
    /// a moving camera, a living hex world, a lit map (the Свет section keeps its own frame, and a
    /// flickering torch changes the picture every frame).
    /// </summary>
    private static FrameKey? KeyFor(MapDocument bare, MapStyle style, RenderOptions options, GRContext? gpu, SKMatrix m, SKRectI device)
    {
        if (gpu is null || options.IsExport || options.Stats is null || options.FastTerrain || options.FastPreview
            || options.LodTier != RenderLodTier.Exact || options.Preview is not null || options.MaskEditLayerId is not null
            || options.ObjectOverrides is { Count: > 0 } || options.RiverOverrides is { Count: > 0 } || options.RoadOverrides is { Count: > 0 }
            || options.PlacementPreviewObjects is { Count: > 0 }
            || options.HexAmbient || options.HexGestureLive || options.HexSettles is not null
            || Lighting.LightingRenderer.Applies(bare, options))
            return null;
        return new FrameKey(bare, style, m, device, gpu, options.Stamps, options.HexCursor, options.HexOverlay,
            options.HexLevel, options.HexGhostLevel, options.HexPresentation, options.HexPlayerView);
    }

    private static SKImage? Paint(MapDocument bare, MapStyle style, RenderOptions options, GRContext? gpu, SKMatrix m, SKRectI device,
        Action<SKCanvas, MapDocument, RenderOptions> drawScene)
    {
        var scale = m.ScaleX;
        // The widest filter reaches this far: the map is drawn that much wider on every side.
        var margin = (int)Math.Ceiling(Math.Min(InkShader.ReachDocumentPixels * scale, 256)) + 4;
        int w = device.Width + 2 * margin, h = device.Height + 2 * margin;
        var left = device.Left - margin; var top = device.Top - margin;
        WorldBounds? visible = options.VisibleWorld is { } v
            ? new WorldBounds(v.Left - margin / scale, v.Top - margin / scale, v.Right + margin / scale, v.Bottom + margin / scale)
            : null;

        using var sceneSurface = NewSurface(gpu, w, h, SKColorType.Rgba8888);
        if (sceneSurface is null) return null;
        sceneSurface.Canvas.Clear(SKColors.Transparent);
        sceneSurface.Canvas.SetMatrix(SKMatrix.Concat(SKMatrix.CreateTranslation(-left, -top), m));
        drawScene(sceneSurface.Canvas, bare, options.ForStyleScene(visible));
        using var scene = sceneSurface.Snapshot();

        using var tensor = Tensor(scene, gpu, InkShader.TensorSigma * scale);
        using var soft = Blurred(scene, gpu, InkShader.SoftSigma * scale);
        using var wide = Blurred(scene, gpu, InkShader.WideSigma * scale);
        if (tensor is null || soft is null || wide is null) return null;

        var origin = new SKPoint((left - m.TransX) / scale, (top - m.TransY) / scale);
        using var shader = InkShader.Build(scene, tensor, soft, wide, origin, scale, style);
        using var output = NewSurface(gpu, device.Width, device.Height, SKColorType.Rgba8888);
        if (output is null) return null;
        output.Canvas.Clear(SKColors.Transparent);
        output.Canvas.Translate(-margin, -margin);
        using (var paint = new SKPaint { Shader = shader })
            output.Canvas.DrawRect(SKRect.Create(margin, margin, device.Width, device.Height), paint);
        return output.Snapshot();
    }

    private static SKImage? Blurred(SKImage image, GRContext? gpu, float sigma)
    {
        using var surface = NewSurface(gpu, image.Width, image.Height, SKColorType.Rgba8888);
        if (surface is null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        using var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = blur };
        surface.Canvas.DrawImage(image, 0, 0, paint);
        return surface.Snapshot();
    }

    /// <summary>The smoothed structure tensor, in half floats (squared gradients are too small for bytes).</summary>
    private static SKImage? Tensor(SKImage scene, GRContext? gpu, float sigma)
    {
        using var raw = NewSurface(gpu, scene.Width, scene.Height, SKColorType.RgbaF16);
        if (raw is null) return null;
        using (var shader = InkShader.TensorShader(scene))
        using (var paint = new SKPaint { Shader = shader })
            raw.Canvas.DrawRect(0, 0, scene.Width, scene.Height, paint);
        using var rawImage = raw.Snapshot();
        using var smooth = NewSurface(gpu, scene.Width, scene.Height, SKColorType.RgbaF16);
        if (smooth is null) return null;
        using var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var blurPaint = new SKPaint { ImageFilter = blur };
        smooth.Canvas.DrawImage(rawImage, 0, 0, blurPaint);
        return smooth.Snapshot();
    }

    private static SKSurface? NewSurface(GRContext? gpu, int w, int h, SKColorType type)
    {
        var info = new SKImageInfo(w, h, type, SKAlphaType.Premul);
        return gpu is null ? SKSurface.Create(info) : SKSurface.Create(gpu, false, info) ?? SKSurface.Create(info);
    }

    // ---------------------------------------------------------------- the map without names, the names alone

    private sealed record Parts(IReadOnlyList<MapLayer> Bare, IReadOnlyList<MapLayer>? Names);

    private static readonly ConditionalWeakTable<IReadOnlyList<MapLayer>, Parts> PartsByLayers = [];
    private static readonly ConditionalWeakTable<ObjectLayer, ObjectLayer> BareLayers = [];
    private static readonly ConditionalWeakTable<ObjectLayer, ObjectLayer> NameLayers = [];

    /// <summary>
    /// The map without its texts (the style paints it) and its texts alone (drawn sharp on top).
    /// Both are kept per layer list and per layer, so an unchanged layer keeps the very same
    /// object list and its dense tiles stay valid.
    /// </summary>
    public static (MapDocument Bare, MapDocument? Names) Split(MapDocument document)
    {
        var parts = PartsByLayers.GetValue(document.Layers, static layers =>
        {
            var bare = new List<MapLayer>(layers.Count);
            var names = new List<MapLayer>();
            foreach (var layer in layers)
            {
                if (layer is ObjectLayer objects && objects.Objects.Any(o => o.Kind == SceneObjectKind.Text))
                {
                    bare.Add(BareLayers.GetValue(objects, static l => l with { Objects = [.. l.Objects.Where(o => o.Kind != SceneObjectKind.Text)] }));
                    names.Add(NameLayers.GetValue(objects, static l => l with
                    {
                        Objects = [.. l.Objects.Where(o => o.Kind == SceneObjectKind.Text)],
                        Rivers = [], Roads = [], PlacementPaths = [], PlacementAreas = [], SurfaceOrder = [],
                    }));
                }
                else bare.Add(layer);
            }
            // The names keep the base layer first (hidden): a document always starts with it.
            if (names.Count > 0 && layers[0] is BaseColorLayer page) names.Insert(0, page with { Visible = false });
            return new Parts(bare, names.Count > 0 ? names : null);
        });
        var bareDocument = document with { Layers = parts.Bare, Style = null };
        return (bareDocument, parts.Names is { } n ? document with { Layers = n, Style = null, Lighting = null } : null);
    }
}
