using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, round 5 (owner 2026-10-24: «сейчас ты делаешь 5 вариантов гравюры … изучи всё про этот стиль»): five
/// print techniques, each unlike the chosen «Атлас Блау» (hand-coloured copperplate) and unlike each other - research in
/// docs/research/2026-10-24-r09-engraving-styles.md. MAP_STYLE_ENGRAVING = output folder, MAP_LIGHT_ASSETS as before.
/// The same three parts and coast field as the old-map round; one pass per technique; names on top unchanged.
/// </summary>
public sealed partial class StyleMockupDump
{
    private sealed record Print(int Variant, string Paper, string Ink, string Second, float Plate);

    [Fact]
    public void Dump_engraving_variants_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_ENGRAVING");
        var assets = Environment.GetEnvironmentVariable("MAP_LIGHT_ASSETS");
        if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(assets) || !Directory.Exists(assets)) return;
        Directory.CreateDirectory(output);
        var images = Directory.GetFiles(assets, "*.png").Take(3)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => SKImage.FromEncodedData(f)!);
        try
        {
            var (document, labels) = Scene(images);
            var mountainsId = document.Layers.First(l => l.Name == "Горы").Id;
            var riversId = document.Layers.First(l => l.Name == "Реки").Id;
            var land = document.Layers.OfType<Map.Core.Documents.LandLayer>().First().Id;
            using var mountains = RenderOnly(document, mountainsId, images);
            using var names = RenderOnly(document, labels, images);
            using var landOnly = RenderOnly(document, land, images);
            using var riverOnly = RenderOnly(document, riversId, images);
            using var geo = CoastField(landOnly, riverOnly);
            using var mountainsOnWhite = OnWhite(mountains);
            using var tensor = Tensor(mountainsOnWhite);

            var variants = new (string Name, Print Print)[]
            {
                ("Гравюра 1 · Ксилография, Нюрнбергская хроника", new(1, "#ebe2c9", "#1c1814", "#000000", 0)),
                ("Гравюра 2 · Торцовая гравюра, белая линия", new(2, "#f1ede2", "#151413", "#000000", 0)),
                ("Гравюра 3 · Стальная гравюра, атлас XIX века", new(3, "#f7f5ef", "#1f2630", "#000000", 1)),
                ("Гравюра 4 · Меццо-тинто", new(4, "#eee4cf", "#3a2a1e", "#000000", 1)),
                ("Гравюра 5 · Офорт с акватинтой", new(5, "#efe7d6", "#4a3324", "#000000", 1)),
            };
            foreach (var (name, print) in variants)
            {
                using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                var canvas = surface.Canvas;
                using (var shader = PrintShader(mountains, geo, tensor, print))
                using (var paint = new SKPaint { Shader = shader })
                    canvas.DrawRect(0, 0, W, H, paint);
                if (print.Variant == 3) DrawGuillocheBorder(canvas);
                canvas.DrawImage(names, 0, 0);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(output, name + ".png"), data.ToArray());
            }
        }
        finally
        {
            foreach (var image in images.Values) image.Dispose();
        }
    }

    private const string PrintSource = """
uniform shader mount;
uniform shader geo;
uniform shader tensor;
uniform float variant;
uniform float2 size;
uniform float3 paper;
uniform float3 ink;
uniform float plate;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

// A line screen: lines along `coord`, ink covering about `tone` of each period (swelling where the tone rises).
float screen(float coord, float period, float tone, float aa) {
    float tri = abs(fract(coord / period) - 0.5) * 2.0;
    return 1.0 - smoothstep(tone - aa, tone + aa, tri);
}

// Lines spaced ever wider from the coast: the k-th lies at s0 (k + a k^2).
float waterlines(float d, float s0, float a, float count, float width) {
    if (d <= 0.5) return 0.0;
    float k = (-1.0 + sqrt(1.0 + 4.0 * a * d / s0)) / (2.0 * a);
    float spacing = s0 * (1.0 + 2.0 * a * k);
    float ph = fract(k);
    float px = min(ph, 1.0 - ph) * spacing;
    return (1.0 - smoothstep(width, width + 0.9, px)) * step(k, count + 0.5);
}

// Short tapered strokes scattered in cells (cell splatting, constant-bound loops): one stroke per kept cell, laid at
// the angle of a slow wave field, kept with probability `density`. Returns the stroke's coverage.
float flicks(float2 p, float cell, float density, float lengthK, float width, float seed) {
    float2 base = floor(p / cell);
    float best = 0.0;
    for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
        float2 gc = base + float2(float(i), float(j));
        if (hash(gc + seed) >= density) continue;
        float2 c = (gc + 0.2 + 0.6 * float2(hash(gc + 1.7 + seed), hash(gc + 4.1 + seed))) * cell;
        float a = 0.5 * (noise(c / 380.0) - 0.5) + 0.25 * sin(c.x / 210.0);
        float2 d = float2(cos(a), sin(a));
        float2 q = p - c;
        float halfLen = cell * lengthK * (0.6 + 0.6 * hash(gc + 9.3 + seed));
        float tt = clamp(dot(q, d) / halfLen, -1.0, 1.0);
        float w = width * (1.0 - tt * tt);
        float perp = abs(dot(q, float2(-d.y, d.x)));
        float along = abs(dot(q, d)) - halfLen;
        best = max(best, (1.0 - smoothstep(w - 0.5, w + 0.5, perp)) * step(along, 0.0));
    }
    return best;
}

// Woodcut wave crests: little arcs with a curl, in staggered rows.
float crests(float2 p, float cell, float density) {
    float row = floor(p.y / cell);
    float2 q = float2(p.x + mod(row, 2.0) * cell * 0.5, p.y);
    float2 gc = floor(q / cell);
    if (hash(gc + 2.2) >= density) return 0.0;
    float2 c = (gc + float2(0.5, 0.55)) * cell;
    float2 v = q - c;
    float r = cell * 0.22;
    // The crest's back: the upper half of a circle; its curl: a smaller hook turning in at the front.
    float arc = abs(length(v) - r) + (v.y > r * 0.1 ? 99.0 : 0.0);
    float2 hc = float2(r * 0.5, -r * 0.05);
    float curl = abs(length(v - hc) - r * 0.45) + ((v.x < hc.x || v.y > hc.y + r * 0.2) ? 99.0 : 0.0);
    // A short tail running back from the crest.
    float tail = abs(v.y - r * 0.08) + ((v.x > -r || v.x < -r * 2.2) ? 99.0 : 0.0);
    return 1.0 - smoothstep(0.9, 1.9, min(min(arc, curl), tail));
}

float3 unpre(float4 c) { return c.a > 0.001 ? c.rgb / c.a : float3(1); }
float lumAt(float2 p) { float4 c = mount.eval(p); return lum(mix(float3(1), unpre(c), c.a)); }

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float dL = g.r * 255.0;
    float dW = g.g * 255.0;
    float isLand = step(0.5, dW);
    float river = g.b * smoothstep(0.5, 3.0, dW);
    float dc = max(dL, dW);
    float4 m = mount.eval(p);
    float ma = m.a;
    float lm = lum(unpre(m));
    float lo = lumAt(p);
    float around = 0.25 * (lumAt(p + float2(2, 0)) + lumAt(p - float2(2, 0)) + lumAt(p + float2(0, 2)) + lumAt(p - float2(0, 2)));
    float contour = smoothstep(0.45, 0.2, lo) * smoothstep(0.02, 0.12, around - lo);
    float4 t = tensor.eval(p);
    float E = t.r, G = t.g, F = t.b - 0.5;
    float ang = 0.5 * atan(2.0 * F, E - G + 1e-7);
    float2 grad = float2(cos(ang), sin(ang));
    float2 tang = float2(-grad.y, grad.x);
    float wob = noise(p / 23.0) - 0.5;

    float k = 0.0;              // ink coverage, 0 = paper, 1 = full ink
    float3 col = paper;

    if (variant < 1.5) {
        // ---- 1. Black-line woodcut (Nuremberg Chronicle, Münster): the knife leaves few, bold lines. The sea is blank paper
        // with sparse curled crests in staggered rows, kept off the coast; a second thin line doubles the shore.
        float sea = crests(p + 3.0 * wob, 46.0, 0.55) * smoothstep(18.0, 30.0, dL);
        float near = 1.0 - smoothstep(0.8, 1.8, abs(dL - 9.0 - 2.0 * wob));
        k = max(sea, near) * (1.0 - isLand);
        // Land: blank, with short strokes inside the coast as the cutter shaded the shore.
        float shore = screen(p.x * 0.6 + p.y, 8.0, 0.35, 0.1) * (1.0 - smoothstep(6.0, 16.0, dW + 4.0 * wob)) * step(3.0, dW);
        k = max(k, shore * isLand * 0.9);
        // River: two bold banks, a blank middle.
        k = max(k, clamp(river * (1.0 - river) * 4.0, 0.0, 1.0));
        // Mountains: three cuts of tone - left blank, parallel cuts along the form, solid black.
        float tone = 1.0 - lm;
        float hatch = screen(dot(p, tang) + 2.0 * wob, 7.0, 0.45, 0.1);
        float mk = tone < 0.3 ? 0.0 : (tone < 0.62 ? hatch : 1.0);
        k = mix(k, mk, ma);
        // Bold keyblock contour: the owner's line, thickened.
        float bold = smoothstep(0.5, 0.25, min(lumAt(p + float2(1.2, 0)), lumAt(p - float2(1.2, 0)))) * ma;
        k = max(k, max(contour, bold * smoothstep(0.02, 0.1, around - lo)));
        k = max(k, 1.0 - smoothstep(1.6, 3.2, dc));
        // Uneven inking on the block and wood grain in the solids.
        float grain = noise(float2(p.x / 90.0 + fbm(p / 300.0) * 4.0, p.y / 2.4));
        k *= 1.0 - 0.12 * smoothstep(0.6, 0.9, grain) * step(0.9, k) - 0.12 * smoothstep(0.6, 0.9, fbm(p / 140.0));
        // Loose hand colouring, brushed after printing and off register: sap green and ochre on the land, red lead on the
        // peaks, a pale wash along the shore.
        float2 off = float2(9.0, 6.0);
        float4 go = geo.eval(p + off);
        float landW = smoothstep(0.0, 2.0, go.g * 255.0);
        float seaW = (1.0 - landW) * (1.0 - smoothstep(20.0, 60.0, go.r * 255.0 + 20.0 * wob));
        float mw = mount.eval(p + off).a;
        float patch = smoothstep(0.45, 0.6, fbm(p / 260.0));
        float3 tint = float3(1);
        tint = mix(tint, mix(float3(0.55, 0.60, 0.36) * 1.5, float3(0.79, 0.63, 0.31) * 1.25, patch * 0.7), landW * 0.32 * (0.8 + 0.4 * fbm(p / 90.0)));
        tint = mix(tint, float3(0.62, 0.70, 0.72) * 1.35, seaW * 0.45);
        tint = mix(tint, float3(0.78, 0.33, 0.24) * 1.35, mw * 0.4 * smoothstep(0.4, 0.7, 1.0 - lumAt(p + off)));
        col = mix(paper * tint, ink, clamp(k, 0.0, 1.0));
    } else if (variant < 2.5) {
        // ---- 2. White-line wood engraving (Bewick): the block prints black; the graver cuts white lines - many where light.
        // Sea: the uncut black block; the graver flicks short tapered white strokes along a slow wave field, few near the
        // shore and many offshore (silvery), over a fine tint-tool ruling that lightens away from the land.
        float off01 = smoothstep(10.0, 220.0, dL);
        float whiteFlicks = max(flicks(p, 26.0, 0.2 + 0.7 * off01, 0.9, 1.6, 0.0), flicks(p + 13.0, 17.0, 0.1 + 0.6 * off01, 0.7, 1.1, 5.0));
        float tint = screen(p.y + 1.5 * wob, 3.6, 0.12 + 0.3 * off01, 0.12);
        float seaInk = 1.0 - max(whiteFlicks, tint);
        // The white surf band cut along the coast.
        seaInk *= 1.0 - (1.0 - smoothstep(4.0, 7.0, dL + 2.0 * wob)) * step(1.6, dL);
        // Land: the block cut away to white; along the coast a "lowered" band prints a soft grey, flecked by the graver.
        float lowered = (1.0 - smoothstep(12.0, 30.0, dW + 10.0 * wob)) * step(2.0, dW) * 0.3;
        float fleck = step(0.985, hash(floor(p / 1.6))) * (1.0 - smoothstep(0.0, 40.0, dW));
        k = mix(seaInk, max(lowered, fleck * 0.8), isLand);
        k = max(k, 1.0 - smoothstep(0.8, 1.8, dc));
        // A seam where two boxwood blocks were bolted together: one thin white line across the whole print.
        k *= 1.0 - (1.0 - smoothstep(0.3, 0.9, abs(p.x - size.x * 0.618)));
        // River: cut like the sea, dark with ruled white lines.
        k = mix(k, 1.0 - screen(p.y + 1.5 * wob, 4.2, 0.45, 0.12), smoothstep(0.2, 0.6, river));
        // Mountains: black block, white lines along the form; more and wider where the painting is light.
        float lightM = clamp(lm * 1.05 - 0.22, 0.0, 1.0);
        float cut = screen(dot(p, tang) + 1.4 * wob, 3.6, lightM, 0.1);
        float cut2 = screen(dot(p, grad), 5.0, lightM - 0.55, 0.1) * step(0.6, lightM);
        float mk = 1.0 - max(cut, cut2);
        k = mix(k, mk, ma);
        k = max(k, contour);
        col = mix(paper, ink, clamp(k, 0.0, 1.0));
    } else if (variant < 3.5) {
        // ---- 3. Steel engraving for an atlas (SDUK, Stieler): waterlining from the coast, then a machine-ruled tint at 45°,
        // hachures down the slopes, blue-black ink, crisp wove paper. Its look is its precision: no roughness at all.
        float wl = waterlines(dL, 3.2, 0.06, 15.0, 0.32);
        float ruled = screen((p.x + p.y) * 0.7071, 2.7, 0.16, 0.2) * smoothstep(150.0, 200.0, dL);
        k = max(ruled * 0.85, wl * 0.9) * (1.0 - isLand);
        // Land: stipple shading along the coast, finest.
        // Strict comparison: the sine hash is often exactly 0 in float, and step(0, 0) would ink every such pixel.
        float st = (hash(floor(p / 1.25)) < 0.12 * (1.0 - smoothstep(0.0, 16.0, dW)) ? 1.0 : 0.0) * isLand;
        k = max(k, st * 0.7);
        k = max(k, clamp(river * (1.0 - river) * 4.0, 0.0, 1.0));
        k = max(k, screen(p.y, 2.6, 0.3, 0.18) * step(0.5, river) * 0.6);
        // Mountains: Lehmann hachures - short strokes down the slope (along the gradient), side by side along the contour,
        // in rows, thicker where steeper and darker; the deepest shadows get a fine crossing layer.
        float tone2 = clamp((1.0 - lm) * 1.1, 0.0, 1.0);
        float across = dot(p, tang);
        float alongS = dot(p, grad);
        float rowH = floor(alongS / 8.0);
        float hach = screen(across + 1.3 * hash(float2(rowH, 3.0)), 2.4, tone2 * 0.85, 0.15);
        hach *= smoothstep(0.06, 0.16, abs(fract(alongS / 8.0) - 0.5) * 2.0);
        float l3 = screen(alongS, 2.6, tone2 - 0.65, 0.15) * step(0.65, tone2);
        k = mix(k, max(hach, l3), ma);
        k = max(k, contour);
        k = max(k, 1.0 - smoothstep(0.7, 1.6, dc));
        col = mix(paper, ink, clamp(k, 0.0, 1.0));
    } else if (variant < 4.5) {
        // ---- 4. Mezzotint: the rocked plate prints velvet black; the image is burnished toward light.
        float tone;
        // The sea stays near the rocked black, burnished to a glow along the shore; the land is burnished almost to paper.
        float seaTone = 0.9 - 0.45 * exp(-dL / 45.0) + 0.06 * (fbm(p / 220.0) - 0.5);
        float landTone = 0.06 + 0.08 * fbm(p / 260.0) + 0.3 * (1.0 - smoothstep(0.0, 18.0, dW));
        tone = mix(seaTone, landTone, isLand);
        tone = mix(tone, 0.55, step(0.5, river) * isLand);
        // Scraped glints on the sea: horizontal-ish streaks the scraper lifted.
        float streak = smoothstep(0.62, 0.8, noise(float2(p.x / 140.0, p.y / 5.0))) * smoothstep(30.0, 90.0, dL);
        tone -= 0.18 * streak * (1.0 - isLand);
        float mt = clamp(pow(1.0 - lm, 0.85) * 1.05, 0.0, 1.0);
        tone = mix(tone, mt, ma);
        // The rocker's burr: a dense grain of tiny pits in crossing directions, burnished smooth where light.
        float pits = hash(floor(p / 1.05)) * 0.55 + noise(p / 1.6) * 0.45;
        float rocker = 0.5 + 0.5 * sin((p.x * 0.7 + p.y * 0.7) * 2.2) * sin((p.x * 0.7 - p.y * 0.7) * 2.0);
        float grainy = pits * 0.8 + rocker * 0.2;
        k = smoothstep(grainy - 0.3, grainy + 0.3, tone * 1.15);
        k = mix(k, tone, 0.45);
        // Burnished highlights are polished smooth: no grain left where the tone is light.
        k = mix(tone, k, smoothstep(0.12, 0.35, tone));
        k = max(k, contour * 0.7);
        k = max(k, (1.0 - smoothstep(0.7, 1.8, dc)) * 0.9);
        col = mix(paper, ink, clamp(k, 0.0, 1.0));
        // Velvet: deep blacks carry a warm brown.
        col = mix(col, ink * float3(1.15, 1.0, 0.85), smoothstep(0.85, 1.0, k) * 0.4);
    } else {
        // ---- 5. Etching with aquatint: bitten lines (uneven, blobby) and resin-grain tone in flat stopped-out stages.
        // Resin grain: rosin dust melted on the plate protects tiny islands - a net of white islands in bitten ground
        // (Worley cells: the island is the cell's middle, the bitten net its edges).
        float2 rc = floor(p / 3.2);
        float f1 = 9.0; float f2 = 9.0;
        for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
            float2 gc = rc + float2(float(i), float(j));
            float dd = length(p - (gc + float2(hash(gc), hash(gc + 3.1))) * 3.2);
            if (dd < f1) { f2 = f1; f1 = dd; } else if (dd < f2) { f2 = dd; }
        }
        float resin = clamp((f2 - f1) / 2.2, 0.0, 1.0);
        // Aquatint stages, each stopped out with varnish before the next bite: the open sea darkest, a lighter stage
        // along the shore, a white rim at the coast; the land one pale bite in patches.
        float edgeWob = 22.0 * (fbm(p / 110.0) - 0.5);
        float seaStage = 0.2 + 0.18 * step(40.0 + edgeWob, dL) + 0.14 * step(130.0 + edgeWob, dL);
        float landStage = 0.1 * step(0.63, fbm(p / 150.0)) * isLand;
        float aqTone = mix(seaStage, landStage, isLand);
        float aq = smoothstep(resin - 0.07, resin + 0.07, aqTone * 1.25);
        k = aq * 0.85;
        k = mix(k, 0.0, (1.0 - smoothstep(2.0, 6.0, dL)) * (1.0 - isLand));
        // River: a bitten line each side.
        k = max(k, clamp(river * (1.0 - river) * 3.6, 0.0, 1.0) * 0.9);
        // Mountains: etched hatching along the form, the needle's line swelling and blotting where the acid bit too long.
        float tone = clamp((1.0 - lm) * 1.1, 0.0, 1.0);
        float bite = 0.85 + 0.35 * noise(p / 6.0);
        float h1 = screen(dot(p, tang) + 2.5 * wob, 4.6, tone * 0.8 * bite, 0.12);
        float h2 = screen(dot(p, grad) + 2.5 * wob, 5.4, (tone - 0.45) * bite, 0.12) * step(0.45, tone);
        float foul = step(0.992, hash(floor(p / 2.0))) * step(0.4, tone);
        float maq = smoothstep(resin - 0.12, resin + 0.12, tone * 0.5) * 0.5;
        k = mix(k, max(max(h1, h2), max(foul, maq)), ma);
        k = max(k, contour);
        k = max(k, 1.0 - smoothstep(1.0, 2.2, dc + 0.8 * wob));
        col = mix(paper, ink, clamp(k, 0.0, 1.0));
        // Plate tone: ink left on the plate, wiped unevenly, darker toward the edges and in streaks.
        float wipe = fbm(float2(p.x / 400.0, p.y / 90.0));
        col *= 1.0 - 0.08 * smoothstep(0.3, 0.8, wipe);
    }

    // ---- The sheet: paper tone, platemark for intaglio prints, a little grain.
    col *= 0.96 + 0.06 * fbm(p / 260.0);
    if (plate > 0.0) {
        float2 qd = abs(p - size * 0.5) - (size * 0.5 - 52.0);
        float sdf = max(qd.x, qd.y);
        // Outside the plate the paper is untouched (no ink, a little brighter, embossed edge).
        float outside = step(0.0, sdf);
        col = mix(col, paper * 1.02, outside);
        float ridge = 1.0 - smoothstep(0.0, 2.5, abs(sdf));
        float side = (p.x < size.x * 0.5 ? 1.0 : -1.0) * step(abs(qd.y), abs(qd.x)) + (p.y < size.y * 0.5 ? 1.0 : -1.0) * step(abs(qd.x), abs(qd.y));
        col *= 1.0 + plate * ridge * 0.07 * side;
        col *= 1.0 - plate * 0.03 * (1.0 - outside);
    }
    col += (hash(floor(p)) - 0.5) * 0.02;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> PrintEffect = new(() =>
        SKRuntimeEffect.CreateShader(PrintSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader PrintShader(SKImage mountains, SKImage geo, SKImage tensor, Print print)
    {
        var builder = new SKRuntimeShaderBuilder(PrintEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        using var cMount = mountains.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cGeo = geo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cTensor = tensor.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        builder.Children["mount"] = cMount; builder.Children["geo"] = cGeo; builder.Children["tensor"] = cTensor;
        builder.Uniforms["variant"] = (float)print.Variant;
        builder.Uniforms["size"] = new float[] { W, H };
        builder.Uniforms["paper"] = Stylize.Hex(print.Paper); builder.Uniforms["ink"] = Stylize.Hex(print.Ink);
        builder.Uniforms["plate"] = print.Plate;
        return builder.Build();
    }

    /// <summary>A banknote-like guilloche band around the map: interlaced sine ribbons drawn by a "geometric lathe".</summary>
    private static void DrawGuillocheBorder(SKCanvas canvas)
    {
        var ink = new SKColor(0x1b, 0x22, 0x30);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = ink.WithAlpha(210), StrokeWidth = 0.8f };
        var inner = SKRect.Create(96, 96, W - 192, H - 192);
        var outer = SKRect.Create(64, 64, W - 128, H - 128);
        using var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = ink, StrokeWidth = 1.6f };
        canvas.DrawRect(outer, frame);
        canvas.DrawRect(inner, frame);
        // Ribbons along each side between the two frames, eight phase-shifted sines.
        for (var r = 0; r < 8; r++)
        {
            var phase = r * Math.PI / 8;
            foreach (var (from, to, horizontal, centre) in new[]
            {
                (64f, W - 64f, true, 80f), (64f, W - 64f, true, H - 80f), (64f, H - 64f, false, 80f), (64f, H - 64f, false, W - 80f),
            })
            {
                using var path = new SKPath();
                for (var s = from; s <= to; s += 1.5f)
                {
                    var o = (float)(10 * Math.Sin(s / 11.0 + phase) * Math.Cos(s / 97.0));
                    var pt = horizontal ? new SKPoint(s, centre + o) : new SKPoint(centre + o, s);
                    if (s == from) path.MoveTo(pt); else path.LineTo(pt);
                }
                canvas.DrawPath(path, line);
            }
        }
    }
}
