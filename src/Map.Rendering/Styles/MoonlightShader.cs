using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// DEC-068: the approved «Лунная ночь» mockups (StyleMockupDump.Moonlight.cs, variants 3 «Ночная гравюра Хасуи, индиго
/// и бокаси» and 5 «Пастель на тёмной бумаге»). The moon is a cold light from the north-west: the lit facets of the owner's
/// painting take it, the shadow sides sink into the night. Hasui cuts the mountain into three flat indigo steps under a
/// dark key block and grades the sea away from the shore (bokashi); the pastel lays the light in chalk along the planes
/// on the tooth of a dark blue sheet. Mountain marks are spaced in the asset's brush scale, every procedural sample is in
/// document coordinates.
/// </summary>
internal static class MoonlightShader
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
uniform float variant;
uniform float spacing;
uniform float keyBlock;
uniform float grainAmt;
uniform float bokashi;
uniform float moonPath;
uniform float chalk;
uniform float tooth;
uniform float shore;
uniform float3 cGround; uniform float3 cSea; uniform float3 cShadow; uniform float3 cLit; uniform float3 cGlint;

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

half4 paintPixel(float2 pixel) {
    float2 p = origin + pixel * docPerPx;
    float4 g = geo.eval(pixel);
    float jit = (fbm(p / 16.0) - 0.5) * 3.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    // The brush scale is stored premultiplied: read it where the map is most solid nearby (as the charcoal does).
    float4 dm = direction.eval(pixel);
    for (int k = 0; k < 4; k++) {
        float2 o = float2(k == 0 ? 12.0 : (k == 1 ? -12.0 : 0.0), k == 2 ? 12.0 : (k == 3 ? -12.0 : 0.0));
        float4 dk = direction.eval(raster(p + o));
        if (dk.a > dm.a) dm = dk;
    }
    float ms = dm.a > 0.2 ? clamp(unpre(dm).b, 0.2, 5.0) : 1.0;
    float2 q = p / ms;
    float4 mb = mbig.eval(pixel);
    float4 mf = mfacet.eval(pixel);
    float massA = smoothstep(0.38, 0.6, mb.a + 0.1 * (fbm(q / 9.0) - 0.5));
    float crisp = clamp(mix(toneOf(mb), toneOf(mf), 0.6), 0.0, 1.0);
    // Moonlight on the form: the painting's own light taken a little harder, as a single cold source.
    float light = 1.0 - crisp;
    float crest = smoothstep(0.35, 0.8, unpre(features.eval(pixel)).r) * smoothstep(0.5, 0.7, mb.a);
    float edge = smoothstep(0.32, 0.45, mb.a) * (1.0 - smoothstep(0.45, 0.6, mb.a));
    float coast = 1.0 - smoothstep(0.5, 1.6, max(abs(dL), abs(dW)) * 0.5 + 0.4 * noise(p / 5.0));
    float bank = smoothstep(0.08, 0.3, river) * (1.0 - smoothstep(0.35, 0.65, river));
    float sea = 1.0 - landF;
    float deep = smoothstep(10.0, 220.0, dL);

    // One path of moonlight across the sea (the moon in the north-west): short level dashes in a broad soft band.
    float band = 1.0 - smoothstep(0.03, 0.12, abs(fract((p.x * 0.55 + p.y) / 2600.0 + 0.2) - 0.5));
    float dash = step(0.86, noise(float2(p.x / (14.0 * ms), p.y / (2.2 * ms))));
    float glint = dash * band * sea * smoothstep(6.0, 30.0, dL) * (0.6 + 0.4 * noise(p / 60.0)) * moonPath;

    // Water lines along the shore.
    float gap = 7.0;
    float s = max(dL, 0.0) / gap; float kk = floor(s); float fs = fract(s);
    float wl = (1.0 - smoothstep(0.06, 0.13, min(fs, 1.0 - fs))) * step(0.5, s) * (1.0 - smoothstep(2.5, 4.5, s))
        * step(0.25, noise(p / (gap * 3.5) + kk * 7.0)) * sea * (0.9 - 0.15 * kk) * moonPath;

    // The chalk is laid in patches anchored at their centres, each along the form there.
    float cell = spacing * 6.0;
    float2 gc = floor(p / cell);
    float best = -1.0; float2 ax = normalize(float2(0.5, -0.86)); float2 anchor = (gc + 0.5) * cell;
    if (variant > 4.5 && mb.a > 0.2) {
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
                    if (abs(u) < 1.0 + (noise(float2(v * 2.5, pri * 31.0)) - 0.5) * 0.3 && abs(v) < 1.0) { best = pri; ax = al; anchor = c; }
                }
            }
        }
    }
    if (ax.x < 0.0) ax = -ax;
    float2 lp = p - anchor;
    float aseed = hash(anchor * 0.013) * 50.0;
    float mottle = fbm(p / 140.0) - 0.5;
    float3 col;

    if (variant < 4.5) {
        // Hasui: three flat indigo steps, bokashi of the sea, a dark key block, the grain of the block.
        float grain = (noise(float2(p.x / 2.0, p.y / 40.0)) - 0.5) * grainAmt;
        float3 water = mix(mix(cGround, cSea, 0.4), mix(mix(cGround, cSea, 0.4), cSea * 0.75, bokashi), smoothstep(0.0, 260.0, dL));
        float3 ground = mix(cGround, cGround * 0.82, smoothstep(0.0, 300.0, dW));
        col = mix(water, ground, landF) * (1.0 + grain * 0.05);
        float step3 = light < 0.38 ? 0.0 : (light < 0.7 ? 0.5 : 1.0);
        float3 mtn = mix(cShadow, cLit, step3);
        col = mix(col, mtn * (1.0 + grain * 0.04), massA);
        float key = max(max(edge, crest * 0.9), coast * 0.95) * keyBlock;
        col = mix(col, cShadow * 0.6, clamp(key, 0.0, 1.0) * 0.9);
        col = mix(col, cGlint, glint * 0.8);
        col = mix(col, cLit, wl * 0.35);
        col = mix(col, cShadow * 0.7, bank * 0.8);
    } else {
        // Pastel: the paper shows through the tooth; chalk laid along the planes.
        float t = mix(0.5, 0.55 * noise(p / 0.9) + 0.45 * fbm(p / 2.6), tooth);
        float laid = noise(float2(p.x / 1.1, p.y / 6.0)) - 0.5;
        float3 paper = mix(cGround, cSea, sea * (0.4 + 0.6 * deep)) * (1.0 + mottle * 0.08 + laid * 0.04 * tooth);
        col = paper;
        float stroke = mix(0.5, noise(float2(dot(lp, ax) / (22.0 * ms), dot(lp, float2(-ax.y, ax.x)) / (3.0 * ms)) + aseed), chalk);
        float lit = smoothstep(0.55, 0.8, light + (stroke - 0.5) * 0.25) * massA;
        float turning = smoothstep(0.3, 0.5, light + (stroke - 0.5) * 0.25) * (1.0 - lit) * massA;
        float catchT = smoothstep(0.35, 0.7, t + 0.25);
        float3 midChalk = mix(cGround, cLit, 0.38);
        col = mix(col, midChalk, turning * catchT * 0.85);
        col = mix(col, cLit, lit * catchT * 0.95);
        col = mix(col, cShadow * 0.75, smoothstep(0.0, 0.3, 1.0 - light) * massA * 0.35);
        // The land scumbled lightly, the shore a chalk line, a little chalk dust of moonlight on the water.
        float scumble = landF * (1.0 - massA) * smoothstep(0.62, 0.85, t + 0.15 * noise(p / 30.0)) * 0.22;
        col = mix(col, midChalk, scumble);
        col = mix(col, cLit, clamp((coast * 0.75 * catchT + edge * light * 0.35 * catchT) * shore, 0.0, 1.0));
        col = mix(col, cGlint, glint * catchT * 0.7 + wl * 0.25 * catchT);
        col = mix(col, cShadow * 0.7, bank * 0.6);
    }
    col += (hash(floor(p)) - 0.5) * 0.008;
    float4 src = scene.eval(pixel);
    float3 original = src.rgb + cGround * (1.0 - src.a);
    return half4(half3(mix(original, clamp(col, 0.0, 1.0), strength)), 1.0);
}

