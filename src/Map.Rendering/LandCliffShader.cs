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
uniform float lightTurn;
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
uniform shader apron;
uniform float scree;
uniform float screeWidth;

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

// Scree: where a cone of fallen stone climbs the wall (a gully), 0..1, read at the point of the rim.
float gullyAt(float2 rim, float H) { return smoothstep(0.5, 0.82, noise2(rim / (H * 1.3) + float2(21.0, 4.0))); }
// Scree: height of the band of rock left standing above the slope, at the point of the rim.
float wallHeight(float2 rim, float H, float share, float cones) {
    float n1 = noise2(rim / (H * 1.9) + float2(3.7, 11.3));
    float n2 = noise2(rim / (H * 0.6) + float2(8.1, 2.9));
    return max(0.0, H * share * (0.45 + n1 + 0.4 * (n2 - 0.5)) * (1.0 - cones * gullyAt(rim, H)));
}
// Scree: how far the slope runs out from the rim here; nothing under an edge that faces away from the viewer.
float slopeWidth(float2 p, float2 rim, float phi, float H, float width, float tongues, float cones) {
    float south = smoothstep(-0.5, 0.3, cos(phi));
    float wn = noise2(p / (H * 1.7) + float2(5.0, 9.0));
    float tn = noise2(rim / (H * 0.5) + float2(31.0, 17.0));
    return min(H * width * 1.5 * south * (0.55 + 0.5 * wn + tongues * smoothstep(0.4, 0.8, tn) + 0.4 * cones * gullyAt(rim, H)), H * screeWidth * 2.2);
}

