using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// Approved formulas only: OldMapSource variant 1 and PrintSource variant 5 in StyleMockupDump.
/// Samples are in raster pixels; marks, noise, distances and page furniture are in document pixels.
/// </summary>
internal static class PrintShader
{
    private const string Source = """
uniform shader scene;
uniform shader mount;
uniform shader geo;
uniform shader tensor;
uniform shader height;
uniform shader mlines;
uniform shader mfaces;
uniform float2 origin;
uniform float2 docPerPx;
uniform float2 size;
uniform float variant;
uniform float strength;
uniform float pen;
uniform float contourStrength;
uniform float seaStrength;
uniform float coastStrength;
uniform float wash;
uniform float aquatint;
uniform float fibres;
uniform float plate;
uniform float border;
uniform float3 paper;
uniform float3 ink;

float2 raster(float2 q) { return (q - origin) / docPerPx; }
float4 mountain(float2 q) { return mount.eval(raster(q)); }
float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
float3 unpre(float4 c) { return c.a > 0.001 ? c.rgb / c.a : float3(1); }
float lumAt(float2 p) { float4 c = mountain(p); return lum(mix(float3(1), unpre(c), c.a)); }
float lineLum(float2 p) { float4 c = mlines.eval(raster(p)); return lum(mix(float3(1), unpre(c), c.a)); }
float hAt(float2 p) { return height.eval(raster(p)).r; }
float rimAt(float2 p, float r) {
    float inner = min(min(mountain(p + float2(r, 0)).a, mountain(p - float2(r, 0)).a),
                      min(mountain(p + float2(0, r)).a, mountain(p - float2(0, r)).a));
    return smoothstep(0.35, 0.65, mountain(p).a) * (1.0 - smoothstep(0.35, 0.65, inner));
}
float screen(float coord, float period, float tone, float aa) {
    float tri = abs(fract(coord / period) - 0.5) * 2.0;
    return 1.0 - smoothstep(tone - aa, tone + aa, tri);
}
float waterlines(float d, float s0, float a, float count, float width) {
    if (d <= 0.5) return 0.0;
    float k = (-1.0 + sqrt(1.0 + 4.0 * a * d / s0)) / (2.0 * a);
    float spacing = s0 * (1.0 + 2.0 * a * k);
    float ph = fract(k);
    float px = min(ph, 1.0 - ph) * spacing;
    float on = 1.0 - smoothstep(width, width + 0.9, px);
    return on * step(k, count + 0.5) * (1.0 - 0.6 * k / (count + 1.0));
}
float rectLine(float2 p, float inset, float width) {
    float2 d = abs(p - size * 0.5) - (size * 0.5 - inset);
    return 1.0 - smoothstep(width * 0.5 - 0.5, width * 0.5 + 0.5, abs(max(d.x, d.y)));
}
// DrawAtlasBorder: double neatline and alternate 64-pixel blocks, anchored to the document.
float atlasBorder(float2 p) {
    float k = max(rectLine(p, 64.0, 2.2), rectLine(p, 84.0, 1.4));
    float bx = step(84.0, p.x) * step(p.x, size.x - 84.0) * (1.0 - mod(floor((p.x - 84.0) / 64.0), 2.0));
    float by = step(84.0, p.y) * step(p.y, size.y - 84.0) * (1.0 - mod(floor((p.y - 84.0) / 64.0), 2.0));
    float horizontal = max(step(66.0, p.y) * step(p.y, 82.0), step(size.y - 82.0, p.y) * step(p.y, size.y - 66.0));
    float vertical = max(step(66.0, p.x) * step(p.x, 82.0), step(size.x - 82.0, p.x) * step(p.x, size.x - 66.0));
    return max(k, max(bx * horizontal, by * vertical) * (235.0 / 255.0));
}

half4 main(float2 pixel) {
    float2 p = origin + pixel * docPerPx;
    float4 src = scene.eval(pixel);
    float4 g = geo.eval(pixel);
    float dL = g.r * 255.0;
    float dW = g.g * 255.0;
    float isLand = step(0.5, dW);
    float river = g.b * smoothstep(0.5, 3.0, dW);
    float dc = max(dL, dW);
    float4 m = mountain(p);
    float ma = m.a;
    float lm = lum(unpre(m));
    float lo = lumAt(p);
    float around = 0.25 * (lumAt(p + float2(2, 0)) + lumAt(p - float2(2, 0)) + lumAt(p + float2(0, 2)) + lumAt(p - float2(0, 2)));
    float contour = smoothstep(0.45, 0.2, lo) * smoothstep(0.02, 0.12, around - lo);
    float3 col = paper;
    if (variant < 1.5) {
        // OldMapSource, A. Copperplate, hand-coloured. Colours and constants from the selected mockup.
        float3 cA = float3(63.0,143.0,94.0) / 255.0;
        float3 cB = float3(107.0,122.0,69.0) / 255.0;
        float3 cC = float3(216.0,169.0,60.0) / 255.0;
        float3 cD = float3(124.0,155.0,176.0) / 255.0;
        float4 t = tensor.eval(pixel);
        // Preserve the mockup's tensor interpretation, including its half cross term.
        float E = t.r, G = t.g, F = t.b - 0.5;
        float ang = 0.5 * atan(2.0 * F, E - G + 1e-7);
        float2 grad = float2(cos(ang), sin(ang));
        float2 tang = float2(-grad.y, grad.x);
        float wob = (noise(p / 18.0) - 0.5) * 2.2 + (noise(p / 5.0) - 0.5) * 0.8;
        float y = p.y + 1.2 * (noise(float2(p.x / 140.0, p.y / 9.0)) - 0.5);
        float hl = abs(fract(y / 5.0) - 0.5) * 5.0;
        float seaLine = (1.0 - smoothstep(0.35, 1.0, hl)) * (0.28 + 0.4 * exp(-dL / 60.0)) * (0.75 + 0.5 * noise(p / 7.0));
        float wl = waterlines(dL + wob * 0.6, 7.0, 0.35, 3.0, 0.45);
        float sea = clamp(seaLine * (1.0 - wl) + wl * 0.95, 0.0, 1.0) * (1.0 - isLand);
        col = mix(col, ink, sea * seaStrength);
        float band = (1.0 - smoothstep(10.0, 22.0, dW + wob * 2.0)) * isLand;
        float pooled = (1.0 - smoothstep(0.0, 4.0, abs(dW - 17.0 + wob * 2.0))) * isLand;
        float grain = 0.8 + 0.4 * noise(p / 3.0);
        float3 green = mix(cA, cB, 0.55 + 0.3 * fbm(p / 90.0));
        col = mix(col, col * mix(float3(1), green * 1.35, 0.55), band * grain * wash);
        col = mix(col, col * green * 1.1, pooled * 0.35 * wash);
        float halo = (1.0 - smoothstep(18.0, 30.0, dW + wob * 3.0)) * smoothstep(14.0, 22.0, dW + wob * 3.0) * isLand;
        col = mix(col, col * float3(0.78, 0.64, 0.45), halo * 0.35 * wash);
        col = mix(col, col * mix(float3(1), cD * 1.25, 0.7), river * wash);
        col = mix(col, ink, clamp(river * (1.0 - river) * 3.2, 0.0, 1.0) * 0.8 * coastStrength);
        float tone = clamp((1.0 - lm) * 1.1, 0.0, 1.0);
        float2 n1 = float2(-tang.y, tang.x);
        float tri1 = abs(fract(dot(p, tang) / 3.6 + 0.15 * noise(p / 25.0)) - 0.5) * 2.0;
        float tri2 = abs(fract(dot(p, n1) / 4.2 + 0.15 * noise(p / 31.0)) - 0.5) * 2.0;
        float eng = 1.0 - smoothstep(tone * 0.95 - 0.12, tone * 0.95, tri1);
        eng = max(eng, (1.0 - smoothstep(tone - 0.62, tone - 0.5, tri2)) * step(0.5, tone));
        float3 mcol = paper * mix(float3(1), mix(cC, cB, 0.5) * 1.3, 0.35 * wash);
        mcol = mix(mcol, ink, eng * 0.92 * pen);
        col = mix(col, mcol, ma);
        col = mix(col, ink, contour * contourStrength);
        col = mix(col, ink, (1.0 - smoothstep(0.9, 2.1, dc)) * 0.95 * coastStrength);
        // The sheet, identical to the Blaeu mockup (laid=1, foxing=.5, folds=1, edgeWear=.6).
        float3 beforePaper = col;
        col *= 0.94 + 0.1 * fbm(p / 260.0);
        float f = fract((p.x + 4.0 * noise(float2(0.0, p.y / 120.0))) / 96.0);
        float chain = 1.0 - smoothstep(0.6, 2.2, min(f, 1.0 - f) * 96.0);
        float laidLines = 0.5 + 0.5 * cos(6.2832 * fract(p.y / 3.6 + 0.3 * noise(p / 40.0)));
        col *= 1.0 + 0.03 * chain + 0.018 * (laidLines - 0.5);
        float2 cell = floor(p / 70.0);
        float best = 1e9; float rnd = 0.0; float rnd2 = 0.0;
        for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
            float2 gc = cell + float2(float(i), float(j));
            float2 centre = (gc + float2(hash(gc), hash(gc + 5.3))) * 70.0;
            float dist = length(p - centre);
            if (dist < best) { best = dist; rnd = hash(gc + 9.1); rnd2 = hash(gc + 2.7); }
        }
        float gate = smoothstep(0.45, 0.75, fbm(p / 420.0));
        float on = step(rnd, 0.06 * (0.3 + gate));
        float r = 2.0 + rnd2 * 7.0;
        col = mix(col, col * float3(0.62, 0.44, 0.25), on * (1.0 - smoothstep(r * 0.35, r, best)) * 0.6);
        col = mix(col, col * float3(0.86, 0.76, 0.6), on * (1.0 - smoothstep(r, r * 2.4, best)) * 0.35);
        float dx = (p.x - size.x * 0.5) / 2.5; float dy = (p.y - size.y * 0.5) / 2.5;
        float crease = max(exp(-dx * dx), exp(-dy * dy) * 0.8);
        float lit = max(exp(-(dx + 1.6) * (dx + 1.6)), exp(-(dy + 1.6) * (dy + 1.6)) * 0.8);
        col *= 1.0 - (0.12 * crease - 0.05 * lit);
        col *= 1.0 - 0.04 * max(exp(-dx * dx / 400.0), exp(-dy * dy / 400.0));
        float e = min(min(p.x, size.x - p.x), min(p.y, size.y - p.y));
        float wear = (1.0 - smoothstep(0.0, 200.0, e + 60.0 * (fbm(p / 90.0) - 0.5))) * 0.6;
        col = mix(col, col * float3(0.72, 0.58, 0.40), wear * 0.55);
        col += (hash(floor(p)) - 0.5) * 0.025;
        col = mix(beforePaper, col, fibres);
        float2 qd = abs(p - size * 0.5) - (size * 0.5 - 46.0);
        float sdf = max(qd.x, qd.y);
        col *= 1.0 - plate * 0.03 * step(sdf, 0.0);
        float ridge = 1.0 - smoothstep(0.0, 2.2, abs(sdf));
        float side = (p.x < size.x * 0.5 ? 1.0 : -1.0) * step(abs(qd.y), abs(qd.x)) + (p.y < size.y * 0.5 ? 1.0 : -1.0) * step(abs(qd.x), abs(qd.y));
        col *= 1.0 + plate * ridge * 0.06 * side;
        col = mix(col, ink, atlasBorder(p) * border);
    } else {
        // PrintSource, 5. Etching with aquatint, including the form-driven redraw of the mountains.
        float sil = smoothstep(0.35, 0.65, ma);
        float2 hg = float2(hAt(p + float2(2, 0)) - hAt(p - float2(2, 0)), hAt(p + float2(0, 2)) - hAt(p - float2(0, 2))) * 0.25;
        float steep = clamp(length(hg) * 45.0, 0.0, 1.0);
        float2 down = length(hg) > 1e-5 ? -normalize(hg) : float2(0.0, 1.0);
        float2 along = float2(-down.y, down.x);
        float3 nrm = normalize(float3(-hg.x * 60.0, -hg.y * 60.0, 1.0));
        float hill = clamp(dot(nrm, normalize(float3(-0.55, -0.65, 0.55))), 0.0, 1.0);
        float4 mf = mfaces.eval(pixel);
        float broad = lum(mix(float3(1), unpre(mf), mf.a));
        float shade = clamp(mix(hill, broad * 1.15, 0.45), 0.0, 1.0);
        float l0 = lineLum(p);
        float l1 = 0.25 * (lineLum(p + float2(3, 0)) + lineLum(p - float2(3, 0)) + lineLum(p + float2(0, 3)) + lineLum(p - float2(0, 3)));
        float ridge = smoothstep(0.55, 0.32, l0) * smoothstep(0.025, 0.1, l1 - l0) * sil;
        float wob = noise(p / 23.0) - 0.5;
        float2 rc = floor(p / 3.2);
        float f1 = 9.0; float f2 = 9.0;
        for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
            float2 gc = rc + float2(float(i), float(j));
            float dd = length(p - (gc + float2(hash(gc), hash(gc + 3.1))) * 3.2);
            if (dd < f1) { f2 = f1; f1 = dd; } else if (dd < f2) { f2 = dd; }
        }
        float resin = clamp((f2 - f1) / 2.2, 0.0, 1.0);
        float edgeWob = 22.0 * (fbm(p / 110.0) - 0.5);
        float seaStage = 0.2 + 0.18 * step(40.0 + edgeWob, dL) + 0.14 * step(130.0 + edgeWob, dL);
        float landStage = 0.1 * step(0.63, fbm(p / 150.0)) * isLand;
        float aqTone = mix(seaStage, landStage, isLand);
        float aq = smoothstep(resin - 0.07, resin + 0.07, aqTone * 1.25);
        float k = aq * 0.85 * mix(seaStrength, aquatint, isLand);
        k = mix(k, 0.0, (1.0 - smoothstep(2.0, 6.0, dL)) * (1.0 - isLand));
        k = max(k, clamp(river * (1.0 - river) * 3.6, 0.0, 1.0) * 0.9 * coastStrength);
        float bite = 0.8 + 0.4 * noise(p / 7.0);
        float outline = rimAt(p, 1.6 * bite) * contourStrength;
        float lines = smoothstep(0.25, 0.55, ridge * bite) * contourStrength;
        float stage = 0.32 * step(shade, 0.48) + 0.3 * step(shade, 0.26);
        float maq = smoothstep(resin - 0.07, resin + 0.07, stage * 1.25) * 0.9 * aquatint;
        float loose = screen(dot(p, along) + 3.0 * wob, 6.0, 0.2 * bite, 0.1) * step(shade, 0.3) * step(0.1, steep);
        float foul = step(0.993, hash(floor(p / 2.0))) * step(shade, 0.4);
        k = mix(k, max(max(outline, lines), max(maq, max(loose, foul) * pen)), sil);
        k = max(k, (1.0 - smoothstep(1.0, 2.2, dc + 0.8 * wob)) * coastStrength);
        col = mix(paper, ink, clamp(k, 0.0, 1.0));
        float wipe = fbm(float2(p.x / 400.0, p.y / 90.0));
        col *= 1.0 - 0.08 * smoothstep(0.3, 0.8, wipe) * fibres;
        col *= mix(1.0, 0.96 + 0.06 * fbm(p / 260.0), fibres);
        float2 qd = abs(p - size * 0.5) - (size * 0.5 - 52.0);
        float sdf = max(qd.x, qd.y);
        float outside = step(0.0, sdf);
        col = mix(col, paper * 1.02, outside * plate);
        float edge = 1.0 - smoothstep(0.0, 2.5, abs(sdf));
        float side = (p.x < size.x * 0.5 ? 1.0 : -1.0) * step(abs(qd.y), abs(qd.x)) + (p.y < size.y * 0.5 ? 1.0 : -1.0) * step(abs(qd.x), abs(qd.y));
        col *= 1.0 + plate * edge * 0.07 * side;
        col *= 1.0 - plate * 0.03 * (1.0 - outside);
        col += (hash(floor(p)) - 0.5) * 0.02 * fibres;
    }
    float3 base = src.rgb / max(src.a, 0.001);
    col = mix(base, clamp(col, 0.0, 1.0), strength);
    return half4(half3(col * src.a), half(src.a));
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
        SKRuntimeEffect.CreateShader(Source, out var errors) ?? throw new InvalidOperationException(errors));

    public static SKShader Build(SKImage scene, SKImage mountains, SKImage geo, SKImage tensor, SKImage height,
        SKImage lines, SKImage faces, SKPoint origin, SKPoint docPerPx, SKSize size, MapStyle style)
    {
        var b = new SKRuntimeShaderBuilder(Effect.Value);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        using var s0 = scene.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var s1 = mountains.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var s2 = geo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var s3 = tensor.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var s4 = height.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var s5 = lines.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        using var s6 = faces.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, sampling);
        b.Children["scene"] = s0; b.Children["mount"] = s1; b.Children["geo"] = s2; b.Children["tensor"] = s3;
        b.Children["height"] = s4; b.Children["mlines"] = s5; b.Children["mfaces"] = s6;
        b.Uniforms["origin"] = new[] { origin.X, origin.Y };
        b.Uniforms["docPerPx"] = new[] { docPerPx.X, docPerPx.Y };
        b.Uniforms["size"] = new[] { size.Width, size.Height };
        var atlas = style.Preset == MapStylePreset.OldMapBlaeu;
        b.Uniforms["variant"] = atlas ? 1f : 5f;
        b.Uniforms["paper"] = Rgb(atlas ? "#e6dab9" : "#efe7d6");
        b.Uniforms["ink"] = Rgb(atlas ? "#221c17" : "#4a3324");
        b.Uniforms["strength"] = (float)style.Strength;
        b.Uniforms["pen"] = (float)style.Pen.Effective;
        b.Uniforms["contourStrength"] = (float)style.Contour.Effective;
        b.Uniforms["seaStrength"] = (float)style.Sea.Effective;
        b.Uniforms["coastStrength"] = (float)style.Coast.Effective;
        b.Uniforms["wash"] = (float)style.ColourWash.Effective;
        b.Uniforms["aquatint"] = (float)style.Aquatint.Effective;
        b.Uniforms["fibres"] = (float)style.Paper.Effective;
        b.Uniforms["plate"] = (float)style.Plate.Effective;
        b.Uniforms["border"] = (float)style.Border.Effective;
        return b.Build();
    }

    private static float[] Rgb(string hex)
    {
        var c = SKColor.Parse(hex);
        return [c.Red / 255f, c.Green / 255f, c.Blue / 255f];
    }
}
