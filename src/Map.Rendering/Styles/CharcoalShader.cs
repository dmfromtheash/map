using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// DEC-066: the approved charcoal mockups (StyleMockupDump.Charcoal.cs, variants 1 «Ивовый уголь и растушёвка» and
/// 2 «Прессованный уголь, штриховка»). Charcoal sits on the tooth of the paper: a light pressure catches only its peaks,
/// a heavy one fills the valleys; the stump rubs the masses into soft tone, the hatch follows the form (the asset's
/// direction map) in up to three layers by value, the sea is hatched level beyond water lines that follow the shore.
/// Mountain marks are spaced in the asset's brush scale, the ground's in document units; every procedural sample is in
/// document coordinates.
/// </summary>
internal static class CharcoalShader
{
    private const string Source = """
uniform shader scene;
uniform shader mbig;
uniform shader mfacet;
uniform shader features;
uniform shader direction;
uniform shader geo;
uniform float2 origin;
uniform float2 docPerPx;
uniform float strength;
uniform float spacing;
uniform float smudge;
uniform float hatchAmt;
uniform float crossAmt;
uniform float contour;
uniform float waterLines;
uniform float grain;
uniform float3 ground;   // sea near, sea deep, land
uniform float3 layers;
uniform float3 cPaper; uniform float3 cDark;

float2 raster(float2 p) { return (p - origin) / docPerPx; }
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

float tooth(float2 p) { return clamp(mix(0.5, 0.55 * noise(p / 0.9) + 0.45 * fbm(p / 2.6), grain), 0.0, 1.0); }
float onTooth(float pressure, float t) { return smoothstep(1.0 - pressure - 0.18, 1.0 - pressure + 0.18, t); }
float2 turn(float2 a, float r) { return float2(a.x * cos(r) - a.y * sin(r), a.x * sin(r) + a.y * cos(r)); }

float hatch(float2 p, float2 ax, float sp, float width, float seed) {
    float2 pr = float2(-ax.y, ax.x);
    float s = dot(p, pr) / sp + (noise(p / (sp * 9.0) + seed) - 0.5) * 0.7;
    float row = floor(s);
    float a = dot(p, ax) / (sp * 7.0) + hash(float2(row, seed)) * 13.0;
    float seg = floor(a); float fa = fract(a);
    float on = step(0.2, hash(float2(row * 1.7 + seed, seg)));
    float taper = smoothstep(0.0, 0.15, fa) * (1.0 - smoothstep(0.75, 1.0, fa));
    float d = abs(fract(s) - 0.5) * 2.0;
    float w = width * (0.45 + 0.55 * taper);
    return on * (1.0 - smoothstep(w * 0.55, w, d));
}

half4 paintPixel(float2 pixel) {
    float2 p = origin + pixel * docPerPx;
    float4 g = geo.eval(pixel);
    float jit = (fbm(p / 16.0) - 0.5) * 3.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    float4 dp = direction.eval(pixel);
    // The brush scale is stored premultiplied: at the faint edge of a mountain it divides two tiny 8-bit numbers and
    // comes out as rubbish, and a rubbish spacing turns the hatch into grit. Read it where the map is most solid nearby.
    float4 dm = dp;
    for (int k = 0; k < 4; k++) {
        float2 o = float2(k == 0 ? 12.0 : (k == 1 ? -12.0 : 0.0), k == 2 ? 12.0 : (k == 3 ? -12.0 : 0.0));
        float4 dk = direction.eval(raster(p + o));
        if (dk.a > dm.a) dm = dk;
    }
    float ms = dm.a > 0.2 ? clamp(unpre(dm).b, 0.2, 5.0) : 1.0;
    float2 q = p / ms;
    float4 mb = mbig.eval(pixel);
    float4 mf = mfacet.eval(pixel);
    float massA = smoothstep(0.38, 0.6, mb.a + 0.12 * (fbm(q / 9.0) - 0.5));
    float tBig = toneOf(mb);
    float tFacet = toneOf(mf);
    // The program's crest field is drawn per asset and is grainier than the mockup's: keep only its firm crests.
    float crest = smoothstep(0.35, 0.8, unpre(features.eval(pixel)).r) * smoothstep(0.5, 0.7, mb.a);
    float t = tooth(p);

    float seaT = mix(ground.x, ground.y, smoothstep(6.0, 180.0, dL));
    float landT = ground.z + 0.12 * (1.0 - smoothstep(3.0, 50.0, dW));
    float gT = mix(seaT, landT, landF);
    float soft = mix(gT, tBig, massA);
    float crisp = mix(gT, clamp(mix(tBig, tFacet, 0.6), 0.0, 1.0), massA);

    // The mountain is hatched along its masses; the ground level, as a draughtsman lays a sea.
    // One spacing per mode, never blended: a spacing that changes from pixel to pixel shifts the rows just the same.
    float onMountain = step(0.2, mb.a);
    float sp = onMountain > 0.5 ? spacing * ms : spacing * 1.25;
    // A hatch line's place is measured from an anchor: from the whole map a tiny turn of the direction would shift the
    // lines by whole rows and break them into grit. So the mountain is hatched as a draughtsman does it - in overlapping
    // patches, each with straight parallel lines in the direction of the form at its centre, measured from that centre.
    float2 ax = normalize(float2(1.0, 0.07));
    float2 anchor = float2(0);
    if (onMountain > 0.5) {
        // The grid of patches is fixed in document units and each patch takes the brush scale at its own centre, so
        // neither the grid nor the spacing jumps between neighbouring pixels.
        float cell = spacing * 6.0;
        float2 gc = floor(p / cell);
        float best = -1.0;
        anchor = (gc + 0.5) * cell;
        ax = normalize(float2(0.5, -0.86));
        for (int j = -1; j <= 1; j++) {
            for (int i = -1; i <= 1; i++) {
                float2 cid = gc + float2(float(i), float(j));
                float2 c = (cid + 0.5 + (float2(hash(cid), hash(cid + 11.0)) - 0.5) * 0.8) * cell;
                float pri = hash(cid + 19.7);
                if (pri > best) {
                    float4 d = direction.eval(raster(c));
                    float2 across = unpre(d).rg * 2.0 - 1.0;
                    float2 al = d.a > 0.05 && length(across) > 1e-3 ? normalize(float2(-across.y, across.x)) : normalize(float2(0.5, -0.86));
                    float2 dd = p - c;
                    float u = dot(dd, al) / (cell * 0.95);
                    float v = dot(dd, float2(-al.y, al.x)) / (cell * 0.8);
                    float ragged = (noise(float2(v * 2.5, pri * 31.0)) - 0.5) * 0.3;
                    if (abs(u) < 1.0 + ragged && abs(v) < 1.0) {
                        best = pri; ax = al; anchor = c;
                        sp = spacing * (d.a > 0.2 ? clamp(unpre(d).b, 0.2, 5.0) : ms);
                    }
                }
            }
        }
    }
    if (ax.x < 0.0) ax = -ax;

    float rub = soft * smudge;
    float dark = rub * mix(onTooth(rub, t), 1.0, 0.45);

    float2 lp = p - anchor;
    float aseed = hash(anchor * 0.013) * 50.0;
    float l1 = smoothstep(layers.x, layers.x + 0.12, crisp) * hatch(lp, ax, sp, 0.32 + 0.4 * crisp, 1.0 + aseed);
    float l2 = smoothstep(layers.y, layers.y + 0.12, crisp) * hatch(lp, turn(ax, 0.62), sp * 1.05, 0.3 + 0.4 * crisp, 2.0 + aseed);
    float l3 = smoothstep(layers.z, layers.z + 0.1, crisp) * hatch(lp, turn(ax, -0.55), sp * 0.95, 0.35 + 0.35 * crisp, 3.0 + aseed);
    float offshore = mix(smoothstep(22.0, 40.0, dL), 1.0, max(massA, landF));
    l1 *= offshore; l2 *= offshore; l3 *= offshore;
    float pressure = 0.55 + 0.45 * crisp;
    float lines = 1.0 - (1.0 - l1 * hatchAmt) * (1.0 - l2 * crossAmt) * (1.0 - l3 * crossAmt);
    lines *= mix(onTooth(pressure, t), 1.0, 0.3);
    dark = 1.0 - (1.0 - dark) * (1.0 - lines * 0.9);

    float edge = smoothstep(0.3, 0.45, mb.a) * (1.0 - smoothstep(0.45, 0.62, mb.a)) * step(0.3, noise(p / (11.0 * ms)));
    float coast = 1.0 - smoothstep(0.6, 2.0, max(abs(dL), abs(dW)) * 0.5 + 0.6 * noise(p / 5.0));
    float drawn = max(max(edge, crest * step(0.45, noise(p / (16.0 * ms))) * 0.8), coast * 0.8) * contour;
    dark = max(dark, drawn * onTooth(0.85, t) * 0.92);

    float gap = 7.0;
    float s = max(dL, 0.0) / gap;
    float k = floor(s);
    float fs = fract(s);
    float wl = (1.0 - smoothstep(0.07, 0.15, min(fs, 1.0 - fs))) * step(0.5, s) * (1.0 - smoothstep(2.5, 4.5, s))
        * step(0.28, noise(p / (gap * 3.5) + k * 7.0)) * (1.0 - landF);
    dark = 1.0 - (1.0 - dark) * (1.0 - wl * (0.75 - 0.12 * k) * onTooth(0.75, t) * waterLines);

    float3 col = mix(cPaper, cDark, clamp(dark, 0.0, 1.0));
    col = mix(col, cPaper, river * 0.85);
    float bank = smoothstep(0.08, 0.3, river) * (1.0 - smoothstep(0.35, 0.65, river));
    col = mix(col, cDark, bank * 0.7 * onTooth(0.8, t) * step(0.2, noise(p / 9.0)));
    col *= 1.0 - 0.035 * (0.5 - t) * grain;
    col += (hash(floor(p)) - 0.5) * 0.01;
    float4 src = scene.eval(pixel);
    float3 original = src.rgb + cPaper * (1.0 - src.a);
    return half4(half3(mix(original, clamp(col, 0.0, 1.0), strength)), 1.0);
}

half4 main(float2 pixel) {
    // Zoomed out, the hatch is finer than a pixel: integrate it as the mockups' 50 % images filter the 100 % drawing.
    if (max(docPerPx.x, docPerPx.y) > 1.001) {
        return (paintPixel(pixel + float2(-0.25, -0.25)) + paintPixel(pixel + float2(0.25, -0.25))
              + paintPixel(pixel + float2(-0.25, 0.25)) + paintPixel(pixel + float2(0.25, 0.25))) * 0.25;
    }
    return paintPixel(pixel);
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() => PainterFormMaps.Compile(Source));

    internal static bool Applies(MapStylePreset preset) => preset is MapStylePreset.CharcoalVineStump or MapStylePreset.CharcoalHatching;

    private sealed record Look(float Spacing, float Smudge, float Hatch, float Cross, float Contour, float Grain,
        float[] Ground, float[] Layers, string Paper, string Dark);

    private static Look LookOf(MapStylePreset preset) => preset == MapStylePreset.CharcoalHatching
        ? new(5.5f, 0f, 1f, 1f, 1f, 0.5f, [0.38f, 0.6f, 0.06f], [0.12f, 0.45f, 0.72f], "#f3efe6", "#1d1c1b")
        : new(7f, 0.85f, 0.35f, 0f, 0.55f, 0.8f, [0.42f, 0.62f, 0.05f], [0.2f, 0.55f, 0.8f], "#efeae0", "#2a2826");

    public static SKShader Build(SKImage scene, SKImage wetBig, SKImage facets, SKImage wetFeatures,
        SKImage direction, SKImage geo, SKPoint origin, SKPoint docPerPx, MapStyle style)
    {
        var look = LookOf(style.Preset);
        var hatching = style.Preset == MapStylePreset.CharcoalHatching;
        var builder = new SKRuntimeShaderBuilder(Effect.Value);
        var children = new List<SKShader>();
        void Child(string name, SKImage image)
        {
            var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
            children.Add(shader); builder.Children[name] = shader;
        }
        Child("scene", scene); Child("mbig", wetBig); Child("mfacet", facets);
        Child("features", wetFeatures); Child("direction", direction); Child("geo", geo);
        float Amount(StyleEffectKind kind) => (float)style.Effect(kind).Effective;
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["docPerPx"] = new[] { docPerPx.X, docPerPx.Y };
        builder.Uniforms["strength"] = (float)style.Strength;
        builder.Uniforms["spacing"] = look.Spacing;
        builder.Uniforms["smudge"] = look.Smudge * Amount(StyleEffectKind.Smudge);
        builder.Uniforms["hatchAmt"] = look.Hatch * Amount(StyleEffectKind.Strokes);
        builder.Uniforms["crossAmt"] = look.Cross * Amount(StyleEffectKind.CrossHatch);
        builder.Uniforms["contour"] = look.Contour * Amount(hatching ? StyleEffectKind.Contour : StyleEffectKind.Crest);
        builder.Uniforms["waterLines"] = Math.Max(look.Contour, 0.5f) * Amount(StyleEffectKind.WaterLines);
        builder.Uniforms["grain"] = look.Grain * Amount(StyleEffectKind.Paper);
        builder.Uniforms["ground"] = look.Ground;
        builder.Uniforms["layers"] = look.Layers;
        builder.Uniforms["cPaper"] = Hex(look.Paper);
        builder.Uniforms["cDark"] = Hex(look.Dark);
        var result = builder.Build();
        foreach (var child in children) child.Dispose();
        return result;
    }

    private static float[] Hex(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return [((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f];
    }
}
