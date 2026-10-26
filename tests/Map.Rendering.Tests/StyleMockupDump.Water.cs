using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R12 «Вода глубже» (S21): five clearly different ways to draw the sea next to the owner's painted land - the land,
/// mountains, river and names stay exactly as the program draws them, only the open water changes. Pen water lines
/// follow the shore as on the owner's mountains (slate ink, broken, wider apart outwards); bathymetric contours with
/// index lines as on a sea chart; engraved ripple strokes, denser at the shore; depth in colour as flat watercolour
/// bands; and the colour bands under two pen lines. The ring ripple of the land edge is switched off so nothing doubles.
/// Research: docs/research/2026-10-26-r12-water-styles.md. MAP_STYLE_WATER = output folder, MAP_LIGHT_ASSETS as before;
/// MAP_STYLE_WATER_SCENE = малая|большая, MAP_STYLE_WATER_ONLY = variant numbers.
/// </summary>
public sealed partial class StyleMockupDump
{
    /// <summary>One sea variant: its ink, shallow, middle and deep water.</summary>
    private sealed record Water(int Variant, string Ink, string Shallow, string Middle, string Deep);

    [Fact]
    public void Dump_water_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_WATER");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var only = Environment.GetEnvironmentVariable("MAP_STYLE_WATER_ONLY");
        var sceneOnly = Environment.GetEnvironmentVariable("MAP_STYLE_WATER_SCENE");
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var variants = WaterVariants().Where(v => string.IsNullOrEmpty(only) || only.Contains(v.Water.Variant.ToString())).ToArray();
            foreach (var tag in new[] { "малая", "большая" })
            {
                if (!string.IsNullOrEmpty(sceneOnly) && sceneOnly != tag) continue;
                var (scene, _) = tag == "малая" ? Scene(images) : Crowd(images);
                // The land's own ring ripple off: the variants draw all the water.
                var document = scene.WithLayers([.. scene.Layers.Select(l => l is LandLayer land
                    ? land with { EdgeStyle = land.EdgeStyle with { RippleCount = 0 } } : l)]);
                var riversId = document.Layers.First(l => l.Name == "Реки").Id;
                var landId = document.Layers.OfType<LandLayer>().First().Id;
                using var full = Render(document with { Lighting = null }, images);
                using var landOnly = RenderOnly(document, landId, images);
                using var riverOnly = RenderOnly(document, riversId, images);
                using var geo = CoastField(landOnly, riverOnly);
                foreach (var (name, water) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = WaterShader(full, geo, water))
                    using (var paint = new SKPaint { Shader = shader })
                        surface.Canvas.DrawRect(0, 0, W, H, paint);
                    using var image = surface.Snapshot();
                    Save(image, Path.Combine(output, $"{name} · {tag} · 50%.png"), SKRectI.Create(0, 0, W, H), 0.5f);
                    if (tag == "малая")
                    {
                        Save(image, Path.Combine(output, $"{name} · {tag} · берег 100%.png"), SKRectI.Create(150, 760, 960, 540), 1f);
                        Save(image, Path.Combine(output, $"{name} · {tag} · открытое море 100%.png"), SKRectI.Create(1500, 980, 960, 460), 1f);
                    }
                }
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    private static IEnumerable<(string Name, Water Water)> WaterVariants() =>
    [
        // 1. Pen water lines: four broken slate lines following the shore, wider apart and fainter outwards.
        ("Вода 1 · Береговые линии пером", new(1, "#2e4455", "#93b0b8", "#7f9fae", "#6f90a2")),
        // 2. Sea chart: depth contours at equal steps, every fourth heavier, the deep a shade darker.
        ("Вода 2 · Изолинии глубин, как на морской карте", new(2, "#34505f", "#9ab6bb", "#86a5b1", "#6b8ca0")),
        // 3. Engraved ripples: short level strokes, crowded at the shore, thinning out to clear water.
        ("Вода 3 · Рябь штрихом, как на гравюре", new(3, "#2c4252", "#9cb7bd", "#86a4b0", "#7393a5")),
        // 4. Depth in colour: three flat watercolour bands, shallow turquoise to deep slate, soft wet edges, granulation.
        ("Вода 4 · Глубина цветом, акварельные полосы", new(4, "#2e4455", "#93b0b3", "#7f9fae", "#587891")),
        // 5. Both: the colour bands, paler, under two pen lines at the shore.
        ("Вода 5 · Глубина цветом и две линии пером", new(5, "#2e4455", "#95b2b5", "#84a3b0", "#62829a")),
    ];

