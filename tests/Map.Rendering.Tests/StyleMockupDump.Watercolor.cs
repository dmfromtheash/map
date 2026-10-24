using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, round 6 (owner 2026-10-25: «делай акварель»): five watercolour schools, each repainting the mountains
/// from their form - height, north-west light, big faces, main lines - in its own way (DEC-060: a style redraws the
/// mountains, it does not lay a texture over the owner's painting). The original mountain colours are used only as a
/// mask. Research: docs/research/2026-10-25-r09-watercolour-styles.md. MAP_STYLE_WATERCOLOR = output folder,
/// MAP_LIGHT_ASSETS as before. Names go on top unchanged.
/// </summary>
public sealed partial class StyleMockupDump
{
    private sealed record Wash(int Variant, string Paper);

    [Fact]
    public void Dump_watercolour_variants_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_WATERCOLOR");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var (document, labels) = Scene(images);
            var mountainsId = document.Layers.First(l => l.Name == "Горы").Id;
            var riversId = document.Layers.First(l => l.Name == "Реки").Id;
            var land = document.Layers.OfType<Map.Core.Documents.LandLayer>().First().Id;
            using var mountains = RenderOnly(document, mountainsId, images);
            using var names = RenderOnly(document, labels, images);
            using var landOnly = RenderOnly(document, land, images);
            using var riverOnly = RenderOnly(document, riversId, images);
            using var geo = CoastField(landOnly, riverOnly);
            using var height = MountainHeight(mountains);
            using var mainLines = Blurred(mountains, 2f);
            using var faces = Blurred(mountains, 8f);
            using var near = Blurred(mountains, 3f);
            using var far = Blurred(mountains, 12f);

