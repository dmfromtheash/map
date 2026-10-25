using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles: «Укиё-э», five woodblock schools (owner 2026-10-25: «5 вариантов делай, учитывай все мои ранее пожелания»).
/// As in the chosen watercolour (DEC-061) the mountains are repainted from their large-scale form - silhouette, big
/// facets, main light and shadow, main crests - in the medium's own language: a carved key line, flat colour blocks
/// printed slightly off register, bokashi gradients wiped on the block, wood grain, baren rubbing, washi fibre. Line and
/// block sizes follow the mountains' size. Names are not stylised. Different from «Старая карта 4» (seigaiha, coastal
/// bokashi). Research: docs/research/2026-10-25-r09-ukiyoe-styles.md. MAP_STYLE_UKIYOE = output folder, MAP_LIGHT_ASSETS
/// as before; MAP_STYLE_WATERCOLOR2_SCENE / _ONLY also apply.
/// </summary>
public sealed partial class StyleMockupDump
{
    [Fact]
    public void Dump_ukiyoe_variants_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_UKIYOE");
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
                ("Укиё-э 1 · Хокусай, берлинская лазурь", new(1, "#f2e8d2")),
                ("Укиё-э 2 · Хиросигэ, полосы тумана", new(2, "#f3ead6")),
                ("Укиё-э 3 · Красная Фудзи", new(3, "#efe4cc")),
                ("Укиё-э 4 · Син-ханга, вечер в горах", new(4, "#efe4cc")),
                ("Укиё-э 5 · Сосаку-ханга, след резца", new(5, "#ece2c8")),
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
                var ms = tag == "малая" ? 1f : 0.42f;
                using var soft = Blurred(mountains, 4f * ms);
                using var bigK = Kuwahara(soft, 3.2f * ms);
                using var big = Blurred(bigK, 1.5f * ms);
                using var facetSoft = Blurred(mountains, 1.8f * ms);
                using var facetK = Kuwahara(facetSoft, 2f * ms);
                using var facets = Blurred(facetK, 1f * ms);
                using var crestA = Blurred(mountains, 2.5f * ms);
                using var crestB = Blurred(mountains, 7f * ms);
                using var wide = Blurred(mountains, 14f * ms);
                using var tensor = Tensor(big);
                foreach (var (name, aqua) in variants)
                {
                    using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                    using (var shader = PrintBlockShader(mountains, big, facets, crestA, crestB, wide, tensor, geo, riverOnly, aqua, ms))
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

    private const string BlockSource = """
uniform shader mount;
uniform shader mbig;
uniform shader mfacet;
uniform shader crestA;
uniform shader crestB;
uniform shader mwide;
uniform shader tensor;
uniform shader geo;
uniform shader river;
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
float toneOf(float4 c) { return clamp((0.94 - onWhite(c)) / 0.66, 0.0, 1.0); }
// Water-based pigment printed from a block: it multiplies the paper, a little uneven (the baren's pressure).
float3 ink(float3 under, float3 pigment, float amt) { return under * mix(float3(1), pigment, clamp(amt, 0.0, 1.0)); }
// A carved edge: crisp, a pixel wide, never soft.
float block(float field, float t) { return smoothstep(t - 0.012, t + 0.012, field); }
// Distance (in px) to the 0.5 level of a sampled mask, from its value and gradient.
float edgeDist(float a, float2 g) { return abs(a - 0.5) / max(length(g), 1e-4); }

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float dL = g.r * 255.0;
    float dW = g.g * 255.0;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float seaF = 1.0 - landF;
    float riv = river.eval(p).a * smoothstep(0.5, 3.0, dW);
    float2 q = p / ms;

    // ---- paper and block textures
    // Washi: long kozo fibres in a few directions, very faint.
    float fib = 0.0;
    fib += smoothstep(0.78, 0.95, noise(float2(p.x * 0.9 + p.y * 0.2, p.y * 0.04 - p.x * 0.01)));
    fib += smoothstep(0.8, 0.96, noise(float2(p.y * 0.8 - p.x * 0.3, p.x * 0.05) + 13.0));
    // Wood grain of the block (mokume), seen in flat areas: wavy parallel lines along the plank.
    float grain = 0.5 + 0.5 * sin((p.y + 9.0 * fbm(float2(p.x / 140.0, p.y / 30.0))) * 0.9);
    // The baren: soft circular rubbing streaks.
    float2 bc = floor(p / 46.0);
    float baren = 0.5 + 0.5 * sin(length(p - (bc + float2(hash(bc), hash(bc + 3.1))) * 46.0) * 0.6 + hash(bc) * 6.0);
    float press = 0.94 + 0.06 * fbm(p / 26.0);
    float3 col = paper * (1.0 - 0.025 * fib) * (0.99 + 0.02 * noise(p / 240.0));

    // ---- the form at the block cutter's scale
    float4 mb = mbig.eval(p);
    float4 mf = mfacet.eval(p);
    float2 ga = float2(mbig.eval(p + float2(1.5, 0)).a - mbig.eval(p - float2(1.5, 0)).a,
                       mbig.eval(p + float2(0, 1.5)).a - mbig.eval(p - float2(0, 1.5)).a) / 3.0;
    float massA = smoothstep(0.47, 0.53, mb.a);
    float outlineD = edgeDist(mb.a, ga);
    float tBig = toneOf(mb) * massA;
    float tFacet = toneOf(mf) * smoothstep(0.45, 0.55, mf.a);
    float crestRaw = onWhite(crestB.eval(p)) - onWhite(crestA.eval(p));
    // Only the main crests are carved: a high threshold, and short bits are dropped where the coarse DoG is weak.
    float crest = smoothstep(0.11, 0.125, crestRaw) * smoothstep(0.5, 0.7, mount.eval(p).a);
    // Bokashi from the top of each mountain: 1 at the silhouette's upper edge, fading down.
    float top = (1.0 - mwide.eval(p - float2(0, 70.0 * ms)).a) * massA;
    float topSoft = smoothstep(0.0, 1.0, top);
    float up = mwide.eval(p + float2(0, 70.0 * ms)).a * massA;
    float4 J = tensor.eval(p);
    float ang = 0.5 * atan(2.0 * (J.b - 0.5) * 2.0, J.r - J.g + 1e-6);
    float2 across = float2(cos(ang), sin(ang));
    float2 along = float2(-across.y, across.x);
    // Off register: the colour blocks sit a little away from the key block.
    float2 reg = float2(1.6, -1.0) * ms;
    float4 mbR = mbig.eval(p + reg);
    float massR = smoothstep(0.47, 0.53, mbR.a);
    float tR = toneOf(mbR) * massR;
    float topR = smoothstep(0.0, 0.8, 1.0 - mwide.eval(p + reg - float2(0, 70.0 * ms)).a) * massR;
    float4 mw = mwide.eval(p);

    float seaT = smoothstep(0.0, 300.0, dL);
    // Coast-parallel wave lines: every `gap` px of distance from the shore, thinner and fewer far out.
    float wob = 3.0 * (noise(p / 60.0) - 0.5);

    if (variant < 1.5) {
        // ---- 1. Hokusai, Prussian blue (Thirty-six Views): the sea in bero-ai with an ichimonji bokashi darkening away
        // from the shore, carved wave lines with claw-like foam along the coast; mountains as a few flat planes in
        // indigo and blue-green, dark bokashi at every peak, snow left as paper with a carved edge; sumi key line.
        float3 bero = hex(38, 70, 128);
        col = ink(col, mix(hex(150, 182, 204), bero, smoothstep(10.0, 260.0, dL)), seaF * 0.92 * press);
        float wl = abs(fract((dL + wob) / 13.0) - 0.5) * 13.0;
        float waveLine = seaF * (1.0 - smoothstep(0.7, 1.3, wl)) * (1.0 - smoothstep(30.0, 120.0, dL)) * step(0.35, noise(p / 30.0));
        col = ink(col, hex(22, 40, 80), waveLine * 0.85);
        // Foam claws along the shore: white curls reserved out of the blue.
        float claw = seaF * (1.0 - smoothstep(4.0, 9.0, dL + 4.0 * sin(p.x / 9.0 + p.y / 11.0)));
        col = mix(col, paper, claw * 0.9);
        col = ink(col, hex(230, 214, 168), landF * 0.6 * press);
        col = ink(col, hex(190, 196, 140), landF * smoothstep(0.0, 1.0, mw.a) * 0.4);
        col = ink(col, hex(150, 182, 204), riv * 0.9);
        // Mountains.
        col = ink(col, hex(140, 176, 160), massR * 0.5 * press);
        col = ink(col, hex(70, 110, 146), block(tR, 0.38) * 0.7 * press);
        col = ink(col, hex(40, 62, 104), block(tR, 0.68) * 0.7 * press);
        col = ink(col, bero, topR * 0.75);
        col = mix(col, paper * (1.0 - 0.02 * fib), massR * (1.0 - block(tR, 0.1)) * smoothstep(0.4, 0.75, up));
        // Hokusai's 1831 key block is itself blue (Prussian + indigo), not black.
        float key = (1.0 - smoothstep(1.1 * ms, 1.9 * ms, outlineD)) + crest;
        col = ink(col, hex(28, 42, 68), clamp(key, 0.0, 1.0) * 0.92);
        col = ink(col, hex(28, 42, 68), seaF * (1.0 - smoothstep(0.6, 1.3, abs(dL - 0.5))) * 0.9);
        col *= 1.0 - 0.04 * (grain - 0.5) * seaF;
    } else if (variant < 2.5) {
        // ---- 2. Hiroshige, mist bands (Tōkaidō): soft colour, horizontal bands of mist laid across the mountains'
        // feet (they dissolve into paper), green-grey slopes, peach-pink bokashi at the tops, a fine grey key line; the
        // sea pale with a deep indigo ichimonji band far out; rain-fine horizontal ripples.
        col = ink(col, mix(hex(190, 212, 214), hex(70, 96, 140), smoothstep(80.0, 320.0, dL)), seaF * 0.85 * press);
        float rip = seaF * (1.0 - smoothstep(0.4, 0.9, abs(fract((p.y + 3.0 * noise(p / 80.0)) / 9.0) - 0.5) * 9.0))
            * step(0.5, noise(float2(p.x / 70.0, p.y / 9.0))) * (1.0 - smoothstep(40.0, 200.0, dL));
        col = ink(col, hex(80, 110, 140), rip * 0.4);
        col = ink(col, hex(222, 216, 176), landF * 0.6 * press);
        col = ink(col, hex(150, 170, 120), landF * 0.25 * (1.0 - smoothstep(0.0, 80.0, dW)));
        col = ink(col, hex(190, 212, 214), riv * 0.9);
        // The mist: a band of bare paper at each mountain's foot, its upper edge a straight bokashi line.
        float mist = 1.0 - smoothstep(0.15, 0.45, up);
        float m2 = massR * (1.0 - mist);
        col = ink(col, hex(140, 160, 120), m2 * 0.6 * press);
        col = ink(col, hex(90, 110, 100), block(tR, 0.4) * m2 * 0.6 * press);
        col = ink(col, hex(70, 80, 110), block(tR, 0.7) * m2 * 0.45 * press);
        col = ink(col, hex(70, 96, 140), topR * 0.55);
        col = ink(col, hex(140, 160, 120), massR * mist * smoothstep(0.0, 0.45, up) * 0.4);
        float key = (1.0 - smoothstep(0.8 * ms, 1.4 * ms, outlineD)) * (1.0 - mist) + crest * (1.0 - mist) * 0.7;
        col = ink(col, hex(70, 70, 80), clamp(key, 0.0, 1.0) * 0.8);
        col = ink(col, hex(80, 90, 100), seaF * (1.0 - smoothstep(0.5, 1.1, abs(dL - 0.5))) * 0.7);
        col *= 1.0 - 0.03 * (grain - 0.5);
    } else if (variant < 3.5) {
        // ---- 3. Red Fuji (Gaifū kaisei): one form in three zones - a green forest at the foot printed in goma-zuri
        // speckle, the lit slopes a red-iron (benigara) bokashi, the shadow plane deep red; the summit darkened with
        // snow streaks left as paper along the form; the sea an uneven (atenashi) Prussian blue; a thin brown key line.
        float3 bero = hex(26, 58, 118);
        col = ink(col, mix(hex(70, 110, 170), bero, smoothstep(0.3, 0.7, fbm(p / 150.0) * 0.6 + seaT * 0.6)), seaF * 0.9 * press);
        col = ink(col, hex(226, 204, 160), landF * 0.6 * press);
        col = ink(col, hex(70, 110, 170), riv * 0.85);
        float zoneSoft = 0.06;
        float forest = massR * (1.0 - smoothstep(0.22 - zoneSoft, 0.22 + zoneSoft, up + 0.08 * (noise(q / 30.0) - 0.5)));
        float goma = step(0.42, noise(q / 1.4)) * 0.6 + 0.4;
        col = ink(col, mix(hex(220, 120, 80), hex(178, 62, 38), smoothstep(0.2, 0.75, up)), massR * (1.0 - forest) * 0.85 * press);
        col = ink(col, hex(118, 26, 26), block(tR, 0.5) * (1.0 - forest) * 0.75 * press);
        col = ink(col, hex(46, 84, 62), forest * goma * 0.85);
        col = ink(col, hex(30, 50, 40), forest * block(tR, 0.55) * 0.6);
        col = ink(col, hex(60, 26, 24), topR * 0.6);
        float2 sq = float2(dot(q, across) / 2.0, dot(q, along) / 18.0);
        float snow = topR * smoothstep(0.3, 0.6, topR) * smoothstep(0.62, 0.68, noise(sq)) * (1.0 - block(tR, 0.55));
        col = mix(col, paper, snow * 0.9);
        float key = (1.0 - smoothstep(0.7 * ms, 1.2 * ms, outlineD)) * 0.6 + crest * 0.8;
        col = ink(col, hex(34, 24, 24), clamp(key, 0.0, 1.0) * 0.85);
        col = ink(col, hex(34, 40, 60), seaF * (1.0 - smoothstep(0.5, 1.1, abs(dL - 0.5))) * 0.7);
        col *= 1.0 - 0.04 * (grain - 0.5) * seaF;
    } else if (variant < 4.5) {
        // ---- 4. Shin-hanga, evening in the mountains (Yoshida, Hasui): many blocks, quiet realism - the lit faces
        // warm peach, the shadows layered violet-blue in four steps, the sea in evening teal with a light path; the
        // key line only on the silhouette, thin brown; baren rubbing and wood grain visible in the flat colours.
        col = ink(col, mix(hex(150, 180, 180), hex(50, 80, 110), smoothstep(0.0, 280.0, dL)), seaF * 0.9 * press);
        col = mix(col, paper, seaF * smoothstep(0.82, 0.9, noise(float2(p.x / 220.0, p.y / 14.0))) * 0.35);
        col = ink(col, hex(214, 196, 150), landF * 0.6 * press);
        col = ink(col, hex(160, 160, 110), landF * smoothstep(0.0, 1.0, mw.a) * 0.3);
        col = ink(col, hex(150, 180, 180), riv * 0.9);
        // Each block printed over the last with a near-opaque body (shin-hanga layers many thin impressions).
        float t = tR * 0.6 + toneOf(mfacet.eval(p + reg)) * massR * 0.4;
        float3 m = hex(246, 206, 170);
        m = mix(m, hex(206, 180, 196), block(t, 0.22));
        m = mix(m, hex(156, 146, 190), block(t, 0.42));
        m = mix(m, hex(110, 106, 160), block(t, 0.62));
        m = mix(m, hex(72, 70, 120), block(t, 0.8));
        m = mix(m, hex(252, 222, 176), topR * (1.0 - block(t, 0.3)) * 0.6);
        col = mix(col, col * m / max(paper, float3(0.5)), massR * press);
        float key = 1.0 - smoothstep(0.7 * ms, 1.3 * ms, outlineD);
        col = ink(col, hex(80, 56, 70), key * 0.8);
        col *= 1.0 - 0.03 * (baren - 0.5) - 0.04 * (grain - 0.5);
    } else {
        // ---- 5. Sōsaku-hanga, the gouge's trace (Munakata): the mountain cut as one black block - the light is what
        // the gouge carved away, in broad white planes on the lit faces and in V-cut strokes along the form in the
        // half-tones; strong flat earth red on land, the sea in a cut indigo with white gouge lines; no fine lines.
        float2 sp = float2(dot(q, across) / 2.2, dot(q, along) / 22.0);
        float gouge = noise(sp) * 0.7 + noise(sp * float2(0.5, 2.0) + 4.0) * 0.3;
        col = ink(col, hex(46, 62, 104), seaF * 0.9 * press);
        // Gouge cuts in the sea: long shallow strokes following the shore, a few, white where the wood was removed.
        float band = fract((dL + 6.0 * noise(p / 90.0)) / 22.0);
        float cutSea = seaF * (1.0 - smoothstep(0.0, 0.12, abs(band - 0.5) - 0.02 * noise(p / 4.0)))
            * step(0.42, noise(float2(p.x / 55.0, dL / 22.0))) * (1.0 - smoothstep(30.0, 200.0, dL));
        col = mix(col, paper, cutSea * 0.85);
        col = ink(col, hex(214, 150, 110), landF * 0.5 * press);
        col = ink(col, hex(140, 70, 46), landF * (1.0 - smoothstep(0.0, 5.0, dW)) * 0.6);
        col = ink(col, hex(46, 62, 104), riv * 0.9);
        // The mountain: black only on the shadow side; the half-tones cut in V-strokes along the form; the lit faces
        // carved away to the paper, with the earth colour printed over them.
        float t = tFacet * 0.5 + tBig * 0.5;
        float shadow = block(t + 0.22 * (gouge - 0.5), 0.5);
        float half_ = block(t + 0.3 * (gouge - 0.5), 0.3) * (1.0 - shadow);
        float vcut = smoothstep(0.55, 0.62, gouge);
        float blackBlock = massA * (shadow * (1.0 - 0.8 * vcut * smoothstep(0.5, 0.65, 1.0 - t)) + half_ * (1.0 - vcut));
        col = ink(col, hex(232, 196, 150), massA * 0.45);
        col = mix(col, hex(30, 26, 24), blackBlock * 0.92);
        float key = 1.0 - smoothstep(1.4 * ms, 2.4 * ms, outlineD);
        col = mix(col, hex(30, 26, 24), key * massA * 0.9);
        col *= 1.0 - 0.06 * (grain - 0.5) * (landF + blackBlock);
    }

    col += (hash(floor(p)) - 0.5) * 0.01;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> BlockEffect = new(() =>
        SKRuntimeEffect.CreateShader(BlockSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader PrintBlockShader(SKImage mountains, SKImage big, SKImage facets, SKImage crestA, SKImage crestB, SKImage wide,
        SKImage tensor, SKImage geo, SKImage river, Aqua aqua, float ms)
    {
        var builder = new SKRuntimeShaderBuilder(BlockEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        var shaders = new List<SKShader>();
        SKShader Child(SKImage image) { var s = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear); shaders.Add(s); return s; }
        builder.Children["mount"] = Child(mountains); builder.Children["mbig"] = Child(big); builder.Children["mfacet"] = Child(facets);
        builder.Children["crestA"] = Child(crestA); builder.Children["crestB"] = Child(crestB); builder.Children["mwide"] = Child(wide);
        builder.Children["tensor"] = Child(tensor); builder.Children["geo"] = Child(geo); builder.Children["river"] = Child(river);
        builder.Uniforms["variant"] = (float)aqua.Variant;
        builder.Uniforms["paper"] = Stylize.Hex(aqua.Paper);
        builder.Uniforms["ms"] = ms;
        var result = builder.Build();
        foreach (var s in shaders) s.Dispose();
        return result;
    }
}
