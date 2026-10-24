using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// The «Тушь» family (R09, DEC-059; the owner's choice of 2026-10-24 among five mock-ups): a
/// medium simulation over the finished map. A structure-tensor flow field gives the direction of
/// the forms; line integral convolution of noise along it lays brush hairs on the slopes; the
/// painting's values become a few ink dilutions; wet ink bleeds into the paper fibres and leaves a
/// tide line where it dried; a dry brush breaks on the paper's tooth; mist bands thin the ink;
/// the pen variants hatch along the forms or stipple; the owner's ink contour comes back on top.
/// Every texture is laid in document coordinates, so the canvas and the export at the same scale
/// give the same picture and export tiles meet without seams; detail finer than a device pixel
/// fades to its mean tone when the view is zoomed out.
/// </summary>
internal static class InkShader
{
    /// <summary>The constants a preset was tuned with; the effects' strengths come from the document.</summary>
    private sealed record Medium(string Ink, string Paper, float Gamma, float Reserve, float Levels, float Wash,
        float HatchSpacing, float Granulation);

    private static Medium For(MapStylePreset preset) => preset switch
    {
        MapStylePreset.InkPenHatching => new("#0e0e10", "#f7f5ef", 1.1f, 0.12f, 0, 0, 5.5f, 0),
        MapStylePreset.InkPenStipple => new("#121214", "#f5f1e6", 1.15f, 0.10f, 0, 0, 6, 0),
        _ => new("#121316", "#f2eee3", 1.25f, 0.16f, 4, 1, 6, 0.35f),
    };

    /// <summary>How far, in document pixels, the widest filter of the medium reaches beyond a pixel.</summary>
    public const float ReachDocumentPixels = 32;

    public const float SoftSigma = 2.5f;
    public const float WideSigma = 9f;
    public const float TensorSigma = 5f;