            var variants = new (string Name, Wash Wash)[]
            {
                ("Акварель 1 · Туманный Тёрнер, по сырому", new(1, "#f3ead6")),
                ("Акварель 2 · Плоская заливка Котмана", new(2, "#f4eedc")),
                ("Акварель 3 · Сухая кисть по торшону", new(3, "#efe7d4")),
                ("Акварель 4 · Сине-зелёные горы", new(4, "#e8d9b5")),
                ("Акварель 5 · Соль и маскировочная жидкость", new(5, "#fbf7ee")),
            };
            foreach (var (name, wash) in variants)
            {
                using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                using (var shader = WashShader(mountains, geo, height, mainLines, faces, near, far, wash))
                using (var paint = new SKPaint { Shader = shader })
                    surface.Canvas.DrawRect(0, 0, W, H, paint);
                surface.Canvas.DrawImage(names, 0, 0);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(output, name + ".png"), data.ToArray());
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    private const string WashSource = """
uniform shader mount;
uniform shader geo;
uniform shader height;
uniform shader mlines;
uniform shader mfaces;
uniform shader mnear;
uniform shader mfar;
uniform float variant;
uniform float2 size;
uniform float3 paper;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float3 unpre(float4 c) { return c.a > 0.001 ? c.rgb / c.a : float3(1); }
float3 hex(float r, float g, float b) { return float3(r, g, b) / 255.0; }
float hAt(float2 p) { return height.eval(p).r; }
float lineLum(float2 p) { float4 c = mlines.eval(p); return lum(mix(float3(1), unpre(c), c.a)); }
float rimAt(float2 p, float r) {
    float inner = min(min(mount.eval(p + float2(r, 0)).a, mount.eval(p - float2(r, 0)).a),
                      min(mount.eval(p + float2(0, r)).a, mount.eval(p - float2(0, r)).a));
    return smoothstep(0.35, 0.65, mount.eval(p).a) * (1.0 - smoothstep(0.35, 0.65, inner));
}

// A transparent glaze: the pigment's colour multiplies what lies under it, `amt` of the way.
float3 glaze(float3 under, float3 pigment, float amt) { return under * mix(float3(1), pigment, clamp(amt, 0.0, 1.0)); }
// An opaque body colour laid over.
float3 body(float3 under, float3 pigment, float amt) { return mix(under, pigment, clamp(amt, 0.0, 1.0)); }

// Salt crystals: pale star-shaped blooms - nearest crystal, with arms.
float salt(float2 p, float cell) {
    float2 base = floor(p / cell);
    float best = 0.0;
    for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
        float2 gc = base + float2(float(i), float(j));
        float2 c = (gc + float2(hash(gc), hash(gc + 2.9))) * cell;
        float2 v = p - c;
        float r = length(v);
        float a = atan(v.y, v.x);
        float arms = 0.65 + 0.35 * abs(sin(a * (3.0 + floor(hash(gc + 5.1) * 3.0)) + hash(gc) * 6.0));
        float rad = cell * (0.18 + 0.2 * hash(gc + 7.7)) * arms;
        best = max(best, (1.0 - smoothstep(rad * 0.4, rad, r)) * step(hash(gc + 1.3), 0.55));
    }
    return best;
}

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float dL = g.r * 255.0;
    float dW = g.g * 255.0;
    float isLand = step(0.5, dW);
    float river = g.b * smoothstep(0.5, 3.0, dW);
    float wob = noise(p / 23.0) - 0.5;

    // The mountains' form, without the painting's colours.
    float4 m = mount.eval(p);
    float sil = smoothstep(0.35, 0.65, m.a);
    float silNear = mnear.eval(p).a;
    float silFar = mfar.eval(p).a;
    float Hh = hAt(p);
    float2 hg = float2(hAt(p + float2(2, 0)) - hAt(p - float2(2, 0)), hAt(p + float2(0, 2)) - hAt(p - float2(0, 2))) * 0.25;
    float steep = clamp(length(hg) * 45.0, 0.0, 1.0);
    float2 down = length(hg) > 1e-5 ? -normalize(hg) : float2(0.0, 1.0);
    float2 along = float2(-down.y, down.x);
    float3 nrm = normalize(float3(-hg.x * 60.0, -hg.y * 60.0, 1.0));
    float hill = clamp(dot(nrm, normalize(float3(-0.55, -0.65, 0.55))), 0.0, 1.0);
    float4 mf = mfaces.eval(p);
    float broad = lum(mix(float3(1), unpre(mf), mf.a));
    float shade = clamp(mix(hill, broad * 1.15, 0.45), 0.0, 1.0);
    float l0 = lineLum(p);
    float l1 = 0.25 * (lineLum(p + float2(3, 0)) + lineLum(p - float2(3, 0)) + lineLum(p + float2(0, 3)) + lineLum(p - float2(0, 3)));
    float ridge = smoothstep(0.55, 0.32, l0) * smoothstep(0.025, 0.1, l1 - l0) * sil;

    // The paper: its tooth (height of the grain) and slow sizing blotches.
    float tooth = fbm(p / 5.0) * 0.6 + noise(p / 1.4) * 0.4;
    float turb = fbm(p / 140.0);
    float3 col = paper;

    if (variant < 1.5) {
        // ---- 1. Turner, wet-in-wet: everything laid into a wet sheet, edges lost, blooms, lights lifted out.
        float3 sea = mix(hex(143, 163, 191), hex(47, 75, 143), smoothstep(0.0, 320.0, dL + 60.0 * (turb - 0.5)));
        float seaAmt = (1.0 - isLand) * (0.55 + 0.35 * turb);
        // The wet sea runs into the land: a soft bleed a few pixels in, after a pale halo at the very edge.
        seaAmt += isLand * (1.0 - smoothstep(3.0, 11.0, dW + 6.0 * wob)) * smoothstep(0.0, 3.0, dW) * 0.35;
        col = glaze(col, sea, seaAmt);
        // Lifted light across the water: soft horizontal streaks taken out with a damp brush.
        col = mix(col, paper, (1.0 - isLand) * 0.25 * smoothstep(0.55, 0.8, noise(float2(p.x / 300.0, p.y / 22.0))));
        col = glaze(col, hex(226, 196, 142), isLand * (0.45 + 0.3 * turb) * (1.0 - smoothstep(0.0, 4.0, 4.0 - dW)));
        col = glaze(col, hex(143, 163, 191), river * 0.6);
        // Mountains: one soft blue-violet mass (the blurred silhouette, thresholded with noise), darker on the shadow
        // side, blooming at the foot, lifted to warm light on the lit upper faces; a hard edge only along the crest.
        float mass = smoothstep(0.3, 0.6, silFar + 0.25 * (noise(p / 18.0) - 0.5));
        float shadowGlaze = (1.0 - shade) * 0.55;
        float lift = smoothstep(0.6, 0.85, shade) * smoothstep(0.35, 0.7, Hh);
        float mAmt = mass * (0.6 + shadowGlaze) * (1.0 - 0.75 * lift);
        col = glaze(col, hex(106, 111, 160), mAmt);
        float bloomRim = smoothstep(0.02, 0.12, silFar - silNear) * (1.0 - smoothstep(0.15, 0.35, Hh + 0.2)) * noise(p / 9.0);
        col = glaze(col, hex(74, 78, 130), bloomRim * 0.6);
        col = glaze(col, hex(184, 71, 90), smoothstep(0.65, 0.95, Hh) * smoothstep(0.55, 0.8, shade) * sil * 0.35);
        col = glaze(col, hex(60, 62, 110), rimAt(p, 2.0) * smoothstep(0.55, 0.85, Hh) * 0.45);
        // Paper tooth under every wash.
        col *= 1.0 - 0.06 * (tooth - 0.5);
    } else if (variant < 2.5) {
        // ---- 2. Cotman's flat washes: every plane one even wash with a crisp edge, tonal steps as separate glazes.
        float band = dL < 9.0 + 2.0 * wob ? 1.0 : (dL < 34.0 + 3.0 * wob ? 2.0 : 3.0);
        col = glaze(col, hex(74, 90, 114), (1.0 - isLand) * (0.18 + 0.17 * band));
        col = glaze(col, hex(201, 165, 92), isLand * 0.42);
        col = glaze(col, hex(74, 90, 114), river * 0.55);
        // The drying line of a flat wash: a thin darker edge where it stopped.
        float edgeDark = (1.0 - isLand) * (1.0 - smoothstep(0.0, 1.6, abs(dL - 9.0 - 2.0 * wob)) + 1.0 - smoothstep(0.0, 1.6, abs(dL - 34.0 - 3.0 * wob)));
        col = glaze(col, hex(74, 90, 114), edgeDark * 0.25);
        // Mountains: an ochre underwash, then the form cut into flat planes - lit left as underwash, half-light sage,
        // shadow slate, the deepest slate twice - with hard edges and a hair of darkening on each drying line.
        float s = clamp(shade + 0.04 * (noise(p / 40.0) - 0.5), 0.0, 1.0);
        col = glaze(col, hex(201, 165, 92), sil * 0.35);
        col = glaze(col, hex(110, 127, 106), sil * step(s, 0.62) * 0.55);
        col = glaze(col, hex(74, 90, 114), sil * step(s, 0.4) * 0.6);
        col = glaze(col, hex(74, 90, 114), sil * step(s, 0.22) * 0.55);
        float lines = 0.0;
        lines += 1.0 - smoothstep(0.0, 0.012, abs(s - 0.62));
        lines += 1.0 - smoothstep(0.0, 0.012, abs(s - 0.4));
        lines += 1.0 - smoothstep(0.0, 0.012, abs(s - 0.22));
        col = glaze(col, hex(60, 70, 90), sil * clamp(lines, 0.0, 1.0) * 0.22);
        col = glaze(col, hex(138, 90, 60), (ridge * 0.6 + rimAt(p, 1.2) * 0.5));
        col *= 1.0 - 0.025 * (tooth - 0.5);
    } else if (variant < 3.5) {
        // ---- 3. Dry brush on rough paper: a nearly dry brush dragged across the tooth - paint only on the grain's peaks.
        float grain = fbm(p / 2.6) * 0.65 + noise(p / 1.1) * 0.35;
        // Sea: a dark underwash, dry horizontal strokes over it, white sparkle near the shore.
        float streakSea = noise(float2(p.x / 46.0, p.y / 2.2)) * 0.7 + noise(float2(p.x / 9.0, p.y / 1.4)) * 0.3;
        col = glaze(col, hex(45, 58, 85), (1.0 - isLand) * 0.45);
        float drySea = step(grain, 0.28 + 0.55 * streakSea) * (1.0 - isLand);
        col = glaze(col, hex(45, 58, 85), drySea * 0.65);
        col = mix(col, paper, (1.0 - isLand) * (1.0 - smoothstep(0.0, 26.0, dL)) * step(0.72 - 0.25 * (1.0 - smoothstep(0.0, 26.0, dL)), grain) * 0.8);
        // Land: granulating sienna and olive.
        float gran = 1.0 + 0.8 * (0.5 - grain);
        col = glaze(col, mix(hex(181, 138, 74), hex(110, 120, 80), smoothstep(0.4, 0.6, turb)), isLand * 0.38 * gran);
        col = glaze(col, hex(45, 58, 85), river * 0.6);
        // Mountains: an olive underwash, then dry strokes dragged down the slope, thick in shadow, burnt sienna along the
        // main lines, the lit ridge left as raw paper, knife scratches of light on the lit faces.
        col = glaze(col, hex(59, 74, 58), sil * (0.25 + 0.3 * (1.0 - shade)));
        float2 sp = float2(dot(p, along) / 2.4, dot(p, down) / 26.0);
        float streak = noise(sp) * 0.7 + noise(sp * float2(0.5, 3.0) + 9.1) * 0.3;
        float cover = (1.0 - shade) * 0.95 + 0.1;
        float dryM = step(grain, cover * 0.75 + 0.35 * (streak - 0.5)) * sil * (1.0 - smoothstep(0.75, 0.95, shade));
        col = glaze(col, hex(45, 52, 64), dryM * 0.75);
        col = glaze(col, hex(122, 74, 42), ridge * step(grain, 0.7) * 0.7);
        col = mix(col, paper, sil * ridge * smoothstep(0.6, 0.8, shade) * 0.7);
        col = glaze(col, hex(60, 52, 40), rimAt(p, 1.4) * step(grain, 0.75) * 0.6);
        // Rough paper: the tooth catches light everywhere.
        col *= 1.0 - 0.09 * (grain - 0.5);
    } else if (variant < 4.5) {
        // ---- 4. Blue-green landscape (qinglü): mineral colours on aged silk - ochre foot, malachite slopes, azurite
        // peaks, opaque and layered, ink outlines and texture strokes; waves drawn as fine ink lines.
        float weave = 0.5 + 0.25 * sin(p.x * 1.7) + 0.25 * sin(p.y * 1.7);
        col = paper * (0.97 + 0.04 * weave) * (0.95 + 0.08 * turb);
        float3 water = hex(191, 211, 200);
        col = body(col, water, (1.0 - isLand) * 0.75);
        float wave = abs(fract((dL + 1.5 * sin(p.x / 18.0 + p.y / 23.0)) / 7.0) - 0.5) * 7.0;
        col = body(col, hex(70, 90, 95), (1.0 - isLand) * (1.0 - smoothstep(0.35, 0.95, wave)) * (1.0 - smoothstep(60.0, 140.0, dL)) * 0.55);
        col = body(col, hex(222, 196, 150), isLand * 0.45);
        col = body(col, water, river * 0.85);
        // Mountains in height bands with jittered, overlapping edges; shadow side darker; azurite speckled.
        float hn = Hh + 0.08 * (noise(p / 14.0) - 0.5);
        float3 mc = hex(181, 118, 58);
        mc = mix(mc, hex(62, 154, 122), smoothstep(0.3, 0.42, hn));
        mc = mix(mc, hex(46, 111, 168), smoothstep(0.62, 0.74, hn));
        mc *= 0.7 + 0.35 * shade;
        mc *= 1.0 - 0.15 * step(0.93, hash(floor(p / 1.6))) * smoothstep(0.62, 0.74, hn);
        col = body(col, mc, sil * 0.92);
        // Ink: outline and texture strokes (cun) from the main lines.
        col = body(col, hex(42, 42, 42), clamp(rimAt(p, 1.6) + smoothstep(0.2, 0.5, ridge), 0.0, 1.0) * 0.85);
        col = body(col, hex(42, 42, 42), (1.0 - smoothstep(0.8, 1.8, max(dL, dW))) * 0.8);
    } else {
        // ---- 5. Salt and masking fluid: saturated gradients, salt stars in the washes, crisp reserved whites.
        float3 sea = mix(hex(31, 138, 158), hex(43, 47, 74), smoothstep(0.0, 280.0, dL));
        col = glaze(col, sea, (1.0 - isLand) * (0.7 + 0.2 * turb));
        col = mix(col, paper, (1.0 - isLand) * salt(p, 38.0) * 0.4);
        // The dried edge of the sea wash: a darker rim at the coast.
        col = glaze(col, hex(31, 90, 110), (1.0 - isLand) * (1.0 - smoothstep(0.0, 5.0, dL)) * 0.5);
        float3 landC = mix(hex(242, 217, 138), hex(224, 122, 63), smoothstep(0.35, 0.7, turb));
        float gran = 1.0 + 0.6 * (0.5 - tooth);
        col = glaze(col, landC, isLand * 0.45);
        col = glaze(col, hex(70, 80, 160), isLand * smoothstep(0.55, 0.75, fbm(p / 210.0 + 3.0)) * 0.25 * gran);
        col = glaze(col, hex(31, 138, 158), river * 0.75);
        // Mountains: a gradient wash warm at the foot to violet at the peak, salt only on the shadow side, snow caps
        // and lit crests reserved crisp with masking fluid, a strong dried rim on the silhouette, splatter near the tops.
        float3 mg = mix(hex(224, 122, 63), hex(91, 63, 140), smoothstep(0.15, 0.8, Hh));
        col = glaze(col, mg, sil * (0.55 + 0.35 * (1.0 - shade)));
        col = mix(col, paper, sil * salt(p + 11.0, 22.0) * (1.0 - shade) * 0.45);
        float mask = step(0.78, Hh + 0.06 * (noise(p / 8.0) - 0.5)) * step(0.55, shade);
        col = mix(col, paper, sil * mask);
        col = glaze(col, hex(60, 40, 100), rimAt(p, 2.4) * 0.55);
        float spl = 0.0;
        float2 sc = floor(p / 9.0);
        float2 sd = (sc + float2(hash(sc), hash(sc + 4.4))) * 9.0;
        spl = (1.0 - smoothstep(0.6, 1.4 + 2.0 * hash(sc + 8.8), length(p - sd))) * step(0.97, hash(sc + 2.2));
        col = glaze(col, hex(91, 63, 140), spl * smoothstep(0.5, 0.9, silFar * Hh + 0.3 * (1.0 - isLand)) * 0.8);
        col *= 1.0 - 0.04 * (tooth - 0.5);
    }

    col += (hash(floor(p)) - 0.5) * 0.015;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> WashEffect = new(() =>
        SKRuntimeEffect.CreateShader(WashSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader WashShader(SKImage mountains, SKImage geo, SKImage height, SKImage mainLines, SKImage faces,
        SKImage near, SKImage far, Wash wash)
    {
        var builder = new SKRuntimeShaderBuilder(WashEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        using var cMount = mountains.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cGeo = geo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cHeight = height.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cLines = mainLines.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cFaces = faces.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cNear = near.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cFar = far.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        builder.Children["mount"] = cMount; builder.Children["geo"] = cGeo; builder.Children["height"] = cHeight;
        builder.Children["mlines"] = cLines; builder.Children["mfaces"] = cFaces;
        builder.Children["mnear"] = cNear; builder.Children["mfar"] = cFar;
        builder.Uniforms["variant"] = (float)wash.Variant;
        builder.Uniforms["size"] = new float[] { W, H };
        builder.Uniforms["paper"] = Stylize.Hex(wash.Paper);
        return builder.Build();
    }
}
