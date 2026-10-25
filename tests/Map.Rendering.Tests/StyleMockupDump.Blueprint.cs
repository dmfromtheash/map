using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, «Синька» (owner 2026-10-25: five clearly different variants of one style; the style takes the big form of
/// his mountain - silhouette, big facets, light and shadow, main crests - and draws it again in its own medium, the mark
/// sized to the mountain, no big spots on the land, the names not stylized). The blueprint family is a contact print of
/// a drawing: in the cyanotype blueprint the paper turns Prussian blue where light reached it and the drawn lines stay
/// white; the diazo whiteprint is the reverse (blue-violet lines on a pale sheet); Anna Atkins' cyanotype photograms
/// keep the tones of what lay on the sheet; the surveyor's blueprint adds a grid and contour lines; the architect's
/// presentation print is worked over with white gouache. The hatching is laid in patches anchored at their centres, each
/// along the form there (structure tensor), as the program can draw it. Research:
/// docs/research/2026-10-25-r09-blueprint-styles.md. MAP_STYLE_BLUEPRINT = output folder, MAP_LIGHT_ASSETS as before;
/// MAP_STYLE_BLUEPRINT_SCENE = малая|большая, MAP_STYLE_BLUEPRINT_ONLY = variant numbers.
/// </summary>
public sealed partial class StyleMockupDump
{
    /// <summary>One blueprint variant: the sheet, the line and how the form is drawn.</summary>
    private sealed record Blueprint(int Variant, float Spacing, string Paper, string Paper2, string Ink, float[] Layers);

    [Fact]
    public void Dump_blueprint_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_BLUEPRINT");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var only = Environment.GetEnvironmentVariable("MAP_STYLE_BLUEPRINT_ONLY");
        var sceneOnly = Environment.GetEnvironmentVariable("MAP_STYLE_BLUEPRINT_SCENE");
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var variants = BlueprintVariants().Where(v => string.IsNullOrEmpty(only) || only.Contains(v.Blueprint.Variant.ToString())).ToArray();
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
                var ms = tag == "малая" ? 1f : 0.42f;
                using var soft = Blurred(mountains, 4f * ms);
                using var bigK = Kuwahara(soft, 3.2f * ms);
                using var big = Blurred(bigK, 2.5f * ms);
                using var facetSoft = Blurred(mountains, 1.8f * ms);
                using var facetK = Kuwahara(facetSoft, 2f * ms);
                using var facets = Blurred(facetK, 1.2f * ms);
                using var crestA = Blurred(mountains, 2.5f * ms);
                using var crestB = Blurred(mountains, 7f * ms);
                using var tensor = Tensor(big);
                foreach (var (name, blueprint) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = BlueprintShader(mountains, big, facets, crestA, crestB, tensor, geo, blueprint, ms))
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

    private static IEnumerable<(string Name, Blueprint Blueprint)> BlueprintVariants() =>
    [
        // 1. The cyanotype blueprint (Herschel's process, the drawing office's copy from 1870 to 1940): Prussian blue
        // sheet, the drawn line left white - a firm silhouette, crest lines, the shadow sides hatched in white along the
        // form; the coast and its water lines white; the sea a little deeper blue; the coating faintly mottled.
        ("Синька 1 · Классическая синька, белые линии", new(1, 6f, "#1f5296", "#173f7a", "#eef4fb", [0.42f, 0.64f, 0.84f])),
        // 2. The diazo whiteprint (blueline, 1950s on): a pale, slightly yellowed sheet, violet-blue lines a little fuzzy
        // from the ammonia; the drawing positive - shadows hatched blue, the sea hatched level beyond the water lines.
        ("Синька 2 · Диазокопия, синие линии на белом", new(2, 5.5f, "#f1efe6", "#e9e2c8", "#39408f", [0.14f, 0.46f, 0.74f])),
        // 3. Anna Atkins' cyanotype photogram (British Algae, 1843): no line at all - the mountain kept in its tones of
        // blue, white where it blocked the light, deep where the light went through; the land a pale print, the sea the
        // full exposure; the emulsion brushed on, with its streaks.
        ("Синька 3 · Цианотипия Аткинс, тон без линий", new(3, 6f, "#f2f4ef", "#123a7a", "#123a7a", [0f, 0f, 0f])),
        // 4. The surveyor's blueprint: a coordinate grid, the mountain drawn by its form lines (contours of its values)
        // and bold crest lines, the sea by depth lines following the shore; nothing else - a technical drawing.
        ("Синька 4 · Топографическая синька, сетка и горизонтали", new(4, 6f, "#21538f", "#1a4479", "#e6f0fa", [0f, 0f, 0f])),
        // 5. The architect's presentation print: a blueprint worked over with white gouache - the lit planes of the
        // mountain laid in opaque white with a dry brush, the shadows left blue with a light white hatch, white water lines.
        ("Синька 5 · Синька с белой гуашью", new(5, 7f, "#1c4b8d", "#163c72", "#f4f7fb", [0.5f, 0.72f, 0.9f])),
    ];

    private const string BlueprintSource = """
uniform shader mount;
uniform shader mbig;
uniform shader mfacet;
uniform shader crestA;
uniform shader crestB;
uniform shader tensor;
uniform shader geo;
uniform float variant;
uniform float ms;
uniform float spacing;
uniform float3 layers;
uniform float3 cPaper; uniform float3 cPaper2; uniform float3 cInk;

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
float2 turn(float2 a, float r) { return float2(a.x * cos(r) - a.y * sin(r), a.x * sin(r) + a.y * cos(r)); }

float hatch(float2 p, float2 ax, float sp, float width, float seed) {
    float2 pr = float2(-ax.y, ax.x);
    float s = dot(p, pr) / sp + (noise(p / (sp * 9.0) + seed) - 0.5) * 0.5;
    float row = floor(s);
    float a = dot(p, ax) / (sp * 8.0) + hash(float2(row, seed)) * 13.0;
    float seg = floor(a); float fa = fract(a);
    float on = step(0.15, hash(float2(row * 1.7 + seed, seg)));
    float taper = smoothstep(0.0, 0.1, fa) * (1.0 - smoothstep(0.85, 1.0, fa));
    float d = abs(fract(s) - 0.5) * 2.0;
    float w = width * (0.5 + 0.5 * taper);
    return on * (1.0 - smoothstep(w * 0.6, w, d));
}

float2 alongAt(float2 c) {
    float4 J = tensor.eval(c);
    float jxx = J.r; float jyy = J.g; float jxy = (J.b - 0.5) * 2.0;
    float ang = 0.5 * atan(2.0 * jxy, jxx - jyy + 1e-6);
    float2 along = float2(-sin(ang), cos(ang));
    return normalize(mix(normalize(float2(0.5, -0.86)), along, smoothstep(0.0, 0.0006, jxx + jyy)));
}

// The value at a point (0 = lit, 1 = darkest) and its rate of change, for contour lines a pixel wide.
float valueAt(float2 p) { return toneOf(mbig.eval(p)); }

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
    float massA = smoothstep(0.38, 0.6, mb.a + 0.1 * (fbm(q / 9.0) - 0.5));
    float tBig = toneOf(mb);
    float crisp = clamp(mix(tBig, toneOf(mf), 0.6), 0.0, 1.0);
    float crest = smoothstep(0.05, 0.13, onWhite(crestB.eval(p)) - onWhite(crestA.eval(p))) * smoothstep(0.5, 0.7, mount.eval(p).a);
    float edge = smoothstep(0.32, 0.45, mb.a) * (1.0 - smoothstep(0.45, 0.6, mb.a));
    float coast = 1.0 - smoothstep(0.5, 1.6, max(abs(dL), abs(dW)) * 0.5 + 0.4 * noise(p / 5.0));
    float bank = smoothstep(0.08, 0.3, river) * (1.0 - smoothstep(0.35, 0.65, river));

    // Water lines following the shore, fainter outwards.
    float gap = 7.0;
    float s = max(dL, 0.0) / gap; float k = floor(s); float fs = fract(s);
    float wl = (1.0 - smoothstep(0.06, 0.13, min(fs, 1.0 - fs))) * step(0.5, s) * (1.0 - smoothstep(2.5, 4.5, s))
        * step(0.25, noise(p / (gap * 3.5) + k * 7.0)) * (1.0 - landF) * (0.9 - 0.15 * k);

    // Hatching in patches anchored at their centres, each along the form there.
    float sp = spacing * ms;
    float cell = spacing * 6.0;
    float2 gc = floor(p / cell);
    float best = -1.0; float2 ax = normalize(float2(0.5, -0.86)); float2 anchor = (gc + 0.5) * cell;
    for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
            float2 cid = gc + float2(float(i), float(j));
            float2 c = (cid + 0.5 + (float2(hash(cid), hash(cid + 11.0)) - 0.5) * 0.8) * cell;
            float pri = hash(cid + 19.7);
            if (pri > best) {
                float2 al = alongAt(c);
                float2 dd = p - c;
                float u = dot(dd, al) / (cell * 0.95);
                float v = dot(dd, float2(-al.y, al.x)) / (cell * 0.8);
                if (abs(u) < 1.0 + (noise(float2(v * 2.5, pri * 31.0)) - 0.5) * 0.3 && abs(v) < 1.0) { best = pri; ax = al; anchor = c; }
            }
        }
    }
    if (ax.x < 0.0) ax = -ax;
    float2 lp = p - anchor;
    float aseed = hash(anchor * 0.013) * 50.0;
    float l1 = smoothstep(layers.x, layers.x + 0.1, crisp) * hatch(lp, ax, sp, 0.3 + 0.3 * crisp, 1.0 + aseed);
    float l2 = smoothstep(layers.y, layers.y + 0.1, crisp) * hatch(lp, turn(ax, 0.62), sp * 1.05, 0.28 + 0.3 * crisp, 2.0 + aseed);
    float l3 = smoothstep(layers.z, layers.z + 0.1, crisp) * hatch(lp, turn(ax, -0.55), sp * 0.95, 0.3 + 0.3 * crisp, 3.0 + aseed);
    float lines = (1.0 - (1.0 - l1) * (1.0 - l2) * (1.0 - l3)) * massA;

    // The sheet: the coating faintly mottled, a streak of the brush, the fibres.
    float mottle = fbm(p / 140.0) - 0.5;
    float streak = noise(float2(p.x / 400.0, p.y / 9.0)) - 0.5;
    float fibre = noise(p / 1.3) - 0.5;
    float3 paper = mix(cPaper, cPaper2, clamp(0.5 + mottle * 1.2, 0.0, 1.0));
    float ink = 0.0;
    float3 col = paper;

    if (variant < 1.5) {
        // White lines on Prussian blue; the sea one exposure deeper.
        col = mix(cPaper, cPaper2, (1.0 - landF) * (0.35 + 0.5 * smoothstep(10.0, 200.0, dL)));
        col *= 1.0 + mottle * 0.12 + streak * 0.03 + fibre * 0.03;
        ink = max(max(edge * step(0.25, noise(p / (12.0 * ms))) , crest * 0.85), coast);
        ink = max(ink, lines * 0.85);
        ink = max(ink, wl * 0.8);
        ink = max(ink, bank * 0.9);
        col = mix(col, cInk, clamp(ink, 0.0, 1.0) * 0.92);
    } else if (variant < 2.5) {
        // Diazo: pale sheet yellowing at random, fuzzy violet-blue lines; the sea hatched level beyond the water lines.
        col = mix(cPaper, cPaper2, clamp(0.3 + mottle * 1.5, 0.0, 1.0));
        col *= 1.0 + fibre * 0.02;
        float2 lev = normalize(float2(1.0, 0.06));
        float seaT = (1.0 - landF) * mix(0.3, 0.55, smoothstep(10.0, 200.0, dL)) * smoothstep(24.0, 40.0, dL);
        float sea = smoothstep(0.2, 0.3, seaT) * hatch(p, lev, spacing * 1.3, 0.25 + 0.4 * seaT, 9.0);
        ink = max(max(edge * step(0.3, noise(p / (11.0 * ms))), crest * 0.7), coast * 0.9);
        ink = max(ink, lines);
        ink = max(ink, max(sea * 0.8, wl * 0.8));
        ink = max(ink, bank * 0.85);
        col = mix(col, cInk, clamp(ink, 0.0, 1.0) * 0.88);
        // A faint bluish bloom around every line, as the diazo ammonia spreads it.
        col = mix(col, cInk, 0.05 * smoothstep(0.0, 0.6, ink));
    } else if (variant < 3.5) {
        // Photogram: exposure in blue; white where blocked; brushed emulsion streaks.
        float e = mix(0.95, 0.12 + 0.12 * (1.0 - smoothstep(3.0, 50.0, dW)), landF);
        float mtn = 0.08 + 0.85 * clamp(mix(tBig, crisp, 0.35), 0.0, 1.0);
        e = mix(e, mtn, massA);
        e = mix(e, 0.04, river * 0.9);
        // The emulsion brushed on in broad passes: a soft, wide streak, not a striped sheet.
        float pass = noise(float2(p.x / 700.0, p.y / 70.0)) - 0.5;
        e = clamp(e + mottle * 0.15 + pass * 0.05 + fibre * 0.03, 0.0, 1.0);
        col = mix(cPaper, cInk, e);
        // Exposure through the paper softens edges: a little halo of light blue around the white.
        col = mix(col, mix(cPaper, cInk, 0.35), edge * 0.25);
    } else if (variant < 4.5) {
        // Surveyor's print: grid, form lines of the mountain, crest lines, depth lines of the sea.
        col = paper * (1.0 + mottle * 0.1 + fibre * 0.03);
        float2 gp = abs(fract(p / 128.0 + 0.5) - 0.5) * 128.0;
        float grid = (1.0 - smoothstep(0.4, 1.0, min(gp.x, gp.y))) * 0.45;
        float2 gq = abs(fract(p / 32.0 + 0.5) - 0.5) * 32.0;
        grid = max(grid, (1.0 - smoothstep(0.3, 0.8, min(gq.x, gq.y))) * 0.14);
        float v0 = valueAt(p);
        float vx = valueAt(p + float2(1.5, 0)) - valueAt(p - float2(1.5, 0));
        float vy = valueAt(p + float2(0, 1.5)) - valueAt(p - float2(0, 1.5));
        float gradV = length(float2(vx, vy)) / 3.0;
        float levels = 5.0;
        float fv = fract(v0 * levels);
        float dpx = min(fv, 1.0 - fv) / max(gradV * levels, 1e-4);
        // A flat plateau sitting exactly on a level is no line: contours only where the value really slopes.
        float iso = (1.0 - smoothstep(0.5, 1.2, dpx)) * smoothstep(0.45, 0.6, mb.a) * smoothstep(0.004, 0.012, gradV);
        float index = step(0.5, fract(floor(v0 * levels + 0.5) / 2.0));
        float d = max(dL, 0.0) / 18.0; float fd = fract(d);
        float depth = (1.0 - smoothstep(0.04, 0.09, min(fd, 1.0 - fd))) * step(0.5, d) * (1.0 - smoothstep(4.0, 9.0, d))
            * (1.0 - landF) * step(0.2, noise(p / 40.0 + floor(d)));
        ink = max(grid * (1.0 - massA * 0.7), iso * mix(0.6, 0.95, index));
        ink = max(ink, max(crest, edge) * 0.95);
        ink = max(ink, coast);
        ink = max(ink, depth * 0.55);
        ink = max(ink, bank * 0.9);
        col = mix(col, cInk, clamp(ink, 0.0, 1.0) * 0.9);
    } else {
        // Presentation print: white gouache on the lit planes (dry brush on the tooth), light white hatch in the shadow.
        col = mix(cPaper, cPaper2, (1.0 - landF) * (0.4 + 0.5 * smoothstep(10.0, 200.0, dL)));
        col *= 1.0 + mottle * 0.1 + streak * 0.03 + fibre * 0.03;
        float tooth = 0.55 * noise(p / 0.9) + 0.45 * fbm(p / 2.6);
        float lit = (1.0 - smoothstep(0.1, 0.45, crisp)) * massA;
        float brush = smoothstep(0.25, 0.6, lit + (noise(float2(dot(p, ax) / 30.0, dot(p, float2(-ax.y, ax.x)) / 4.0)) - 0.5) * 0.35);
        float gouache = brush * smoothstep(0.15, 0.45, tooth + lit * 0.6);
        float landWash = landF * (1.0 - massA) * 0.12 * smoothstep(0.3, 0.7, tooth + 0.2 * noise(p / 25.0));
        col = mix(col, cInk, clamp(gouache * 0.92 + landWash, 0.0, 1.0));
        ink = max(max(edge * step(0.3, noise(p / (12.0 * ms))), crest * 0.6), coast * 0.95);
        ink = max(ink, lines * 0.55 * (1.0 - gouache));
        ink = max(ink, wl * 0.75);
        ink = max(ink, bank * 0.9);
        col = mix(col, cInk, clamp(ink, 0.0, 1.0) * 0.9);
    }
    col += (hash(floor(p)) - 0.5) * 0.008;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> BlueprintEffect = new(() =>
        SKRuntimeEffect.CreateShader(BlueprintSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader BlueprintShader(SKImage mountains, SKImage big, SKImage facets, SKImage crestA, SKImage crestB,
        SKImage tensor, SKImage geo, Blueprint b, float ms)
    {
        var builder = new SKRuntimeShaderBuilder(BlueprintEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        var shaders = new List<SKShader>();
        SKShader Child(SKImage image) { var s = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear); shaders.Add(s); return s; }
        builder.Children["mount"] = Child(mountains); builder.Children["mbig"] = Child(big); builder.Children["mfacet"] = Child(facets);
        builder.Children["crestA"] = Child(crestA); builder.Children["crestB"] = Child(crestB);
        builder.Children["tensor"] = Child(tensor); builder.Children["geo"] = Child(geo);
        builder.Uniforms["variant"] = (float)b.Variant;
        builder.Uniforms["ms"] = ms;
        builder.Uniforms["spacing"] = b.Spacing;
        builder.Uniforms["layers"] = b.Layers;
        builder.Uniforms["cPaper"] = Stylize.Hex(b.Paper); builder.Uniforms["cPaper2"] = Stylize.Hex(b.Paper2);
        builder.Uniforms["cInk"] = Stylize.Hex(b.Ink);
        var result = builder.Build();
        foreach (var s in shaders) s.Dispose();
        return result;
    }
}
