using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// «Пиксельный атлас» (owner's choice of 2026-11-11 from the page «Пиксельный атлас Map», variants 1 and 2; DEC-098). The
/// map rebuilt on a grid of big pixels in document space (one cell is 1/400 of the canvas's long side, at least 4 document
/// pixels, so the whole map always reads as pixel art). Each cell takes the average colour of the painted map under it.
/// «Фэнтези, 16 цветов» maps it to a sixteen-colour fantasy palette with ordered (Bayer) dithering - light on the sea -,
/// outlines the land and every mountain in the darkest colour and scatters small wave glyphs on the open sea. «Карманная
/// консоль» takes four greens by luminance with a two-by-two checker between them and the darkest green as the outline.
/// </summary>
internal static class PixelShader
{
    internal const string Source = """
uniform shader scene;
uniform shader mbig;
uniform shader geo;
uniform float2 origin;
uniform float2 docPerPx;
uniform float cell;
uniform float strength;
uniform float variant;
uniform float outlineAmt;
uniform float ditherAmt;
uniform float wavesAmt;

float2 raster(float2 p) { return (p - origin) / docPerPx; }
float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float bayer2(float2 a) { a = floor(a); return fract(dot(a, float2(0.5, a.y * 0.75))); }
float bayer4(float2 a) { return bayer2(0.5 * a) * 0.25 + bayer2(a); }

// The sixteen colours of «Фэнтези».
float3 pal(int i) {
    if (i == 0) return float3(0.102, 0.110, 0.173); if (i == 1) return float3(0.365, 0.153, 0.365);
    if (i == 2) return float3(0.694, 0.243, 0.325); if (i == 3) return float3(0.937, 0.490, 0.341);
    if (i == 4) return float3(1.000, 0.804, 0.459); if (i == 5) return float3(0.655, 0.941, 0.439);
    if (i == 6) return float3(0.220, 0.718, 0.392); if (i == 7) return float3(0.145, 0.443, 0.475);
    if (i == 8) return float3(0.161, 0.212, 0.435); if (i == 9) return float3(0.231, 0.365, 0.788);
    if (i == 10) return float3(0.255, 0.651, 0.965); if (i == 11) return float3(0.451, 0.937, 0.969);
    if (i == 12) return float3(0.957, 0.957, 0.957); if (i == 13) return float3(0.580, 0.690, 0.761);
    if (i == 14) return float3(0.337, 0.424, 0.525); return float3(0.200, 0.235, 0.341);
}

float3 nearest(float3 c) {
    float3 best = pal(0); float bestD = 1e9;
    for (int i = 0; i < 16; i++) {
        float3 d = c - pal(i);
        float e = 2.0 * d.r * d.r + 4.0 * d.g * d.g + 3.0 * d.b * d.b;
        if (e < bestD) { bestD = e; best = pal(i); }
    }
    return best;
}

// What one cell holds: its average colour (four samples), whether it is land, whether it is mountain, its shore distance.
float3 cellColour(float2 c) {
    float3 sum = float3(0);
    for (int k = 0; k < 4; k++) {
        float2 o = float2(k == 0 || k == 2 ? 0.25 : 0.75, k < 2 ? 0.25 : 0.75);
        float4 s = scene.eval(raster((c + o) * cell));
        sum += s.rgb + (1.0 - s.a);
    }
    return sum * 0.25;
}
float isLand(float2 c) { float4 g = geo.eval(raster((c + 0.5) * cell)); return g.g * 255.0 - g.r * 255.0 > 0.0 ? 1.0 : 0.0; }
float isMountain(float2 c) { return mbig.eval(raster((c + 0.5) * cell)).a > 0.5 ? 1.0 : 0.0; }
float shoreCells(float2 c) { return geo.eval(raster((c + 0.5) * cell)).r * 255.0 / cell; }

float waveAt(float2 o) { return shoreCells(o) >= 4.0 && hash(o + 0.37) < 0.035 * wavesAmt ? 1.0 : 0.0; }

half4 main(float2 pixel) {
    float2 p = origin + pixel * docPerPx;
    float2 c = floor(p / cell);
    float3 col = cellColour(c);
    float land = isLand(c);
    float mtn = isMountain(c);
    float solid = max(land, mtn);
    // A cell on the border of the land (not under a mountain) or of a mountain is outlined.
    float coastEdge = land * (1.0 - mtn) * (1.0 - min(min(isLand(c + float2(1, 0)), isLand(c - float2(1, 0))), min(isLand(c + float2(0, 1)), isLand(c - float2(0, 1)))));
    float mtnEdge = mtn * (1.0 - min(min(isMountain(c + float2(1, 0)), isMountain(c - float2(1, 0))), min(isMountain(c + float2(0, 1)), isMountain(c - float2(0, 1)))));
    float edge = max(coastEdge, mtnEdge) * outlineAmt;
    float3 outCol;
    if (variant < 1.5) {
        // Фэнтези, 16 цветов.
        float spread = (solid > 0.5 ? 48.0 : 14.0) / 255.0 * ditherAmt;
        float t = (bayer4(c) - 0.5) * spread;
        outCol = nearest(col + t);
        float wave = (1.0 - solid) * max(max(waveAt(c), waveAt(c - float2(1, -1))), waveAt(c - float2(2, 0)));
        outCol = mix(outCol, pal(11), wave);
        outCol = mix(outCol, pal(0), edge);
    } else {
        // Карманная консоль: four greens.
        float l = lum(col) * 3.0 + (mod(c.x + c.y, 2.0) < 1.0 ? 0.25 : -0.25) * ditherAmt;
        float k = clamp(floor(l + 0.5), 0.0, 3.0);
        float3 g0 = float3(0.059, 0.220, 0.059); float3 g1 = float3(0.188, 0.384, 0.188);
        float3 g2 = float3(0.545, 0.675, 0.059); float3 g3 = float3(0.608, 0.737, 0.059);
        outCol = k < 0.5 ? g0 : (k < 1.5 ? g1 : (k < 2.5 ? g2 : g3));
        outCol = mix(outCol, g0, edge);
    }
    float4 src = scene.eval(pixel);
    float3 original = src.rgb + (1.0 - src.a);
    return half4(half3(mix(original, outCol, strength)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() => PainterFormMaps.Compile(Source));

    internal static bool Applies(MapStylePreset preset) => preset is MapStylePreset.PixelFantasy16 or MapStylePreset.PixelHandheld;

    /// <summary>One pixel of the style in document pixels: 1/400 of the canvas's long side, at least 4.</summary>
    internal static float CellSize(SKSize document) => MathF.Max(4, MathF.Round(MathF.Max(document.Width, document.Height) / 400));

    public static SKShader Build(SKImage scene, SKImage wetBig, SKImage geo, SKPoint origin, SKPoint docPerPx, SKSize docSize, MapStyle style)
    {
        var builder = new SKRuntimeShaderBuilder(Effect.Value);
        var children = new List<SKShader>();
        void Child(string name, SKImage image)
        {
            // Nearest sampling: a cell reads exact values, never a blend across the shore.
            var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Nearest));
            children.Add(shader); builder.Children[name] = shader;
        }
        Child("scene", scene); Child("mbig", wetBig); Child("geo", geo);
        float Amount(StyleEffectKind kind) => (float)style.Effect(kind).Effective;
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["docPerPx"] = new[] { docPerPx.X, docPerPx.Y };
        builder.Uniforms["cell"] = CellSize(docSize);
        builder.Uniforms["strength"] = (float)style.Strength;
        builder.Uniforms["variant"] = style.Preset == MapStylePreset.PixelHandheld ? 2f : 1f;
        builder.Uniforms["outlineAmt"] = Amount(StyleEffectKind.Contour);
        builder.Uniforms["ditherAmt"] = Amount(StyleEffectKind.Granulation);
        builder.Uniforms["wavesAmt"] = Amount(StyleEffectKind.WaterLines);
        var result = builder.Build();
        foreach (var child in children) child.Dispose();
        return result;
    }
}
