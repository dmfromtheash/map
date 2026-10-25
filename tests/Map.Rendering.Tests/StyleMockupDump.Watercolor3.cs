using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, watercolour round 3 (owner 2026-10-25 on round 2: «3 вариант более менее, но внешность гор сильно
/// напоминает оригинал, а не чуть ли иную перерисовку, как если бы рисовали акварелью»). Five wet-in-wet repaints that
/// keep only what a painter would take from the drawing - the silhouette, the big facets, the main light and shadow,
/// the main crest lines - and drop its fine line work: the form is read at a large scale (blur, then a coarse Kuwahara)
/// and painted back as a few washes, strokes and runs. The sea and land are those of round 2 variant 3.
/// MAP_STYLE_WATERCOLOR3 = output folder, MAP_LIGHT_ASSETS as before; MAP_STYLE_WATERCOLOR2_SCENE / _ONLY also apply.
/// </summary>
public sealed partial class StyleMockupDump
{
    [Fact]
    public void Dump_watercolour_round3_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_WATERCOLOR3");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var only = Environment.GetEnvironmentVariable("MAP_STYLE_WATERCOLOR2_ONLY");
        var sceneOnly = Environment.GetEnvironmentVariable("MAP_STYLE_WATERCOLOR2_SCENE");
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var variants = new (string Name, Aqua Aqua)[]
            {
                ("Акварель 3.1 · Крупные пятна", new(1, "#f9f6ee")),
                ("Акварель 3.2 · Мазки по форме", new(2, "#f9f6ee")),
                ("Акварель 3.3 · Пятно по сырому, тени по сухому", new(3, "#f9f6ee")),
                ("Акварель 3.4 · Потерянные и найденные края", new(4, "#f9f6ee")),
                ("Акварель 3.5 · Подтёки", new(5, "#f9f6ee")),
            }.Where(v => string.IsNullOrEmpty(only) || only.Contains(v.Aqua.Variant.ToString())).ToArray();
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
                using var riverSoft = Blurred(riverOnly, 4f);
                // The form at a painter's scale: big masses (no line work left), facets, main crest lines only.
                // Brush sizes follow the mountains' size (the crowded range uses them at about 0.4 of the small scene),
                // as the program will, abstracting each asset once at its own scale.
                var ms = tag == "малая" ? 1f : 0.42f;
                using var soft = Blurred(mountains, 4f * ms);
                using var bigK = Kuwahara(soft, 3.2f * ms);
                using var big = Blurred(bigK, 2.5f * ms);
                using var facetSoft = Blurred(mountains, 1.8f * ms);
                using var facetK = Kuwahara(facetSoft, 2f * ms);
                using var facets = Blurred(facetK, 1.5f * ms);
                using var crestA = Blurred(mountains, 2.5f * ms);
                using var crestB = Blurred(mountains, 7f * ms);
                using var wide = Blurred(mountains, 14f * ms);
                using var tensor = Tensor(big);
                foreach (var (name, aqua) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = WetShader(mountains, big, facets, crestA, crestB, wide, tensor, geo, riverSoft, aqua, ms))
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

    private const string WetSource = """
uniform shader mount;
uniform shader mbig;
uniform shader mfacet;
uniform shader crestA;
uniform shader crestB;
uniform shader mwide;
uniform shader tensor;
uniform shader geo;
uniform shader rsoft;
uniform float variant;
uniform float3 paper;
uniform float ms;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float3 hex(float r, float g, float b) { return float3(r, g, b) / 255.0; }
float onWhite(float4 c) { return lum(c.rgb + (1.0 - c.a)); }
float3 glaze(float3 under, float3 pigment, float amt, float d) {
    float3 c = mix(float3(1), pigment, clamp(amt, 0.0, 1.0));
    c = c * (1.0 - (1.0 - c) * (d - 1.0));
    return under * clamp(c, 0.0, 1.0);
}
float washMask(float field, float t, float soft) { return smoothstep(t - soft, t + soft, field); }
float dryEdge(float field, float t, float w) { return smoothstep(t - 0.01, t + 0.01, field) * (1.0 - smoothstep(t, t + w, field)); }
// A wash with lost and found edges: hard where the brush stopped on dry paper, soft where it met damp paper.
float lostFound(float2 p) { return mix(0.012, 0.09, smoothstep(0.5, 0.72, noise(p / 55.0 + 3.0))); }
float wash(float field, float t, float2 p) { return washMask(field, t, lostFound(p)); }
// Pigment pooled at the drying edge: darker in a band just inside the wash, lighter in its middle.
float pool(float field, float t) { return 0.65 + 0.7 * (1.0 - smoothstep(t, t + 0.16, field)); }
// Value of the abstracted form, 0 = lit, 1 = darkest.
float toneOf(float4 c) { return clamp((0.94 - onWhite(c)) / 0.66, 0.0, 1.0); }
// The wet palette: rose at the foot, cerulean and violet up the mountain.
float3 wetColour(float up, float2 p) {
    return mix(hex(232, 176, 176), mix(hex(104, 166, 220), hex(128, 104, 190), smoothstep(0.35, 0.8, fbm(p / 60.0))), smoothstep(0.1, 0.6, up));
}

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float jit = (fbm(p / 16.0) - 0.5) * 5.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float seaF = 1.0 - landF;
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    float riverS = rsoft.eval(p).a;
    float tooth = fbm(p / 3.0) * 0.55 + noise(p / 1.15) * 0.45;
    float turb = fbm(p / 70.0);
    float d = 1.0 + 0.5 * (turb - 0.5) + 0.12 * (noise(p / 2.2) - 0.5);
    float3 col = paper * (1.0 - 0.03 * (noise(p / 300.0) - 0.5));

    // ---- sea and land, as round 2 variant 3
    float seaT = smoothstep(0.0, 280.0, dL);
    float3 sea = mix(mix(hex(80, 190, 200), hex(40, 110, 190), seaT), hex(120, 90, 170), smoothstep(0.55, 0.8, fbm(p / 160.0)) * 0.6);
    col = glaze(col, sea, seaF * (0.55 + 0.2 * turb), d);
    col = glaze(col, hex(40, 110, 190), seaF * (1.0 - smoothstep(0.0, 5.0, dL)) * 0.3, 1.0);
    float4 mw = mwide.eval(p);
    float halo = smoothstep(0.02, 0.4, mw.a);
    float3 landC = mix(hex(240, 204, 120), hex(236, 186, 160), smoothstep(0.35, 0.75, noise(p / 380.0 + 4.0)) * 0.6);
    col = glaze(col, landC, landF * (1.0 - 0.6 * halo) * 0.4, 1.0 + 0.2 * (turb - 0.5));
    col = glaze(col, hex(80, 190, 200), river * 0.5 + riverS * landF * 0.15, d);

    // ---- the form at a painter's scale
    // q: mountain space, so the brush is the same size relative to every mountain, big or small.
    float2 q = p / ms;
    float2 wob = float2(noise(q / 40.0) - 0.5, noise(q / 40.0 + 17.0) - 0.5) * 7.0 * ms;
    float4 mb = mbig.eval(p + wob);
    float4 mf = mfacet.eval(p + wob * 0.5);
    float massA = smoothstep(0.35, 0.6, mb.a + 0.18 * (fbm(q / 9.0) - 0.5));
    float tBig = toneOf(mb) * massA;
    float tFacet = toneOf(mf) * smoothstep(0.4, 0.6, mf.a);
    float up = mwide.eval(p + float2(0, 70.0 * ms)).a * massA;
    // Main crest lines only: a coarse difference of Gaussians, nothing of the fine hatching survives.
    float crest = smoothstep(0.04, 0.12, onWhite(crestB.eval(p)) - onWhite(crestA.eval(p))) * smoothstep(0.3, 0.7, mount.eval(p).a);
    // Direction of the form (structure tensor of the big masses): strokes run along the facets.
    float4 J = tensor.eval(p);
    float jxx = J.r; float jyy = J.g; float jxy = (J.b - 0.5) * 2.0;
    float ang = 0.5 * atan(2.0 * jxy, jxx - jyy + 1e-6);
    float2 across = float2(cos(ang), sin(ang));
    float2 along = float2(-across.y, across.x);
    float3 wet = wetColour(up, p);
    float bleed = smoothstep(0.05, 0.55, mw.a + 0.2 * (fbm(q / 18.0) - 0.5));
    // Pigment inside a wash is never flat: granulation in the paper's valleys, two blues separating.
    float valley = 1.0 - smoothstep(0.3, 0.7, tooth);
    float dg = d + 0.35 * (valley - 0.5);
    float3 shadowC = mix(hex(96, 110, 190), hex(120, 92, 170), smoothstep(0.3, 0.7, fbm(p / 45.0 + 9.0)));
    float3 deepC = mix(hex(72, 70, 150), hex(90, 60, 130), smoothstep(0.3, 0.7, fbm(p / 45.0 + 1.0)));

    if (variant < 1.5) {
        // ---- 1. Big shapes: the mountain massed into three values as a painter squints at it - a pale wet wash over
        // the whole, a middle wash on the shadow side, a dark accent in the deepest folds; crests broken, few.
        col = glaze(col, wet, bleed * 0.3, d);
        float tj = tBig + 0.08 * (fbm(q / 26.0) - 0.5);
        col = glaze(col, shadowC, wash(tj, 0.36, p) * pool(tj, 0.36) * 0.36, dg);
        col = glaze(col, deepC, wash(tj, 0.66, p) * pool(tj, 0.66) * 0.36, dg);
        col = mix(col, paper, massA * (1.0 - smoothstep(0.04, 0.14, tj)) * smoothstep(0.4, 0.8, up) * 0.6);
        col = glaze(col, hex(50, 50, 110), crest * step(0.5, noise(p / 22.0)) * 0.4, 1.0);
    } else if (variant < 2.5) {
        // ---- 2. Strokes along the form: the mountain painted with a flat brush, each stroke following the facet it
        // lies on; dark strokes where the drawing is dark, the lit faces left as paper with a few dry touches.
        col = glaze(col, wet, bleed * 0.3, d);
        float2 sp = float2(dot(q, across) / 3.5, dot(q, along) / 34.0);
        float stroke = noise(sp) * 0.65 + noise(sp * float2(0.45, 2.2) + 7.3) * 0.35;
        float t = tFacet * 0.6 + tBig * 0.4;
        float cover1 = smoothstep(0.0, 0.08, t + 0.5 * (stroke - 0.5) - 0.2) * massA;
        float cover2 = smoothstep(0.0, 0.06, t + 0.45 * (stroke - 0.5) - 0.55) * massA;
        col = glaze(col, mix(hex(130, 160, 214), hex(150, 120, 190), smoothstep(0.3, 0.7, fbm(p / 80.0))), cover1 * 0.42, dg);
        col = glaze(col, deepC, cover2 * 0.45, dg);
        col = glaze(col, hex(60, 60, 130), (cover2 * (1.0 - cover2) * 4.0) * 0.2, 1.0);
        col = glaze(col, hex(50, 50, 110), crest * 0.3, 1.0);
    } else if (variant < 3.5) {
        // ---- 3. A wet blot, then shadows on dry: the whole mountain dropped in as one wet pool of mingling colour
        // with soft edges and a bloom; when dry, each shadow plane laid once with a hard edge. No outline at all.
        float pool = smoothstep(0.15, 0.6, mw.a + 0.25 * (fbm(q / 14.0) - 0.5));
        col = glaze(col, wet, pool * 0.42, d);
        float bloom = fbm(q / 75.0 + 2.0) + 0.04 * (noise(q / 6.0) - 0.5);
        col = glaze(col, hex(100, 100, 180), pool * (1.0 - smoothstep(0.0, 0.035, abs(bloom - 0.6))) * 0.25, 1.0);
        col = mix(col, paper, pool * smoothstep(0.62, 0.7, bloom) * 0.3);
        float t = tBig * 0.55 + tFacet * 0.45 + 0.06 * (noise(q / 18.0) - 0.5);
        col = glaze(col, shadowC, washMask(t, 0.46, 0.012) * (0.65 + 0.7 * (1.0 - smoothstep(0.46, 0.6, t))) * 0.4, dg);
        col = glaze(col, deepC, washMask(tFacet, 0.8, 0.02) * 0.3, 1.0);
    } else if (variant < 4.5) {
        // ---- 4. Lost and found edges: the crest drawn sharp where the light hits the top, the outline dissolving
        // into wet colour toward the foot; two washes, crisp above, bleeding into the land below.
        float found = smoothstep(0.25, 0.75, up);
        col = glaze(col, wet, bleed * (0.3 + 0.2 * (1.0 - found)), d);
        float t = tFacet * 0.5 + tBig * 0.5;
        float sharp = mix(0.08, 0.012, found);
        col = glaze(col, shadowC, washMask(t, 0.32, sharp) * massA * pool(t, 0.32) * 0.36, dg);
        col = glaze(col, deepC, washMask(t, 0.62, sharp) * massA * mix(1.0, pool(t, 0.62), found) * 0.36, dg);
        float rimLine = smoothstep(0.35, 0.5, mount.eval(p).a) * (1.0 - smoothstep(0.5, 0.75, mf.a));
        col = glaze(col, hex(50, 50, 110), max(crest, rimLine) * found * step(0.35, noise(p / 16.0)) * 0.7, 1.0);
    } else {
        // ---- 5. Runs: the paper tilted while wet - the colour of every plane runs downward in streaks and pools at the
        // foot with a dark tide line; the top edges stay crisp.
        // A soft run: each wash sampled a little higher, so its colour slides down 0-24 px, unevenly by column.
        float slide = 24.0 * ms * noise(float2(q.x / 14.0, 3.0));
        float4 cr = mbig.eval(p - float2(0.0, slide) + wob);
        float runA = smoothstep(0.35, 0.6, cr.a + 0.18 * (fbm(q / 9.0) - 0.5));
        float tr = max(tBig, toneOf(cr) * runA);
        col = glaze(col, wet, max(bleed * 0.25, runA * 0.32), d);
        col = glaze(col, shadowC, wash(tr, 0.36, p) * pool(tr, 0.36) * 0.36, dg);
        col = glaze(col, deepC, wash(tr, 0.68, p) * 0.3, dg);
        // The tide line where the run stopped: a dark wavy line along the slid foot.
        float tide = runA * (1.0 - massA) * (1.0 - smoothstep(0.35, 0.75, cr.a));
        col = glaze(col, hex(80, 70, 160), tide * 0.45, 1.0);
        // A few drips below the foot: thin, sparse columns, each ending in a bead.
        float colId = floor(p.x / 9.0);
        float live = step(0.86, hash(float2(colId, 7.0)));
        float len = (18.0 + 60.0 * hash(float2(colId, 3.0))) * ms;
        float above = 0.0;
        for (int k = 1; k <= 8; k++) above = max(above, smoothstep(0.4, 0.6, mbig.eval(float2((colId + 0.5) * 9.0, p.y - float(k) * len / 8.0)).a));
        float x = abs(fract(p.x / 9.0) - 0.5) * 9.0;
        float drip = live * above * (1.0 - massA) * (1.0 - smoothstep(0.6, 1.4, x));
        col = glaze(col, wetColour(0.5, p), drip * 0.4, 1.0);
        col = glaze(col, hex(50, 50, 110), crest * smoothstep(0.5, 0.9, up) * 0.35, 1.0);
    }

    col *= 1.0 - 0.035 * (tooth - 0.5);
    col += (hash(floor(p)) - 0.5) * 0.01;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> WetEffect = new(() =>
        SKRuntimeEffect.CreateShader(WetSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader WetShader(SKImage mountains, SKImage big, SKImage facets, SKImage crestA, SKImage crestB, SKImage wide,
        SKImage tensor, SKImage geo, SKImage riverSoft, Aqua aqua, float ms)
    {
        var builder = new SKRuntimeShaderBuilder(WetEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        var shaders = new List<SKShader>();
        SKShader Child(SKImage image) { var s = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear); shaders.Add(s); return s; }
        builder.Children["mount"] = Child(mountains); builder.Children["mbig"] = Child(big); builder.Children["mfacet"] = Child(facets);
        builder.Children["crestA"] = Child(crestA); builder.Children["crestB"] = Child(crestB); builder.Children["mwide"] = Child(wide);
        builder.Children["tensor"] = Child(tensor); builder.Children["geo"] = Child(geo); builder.Children["rsoft"] = Child(riverSoft);
        builder.Uniforms["variant"] = (float)aqua.Variant;
        builder.Uniforms["paper"] = Stylize.Hex(aqua.Paper);
        builder.Uniforms["ms"] = ms;
        var result = builder.Build();
        foreach (var s in shaders) s.Dispose();
        return result;
    }
}
