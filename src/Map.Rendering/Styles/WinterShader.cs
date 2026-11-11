using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// «Зима» (owner's choice of 2026-11-11 from the page «Зимний стиль Map», variants 1, 2, 4; DEC-097). The map under
/// snow: the land is snow, the owner's mountains keep their own light and shadow redrawn in the medium, the sea is
/// frozen - fast ice along the shore with its cracks, floes thinning out into open water (drawn here from the coast
/// distance, whatever sea the map has). «Гравюра по снегу» cuts the tone in cold blue lines on bare paper, steeper on
/// the mountains, crossed in the dark, with one firm line round the mountains and the coast. «Акварель метели» lays three
/// cold washes with the dark rim of a dried wash, a drift of snow haze, flakes and wind streaks. «Японская гравюра зимой»
/// cuts three flat blocks under a key line, the sea darkening towards the top (bokashi), the wood grain, heavy flakes.
/// Every procedural sample is in document coordinates.
/// </summary>
internal static class WinterShader
{
    internal const string Source = """
uniform shader scene;
uniform shader mbig;
uniform shader geo;
uniform float2 origin;
uniform float2 docPerPx;
uniform float2 docSize;
uniform float strength;
uniform float variant;
uniform float pen;
uniform float crossAmt;
uniform float contour;
uniform float ice;
uniform float paperAmt;
uniform float rim;
uniform float granule;
uniform float haze;
uniform float snow;
uniform float keyBlock;
uniform float grainAmt;
uniform float bokashi;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

// Coverage of a line of relative width w (0..1 of the spacing) at a position measured in spacings.
float lineCover(float proj, float w, float spacing, float px) {
    float d = abs(fract(proj) - 0.5) * spacing;
    return clamp((w * spacing * 0.5 - d) / px + 0.5, 0.0, 1.0);
}

// Falling snow: one flake per cell, of random size; returns its coverage.
float flakes(float2 p, float cellSize, float rMin, float rMax, float px) {
    float2 g = floor(p / cellSize);
    float best = 0.0;
    for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
            float2 c = g + float2(float(i), float(j));
            float2 f = (c + float2(hash(c + 5.0), hash(c + 9.0))) * cellSize;
            float r = rMin + (rMax - rMin) * pow(hash(c + 2.0), 2.2);
            float a = 0.55 + 0.45 * hash(c + 31.0);
            best = max(best, a * clamp((r - length(p - f)) / max(px, 0.6) + 0.5, 0.0, 1.0));
        }
    }
    return best;
}

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
    float4 src = scene.eval(pixel);
    float3 onWhite = src.rgb + (1.0 - src.a);
    // The owner's mountain: its light sides are the snow, its dark ridges the shadow.
    float peak = clamp((0.92 - lum(onWhite)) * 1.25, 0.0, 1.0);
    // The snowfield: a few soft drifts; the river stays open water.
    float drift = smoothstep(0.62, 0.82, fbm(p / 90.0)) * 0.16;
    // The frozen sea is the program's own «Лёд» (the style draws the map with it): its ice light, the open water dark,
    // softened over a few document pixels so the floes read as drifts of ice in the medium.
    float seaLum = 0.0;
    for (int k = 0; k < 5; k++) {
        float2 o = k == 0 ? float2(0) : float2(k == 1 ? 3.0 : (k == 2 ? -3.0 : 0.0), k == 3 ? 3.0 : (k == 4 ? -3.0 : 0.0));
        float4 s = scene.eval(pixel + o / docPerPx);
        seaLum += lum(s.rgb + (1.0 - s.a)) * 0.2;
    }
    float sea = mix(0.62, clamp((0.95 - seaLum) * 1.1, 0.0, 1.0) * 0.8, ice);
    float ground = mix(sea, drift, landF);
    ground = max(ground, river * 0.85);
    float t = mix(ground, peak, massA);
    float edge = smoothstep(0.32, 0.45, mb.a) * (1.0 - smoothstep(0.45, 0.6, mb.a));
    float coast = (1.0 - smoothstep(0.4, 1.4, max(abs(dL), abs(dW)) * 0.5)) * (1.0 - massA);
    float outline = max(edge, coast);
    float3 col;

    if (variant < 1.5) {
        // Гравюра по снегу.
        float3 paper = float3(0.965, 0.958, 0.94) * (1.0 - 0.025 * paperAmt * noise(p / 1.4));
        float3 ink = float3(0.12, 0.17, 0.30);
        float spacing = 3.5;
        float a = mix(-0.55, 1.05, massA);
        float wob = (fbm(p / 60.0) - 0.5) * 1.6;
        float c1 = lineCover(dot(p, float2(cos(a), sin(a))) / spacing + wob, clamp((t - 0.05) * 1.05, 0.0, 0.9) * pen, spacing, px);
        float b = a + 1.25;
        float c2 = t > 0.5 ? lineCover(dot(p, float2(cos(b), sin(b))) / spacing + wob * 0.75, (t - 0.5) * 1.5 * crossAmt, spacing, px) : 0.0;
        float cover = max(max(c1, c2), smoothstep(0.15, 0.5, outline) * contour);
        col = mix(paper, ink, cover * 0.94);
    } else if (variant < 2.5) {
        // Акварель метели.
        float3 paper = float3(0.975, 0.975, 0.975) - 0.02 * paperAmt * noise(p / 3.0);
        float3 pigment = float3(0.42, 0.52, 0.68);
        float v = t;
        float w = (smoothstep(0.08, 0.14, v) + smoothstep(0.32, 0.38, v) + smoothstep(0.60, 0.66, v)) / 3.0;
        float r = (1.0 - smoothstep(0.0, 0.07, v - 0.14)) * step(0.11, v)
            + (1.0 - smoothstep(0.0, 0.07, v - 0.38)) * step(0.35, v)
            + (1.0 - smoothstep(0.0, 0.07, v - 0.66)) * step(0.63, v);
        float amount = clamp(w + r * 0.12 * rim, 0.0, 1.0) * (1.0 + (noise(p / 2.6) - 0.5) * 0.28 * granule);
        float detail = mix(1.0, 0.62 + 0.45 * lum(onWhite), massA);
        col = paper * (1.0 - amount * (1.0 - pigment)) * detail;
        float fog = smoothstep(0.45, 0.85, fbm(p / 320.0)) * 0.42 * haze;
        col = mix(col, float3(0.985), fog);
        float fl = flakes(p, 14.0, 0.5, 2.1, px) * (150.0 / 255.0);
        float2 sc = floor(p / 60.0);
        float2 so = (sc + float2(hash(sc + 41.0), hash(sc + 43.0))) * 60.0;
        float2 dir = normalize(float2(1.0, 0.32));
        float along = dot(p - so, dir); float across = abs(dot(p - so, float2(-dir.y, dir.x)));
        float len = 13.0 + 27.0 * hash(sc + 47.0);
        float streak = step(0.0, along) * step(along, len) * clamp((0.6 - across) / max(px, 0.6) + 0.5, 0.0, 1.0)
            * (0.15 + 0.28 * hash(sc + 53.0));
        col = mix(col, float3(1.0), clamp(max(fl, streak) * snow, 0.0, 1.0));
    } else {
        // Японская гравюра зимой.
        float value = 1.0 - t;
        if (landF < 0.5 && massA < 0.1) {
            float sky = mix(1.0, 0.78 + 0.22 * smoothstep(0.0, docSize.y * 0.55, p.y), bokashi);
            col = value > 0.66 ? float3(0.84, 0.87, 0.88) * sky : float3(0.34, 0.40, 0.47) * sky;
        } else {
            col = value > 0.70 ? float3(0.94, 0.94, 0.92) : (value > 0.44 ? float3(0.64, 0.68, 0.75) : float3(0.30, 0.34, 0.43));
        }
        float wood = 1.0 + ((noise(float2(p.x / 40.0, p.y / 2.0)) - 0.5) * 0.05 + (noise(p / 1.2) - 0.5) * 0.015) * grainAmt;
        col = clamp(col * wood, 0.0, 1.0);
        col = mix(col, float3(0.2), smoothstep(0.22, 0.45, outline) * keyBlock * 0.9);
        col = mix(col, float3(1.0), flakes(p, 34.0, 1.5, 4.4, px) * 0.92 * snow);
    }
    float3 original = onWhite;
    return half4(half3(mix(original, clamp(col, 0.0, 1.0), strength)), 1.0);
}

half4 main(float2 pixel) {
    // Zoomed out, the lines and the grain are finer than a pixel: four samples, as the moonlight does.
    bool fine = max(docPerPx.x, docPerPx.y) > 1.001;
    half4 sum = half4(0);
    for (int i = 0; i < 4; i++) {
        if (i > 0 && !fine) { break; }
        float2 o = fine ? float2(i == 0 || i == 2 ? -0.25 : 0.25, i < 2 ? -0.25 : 0.25) : float2(0);
        sum += paintPixel(pixel + o);
    }
    return fine ? sum * 0.25 : sum;
}
""";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MapDocument, MapDocument> FrozenDocuments = [];

