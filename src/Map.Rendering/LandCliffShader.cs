using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Draws the wall of a cliff from its <see cref="LandCliffField"/>, on the GPU or the CPU alike
/// (Skia runtime effect). Every pixel reads its depth below the edge, the number of steps and the
/// way the edge faces, and builds the wall the way the owner's tier sheets draw it:
/// <list type="bullet">
/// <item>the wall is split into faces along the edge - where the edge turns, and every face width
/// along a straight run - and each face has one tone by the way it faces the light (upper left);</item>
/// <item>ink lines stand on the corners between faces, along the foot and up the sides where a wall
/// ends in a gap, and the rim of the land is inked along the top;</item>
/// <item>the faces carry pen hatching (slanted strokes hanging from the rim), stippled dots thickest
/// under the rim, a wash darkening towards the rim or the foot, and strata;</item>
/// <item>the height rises and falls along the edge, the foot is ragged, may carry hanging ticks, and
/// ends in ink, in a fade, or plainly; a soft shadow lies on the ground below it.</item>
/// </list>
/// All patterns are fixed to the world, so nothing crawls while panning or zooming.
/// </summary>
public static class LandCliffShader
{
    private const string Source = """
uniform shader field;
uniform float px;
uniform float stepHeight;
uniform float heightVariation;
uniform float variationLength;
uniform float gaps;
uniform float gapLength;
uniform float lean;
uniform float facets;
uniform float facetWidth;
uniform float facetLines;
uniform float4 lightColor;
uniform float4 shadeColor;
uniform float4 inkColor;
uniform float4 rimColor;
uniform float4 shadowColor;
uniform float contrast;
uniform float wash;
uniform float rimShadow;
uniform float hatching;
uniform float hatchSpacing;
uniform float hatchLength;
uniform float hatchSlant;
uniform float stipple;
uniform float stippleSize;
uniform float strata;
uniform float inkWidth;
uniform float rimWidth;
uniform float foot;
uniform float footRoughness;
uniform float footTicks;
uniform float baseShadow;
uniform float shadowLength;

float hash1(float n) { return fract(sin(mod(n, 997.0) * 12.9898 + 4.1414) * 43758.5453); }
float hash2(float2 p) { p = mod(p, 997.0); return fract(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }
float noise1(float x) {
    float i = floor(x); float f = x - i; float u = f * f * (3.0 - 2.0 * f);
    return mix(hash1(i), hash1(i + 1.0), u);
}
float noise2(float2 x) {
    float2 i = floor(x); float2 f = x - i; float2 u = f * f * (3.0 - 2.0 * f);
    float a = hash2(i); float b = hash2(i + float2(1.0, 0.0));
    float c = hash2(i + float2(0.0, 1.0)); float e = hash2(i + float2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, e, u.x), u.y);
}
// Coverage of a line of half-width w (world units) at signed distance d, antialiased to a pixel.
float line(float d, float w) { return clamp(w - abs(d) + 0.5 * px, 0.0, px) / px; }

half4 main(float2 p) {
    float4 f = field.eval(p);
    float d = f.r;
    float steps = floor(f.g + 0.5);
    float th = f.b;
    if (steps < 0.5 || d <= 0.0 || d > 50000.0) { return half4(0.0); }

    // The point of the edge this pixel hangs from, and the position along that edge.
    float ox = p.x - lean * d;
    float oy = p.y - d;
    // Along the edge the wall is seen from the south: the x of the edge point is a smooth measure
    // of position on every edge that has a visible wall (a north-south edge shows only a sliver).
    float s = ox;

    // Gaps: stretches of the edge with no wall. They are read from the edge point itself (x and
    // y), so two tiers one above the other do not break at the same x. Towards the end of a wall
    // it tapers to a point, as the sheets draw it, and its side is inked.
    float2 edge = float2(ox, oy);
    float g = noise2(edge / gapLength + float2(17.31, 5.17));
    float slope = abs(noise2((edge + float2(1.0, 0.0)) / gapLength + float2(17.31, 5.17)) - g) + 1e-5;
    float side = (g - gaps) / slope;                  // world units from the end of the wall
    if (gaps > 0.001 && side < 0.0) { return half4(0.0); }
    float taper = gaps > 0.001 ? smoothstep(0.0, stepHeight * 1.6, side) : 1.0;

    // Height of this stretch: rises and falls along the edge, and ragged at the foot.
    float swell = noise2(edge / variationLength + float2(3.7, 11.3));
    float local = stepHeight * max(0.25, 1.0 + heightVariation * (1.6 * swell * swell - 0.55));
    float waves = noise1(s / max(2.0, facetWidth * 0.55) + 9.1);
    float teeth = abs(fract(s / max(2.0, facetWidth * 0.42) + 0.5 * noise1(s / 31.0)) * 2.0 - 1.0);
    float ragged = 1.0 - footRoughness * 0.32 * (0.55 * waves + 0.45 * teeth * teeth);
    float h = steps * local * ragged * taper;

    if (d > h) {
        float below = d - h;
        float4 outCol = float4(0.0);
        // Ticks and roots hanging under the foot (pen hairs).
        if (footTicks > 0.001) {
            float cell = floor(s / max(1.5, facetWidth * 0.22));
            float at = (cell + 0.2 + 0.6 * hash1(cell * 3.1)) * max(1.5, facetWidth * 0.22);
            float len = stepHeight * 0.32 * hash1(cell * 7.7) * footTicks;
            if (hash1(cell * 1.9) < footTicks && below < len) {
                float a = line(s - at, inkWidth * 0.5 * (1.0 - below / max(len, 1e-3)));
                outCol = float4(inkColor.rgb, 1.0) * a * inkColor.a;
            }
        }
        if (baseShadow > 0.001 && shadowLength > 0.0 && below < shadowLength) {
            float a = baseShadow * pow(1.0 - below / shadowLength, 1.6) * shadowColor.a;
            outCol = outCol + float4(shadowColor.rgb, 1.0) * a * (1.0 - outCol.a);
        }
        return half4(outCol);
    }
    float t = d / max(h, 1e-3);

    // Faces: the turn of the edge in steps of 30 degrees, and a face every facetWidth along it.
    float binned = floor(th / 0.5236 + 0.5) * 0.5236;
    float facing = mix(th, binned, facets);
    float lit = clamp(0.55 - 0.5 * sin(facing) + 0.1 * cos(facing), 0.0, 1.0);
    float fs = s / facetWidth + 0.45 * noise1(s / (facetWidth * 3.0) + 5.3);
    float fid = floor(fs);
    float within = fs - fid;
    lit = clamp(lit + facets * 0.34 * (hash1(fid * 1.37) - 0.5), 0.0, 1.0);
    // Inside a face, the corner side lies in the shade of the next face.
    lit = lit - facets * 0.12 * smoothstep(0.55, 1.0, within);

    float dark = (1.0 - lit) * contrast;
    if (wash < 0.0) { dark += -wash * pow(1.0 - t, 1.3); } else { dark += wash * pow(t, 1.2); }
    float rimBand = stepHeight * 0.22;
    if (d < rimBand) { dark += rimShadow * (1.0 - d / rimBand); }
    float3 col = mix(lightColor.rgb, shadeColor.rgb, clamp(dark, 0.0, 1.0));

    float ink = 0.0;
    // Corners between faces, from the rim down a share of the wall.
    if (facetLines > 0.001) {
        float reach = mix(0.55, 1.0, hash1(fid * 2.71)) * h;
        float distance = min(within, 1.0 - within) * facetWidth;
        float corner = line(distance, inkWidth * 0.75) * step(d, reach);
        float4 g2 = field.eval(p + float2(px, 0.0));
        float turned = step(0.3, abs(floor(g2.b / 0.5236 + 0.5) * 0.5236 - binned)) * step(0.5, g2.g) * step(0.0001, g2.r);
        ink = max(ink, facetLines * max(corner, turned * facets));
    }
    // Ledges between the steps of a wall more than one level high.
    if (steps > 1.5) {
        float k = floor(d / (local * ragged) + 0.5);
        if (k >= 1.0 && k < steps) { ink = max(ink, line(d - k * local * ragged, inkWidth * 0.55)); }
    }
    // Strata: thin bands across each step.
    if (strata > 0.5) {
        float band = d / (local / (strata + 1.0)) + 0.35 * noise1(s / 40.0 + fid);
        float seam = (band - floor(band + 0.5)) * local / (strata + 1.0);
        ink = max(ink, 0.35 * line(seam, inkWidth * 0.35) * (0.5 + 0.5 * noise1(s / 25.0 + band)));
    }
    // Pen hatching: slanted strokes hanging from the rim, thicker and longer near a corner.
    if (hatching > 0.001) {
        float u = (s - d * hatchSlant) / hatchSpacing;
        float stroke = floor(u);
        float fr = u - stroke;
        float near = 1.0 - within;
        float wanted = hatching * mix(0.55, 1.0, near);
        float len = hatchLength * h * mix(0.35, 1.0, hash1(stroke * 4.13)) * mix(0.7, 1.0, near);
        if (hash1(stroke * 9.7) < wanted && d < len) {
            float width = inkWidth * 0.5 * (1.0 - 0.6 * d / max(len, 1e-3));
            ink = max(ink, hatching * line((fr - 0.5) * hatchSpacing, width));
        }
    }
    // Stippling: dots on a jittered grid, thick under the rim and thinning out downwards.
    if (stipple > 0.001) {
        float cellSize = stippleSize * 1.9;
        float2 q = float2(s, d) / cellSize;
        float2 cell = floor(q);
        float2 centre = cell + 0.2 + 0.6 * float2(hash2(cell), hash2(cell + 31.7));
        float distance = length(q - centre) * cellSize;
        float chance = stipple * mix(1.0, 0.3, pow(t, 0.7));
        if (hash2(cell + 7.3) < chance) { ink = max(ink, line(distance, stippleSize * 0.45)); }
    }
    // The side of a wall where it ends in a gap.
    if (gaps > 0.001) { ink = max(ink, line(side, inkWidth * 0.5) * facetLines); }
    // The foot: an ink line, a fade, or a plain end.
    float alpha = clamp((h - d) / px + 0.5, 0.0, 1.0);
    if (foot < 0.5) { ink = max(ink, line(h - d - inkWidth * 0.5, inkWidth * 0.5)); }
    else if (foot < 1.5) { float fade = smoothstep(0.4, 1.0, t); alpha *= 1.0 - fade; ink *= 1.0 - 0.6 * fade; }

    col = mix(col, inkColor.rgb, clamp(ink, 0.0, 1.0) * inkColor.a);
    // The rim of the land along the top of the wall, in the land's own outline.
    if (rimWidth > 0.0 && d < rimWidth) { col = mix(col, rimColor.rgb, rimColor.a); alpha = max(alpha, rimColor.a); }
    float a = alpha * lightColor.a;
    // Below the wall's own fade, the shadow at its foot shows through.
    float shade = 0.0;
    if (foot > 0.5 && foot < 1.5 && baseShadow > 0.001) { shade = baseShadow * shadowColor.a * smoothstep(0.55, 1.0, t); }
    float4 outCol = float4(col, 1.0) * a + float4(shadowColor.rgb, 1.0) * shade * (1.0 - a);
    return half4(outCol);
}
""";

