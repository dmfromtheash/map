using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

public static partial class StyleRenderer
{
    private sealed record PrintParts(MapDocument Mountains, MapDocument Land, MapDocument Rivers);
    private static readonly ConditionalWeakTable<MapDocument, PrintParts> PrintDocuments = [];

    private sealed record PrintBuffers(SKImage Mountains, SKImage Geo, SKImage Tensor, SKImage Height,
        SKImage Lines, SKImage Faces) : IDisposable
    {
        public void Dispose()
        {
            Mountains.Dispose(); Geo.Dispose(); Tensor.Dispose(); Height.Dispose(); Lines.Dispose(); Faces.Dispose();
        }
    }

    private static bool IsPrint(MapStylePreset preset) => preset is MapStylePreset.OldMapBlaeu or MapStylePreset.EngravingAquatint;

    private static PrintParts PrintPartsFor(MapDocument bare) => PrintDocuments.GetValue(bare, static d =>
    {
        MapDocument Part(Func<MapLayer, MapLayer> transform) => d with
        {
            Layers = d.Layers.Select(transform).ToArray(), Lighting = null, Style = null,
        };
        var mountains = Part(l => l is ObjectLayer o
            ? o with { Rivers = [], PlacementPaths = [], PlacementAreas = [] }
            : l with { Visible = false });
        var rivers = Part(l => l is ObjectLayer o
            ? o with { Objects = [], Roads = [], PlacementPaths = [], PlacementAreas = [] }
            : l with { Visible = false });
        // CoastField in the selected prototypes thresholds the rendered land's alpha (including
        // its own edge), not a newly invented white fill or a second shoreline approximation.
        var land = Part(l => l is LandLayer ? l : l with { Visible = false });
        return new PrintParts(mountains, land, rivers);
    });

    private static PrintBuffers? BuildPrintBuffers(MapDocument bare, RenderOptions options, GRContext? gpu,
        SKMatrix matrix, int w, int h, float scale, WorldBounds? visible,
        Action<SKCanvas, MapDocument, RenderOptions> drawScene)
    {
        var parts = PrintPartsFor(bare);
        // These auxiliary draws must not populate the editor's scene/layer caches or consume its
        // stats. Their own immutable layer lists still reuse the renderer's spatial indexes.
        var auxiliary = options.ForStyleNames().ForStyleScene(visible);
        SKImage RenderPart(MapDocument d)
        {
            using var surface = NewSurface(gpu, w, h, SKColorType.Rgba8888)!;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.SetMatrix(matrix);
            drawScene(surface.Canvas, d, auxiliary);
            return surface.Snapshot();
        }
        var mountains = RenderPart(parts.Mountains);
        using var land = RenderPart(parts.Land);
        using var rivers = RenderPart(parts.Rivers);
        var geo = BuildCoastField(land, rivers, w, h, scale);
        using var white = NewSurface(gpu, w, h, SKColorType.Rgba8888)!;
        white.Canvas.Clear(SKColors.White); white.Canvas.DrawImage(mountains, 0, 0);
        using var onWhite = white.Snapshot();
        var tensor = Tensor(onWhite, gpu, Math.Min(5 * scale, MaxTensorSigma));
        var lines = Blurred(mountains, gpu, Math.Min(2 * scale, MaxSoftSigma));
        var faces = Blurred(mountains, gpu, Math.Min(8 * scale, MaxWideSigma));
        using var heightRaw = NewSurface(gpu, w, h, SKColorType.Rgba8888)!;
        heightRaw.Canvas.Clear(SKColors.Black);
        heightRaw.Canvas.SetMatrix(matrix);
        DrawPrintHeights(heightRaw.Canvas, parts.Mountains, options, visible);
        using var raw = heightRaw.Snapshot();
        var height = Blurred(raw, gpu, Math.Min(9 * scale, MaxWideSigma));
        if (tensor is not null && lines is not null && faces is not null && height is not null)
            return new PrintBuffers(mountains, geo, tensor, height, lines, faces);
        mountains.Dispose(); geo.Dispose(); tensor?.Dispose(); lines?.Dispose(); faces?.Dispose(); height?.Dispose();
        return null;
    }

