using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Round 3 of the R09 styles, one style at a time (owner 2026-10-24): five variants of «Тушь» that
/// look like real ink paintings, not a filter. MAP_STYLE_INK = output folder, MAP_LIGHT_ASSETS as
/// before. The test map is rendered unlit and without names; a multi-pass medium simulation paints it:
/// a structure-tensor flow field (the direction of the forms), line integral convolution of noise
/// along it (brush hairs following the slopes), ink tones from the painting's values, ink bleeding
/// into paper fibres, tide lines where wet ink dried, dry-brush break-up on the paper tooth, pen
/// hatching or stippling for the pen variants; the owner's ink lines come back on top, then the names.
/// </summary>
public sealed partial class StyleMockupDump
{
    private sealed record Ink(
        float[] InkColor, float[] Paper, float Gamma, float Reserve, float Levels, float Wash,
        float Bleed, float Tide, float Stroke, float Dry, float Lines, float Hatch, float HatchSpacing,
        float Stipple, float Tint, float[] TintWarm, float[] TintCool, float Mist, float Granulation, float Fibres, float Age);

    [Fact]
    public void Dump_ink_variants_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_INK");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var (document, labels) = Scene(images);
            using var bare = Render(document.WithLayer(labels, l => l with { Visible = false }), images);
            using var names = RenderOnly(document, labels, images);
            using var tensor = Tensor(bare);
            using var soft = Blurred(bare, 2.5f);
            using var wide = Blurred(bare, 9f);
            var black = Stylize.Hex("#121316");
            var variants = new (string Name, Ink Ink)[]
            {
                // 1. Japanese wet brush on xuan paper: few ink tones, lots of white paper, bleeding, tide lines, mist.
                ("Тушь 1 · Суми-э, мокрая кисть", new(black, Stylize.Hex("#f2eee3"), 1.25f, 0.16f, 4, 1, 1, 0.75f, 0.55f, 0.25f, 0.9f, 0, 6, 0, 0,
                    Stylize.Hex("#c8a46a"), Stylize.Hex("#4c5f7a"), 0.55f, 0.35f, 0.7f, 0)),
                // 2. Chinese shan shui: dry textured brush strokes along the forms, pale ochre and indigo, mist bands, aged silk.
                ("Тушь 2 · Шань-шуй, сухая кисть и туман", new(Stylize.Hex("#1a1612"), Stylize.Hex("#e6d8bc"), 1.0f, 0.10f, 0, 1, 0.35f, 0.25f, 1.0f, 0.85f, 0.75f, 0, 6, 0, 0.45f,
                    Stylize.Hex("#c99a55"), Stylize.Hex("#3d5670"), 0.9f, 0.2f, 0.9f, 0.55f)),
                // 3. Pen and ink: no wash at all, black line and hatching that follows the forms, white paper.
                ("Тушь 3 · Перо, штриховка по форме", new(Stylize.Hex("#0e0e10"), Stylize.Hex("#f7f5ef"), 1.1f, 0.12f, 0, 0, 0, 0, 0.15f, 0, 1, 1, 5.5f, 0, 0,
                    Stylize.Hex("#000000"), Stylize.Hex("#000000"), 0, 0, 0.25f, 0)),
                // 4. Pen stippling: tone made only of ink dots, line kept.
                ("Тушь 4 · Перо, пуантилизм", new(Stylize.Hex("#121214"), Stylize.Hex("#f5f1e6"), 1.15f, 0.10f, 0, 0, 0, 0, 0, 0, 1, 0, 6, 1, 0,
                    Stylize.Hex("#000000"), Stylize.Hex("#000000"), 0, 0, 0.3f, 0)),
                // 5. Line and wash, a traveller's sketchbook: iron-gall sepia line, two pale washes with pooled edges, cream paper.
                ("Тушь 5 · Перо и размывка, путевой альбом", new(Stylize.Hex("#3a2414"), Stylize.Hex("#efe3c8"), 1.35f, 0.22f, 2, 0.55f, 0.6f, 1.0f, 0.3f, 0.15f, 1, 0, 6, 0, 0,
                    Stylize.Hex("#000000"), Stylize.Hex("#000000"), 0, 0.8f, 0.5f, 0.35f)),
            };
            foreach (var (name, ink) in variants)
            {
                using var painted = PaintInk(bare, tensor, soft, wide, ink);
                using var tintedNames = TintNames(names, ink.InkColor);
                using var withNames = Overlay(painted, tintedNames);
                using var data = withNames.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(output, name + ".png"), data.ToArray());
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    /// <summary>
    /// The three chosen ink styles as Map itself paints them (document style, SceneRenderer), beside
    /// the mock-ups: MAP_STYLE_INK_APP = output folder. Also a half-scale render, the zoomed-out view.
    /// </summary>
    [Fact]
    public void Dump_ink_styles_from_the_renderer_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_INK_APP");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var (document, _) = Scene(images);
            foreach (var (name, preset) in new[]
            {
                ("Тушь · Суми-э, мокрая кисть", MapStylePreset.InkWetBrush),
                ("Тушь · Перо, штриховка по форме", MapStylePreset.InkPenHatching),
                ("Тушь · Перо, пуантилизм", MapStylePreset.InkPenStipple),
            })
            {
                var styled = document with { Style = MapStyle.For(preset) };
                foreach (var scale in new[] { 1f, 0.5f })
                {
                    using var surface = SKSurface.Create(new SKImageInfo((int)(W * scale), (int)(H * scale)))!;
                    surface.Canvas.Scale(scale);
                    SceneRenderer.Render(surface.Canvas, styled, new RenderOptions { Stamps = new Images(images), IsExport = true, ViewScale = scale });
                    using var image = surface.Snapshot();
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(output, name + (scale < 1 ? " · 50 %" : "") + ".png"), data.ToArray());
                }
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    private static SKImage Blurred(SKImage image, float sigma)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) };
        surface.Canvas.DrawImage(image, 0, 0, paint);
        return surface.Snapshot();
    }

    /// <summary>The smoothed structure tensor of the painting's values: where and how strongly its forms run.</summary>
    private static SKImage Tensor(SKImage map)
    {
        const string source = """
uniform shader map;
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
half4 main(float2 p) {
    float tl = lum(map.eval(p + float2(-1, -1)).rgb), t = lum(map.eval(p + float2(0, -1)).rgb), tr = lum(map.eval(p + float2(1, -1)).rgb);
    float ml = lum(map.eval(p + float2(-1, 0)).rgb), mr = lum(map.eval(p + float2(1, 0)).rgb);
    float bl = lum(map.eval(p + float2(-1, 1)).rgb), b = lum(map.eval(p + float2(0, 1)).rgb), br = lum(map.eval(p + float2(1, 1)).rgb);
    float gx = (tr + 2.0 * mr + br - tl - 2.0 * ml - bl) * 0.25;
    float gy = (bl + 2.0 * b + br - tl - 2.0 * t - tr) * 0.25;
    return half4(half(gx * gx), half(gy * gy), half(gx * gy * 0.5 + 0.5), 1.0);
}
""";
        var info = new SKImageInfo(W, H, SKColorType.RgbaF16, SKAlphaType.Premul);
        using var raw = SKSurface.Create(info)!;
        using (var effect = SKRuntimeEffect.CreateShader(source, out var errors) ?? throw new InvalidOperationException(errors))
        {
            var builder = new SKRuntimeShaderBuilder(effect);
            using var child = map.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            builder.Children["map"] = child;
            using var shader = builder.Build();
            using var paint = new SKPaint { Shader = shader };
            raw.Canvas.DrawRect(0, 0, W, H, paint);
        }
        using var rawImage = raw.Snapshot();
        using var smooth = SKSurface.Create(info)!;
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(5, 5, SKShaderTileMode.Clamp) })
            smooth.Canvas.DrawImage(rawImage, 0, 0, paint);
        return smooth.Snapshot();
    }

    private static SKImage TintNames(SKImage names, float[] ink)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        surface.Canvas.Clear(SKColors.Transparent);
        // The names' dark letters take the ink; their light halo stays paper-light.
        var m = new float[]
        {
            0.35f, 0.35f, 0.35f, 0, ink[0] * 0.65f,
            0.35f, 0.35f, 0.35f, 0, ink[1] * 0.65f,
            0.35f, 0.35f, 0.35f, 0, ink[2] * 0.65f,
            0, 0, 0, 1, 0,
        };
        using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(m) };
        surface.Canvas.DrawImage(names, 0, 0, paint);
        return surface.Snapshot();
    }

    private const string InkSource = """
uniform shader map;
uniform shader tensor;
uniform shader soft;
uniform shader wide;
uniform float3 inkColor;
uniform float3 paper;
uniform float gamma;
uniform float reserve;
uniform float levels;
uniform float wash;
uniform float bleed;
uniform float tide;
uniform float stroke;
uniform float dry;
uniform float lines;
uniform float hatch;
uniform float hatchSpacing;
uniform float stipple;
uniform float tint;
uniform float3 tintWarm;
uniform float3 tintCool;
uniform float mist;
uniform float granulation;
uniform float fibres;
uniform float age;
uniform float2 size;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float2 rot(float2 v, float a) { float c = cos(a), s = sin(a); return float2(c * v.x - s * v.y, s * v.x + c * v.y); }

// Paper fibres: thin long strands in slowly turning directions, and soft blotches of sizing.
float fibre(float2 p) {
    float a = fbm(p / 260.0) * 6.2832;
    float2 q = rot(p, a);
    float strands = noise(float2(q.x / 22.0, q.y / 0.9));
    return strands * 0.7 + noise(p / 1.7) * 0.3;
}

// Ink density of the painting at p, from its values: dark paint = much ink, light = paper left white.
float density(float3 c) {
    float d = pow(clamp(1.0 - lum(c), 0.0, 1.0), gamma);
    return smoothstep(reserve, reserve + 0.12, d) * d;
}

half4 main(float2 p) {
    float4 src = map.eval(p);
    float3 c0 = src.rgb;
    // Flow of the forms: the tangent of the smoothed structure tensor.
    float4 t = tensor.eval(p);
    float E = t.r, G = t.g, F = t.b - 0.5;
    float ang = 0.5 * atan(2.0 * F, E - G + 1e-7);
    float2 grad = float2(cos(ang), sin(ang));
    float2 tang = float2(-grad.y, grad.x);
    float strength = clamp(sqrt((E - G) * (E - G) + 4.0 * F * F) * 40.0, 0.0, 1.0);

    // Line integral convolution: noise smeared along the forms = hairs of a brush.
    float lic = 0.0;
    for (int k = -9; k <= 9; k++) lic += noise((p + tang * float(k) * 1.6) / 1.4);
    lic /= 19.0;
    lic = clamp((lic - 0.5) * 3.2 + 0.5, 0.0, 1.0);
    float fib = fibre(p);

    // Ink tones: the painting's values, quantised into a few ink dilutions with soft edges.
    float d = density(c0);
    // Bleeding: wet ink creeps into the fibres - the density of a nearby, fibre-shifted, softened spot.
    float2 drift = (float2(fib, fibre(p + 31.7)) - 0.5) * 7.0;
    float db = density(soft.eval(p + drift).rgb);
    d = mix(d, max(d, db * 0.92), bleed * wash);
    if (levels > 0.0) {
        float q = d * levels;
        float f = fract(q);
        d = (floor(q) + smoothstep(0.42, 0.58, f)) / levels;
    }
    // Tide line: where a wet patch dried, pigment piled up along its edge.
    float dw = density(wide.eval(p).rgb);
    float edge = clamp((d - dw) * 4.0, 0.0, 1.0) * smoothstep(0.05, 0.25, d);
    d = clamp(d + tide * wash * 0.35 * edge, 0.0, 1.0);
    // Brush hairs follow the forms where there are forms.
    d *= mix(1.0, 0.45 + 1.1 * lic, stroke * (0.35 + 0.65 * strength) * smoothstep(0.03, 0.3, d));
    // Dry brush: the paper's tooth breaks the stroke where the brush carried little ink.
    float tooth = fib * 0.6 + noise(p / 2.2) * 0.4;
    float carried = d * (0.8 + 0.6 * lic);
    d = mix(d, d * smoothstep(tooth - 0.12, tooth + 0.12, carried * 1.4), dry);
    // Granulation of the pigment in the wash.
    d *= 1.0 + granulation * (noise(p / 1.1) - 0.5) * 0.6 * wash;
    // Mist: horizontal bands where the ink thins out to paper, the way a landscape fades into fog.
    float band = smoothstep(0.45, 0.75, fbm(float2(p.x / 900.0, p.y / 140.0)));
    d *= 1.0 - mist * band * (1.0 - smoothstep(0.55, 0.9, d));
    d *= wash;

    // Pen: hatching along the forms in four straight families, crossing as the tone darkens.
    float pen = 0.0;
    if (hatch > 0.0) {
        float tone = pow(clamp(1.0 - lum(soft.eval(p).rgb), 0.0, 1.0), gamma);
        float a = atan(tang.y, tang.x);
        float bin = floor(a / 0.7854 + 0.5) * 0.7854;
        float2 n1 = float2(-sin(bin), cos(bin));
        float2 n2 = float2(-sin(bin + 1.5708), cos(bin + 1.5708));
        float wob = (noise(p / 30.0) - 0.5) * 1.5;
        float l1 = 1.0 - smoothstep(0.08, 0.22, abs(fract((dot(p, n1) + wob) / hatchSpacing) - 0.5));
        float l2 = 1.0 - smoothstep(0.08, 0.22, abs(fract((dot(p, n2) + wob) / (hatchSpacing * 0.85)) - 0.5));
        float l3 = 1.0 - smoothstep(0.08, 0.22, abs(fract((p.x + p.y) / (hatchSpacing * 0.7)) - 0.5));
        pen = max(pen, l1 * smoothstep(0.18, 0.28, tone));
        pen = max(pen, l2 * smoothstep(0.42, 0.52, tone));
        pen = max(pen, l3 * smoothstep(0.66, 0.76, tone));
        pen *= 0.75 + 0.5 * noise(p / 3.0);
        pen = clamp(pen * hatch, 0.0, 1.0);
    }
    // Pen: stippling, dots in a jittered grid sized by the tone.
    if (stipple > 0.0) {
        float tone = pow(clamp(1.0 - lum(soft.eval(p).rgb), 0.0, 1.0), gamma);
        float cellSize = 4.2;
        float2 cell = floor(p / cellSize);
        float best = 1e9;
        float rnd = 0.0;
        for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
            float2 g = cell + float2(float(i), float(j));
            float2 centre = (g + float2(hash(g), hash(g + 7.1))) * cellSize;
            float dist = length(p - centre);
            if (dist < best) { best = dist; rnd = hash(g + 3.3); }
        }
        float on = step(rnd, tone * 1.15);
        pen = max(pen, on * (1.0 - smoothstep(0.9, 1.6, best)) * stipple);
    }

    // The owner's ink: dark thin strokes of the painting, with a living, slightly varying weight.
    float lo = lum(c0);
    float around = 0.25 * (lum(map.eval(p + float2(2.0, 0)).rgb) + lum(map.eval(p - float2(2.0, 0)).rgb)
        + lum(map.eval(p + float2(0, 2.0)).rgb) + lum(map.eval(p - float2(0, 2.0)).rgb));
    float line = smoothstep(0.45, 0.2, lo) * smoothstep(0.02, 0.12, around - lo);
    line *= 0.8 + 0.4 * lic;

    // Paper: fibres, blotches, ageing toward the edges of the sheet.
    float2 qn = (p - size * 0.5) / size;
    float ageing = age * smoothstep(0.25, 0.75, length(qn * float2(1.0, 0.9)) * 1.3);
    float3 sheet = paper * (1.0 - 0.06 * fibres * (fib - 0.5) - 0.05 * fibres * (fbm(p / 220.0) - 0.5));
    sheet *= 1.0 - ageing * 0.25 * float3(0.6, 0.8, 1.0);
    // A faint wash colour (shan shui): ochre in the light ink, indigo in the deep.
    float3 inkHere = mix(inkColor, mix(tintWarm, tintCool, smoothstep(0.2, 0.7, d)), tint * (1.0 - smoothstep(0.55, 0.95, d)));
    float3 col = mix(sheet, sheet * inkHere * 1.05 + inkHere * 0.1, clamp(d, 0.0, 1.0));
    col = mix(col, inkColor, clamp(pen, 0.0, 1.0));
    col = mix(col, inkColor, clamp(line * lines, 0.0, 1.0));
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> InkEffect = new(() =>
        SKRuntimeEffect.CreateShader(InkSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKImage PaintInk(SKImage map, SKImage tensor, SKImage soft, SKImage wide, Ink ink)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        var builder = new SKRuntimeShaderBuilder(InkEffect.Value);
        using var cMap = map.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cTensor = tensor.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cSoft = soft.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
        using var cWide = wide.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        builder.Children["map"] = cMap; builder.Children["tensor"] = cTensor;
        builder.Children["soft"] = cSoft; builder.Children["wide"] = cWide;
        builder.Uniforms["inkColor"] = ink.InkColor; builder.Uniforms["paper"] = ink.Paper;
        builder.Uniforms["gamma"] = ink.Gamma; builder.Uniforms["reserve"] = ink.Reserve; builder.Uniforms["levels"] = ink.Levels;
        builder.Uniforms["wash"] = ink.Wash; builder.Uniforms["bleed"] = ink.Bleed; builder.Uniforms["tide"] = ink.Tide;
        builder.Uniforms["stroke"] = ink.Stroke; builder.Uniforms["dry"] = ink.Dry; builder.Uniforms["lines"] = ink.Lines;
        builder.Uniforms["hatch"] = ink.Hatch; builder.Uniforms["hatchSpacing"] = ink.HatchSpacing; builder.Uniforms["stipple"] = ink.Stipple;
        builder.Uniforms["tint"] = ink.Tint; builder.Uniforms["tintWarm"] = ink.TintWarm; builder.Uniforms["tintCool"] = ink.TintCool;
        builder.Uniforms["mist"] = ink.Mist; builder.Uniforms["granulation"] = ink.Granulation; builder.Uniforms["fibres"] = ink.Fibres;
        builder.Uniforms["age"] = ink.Age; builder.Uniforms["size"] = new float[] { W, H };
        using var shader = builder.Build();
        using (var paint = new SKPaint { Shader = shader }) surface.Canvas.DrawRect(0, 0, W, H, paint);
        return surface.Snapshot();
    }
}