half4 main(float2 p) {
    float4 f = field.eval(p);
    float d = f.r;
    float steps = floor(f.g + 0.5);
    // «здесь стена» of the cliff mask: the steps carry a quarter, and this stretch has no gap.
    float forced = step(0.12, f.g - steps);
    float th = f.b;

    if (scree > 0.5) {
        if (steps < 0.5) { return half4(0.0); }
        float4 ax = apron.eval(p);
        float e = ax.r;
        bool walled = d > 0.0 && d < 50000.0;
        bool sloped = e > 0.0 && e < 50000.0;
        if (!walled && !sloped) { return half4(0.0); }
        float wet = smoothstep(0.35, 0.65, ax.g);
        float phi = ax.b;
        float H = stepHeight * min(steps, 3.0);
        float k = stepHeight / 32.0;

        // The four slopes of CliffScree: 1 two benches; 2 a taller wall and one bench; 3 three thin steps in pen,
        // no stones; 4 benches broken into pieces.
        float share = 0.45; float width = 1.0; float tongues = 0.25; float cones = 0.2; float fillA = 0.8;
        float streaks = 0.5; float ledges = 2.0; float broken = 0.0;
        float cellA = 8.0; float densA = 0.5; float cellB = 4.0; float densB = 0.45; float contour = 1.0;
        if (scree > 3.5) { share = 0.4; width = 1.0; fillA = 0.7; broken = 1.0; densA = 0.4; densB = 0.5; }
        else if (scree > 2.5) { share = 0.3; width = 0.95; fillA = 0.88; streaks = 0.25; ledges = 3.0; densA = 0.0; densB = 0.0; tongues = 0.1; }
        else if (scree > 1.5) { share = 0.85; width = 0.85; fillA = 0.8; ledges = 1.0; densA = 0.35; densB = 0.55; }

        float3 light = lightColor.rgb; float3 shade = shadeColor.rgb; float3 inkc = inkColor.rgb;
        float3 foam = float3(0.95, 0.97, 0.97);
        float cl = cos(lightTurn); float sl = sin(lightTurn);
        float2 L = float2(-0.7071 * cl + 0.7071 * sl, -0.7071 * sl - 0.7071 * cl);    // towards the light

        // The band of rock under the rim: blocks of uneven width and tone, slanted cracks, a broken foot.
        float2 edge = float2(p.x - lean * d, p.y - d);
        float hw = walled ? wallHeight(edge, H, share, cones) : 0.0;
        float4 wallOut = float4(0.0);
        if (walled && d <= hw + px) {
            float t = d / max(hw, 1e-3);
            float toLight = th + lightTurn;
            float lit = clamp(0.55 - 0.5 * sin(toLight) + 0.1 * cos(toLight), 0.0, 1.0);
            float slant = (noise1(edge.x / (facetWidth * 4.0) + 2.2) - 0.5) * 1.3;
            float bs = (edge.x + slant * d) / (facetWidth * 0.9) + 0.6 * noise1(edge.x / (facetWidth * 2.3) + 5.3);
            float bid = floor(bs); float within = bs - bid;
            lit = clamp(lit + 0.3 * (hash1(bid * 1.37) - 0.5), 0.0, 1.0);
            float dark = (1.0 - lit) * contrast + 0.3 * gullyAt(edge, H) + rimShadow * (1.0 - smoothstep(0.0, H * 0.2, d)) + 0.18 * t;
            dark += 0.14 * (noise2(p / (3.0 * k)) - 0.5);
            float3 col = mix(light, shade, clamp(dark, 0.0, 1.0));
            float reach = mix(0.4, 1.0, hash1(bid * 2.71)) * hw;
            float ink = facetLines * line(min(within, 1.0 - within) * facetWidth * 0.9, inkWidth * 0.6) * step(d, reach);
            ink = max(ink, line(hw - d - inkWidth * 0.5, inkWidth * 0.5) * step(0.35, noise2(edge / (9.0 * k) + 3.0)) * (1.0 - wet));
            col = mix(col, inkc, clamp(ink, 0.0, 1.0) * inkColor.a);
            // In the water the foot is a line of surf.
            col = mix(col, foam, wet * line(hw - d - 1.2 * k, 1.2 * k) * step(0.3, noise2(edge / (6.0 * k) + 9.0)) * 0.9);
            float alpha = clamp((hw - d) / px + 0.5, 0.0, 1.0);
            if (rimWidth > 0.0 && d < rimWidth) { col = mix(col, rimColor.rgb, rimColor.a); alpha = max(alpha, rimColor.a); }
            wallOut = float4(col, 1.0) * alpha * lightColor.a;
        }

        float4 acc = float4(0.0);
        if (sloped) {
            float2 nrm = float2(sin(phi), cos(phi));
            float2 rim = p - nrm * e;
            float W = slopeWidth(p, rim, phi, H, width * screeWidth, tongues, cones);
            float u = e / max(W, 1e-3);
            float rag = (noise2(p / (H * 0.2)) - 0.5) * 0.35 + (noise2(p / (H * 0.07)) - 0.5) * 0.15;
            float inside = (1.0 - smoothstep(0.6, 1.0, u + rag)) * step(1e-3, W);

            // The slope itself: a wash that lightens outwards, streaked down the fall line; sunk and dark in water.
            float st = noise2(rim / (H * 0.13)) * 0.6 + noise2(float2(rim.x + rim.y, e) / (H * 0.3)) * 0.4;
            float tone = 0.3 + 0.45 * smoothstep(0.0, 0.9, u) + streaks * (st - 0.5) * 0.4 * (1.0 - smoothstep(0.3, 0.85, u));
            if (walled) { tone -= 0.25 * (1.0 - smoothstep(0.0, H * 0.35, d - hw)); }
            float3 fill = mix(shade, light, clamp(tone, 0.0, 1.0));
            float a = inside * fillA * (1.0 - 0.35 * smoothstep(0.3, 1.0, u));
            // Under water the slope is a pale shelf that darkens towards the wall, with a broken line of surf along its edge.
            fill = mix(fill, mix(shadowColor.rgb, light, 0.25 + 0.3 * smoothstep(0.1, 0.9, u)), wet);
            a = mix(a, inside * 0.34, wet);
            acc = float4(fill, 1.0) * a;
            float surf = wet * (1.0 - smoothstep(0.0, 1.3 * k, abs((u + rag - 0.8) * W))) * step(0.42, noise2(p / (5.0 * k) + 11.0)) * step(1e-3, W) * 0.75;
            acc = float4(foam, 1.0) * surf + acc * (1.0 - surf);

            // Benches across the slope: a dark riser under a broken pen line.
            for (int j = 0; j < 3; j++) {
                if (float(j) < ledges - 0.5) {
                    float at = (float(j) + 1.0) / (ledges + 1.0) * 0.95;
                    float wob = (noise2(rim / (H * 0.45) + 7.0 * float(j)) - 0.5) * 0.5 / (ledges + 1.0);
                    // Broken benches: pieces along the rim, each set a little higher or lower, with gaps between them.
                    float piece = floor(dot(rim, float2(0.7, 0.45)) / (H * 0.55) + 3.0 * float(j) + noise2(rim / (H * 1.1)));
                    float present = broken > 0.5 ? step(0.3, hash1(piece * 3.7 + float(j))) : 1.0;
                    if (broken > 0.5) { wob += (hash1(piece * 1.9 + float(j) * 5.0) - 0.5) * 0.3; }
                    float past = (u - at - wob) * W;
                    float riser = (1.0 - smoothstep(0.0, H * 0.1, past)) * step(0.0, past) * inside * (1.0 - wet) * 0.5 * present;
                    acc = float4(shade * 0.85, 1.0) * riser + acc * (1.0 - riser);
                    float pen = line(past, inkWidth * 0.5) * step(0.3, noise2(rim / (7.0 * k) + 3.0 * float(j))) * inside * (1.0 - wet) * inkColor.a * present;
                    acc = float4(inkc, 1.0) * pen + acc * (1.0 - pen);
                }
            }
            // Stones under the water: dark soft shapes, thinning out from the shore.
            if (wet > 0.01) {
                float2 bq = p / (8.0 * k) + 40.0; float2 bc = floor(bq);
                float2 centre = bc + 0.25 + 0.5 * float2(hash2(bc), hash2(bc + 31.7));
                float r = (1.4 + 1.6 * hash2(bc + 3.3)) * k;
                float blob = step(hash2(bc + 7.3), 0.55) * (1.0 - smoothstep(r * 0.5, r, length(bq - centre) * 8.0 * k));
                blob *= wet * 0.3 * (1.0 - smoothstep(0.5, 1.0, u)) * step(1e-3, W);
                acc = float4(shadowColor.rgb * 0.6, 1.0) * blob + acc * (1.0 - blob);
            }

            // The stones, large and small: each is whole or absent by what lies under its own centre.
            for (int j = 0; j < 2; j++) {
                float cell = (j == 0 ? cellA : cellB) * k * 1.5;
                float dens = j == 0 ? densA : densB;
                float2 g = p / cell + 13.7 * float(j); float2 gi = floor(g); float2 gf = g - gi;
                float f1 = 8.0; float f2 = 8.0; float2 best = float2(0.0); float2 bestCell = float2(0.0);
                for (int yy = -1; yy <= 1; yy++) {
                    for (int xx = -1; xx <= 1; xx++) {
                        float2 o = float2(float(xx), float(yy));
                        float2 r = o + 0.15 + 0.7 * float2(hash2(gi + o), hash2(gi + o + 57.0)) - gf;
                        float dd = dot(r, r);
                        if (dd < f1) { f2 = f1; f1 = dd; best = r; bestCell = gi + o; } else if (dd < f2) { f2 = dd; }
                    }
                }
                float border = (sqrt(f2) - sqrt(f1)) * 0.5 * cell;
                float2 r = -best * cell;
                float2 c = p - r;
                float4 cx = apron.eval(c);
                float ec = cx.r; float wetc = step(0.5, cx.g);
                float2 nc = float2(sin(cx.b), cos(cx.b));
                float2 rimC = c - nc * ec;
                float Wc = slopeWidth(c, rimC, cx.b, H, width * screeWidth, tongues, cones);
                float uc = ec / max(Wc, 1e-3);
                float id1 = hash2(bestCell + 13.0); float id2 = hash2(bestCell + 29.0);
                float id3 = hash2(bestCell + 43.0); float id4 = hash2(bestCell + 71.0);
                float rad = cell * mix(0.16, 0.38, id3) * mix(0.7, 1.2, clamp(uc, 0.0, 1.0));
                // Stones gather on the benches and stay on the slope: none past its toe.
                float bench = fract(uc * (ledges + 1.0) / 0.95);
                float want = dens * mix(1.0, 0.4, smoothstep(0.25, 0.9, uc)) * (0.35 + 1.4 * smoothstep(0.55, 0.95, bench));
                want *= 1.0 - 0.45 * wetc;
                float clear = max(rad * 0.7, wallHeight(float2(c.x, c.y - ec), H, share, cones) * 0.8 * smoothstep(0.0, 0.5, nc.y));
                if (dens > 0.001 && id1 < want && uc < 0.82 && Wc > 1e-3 && ec > clear && ec < 50000.0) {
                    float2 axis = float2(cos(id2 * 6.2832), sin(id2 * 6.2832));
                    float2 l = float2(dot(r, axis), dot(r, float2(-axis.y, axis.x)) / mix(0.58, 0.92, id4));
                    float an = atan(l.y, l.x);
                    float wobble = 1.0 + 0.13 * sin(an * 3.0 + id2 * 20.0) + 0.07 * sin(an * 5.0 + id3 * 40.0);
                    float sd = max(length(l) / wobble - rad, 0.5 * k - border);
                    float cover = clamp(0.5 - sd / px, 0.0, 1.0);
                    // Its shadow on the ground, or its dark mirror on the water.
                    float2 off = wetc > 0.5 ? float2(0.0, rad * 0.55) : -L * rad * 0.5;
                    float sh = (1.0 - smoothstep(-rad * 0.3, rad * 0.25, length(r - off) - rad * 0.95)) * (wetc > 0.5 ? 0.28 : 0.4);
                    acc = float4(shadowColor.rgb * 0.7, 1.0) * sh + acc * (1.0 - sh);
                    // Surf round a stone that stands in the water.
                    float ring = wetc * (1.0 - smoothstep(0.6 * k, 2.4 * k, sd)) * smoothstep(0.3, 0.55, noise2(p / (2.6 * k) + 3.0 + id1 * 9.0)) * 0.85;
                    acc = float4(foam, 1.0) * ring + acc * (1.0 - ring);
                    float nl = dot(normalize(r + 1e-4), L);
                    float lit = smoothstep(-0.35, 0.25, nl + (noise2(p / (1.7 * k) + id1 * 30.0) - 0.5) * 0.5);
                    float3 sc = mix(shade, light, 0.18 + 0.72 * lit);
                    sc = mix(sc, sc * 0.72, wetc * smoothstep(-0.1, 0.4, r.y / rad));
                    float pen = line(sd + inkWidth * 0.5, inkWidth * 0.5) * contour * mix(1.0, 0.45 + 0.55 * step(0.4, noise2(p / (4.0 * k) + id1 * 17.0)), lit);
                    sc = mix(sc, inkc, clamp(pen, 0.0, 1.0) * inkColor.a);
                    acc = float4(sc, 1.0) * cover + acc * (1.0 - cover);
                }
            }
        }
        return half4(wallOut + acc * (1.0 - wallOut.a));
    }

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
    if (gaps > 0.001 && side < 0.0 && forced < 0.5) { return half4(0.0); }
    float taper = gaps > 0.001 && forced < 0.5 ? smoothstep(0.0, stepHeight * 1.6, side) : 1.0;

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
    // The light: turned from the north-west every older map has by lightTurn.
    float toLight = facing + lightTurn;
    float lit = clamp(0.55 - 0.5 * sin(toLight) + 0.1 * cos(toLight), 0.0, 1.0);
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
    if (gaps > 0.001 && forced < 0.5) { ink = max(ink, line(side, inkWidth * 0.5) * facetLines); }
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
    internal static SKShader? Create(SKShader field, SKShader? apron, LandCliffStyle style, string rimColor, double rimWidth, double pixel)
    {
        if (Effect.Value is not { } effect) return null;
        var builder = new SKRuntimeShaderBuilder(effect);
        builder.Children["field"] = field;
        // Without scree the ground round the foot is never read; the field stands in for it.
        builder.Children["apron"] = apron ?? field;
        builder.Uniforms["scree"] = apron is null ? 0f : (int)style.Scree;
        builder.Uniforms["screeWidth"] = (float)style.ScreeWidth;
        builder.Uniforms["px"] = (float)pixel;
        builder.Uniforms["stepHeight"] = (float)style.Height;
        builder.Uniforms["heightVariation"] = (float)style.HeightVariation;
        builder.Uniforms["variationLength"] = (float)style.VariationLength;
        builder.Uniforms["gaps"] = (float)style.Gaps;
        builder.Uniforms["gapLength"] = (float)style.GapLength;
        builder.Uniforms["lean"] = (float)style.Lean;
        builder.Uniforms["lightTurn"] = (float)((style.LightAngle - LandCliffStyle.DefaultLightAngle) * Math.PI / 180);
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