    private static SKImage BuildCoastField(SKImage land, SKImage rivers, int w, int h, float scale)
    {
        using var landBitmap = SKBitmap.FromImage(land);
        using var riverBitmap = SKBitmap.FromImage(rivers);
        var landBytes = landBitmap.GetPixelSpan();
        var riverBytes = riverBitmap.GetPixelSpan();
        var coverage = new byte[w * h];
        for (var i = 0; i < coverage.Length; i++) coverage[i] = landBytes[i * 4 + 3];
        // Use the existing exact coast transform, once for the union, shared by both presets.
        var distance = CoastDistanceField.SignedDistance(coverage, w, h);
        using var geoBitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var geoBytes = geoBitmap.GetPixelSpan();
        for (var i = 0; i < distance.Length; i++)
        {
            var d = distance[i];
            // The mockup measured centre-to-centre; the production field measures to the half-pixel shore.
            var docDistance = (Math.Abs(d) + 0.5f) / scale;
            geoBytes[i * 4] = d > 0 ? (byte)Math.Min(255, docDistance) : (byte)0;
            geoBytes[i * 4 + 1] = d < 0 ? (byte)Math.Min(255, docDistance) : (byte)0;
            geoBytes[i * 4 + 2] = riverBytes[i * 4 + 3];
            geoBytes[i * 4 + 3] = 255;
        }
        return SKImage.FromBitmap(geoBitmap);
    }

    private static void DrawPrintHeights(SKCanvas canvas, MapDocument document, RenderOptions options, WorldBounds? visible)
    {
        var maps = new Dictionary<string, (SKImage Height, SKImage Source)>(StringComparer.Ordinal);
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Visible || layer.Opacity <= 0) continue;
            IEnumerable<SceneObject> candidates = visible is { } v ? ObjectSpatialIndex.For(layer).Query(v) : layer.Objects;
            foreach (var item in candidates.OrderBy(o => o.Sublayer))
            {
                if (!item.Visible || item.ObjectOpacity <= 0 || !layer.StateFor(item.Sublayer).Visible
                    || item.Stamp is not { } stamp) continue;
                if (!maps.TryGetValue(stamp.AssetSha256, out var map))
                {
                    if (options.Stamps?.TryGetImage(stamp.AssetSha256) is not { } image) continue;
                    maps[stamp.AssetSha256] = map = (PrintHeightMaps.Get(stamp.AssetSha256, image), image);
                }
                var (height, source) = map;
                var rect = new SKRect((float)item.X, (float)item.Y, (float)(item.X + item.Width), (float)(item.Y + item.Height));
                using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(
                    (byte)Math.Clamp(Math.Round(255 * item.ObjectOpacity * layer.Opacity), 0, 255)) };
                canvas.Save();
                canvas.Translate(rect.MidX, rect.MidY);
                canvas.RotateDegrees((float)item.RotationDegrees);
                canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
                var centred = SKRect.Create(-rect.Width / 2, -rect.Height / 2, rect.Width, rect.Height);
                // The source alpha masks the black surroundings while retaining the top stamp's ordering.
                // Height is composited through the asset silhouette by the small shader below.
                using var shader = HeightShader(height, source, centred);
                paint.Shader = shader;
                canvas.DrawRect(centred, paint);
                canvas.Restore();
            }
        }
    }

    private static readonly Lazy<SKRuntimeEffect> HeightEffect = new(() => SKRuntimeEffect.CreateShader("""
uniform shader height;
uniform shader mask;
uniform float2 offset;
uniform float2 toHeight;
uniform float2 toMask;
uniform float width;
half4 main(float2 p) {
    float a = mask.eval((p - offset) * toMask).a;
    float4 field = height.eval((p - offset) * toHeight);
    float fromTop = 1.0 - clamp(field.r * width / 320.0, 0.0, 1.0);
    float inward = clamp(field.g * width / 140.0, 0.0, 1.0);
    float h = (0.72 * fromTop + 0.28 * pow(inward, 0.7)) * field.b;
    return half4(half3(h * a), half(a));
}
""", out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader HeightShader(SKImage height, SKImage source, SKRect rect)
    {
        var b = new SKRuntimeShaderBuilder(HeightEffect.Value);
        using var hs = height.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var ms = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        b.Children["height"] = hs; b.Children["mask"] = ms;
        b.Uniforms["offset"] = new[] { rect.Left, rect.Top };
        b.Uniforms["toHeight"] = new[] { height.Width / rect.Width, height.Height / rect.Height };
        b.Uniforms["toMask"] = new[] { source.Width / rect.Width, source.Height / rect.Height };
        b.Uniforms["width"] = rect.Width;
        return b.Build();
    }
}
