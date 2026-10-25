using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// DEC-067: the approved blueprint mockup (StyleMockupDump.Blueprint.cs, variant 4 «Топографическая синька»). A
/// surveyor's blueprint: Prussian blue sheet, faintly mottled; a coordinate grid; the mountain drawn by form lines
/// (contours of its abstracted values, a pixel wide at any zoom), bold crest lines and silhouette; the coast and depth
/// lines following it in the sea; rivers by their banks. Grid, noise and depth lines are in document units.
/// </summary>
internal static class BlueprintShader
{
    private const string Source = """
uniform shader scene;
uniform shader mbig;
uniform shader features;
uniform shader geo;
uniform float2 origin;
uniform float2 docPerPx;
uniform float strength;
uniform float grid;
uniform float formLines;
uniform float crests;
uniform float depthLines;
uniform float mottling;
uniform float3 cPaper; uniform float3 cPaper2; uniform float3 cInk;

float3 unpre(float4 c) { return c.a > 0.0001 ? c.rgb / c.a : float3(0); }
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

half4 paintPixel(float2 pixel) {
    float2 p = origin + pixel * docPerPx;
    float px = max(docPerPx.x, docPerPx.y);
    float4 g = geo.eval(pixel);
    float jit = (fbm(p / 16.0) - 0.5) * 3.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    float4 mb = mbig.eval(pixel);
    float massA = smoothstep(0.38, 0.6, mb.a);
    // The program's crest field is drawn per asset and is grainier than the mockup's: keep only its firm crests.
    float crest = smoothstep(0.35, 0.8, unpre(features.eval(pixel)).r) * smoothstep(0.5, 0.7, mb.a);
    float edge = smoothstep(0.32, 0.45, mb.a) * (1.0 - smoothstep(0.45, 0.6, mb.a));
    // Lines stay about a screen pixel wide: distances in document units are divided by the size of a pixel.
    float coast = 1.0 - smoothstep(0.5, 1.6, max(abs(dL), abs(dW)) * 0.5 / max(px, 1.0) + 0.4 * noise(p / 5.0));
    float bank = smoothstep(0.08, 0.3, river) * (1.0 - smoothstep(0.35, 0.65, river));

    float mottle = fbm(p / 140.0) - 0.5;
    float fibre = noise(p / 1.3) - 0.5;
    float3 col = mix(cPaper, cPaper2, clamp(0.5 + mottle * 1.2 * mottling, 0.0, 1.0));
    col *= 1.0 + (mottle * 0.1 + fibre * 0.03) * mottling;

    float2 gp = abs(fract(p / 128.0 + 0.5) - 0.5) * 128.0 / px;
    float lines = (1.0 - smoothstep(0.4, 1.0, min(gp.x, gp.y))) * 0.45;
    float2 gq = abs(fract(p / 32.0 + 0.5) - 0.5) * 32.0 / px;
    lines = max(lines, (1.0 - smoothstep(0.3, 0.8, min(gq.x, gq.y))) * 0.14);
    float ink = lines * grid * (1.0 - massA * 0.7);

    // Form lines: contours of the abstracted value, measured in screen pixels from its local slope.
    float v0 = toneOf(mb);
    float vx = toneOf(mbig.eval(pixel + float2(1.5, 0))) - toneOf(mbig.eval(pixel - float2(1.5, 0)));
    float vy = toneOf(mbig.eval(pixel + float2(0, 1.5))) - toneOf(mbig.eval(pixel - float2(0, 1.5)));
    float gradV = length(float2(vx, vy)) / 3.0;
    float levels = 5.0;
    float fv = fract(v0 * levels);
    float dpx = min(fv, 1.0 - fv) / max(gradV * levels, 1e-4);
    // A flat plateau sitting exactly on a level is no line: contours only where the value really slopes.
    float iso = (1.0 - smoothstep(0.5, 1.2, dpx)) * smoothstep(0.45, 0.6, mb.a) * smoothstep(0.004, 0.012, gradV / px);
    float index = step(0.5, fract(floor(v0 * levels + 0.5) / 2.0));
    ink = max(ink, iso * mix(0.6, 0.95, index) * formLines);
    ink = max(ink, max(crest, edge) * 0.95 * crests);
    ink = max(ink, coast);

    float d = max(dL, 0.0) / 18.0; float fd = fract(d);
    float depth = (1.0 - smoothstep(0.04 * max(px, 1.0), 0.09 * max(px, 1.0), min(fd, 1.0 - fd))) * step(0.5, d)
        * (1.0 - smoothstep(4.0, 9.0, d)) * (1.0 - landF) * step(0.2, noise(p / 40.0 + floor(d)));
    ink = max(ink, depth * 0.55 * depthLines);
    ink = max(ink, bank * 0.9);
    col = mix(col, cInk, clamp(ink, 0.0, 1.0) * 0.9);
    col += (hash(floor(p)) - 0.5) * 0.008;
    float4 src = scene.eval(pixel);
    float3 original = src.rgb + cPaper * (1.0 - src.a);
    return half4(half3(mix(original, clamp(col, 0.0, 1.0), strength)), 1.0);
}

half4 main(float2 pixel) {
    if (max(docPerPx.x, docPerPx.y) > 1.001) {
        return (paintPixel(pixel + float2(-0.25, -0.25)) + paintPixel(pixel + float2(0.25, -0.25))
              + paintPixel(pixel + float2(-0.25, 0.25)) + paintPixel(pixel + float2(0.25, 0.25))) * 0.25;
    }
    return paintPixel(pixel);
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() => PainterFormMaps.Compile(Source));

    internal static bool Applies(MapStylePreset preset) => preset is MapStylePreset.BlueprintSurvey;

    public static SKShader Build(SKImage scene, SKImage wetBig, SKImage wetFeatures, SKImage geo,
        SKPoint origin, SKPoint docPerPx, MapStyle style)
    {
        var builder = new SKRuntimeShaderBuilder(Effect.Value);
        var children = new List<SKShader>();
        void Child(string name, SKImage image)
        {
            var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
            children.Add(shader); builder.Children[name] = shader;
        }
        Child("scene", scene); Child("mbig", wetBig); Child("features", wetFeatures); Child("geo", geo);
        float Amount(StyleEffectKind kind) => (float)style.Effect(kind).Effective;
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["docPerPx"] = new[] { docPerPx.X, docPerPx.Y };
        builder.Uniforms["strength"] = (float)style.Strength;
        builder.Uniforms["grid"] = Amount(StyleEffectKind.Grid);
        builder.Uniforms["formLines"] = Amount(StyleEffectKind.FormLines);
        builder.Uniforms["crests"] = Amount(StyleEffectKind.Crest);
        builder.Uniforms["depthLines"] = Amount(StyleEffectKind.WaterLines);
        builder.Uniforms["mottling"] = Amount(StyleEffectKind.Paper);
        builder.Uniforms["cPaper"] = new[] { 0x21 / 255f, 0x53 / 255f, 0x8f / 255f };
        builder.Uniforms["cPaper2"] = new[] { 0x1a / 255f, 0x44 / 255f, 0x79 / 255f };
        builder.Uniforms["cInk"] = new[] { 0xe6 / 255f, 0xf0 / 255f, 0xfa / 255f };
        var result = builder.Build();
        foreach (var child in children) child.Dispose();
        return result;
    }
}
