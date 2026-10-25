using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// DEC-065: the approved oil mockups (StyleMockupDump.Oil.cs, variants 1 «Алла прима» and 5 «Ритм импасто»). Strokes are
/// placed coarse to fine on a grid of cells, each oriented along the form (the asset's direction map) or along the shore
/// (the coast field), coloured once at its centre and lit through its own paint profile. Mountain strokes are sized in
/// the asset's brush scale, sea and land strokes in document units; every procedural sample is in document coordinates.
/// </summary>
internal static class OilShader
{
    private const string Source = """
uniform shader scene;
uniform shader mbig;
uniform shader mfacet;
uniform shader mwide;
uniform shader features;
uniform shader direction;
uniform shader geo;
uniform float2 origin;
uniform float2 docPerPx;
uniform float variant;
uniform float strength;
uniform float3 mlay0;
uniform float3 mlay1;
uniform float3 mlay2;
uniform float2 glay;
uniform float relief;
uniform float shine;
uniform float groundShow;
uniform float weave;
uniform float crests;
uniform float3 cLight; uniform float3 cMid; uniform float3 cShadow; uniform float3 cDeep;
uniform float3 cLight2; uniform float3 cMid2; uniform float3 cShadow2; uniform float3 cDeep2;
uniform float3 cSeaNear; uniform float3 cSeaDeep; uniform float3 cLandCoast; uniform float3 cLandInland;
uniform float3 cRiver; uniform float3 cGround; uniform float3 cContour;

float2 raster(float2 p) { return (p - origin) / docPerPx; }
float3 unpre(float4 c) { return c.a > 0.0001 ? c.rgb / c.a : float3(0); }
float4 bigAt(float2 p) { return mbig.eval(raster(p)); }
float4 facetAt(float2 p) { return mfacet.eval(raster(p)); }
float4 geoAt(float2 p) { return geo.eval(raster(p)); }
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

float3 mountainPaint(float t, float alt, float up) {
    float3 a = mix(cLight, cLight2, alt);
    float3 b = mix(cMid, cMid2, alt);
    float3 c = mix(cShadow, cShadow2, alt);
    float3 d = mix(cDeep, cDeep2, alt);
    float3 col = t < 0.24 ? a : (t < 0.5 ? b : (t < 0.76 ? c : d));
    return mix(col, cLandInland, (1.0 - smoothstep(0.15, 0.55, up)) * 0.12);
}

float3 groundPaint(float2 c, float alt) {
    float4 g = geoAt(c);
    float dL = g.r * 255.0;
    float dW = g.g * 255.0;
    float landF = step(dL, dW);
    float3 sea = mix(cSeaNear, cSeaDeep, 0.25 + 0.6 * smoothstep(8.0, 160.0, dL));
    float3 land = mix(cLandCoast, cLandInland, smoothstep(8.0, 160.0, dW));
    return mix(sea, land, landF) * (1.0 + (alt - 0.5) * 0.07);
}

float2 axisAt(float2 c, float kind, float2 cid) {
    if (kind < 0.5) {
        float4 d = direction.eval(raster(c));
        float rnd = (hash(cid + 41.0) - 0.5) * 0.5 + 0.9;
        float2 fallback = float2(cos(rnd), -sin(rnd));
        if (d.a < 0.05) return fallback;
        float2 across = unpre(d).rg * 2.0 - 1.0;
        float2 along = float2(-across.y, across.x);
        return length(along) > 1e-3 ? normalize(along) : fallback;
    }
    float s0 = (geoAt(c).r - geoAt(c).g) * 255.0;
    float4 gx4 = geoAt(c + float2(4, 0)); float4 gy4 = geoAt(c + float2(0, 4));
    float2 grad = float2((gx4.r - gx4.g) * 255.0 - s0, (gy4.r - gy4.g) * 255.0 - s0) / 4.0;
    float m = length(grad);
    float2 n = m > 1e-3 ? grad / m : float2(0);
    float2 shore = m > 1e-3 ? float2(-n.y, n.x) : float2(1, 0);
    float2 level = float2(1.0, (hash(cid + 5.0) - 0.5) * 0.25);
    float near = clamp(m, 0.0, 1.0) * (variant > 4.5 ? 1.0 : 0.55);
    return normalize(mix(level, shore, near));
}

float shapeOf(float u, float v, float seed) {
    float au = abs(u); float av = abs(v);
    float body = step(av, 1.0 - 0.25 * u * u);
    float bristle = noise(float2(v * 3.0 + seed * 13.0, seed * 7.0));
    float tail = step(au, 1.0) * step((au - 0.8) * 5.0, bristle);
    return body * tail;
}

float heightOf(float u, float v, float seed) {
    float au = abs(u); float av = abs(v);
    float body = 1.0 - 0.45 * v * v;
    float ed = (av - 0.8) / 0.16;
    float edges = 0.35 * exp(-ed * ed);
    float load = mix(1.2, 0.65, (u + 1.0) * 0.5);
    float ends = 1.0 - smoothstep(0.55, 1.0, au);
    float grooves = noise(float2(v * 2.6 + seed * 13.0, u * 0.5 + seed));
    return (body + edges) * load * ends * (0.85 + 0.3 * grooves);
}

void strokeLayer(float2 p, float len, float wid, float lid, float kind, float detail,
                 inout float found, inout float2 sc, inout float2 sax, inout float su, inout float sv, inout float sseed,
                 inout float slf) {
    float cell = len * 0.5;
    float2 g = floor(p / cell);
    float best = -1.0;
    for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
            float2 cid = g + float2(float(i), float(j)) + lid * 101.0;
            float2 c = (g + float2(float(i), float(j)) + 0.5 + (float2(hash(cid), hash(cid + 11.0)) - 0.5) * 0.9) * cell;
            float pri = hash(cid + 19.7);
            if (pri > best) {
                float inside = bigAt(c).a;
                float ok = kind < 0.5 ? step(0.42, inside) : 1.0 - step(0.5, inside);
                if (ok > 0.5 && detail > 0.0) {
                    float diff = abs(toneOf(facetAt(c)) - toneOf(bigAt(c)));
                    // The program's crest field is the watercolour one (a lower threshold than the oil mockup's edge),
                    // and its facets are a little sharper: both weighed down to place as many fine strokes as the mockup.
                    float edge = unpre(features.eval(raster(c))).r;
                    ok = step(detail, max(diff * 0.8, edge * 0.15));
                }
                if (ok > 0.5) {
                    float2 ax = axisAt(c, kind, cid);
                    float lf = 0.7 + 0.4 * hash(cid + 3.0);
                    float ja = (hash(cid + 29.0) - 0.5) * (kind < 0.5 ? 0.22 : 0.35) * 2.0;
                    ax = float2(ax.x * cos(ja) - ax.y * sin(ja), ax.x * sin(ja) + ax.y * cos(ja));
                    float2 d = p - c;
                    float u = dot(d, ax) / (len * lf * 0.5);
                    float v = dot(d, float2(-ax.y, ax.x)) / (wid * 0.5);
                    v += (noise(float2(u * 2.0, pri * 31.0)) - 0.5) * 0.25;
                    if (shapeOf(u, v, pri) > 0.5) { best = pri; sc = c; sax = ax; su = u; sv = v; sseed = pri; slf = lf; }
                }
            }
        }
    }
    if (best >= 0.0) found = 1.0;
}

half4 paintPixel(float2 pixel) {
    float2 p = origin + pixel * docPerPx;
    float4 g = geo.eval(pixel);
    float jit = (fbm(p / 16.0) - 0.5) * 4.0;
    float dL = g.r * 255.0 + jit;
    float dW = g.g * 255.0 - jit;
    float landF = smoothstep(-0.8, 0.8, dW - dL);
    float river = g.b * smoothstep(0.5, 3.0, g.g * 255.0);
    float4 dp = direction.eval(pixel);
    float ms = dp.a > 0.001 && unpre(dp).b > 0.001 ? unpre(dp).b : 1.0;
    float2 q = p / ms;
    float4 mb = bigAt(p);
    float4 mw = mwide.eval(pixel);
    float3 form = unpre(features.eval(pixel));
    float massA = smoothstep(0.38, 0.6, mb.a + 0.14 * (fbm(q / 9.0) - 0.5));
    float tBig = toneOf(mb);
    float up = form.g;
    float crest = form.r;

    float3 under = mix(groundPaint(p, 0.5), mountainPaint(tBig, 0.0, up), massA);
    float3 col = mix(under, cGround, groundShow * mix(0.25, 0.55, massA));
    float h = 0.0;
    float2 grad = float2(0);

    float found = 0.0; float2 sc = float2(0); float2 sax = float2(1, 0); float su = 0.0; float sv = 0.0; float seed = 0.0; float slf = 1.0;
    float kind = 1.0; float slen = glay.x; float swid = glay.y; float slayer = 0.0;
    if (mb.a < 0.92) {
        strokeLayer(p, glay.x, glay.y, 1.0, 1.0, 0.0, found, sc, sax, su, sv, seed, slf);
        float f2 = 0.0; float2 c2 = sc; float2 a2 = sax; float u2 = su; float v2 = sv; float s2 = seed; float l2 = slf;
        strokeLayer(p, glay.x * 0.6, glay.y * 0.7, 2.0, 1.0, 0.0, f2, c2, a2, u2, v2, s2, l2);
        if (f2 > 0.5 && min(g.r, g.g) * 255.0 < 40.0) { found = 1.0; sc = c2; sax = a2; su = u2; sv = v2; seed = s2; slf = l2; slen = glay.x * 0.6; swid = glay.y * 0.7; }
    }
    if (mw.a > 0.02) {
        float lens[3]; float wids[3]; float dets[3];
        lens[0] = mlay0.x * ms; wids[0] = mlay0.y * ms; dets[0] = 0.0;
        lens[1] = mlay1.x * ms; wids[1] = mlay1.y * ms; dets[1] = 0.06;
        lens[2] = mlay2.x * ms; wids[2] = mlay2.y * ms; dets[2] = 0.14;
        for (int k = 0; k < 3; k++) {
            float fk = 0.0; float2 ck = float2(0); float2 ak = float2(1, 0); float uk = 0.0; float vk = 0.0; float sk = 0.0; float lk = 1.0;
            strokeLayer(p, lens[k], wids[k], 10.0 + float(k), 0.0, dets[k], fk, ck, ak, uk, vk, sk, lk);
            float clipA = mb.a + 0.16 * (noise(q / 3.0 + sk * 9.0) - 0.5);
            if (fk > 0.5 && clipA > 0.4) { found = 1.0; kind = 0.0; sc = ck; sax = ak; su = uk; sv = vk; seed = sk; slf = lk; slen = lens[k]; swid = wids[k]; slayer = float(k); }
        }
    }
    slen *= slf;
    if (found > 0.5 && kind > 0.5) {
        float4 gc = geoAt(sc);
        if (abs(step(gc.r, gc.g) - landF) > 0.5) found = 0.0;
    }

    if (found > 0.5) {
        float alt = variant > 4.5 ? step(0.5, hash(sc * 0.71)) : hash(sc * 0.71) * 0.5;
        float3 paint;
        if (kind < 0.5) {
            float4 cb = bigAt(sc); float4 cf = facetAt(sc);
            float t = mix(toneOf(cb), toneOf(cf), 0.25 + 0.3 * slayer) + (hash(sc * 0.37) - 0.5) * 0.08;
            float upc = unpre(features.eval(raster(sc))).g;
            paint = mountainPaint(t, alt, upc);
            if (variant < 1.5) {
                float2 st = sc - sax * slen * 0.4;
                float ts = mix(toneOf(bigAt(st)), toneOf(facetAt(st)), 0.6);
                float3 from = mountainPaint(ts, alt, upc);
                paint = mix(paint, from, (1.0 - smoothstep(-1.0, 0.2, su)) * 0.5);
            }
        } else {
            paint = groundPaint(sc, alt);
        }
        col = paint;
        float e = 0.03;
        float h0 = heightOf(su, sv, seed);
        float hu = (heightOf(su + e, sv, seed) - h0) / e;
        float hv = (heightOf(su, sv + e, seed) - h0) / e;
        float2 perp = float2(-sax.y, sax.x);
        float px = 1.4 * ms;
        grad = (sax * hu / (slen * 0.5) + perp * hv / (swid * 0.5)) * px * (kind < 0.5 ? 1.0 : 0.5);
        h = h0;
    }

    col = mix(col, cRiver * (0.94 + 0.12 * noise(p / 3.0)), river * 0.85);
    if (variant < 1.5) col = mix(col, cContour, crest * step(0.55, noise(p / 18.0)) * 0.5 * crests);

    float3 N = normalize(float3(-grad * relief, 1.0));
    float3 L = normalize(float3(-0.55, -0.65, 0.55));
    col *= 1.0 + 0.6 * (dot(N, L) - L.z);
    float3 R = reflect(-L, N);
    col += pow(max(R.z, 0.0), 30.0) * shine * smoothstep(0.2, 0.8, h) * 0.5;
    float wv = 0.5 + 0.25 * (sin(p.x * 1.9) + sin(p.y * 1.9));
    col *= 1.0 - weave * 0.08 * (wv - 0.5) * (1.0 - smoothstep(0.0, 0.6, h));
    col += (hash(floor(p)) - 0.5) * 0.008;
    float4 src = scene.eval(pixel);
    float3 original = src.rgb + cGround * (1.0 - src.a);
    return half4(half3(mix(original, clamp(col, 0.0, 1.0), strength)), 1.0);
}

// One sample per pixel: the stroke search is too large for SkSL to inline four times (the painter shader's 2x2
// filter when zoomed out), and oil strokes stay wider than a pixel even at 50 %.
half4 main(float2 pixel) { return paintPixel(pixel); }
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() => PainterFormMaps.Compile(Source));

    internal static bool Applies(MapStylePreset preset) => preset is MapStylePreset.OilAllaPrima or MapStylePreset.OilImpastoRhythm;

    private sealed record Look(float Variant, float[] Mountain, float[] Ground, float Relief, float Shine, float GroundShow,
        float Weave, string[] Colours);

    // Light, Mid, Shadow, Deep, Light2, Mid2, Shadow2, Deep2, SeaNear, SeaDeep, LandCoast, LandInland, River, Ground, Contour.
    private static readonly string[] ColourNames =
        ["cLight", "cMid", "cShadow", "cDeep", "cLight2", "cMid2", "cShadow2", "cDeep2",
         "cSeaNear", "cSeaDeep", "cLandCoast", "cLandInland", "cRiver", "cGround", "cContour"];

    private static Look LookOf(MapStylePreset preset) => preset == MapStylePreset.OilImpastoRhythm
        ? new(5, [26, 7.5f, 17, 5.5f, 11, 4], [30, 8], 0.7f, 0.35f, 0.9f, 0f,
            ["#f7e7b0", "#9cc3d6", "#4f78b8", "#2b3c7c", "#e8f1ee", "#b7cba0", "#3f8f9a", "#4a3a7a",
             "#7fc0c8", "#2f5e9c", "#d8b860", "#b4b65c", "#9fd0d0", "#6b7890", "#22305a"])
        : new(1, [42, 15, 26, 9, 14, 5], [70, 24], 0.6f, 0.25f, 0.55f, 0.25f,
            ["#f6ead6", "#b9c4d6", "#6f80a8", "#3d4572", "#f2e2cc", "#aebbd2", "#7a7ca6", "#45436e",
             "#76aab2", "#2b5078", "#d8c49c", "#b5a26e", "#86b6c0", "#a8603a", "#3a3f66"]);

    public static SKShader Build(SKImage scene, SKImage wetBig, SKImage facets, SKImage wide, SKImage wetFeatures,
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
        Child("scene", scene); Child("mbig", wetBig); Child("mfacet", facets); Child("mwide", wide);
        Child("features", wetFeatures); Child("direction", direction); Child("geo", geo);
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["docPerPx"] = new[] { docPerPx.X, docPerPx.Y };
        builder.Uniforms["variant"] = look.Variant;
        builder.Uniforms["strength"] = (float)style.Strength;
        builder.Uniforms["mlay0"] = new[] { look.Mountain[0], look.Mountain[1], 0f };
        builder.Uniforms["mlay1"] = new[] { look.Mountain[2], look.Mountain[3], 0f };
        builder.Uniforms["mlay2"] = new[] { look.Mountain[4], look.Mountain[5], 0f };
        builder.Uniforms["glay"] = look.Ground;
        float Amount(StyleEffectKind kind) => (float)style.Effect(kind).Effective;
        builder.Uniforms["relief"] = look.Relief * Amount(StyleEffectKind.Impasto);
        builder.Uniforms["shine"] = look.Shine * Amount(StyleEffectKind.Gloss);
        builder.Uniforms["groundShow"] = look.GroundShow * Amount(StyleEffectKind.Imprimatura);
        builder.Uniforms["weave"] = look.Weave * Amount(StyleEffectKind.Paper);
        builder.Uniforms["crests"] = Amount(StyleEffectKind.Crest);
        for (var i = 0; i < ColourNames.Length; i++) builder.Uniforms[ColourNames[i]] = Hex(look.Colours[i]);
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
