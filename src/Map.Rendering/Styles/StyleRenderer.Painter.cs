using System.Runtime.CompilerServices;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

public static partial class StyleRenderer
{
    private static bool IsPainter(MapStylePreset preset) => preset is MapStylePreset.WatercolorBigShapes
        or MapStylePreset.UkiyoeSosakuHanga or MapStylePreset.PrintLetterpress or MapStylePreset.PrintTintedLithograph
        || OilShader.Applies(preset) || CharcoalShader.Applies(preset) || BlueprintShader.Applies(preset)
        || MoonlightShader.Applies(preset) || WinterShader.Applies(preset) || PixelShader.Applies(preset);

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
        var partsStarted = StyleDiagnostics.StageStart();
        var parts = PrintPartsFor(bare);
        StyleDiagnostics.StageEnd("parts", partsStarted);
        var auxiliary = options.ForStyleNames().ForStyleScene(visible);
        SKImage RenderPart(MapDocument d)
        {
            using var surface = NewSurface(gpu, w, h, SKColorType.Rgba8888)!;
            surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.SetMatrix(matrix);
            drawScene(surface.Canvas, d, auxiliary);
            return surface.Snapshot();
        }
        var auxStarted = StyleDiagnostics.StageStart();
        using var land = RenderPart(parts.Land);
        using var rivers = RenderPart(parts.Rivers);
        var geo = BuildCoastField(land, rivers, w, h, scale);
        var riverSoft = Blurred(rivers, gpu, 4 * scale)!;
        StyleDiagnostics.StageEnd("land-rivers-coast", auxStarted);
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
        try
        {
            foreach (var surface in surfaces)
            {
                surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.SetMatrix(matrix);
            }
            // The stamps in drawing order, then one buffer at a time (R09b): consecutive draws of one form map into one
            // target batch on the GPU, where interleaving the seven targets split every stamp into seven passes. Each
            // buffer still receives the same draws in the same order.
            var gatherStarted = StyleDiagnostics.StageStart();
            var visibleStamps = new List<(SceneObject Item, StampStyle Stamp, SKImage Source, byte Alpha)>();
            for (var index = 0; index < parts.Mountains.Layers.Count; index++)
            {
                if (parts.Mountains.Layers[index] is not ObjectLayer layer || !layer.Visible || layer.Opacity <= 0) continue;
                // Include a stamp whose wet halo reaches the view even if its original bounds do not.
                // Spatial candidates expand by the largest style support of this layer.
                IEnumerable<SceneObject> candidates = layer.Objects;
                if (visible is { } v)
                {
                    var haloStarted = StyleDiagnostics.StageStart();
                    // The largest side of the layer's objects, kept per object list and carried through its edits (the
                    // bare record at the same place holds the edit's link; both records share the list).
                    var reach = layer.Objects.Count == 0 ? 0
                        : LargestSide(bare.Layers[index] is ObjectLayer linked && ReferenceEquals(linked.Objects, layer.Objects) ? linked : layer)
                            * PainterFormMaps.Padding / PainterFormMaps.ReferenceWidth;
                    StyleDiagnostics.StageEnd("halo", haloStarted);
                    candidates = ObjectSpatialIndex.For(layer).Query(new WorldBounds(v.Left - reach, v.Top - reach, v.Right + reach, v.Bottom + reach));
                }
                foreach (var item in candidates.OrderBy(o => o.Sublayer))
                {
                    if (!item.Visible || item.ObjectOpacity <= 0 || !layer.StateFor(item.Sublayer).Visible
                        || item.Stamp is not { } stamp || options.Stamps?.TryGetImage(stamp.AssetSha256) is not { } source) continue;
                    visibleStamps.Add((item, stamp, source, (byte)Math.Clamp(Math.Round(255 * item.ObjectOpacity * layer.Opacity), 0, 255)));
                }
            }
            // The form maps of the pictures this frame draws, the missing ones side by side (before: every picture of
            // the map, found by walking all its objects on each build). The maps themselves are the same.
            var formMapsStarted = StyleDiagnostics.StageStart();
            PrebuildFormMaps(visibleStamps.Select(d => d.Source));
            StyleDiagnostics.StageEnd("form-maps", formMapsStarted);
            var stamps = new List<(SceneObject Item, StampStyle Stamp, SKImage[] Maps, float Sx, float Sy, SKPoint Mid, SKRect Padded, byte Alpha)>(visibleStamps.Count);
            foreach (var (item, stamp, source, alpha) in visibleStamps)
            {
                var wet = PainterFormMaps.Get(source, true);
                var block = PainterFormMaps.Get(source, false);
                var maps = MapsFor(source, wet, block);
                var rect = SKRect.Create((float)item.X, (float)item.Y, (float)item.Width, (float)item.Height);
                var sx = rect.Width / PainterFormMaps.ReferenceWidth;
                var sy = rect.Height / (block.Big.Height - 2 * PainterFormMaps.Padding);
                var padded = SKRect.Create(-rect.Width / 2 - PainterFormMaps.Padding * sx,
                    -rect.Height / 2 - PainterFormMaps.Padding * sy, block.Big.Width * sx, block.Big.Height * sy);
                stamps.Add((item, stamp, maps, sx, sy, new SKPoint(rect.MidX, rect.MidY), padded, alpha));
            }
            StyleDiagnostics.StageEnd("gather", gatherStarted);
            var passesStarted = StyleDiagnostics.StageStart();
            var sampling = new SKSamplingOptions(SKFilterMode.Linear);
            using var paint = new SKPaint();
            for (var i = 0; i < surfaces.Length; i++)
            {
                var canvas = surfaces[i].Canvas;
                var drawn = 0;
                foreach (var (item, stamp, maps, sx, sy, mid, padded, alpha) in stamps)
                {
                    // Skia keeps merging the queued stamp draws into ever larger operations; 1500 queued
                    // stamps took minutes of CPU at the next flush, a bounded batch takes milliseconds.
                    if (++drawn % 64 == 0) gpu?.Flush();
                    canvas.Save(); canvas.Translate(mid.X, mid.Y);
                    canvas.RotateDegrees((float)item.RotationDegrees);
                    canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
                    paint.Color = SKColors.White.WithAlpha(alpha);
                    if (i == 6)
                    {
                        // Transform the asset's tensor direction into document axes. B carries this
                        // instance's brush scale; support alpha makes the top stamp own its fields.
                        using var shader = DirectionShader(maps[6], maps[3], sx, sy, padded, item);
                        paint.Shader = shader; canvas.DrawRect(padded, paint); paint.Shader = null;
                    }
                    else canvas.DrawImage(maps[i], padded, sampling, paint);
                    canvas.Restore();
                }
            }
            var images = surfaces.Select(s => s.Snapshot()).ToArray();
            StyleDiagnostics.StageEnd("passes", passesStarted);
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
    /// <summary>
    /// Builds, off the render thread and before any style is chosen, what the first frame of a style needs per asset:
    /// the painter form maps (both sets) and the print height map of every mountain of the map. Without it the first
    /// frame of «Акварель» froze the map for 12,8 s and «Атлас Блау» for 4,5 s (paint-perf MAP_NEW_ART_STYLE,
    /// 2026-10-31); «Оформление» calls this when it opens. The maps are the same ones the frame would build.
    /// Each step leases its picture from <paramref name="stamps"/> only while it works (audit 2026-11-01: the pictures
    /// were taken from the canvas's cache on this thread and could be freed under the work), so at most one picture per
    /// worker is held, however many mountains the map has.
    /// </summary>
    public static void PrepareAssets(MapDocument document, IStampImageSource? stamps, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(document);
        // The style's own per-layer data as well, so the first styled frame of a large map does not make it: the map
        // without its names, the spatial index of each bare layer, its largest side and its pictures.
        foreach (var layer in Split(document).Bare.Layers.OfType<ObjectLayer>())
        {
            if (token.IsCancellationRequested) return;
            if (!layer.Visible || layer.Objects.Count == 0) continue;
            ObjectSpatialIndex.For(layer);
            LargestSide(layer);
            VisibleStampAssetsOf(layer);
        }
        if (stamps is null) return;
        var work = new List<Action>();
        foreach (var sha in document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).SelectMany(l => l.Objects)
                     .Select(o => o.Stamp?.AssetSha256).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            work.Add(() => WithImage(stamps, sha, image => PainterFormMaps.Get(image, true)));
            work.Add(() => WithImage(stamps, sha, image => PainterFormMaps.Get(image, false)));
            work.Add(() => WithImage(stamps, sha, image => PrintHeightMaps.Get(sha, image)));
        }
        if (work.Count == 0) return;
        try
        {
            // Half the cores: the map, the panels and the pictures of «Вид» keep the rest.
            using (Map.Core.Resources.BackgroundLoad.Pause())
                Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = token }, step =>
                {
                    using var load = Map.Core.Resources.BackgroundLoad.Enter(Map.Core.Resources.BackgroundProducer.StylePaint);
                    step();
                });
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Keeps the painter form maps of the owner's mountains in <paramref name="folder"/> between runs (A2 step 4) and
    /// deletes what is no longer current there (another recipe, over the size limit). Null turns the disk cache off.
    /// </summary>
    public static void UseFormMapCache(string? folder)
    {
        FormMapDiskCache.Folder = folder;
        FormMapDiskCache.Trim();
    }