    private static readonly Lazy<SKRuntimeEffect?> Effect = new(() =>
    {
        var effect = SKRuntimeEffect.CreateShader(Source, out var errors);
        if (effect is null) LastError = errors;
        return effect;
    });

    /// <summary>The compiler message when the shader did not build (then no cliff is drawn).</summary>
    public static string? LastError { get; private set; }

    public static bool Available => Effect.Value is not null;

    /// <summary>
    /// The wall shader for one field. <paramref name="pixel"/> is the size of one device pixel in
    /// world units, for antialiasing; the rim is the land's own outline.
    /// </summary>
    internal static SKShader? Create(SKShader field, LandCliffStyle style, string rimColor, double rimWidth, double pixel)
    {
        if (Effect.Value is not { } effect) return null;
        var builder = new SKRuntimeShaderBuilder(effect);
        builder.Children["field"] = field;
        builder.Uniforms["px"] = (float)pixel;
        builder.Uniforms["stepHeight"] = (float)style.Height;
        builder.Uniforms["heightVariation"] = (float)style.HeightVariation;
        builder.Uniforms["variationLength"] = (float)style.VariationLength;
        builder.Uniforms["gaps"] = (float)style.Gaps;
        builder.Uniforms["gapLength"] = (float)style.GapLength;
        builder.Uniforms["lean"] = (float)style.Lean;
        builder.Uniforms["facets"] = (float)style.Facets;
        builder.Uniforms["facetWidth"] = (float)style.FacetWidth;
        builder.Uniforms["facetLines"] = (float)style.FacetLines;
        builder.Uniforms["lightColor"] = Color(style.Color);
        builder.Uniforms["shadeColor"] = Color(style.ShadeColor);
        builder.Uniforms["inkColor"] = Color(style.InkColor);
        builder.Uniforms["rimColor"] = Color(rimColor);
        builder.Uniforms["shadowColor"] = Color(style.BaseShadowColor);
        builder.Uniforms["contrast"] = (float)style.Contrast;
        builder.Uniforms["wash"] = (float)style.Wash;
        builder.Uniforms["rimShadow"] = (float)style.RimShadow;
        builder.Uniforms["hatching"] = (float)style.Hatching;
        builder.Uniforms["hatchSpacing"] = (float)style.HatchSpacing;
        builder.Uniforms["hatchLength"] = (float)style.HatchLength;
        builder.Uniforms["hatchSlant"] = (float)style.HatchSlant;
        builder.Uniforms["stipple"] = (float)style.Stipple;
        builder.Uniforms["stippleSize"] = (float)style.StippleSize;
        builder.Uniforms["strata"] = (float)style.Strata;
        builder.Uniforms["inkWidth"] = (float)Math.Max(style.InkWidth, pixel * 0.6);
        builder.Uniforms["rimWidth"] = (float)rimWidth;
        builder.Uniforms["foot"] = (float)(int)style.Foot;
        builder.Uniforms["footRoughness"] = (float)style.FootRoughness;
        builder.Uniforms["footTicks"] = (float)style.FootTicks;
        builder.Uniforms["baseShadow"] = (float)style.BaseShadow;
        builder.Uniforms["shadowLength"] = (float)(style.Height * style.BaseShadowLength);
        return builder.Build();
    }

    private static float[] Color(string value)
    {
        var color = SKColor.TryParse(value, out var parsed) ? parsed : SKColors.Gray;
        return [color.Red / 255f, color.Green / 255f, color.Blue / 255f, color.Alpha / 255f];
    }
}
