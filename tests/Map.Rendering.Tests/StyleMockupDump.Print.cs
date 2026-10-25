using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles: «Печать (двухкрасочная)», five two-ink print processes (owner 2026-10-25, next after «Укиё-э»). Every
/// variant separates the map into exactly two inks, each with its own screen - risograph grain, a screen-print poster's
/// flat opaque inks, letterpress solids with ink squeeze, a tinted lithograph's crayon on stone, a duotone halftone - and
/// repaints the mountains from their large-scale form (DEC-061 п. 2): silhouette, big facets, light and shadow, main crests.
/// Dot and grain sizes follow the mountains' size. Names are not stylised. Research:
/// docs/research/2026-10-25-r09-print-styles.md. MAP_STYLE_PRINT = output folder, MAP_LIGHT_ASSETS as before;
/// MAP_STYLE_WATERCOLOR2_SCENE / _ONLY also apply.
/// </summary>
public sealed partial class StyleMockupDump
{
    [Fact]
    public void Dump_print_variants_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_PRINT");
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
                ("Печать 1 · Ризограф, розовый и синий", new(1, "#f6f3ec")),
                ("Печать 2 · Шелкография, плакат", new(2, "#efe6d2")),
                ("Печать 3 · Высокая печать, чёрный и красный", new(3, "#f1ece0")),
                ("Печать 4 · Литография с тоновой доской", new(4, "#f2ede2")),
                ("Печать 5 · Дуплекс, растр атласа", new(5, "#f7f5ef")),
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
                    using (var shader = TwoInkShader(mountains, big, facets, crestA, crestB, wide, tensor, geo, riverOnly, aqua, ms))
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