    /// <summary>The map as the winter styles paint it: the same map, its sea drawn as the program's «Лёд».</summary>
    internal static MapDocument Frozen(MapDocument bare) => FrozenDocuments.GetValue(bare, static d =>
        d.Layers.Count > 0 && d.Layers[0] is BaseColorLayer page
            ? d.WithLayers([page with { Sea = SeaStyle.For(SeaManner.Ice) }, .. d.Layers.Skip(1)]) with { Style = d.Style, Lighting = d.Lighting }
            : d);

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() => PainterFormMaps.Compile(Source));

    internal static bool Applies(MapStylePreset preset) =>
        preset is MapStylePreset.WinterEngraving or MapStylePreset.WinterBlizzard or MapStylePreset.WinterWoodblock;

    /// <summary>
    /// The cliff walls and roads drawn over the painted frame (multiplied) take the medium's ink instead of their own
    /// colours, and only where they are dark: the ground's material under a raised plateau stays snow.
    /// </summary>
    internal static SKColorFilter OverlayInk(MapStylePreset preset)
    {
        float[] ink = preset == MapStylePreset.WinterWoodblock ? [.2f, .2f, .2f]
            : preset == MapStylePreset.WinterBlizzard ? [.42f, .52f, .68f] : [.12f, .17f, .30f];
        const float threshold = .5f, slope = 2.2f;
        var m = new float[20];
        for (var row = 0; row < 3; row++)
        {
            // out = 1 - slope (1 - ink) (threshold - lum), clamped: white above the threshold, the ink in the dark.
            var k = slope * (1 - ink[row]);
            m[row * 5 + 0] = k * .299f; m[row * 5 + 1] = k * .587f; m[row * 5 + 2] = k * .114f;
            m[row * 5 + 4] = 1 - k * threshold;
        }
        m[18] = 1;
        return SKColorFilter.CreateColorMatrix(m);
    }

    public static SKShader Build(SKImage scene, SKImage wetBig, SKImage geo, SKPoint origin, SKPoint docPerPx, SKSize docSize, MapStyle style)
    {
        var builder = new SKRuntimeShaderBuilder(Effect.Value);
        var children = new List<SKShader>();
        void Child(string name, SKImage image)
        {
            var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
            children.Add(shader); builder.Children[name] = shader;
        }
        Child("scene", scene); Child("mbig", wetBig); Child("geo", geo);
        float Amount(StyleEffectKind kind) => (float)style.Effect(kind).Effective;
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["docPerPx"] = new[] { docPerPx.X, docPerPx.Y };
        builder.Uniforms["docSize"] = new[] { docSize.Width, docSize.Height };
        builder.Uniforms["strength"] = (float)style.Strength;
        builder.Uniforms["variant"] = style.Preset switch
        {
            MapStylePreset.WinterBlizzard => 2f,
            MapStylePreset.WinterWoodblock => 3f,
            _ => 1f,
        };
        builder.Uniforms["pen"] = Amount(StyleEffectKind.Pen);
        builder.Uniforms["crossAmt"] = Amount(StyleEffectKind.CrossHatch);
        builder.Uniforms["contour"] = Amount(StyleEffectKind.Contour);
        builder.Uniforms["ice"] = Amount(StyleEffectKind.Sea);
        builder.Uniforms["paperAmt"] = Amount(StyleEffectKind.Paper);
        builder.Uniforms["rim"] = Amount(StyleEffectKind.Edges);
        builder.Uniforms["granule"] = Amount(StyleEffectKind.Granulation);
        builder.Uniforms["haze"] = Amount(StyleEffectKind.Mist);
        builder.Uniforms["snow"] = Amount(StyleEffectKind.Strokes);
        builder.Uniforms["keyBlock"] = Amount(StyleEffectKind.Crest);
        builder.Uniforms["grainAmt"] = Amount(StyleEffectKind.WoodGrain);
        builder.Uniforms["bokashi"] = Amount(StyleEffectKind.Coast);
        var result = builder.Build();
        foreach (var child in children) child.Dispose();
        return result;
    }
}
