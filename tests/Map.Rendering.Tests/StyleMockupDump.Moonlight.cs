using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, «Лунная ночь» (owner 2026-10-25/26: five clearly different variants of one style; the style takes the big
/// form of his mountain - silhouette, big facets, light and shadow, main crests - and draws it again in its own medium,
/// the mark sized to the mountain, no big spots on the land, the names not stylized). The moon is a cold light from the
/// north-west: the lit facets of the owner's painting take it, the shadow sides sink into the night. Whistler's
/// nocturnes are thin tonal washes; Kuindzhi's «Moonlit night on the Dnieper» is near-black ground with glowing lit
/// planes; Kawase Hasui's shin-hanga night prints are flat indigo steps with bokashi gradation and a dark key block;
/// scratchboard scratches the light out of black along the form; pastel on dark paper lays the light in chalk on the
/// tooth. Research: docs/research/2026-10-26-r09-moonlight-styles.md. MAP_STYLE_MOONLIGHT = output folder,
/// MAP_LIGHT_ASSETS as before; MAP_STYLE_MOONLIGHT_SCENE = малая|большая, MAP_STYLE_MOONLIGHT_ONLY = variant numbers.
/// </summary>
public sealed partial class StyleMockupDump
{
    /// <summary>One night variant: night ground, sea, the shadow and lit colour of the mountains, the moon's mark.</summary>
    private sealed record Moonlight(int Variant, float Spacing, string Ground, string Sea, string Shadow, string Lit, string Glint);

    [Fact]
    public void Dump_moonlight_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_MOONLIGHT");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var only = Environment.GetEnvironmentVariable("MAP_STYLE_MOONLIGHT_ONLY");
        var sceneOnly = Environment.GetEnvironmentVariable("MAP_STYLE_MOONLIGHT_SCENE");
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var variants = MoonlightVariants().Where(v => string.IsNullOrEmpty(only) || only.Contains(v.Moonlight.Variant.ToString())).ToArray();
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
                foreach (var (name, moonlight) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = MoonlightShader(mountains, big, facets, crestA, crestB, tensor, geo, moonlight, ms))
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

    private static IEnumerable<(string Name, Moonlight Moonlight)> MoonlightVariants() =>
    [
        // 1. Whistler's nocturne: thin blue-grey washes laid one over another, edges lost in the dark, almost no detail;
        // the lit planes only a breath lighter; the sea a quiet darker sheet with a faint path of moonlight.
        ("Лунная ночь 1 · Ноктюрн Уистлера, тональные заливки", new(1, 7f, "#2b3a47", "#1c2935", "#18222d", "#8aa1ad", "#c9d6d8")),
        // 2. Kuindzhi's moonlight: near-black ground, the lit planes glowing cold green-white against it, the shadow
        // sides black; crests catch a thin rim of light; the sea black with a silver path of glints and a lit wet shore.
        ("Лунная ночь 2 · Лунный свет Куинджи, сияющие грани", new(2, 7f, "#141c17", "#0b1219", "#0a0f0d", "#d6ecdc", "#eef6ee")),
        // 3. Kawase Hasui's night print (shin-hanga): indigo in three flat steps cut by the light, bokashi gradation of
        // the sea away from the shore, a dark key block outlining the mountain and its crests, a faint wood grain.
        ("Лунная ночь 3 · Ночная гравюра Хасуи, индиго и бокаси", new(3, 7f, "#2c3d6b", "#1a2550", "#0f1734", "#9fb3d9", "#e6ecf7")),
        // 4. Scratchboard: a black sheet with the light scratched out in white along the form, denser where the moon
        // strikes; the coast one scratched line, the sea level scratches fading out, a few glints of the path.
        ("Лунная ночь 4 · Граттаж, свет выцарапан по форме", new(4, 7.5f, "#0e0f12", "#0a0b0e", "#0e0f12", "#eef0f2", "#f4f5f6")),
        // 5. Pastel on dark paper: a dark blue sheet with its tooth; the light laid in soft chalk along the planes - pale
        // on the lit facets, a middle blue on the turning ones - the land scumbled lightly, the shore a chalk line.
        ("Лунная ночь 5 · Пастель на тёмной бумаге", new(5, 8f, "#22324d", "#1b2740", "#1b2740", "#d3deec", "#e9eff7")),
    ];

    private const string MoonlightSource = """
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
uniform float3 cGround; uniform float3 cSea; uniform float3 cShadow; uniform float3 cLit; uniform float3 cGlint;

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
    // Moonlight on the form: the painting's own light (lit = 1) taken a little harder, as a single cold source.
    float light = 1.0 - crisp;
    float crest = smoothstep(0.05, 0.13, onWhite(crestB.eval(p)) - onWhite(crestA.eval(p))) * smoothstep(0.5, 0.7, mount.eval(p).a);
    float edge = smoothstep(0.32, 0.45, mb.a) * (1.0 - smoothstep(0.45, 0.6, mb.a));
    float coast = 1.0 - smoothstep(0.5, 1.6, max(abs(dL), abs(dW)) * 0.5 + 0.4 * noise(p / 5.0));
    float bank = smoothstep(0.08, 0.3, river) * (1.0 - smoothstep(0.35, 0.65, river));
    float sea = 1.0 - landF;
    float deep = smoothstep(10.0, 220.0, dL);

    // The moon's glitter on open water: short level dashes, densest in a broad soft band.
    // One path of moonlight across the sea (the moon in the north-west), not glitter everywhere.
    float band = 1.0 - smoothstep(0.03, 0.12, abs(fract((p.x * 0.55 + p.y) / 2600.0 + 0.2) - 0.5));
    float dash = step(0.86, noise(float2(p.x / (14.0 * ms), p.y / (2.2 * ms))));
    float glint = dash * band * sea * smoothstep(6.0, 30.0, dL) * (0.6 + 0.4 * noise(p / 60.0));

    // Water lines along the shore.
    float gap = 7.0;
    float s = max(dL, 0.0) / gap; float k = floor(s); float fs = fract(s);
    float wl = (1.0 - smoothstep(0.06, 0.13, min(fs, 1.0 - fs))) * step(0.5, s) * (1.0 - smoothstep(2.5, 4.5, s))
        * step(0.25, noise(p / (gap * 3.5) + k * 7.0)) * sea * (0.9 - 0.15 * k);

    // Patches anchored at their centres, each along the form there (as the program lays strokes).
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

    float mottle = fbm(p / 140.0) - 0.5;
    float fibre = noise(p / 1.3) - 0.5;
    float3 col = cGround;

    if (variant < 1.5) {
        // Nocturne: washes laid level across the sheet, the forms only a breath lighter where the moon reaches.
        float wash = noise(float2(p.x / 700.0, p.y / 45.0)) - 0.5;
        float3 ground = cGround * (1.0 + mottle * 0.10 + wash * 0.06);
        float3 water = mix(cGround, cSea, 0.55 + 0.45 * deep) * (1.0 + wash * 0.08);
        col = mix(water, ground, landF);
        float lift = pow(clamp(light, 0.0, 1.0), 1.4);
        float3 mtn = mix(cShadow, cLit, lift * 0.75);
        // The edge of each mountain dissolves into the night instead of a drawn line.
        float soft = smoothstep(0.3, 0.62, mb.a);
        col = mix(col, mtn, soft * (0.85 + 0.15 * noise(q / 6.0)));
        col = mix(col, cLit, crest * light * 0.25 * soft);
        col = mix(col, cGlint, glint * 0.35 + coast * 0.12 * sea);
        col = mix(col, cSea * 0.8, bank * 0.5);
    } else if (variant < 2.5) {
        // Kuindzhi: everything dark except where the moon strikes; the light glows, the shadow is black.
        float3 ground = cGround * (1.0 + mottle * 0.12 + fibre * 0.03);
        float3 water = mix(cSea * 1.15, cSea * 0.8, deep);
        col = mix(water, ground, landF);
        float glow = smoothstep(0.42, 0.9, light);
        float3 mtn = mix(cShadow, cLit, glow);
        mtn = mix(mtn, cShadow * 2.2, smoothstep(0.2, 0.45, light) * (1.0 - glow) * 0.5);
        col = mix(col, mtn, massA);
        // A rim of moonlight on the crests and the lit edge, a faint halo around what glows.
        // Matte, not glossy: only the crests catching the light get a thin rim.
        col = mix(col, cGlint, clamp(crest * smoothstep(0.45, 0.75, light) * 0.4, 0.0, 1.0));
        col = mix(col, cGlint, clamp(glint * 0.95 + coast * sea * 0.55 + wl * 0.18, 0.0, 1.0));
        col = mix(col, cSea * 1.4, bank * 0.6);
    } else if (variant < 3.5) {
        // Hasui: three flat indigo steps, bokashi of the sea, a dark key block, the grain of the block.
        float grain = noise(float2(p.x / 2.0, p.y / 40.0)) - 0.5;
        float3 water = mix(mix(cGround, cSea, 0.4), cSea * 0.75, smoothstep(0.0, 260.0, dL));
        float3 ground = mix(cGround, cGround * 0.82, smoothstep(0.0, 300.0, dW));
        col = mix(water, ground, landF) * (1.0 + grain * 0.05);
        float step3 = light < 0.38 ? 0.0 : (light < 0.7 ? 0.5 : 1.0);
        float3 mtn = mix(cShadow, cLit, step3);
        col = mix(col, mtn * (1.0 + grain * 0.04), massA);
        float key = max(max(edge, crest * 0.9), coast * 0.95);
        col = mix(col, cShadow * 0.6, clamp(key, 0.0, 1.0) * 0.9);
        col = mix(col, cGlint, glint * 0.8);
        col = mix(col, cLit, wl * 0.35);
        col = mix(col, cShadow * 0.7, bank * 0.8);
    } else if (variant < 4.5) {
        // Scratchboard: black; white only where it is scratched out.
        col = cGround * (1.0 + fibre * 0.05);
        // Lines first along the form, a cross only where the moon strikes hardest.
        float l1 = smoothstep(0.35, 0.45, light) * hatch(lp, ax, sp, 0.2 + 0.35 * light, 1.0 + aseed);
        float l2 = smoothstep(0.66, 0.76, light) * hatch(lp, turn(ax, 0.55), sp * 1.1, 0.2 + 0.3 * light, 2.0 + aseed);
        float l3 = smoothstep(0.88, 0.95, light) * hatch(lp, turn(ax, -0.5), sp, 0.22 + 0.3 * light, 3.0 + aseed);
        float lines = (1.0 - (1.0 - l1) * (1.0 - l2) * (1.0 - l3)) * massA;
        float scratch = max(lines, crest * smoothstep(0.3, 0.6, light));
        scratch = max(scratch, edge * smoothstep(0.35, 0.6, light) * step(0.3, noise(p / (10.0 * ms))));
        scratch = max(scratch, coast * 0.95);
        scratch = max(scratch, wl * 0.75);
        scratch = max(scratch, glint * 0.9);
        scratch = max(scratch, bank * 0.8);
        // The land keeps a sparse level texture of fine scratches, never a patch.
        float landTex = landF * (1.0 - massA) * hatch(p, normalize(float2(1.0, 0.04)), spacing * 2.6, 0.12, 7.0) * 0.18;
        scratch = max(scratch, landTex);
        col = mix(col, cLit, clamp(scratch, 0.0, 1.0));
    } else {
        // Pastel: the paper shows through the tooth; chalk laid along the planes.
        float tooth = 0.55 * noise(p / 0.9) + 0.45 * fbm(p / 2.6);
        float laid = noise(float2(p.x / 1.1, p.y / 6.0)) - 0.5;
        float3 paper = mix(cGround, cSea, sea * (0.4 + 0.6 * deep)) * (1.0 + mottle * 0.08 + laid * 0.04);
        col = paper;
        float stroke = noise(float2(dot(lp, ax) / (22.0 * ms), dot(lp, float2(-ax.y, ax.x)) / (3.0 * ms)) + aseed);
        float lit = smoothstep(0.55, 0.8, light + (stroke - 0.5) * 0.25) * massA;
        float turning = smoothstep(0.3, 0.5, light + (stroke - 0.5) * 0.25) * (1.0 - lit) * massA;
        float catchT = smoothstep(0.35, 0.7, tooth + 0.25);
        float3 midChalk = mix(cGround, cLit, 0.38);
        col = mix(col, midChalk, turning * catchT * 0.85);
        col = mix(col, cLit, lit * catchT * 0.95);
        col = mix(col, cShadow * 0.75, smoothstep(0.0, 0.3, 1.0 - light) * massA * 0.35);
        // The land scumbled lightly, the shore a chalk line, a little chalk dust of moonlight on the water.
        float scumble = landF * (1.0 - massA) * smoothstep(0.62, 0.85, tooth + 0.15 * noise(p / 30.0)) * 0.22;
        col = mix(col, midChalk, scumble);
        col = mix(col, cLit, clamp(coast * 0.75 * catchT + edge * light * 0.35 * catchT, 0.0, 1.0));
        col = mix(col, cGlint, glint * catchT * 0.7 + wl * 0.25 * catchT);
        col = mix(col, cShadow * 0.7, bank * 0.6);
    }
    col += (hash(floor(p)) - 0.5) * 0.008;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> MoonlightEffect = new(() =>
        SKRuntimeEffect.CreateShader(MoonlightSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader MoonlightShader(SKImage mountains, SKImage big, SKImage facets, SKImage crestA, SKImage crestB,
        SKImage tensor, SKImage geo, Moonlight m, float ms)
    {
        var builder = new SKRuntimeShaderBuilder(MoonlightEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        var shaders = new List<SKShader>();
        SKShader Child(SKImage image) { var s = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear); shaders.Add(s); return s; }
        builder.Children["mount"] = Child(mountains); builder.Children["mbig"] = Child(big); builder.Children["mfacet"] = Child(facets);
        builder.Children["crestA"] = Child(crestA); builder.Children["crestB"] = Child(crestB);
        builder.Children["tensor"] = Child(tensor); builder.Children["geo"] = Child(geo);
        builder.Uniforms["variant"] = (float)m.Variant;
        builder.Uniforms["ms"] = ms;
        builder.Uniforms["spacing"] = m.Spacing;
        builder.Uniforms["cGround"] = Stylize.Hex(m.Ground); builder.Uniforms["cSea"] = Stylize.Hex(m.Sea);
        builder.Uniforms["cShadow"] = Stylize.Hex(m.Shadow); builder.Uniforms["cLit"] = Stylize.Hex(m.Lit);
        builder.Uniforms["cGlint"] = Stylize.Hex(m.Glint);
        var result = builder.Build();
        foreach (var s in shaders) s.Dispose();
        return result;
    }
}