    private const string TwoInkSource = """
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
float edgeDist(float a, float2 g) { return abs(a - 0.5) / max(length(g), 1e-4); }
// An amplitude-modulated halftone: coverage `c` printed as round dots on a screen of `cell` px turned by `ang`.
float halftone(float2 p, float c, float cell, float ang) {
    float2 r = float2(cos(ang) * p.x - sin(ang) * p.y, sin(ang) * p.x + cos(ang) * p.y) / cell;
    float v = 0.5 + 0.25 * (cos(r.x * 6.2832) + cos(r.y * 6.2832));
    float aa = 0.9 / cell;
    return smoothstep(v - aa, v + aa, c);
}
// Risograph grain: the stencil's master is a stochastic dither - clumpy, slightly lighter in streaks along the drum.
float riso(float2 p, float c, float g) {
    float n = noise(p / (0.9 * g)) * 0.6 + noise(p / (2.3 * g) + 7.0) * 0.4;
    return smoothstep(n - 0.06, n + 0.06, c * (0.93 + 0.07 * noise(float2(p.x / 400.0, p.y / 3.0))));
}

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float dL = g.r * 255.0;
    float dW = g.g * 255.0;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float seaF = 1.0 - landF;
    float riv = river.eval(p).a * smoothstep(0.5, 3.0, dW);
    float2 q = p / ms;
    float seaT = smoothstep(0.0, 300.0, dL);

    // ---- the form, read at the printer's scale
    float4 mb = mbig.eval(p);
    float4 mf = mfacet.eval(p);
    float2 ga = float2(mbig.eval(p + float2(1.5, 0)).a - mbig.eval(p - float2(1.5, 0)).a,
                       mbig.eval(p + float2(0, 1.5)).a - mbig.eval(p - float2(0, 1.5)).a) / 3.0;
    float massA = smoothstep(0.47, 0.53, mb.a);
    float outlineD = edgeDist(mb.a, ga);
    float t = (toneOf(mb) * 0.55 + toneOf(mf) * 0.45) * massA;
    float crest = smoothstep(0.11, 0.125, onWhite(crestB.eval(p)) - onWhite(crestA.eval(p))) * smoothstep(0.5, 0.7, mount.eval(p).a);
    float up = mwide.eval(p + float2(0, 70.0 * ms)).a * massA;
    float4 mw = mwide.eval(p);
    float4 J = tensor.eval(p);
    float ang = 0.5 * atan(2.0 * (J.b - 0.5) * 2.0, J.r - J.g + 1e-6);
    float2 across = float2(cos(ang), sin(ang));
    float key = 1.0 - smoothstep(0.9 * ms, 1.6 * ms, outlineD);

    // Two ink coverages, 0..1, composited at the end; `opaque` = inks cover (screen print) instead of overprinting.
    float c1 = 0.0; float c2 = 0.0;
    float3 ink1 = float3(0); float3 ink2 = float3(0);
    float opaque = 0.0;
    float3 col = paper * (1.0 - 0.02 * (noise(p / 1.3) - 0.5));

    if (variant < 1.5) {
        // ---- 1. Risograph, fluorescent pink and blue: two drums, grainy stochastic dots, the second drum ~2 px off;
        // where both print, they overprint to violet. Pink = warm (land, lit slopes, the shore), blue = water and shadow.
        ink1 = hex(255, 72, 176); ink2 = hex(0, 120, 191);
        float2 off = float2(2.2, -1.4) * ms;
        float4 mb2 = mbig.eval(p + off);
        float t2 = (toneOf(mb2) * 0.55 + toneOf(mfacet.eval(p + off)) * 0.45) * smoothstep(0.47, 0.53, mb2.a);
        float g2 = geo.eval(p + off).r * 255.0;
        float sea2 = 1.0 - smoothstep(-0.8, 0.8, geo.eval(p + off).g * 255.0 - g2);
        float pinkD = landF * 0.22 + massA * (0.35 * (1.0 - t) + 0.1) + seaF * (1.0 - smoothstep(0.0, 18.0, dL)) * 0.5;
        float blueD = sea2 * (0.35 + 0.45 * smoothstep(0.0, 260.0, g2)) + smoothstep(0.25, 0.85, t2) * 0.85 + riv * 0.6;
        blueD += (1.0 - smoothstep(0.9 * ms, 1.6 * ms, edgeDist(mb2.a, ga))) * 0.9;
        c1 = riso(p, clamp(pinkD, 0.0, 0.92), max(ms, 0.6));
        c2 = riso(p + 11.0, clamp(blueD, 0.0, 0.92), max(ms, 0.6));
    } else if (variant < 2.5) {
        // ---- 2. Screen-print poster (national-park posters): two opaque flat inks with knock-outs - burnt orange for the
        // light, deep navy for shade, sea and line; mid-tones in a coarse halftone; the paper is the third colour.
        ink1 = hex(226, 112, 58); ink2 = hex(32, 48, 76);
        opaque = 1.0;
        float cell = 6.0 * max(ms, 0.6);
        c1 = landF * (1.0 - massA) * halftone(p, 0.28, cell, 0.785) + massA * smoothstep(0.08, 0.12, t + 0.05) * (1.0 - smoothstep(0.62, 0.66, t));
        c1 = max(c1, seaF * (1.0 - smoothstep(6.0, 9.0, dL)));
        c2 = seaF * smoothstep(9.0, 12.0, dL) * max(step(140.0, dL), halftone(p, 0.35 + 0.6 * seaT, cell, 0.785));
        c2 = max(c2, massA * max(smoothstep(0.62, 0.66, t), halftone(p, smoothstep(0.35, 0.62, t) * 0.7, cell, 0.785)));
        c2 = max(c2, key * massA + crest * 0.9 + riv);
        c2 = max(c2, seaF * (1.0 - smoothstep(0.6, 1.4, abs(dL - 0.5))));
        // Snow at the peaks: knocked out of both inks.
        float snowK = massA * smoothstep(0.55, 0.75, up) * (1.0 - smoothstep(0.1, 0.16, t));
        c1 *= 1.0 - snowK; c2 *= 1.0 - snowK;
    } else if (variant < 3.5) {
        // ---- 3. Letterpress, black and red: solids from lino blocks - ink squeezed to a darker rim at every edge, the
        // middle of big solids a little starved (the paper's grain shows), the forms pressed into the paper.
        ink1 = hex(200, 30, 46); ink2 = hex(26, 24, 26);
        float starve = smoothstep(0.35, 0.65, noise(p / 1.6) * 0.6 + noise(p / 9.0) * 0.4);
        float redSolid = massA * smoothstep(0.26, 0.3, t) * (1.0 - smoothstep(0.68, 0.72, t));
        float landRed = landF * (1.0 - smoothstep(0.0, 10.0, dW)) * smoothstep(0.0, 2.0, dW);
        c1 = max(redSolid, landRed) * (0.82 + 0.18 * starve);
        float blackSolid = massA * smoothstep(0.68, 0.72, t);
        float wl = abs(fract((dL + 2.0 * noise(p / 40.0)) / 11.0) - 0.5) * 11.0;
        float seaBlack = seaF * (1.0 - smoothstep(0.9, 1.6, wl)) * (1.0 - smoothstep(30.0, 46.0, dL));
        c2 = max(max(blackSolid, key * massA), max(crest, seaBlack)) * (0.85 + 0.15 * starve);
        c2 = max(c2, seaF * (1.0 - smoothstep(0.7, 1.6, abs(dL - 0.5))) + riv * 0.85);
        // Ink squeeze: the full-strength rim just inside each solid's edge, where the middle is starved.
        float rim = redSolid * ((1.0 - smoothstep(0.28, 0.32, t)) + smoothstep(0.65, 0.69, t)) + landRed * (1.0 - smoothstep(2.0, 4.0, dW));
        c1 = max(c1, clamp(rim, 0.0, 1.0));
    } else if (variant < 4.5) {
        // ---- 4. Tinted lithograph (travel books, 1830-1870): a black crayon drawing on grained stone - tone is the
        // density of crayon specks - and one buff tint stone over everything, with the highlights scraped out of it.
        ink1 = hex(214, 190, 140); ink2 = hex(34, 30, 30);
        float stone = fbm(p / 1.1) * 0.65 + noise(p / 0.7) * 0.35;
        float crayon = t * 0.9 + crest * 0.6 + key * 0.7;
        float seaCrayon = seaF * (0.18 + 0.3 * seaT) * (0.6 + 0.4 * step(0.5, fract(p.y / 5.0 + 0.3 * noise(p / 60.0))));
        float landCrayon = landF * (1.0 - massA) * (1.0 - smoothstep(0.0, 14.0, dW)) * 0.35;
        c2 = smoothstep(stone - 0.05, stone + 0.05, max(max(crayon, seaCrayon), max(landCrayon, riv * 0.6)) * 0.95);
        float scraped = massA * smoothstep(0.55, 0.8, up) * (1.0 - smoothstep(0.08, 0.16, t))
            + seaF * (1.0 - smoothstep(2.0, 7.0, dL)) * step(0.4, noise(p / 5.0));
        c1 = 0.85 * (1.0 - clamp(scraped, 0.0, 1.0));
    } else {
        // ---- 5. Duotone halftone (mid-century atlas): black at 45° for relief shading and line, a blue spot ink at 75°
        // for water and the cool shadow side; the same two screens make every tone, with the rosette visible close up.
        ink1 = hex(0, 94, 160); ink2 = hex(28, 28, 32);
        float cell = 4.2 * max(ms, 0.7);
        float blue = seaF * (0.3 + 0.45 * seaT) + riv * 0.8 + massA * smoothstep(0.3, 0.8, t) * 0.35 + landF * (1.0 - massA) * 0.05;
        float black = massA * smoothstep(0.1, 0.9, t) * 0.75 + seaF * seaT * 0.12 + landF * (1.0 - massA) * 0.06;
        c1 = halftone(p, clamp(blue, 0.0, 0.95), cell, 1.309);
        c2 = halftone(p, clamp(black, 0.0, 0.95), cell, 0.785);
        c2 = max(c2, key * massA * 0.95 + crest * 0.8);
        c2 = max(c2, seaF * (1.0 - smoothstep(0.6, 1.3, abs(dL - 0.5))));
        c1 = max(c1, seaF * (1.0 - smoothstep(0.6, 1.3, abs(dL - 6.0))) * 0.9);
    }

    if (opaque > 0.5) {
        col = mix(col, ink1, c1);
        col = mix(col, ink2, c2);
    } else {
        col = col * mix(float3(1), ink1, c1) * mix(float3(1), ink2, c2);
    }
    col += (hash(floor(p)) - 0.5) * 0.008;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> TwoInkEffect = new(() =>
        SKRuntimeEffect.CreateShader(TwoInkSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader TwoInkShader(SKImage mountains, SKImage big, SKImage facets, SKImage crestA, SKImage crestB, SKImage wide,
        SKImage tensor, SKImage geo, SKImage river, Aqua aqua, float ms)
    {
        var builder = new SKRuntimeShaderBuilder(TwoInkEffect.Value);
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