    private const string WaterSource = """
uniform shader full;
uniform shader geo;
uniform float variant;
uniform float3 cInk; uniform float3 cShallow; uniform float3 cMiddle; uniform float3 cDeep;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }

// A line at distance d (pixels, along the shore), its width w; broken where noise drops.
float penLine(float d, float at, float w, float2 p, float seed) {
    float on = step(0.3, noise(p / 46.0 + seed));
    float wob = (noise(p / 23.0 + seed * 3.0) - 0.5) * 2.0;
    return on * (1.0 - smoothstep(w * 0.5, w * 0.5 + 0.8, abs(d - at - wob)));
}

half4 main(float2 p) {
    float4 base = full.eval(p);
    float4 g = geo.eval(p);
    float jit = (fbm(p / 18.0) - 0.5) * 3.0;
    float dL = g.r * 255.0 + jit;
    // Keep the program's own coast (pen line, soft shadow) for the first pixels off the shore.
    float sea = smoothstep(3.0, 7.0, g.r * 255.0) * (1.0 - step(0.5, g.g * 255.0));
    if (sea <= 0.0) return half4(base);

    float grain = fbm(p / 2.4) - 0.5;
    float wash = fbm(p / 180.0) - 0.5;
    float3 col = base.rgb;

    if (variant < 1.5) {
        col = mix(cShallow, cMiddle, smoothstep(10.0, 90.0, dL));
        col = mix(col, cDeep, smoothstep(90.0, 250.0, dL) * 0.6);
        col *= 1.0 + wash * 0.05 + grain * 0.02;
        float lines = 0.0;
        lines = max(lines, penLine(dL, 9.0, 1.6, p, 1.0) * 0.85);
        lines = max(lines, penLine(dL, 20.0, 1.3, p, 2.0) * 0.65);
        lines = max(lines, penLine(dL, 35.0, 1.1, p, 3.0) * 0.45);
        lines = max(lines, penLine(dL, 55.0, 0.9, p, 4.0) * 0.28);
        col = mix(col, cInk, lines);
    } else if (variant < 2.5) {
        col = mix(cShallow, cMiddle, smoothstep(5.0, 120.0, dL));
        col = mix(col, cDeep, smoothstep(120.0, 250.0, dL));
        col *= 1.0 + wash * 0.04 + grain * 0.015;
        float step_ = 22.0;
        float s = dL / step_; float k = floor(s + 0.5);
        float dist = abs(s - k) * step_;
        // Every fourth contour is an index line, drawn heavier.
        float heavy = step(3.5, mod(k, 4.0) + 0.5);
        float w = mix(0.9, 1.6, heavy);
        float contour = (1.0 - smoothstep(w * 0.5, w * 0.5 + 0.8, dist)) * step(0.5, k) * (1.0 - smoothstep(10.0, 12.0, k));
        col = mix(col, cInk, contour * mix(0.32, 0.55, heavy));
    } else if (variant < 3.5) {
        col = mix(cShallow, cMiddle, smoothstep(10.0, 110.0, dL));
        col = mix(col, cDeep, smoothstep(110.0, 250.0, dL) * 0.5);
        col *= 1.0 + wash * 0.04 + grain * 0.02;
        // Level strokes: rows 5 px apart, short dashes whose share falls with the distance from shore.
        float row = floor(p.y / 5.0);
        float fy = fract(p.y / 5.0);
        float a = p.x / 26.0 + hash(float2(row, 3.0)) * 17.0;
        float seg = floor(a); float fa = fract(a);
        float keep = 1.0 - smoothstep(8.0, 140.0, dL);
        float on = step(1.0 - keep * 0.85, hash(float2(row, seg)));
        float len = 0.35 + 0.5 * hash(float2(seg, row + 7.0));
        float dash = on * step(fa, len) * (1.0 - smoothstep(0.18, 0.42, abs(fy - 0.5) * 2.0));
        col = mix(col, cInk, dash * mix(0.25, 0.6, keep));
    } else if (variant < 4.5) {
        // Three bands with wet, irregular edges; the pigment pools at each band's edge.
        float e1 = 28.0 + (fbm(p / 60.0) - 0.5) * 18.0;
        float e2 = 110.0 + (fbm(p / 90.0 + 4.0) - 0.5) * 50.0;
        float b1 = smoothstep(e1 - 3.0, e1 + 3.0, dL);
        float b2 = smoothstep(e2 - 6.0, e2 + 6.0, dL);
        col = mix(cShallow, cMiddle, b1);
        col = mix(col, cDeep, b2);
        float pool = (1.0 - smoothstep(0.0, 4.0, abs(dL - e1))) * 0.08 + (1.0 - smoothstep(0.0, 7.0, abs(dL - e2))) * 0.1;
        col *= 1.0 - pool + wash * 0.06;
        col *= 1.0 + grain * 0.05 * (0.5 + b2);
    } else {
        float e1 = 30.0 + (fbm(p / 60.0) - 0.5) * 16.0;
        float e2 = 120.0 + (fbm(p / 90.0 + 4.0) - 0.5) * 50.0;
        col = mix(cShallow, cMiddle, smoothstep(e1 - 3.0, e1 + 3.0, dL));
        col = mix(col, cDeep, smoothstep(e2 - 6.0, e2 + 6.0, dL));
        col *= 1.0 + wash * 0.05 + grain * 0.03;
        float lines = max(penLine(dL, 9.0, 1.5, p, 1.0) * 0.8, penLine(dL, 21.0, 1.2, p, 2.0) * 0.55);
        col = mix(col, cInk, lines);
    }
    return half4(half3(mix(base.rgb, clamp(col, 0.0, 1.0), sea)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> WaterEffect = new(() =>
        SKRuntimeEffect.CreateShader(WaterSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader WaterShader(SKImage full, SKImage geo, Water w)
    {
        var builder = new SKRuntimeShaderBuilder(WaterEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        using var fullShader = full.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var geoShader = geo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        builder.Children["full"] = fullShader; builder.Children["geo"] = geoShader;
        builder.Uniforms["variant"] = (float)w.Variant;
        builder.Uniforms["cInk"] = Stylize.Hex(w.Ink); builder.Uniforms["cShallow"] = Stylize.Hex(w.Shallow);
        builder.Uniforms["cMiddle"] = Stylize.Hex(w.Middle); builder.Uniforms["cDeep"] = Stylize.Hex(w.Deep);
        return builder.Build();
    }
}
