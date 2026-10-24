using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// The style of a map (R09, DEC-059). The map is drawn without its names - lit when the Свет
/// section is on - into an off-screen image a little wider than the frame (the reach of the
/// medium's blurs, so export tiles and the canvas edge get the same picture as their middle);
/// the medium's buffers are made from it (structure tensor, soft and wide blurs) and one SkSL pass
/// paints the frame. The names are drawn over it as they are, sharp. The canvas keeps the buffers
/// of its view while the map and the view stay (a style slider then costs only the paint pass)
/// and the painted frame while the style stays too; the export paints every tile.
/// </summary>
public static class StyleRenderer
{
    /// <summary>The blurs stop growing past these radii (device pixels) when zoomed far in: bounded cost and reach.</summary>
    private const float MaxSoftSigma = 24, MaxWideSigma = 64, MaxTensorSigma = 40;

    internal static bool Applies(MapDocument document, RenderOptions options) =>
        document.Style is { Enabled: true, Strength: > 0 } && !options.DisableStyle;

    internal static void Render(SKCanvas canvas, MapDocument document, RenderOptions options,
        Action<SKCanvas, MapDocument, RenderOptions> drawScene, Action<SKCanvas, MapDocument, RenderOptions> drawNames)
    {
        var style = document.Style!.Normalized();
        var device = canvas.DeviceClipBounds;
        var m = canvas.TotalMatrix;
        // An export rounds its width and height separately: scales a hair apart are one view.
        if (device.Width <= 0 || device.Height <= 0 || m.SkewX != 0 || m.SkewY != 0 || m.ScaleX <= 0 || m.ScaleY <= 0
            || Math.Abs(m.ScaleX - m.ScaleY) > 0.02 * m.ScaleX || m.Persp0 != 0 || m.Persp1 != 0)
        {
            drawScene(canvas, document with { Style = null }, options);
            return;
        }

        var (bare, names) = Split(document);
        var gpu = options.IsExport ? null : options.GpuContext;
        var key = KeyFor(bare, options, gpu, m, device);
        var measure = StyleDiagnostics.MeasureStages;
        var sample = new StyleFrameSample { Width = device.Width, Height = device.Height };
        SKImage? painted = null;
        if (key is not null && Kept is { } kept && kept.Key == key)
        {
            if (kept.Style == style && kept.Painted is { } reuse) { painted = reuse; sample = sample with { Reused = true }; }
            else
            {
                // Only the style changed (a slider, a switch): the buffers of the view stay, the paint pass runs again.
                var started = measure ? Stage(gpu) : 0;
                painted = Shade(kept.Buffers, style, gpu, device);
                if (measure) sample = sample with { ShaderMs = Elapsed(gpu, started) };
                if (painted is not null) { kept.Painted?.Dispose(); kept.Painted = painted; kept.Style = style; }
            }
        }
        else
        {
            if (gpu is not null) DropKept();
            var buffers = Buffers(bare, options, gpu, m, device, drawScene, measure, ref sample);
            if (buffers is not null)
            {
                var started = measure ? Stage(gpu) : 0;
                painted = Shade(buffers, style, gpu, device);
                if (measure) sample = sample with { ShaderMs = Elapsed(gpu, started) };
                // A frame still waiting for refined terrain, detail tiles or hex chunks is drawn once and not kept.
                if (painted is not null && key is not null
                    && options.Stats is { TerrainRefinementsPending: 0, DetailTilesPending: 0, HexChunksPending: 0 })
                    Kept = new KeptFrame(key, buffers) { Style = style, Painted = painted };
                else buffers.Dispose();
            }
        }
        if (painted is null) { drawScene(canvas, document with { Style = null }, options); return; }

        var composite = measure ? Stage(gpu) : 0;
        try
        {
            canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawImage(painted, SKRect.Create(device.Left, device.Top, device.Width, device.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.Restore();
            if (measure) StyleDiagnostics.Samples.Enqueue(sample with { CompositeMs = Elapsed(gpu, composite) });
        }
        finally
        {
            if (!ReferenceEquals(painted, Kept?.Painted)) painted.Dispose();
        }
        if (names is not null) drawNames(canvas, names, options.ForStyleNames());
    }

    /// <summary>The map of one view and the medium's buffers made from it, in the padded raster.</summary>
    private sealed record FrameBuffers(SKImage Scene, SKImage Tensor, SKImage Soft, SKImage Wide, int Margin,
        SKPoint Origin, SKPoint DocPerPx, float PxPerDoc) : IDisposable
    {
        public void Dispose() { Scene.Dispose(); Tensor.Dispose(); Soft.Dispose(); Wide.Dispose(); }
    }

    /// <summary>The buffers of the canvas's last view and its painted frame (render thread only).</summary>
    private sealed record KeptFrame(FrameKey Key, FrameBuffers Buffers)
    {
        public MapStyle? Style { get; set; }
        public SKImage? Painted { get; set; }
    }

    /// <summary>What the buffers depend on (the style itself is not part of it).</summary>
    private sealed record FrameKey(MapDocument Bare, SKMatrix Matrix, SKRectI Device, GRContext Gpu,
        IStampImageSource? Stamps, object? HexCursor, object? HexOverlay, int? HexLevel, int? HexGhostLevel,
        HexPresentation Presentation, bool HexPlayer);

    private static KeptFrame? Kept;

    /// <summary>Releases the kept buffers and frame (style off, the view closed, the map closed).</summary>
    public static void DropKept()
    {
        var kept = Kept;
        Kept = null;
        if (kept is null) return;
        kept.Buffers.Dispose();
        kept.Painted?.Dispose();
    }

    /// <summary>
    /// The key of a view whose buffers may be kept, or null when they are made afresh: the export, a gesture, a moving
    /// camera, a living hex world, a lit map (the Свет section keeps its own frame; a torch flickers every frame).
    /// </summary>
    private static FrameKey? KeyFor(MapDocument bare, RenderOptions options, GRContext? gpu, SKMatrix m, SKRectI device)
    {
        if (gpu is null || options.IsExport || options.Stats is null || options.FastTerrain || options.FastPreview
            || options.LodTier != RenderLodTier.Exact || options.Preview is not null || options.MaskEditLayerId is not null
            || options.ObjectOverrides is { Count: > 0 } || options.RiverOverrides is { Count: > 0 } || options.RoadOverrides is { Count: > 0 }
            || options.PlacementPreviewObjects is { Count: > 0 }
            // The canvas asks for ambient frames whenever its window is alive; only a hex world actually changes in them.
            || (options.HexAmbient && bare.Layers.Any(l => l is HexLayer))
            || options.HexGestureLive || options.HexSettles is not null
            || Lighting.LightingRenderer.Applies(bare, options))
            return null;
        return new FrameKey(bare, m, device, gpu, options.Stamps, options.HexCursor, options.HexOverlay,
            options.HexLevel, options.HexGhostLevel, options.HexPresentation, options.HexPlayerView);
    }

    private static FrameBuffers? Buffers(MapDocument bare, RenderOptions options, GRContext? gpu, SKMatrix m, SKRectI device,
        Action<SKCanvas, MapDocument, RenderOptions> drawScene, bool measure, ref StyleFrameSample sample)
    {
        var stage = measure ? Stage(gpu) : 0;
        // A print page (PDF, measured in points) is painted at the raster resolution asked for, not one pixel per point.
        var raster = (float)Math.Clamp(options.StyleRasterScale, 1, 8);
        float sx = m.ScaleX * raster, sy = m.ScaleY * raster;
        var scale = (sx + sy) / 2;
        var (soft, wide, tensorSigma) = Sigmas(scale);
        // The widest reach: three sigmas of the widest blur, the shifted samples of the bleed and the contour.
        var margin = (int)Math.Ceiling(3 * Math.Max(wide, tensorSigma + 1) + InkShader.SampleReachDocumentPixels * scale) + 4;
        int w = (int)Math.Ceiling(device.Width * raster) + 2 * margin, h = (int)Math.Ceiling(device.Height * raster) + 2 * margin;
        // Padded raster pixel (0,0) is device pixel (Left, Top) less the margin, at the raster scale.
        var toPadded = SKMatrix.Concat(SKMatrix.CreateTranslation(margin, margin),
            SKMatrix.Concat(SKMatrix.CreateScale(raster, raster), SKMatrix.CreateTranslation(-device.Left, -device.Top)));
        var paddedMatrix = SKMatrix.Concat(toPadded, m);
        WorldBounds? visible = options.VisibleWorld is { } v
            ? new WorldBounds(v.Left - margin / sx, v.Top - margin / sy, v.Right + margin / sx, v.Bottom + margin / sy)
            : null;

        using var sceneSurface = NewSurface(gpu, w, h, SKColorType.Rgba8888);
        if (sceneSurface is null) return null;
        sceneSurface.Canvas.Clear(SKColors.Transparent);
        sceneSurface.Canvas.SetMatrix(paddedMatrix);
        drawScene(sceneSurface.Canvas, bare, options.ForStyleScene(visible));
        var scene = sceneSurface.Snapshot();
        if (measure) { sample = sample with { SceneMs = Elapsed(gpu, stage) }; stage = Stage(gpu); }

        var tensor = Tensor(scene, gpu, tensorSigma);
        if (measure) { sample = sample with { TensorMs = Elapsed(gpu, stage) }; stage = Stage(gpu); }
        var softImage = Blurred(scene, gpu, soft);
        if (measure) { sample = sample with { SoftMs = Elapsed(gpu, stage) }; stage = Stage(gpu); }
        var wideImage = Blurred(scene, gpu, wide);
        if (measure) sample = sample with { WideMs = Elapsed(gpu, stage) };
        if (tensor is null || softImage is null || wideImage is null)
        {
            scene.Dispose(); tensor?.Dispose(); softImage?.Dispose(); wideImage?.Dispose();
            return null;
        }
        // The document point of the padded raster's top-left pixel, and document units per raster pixel on each axis.
        var origin = new SKPoint(-paddedMatrix.TransX / paddedMatrix.ScaleX, -paddedMatrix.TransY / paddedMatrix.ScaleY);
        return new FrameBuffers(scene, tensor, softImage, wideImage, margin, origin,
            new SKPoint(1 / paddedMatrix.ScaleX, 1 / paddedMatrix.ScaleY), scale);
    }

    /// <summary>The medium's blur radii at a view scale, bounded when zoomed far in.</summary>
    private static (float Soft, float Wide, float Tensor) Sigmas(float scale) =>
        (Math.Min(InkShader.SoftSigma * scale, MaxSoftSigma), Math.Min(InkShader.WideSigma * scale, MaxWideSigma),
         Math.Min(InkShader.TensorSigma * scale, MaxTensorSigma));

    /// <summary>The paint pass over kept or fresh buffers: the frame at its raster size.</summary>
    private static SKImage? Shade(FrameBuffers buffers, MapStyle style, GRContext? gpu, SKRectI device)
    {
        var w = buffers.Scene.Width - 2 * buffers.Margin;
        var h = buffers.Scene.Height - 2 * buffers.Margin;
        using var shader = InkShader.Build(buffers.Scene, buffers.Tensor, buffers.Soft, buffers.Wide, buffers.Origin,
            buffers.DocPerPx, buffers.PxPerDoc, style);
        using var output = NewSurface(gpu, w, h, SKColorType.Rgba8888);
        if (output is null) return null;
        output.Canvas.Clear(SKColors.Transparent);
        output.Canvas.Translate(-buffers.Margin, -buffers.Margin);
        using (var paint = new SKPaint { Shader = shader })
            output.Canvas.DrawRect(SKRect.Create(buffers.Margin, buffers.Margin, w, h), paint);
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

    /// <summary>Diagnostics: finish the GPU work recorded so far, then start a clock.</summary>
    private static long Stage(GRContext? gpu)
    {
        gpu?.Flush(); gpu?.Submit(synchronous: true);
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static double Elapsed(GRContext? gpu, long started)
    {
        gpu?.Flush(); gpu?.Submit(synchronous: true);
        return System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
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
    private static readonly ConditionalWeakTable<MapDocument, Tuple<MapDocument, MapDocument?>> SplitByDocument = [];

    /// <summary>
    /// The map without its texts (the style paints it) and its texts alone (drawn sharp on top). Kept per document
    /// (a gesture's frames reuse the same documents and so their revision indexes), per layer list and per layer
    /// (an unchanged layer keeps the very same object list and its dense tiles stay valid). A text layer clipped to
    /// the layer under it stays whole in the painted map: its clip belongs to that layer.
    /// </summary>
    public static (MapDocument Bare, MapDocument? Names) Split(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var split = SplitByDocument.GetValue(document, static d =>
        {
            var parts = PartsByLayers.GetValue(d.Layers, static layers =>
            {
                var bare = new List<MapLayer>(layers.Count);
                var names = new List<MapLayer>();
                foreach (var layer in layers)
                {
                    if (layer is ObjectLayer objects && !objects.Effects.ClippingEnabled
                        && objects.Objects.Any(o => o.Kind == SceneObjectKind.Text))
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
            return Tuple.Create(d with { Layers = parts.Bare, Style = null },
                parts.Names is { } n ? d with { Layers = n, Style = null, Lighting = null } : null);
        });
        return (split.Item1, split.Item2);
    }
}