    private const string Source = """
uniform shader map;
uniform shader tensor;
uniform shader soft;
uniform shader wide;
uniform float2 origin;
uniform float docPerPx;
uniform float pxPerDoc;
uniform float detail;
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
uniform float mist;
uniform float granulation;
uniform float fibres;
uniform float strength;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float2 rot(float2 v, float a) { float c = cos(a), s = sin(a); return float2(c * v.x - s * v.y, s * v.x + c * v.y); }

// The colour of the map at a sample, laid over the paper where the map is transparent.
float3 over(float4 c) { return c.rgb + paper * (1.0 - c.a); }

// Paper fibres: thin long strands in slowly turning directions, and soft blotches of sizing.
float fibre(float2 q) {
    float a = fbm(q / 260.0) * 6.2832;
    float2 r = rot(q, a);
    float strands = noise(float2(r.x / 22.0, r.y / 0.9));
    return mix(0.5, strands * 0.7 + noise(q / 1.7) * 0.3, detail);
}

// Ink density from a value: dark paint = much ink, light = paper left white.
float density(float3 c) {
    float d = pow(clamp(1.0 - lum(c), 0.0, 1.0), gamma);
    return smoothstep(reserve, reserve + 0.12, d) * d;
}

half4 main(float2 p) {
    float4 src = map.eval(p);
    float3 c0 = over(src);
    float2 q = origin + p * docPerPx;
    float px = max(pxPerDoc, 1.0);

    // Flow of the forms: the tangent of the smoothed structure tensor (gradients per document pixel).
    float4 t = tensor.eval(p);
    float k2 = pxPerDoc * pxPerDoc;
    float E = t.r * k2, G = t.g * k2, F = (t.b - 0.5) * k2;
    float ang = 0.5 * atan(2.0 * F, E - G + 1e-7);
    float2 grad = float2(cos(ang), sin(ang));
    float2 tang = float2(-grad.y, grad.x);
    float strength2 = clamp(sqrt((E - G) * (E - G) + 4.0 * F * F) * 40.0, 0.0, 1.0);

    // Line integral convolution: noise smeared along the forms = hairs of a brush.
    float lic = 0.0;
    for (int k = -9; k <= 9; k++) lic += noise((q + tang * float(k) * 1.6) / 1.4);
    lic /= 19.0;
    lic = mix(0.5, clamp((lic - 0.5) * 3.2 + 0.5, 0.0, 1.0), detail);
    float fib = fibre(q);

    float d = density(c0);
    // Bleeding: wet ink creeps into the fibres - the density of a nearby, fibre-shifted, softened spot.
    float2 drift = (float2(fib, fibre(q + 31.7)) - 0.5) * 7.0 * pxPerDoc;
    float db = density(over(soft.eval(p + drift)));
    d = mix(d, max(d, db * 0.92), bleed * wash);
    if (levels > 0.0) {
        float lq = d * levels;
        d = (floor(lq) + smoothstep(0.42, 0.58, fract(lq))) / levels;
    }
    // Tide line: where a wet patch dried, pigment piled up along its edge.
    float dw = density(over(wide.eval(p)));
    float edge = clamp((d - dw) * 4.0, 0.0, 1.0) * smoothstep(0.05, 0.25, d);
    d = clamp(d + tide * wash * 0.35 * edge, 0.0, 1.0);
    // Brush hairs follow the forms where there are forms.
    d *= mix(1.0, 0.45 + 1.1 * lic, stroke * (0.35 + 0.65 * strength2) * smoothstep(0.03, 0.3, d));
    // Dry brush: the paper's tooth breaks the stroke where the brush carried little ink.
    float tooth = fib * 0.6 + mix(0.5, noise(q / 2.2), detail) * 0.4;
    float carried = d * (0.8 + 0.6 * lic);
    d = mix(d, d * smoothstep(tooth - 0.12, tooth + 0.12, carried * 1.4), dry);
    // Granulation of the pigment in the wash.
    d *= 1.0 + granulation * (noise(q / 1.1) - 0.5) * 0.6 * wash * detail;
    // Mist: horizontal bands where the ink thins out to paper, the way a landscape fades into fog.
    float band = smoothstep(0.45, 0.75, fbm(float2(q.x / 900.0, q.y / 140.0)));
    d *= 1.0 - mist * band * (1.0 - smoothstep(0.55, 0.9, d));
    d *= wash;

    // Pen: hatching along the forms in four straight families, crossing as the tone darkens.
    // Lines closer than about two device pixels fade into the flat tone they stand for.
    float tone = pow(clamp(1.0 - lum(over(soft.eval(p))), 0.0, 1.0), gamma);
    float pen = 0.0;
    if (hatch > 0.0) {
        float a = atan(tang.y, tang.x);
        float bin = floor(a / 0.7854 + 0.5) * 0.7854;
        float2 n1 = float2(-sin(bin), cos(bin));
        float2 n2 = float2(-sin(bin + 1.5708), cos(bin + 1.5708));
        float wob = (noise(q / 30.0) - 0.5) * 1.5;
        float l1 = 1.0 - smoothstep(0.08, 0.22, abs(fract((dot(q, n1) + wob) / hatchSpacing) - 0.5));
        float l2 = 1.0 - smoothstep(0.08, 0.22, abs(fract((dot(q, n2) + wob) / (hatchSpacing * 0.85)) - 0.5));
        float l3 = 1.0 - smoothstep(0.08, 0.22, abs(fract((q.x + q.y) / (hatchSpacing * 0.7)) - 0.5));
        float lined = max(max(l1 * smoothstep(0.18, 0.28, tone), l2 * smoothstep(0.42, 0.52, tone)), l3 * smoothstep(0.66, 0.76, tone));
        lined *= 0.75 + 0.5 * noise(q / 3.0);
        float even = 0.3 * smoothstep(0.18, 0.28, tone) + 0.25 * smoothstep(0.42, 0.52, tone) + 0.25 * smoothstep(0.66, 0.76, tone);
        pen = clamp(mix(even, lined, smoothstep(1.5, 3.0, hatchSpacing * 0.7 * pxPerDoc)) * hatch, 0.0, 1.0);
    }
    // Pen: stippling, dots in a jittered grid sized by the tone.
    if (stipple > 0.0) {
        float cellSize = 4.2;
        float2 cell = floor(q / cellSize);
        float best = 1e9;
        float rnd = 0.0;
        for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
            float2 g = cell + float2(float(i), float(j));
            float2 centre = (g + float2(hash(g), hash(g + 7.1))) * cellSize;
            float dist = length(q - centre);
            if (dist < best) { best = dist; rnd = hash(g + 3.3); }
        }
        float dots = step(rnd, tone * 1.15) * (1.0 - smoothstep(0.9, 1.6, best));
        float even = clamp(tone * 1.15, 0.0, 1.0) * 0.16;
        pen = max(pen, mix(even, dots, smoothstep(1.5, 3.5, cellSize * pxPerDoc)) * stipple);
    }

    // The owner's ink: dark thin strokes of the painting, with a living, slightly varying weight.
    float lo = lum(c0);
    float o = 2.0 * px;
    float around = 0.25 * (lum(over(map.eval(p + float2(o, 0)))) + lum(over(map.eval(p - float2(o, 0))))
        + lum(over(map.eval(p + float2(0, o)))) + lum(over(map.eval(p - float2(0, o)))));
    float line = smoothstep(0.45, 0.2, lo) * smoothstep(0.02, 0.12, around - lo);
    line *= 0.8 + 0.4 * lic;

    // Paper: fibres and sizing blotches.
    float3 sheet = paper * (1.0 - 0.06 * fibres * (fib - 0.5) - 0.05 * fibres * (fbm(q / 220.0) - 0.5));
    float3 col = mix(sheet, sheet * inkColor * 1.05 + inkColor * 0.1, clamp(d, 0.0, 1.0));
    col = mix(col, inkColor, clamp(pen, 0.0, 1.0));
    col = mix(col, inkColor, clamp(line * lines, 0.0, 1.0));
    col = mix(c0, clamp(col, 0.0, 1.0), strength);
    return half4(half3(col * src.a), half(src.a));
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
        SKRuntimeEffect.CreateShader(Source, out var errors) ?? throw new InvalidOperationException(errors));

    private const string TensorSource = """
uniform shader map;
float lum(float4 c) { return dot(c.a > 0.001 ? c.rgb / c.a : float3(1), float3(0.299, 0.587, 0.114)); }
half4 main(float2 p) {
    float tl = lum(map.eval(p + float2(-1, -1))), t = lum(map.eval(p + float2(0, -1))), tr = lum(map.eval(p + float2(1, -1)));
    float ml = lum(map.eval(p + float2(-1, 0))), mr = lum(map.eval(p + float2(1, 0)));
    float bl = lum(map.eval(p + float2(-1, 1))), b = lum(map.eval(p + float2(0, 1))), br = lum(map.eval(p + float2(1, 1)));
    float gx = (tr + 2.0 * mr + br - tl - 2.0 * ml - bl) * 0.25;
    float gy = (bl + 2.0 * b + br - tl - 2.0 * t - tr) * 0.25;
    return half4(half(gx * gx), half(gy * gy), half(gx * gy * 0.5 + 0.5), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> TensorEffect = new(() =>
        SKRuntimeEffect.CreateShader(TensorSource, out var errors) ?? throw new InvalidOperationException(errors));

    /// <summary>The raw (unsmoothed) structure tensor of an image, one device pixel per step.</summary>
    public static SKShader TensorShader(SKImage map)
    {
        var builder = new SKRuntimeShaderBuilder(TensorEffect.Value);
        using var child = map.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        builder.Children["map"] = child;
        return builder.Build();
    }

    /// <summary>
    /// The ink shader over the padded scene image (local coordinates = its pixels). <paramref name="origin"/>
    /// is the document point of the image's top-left pixel, <paramref name="pxPerDoc"/> the view scale.
    /// </summary>
    public static SKShader Build(SKImage map, SKImage tensor, SKImage soft, SKImage wide, SKPoint origin, float pxPerDoc, MapStyle style)
    {
        var medium = For(style.Preset);
        var pen = style.Pen.Effective;
        var builder = new SKRuntimeShaderBuilder(Effect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        using var cMap = map.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cTensor = tensor.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cSoft = soft.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cWide = wide.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        builder.Children["map"] = cMap; builder.Children["tensor"] = cTensor;
        builder.Children["soft"] = cSoft; builder.Children["wide"] = cWide;
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["docPerPx"] = 1 / pxPerDoc;
        builder.Uniforms["pxPerDoc"] = pxPerDoc;
        // Grain finer than a device pixel only flickers: it fades to its mean as the view zooms out.
        builder.Uniforms["detail"] = Smoothstep(0.3f, 0.9f, pxPerDoc);
        builder.Uniforms["inkColor"] = Rgb(medium.Ink);
        builder.Uniforms["paper"] = Rgb(medium.Paper);
        builder.Uniforms["gamma"] = medium.Gamma;
        builder.Uniforms["reserve"] = medium.Reserve;
        builder.Uniforms["levels"] = medium.Levels;
        builder.Uniforms["wash"] = medium.Wash;
        builder.Uniforms["hatchSpacing"] = medium.HatchSpacing;
        builder.Uniforms["granulation"] = medium.Granulation;
        builder.Uniforms["bleed"] = (float)style.Bleed.Effective;
        builder.Uniforms["tide"] = (float)style.Edges.Effective;
        builder.Uniforms["stroke"] = (float)style.Strokes.Effective;
        builder.Uniforms["dry"] = (float)style.DryBrush.Effective;
        builder.Uniforms["mist"] = (float)style.Mist.Effective;
        builder.Uniforms["hatch"] = style.Preset == MapStylePreset.InkPenHatching ? (float)pen : 0f;
        builder.Uniforms["stipple"] = style.Preset == MapStylePreset.InkPenStipple ? (float)pen : 0f;
        builder.Uniforms["fibres"] = (float)style.Paper.Effective;
        builder.Uniforms["lines"] = (float)style.Contour.Effective;
        builder.Uniforms["strength"] = (float)Math.Clamp(style.Strength, 0, 1);
        return builder.Build();
    }

    private static float Smoothstep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static float[] Rgb(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return [((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f];
    }
}
