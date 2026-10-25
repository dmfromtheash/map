using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, «Уголь» (owner 2026-10-25: five clearly different variants of one style; the style takes the big form of
/// his mountain - silhouette, big facets, light and shadow, main crests - and draws it again in its own medium, the mark
/// sized to the mountain, no big spots on the land, the names not stylized). Charcoal is a dry medium: it sits on the
/// tooth of the paper, so a light pressure leaves only the peaks of the grain and a heavy one fills the valleys; a stump
/// smudges it into soft tone, an eraser pulls light back out, and a hatch follows the form. The form is read at the same
/// draughtsman's scale as the painter styles (blur, coarse Kuwahara, structure tensor for the direction of the masses).
/// Research: docs/research/2026-10-25-r09-charcoal-styles.md. MAP_STYLE_CHARCOAL = output folder, MAP_LIGHT_ASSETS as
/// before; MAP_STYLE_CHARCOAL_SCENE = малая|большая, MAP_STYLE_CHARCOAL_ONLY = variant numbers.
/// </summary>
public sealed partial class StyleMockupDump
{
    /// <summary>One charcoal variant: marks, media and paper. Values 0..1 unless noted.</summary>
    private sealed record Charcoal(
        int Variant,
        float Spacing,               // hatch spacing in pixels at the small scene's mountain scale
        float Smudge,                // stump tone under the lines
        float Hatch,                 // directional hatch along the form
        float Cross,                 // cross-hatch in the shadows
        float Chalk,                 // white chalk in the lights (toned paper)
        float Red,                   // sanguine in the half-tones (trois crayons)
        float Erase,                 // subtractive: the sheet toned first, the lights erased
        float Contour,               // drawn silhouette and crest accents
        float Grain, float Laid,     // paper tooth, laid lines of Ingres paper
        float SeaNear, float SeaDeep, float Land,   // values of the ground, 0 = paper, 1 = darkest
        float[] Layers,              // value thresholds of the three hatch layers
        string Paper, string Dark, string ChalkColour, string RedColour);

    [Fact]
    public void Dump_charcoal_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_CHARCOAL");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var only = Environment.GetEnvironmentVariable("MAP_STYLE_CHARCOAL_ONLY");
        var sceneOnly = Environment.GetEnvironmentVariable("MAP_STYLE_CHARCOAL_SCENE");
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var variants = CharcoalVariants().Where(v => string.IsNullOrEmpty(only) || only.Contains(v.Charcoal.Variant.ToString())).ToArray();
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
                using var flow = CoastFlow(geo);
                var ms = tag == "малая" ? 1f : 0.42f;
                using var soft = Blurred(mountains, 4f * ms);
                using var bigK = Kuwahara(soft, 3.2f * ms);
                using var big = Blurred(bigK, 2.5f * ms);
                using var facetSoft = Blurred(mountains, 1.8f * ms);
                using var facetK = Kuwahara(facetSoft, 2f * ms);
                using var facets = Blurred(facetK, 1.2f * ms);
                using var crestA = Blurred(mountains, 2.5f * ms);
                using var crestB = Blurred(mountains, 7f * ms);
                using var wide = Blurred(mountains, 14f * ms);
                using var tensor = Tensor(big);
                foreach (var (name, charcoal) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = CharcoalShader(mountains, big, facets, crestA, crestB, wide, tensor, geo, flow, charcoal, ms))
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

    private static IEnumerable<(string Name, Charcoal Charcoal)> CharcoalVariants() =>
    [
        // 1. Vine charcoal and stump (academic tonal drawing, Seurat's conté nocturnes): the masses rubbed in soft with the
        // stump, the paper's grain left in the half-tones, the lights lifted with the kneaded eraser, a few loose strokes
        // along the form and dark accents in the deepest folds.
        ("Уголь 1 · Ивовый уголь и растушёвка", new(1, 7f, 0.85f, 0.35f, 0f, 0f, 0f, 0f, 0.55f, 0.8f, 0f,
            0.42f, 0.62f, 0.05f, [0.2f, 0.55f, 0.8f], "#efeae0", "#2a2826", "#ffffff", "#000000")),
        // 2. Compressed charcoal hatching (Käthe Kollwitz, engraver's discipline in a dry medium): no smudge, the value made
        // only by line - one layer along the form, a cross layer in the shadow, a third in the deepest; a firm, broken
        // contour; the sea in level strokes that follow the shore near it.
        ("Уголь 2 · Прессованный уголь, штриховка", new(2, 5.5f, 0f, 1f, 1f, 0f, 0f, 0f, 1f, 0.5f, 0f,
            0.38f, 0.6f, 0.06f, [0.12f, 0.45f, 0.72f], "#f3efe6", "#1d1c1b", "#ffffff", "#000000")),
        // 3. Two crayons on toned paper (Prud'hon's black and white chalk on blue paper): the paper itself is the half-tone,
        // charcoal hatched and lightly smudged into the shadows, white chalk hatched into the lights - the mountain modelled
        // from the middle both ways.
        ("Уголь 3 · Два карандаша на тонированной бумаге", new(3, 6f, 0.45f, 0.8f, 0.6f, 0.95f, 0f, 0f, 0.6f, 0.65f, 0.35f,
            0.35f, 0.55f, 0f, [0.42f, 0.62f, 0.82f], "#8d98a3", "#1c1f26", "#f6f4ee", "#000000")),
        // 4. Trois crayons (Watteau, Rubens): black, red and white chalk on buff paper - sanguine for the warm half-tones and
        // the sunlit foot, black only for the shadows, white for the highest lights; the land touched warm at the coast.
        ("Уголь 4 · Три карандаша (уголь, сангина, мел)", new(4, 6f, 0.3f, 0.85f, 0.5f, 0.7f, 0.9f, 0f, 0.5f, 0.7f, 0.5f,
            0.3f, 0.5f, 0.04f, [0.45f, 0.65f, 0.85f], "#dcc7a1", "#221d1b", "#fbf6ea", "#a84a30")),
        // 5. Erased charcoal (Kentridge's drawings for projection): the whole sheet rubbed dark first, the lights pulled out
        // with the eraser in strokes along the form, ghost traces left where it went, the darkest accents drawn back in.
        ("Уголь 5 · Стёртый уголь (свет ластиком)", new(5, 9f, 1f, 0.25f, 0f, 0f, 0f, 1f, 0.7f, 0.9f, 0f,
            0.6f, 0.8f, 0.3f, [0.6f, 0.75f, 0.88f], "#ece8e0", "#201f1f", "#ffffff", "#000000")),
    ];

    private const string CharcoalSource = """
uniform shader mount;
uniform shader mbig;
uniform shader mfacet;
uniform shader crestA;
uniform shader crestB;
uniform shader mwide;
uniform shader tensor;
uniform shader geo;
uniform shader flow;
uniform float variant;
uniform float ms;
uniform float spacing;
uniform float smudge;
uniform float hatchAmt;
uniform float crossAmt;
uniform float chalk;
uniform float red;
uniform float erase;
uniform float contour;
uniform float grain;
uniform float laid;
uniform float3 ground;   // sea near, sea deep, land
uniform float3 layers;
uniform float3 cPaper; uniform float3 cDark; uniform float3 cChalk; uniform float3 cRed;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float onWhite(float4 c) { return lum(c.rgb + (1.0 - c.a)); }
float toneOf(float4 c) { return clamp((0.94 - onWhite(c)) / 0.66, 0.0, 1.0); }

// The tooth of the sheet, 0 = valley, 1 = peak: fine grain, and for Ingres paper the laid lines.
float tooth(float2 p) {
    float t = 0.55 * noise(p / 0.9) + 0.45 * fbm(p / 2.6);
    t = mix(0.5, t, grain);
    return clamp(t + laid * 0.22 * sin(p.y * 1.35 + noise(p / 30.0) * 2.0), 0.0, 1.0);
}

// How much of the sheet a pressure takes: light pressure catches only the peaks, heavy fills the valleys.
float onTooth(float pressure, float t) { return smoothstep(1.0 - pressure - 0.18, 1.0 - pressure + 0.18, t); }

// Axis of the marks: along the masses on the mountain, along the shore near it on the ground, level far from it.
float2 axisAt(float2 p, float massA) {
    float4 J = tensor.eval(p);
    float jxx = J.r; float jyy = J.g; float jxy = (J.b - 0.5) * 2.0;
    float ang = 0.5 * atan(2.0 * jxy, jxx - jyy + 1e-6);
    float2 along = float2(-sin(ang), cos(ang));
    float2 fallback = normalize(float2(0.5, -0.86));
    float2 m = normalize(mix(fallback, along, smoothstep(0.0, 0.0006, jxx + jyy)));
    // The ground is hatched level, as a draughtsman lays a sea: a field that turns would warp the lines into moiré.
    // The shore itself is drawn by the water lines that follow it.
    float2 a = massA > 0.5 ? m : normalize(float2(1.0, 0.07));
    return a.x < 0.0 ? -a : a;
}

float2 turn(float2 a, float r) { return float2(a.x * cos(r) - a.y * sin(r), a.x * sin(r) + a.y * cos(r)); }

// One layer of hatching: broken strokes of a given spacing along the axis, each tapering at its ends; width 0..1 of the
// spacing. Returns the coverage of the line (before the paper tooth).
float hatch(float2 p, float2 ax, float sp, float width, float seed) {
    float2 pr = float2(-ax.y, ax.x);
    float s = dot(p, pr) / sp + (noise(p / (sp * 9.0) + seed) - 0.5) * 0.7;
    float row = floor(s);
    float a = dot(p, ax) / (sp * 7.0) + hash(float2(row, seed)) * 13.0;
    float seg = floor(a); float fa = fract(a);
    float on = step(0.2, hash(float2(row * 1.7 + seed, seg)));
    float taper = smoothstep(0.0, 0.15, fa) * (1.0 - smoothstep(0.75, 1.0, fa));
    float d = abs(fract(s) - 0.5) * 2.0;
    float w = width * (0.45 + 0.55 * taper);
    return on * (1.0 - smoothstep(w * 0.55, w, d));
}

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float jit = (fbm(p / 16.0) - 0.5) * 3.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    float2 q = p / ms;
    float4 mb = mbig.eval(p);
    float4 mf = mfacet.eval(p);
    float massA = smoothstep(0.38, 0.6, mb.a + 0.12 * (fbm(q / 9.0) - 0.5));
    float tBig = toneOf(mb);
    float tFacet = toneOf(mf);
    float crest = smoothstep(0.05, 0.13, onWhite(crestB.eval(p)) - onWhite(crestA.eval(p))) * smoothstep(0.3, 0.7, mount.eval(p).a);
    float t = tooth(p);

    // ---- the values as the draughtsman masses them: the sea darker with depth, the land nearly the paper with a little
    // shading along its coast, the mountain from its big form (soft) and its facets (crisp).
    float seaT = mix(ground.x, ground.y, smoothstep(6.0, 180.0, dL));
    float landT = ground.z + 0.12 * (1.0 - smoothstep(3.0, 50.0, dW));
    float gT = mix(seaT, landT, landF);
    float soft = mix(gT, tBig, massA);
    float crisp = mix(gT, clamp(mix(tBig, tFacet, 0.6), 0.0, 1.0), massA);

    float2 ax = axisAt(p, massA);
    float sp = spacing * mix(1.0, ms, massA) * mix(1.0, 1.25, 1.0 - massA);

    // ---- stump tone: rubbed into the tooth, it half fills the valleys.
    float rub = soft * smudge;
    float dark = rub * mix(onTooth(rub, t), 1.0, 0.45);

    // ---- subtractive: the sheet rubbed dark first, the lights then pulled out with the eraser along the form.
    if (erase > 0.5) {
        float base = mix(0.5, 0.92, soft) * mix(0.8, 1.0, onTooth(0.8, t));
        float lift = 1.0 - smoothstep(0.18, 0.5, crisp);
        float strokes = hatch(p, turn(ax, 0.1), sp * 2.4, 0.8, 7.0);
        // On the mountain the eraser follows the form in strokes; on the land it is swept broadly and unevenly.
        float sweep = mix(0.8, 1.0, noise(float2(p.x / 60.0, p.y / 9.0))) * mix(0.85, 1.0, strokes);
        float lifted = lift * mix(sweep, mix(0.55, 1.0, strokes), massA) * mix(0.75, 1.0, noise(q / 6.0));
        // Ghost traces: the eraser never takes it all.
        dark = base * (1.0 - lifted * 0.85) + 0.05 * lifted * noise(p / 3.0);
    }

    // ---- hatching: one layer along the form, a cross layer in the shadow, a third in the deepest.
    float l1 = smoothstep(layers.x, layers.x + 0.12, crisp) * hatch(p, ax, sp, 0.32 + 0.4 * crisp, 1.0);
    float l2 = smoothstep(layers.y, layers.y + 0.12, crisp) * hatch(p, turn(ax, 0.62), sp * 1.05, 0.3 + 0.4 * crisp, 2.0);
    float l3 = smoothstep(layers.z, layers.z + 0.1, crisp) * hatch(p, turn(ax, -0.55), sp * 0.95, 0.35 + 0.35 * crisp, 3.0);
    // At sea the hatch begins beyond the water lines, so the shore keeps its own drawing.
    float offshore = mix(smoothstep(22.0, 40.0, dL), 1.0, max(massA, landF));
    l1 *= offshore; l2 *= offshore; l3 *= offshore;
    float pressure = 0.55 + 0.45 * crisp;
    float lines =1.0 - (1.0 - l1 * hatchAmt) * (1.0 - l2 * crossAmt) * (1.0 - l3 * crossAmt);
    lines *= mix(onTooth(pressure, t), 1.0, 0.3);
    dark = 1.0 - (1.0 - dark) * (1.0 - lines * 0.9);

    // ---- the drawn line: a broken silhouette and dark accents in the deepest folds along the crests; the coast drawn.
    float edge = smoothstep(0.3, 0.45, mb.a) * (1.0 - smoothstep(0.45, 0.62, mb.a)) * step(0.3, noise(p / (11.0 * ms)));
    float coast = (1.0 - smoothstep(0.6, 2.0, max(abs(dL), abs(dW)) * 0.5 + 0.6 * noise(p / 5.0)));
    float drawn = max(max(edge, crest * step(0.45, noise(p / (16.0 * ms))) * 0.8), coast * 0.8) * contour;
    dark = max(dark, drawn * onTooth(0.85, t) * 0.92);

    // Water lines: three or four broken strokes following the shore at growing distances, fainter outwards - the
    // map-maker's way of drawing a coast by hand.
    float gap = 7.0;
    float s = max(dL, 0.0) / gap;
    float k = floor(s);
    float fs = fract(s);
    float dLine = min(fs, 1.0 - fs);
    float wl = (1.0 - smoothstep(0.07, 0.15, dLine)) * step(0.5, s) * (1.0 - smoothstep(2.5, 4.5, s))
        * step(0.28, noise(p / (gap * 3.5) + k * 7.0)) * (1.0 - landF);
    dark = 1.0 - (1.0 - dark) * (1.0 - wl * (0.75 - 0.12 * k) * onTooth(0.75, t) * max(contour, 0.5));

    float3 col = mix(cPaper, cDark, clamp(dark, 0.0, 1.0));

    // ---- sanguine: the warm half-tones of the mountain and the sunlit foot, a warm touch along the coast of the land.
    if (red > 0.0) {
        float midTone = smoothstep(0.12, 0.3, crisp) * (1.0 - smoothstep(0.6, 0.8, crisp)) * massA;
        float foot = massA * (1.0 - smoothstep(0.1, 0.5, mwide.eval(p + float2(0, 70.0 * ms)).a)) * 0.6;
        float shoreWarm = landF * (1.0 - smoothstep(2.0, 26.0, dW)) * 0.55;
        float rl = hatch(p, turn(ax, 0.25), sp, 0.55, 4.0);
        float r = max(max(midTone, foot) * mix(0.45, 1.0, rl), shoreWarm * mix(0.5, 1.0, rl)) * red;
        col = mix(col, cRed, r * onTooth(0.6, t) * (1.0 - dark * 0.8));
    }

    // ---- white chalk in the lights: hatched on the lit facets and the crests, sitting on the peaks of the grain.
    if (chalk > 0.0) {
        float light = (1.0 - smoothstep(0.08, 0.32, crisp)) * massA;
        float cl = hatch(p, turn(ax, -0.3), sp * 0.9, 0.5, 5.0);
        float riverLight = river * 0.8;
        float c = max(light * mix(0.35, 1.0, cl), riverLight) * chalk;
        col = mix(col, cChalk, c * onTooth(0.7, t));
    }

    // Rivers on white paper: the bed left clean, its banks drawn.
    if (chalk <= 0.0) {
        col = mix(col, cPaper, river * 0.85);
        float bank = smoothstep(0.08, 0.3, river) * (1.0 - smoothstep(0.35, 0.65, river));
        col = mix(col, cDark, bank * 0.7 * onTooth(0.8, t) * step(0.2, noise(p / 9.0)));
    }

    // The paper's own tone under it all, warmer in the valleys of the grain.
    col *= 1.0 - 0.035 * (0.5 - t) * grain;
    col += (hash(floor(p)) - 0.5) * 0.01;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> CharcoalEffect = new(() =>
        SKRuntimeEffect.CreateShader(CharcoalSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader CharcoalShader(SKImage mountains, SKImage big, SKImage facets, SKImage crestA, SKImage crestB, SKImage wide,
        SKImage tensor, SKImage geo, SKImage flow, Charcoal c, float ms)
    {
        var builder = new SKRuntimeShaderBuilder(CharcoalEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        var shaders = new List<SKShader>();
        SKShader Child(SKImage image) { var s = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear); shaders.Add(s); return s; }
        builder.Children["mount"] = Child(mountains); builder.Children["mbig"] = Child(big); builder.Children["mfacet"] = Child(facets);
        builder.Children["crestA"] = Child(crestA); builder.Children["crestB"] = Child(crestB); builder.Children["mwide"] = Child(wide);
        builder.Children["tensor"] = Child(tensor); builder.Children["geo"] = Child(geo); builder.Children["flow"] = Child(flow);
        builder.Uniforms["variant"] = (float)c.Variant;
        builder.Uniforms["ms"] = ms;
        builder.Uniforms["spacing"] = c.Spacing;
        builder.Uniforms["smudge"] = c.Smudge;
        builder.Uniforms["hatchAmt"] = c.Hatch;
        builder.Uniforms["crossAmt"] = c.Cross;
        builder.Uniforms["chalk"] = c.Chalk;
        builder.Uniforms["red"] = c.Red;
        builder.Uniforms["erase"] = c.Erase;
        builder.Uniforms["contour"] = c.Contour;
        builder.Uniforms["grain"] = c.Grain;
        builder.Uniforms["laid"] = c.Laid;
        builder.Uniforms["ground"] = new[] { c.SeaNear, c.SeaDeep, c.Land };
        builder.Uniforms["layers"] = c.Layers;
        builder.Uniforms["cPaper"] = Stylize.Hex(c.Paper); builder.Uniforms["cDark"] = Stylize.Hex(c.Dark);
        builder.Uniforms["cChalk"] = Stylize.Hex(c.ChalkColour); builder.Uniforms["cRed"] = Stylize.Hex(c.RedColour);
        var result = builder.Build();
        foreach (var s in shaders) s.Dispose();
        return result;
    }
}