    private static void WithImage(IStampImageSource stamps, string sha, Action<SKImage> build)
    {
        using var lease = stamps.Lease();
        if (lease.TryGetImage(sha) is { IsTextureBacked: false } image) build(image);
    }

    private static void PrebuildFormMaps(IEnumerable<SKImage> drawn)
    {
        var sources = drawn.Distinct(ReferenceEqualityComparer.Instance).Cast<SKImage>()
            .Where(s => !PainterFormMaps.IsBuilt(s)).ToArray();
        if (sources.Length < 2) return;
        using (Map.Core.Resources.BackgroundLoad.Pause())
        Parallel.ForEach(sources.SelectMany(s => new[] { (s, true), (s, false) }),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            pair =>
            {
                using var load = Map.Core.Resources.BackgroundLoad.Enter(Map.Core.Resources.BackgroundProducer.StylePaint);
                PainterFormMaps.Get(pair.Item1, pair.Item2);
            });
    }

    /// <summary>The largest side of a list's objects and how many objects have it (an exact maximum under edits).</summary>
    private sealed record Extent(double Largest, int AtLargest);

    private static readonly ConditionalWeakTable<IReadOnlyList<SceneObject>, Extent> Extents = [];

    /// <summary>
    /// <c>Objects.Max(o => Math.Max(o.Width, o.Height))</c> of a non-empty layer, kept per object list: an edit updates
    /// it from its removed and added objects, and only the removal of the last object of the largest side scans again.
    /// </summary>
    private static double LargestSide(ObjectLayer layer) => PerObjects(Extents, layer, static objects =>
    {
        var largest = objects.Max(o => Math.Max(o.Width, o.Height));
        return new Extent(largest, objects.Count(o => Math.Max(o.Width, o.Height) == largest));
    }, static (before, delta, objects) =>
    {
        var (largest, count) = before;
        foreach (var index in delta.Removed)
            if (Math.Max(delta.Source.Objects[index].Width, delta.Source.Objects[index].Height) == largest) count--;
        foreach (var index in delta.Added)
        {
            var side = Math.Max(objects[index].Width, objects[index].Height);
            if (side > largest) { largest = side; count = 1; }
            else if (side == largest) count++;
        }
        return count > 0 ? new Extent(largest, count) : null;
    }).Largest;