half4 main(float2 pixel) {
    // Zoomed out, the chalk and the grain are finer than a pixel: integrate them as the mockups' 50 % images do.
    if (max(docPerPx.x, docPerPx.y) > 1.001) {
        return (paintPixel(pixel + float2(-0.25, -0.25)) + paintPixel(pixel + float2(0.25, -0.25))
              + paintPixel(pixel + float2(-0.25, 0.25)) + paintPixel(pixel + float2(0.25, 0.25))) * 0.25;
    }
    return paintPixel(pixel);
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() => PainterFormMaps.Compile(Source));

    internal static bool Applies(MapStylePreset preset) => preset is MapStylePreset.MoonlightHasui or MapStylePreset.MoonlightPastel;

    /// <summary>The mockup's palette of a variant: night ground, sea, the shadow and lit colour of the mountains, the moon's mark.</summary>
    private sealed record Look(float Variant, float Spacing, string Ground, string Sea, string Shadow, string Lit, string Glint);

    private static Look LookOf(MapStylePreset preset) => preset == MapStylePreset.MoonlightPastel
        ? new(5, 8f, "#22324d", "#1b2740", "#1b2740", "#d3deec", "#e9eff7")
        : new(3, 7f, "#2c3d6b", "#1a2550", "#0f1734", "#9fb3d9", "#e6ecf7");

    public static SKShader Build(SKImage scene, SKImage wetBig, SKImage facets, SKImage wetFeatures,
        SKImage direction, SKImage geo, SKPoint origin, SKPoint docPerPx, MapStyle style)
    {
        var look = LookOf(style.Preset);
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
        builder.Uniforms["variant"] = look.Variant;
        builder.Uniforms["spacing"] = look.Spacing;
        builder.Uniforms["keyBlock"] = Amount(StyleEffectKind.Crest);
        builder.Uniforms["grainAmt"] = Amount(StyleEffectKind.WoodGrain);
        builder.Uniforms["bokashi"] = Amount(StyleEffectKind.Sea);
        builder.Uniforms["moonPath"] = Amount(StyleEffectKind.WaterLines);
        builder.Uniforms["chalk"] = Amount(StyleEffectKind.Strokes);
        builder.Uniforms["tooth"] = Amount(StyleEffectKind.Paper);
        builder.Uniforms["shore"] = Amount(StyleEffectKind.Coast);
        builder.Uniforms["cGround"] = Hex(look.Ground); builder.Uniforms["cSea"] = Hex(look.Sea);
        builder.Uniforms["cShadow"] = Hex(look.Shadow); builder.Uniforms["cLit"] = Hex(look.Lit);
        builder.Uniforms["cGlint"] = Hex(look.Glint);
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
