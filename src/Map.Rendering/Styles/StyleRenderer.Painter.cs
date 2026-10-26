using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

public static partial class StyleRenderer
{
    private static bool IsPainter(MapStylePreset preset) => preset is MapStylePreset.WatercolorBigShapes
        or MapStylePreset.UkiyoeSosakuHanga or MapStylePreset.PrintLetterpress or MapStylePreset.PrintTintedLithograph
        || OilShader.Applies(preset) || CharcoalShader.Applies(preset) || BlueprintShader.Applies(preset);

    // Both abstractions stay in the view cache: changing any of the four presets or its effects
    // only runs Shade. They use the same camera-follow path as the existing print/ink buffers.
    private sealed record PainterBuffers(SKImage WetBig, SKImage Big, SKImage Facets, SKImage Wide,
        SKImage WetFeatures, SKImage Features, SKImage Direction, SKImage Geo, SKImage RiverSoft) : IDisposable
    {
        public void Dispose()
        {
            WetBig.Dispose(); Big.Dispose(); Facets.Dispose(); Wide.Dispose(); WetFeatures.Dispose();
            Features.Dispose(); Direction.Dispose(); Geo.Dispose(); RiverSoft.Dispose();
        }
    }

    private static PainterBuffers BuildPainterBuffers(MapDocument bare, RenderOptions options, GRContext? gpu,
        SKMatrix matrix, int w, int h, float scale, WorldBounds? visible,
        Action<SKCanvas, MapDocument, RenderOptions> drawScene)
    {
        var parts = PrintPartsFor(bare);
        var auxiliary = options.ForStyleNames().ForStyleScene(visible);
        SKImage RenderPart(MapDocument d)
        {
            using var surface = NewSurface(gpu, w, h, SKColorType.Rgba8888)!;
            surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.SetMatrix(matrix);
            drawScene(surface.Canvas, d, auxiliary);
            return surface.Snapshot();
        }
        using var land = RenderPart(parts.Land);
        using var rivers = RenderPart(parts.Rivers);
        var geo = BuildCoastField(land, rivers, w, h, scale);
        var riverSoft = Blurred(rivers, gpu, 4 * scale)!;
        // Source maps already include their entire blur support. No per-frame Kuwahara/blur.
        var surfaces = Enumerable.Range(0, 7).Select(i => NewSurface(gpu, w, h,
            i >= 4 ? SKColorType.RgbaF16 : SKColorType.Rgba8888)!).ToArray();
        // The form maps are CPU images; drawn as they are, every stamp uploads them again (F16 ones
        // converted on the way). On the GPU each source's maps go up once per build.
        var uploaded = new Dictionary<SKImage, SKImage[]>(ReferenceEqualityComparer.Instance);
        SKImage[] MapsFor(SKImage source, PainterFormMaps.Set wet, PainterFormMaps.Set block)
        {
            if (uploaded.TryGetValue(source, out var known)) return known;
            SKImage[] maps = [wet.Big, block.Big, block.Facets, block.Wide, wet.Features, block.Features, block.Tensor];
            if (gpu is not null) maps = [.. maps.Select(m => m.ToTextureImage(gpu) ?? m)];
            return uploaded[source] = maps;
        }
        var drawn = 0;
        PrebuildFormMaps(parts.Mountains, options);
        try
        {
            foreach (var surface in surfaces)
            {
                surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.SetMatrix(matrix);
            }
            foreach (var layer in parts.Mountains.Layers.OfType<ObjectLayer>())
            {
                if (!layer.Visible || layer.Opacity <= 0) continue;
                // Include a stamp whose wet halo reaches the view even if its original bounds do not.
                // Spatial candidates expand by the largest style support of this layer.
                IEnumerable<SceneObject> candidates = layer.Objects;
                if (visible is { } v)
                {
                    var reach = layer.Objects.Count == 0 ? 0 : layer.Objects.Max(o => Math.Max(o.Width, o.Height)) * PainterFormMaps.Padding / PainterFormMaps.ReferenceWidth;
                    candidates = ObjectSpatialIndex.For(layer).Query(new WorldBounds(v.Left - reach, v.Top - reach, v.Right + reach, v.Bottom + reach));
                }
                foreach (var item in candidates.OrderBy(o => o.Sublayer))
                {
                    if (!item.Visible || item.ObjectOpacity <= 0 || !layer.StateFor(item.Sublayer).Visible
                        || item.Stamp is not { } stamp || options.Stamps?.TryGetImage(stamp.AssetSha256) is not { } source) continue;
                    var wet = PainterFormMaps.Get(source, true);
                    var block = PainterFormMaps.Get(source, false);
                    var maps = MapsFor(source, wet, block);
                    // Skia keeps merging the queued stamp draws into ever larger operations; 1500 queued
                    // stamps took minutes of CPU at the next flush, 64 at a time take milliseconds.
                    if (++drawn % 64 == 0) gpu?.Flush();
                    var rect = SKRect.Create((float)item.X, (float)item.Y, (float)item.Width, (float)item.Height);
                    var sx = rect.Width / PainterFormMaps.ReferenceWidth;
                    var sy = rect.Height / (block.Big.Height - 2 * PainterFormMaps.Padding);
                    var padded = SKRect.Create(-rect.Width / 2 - PainterFormMaps.Padding * sx,
                        -rect.Height / 2 - PainterFormMaps.Padding * sy, block.Big.Width * sx, block.Big.Height * sy);
                    for (var i = 0; i < surfaces.Length; i++)
                    {
                        var canvas = surfaces[i].Canvas;
                        canvas.Save(); canvas.Translate(rect.MidX, rect.MidY);
                        canvas.RotateDegrees((float)item.RotationDegrees);
                        canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
                        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(
                            Math.Round(255 * item.ObjectOpacity * layer.Opacity), 0, 255)) };
                        if (i == 6)
                        {
                            // Transform the asset's tensor direction into document axes. B carries this
                            // instance's brush scale; support alpha makes the top stamp own its fields.
                            using var shader = DirectionShader(maps[6], maps[3], sx, sy, padded, item);
                            paint.Shader = shader; canvas.DrawRect(padded, paint);
                        }
                        else canvas.DrawImage(maps[i], padded, new SKSamplingOptions(SKFilterMode.Linear), paint);
                        canvas.Restore();
                    }
                }
            }
            var images = surfaces.Select(s => s.Snapshot()).ToArray();
            return new(images[0], images[1], images[2], images[3], images[4], images[5], images[6], geo, riverSoft);
        }
        catch { geo.Dispose(); riverSoft.Dispose(); throw; }
        finally
        {
            foreach (var surface in surfaces) surface.Dispose();
            // Only the uploaded copies belong to this build; the CPU maps stay in PainterFormMaps.
            if (gpu is not null)
                foreach (var maps in uploaded.Values) foreach (var map in maps) if (map.IsTextureBacked) map.Dispose();
        }
    }

    /// <summary>
    /// The first painter frame used to build each asset's form maps one after another on the
    /// render thread (about 4 s per owner mountain). They are CPU-only and independent, so the
    /// missing ones are built side by side; the maps themselves are unchanged.
    /// </summary>
    private static void PrebuildFormMaps(MapDocument mountains, RenderOptions options)
    {
        if (options.Stamps is not { } stamps) return;
        var sources = mountains.Layers.OfType<ObjectLayer>().Where(l => l.Visible)
            .SelectMany(l => l.Objects).Select(o => o.Stamp?.AssetSha256).OfType<string>()
            .Distinct(StringComparer.Ordinal).Select(stamps.TryGetImage).OfType<SKImage>()
            .Where(s => !PainterFormMaps.IsBuilt(s)).ToArray();
        if (sources.Length < 2) return;
        Parallel.ForEach(sources.SelectMany(s => new[] { (s, true), (s, false) }),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            pair => PainterFormMaps.Get(pair.Item1, pair.Item2));
    }

    private static readonly Lazy<SKRuntimeEffect> DirectionEffect = new(() => PainterFormMaps.Compile("""
uniform shader tensor;
uniform shader wide;
uniform float2 offset;
uniform float2 toAsset;
uniform float2 flip;
uniform float2 stretch;
uniform float angle;
uniform float ms;
half4 main(float2 p) {
    float2 q = (p - offset) * toAsset;
    float4 J = tensor.eval(q);
    float a = 0.5 * atan(2.0 * (J.b - 0.5) * 2.0, J.r - J.g + 1e-6);
    float2 v = normalize(float2(cos(a),sin(a)) * flip / stretch);
    v = float2(v.x * cos(angle) - v.y * sin(angle), v.x * sin(angle) + v.y * cos(angle));
    float support = wide.eval(q).a;
    return half4(half3(float3(v * 0.5 + 0.5,ms) * support),half(support));
}
"""));

    private static SKShader DirectionShader(SKImage tensorMap, SKImage wideMap, float sx, float sy, SKRect rect, SceneObject item)
    {
        var b = new SKRuntimeShaderBuilder(DirectionEffect.Value);
        using var tensor = tensorMap.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, new SKSamplingOptions(SKFilterMode.Linear));
        using var wide = wideMap.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, new SKSamplingOptions(SKFilterMode.Linear));
        b.Children["tensor"] = tensor; b.Children["wide"] = wide;
        b.Uniforms["offset"] = new[] { rect.Left, rect.Top };
        b.Uniforms["toAsset"] = new[] { 1 / sx, 1 / sy };
        b.Uniforms["stretch"] = new[] { sx, sy };
        b.Uniforms["flip"] = new[] { item.Stamp!.FlipHorizontal ? -1f : 1f, item.Stamp.FlipVertical ? -1f : 1f };
        b.Uniforms["angle"] = (float)(item.RotationDegrees * Math.PI / 180);
        b.Uniforms["ms"] = sx;
        return b.Build();
    }
}