    /// <summary>How many visible stamps of a list use each picture (StampsReady asks only about the pictures).</summary>
    private sealed class StampAssets(Dictionary<string, int> counts)
    {
        public Dictionary<string, int> Counts { get; } = counts;
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<SceneObject>, StampAssets> VisibleStampAssets = [];

    private static IReadOnlyCollection<string> VisibleStampAssetsOf(ObjectLayer layer) => PerObjects(VisibleStampAssets, layer, static objects =>
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in objects)
            if (item.Visible && item.Stamp is { } stamp) counts[stamp.AssetSha256] = counts.GetValueOrDefault(stamp.AssetSha256) + 1;
        return new StampAssets(counts);
    }, static (before, delta, objects) =>
    {
        var counts = new Dictionary<string, int>(before.Counts, StringComparer.Ordinal);
        foreach (var index in delta.Removed)
            if (delta.Source.Objects[index] is { Visible: true, Stamp: { } stamp })
            {
                var left = counts.GetValueOrDefault(stamp.AssetSha256) - 1;
                if (left < 0) return null;
                if (left == 0) counts.Remove(stamp.AssetSha256); else counts[stamp.AssetSha256] = left;
            }
        foreach (var index in delta.Added)
            if (objects[index] is { Visible: true, Stamp: { } stamp }) counts[stamp.AssetSha256] = counts.GetValueOrDefault(stamp.AssetSha256) + 1;
        return new StampAssets(counts);
    }).Counts.Keys;

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
