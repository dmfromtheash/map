using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, watercolour round 2 (owner 2026-10-25: the first five «не понравились вообще» - the mountains were filled
/// in heavily and the style was laid on crudely). Round 1 threw the painting away and repainted the mountains from the
/// silhouette and an invented height, so the insides became one muddy mass with spots that had nothing to do with the
/// drawing. Here, as in every watercolour filter that keeps a drawing (MNPR, Bousseau 2006, AKVIS «Contour», G'MIC,
/// line-and-wash actions), the painting is the source of the form: its contour lines (difference of Gaussians), its
/// value planes (an edge-preserving Kuwahara abstraction, then a few transparent glazes), its lights (left as paper).
/// Only the medium changes - pigments, glazes, dried edges, granulation, paper. Research:
/// docs/research/2026-10-25-r09-watercolour-round2.md. Two scenes: the small island with three mountains apart and a
/// crowded range. MAP_STYLE_WATERCOLOR2 = output folder, MAP_LIGHT_ASSETS as before.
/// </summary>
public sealed partial class StyleMockupDump
{
    private sealed record Aqua(int Variant, string Paper);

    [Fact]
    public void Dump_watercolour_round2_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_WATERCOLOR2");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var only = Environment.GetEnvironmentVariable("MAP_STYLE_WATERCOLOR2_ONLY");
        var sceneOnly = Environment.GetEnvironmentVariable("MAP_STYLE_WATERCOLOR2_SCENE");
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var variants = AquaVariants().Where(v => string.IsNullOrEmpty(only) || only.Contains(v.Aqua.Variant.ToString())).ToArray();
            foreach (var tag in new[] { "малая", "большая" })
            {
                if (!string.IsNullOrEmpty(sceneOnly) && sceneOnly != tag) continue;
                var (document, labels) = tag == "малая" ? Scene(images) : Crowd(images);
                var mountainsId = document.Layers.First(l => l.Name == "Горы").Id;
                var riversId = document.Layers.First(l => l.Name == "Реки").Id;
                var land = document.Layers.OfType<LandLayer>().First().Id;
                using var mountains = RenderOnly(document, mountainsId, images);
                using var names = RenderOnly(document, labels, images);
                using var landOnly = RenderOnly(document, land, images);
                using var riverOnly = RenderOnly(document, riversId, images);
                using var geo = CoastField(landOnly, riverOnly);
                using var kuwahara = Kuwahara(mountains);
                using var abstracted = Blurred(kuwahara, 0.9f);
                using var near = Blurred(mountains, 1.1f);
                using var mid = Blurred(mountains, 3.2f);
                using var wide = Blurred(abstracted, 9f);
                using var riverSoft = Blurred(riverOnly, 4f);
                foreach (var (name, aqua) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = AquaShader(mountains, abstracted, near, mid, wide, geo, riverSoft, aqua))
                    using (var paint = new SKPaint { Shader = shader })
                        surface.Canvas.DrawRect(0, 0, W, H, paint);
                    surface.Canvas.DrawImage(names, 0, 0);
                    using var image = surface.Snapshot();
                    Save(image, Path.Combine(output, $"{name} · {tag} · 50%.png"), SKRectI.Create(0, 0, W, H), 0.5f);
                    var close = tag == "малая" ? SKRectI.Create(560, 300, 1440, 640) : SKRectI.Create(700, 330, 1440, 640);
                    Save(image, Path.Combine(output, $"{name} · {tag} · горы 100%.png"), close, 1f);
                    if (tag == "малая")
                        Save(image, Path.Combine(output, $"{name} · {tag} · берег 100%.png"), SKRectI.Create(150, 760, 960, 540), 1f);
                }
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    private static IEnumerable<(string Name, Aqua Aqua)> AquaVariants() =>
    [
        ("Акварель 1 · Перо и размывка", new(1, "#f8f3e8")),
        ("Акварель 2 · Книжная лессировка", new(2, "#f1e8d2")),
        ("Акварель 3 · По сырому", new(3, "#f9f6ee")),
        ("Акварель 4 · Грануляция и сухая кисть", new(4, "#f4eee0")),
        ("Акварель 5 · Тёплый свет, холодная тень", new(5, "#f7f2e6")),
    ];

    private static void Save(SKImage image, string path, SKRectI rect, float scale)
    {
        var w = (int)(rect.Width * scale);
        var h = (int)(rect.Height * scale);
        using var surface = SKSurface.Create(new SKImageInfo(w, h))!;
        surface.Canvas.DrawImage(image, rect, SKRect.Create(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var shot = surface.Snapshot();
        using var data = shot.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    /// <summary>The crowded scene: a larger island with a range of many smaller mountains overlapping in rows.</summary>
    private static (MapDocument Document, LayerId Labels) Crowd(Dictionary<string, SKImage> images)
    {
        var (small, labels) = Scene(images);
        var keys = images.Keys.ToArray();
        var objects = new List<SceneObject>();
        var random = new Random(7);
        for (var row = 0; row < 4; row++)
            for (var i = 0; i < 11; i++)
            {
                var sha = keys[random.Next(keys.Length)];
                var image = images[sha];
                var scale = 0.2 + 0.07 * random.NextDouble() + 0.03 * row;
                double w = 1024 * scale, h = 1024.0 * image.Height / image.Width * scale;
                var cx = 620 + i * 125 + row * 40 + random.Next(-30, 30);
                var baseY = 600 + row * 95 + random.Next(-20, 20);
                objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", cx - w / 2, baseY - h, w, h, 0,
                    "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
            }
        var island = Land("Остров", (1280, 740, 1180, 600, 0.08, 7));
        var layers = small.Layers.Select(l => l switch
        {
            LandLayer => island,
            ObjectLayer o when o.Name == "Горы" => o with { Objects = [.. objects.OrderBy(o => o.Y + o.Height)] },
            _ => l,
        }).ToList();
        return (small.WithLayers(layers), labels);
    }

    /// <summary>
    /// An edge-preserving abstraction of the painting (Kuwahara, four quadrants): each pixel takes the mean colour of the
    /// least varied quadrant around it, so brush texture flattens into planes while the edges between planes stay put.
    /// </summary>
    private static SKImage Kuwahara(SKImage map, float step = 1.25f)
    {
        const string source = """
uniform shader map;
uniform float stride;
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
half4 main(float2 p) {
    float4 best = map.eval(p);
    float bestVar = 1e9;
    for (int q = 0; q < 4; q++) {
        float2 dir = float2(q == 0 || q == 3 ? -1.0 : 1.0, q < 2 ? -1.0 : 1.0);
        float4 sum = float4(0);
        float s1 = 0.0; float s2 = 0.0;
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) {
            float4 c = map.eval(p + dir * float2(float(i), float(j)) * stride);
            float l = lum(c.rgb) + (1.0 - c.a) * 0.6;
            sum += c; s1 += l; s2 += l * l;
        }
        float m = s1 / 16.0;
        float v = s2 / 16.0 - m * m;
        if (v < bestVar) { bestVar = v; best = sum / 16.0; }
    }
    return half4(best);
}
""";
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        using var effect = SKRuntimeEffect.CreateShader(source, out var errors) ?? throw new InvalidOperationException(errors);
        var builder = new SKRuntimeShaderBuilder(effect);
        using var child = map.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        builder.Children["map"] = child;
        builder.Uniforms["stride"] = step;
        using var shader = builder.Build();
        using var paint = new SKPaint { Shader = shader };
        surface.Canvas.DrawRect(0, 0, W, H, paint);
        return surface.Snapshot();
    }

    private const string AquaSource = """
uniform shader mount;
uniform shader mabs;
uniform shader mnear;
uniform shader mmid;
uniform shader mwide;
uniform shader geo;
uniform shader rsoft;
uniform float variant;
uniform float3 paper;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float3 hex(float r, float g, float b) { return float3(r, g, b) / 255.0; }
// The value of a premultiplied sample seen over white paper.
float onWhite(float4 c) { return lum(c.rgb + (1.0 - c.a)); }

// A transparent glaze: the pigment multiplies what is under it, `amt` of the way, with Bousseau's pigment density
// d (C' = C (1 - (1 - C)(d - 1)): d > 1 deepens, d < 1 thins, bare paper never changes).
float3 glaze(float3 under, float3 pigment, float amt, float d) {
    float3 c = mix(float3(1), pigment, clamp(amt, 0.0, 1.0));
    c = c * (1.0 - (1.0 - c) * (d - 1.0));
    return under * clamp(c, 0.0, 1.0);
}
// A flat wash where `field` passes `t` (a soft edge `soft` wide) and the pigment drawn to that edge as it dried.
float washMask(float field, float t, float soft) { return smoothstep(t - soft, t + soft, field); }
float dryEdge(float field, float t, float w) { return smoothstep(t - 0.01, t + 0.01, field) * (1.0 - smoothstep(t, t + w, field)); }

half4 main(float2 p) {
    float4 g = geo.eval(p);
    // The coast and the wash boundaries wander a little, as a hand-laid wash does.
    float jit = (fbm(p / 16.0) - 0.5) * 5.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    float riverS = rsoft.eval(p).a;

    // Paper: fine tooth (grain peaks) and slow sizing; turbulence (low frequency) and dispersion (high) for density.
    float tooth = fbm(p / 3.0) * 0.55 + noise(p / 1.15) * 0.45;
    float turb = fbm(p / 70.0);
    float disp = noise(p / 2.2);
    float3 col = paper * (1.0 - 0.03 * (noise(p / 300.0) - 0.5));

    // ---- the drawing, read from the painting itself
    // Washes sit a little off the drawing (gaps and overlaps); the lines are never moved.
    float2 wob = float2(noise(p / 48.0) - 0.5, noise(p / 48.0 + 17.0) - 0.5) * 5.0;
    float4 m = mount.eval(p);
    float4 mn = mnear.eval(p);
    float4 mm = mmid.eval(p);
    float4 ma = mabs.eval(p + wob);
    float4 mw = mwide.eval(p + wob);
    float alpha = smoothstep(0.3, 0.7, m.a);
    float washA = smoothstep(0.4, 0.6, ma.a + 0.14 * (noise(p / 6.0) - 0.5));
    float halo = smoothstep(0.02, 0.35, mw.a);
    // Contour lines: darker and narrower than their surroundings (difference of Gaussians), plus anything very dark.
    float vNear = onWhite(mn);
    float vMid = onWhite(mm);
    float line = clamp(smoothstep(0.03, 0.13, vMid - vNear) * smoothstep(0.72, 0.38, vNear) + smoothstep(0.36, 0.2, vNear), 0.0, 1.0)
        * smoothstep(0.2, 0.6, mn.a);
    // Planes: the abstracted value, 0 = lit (bare paper), 1 = darkest.
    float tone = clamp((0.94 - onWhite(ma)) / 0.68, 0.0, 1.0) * washA;
    float toneWide = clamp((0.94 - onWhite(mw)) / 0.68, 0.0, 1.0);
    // How far up its own mountain a point is: the silhouette still covers the point 70 px below it.
    float up = mwide.eval(p + float2(0, 70)).a * washA;
    // The wash's own edge, darker as it dried: inside the abstracted silhouette, close to its border.
    float rim = washA * (1.0 - smoothstep(0.55, 0.9, mw.a));

    float seaT = smoothstep(0.0, 280.0, dL);
    float seaF = 1.0 - landF;
    float coastDry = seaF * (1.0 - smoothstep(0.0, 5.0, dL));
    float landDry = landF * (1.0 - smoothstep(0.0, 4.0, dW));
    // The land wash is laid around the mountains with a hand-cut, wandering edge that dried darker: their lights stay
    // paper. (Variant 3, wet, uses the soft halo instead.)
    float cutAt = variant < 1.5 ? 0.18 : 0.32;
    float cutF = mw.a + 0.3 * (fbm(p / 13.0) - 0.5) * smoothstep(0.02, 0.12, mw.a);
    float landCut = smoothstep(cutAt, cutAt + 0.05, cutF);
    float landWash = landF * (1.0 - 0.92 * landCut);
    float landRim = landF * (1.0 - landCut) * smoothstep(cutAt - 0.12, cutAt, cutF);
    float riverEdge = river * (1.0 - smoothstep(0.35, 0.8, riverS));

    if (variant < 1.5) {
        // ---- 1. Pen and wash (urban-sketch line and wash): the drawing kept whole as a sepia pen line, the washes
        // loose, light, a little off the line and stopping short of it, so a lot of paper shows.
        float d = 1.0 + 0.35 * (turb - 0.5) + 0.15 * (disp - 0.5);
        col = glaze(col, mix(hex(150, 196, 214), hex(70, 120, 172), seaT), seaF * smoothstep(3.0, 9.0, dL) * 0.62, d);
        col = glaze(col, hex(70, 120, 172), seaF * dryEdge(1.0 - (dL - 3.0) / 30.0, 0.8, 0.12) * 0.35, 1.0);
        col = glaze(col, mix(hex(236, 214, 160), hex(206, 210, 156), smoothstep(0.3, 0.7, noise(p / 420.0))), landWash * smoothstep(3.0, 8.0, dW) * 0.42, 1.0 + 0.12 * (turb - 0.5));
        col = glaze(col, hex(190, 170, 120), landRim * 0.35, 1.0);
        col = glaze(col, hex(150, 196, 214), river * 0.5, d);
        float reach = smoothstep(0.38, 0.62, noise(p / 64.0) * 0.6 + toneWide * 0.9);
        col = glaze(col, mix(hex(205, 170, 140), hex(160, 180, 210), smoothstep(0.2, 0.6, toneWide)), washMask(tone, 0.1, 0.03) * reach * 0.5, d);
        col = glaze(col, hex(96, 112, 170), washMask(tone, 0.42, 0.025) * 0.5, d);
        col = glaze(col, hex(80, 90, 150), dryEdge(tone, 0.42, 0.05) * 0.3, 1.0);
        // Pen: sepia contours of mountains and the coast, broken a little where the nib skipped on the tooth.
        float coastPen = (1.0 - smoothstep(0.6, 1.6, abs(g.r * 255.0 - g.g * 255.0 + 0.5 * jit))) * 0.75;
        float rivPen = (1.0 - smoothstep(0.08, 0.3, abs(river - 0.5))) * 0.6;
        float pen = max(line, max(coastPen, rivPen)) * (0.82 + 0.18 * step(0.28, tooth));
        col = mix(col, hex(66, 48, 40), pen * 0.86);
    } else if (variant < 2.5) {
        // ---- 2. Book glazes (Rackham, Dulac): a thin black ink drawing, then three translucent glazes in a limited,
        // muted palette, each with a hard dried edge; the paper is warm and toned.
        float d = 1.0 + 0.22 * (turb - 0.5);
        col = glaze(col, mix(hex(150, 170, 150), hex(92, 118, 118), seaT), seaF * 0.55, d);
        col = glaze(col, hex(92, 118, 118), seaF * washMask(dL, 26.0, 2.0) * 0.28, d);
        col = glaze(col, hex(70, 92, 96), seaF * (dryEdge(dL, 26.0, 3.0) + coastDry) * 0.45, 1.0);
        col = glaze(col, hex(196, 170, 110), landWash * 0.4, d);
        col = glaze(col, hex(150, 130, 80), (landDry + landRim) * 0.4, 1.0);
        col = glaze(col, hex(150, 170, 150), river * 0.65, d);
        col = glaze(col, hex(120, 110, 80), riverEdge * 0.4, 1.0);
        col = glaze(col, hex(206, 188, 150), washMask(tone, 0.08, 0.02) * 0.55, d);
        col = glaze(col, hex(150, 140, 130), washMask(tone, 0.3, 0.018) * 0.5, d);
        col = glaze(col, hex(96, 98, 110), washMask(tone, 0.56, 0.018) * 0.55, d);
        float edges = dryEdge(tone, 0.08, 0.04) + dryEdge(tone, 0.3, 0.04) + dryEdge(tone, 0.56, 0.04);
        col = glaze(col, hex(90, 80, 70), clamp(edges, 0.0, 1.0) * 0.35, 1.0);
        col = mix(col, hex(34, 30, 30), line * 0.82);
        col *= 0.985 + 0.03 * (tooth - 0.5);
    } else if (variant < 3.5) {
        // ---- 3. Wet-in-wet: colours dropped into a wet sheet and left to mingle - warm and cool meet softly, washes
        // spill past the drawing and bloom; the drawing is painted back on dry paper in deep indigo, not ink.
        float d = 1.0 + 0.5 * (turb - 0.5) + 0.12 * (disp - 0.5);
        float3 sea = mix(mix(hex(80, 190, 200), hex(40, 110, 190), seaT), hex(120, 90, 170), smoothstep(0.55, 0.8, fbm(p / 160.0)) * 0.6);
        col = glaze(col, sea, seaF * (0.55 + 0.2 * turb), d);
        col = glaze(col, hex(40, 110, 190), coastDry * 0.3, 1.0);
        float3 landC = mix(hex(240, 204, 120), hex(236, 186, 160), smoothstep(0.35, 0.75, noise(p / 380.0 + 4.0)) * 0.6);
        col = glaze(col, landC, landF * (1.0 - 0.6 * halo) * 0.4, 1.0 + 0.2 * (turb - 0.5));
        col = glaze(col, hex(80, 190, 200), river * 0.7 + riverS * landF * 0.15, d);
        // The mountains' first wash: soft, bleeding past the outline, rose at the foot, cerulean and violet higher up.
        float bleed = smoothstep(0.05, 0.6, mw.a + 0.2 * (fbm(p / 18.0) - 0.5));
        float3 wet = mix(hex(232, 186, 186), mix(hex(110, 170, 220), hex(130, 110, 190), smoothstep(0.4, 0.8, fbm(p / 55.0))), smoothstep(0.15, 0.65, up));
        col = glaze(col, wet, bleed * (0.22 + 0.5 * toneWide), d);
        // A cauliflower backrun where water crept back into the drying wash: a pale bloom with a dark wavy rim.
        float bloom = fbm(p / 24.0 + 2.0);
        col = glaze(col, hex(90, 90, 170), bleed * (1.0 - smoothstep(0.0, 0.03, abs(bloom - 0.62))) * 0.35, 1.0);
        col = mix(col, paper, bleed * smoothstep(0.64, 0.72, bloom) * 0.25);
        col = glaze(col, hex(70, 80, 150), washMask(tone, 0.48, 0.03) * 0.5, d);
        col = mix(col, hex(40, 44, 96), line * 0.8);
    } else if (variant < 4.5) {
        // ---- 4. Granulation and dry brush on rough paper: ultramarine and burnt sienna settle into the tooth's
        // valleys; a dragged, nearly dry brush leaves the paper's peaks white on the lit faces and on the sea.
        float valley = 1.0 - smoothstep(0.28, 0.72, tooth);
        float d = 1.0 + 0.9 * (valley - 0.5) + 0.2 * (turb - 0.5);
        col = glaze(col, mix(hex(110, 140, 190), hex(60, 76, 150), seaT), seaF * 0.6, d);
        // Dry brush along the waves near the shore: paper peaks left white.
        float streak = noise(float2(p.x / 60.0, p.y / 3.0));
        col = mix(col, paper, seaF * (1.0 - smoothstep(0.0, 60.0, dL)) * step(0.62 - 0.3 * streak, tooth) * 0.7);
        col = glaze(col, hex(190, 140, 100), landWash * 0.38, d);
        col = glaze(col, hex(110, 140, 190), river * 0.5, d);
        col = glaze(col, hex(160, 120, 80), (landRim + landDry) * 0.3, 1.0);
        float3 mixC = mix(hex(200, 170, 150), hex(110, 122, 180), smoothstep(0.2, 0.55, tone + 0.15 * (turb - 0.5)));
        float dry = step(0.5 - 0.5 * tone, valley + 0.05);
        col = glaze(col, mixC, washMask(tone, 0.14, 0.03) * dry * 0.55, d);
        col = glaze(col, hex(84, 90, 150), washMask(tone, 0.5, 0.03) * 0.42, d);
        col = glaze(col, hex(70, 50, 60), rim * 0.25 + dryEdge(tone, 0.45, 0.05) * 0.25, 1.0);
        col = glaze(col, hex(50, 40, 56), line * step(0.2, valley + 0.25) * 0.8, 1.0);
        col *= 1.0 - 0.07 * (tooth - 0.5);
    } else {
        // ---- 5. Warm light, cool shadow (the Swiss manner, Imhof): lit faces glazed warm yellow, shadow faces
        // blue-violet, contrast strongest on the high parts and weakest at the foot (aerial perspective); valleys
        // green-grey, the sea a light even blue; the drawing as a light grey-violet line, fading downward.
        float d = 1.0 + 0.2 * (turb - 0.5);
        col = glaze(col, mix(hex(176, 206, 226), hex(120, 160, 210), seaT), seaF * 0.6, d);
        col = glaze(col, hex(100, 140, 190), coastDry * 0.4, 1.0);
        col = glaze(col, mix(hex(214, 222, 170), hex(236, 220, 160), smoothstep(0.0, 120.0, dW)), landWash * 0.45, d);
        col = glaze(col, hex(150, 160, 130), landF * halo * (1.0 - washA) * 0.25, d);
        col = glaze(col, hex(170, 176, 140), landRim * 0.3, 1.0);
        col = glaze(col, hex(176, 206, 226), river * 0.5, d);
        float hi = 0.45 + 0.55 * up;
        float lit = washA * (1.0 - smoothstep(0.1, 0.35, tone));
        col = glaze(col, hex(250, 222, 140), lit * 0.5 * hi, d);
        col = glaze(col, hex(150, 150, 200), washMask(tone, 0.22, 0.03) * 0.5 * hi, d);
        col = glaze(col, hex(110, 100, 170), washMask(tone, 0.5, 0.03) * 0.5 * hi, d);
        col = glaze(col, hex(160, 170, 150), washA * (1.0 - hi) * 0.4, d);
        col = mix(col, hex(80, 70, 110), line * (0.5 + 0.35 * up));
    }

    col *= 1.0 - 0.035 * (tooth - 0.5);
    col += (hash(floor(p)) - 0.5) * 0.01;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> AquaEffect = new(() =>
        SKRuntimeEffect.CreateShader(AquaSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader AquaShader(SKImage mountains, SKImage abstracted, SKImage near, SKImage mid, SKImage wide, SKImage geo,
        SKImage riverSoft, Aqua aqua)
    {
        var builder = new SKRuntimeShaderBuilder(AquaEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        using var cMount = mountains.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cAbs = abstracted.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cNear = near.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cMid = mid.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cWide = wide.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cGeo = geo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cRiver = riverSoft.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        builder.Children["mount"] = cMount; builder.Children["mabs"] = cAbs; builder.Children["mnear"] = cNear;
        builder.Children["mmid"] = cMid; builder.Children["mwide"] = cWide; builder.Children["geo"] = cGeo;
        builder.Children["rsoft"] = cRiver;
        builder.Uniforms["variant"] = (float)aqua.Variant;
        builder.Uniforms["paper"] = Stylize.Hex(aqua.Paper);
        return builder.Build();
    }
}
