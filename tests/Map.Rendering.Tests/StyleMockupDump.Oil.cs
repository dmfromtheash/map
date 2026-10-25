using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, «Масло» (owner 2026-10-25: five clearly different variants of one style; the style takes the big form of
/// his mountain - silhouette, big facets, light and shadow, main crests - and paints it again in its own technique, the
/// brush sized to the mountain, no big spots on the land, the names not stylized). Oil differs from watercolour in that
/// the paint is opaque: the lights are painted on top, each stroke carries one mixed colour, the ground shows between
/// strokes, and thick paint has a relief that catches the light. The form is read at a painter's scale as in watercolour
/// round 3 (blur, coarse Kuwahara); the strokes are placed coarse to fine (Hertzmann 1998): big strokes everywhere,
/// smaller ones only where the facets differ from the masses, each stroke oriented along the form (structure tensor), its
/// colour taken once at its centre, its relief built from a stroke profile and lit from the north-west.
/// Research: docs/research/2026-10-25-r09-oil-styles.md. MAP_STYLE_OIL = output folder, MAP_LIGHT_ASSETS as before;
/// MAP_STYLE_OIL_SCENE = малая|большая, MAP_STYLE_OIL_ONLY = variant numbers.
/// </summary>
public sealed partial class StyleMockupDump
{
    /// <summary>One oil variant: stroke sizes (length, width) for three mountain layers and the ground, palette, finish.</summary>
    private sealed record Oil(
        int Variant,
        float[] Mountain,            // len0, wid0, len1, wid1, len2, wid2 at the small scene's mountain scale
        float[] Ground,              // len, wid of the sea and land strokes
        float Relief, float Shine, float GroundShow, float Weave,
        string Light, string Mid, string Shadow, string Deep,
        string Light2, string Mid2, string Shadow2, string Deep2,
        string SeaNear, string SeaDeep, string LandCoast, string LandInland,
        string River, string Imprimatura, string Contour);

    [Fact]
    public void Dump_oil_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_OIL");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var only = Environment.GetEnvironmentVariable("MAP_STYLE_OIL_ONLY");
        var sceneOnly = Environment.GetEnvironmentVariable("MAP_STYLE_OIL_SCENE");
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var variants = OilVariants().Where(v => string.IsNullOrEmpty(only) || only.Contains(v.Oil.Variant.ToString())).ToArray();
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
                // The form at a painter's scale, as watercolour round 3: masses, facets, main crests, direction of the masses.
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
                foreach (var (name, oil) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = OilShader(mountains, big, facets, crestA, crestB, wide, tensor, geo, flow, oil, ms))
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

    private static IEnumerable<(string Name, Oil Oil)> OilVariants() =>
    [
        // 1. Alla prima with a flat brush (Sargent, Sorolla, plein-air studies): one layer, wet into wet, confident strokes
        // along the planes, warm light and cool shadow, the burnt-sienna ground glinting between strokes.
        ("Масло 1 · Алла прима, плоская кисть", new(1, [42, 15, 26, 9, 14, 5], [70, 24], 0.6f, 0.25f, 0.55f, 0.25f,
            "#f6ead6", "#b9c4d6", "#6f80a8", "#3d4572", "#f2e2cc", "#aebbd2", "#7a7ca6", "#45436e",
            "#76aab2", "#2b5078", "#d8c49c", "#b5a26e", "#86b6c0", "#a8603a", "#3a3f66")),
        // 2. Palette knife: wide flat slabs with straight edges, two colours dragged into streaks, a ridge of paint left
        // where the knife lifted, wet shine on the ridges; nothing of the ground left.
        ("Масло 2 · Мастихин", new(2, [48, 26, 30, 15, 16, 8], [80, 34], 0.9f, 0.65f, 0f, 0f,
            "#fbf1df", "#a9bdd8", "#56709e", "#2d3a66", "#eef3f6", "#c6cfe0", "#6a6ea6", "#3b3570",
            "#5a98b6", "#1f4a74", "#d6be8e", "#aa9a64", "#7fb4c8", "#8a7a66", "#2a3158")),
        // 3. Cézanne's constructive stroke (Mont Sainte-Victoire): short parallel patches at one slant everywhere, the
        // form built by warm and cool modulation rather than by dark and light, thin paint on a pale canvas, broken blue
        // contours.
        ("Масло 3 · Конструктивный мазок Сезанна", new(3, [18, 7, 13, 5, 8, 3.4f], [20, 8], 0.25f, 0f, 0.8f, 0.6f,
            "#e6cf9f", "#a9b9a2", "#8197bf", "#76689f", "#efc9a8", "#c2c49a", "#9aa7cf", "#6d7fb0",
            "#9cc2c8", "#5f8cae", "#dcc796", "#c3c391", "#8ec2cc", "#efe9dc", "#3f5ea6")),
        // 4. Roerich's Himalayas: the mountain as a few flat, glowing planes with clean edges - rose light, blue and
        // ultramarine shadow - a dark drawn contour, the sea in flat stepped bands; matte paint, no stroke relief.
        ("Масло 4 · Гималаи Рериха", new(4, [0, 0, 0, 0, 0, 0], [0, 0], 0.08f, 0f, 0f, 0.15f,
            "#f7d9bd", "#9ec0e0", "#4a68b0", "#2a2f7a", "#f3cfc0", "#a9b6e0", "#5a5aa8", "#30286a",
            "#70b6ca", "#24508f", "#ecc683", "#e2b36e", "#8cc6d6", "#e9c17e", "#1e2350")),
        // 5. Van Gogh's rhythmic impasto: short thick dashes in two hues of one value laid side by side, following the form
        // on the mountain and running parallel to the coast in the sea and on the land; deep grooves between dashes.
        ("Масло 5 · Ритм импасто Ван Гога", new(5, [26, 7.5f, 17, 5.5f, 11, 4], [30, 8], 0.7f, 0.35f, 0.9f, 0f,
            "#f7e7b0", "#9cc3d6", "#4f78b8", "#2b3c7c", "#e8f1ee", "#b7cba0", "#3f8f9a", "#4a3a7a",
            "#7fc0c8", "#2f5e9c", "#d8b860", "#b4b65c", "#9fd0d0", "#6b7890", "#22305a")),
    ];

    /// <summary>
    /// Direction of the coast: (0.5 + 0.5 * unit gradient of the signed coast distance, strength), smoothed, so the sea and
    /// land strokes can run parallel to the shore near it and level far from it.
    /// </summary>
    private static SKImage CoastFlow(SKImage geo)
    {
        const string source = """
uniform shader geo;
float sd(float2 p) { float4 g = geo.eval(p); return (g.r - g.g) * 255.0; }
half4 main(float2 p) {
    float gx = sd(p + float2(3, 0)) - sd(p - float2(3, 0));
    float gy = sd(p + float2(0, 3)) - sd(p - float2(0, 3));
    float m = length(float2(gx, gy));
    float2 n = m > 1e-3 ? float2(gx, gy) / m : float2(0);
    return half4(half(0.5 + 0.5 * n.x), half(0.5 + 0.5 * n.y), half(clamp(m / 6.0, 0.0, 1.0)), 1.0);
}
""";
        using var raw = SKSurface.Create(new SKImageInfo(W, H))!;
        using (var effect = SKRuntimeEffect.CreateShader(source, out var errors) ?? throw new InvalidOperationException(errors))
        {
            var builder = new SKRuntimeShaderBuilder(effect);
            using var child = geo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            builder.Children["geo"] = child;
            using var shader = builder.Build();
            using var paint = new SKPaint { Shader = shader };
            raw.Canvas.DrawRect(0, 0, W, H, paint);
        }
        using var rawImage = raw.Snapshot();
        return Blurred(rawImage, 6f);
    }

    private const string OilSource = """
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
uniform float3 mlay0;   // len, wid, unused
uniform float3 mlay1;
uniform float3 mlay2;
uniform float2 glay;
uniform float relief;
uniform float shine;
uniform float groundShow;
uniform float weave;
uniform float3 cLight; uniform float3 cMid; uniform float3 cShadow; uniform float3 cDeep;
uniform float3 cLight2; uniform float3 cMid2; uniform float3 cShadow2; uniform float3 cDeep2;
uniform float3 cSeaNear; uniform float3 cSeaDeep; uniform float3 cLandCoast; uniform float3 cLandInland;
uniform float3 cRiver; uniform float3 cGround; uniform float3 cContour;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float onWhite(float4 c) { return lum(c.rgb + (1.0 - c.a)); }
// Value of the abstracted form, 0 = lit, 1 = darkest.
float toneOf(float4 c) { return clamp((0.94 - onWhite(c)) / 0.66, 0.0, 1.0); }

// The mountain's colour for a value: four mixed piles on the palette, the second hue of each pile for broken colour.
float3 mountainPaint(float t, float alt, float up) {
    float3 a = mix(cLight, cLight2, alt);
    float3 b = mix(cMid, cMid2, alt);
    float3 c = mix(cShadow, cShadow2, alt);
    float3 d = mix(cDeep, cDeep2, alt);
    float3 col = t < 0.24 ? a : (t < 0.5 ? b : (t < 0.76 ? c : d));
    // The foot catches the land's warmth a little (reflected light), the summit stays clean.
    return mix(col, cLandInland, (1.0 - smoothstep(0.15, 0.55, up)) * 0.12);
}

float3 groundPaint(float2 c, float alt) {
    float4 g = geo.eval(c);
    float dL = g.r * 255.0;
    float dW = g.g * 255.0;
    float landF = step(dL, dW);
    float3 sea = mix(cSeaNear, cSeaDeep, 0.25 + 0.6 * smoothstep(8.0, 160.0, dL));
    float3 land = mix(cLandCoast, cLandInland, smoothstep(8.0, 160.0, dW));
    float3 col = mix(sea, land, landF);
    // Small hue shifts per stroke only: no big spots on the land.
    return col * (1.0 + (alt - 0.5) * 0.07);
}

// Stroke axis: along the masses on the mountain, parallel to the shore near it on the ground, one slant for Cézanne.
float2 axisAt(float2 c, float kind, float2 cid) {
    if (variant > 2.5 && variant < 3.5) return normalize(float2(0.55, -0.83));
    if (kind < 0.5) {
        float4 J = tensor.eval(c);
        float jxx = J.r; float jyy = J.g; float jxy = (J.b - 0.5) * 2.0;
        float strength = jxx + jyy;
        float ang = 0.5 * atan(2.0 * jxy, jxx - jyy + 1e-6);
        float2 along = float2(-sin(ang), cos(ang));
        float rnd = (hash(cid + 41.0) - 0.5) * 0.5 + 0.9;
        float2 fallback = float2(cos(rnd), -sin(rnd));
        return normalize(mix(fallback, along, smoothstep(0.0, 0.0006, strength)));
    }
    float4 f = flow.eval(c);
    float2 n = (f.rg - 0.5) * 2.0;
    float2 shore = length(n) > 1e-3 ? float2(-n.y, n.x) / length(n) : float2(1, 0);
    float2 level = float2(1.0, (hash(cid + 5.0) - 0.5) * 0.25);
    float near = variant > 4.5 ? f.b : f.b * 0.55;
    return normalize(mix(level, shore, near));
}

// Stroke shape, u along (-1..1), v across (-1..1): coverage.
float shapeOf(float u, float v, float seed) {
    float au = abs(u); float av = abs(v);
    if (variant > 1.5 && variant < 2.5) {
        // Knife: a straight-edged slab, one end cut square, the other a skewed lift.
        float lift = 1.0 - smoothstep(0.92, 1.0, u + 0.25 * v);
        return step(av, 1.0) * step(-1.0, u) * lift;
    }
    if (variant > 2.5 && variant < 3.5) return step(av, 1.0) * step(au, 1.0);
    // Flat or filbert brush: rounded body, ends breaking up into bristle marks.
    float body = step(av, 1.0 - 0.25 * u * u);
    float bristle = noise(float2(v * 3.0 + seed * 13.0, seed * 7.0));
    float tail = step(au, 1.0) * step((au - 0.8) * 5.0, bristle);
    return body * tail;
}

// Paint height in a stroke: body thicker in the middle, ridges at the edges, a load at the start, bristle grooves.
float heightOf(float u, float v, float seed) {
    float au = abs(u); float av = abs(v);
    if (variant > 1.5 && variant < 2.5) {
        float ridge = smoothstep(0.55, 0.95, v) * (1.0 - smoothstep(0.95, 1.0, v));
        float lift = smoothstep(0.55, 0.95, u + 0.25 * v) * (1.0 - smoothstep(0.95, 1.05, u + 0.25 * v));
        return 0.45 + 0.5 * ridge + 0.6 * lift + 0.06 * noise(float2(v * 2.0 + seed * 9.0, u * 3.0));
    }
    float body = 1.0 - 0.45 * v * v;
    float ed = (av - 0.8) / 0.16;
    float edges = 0.35 * exp(-ed * ed);
    float load = mix(1.2, 0.65, (u + 1.0) * 0.5);
    float ends = 1.0 - smoothstep(0.55, 1.0, au);
    float grooves = noise(float2(v * 2.6 + seed * 13.0, u * 0.5 + seed));
    return (body + edges) * load * ends * (0.85 + 0.3 * grooves);
}

// The covering stroke of one layer: kind 0 = mountain, 1 = ground. Writes into the out parameters when found.
void strokeLayer(float2 p, float len, float wid, float lid, float kind, float detail,
                 inout float found, inout float2 sc, inout float2 sax, inout float su, inout float sv, inout float sseed,
                 inout float slf) {
    float cell = len * 0.5;
    float2 g = floor(p / cell);
    float best = -1.0;
    for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
            float2 cid = g + float2(float(i), float(j)) + lid * 101.0;
            float2 c = (g + float2(float(i), float(j)) + 0.5 + (float2(hash(cid), hash(cid + 11.0)) - 0.5) * 0.9) * cell;
            float pri = hash(cid + 19.7);
            if (pri > best) {
                float inside = mbig.eval(c).a;
                float ok = kind < 0.5 ? step(0.42, inside) : 1.0 - step(0.5, inside);
                if (ok > 0.5 && detail > 0.0) {
                    float diff = abs(toneOf(mfacet.eval(c)) - toneOf(mbig.eval(c)));
                    float edge = smoothstep(0.08, 0.2, onWhite(crestB.eval(c)) - onWhite(crestA.eval(c)));
                    ok = step(detail, max(diff, edge * 0.3));
                }
                if (ok > 0.5) {
                    float2 ax = axisAt(c, kind, cid);
                    // Every stroke a little different in length and angle, so the ground never reads as brickwork.
                    float lf = 0.7 + 0.4 * hash(cid + 3.0);
                    float jitter = (variant > 2.5 && variant < 3.5) ? 0.08 : (kind < 0.5 ? 0.22 : 0.35);
                    float ja = (hash(cid + 29.0) - 0.5) * jitter * 2.0;
                    ax = float2(ax.x * cos(ja) - ax.y * sin(ja), ax.x * sin(ja) + ax.y * cos(ja));
                    float2 d = p - c;
                    float u = dot(d, ax) / (len * lf * 0.5);
                    float v = dot(d, float2(-ax.y, ax.x)) / (wid * 0.5);
                    // A slight wobble of the edges: a hand, not a stamp.
                    v += (noise(float2(u * 2.0, pri * 31.0)) - 0.5) * 0.25;
                    if (shapeOf(u, v, pri) > 0.5) { best = pri; sc = c; sax = ax; su = u; sv = v; sseed = pri; slf = lf; }
                }
            }
        }
    }
    if (best >= 0.0) found = 1.0;
}

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float jit = (fbm(p / 16.0) - 0.5) * 4.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    float2 q = p / ms;
    float4 mb = mbig.eval(p);
    float4 mf = mfacet.eval(p);
    float4 mw = mwide.eval(p);
    float massA = smoothstep(0.38, 0.6, mb.a + 0.14 * (fbm(q / 9.0) - 0.5));
    float tBig = toneOf(mb);
    float tFacet = toneOf(mf);
    float up = mwide.eval(p + float2(0, 70.0 * ms)).a;
    float crest = smoothstep(0.05, 0.13, onWhite(crestB.eval(p)) - onWhite(crestA.eval(p))) * smoothstep(0.3, 0.7, mount.eval(p).a);

    // ---- the block-in: a thin, continuous underpainting of every area, the ground glinting through it.
    float3 under = mix(groundPaint(p, 0.5), mountainPaint(tBig, 0.0, up), massA);
    float3 col = mix(under, cGround, groundShow * mix(0.25, 0.55, massA));
    float h = 0.0;
    float2 grad = float2(0);

    if (variant > 3.5 && variant < 4.5) {
        // ---- 4. Roerich: flat planes with clean edges. The values of the facets quantised into four planes, each one
        // flat colour; the sea in stepped bands; a dark contour; fine vertical tempera hatching only.
        float wob = (noise(q / 7.0) - 0.5) * 0.06;
        float t = mix(tBig, tFacet, 0.15) + wob;
        float3 m = mountainPaint(t, smoothstep(0.4, 0.6, noise(q / 60.0)) * 0.6, up);
        float band = floor(clamp(dL + (noise(p / 40.0) - 0.5) * 18.0, 0.0, 239.0) / 60.0);
        float3 sea = mix(cSeaNear, cSeaDeep, band / 3.0);
        float3 land = dW + (noise(p / 40.0) - 0.5) * 10.0 < 22.0 ? cLandCoast : cLandInland;
        col = mix(sea, land, landF);
        col = mix(col, m, massA);
        float edge = massA * (1.0 - smoothstep(0.55, 0.8, mb.a + 0.1 * (noise(q / 5.0) - 0.5)));
        col = mix(col, cContour, max(edge * 0.9, crest * 0.45));
        col = mix(col, cRiver, river * 0.9);
        col *= 1.0 - 0.04 * (noise(float2(p.x / 1.2, p.y / 9.0)) - 0.5);
        col *= 1.0 - weave * 0.05 * (noise(p / 1.6) - 0.5);
        return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
    }

    // ---- the strokes, coarse to fine: ground layers first, then the mountain layers over them.
    float found = 0.0; float2 sc = float2(0); float2 sax = float2(1, 0); float su = 0.0; float sv = 0.0; float seed = 0.0; float slf = 1.0;
    float kind = 1.0; float slen = glay.x; float swid = glay.y; float slayer = 0.0;
    if (mb.a < 0.92) {
        strokeLayer(p, glay.x, glay.y, 1.0, 1.0, 0.0, found, sc, sax, su, sv, seed, slf);
        float f2 = 0.0; float2 c2 = sc; float2 a2 = sax; float u2 = su; float v2 = sv; float s2 = seed; float l2 = slf;
        strokeLayer(p, glay.x * 0.6, glay.y * 0.7, 2.0, 1.0, 0.0, f2, c2, a2, u2, v2, s2, l2);
        // The second, smaller ground layer only near the coast, where the shore needs a finer hand.
        if (f2 > 0.5 && min(g.r, g.g) * 255.0 < 40.0) { found = 1.0; sc = c2; sax = a2; su = u2; sv = v2; seed = s2; slf = l2; slen = glay.x * 0.6; swid = glay.y * 0.7; }
    }
    if (mw.a > 0.02) {
        float lens[3]; float wids[3]; float dets[3];
        lens[0] = mlay0.x * ms; wids[0] = mlay0.y * ms; dets[0] = 0.0;
        lens[1] = mlay1.x * ms; wids[1] = mlay1.y * ms; dets[1] = 0.06;
        lens[2] = mlay2.x * ms; wids[2] = mlay2.y * ms; dets[2] = 0.14;
        for (int k = 0; k < 3; k++) {
            float fk = 0.0; float2 ck = float2(0); float2 ak = float2(1, 0); float uk = 0.0; float vk = 0.0; float sk = 0.0; float lk = 1.0;
            strokeLayer(p, lens[k], wids[k], 10.0 + float(k), 0.0, dets[k], fk, ck, ak, uk, vk, sk, lk);
            // A mountain stroke ends at the silhouette (with a ragged, painted edge), as Hertzmann clips at edges.
            float clipA = mb.a + 0.16 * (noise(q / 3.0 + sk * 9.0) - 0.5);
            if (fk > 0.5 && clipA > 0.4) { found = 1.0; kind = 0.0; sc = ck; sax = ak; su = uk; sv = vk; seed = sk; slf = lk; slen = lens[k]; swid = wids[k]; slayer = float(k); }
        }
    }
    slen *= slf;
    // A sea stroke stays in the sea and a land stroke on the land: the shore is cut clean (with the block-in under it).
    if (found > 0.5 && kind > 0.5) {
        float4 gc = geo.eval(sc);
        if (abs(step(gc.r, gc.g) - landF) > 0.5) found = 0.0;
    }

    if (found > 0.5) {
        float alt = variant > 4.5 ? step(0.5, hash(sc * 0.71)) : hash(sc * 0.71) * (variant > 2.5 ? 1.0 : 0.5);
        float3 paint;
        if (kind < 0.5) {
            float4 cb = mbig.eval(sc); float4 cf = mfacet.eval(sc);
            // The big strokes carry the masses, the small ones the facets (coarse to fine).
            float t = mix(toneOf(cb), toneOf(cf), 0.25 + 0.3 * slayer) + (hash(sc * 0.37) - 0.5) * 0.08;
            float upc = mwide.eval(sc + float2(0, 70.0 * ms)).a;
            paint = mountainPaint(t, alt, upc);
            if (variant < 2.5) {
                // Wet into wet: the stroke drags the colour where it started into the colour where it lands.
                float2 st = sc - sax * slen * 0.4;
                float ts = mix(toneOf(mbig.eval(st)), toneOf(mfacet.eval(st)), 0.6);
                float3 from = mountainPaint(ts, alt, upc);
                float drag = variant < 1.5 ? (1.0 - smoothstep(-1.0, 0.2, su)) * 0.5 : smoothstep(0.3, 0.7, noise(float2(sv * 3.0, seed * 17.0))) * 0.6;
                paint = mix(paint, from, drag);
            }
        } else {
            paint = groundPaint(sc, alt);
            if (variant > 1.5 && variant < 2.5) {
                float3 from = groundPaint(sc - sax * slen * 0.4, 1.0 - alt);
                paint = mix(paint, from, smoothstep(0.3, 0.7, noise(float2(sv * 3.0, seed * 17.0))) * 0.6);
            }
        }
        col = paint;
        // Relief: the stroke's own height profile, differentiated in stroke space and turned to the picture.
        float e = 0.03;
        float h0 = heightOf(su, sv, seed);
        float hu = (heightOf(su + e, sv, seed) - h0) / e;
        float hv = (heightOf(su, sv + e, seed) - h0) / e;
        float2 perp = float2(-sax.y, sax.x);
        float px = 1.4 * ms;   // paint thickness in pixels at its thickest
        grad = (sax * hu / (slen * 0.5) + perp * hv / (swid * 0.5)) * px * (kind < 0.5 ? 1.0 : 0.5);
        h = h0;
    }

    // Rivers: a thin line of light paint laid last, along its own bed.
    col = mix(col, cRiver * (0.94 + 0.12 * noise(p / 3.0)), river * 0.85);

    // Cézanne's broken blue contours along the silhouette and the main crests.
    if (variant > 2.5 && variant < 3.5) {
        float edge = smoothstep(0.25, 0.42, mb.a) * (1.0 - smoothstep(0.42, 0.6, mb.a));
        col = mix(col, cContour, max(edge, crest * 0.8) * step(0.38, noise(p / 14.0)) * 0.75);
    }
    // Dark accents of the alla prima and knife: a few broken strokes in the deepest folds along the crests.
    if (variant < 2.5) col = mix(col, cContour, crest * step(0.55, noise(p / 18.0)) * 0.5);

    // ---- light on the paint: north-west light over the relief, a wet highlight on the ridges.
    float3 N = normalize(float3(-grad * relief, 1.0));
    float3 L = normalize(float3(-0.55, -0.65, 0.55));
    float level = L.z;
    float diffuse = dot(N, L);
    col *= 1.0 + 0.6 * (diffuse - level);
    float3 R = reflect(-L, N);
    float spec = pow(max(R.z, 0.0), 30.0) * shine * smoothstep(0.2, 0.8, h);
    col += spec * 0.5;
    // The canvas weave, where the paint is thin.
    float wv = 0.5 + 0.25 * (sin(p.x * 1.9) + sin(p.y * 1.9));
    col *= 1.0 - weave * 0.08 * (wv - 0.5) * (1.0 - smoothstep(0.0, 0.6, h));
    col += (hash(floor(p)) - 0.5) * 0.008;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> OilEffect = new(() =>
        SKRuntimeEffect.CreateShader(OilSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader OilShader(SKImage mountains, SKImage big, SKImage facets, SKImage crestA, SKImage crestB, SKImage wide,
        SKImage tensor, SKImage geo, SKImage flow, Oil oil, float ms)
    {
        var builder = new SKRuntimeShaderBuilder(OilEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        var shaders = new List<SKShader>();
        SKShader Child(SKImage image) { var s = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear); shaders.Add(s); return s; }
        builder.Children["mount"] = Child(mountains); builder.Children["mbig"] = Child(big); builder.Children["mfacet"] = Child(facets);
        builder.Children["crestA"] = Child(crestA); builder.Children["crestB"] = Child(crestB); builder.Children["mwide"] = Child(wide);
        builder.Children["tensor"] = Child(tensor); builder.Children["geo"] = Child(geo); builder.Children["flow"] = Child(flow);
        builder.Uniforms["variant"] = (float)oil.Variant;
        builder.Uniforms["ms"] = ms;
        builder.Uniforms["mlay0"] = new[] { oil.Mountain[0], oil.Mountain[1], 0f };
        builder.Uniforms["mlay1"] = new[] { oil.Mountain[2], oil.Mountain[3], 0f };
        builder.Uniforms["mlay2"] = new[] { oil.Mountain[4], oil.Mountain[5], 0f };
        builder.Uniforms["glay"] = new[] { oil.Ground[0], oil.Ground[1] };
        builder.Uniforms["relief"] = oil.Relief;
        builder.Uniforms["shine"] = oil.Shine;
        builder.Uniforms["groundShow"] = oil.GroundShow;
        builder.Uniforms["weave"] = oil.Weave;
        builder.Uniforms["cLight"] = Stylize.Hex(oil.Light); builder.Uniforms["cMid"] = Stylize.Hex(oil.Mid);
        builder.Uniforms["cShadow"] = Stylize.Hex(oil.Shadow); builder.Uniforms["cDeep"] = Stylize.Hex(oil.Deep);
        builder.Uniforms["cLight2"] = Stylize.Hex(oil.Light2); builder.Uniforms["cMid2"] = Stylize.Hex(oil.Mid2);
        builder.Uniforms["cShadow2"] = Stylize.Hex(oil.Shadow2); builder.Uniforms["cDeep2"] = Stylize.Hex(oil.Deep2);
        builder.Uniforms["cSeaNear"] = Stylize.Hex(oil.SeaNear); builder.Uniforms["cSeaDeep"] = Stylize.Hex(oil.SeaDeep);
        builder.Uniforms["cLandCoast"] = Stylize.Hex(oil.LandCoast); builder.Uniforms["cLandInland"] = Stylize.Hex(oil.LandInland);
        builder.Uniforms["cRiver"] = Stylize.Hex(oil.River); builder.Uniforms["cGround"] = Stylize.Hex(oil.Imprimatura);
        builder.Uniforms["cContour"] = Stylize.Hex(oil.Contour);
        var result = builder.Build();
        foreach (var s in shaders) s.Dispose();
        return result;
    }
}
