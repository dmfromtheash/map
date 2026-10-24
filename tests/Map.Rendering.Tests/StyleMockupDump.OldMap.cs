using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R09 styles, round 4 (owner 2026-10-24: «старая карта — очень разные, интересные, реалистичные»):
/// five old-map traditions, each from how the real maps were made (research
/// docs/research/2026-10-24-r09-old-map-styles.md). MAP_STYLE_OLDMAP = output folder,
/// MAP_LIGHT_ASSETS as before. The map is rendered in three parts - the ground (land, sea,
/// river), the mountains alone, the names alone - and a coast distance field is computed from
/// the land; one medium pass per tradition paints the ground and the mountains, vector details
/// (wind rose, neatline) follow, and the names go on top unchanged.
/// </summary>
public sealed partial class StyleMockupDump
{
    private sealed record OldMap(int Variant, string Paper, string Ink, string A, string B, string C, string D,
        float Laid, float Foxing, float Folds, float Edge, float Plate);

    [Fact]
    public void Dump_old_map_variants_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("MAP_STYLE_OLDMAP");
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
            var land = document.Layers.OfType<LandLayer>().First().Id;
            using var ground = Render(document.WithLayer(labels, l => l with { Visible = false })
                .WithLayer(mountainsId, l => l with { Visible = false }), images);
            using var mountains = RenderOnly(document, mountainsId, images);
            using var names = RenderOnly(document, labels, images);
            using var landOnly = RenderOnly(document, land, images);
            using var riverOnly = RenderOnly(document, riversId, images);
            using var geo = CoastField(landOnly, riverOnly);
            using var mountainsOnWhite = OnWhite(mountains);
            using var tensor = Tensor(mountainsOnWhite);

            var variants = new (string Name, OldMap Map)[]
            {
                // A. Dutch copperplate atlas, hand-coloured (Blaeu, Ortelius, 1600-1650): laid rag paper, engraved
                // sea lines, verdigris outline colour along the coast with its brown halo, engraved mountains.
                ("Старая карта 1 · Атлас Блау, гравюра с раскраской", new(1, "#e6dab9", "#221c17", "#3f8f5e", "#6b7a45", "#d8a93c", "#7c9bb0", 1, 0.5f, 1, 0.6f, 1)),
                // B. Portolan chart on vellum (1300-1550): blank vellum sea, rhumb lines from wind roses, coloured island fringes.
                ("Старая карта 2 · Портолан на пергаменте", new(2, "#e4d3aa", "#5a3e26", "#2a2420", "#4f7a4a", "#b23a2a", "#c9a24a", 0, 0.25f, 0, 1, 0)),
                // C. Survey sheet (Cassini, Dufour, 1750-1860): wove paper, waterlines, down-slope hachures, no colour.
                ("Старая карта 3 · Съёмка Кассини, штрихи и линии воды", new(3, "#f1ebdd", "#2a231d", "#3e5b82", "#3a2c22", "#000000", "#000000", 0, 0.15f, 1, 0.3f, 0)),
                // D. Edo woodblock map (Ishikawa Ryusen, 1690-1800): washi, bokashi sea and waves, flat colour fields off register.
                ("Старая карта 4 · Японская гравюра на дереве", new(4, "#efe6cf", "#26221e", "#4e7fa6", "#a9c3d3", "#d9b26a", "#7fa36a", 0, 0.2f, 0, 0.5f, 0)),
                // E. 18th-c. manuscript estate or military map: laid paper, Prussian-blue wash pooled at the coast, sap green, iron-gall.
                ("Старая карта 5 · Рукописная акварель", new(5, "#ede4cc", "#6a4a2c", "#6f93ae", "#8aa05a", "#c2627c", "#d8b04a", 1, 0.6f, 0, 0.5f, 0)),
            };
            foreach (var (name, map) in variants)
            {
                using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
                var canvas = surface.Canvas;
                using (var shader = OldMapShader(ground, mountains, geo, tensor, map))
                using (var paint = new SKPaint { Shader = shader })
                    canvas.DrawRect(0, 0, W, H, paint);
                switch (map.Variant)
                {
                    case 1: DrawAtlasBorder(canvas); break;
                    case 2: DrawWindRose(canvas, new SKPoint(2250, 1170), 120); DrawWindRose(canvas, new SKPoint(330, 250), 80); break;
                    case 3: DrawNeatline(canvas); break;
                }
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

    private static SKImage OnWhite(SKImage image)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H))!;
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, 0, 0);
        return surface.Snapshot();
    }

    /// <summary>
    /// The coast field: R = distance to land from the sea, G = distance to the sea inside the land
    /// (pixels, up to 255), B = the river. Exact Euclidean distances (Felzenszwalb and Huttenlocher).
    /// </summary>
    private static SKImage CoastField(SKImage landOnly, SKImage riverOnly)
    {
        using var landBitmap = SKBitmap.FromImage(landOnly);
        using var riverBitmap = SKBitmap.FromImage(riverOnly);
        var land = new bool[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                land[y * W + x] = landBitmap.GetPixel(x, y).Alpha > 127;
        var toLand = Distance(land, true);
        var toSea = Distance(land, false);
        using var bitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                bitmap.SetPixel(x, y, new SKColor((byte)Math.Min(255, Math.Sqrt(toLand[i])), (byte)Math.Min(255, Math.Sqrt(toSea[i])),
                    riverBitmap.GetPixel(x, y).Alpha, 255));
            }
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Squared distance of every pixel to the nearest pixel whose land flag equals <paramref name="target"/>.</summary>
    private static double[] Distance(bool[] land, bool target)
    {
        const double Inf = 1e20;
        var grid = new double[W * H];
        for (var i = 0; i < grid.Length; i++) grid[i] = land[i] == target ? 0 : Inf;
        var line = new double[Math.Max(W, H)];
        var result = new double[Math.Max(W, H)];
        for (var x = 0; x < W; x++)
        {
            for (var y = 0; y < H; y++) line[y] = grid[y * W + x];
            Pass(line, result, H);
            for (var y = 0; y < H; y++) grid[y * W + x] = result[y];
        }
        for (var y = 0; y < H; y++)
        {
            Array.Copy(grid, y * W, line, 0, W);
            Pass(line, result, W);
            Array.Copy(result, 0, grid, y * W, W);
        }
        return grid;

        static void Pass(double[] f, double[] d, int n)
        {
            var v = new int[n];
            var z = new double[n + 1];
            var k = 0;
            v[0] = 0; z[0] = -Inf; z[1] = Inf;
            for (var q = 1; q < n; q++)
            {
                double s;
                while (true)
                {
                    s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                    if (s <= z[k] && k > 0) k--; else break;
                }
                if (s <= z[k]) { v[0] = q; z[0] = -Inf; z[1] = Inf; k = 0; continue; }
                k++; v[k] = q; z[k] = s; z[k + 1] = Inf;
            }
            k = 0;
            for (var q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                d[q] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
            }
        }
    }

    private const string OldMapSource = """
uniform shader ground;
uniform shader mount;
uniform shader geo;
uniform shader tensor;
uniform float variant;
uniform float2 size;
uniform float3 paper;
uniform float3 ink;
uniform float3 cA; uniform float3 cB; uniform float3 cC; uniform float3 cD;
uniform float laid;
uniform float foxing;
uniform float folds;
uniform float edgeWear;
uniform float plate;

float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 5; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

// Lines spaced ever wider from the coast: the k-th lies at s0 (k + a k^2). Returns the line's ink.
float waterlines(float d, float s0, float a, float count, float width) {
    if (d <= 0.5) return 0.0;
    float k = (-1.0 + sqrt(1.0 + 4.0 * a * d / s0)) / (2.0 * a);
    float spacing = s0 * (1.0 + 2.0 * a * k);
    float ph = fract(k);
    float px = min(ph, 1.0 - ph) * spacing;
    float on = 1.0 - smoothstep(width, width + 0.9, px);
    return on * step(k, count + 0.5) * (1.0 - 0.6 * k / (count + 1.0));
}

// Rhumb lines from one centre: 32 winds; returns ink and the wind's rank (0 = main, 1 = half, 2 = quarter).
float2 rhumb(float2 p, float2 c) {
    float2 v = p - c;
    float r = length(v);
    float a = atan(v.y, v.x);
    float step32 = 3.14159265 / 16.0;
    float k = floor(a / step32 + 0.5);
    float d = r * abs(sin(a - k * step32));
    float rank = mod(k, 4.0) == 0.0 ? 0.0 : (mod(k, 2.0) == 0.0 ? 1.0 : 2.0);
    return float2((1.0 - smoothstep(0.25, 1.0, d)) * step(18.0, r), rank);
}

half4 main(float2 p) {
    float4 g = geo.eval(p);
    float dL = g.r * 255.0;             // distance to land, in the sea
    float dW = g.g * 255.0;             // distance to the sea, on land
    float isLand = step(0.5, dW);
    // The test river runs on past the coast; on an old map it ends at its mouth.
    float river = g.b * smoothstep(0.5, 3.0, dW);
    float wob = (noise(p / 18.0) - 0.5) * 2.2 + (noise(p / 5.0) - 0.5) * 0.8;
    float4 m = mount.eval(p);
    float ma = m.a;
    float3 mc = ma > 0.001 ? m.rgb / ma : float3(1);
    float lm = lum(mc);

    // Structure tensor of the mountains: tangent of their forms and the down-slope direction.
    float4 t = tensor.eval(p);
    float E = t.r, G = t.g, F = t.b - 0.5;
    float ang = 0.5 * atan(2.0 * F, E - G + 1e-7);
    float2 grad = float2(cos(ang), sin(ang));
    float2 tang = float2(-grad.y, grad.x);

    // The owner's ink contour of the mountains.
    float around = 0.25 * (lum(mix(float3(1), mount.eval(p + float2(2, 0)).rgb / max(mount.eval(p + float2(2, 0)).a, 0.001), mount.eval(p + float2(2, 0)).a))
        + lum(mix(float3(1), mount.eval(p - float2(2, 0)).rgb / max(mount.eval(p - float2(2, 0)).a, 0.001), mount.eval(p - float2(2, 0)).a))
        + lum(mix(float3(1), mount.eval(p + float2(0, 2)).rgb / max(mount.eval(p + float2(0, 2)).a, 0.001), mount.eval(p + float2(0, 2)).a))
        + lum(mix(float3(1), mount.eval(p - float2(0, 2)).rgb / max(mount.eval(p - float2(0, 2)).a, 0.001), mount.eval(p - float2(0, 2)).a)));
    float lo = lum(mix(float3(1), mc, ma));
    float contour = smoothstep(0.45, 0.2, lo) * smoothstep(0.02, 0.12, around - lo);

    // One of the two distances is 0 on each side: their maximum is the distance to the coast.
    float dc = max(dL, dW);
    float coast = 1.0 - smoothstep(0.9, 2.1, dc);
    float3 col = paper;

    if (variant < 1.5) {
        // ---- A. Copperplate, hand-coloured.
        // Sea: horizontal engraved lines, a little heavier near the coast, the paper between them.
        float y = p.y + 1.2 * (noise(float2(p.x / 140.0, p.y / 9.0)) - 0.5);
        float hl = abs(fract(y / 5.0) - 0.5) * 5.0;
        float seaLine = (1.0 - smoothstep(0.35, 1.0, hl)) * (0.28 + 0.4 * exp(-dL / 60.0)) * (0.75 + 0.5 * noise(p / 7.0));
        float wl = waterlines(dL + wob * 0.6, 7.0, 0.35, 3.0, 0.45);
        float sea = clamp(seaLine * (1.0 - wl) + wl * 0.95, 0.0, 1.0) * (1.0 - isLand);
        col = mix(col, ink, sea);
        // Land: the paper, and a band of verdigris along the coast, darker where the wash pooled at its edge.
        float band = (1.0 - smoothstep(10.0, 22.0, dW + wob * 2.0)) * isLand;
        float pooled = (1.0 - smoothstep(0.0, 4.0, abs(dW - 17.0 + wob * 2.0))) * isLand;
        float grain = 0.8 + 0.4 * noise(p / 3.0);
        float3 green = mix(cA, cB, 0.55 + 0.3 * fbm(p / 90.0));
        col = mix(col, col * mix(float3(1), green * 1.35, 0.55), band * grain);
        col = mix(col, col * green * 1.1, pooled * 0.35);
        // Verdigris eats the paper: a brown halo just outside the green.
        float halo = (1.0 - smoothstep(18.0, 30.0, dW + wob * 3.0)) * smoothstep(14.0, 22.0, dW + wob * 3.0) * isLand;
        col = mix(col, col * float3(0.78, 0.64, 0.45), halo * 0.35);
        // River: blue wash with inked banks.
        col = mix(col, col * mix(float3(1), cD * 1.25, 0.7), river);
        col = mix(col, ink, clamp(river * (1.0 - river) * 3.2, 0.0, 1.0) * 0.8);
        // Mountains: the painting in engraved lines along its forms, crossing where it is dark, over a pale ochre-green wash.
        float tone = clamp((1.0 - lm) * 1.1, 0.0, 1.0);
        float2 n1 = float2(-tang.y, tang.x);
        float tri1 = abs(fract(dot(p, tang) / 3.6 + 0.15 * noise(p / 25.0)) - 0.5) * 2.0;
        float tri2 = abs(fract(dot(p, n1) / 4.2 + 0.15 * noise(p / 31.0)) - 0.5) * 2.0;
        float eng = (1.0 - smoothstep(tone * 0.95 - 0.12, tone * 0.95, tri1));
        eng = max(eng, (1.0 - smoothstep(tone - 0.62, tone - 0.5, tri2)) * step(0.5, tone));
        float3 mcol = paper * mix(float3(1), mix(cC, cB, 0.5) * 1.3, 0.35);
        mcol = mix(mcol, ink, eng * 0.92);
        col = mix(col, mcol, ma);
        col = mix(col, ink, contour);
        col = mix(col, ink, coast * 0.95);
    } else if (variant < 2.5) {
        // ---- B. Portolan on vellum. Rhumb lines from a hidden centre and sixteen points on a circle around it.
        float2 c0 = size * 0.5 + float2(0, -40);
        float best = 0.0; float rank = 0.0;
        float2 h0 = rhumb(p, c0);
        if (h0.x > best) { best = h0.x; rank = h0.y; }
        for (int i = 0; i < 16; i++) {
            float a = float(i) * 0.3926991;
            float2 r = rhumb(p, c0 + float2(cos(a), sin(a)) * 1020.0);
            if (r.x > best) { best = r.x; rank = r.y; }
        }
        float3 windCol = rank < 0.5 ? cA : (rank < 1.5 ? cB : cC);
        col = mix(col, windCol, best * mix(0.7, 0.18, isLand));
        // Islands' coloured fringe and the brown coast with small scallops.
        float fringe = (1.0 - smoothstep(5.0, 10.0, dW + wob)) * isLand;
        col = mix(col, col * mix(float3(1), cC * 1.6, 0.5), fringe);
        float scallop = (1.0 - smoothstep(0.5, 1.4, abs(dL - 2.2 * abs(sin(p.x / 7.0 + p.y / 9.0))))) * (1.0 - isLand);
        col = mix(col, ink, max(coast, scallop * 0.6) * 0.95);
        col = mix(col, col * mix(float3(1), cB * 1.5, 0.6), river);
        col = mix(col, ink, clamp(river * (1.0 - river) * 3.2, 0.0, 1.0) * 0.7);
        // Mountains: the painting in three tones of brown ink, a light green wash.
        float q3 = floor((1.0 - lm) * 3.0 + 0.5) / 3.0;
        float3 mcol = mix(paper * mix(float3(1), cB * 1.6, 0.25), ink, q3 * 0.85);
        col = mix(col, mcol, ma);
        col = mix(col, ink, contour);
        // Vellum: hair-follicle speckle, cockles, darker handled edges.
        float speck = step(0.985, hash(floor(p / 1.5))) * 0.25;
        col *= 1.0 - speck * 0.5;
        float cockle = fbm(p / 340.0);
        col *= 0.94 + 0.12 * cockle;
    } else if (variant < 3.5) {
        // ---- C. Survey sheet: waterlines in blue-grey, hachures down the slopes, no colour.
        float wl = waterlines(dL + wob * 0.5, 5.5, 0.22, 10.0, 0.42);
        col = mix(col, mix(ink, cA, 0.55), wl * (1.0 - isLand));
        // River: two inked banks over the paper with a faint blue tint, as surveyed.
        col = mix(col, col * mix(float3(1), cA * 2.0, 0.12), river);
        col = mix(col, ink, clamp(river * (1.0 - river) * 3.8, 0.0, 1.0));
        // Hachures: short strokes along the down-slope direction, side by side across it, thicker where steeper (darker).
        float tone = clamp((1.0 - lm) * 1.25 - 0.08, 0.0, 1.0);
        float across = dot(p, tang);
        float along = dot(p, grad);
        float row = floor(along / 9.0);
        float tri = abs(fract(across / 2.6 + hash(float2(row, 1.0)) ) - 0.5) * 2.0;
        float gap = smoothstep(0.08, 0.2, abs(fract(along / 9.0) - 0.5) * 2.0);
        float hach = (1.0 - smoothstep(tone * 0.9 - 0.15, tone * 0.9, tri)) * gap;
        float3 mcol = mix(paper, cB, hach * 0.95);
        col = mix(col, mcol, ma);
        col = mix(col, cB, contour * 0.95);
        col = mix(col, ink, coast);
    } else if (variant < 4.5) {
        // ---- D. Woodblock: bokashi sea graded from the coast, waves far out, flat colour plates a little off register.
        float bok = exp(-dL / 110.0);
        float3 sea = mix(mix(paper, cB, 0.55), cA, bok);
        // Seigaiha waves: rows of half-overlapping circles with concentric rings.
        float R = 34.0;
        float2 uv = p / R;
        float rowY = floor(uv.y);
        float2 cell = float2(uv.x + (mod(rowY, 2.0) * 0.5), uv.y);
        float2 cc = float2(floor(cell.x) + 0.5, rowY + 1.0);
        float rr = length(cell - cc);
        float2 cc2 = float2(floor(cell.x + 0.5), rowY + 1.5);
        float rings = abs(fract(rr * 3.5) - 0.5);
        float wave = (1.0 - smoothstep(0.08, 0.16, rings)) * step(rr, 1.0) * smoothstep(60.0, 160.0, dL);
        sea = mix(sea, cA * 0.8, wave * 0.55);
        col = mix(col, sea, 1.0 - isLand);
        // The land plate, printed a little off register (the colour does not quite meet the key line).
        float dWoff = geo.eval(p + float2(3.0, -2.0)).g * 255.0;
        float landPlate = step(0.5, dWoff);
        float grain = noise(float2(p.x / 70.0 + fbm(p / 200.0) * 3.0, p.y / 2.2));
        col = mix(col, cC * (0.94 + 0.12 * grain), landPlate * 0.85);
        col = mix(col, cA, river * 0.9);
        // Mountains: two greens and an ochre, flat, with the key line.
        float tone = 1.0 - lm;
        float3 mcol = tone < 0.33 ? mix(cC, paper, 0.3) : (tone < 0.6 ? cD : cD * 0.62);
        mcol *= 0.95 + 0.1 * grain;
        col = mix(col, mcol, step(0.5, ma));
        float key = smoothstep(0.35, 0.15, lo) * smoothstep(0.01, 0.08, around - lo);
        col = mix(col, ink, clamp(key * 1.4, 0.0, 1.0));
        float thick = 1.0 - smoothstep(1.8, 3.2, dc);
        col = mix(col, ink, thick);
        col *= 0.97 + 0.06 * noise(float2(p.x / 1.2, p.y / 30.0));
    } else {
        // ---- E. Manuscript watercolour: a Prussian-blue wash pooled at the coast, sap green on land, iron-gall line.
        float gran = 0.85 + 0.3 * noise(p / 2.2);
        // The wash dried unevenly: low clouds of pigment, cauliflower backruns with dark rims, a pooled edge.
        float clouds = fbm(p / 150.0);
        float bloom = 1.0 - smoothstep(0.0, 0.035, abs(fbm(p / 75.0 + 3.1) - 0.55));
        float washSea = (0.75 * exp(-dL / 55.0) + 0.55 * (1.0 - smoothstep(0.0, 7.0, dL)) + 0.12 * clouds) * gran
            * (0.7 + 0.6 * clouds) + 0.22 * bloom * exp(-dL / 120.0);
        col = mix(col, col * mix(float3(1), cA * 1.3, 0.85), clamp(washSea, 0.0, 1.0) * (1.0 - isLand));
        float washLand = (0.22 + 0.5 * smoothstep(0.35, 0.75, clouds) + 0.45 * (1.0 - smoothstep(0.0, 8.0, dW))) * gran * isLand
            + 0.25 * bloom * isLand;
        col = mix(col, col * mix(float3(1), cB * 1.4, 0.8), clamp(washLand, 0.0, 1.0));
        // A second, warmer wash of gamboge here and there over the green: fields.
        float field = smoothstep(0.58, 0.68, fbm(p / 210.0 + 7.0)) * isLand * (1.0 - ma);
        col = mix(col, col * mix(float3(1), cD * 1.25, 0.6), field * 0.6 * gran);
        col = mix(col, col * mix(float3(1), cA * 1.2, 0.85), river);
        // Mountains: a bistre wash over the drawing, the line redrawn in iron-gall brown, a pencil underdrawing beside it.
        float3 bistre = float3(0.62, 0.48, 0.32);
        float3 mcol = paper * mix(float3(1), bistre, (1.0 - lm) * 0.95);
        col = mix(col, mcol, ma);
        float4 m2 = mount.eval(p + float2(2.5, 1.8));
        float lo2 = lum(mix(float3(1), m2.a > 0.001 ? m2.rgb / m2.a : float3(1), m2.a));
        float pencil = smoothstep(0.45, 0.2, lo2) * 0.35;
        col = mix(col, float3(0.42, 0.42, 0.44), pencil * (1.0 - contour));
        col = mix(col, ink, contour * 0.9);
        col = mix(col, ink, coast * 0.85);
        col = mix(col, ink, clamp(river * (1.0 - river) * 3.2, 0.0, 1.0) * 0.6);
    }

    // ---- The sheet.
    float stain = fbm(p / 260.0);
    col *= 0.94 + 0.1 * stain;
    if (laid > 0.0) {
        float f = fract((p.x + 4.0 * noise(float2(0.0, p.y / 120.0))) / 96.0);
        float chain = 1.0 - smoothstep(0.6, 2.2, min(f, 1.0 - f) * 96.0);
        float laidLines = 0.5 + 0.5 * cos(6.2832 * fract(p.y / 3.6 + 0.3 * noise(p / 40.0)));
        col *= 1.0 + laid * (0.03 * chain + 0.018 * (laidLines - 0.5));
    }
    if (foxing > 0.0) {
        float2 cell = floor(p / 70.0);
        float best = 1e9; float rnd = 0.0; float rnd2 = 0.0;
        for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
            float2 gc = cell + float2(float(i), float(j));
            float2 centre = (gc + float2(hash(gc), hash(gc + 5.3))) * 70.0;
            float dist = length(p - centre);
            if (dist < best) { best = dist; rnd = hash(gc + 9.1); rnd2 = hash(gc + 2.7); }
        }
        float gate = smoothstep(0.45, 0.75, fbm(p / 420.0));
        float on = step(rnd, 0.12 * foxing * (0.3 + gate));
        float r = 2.0 + rnd2 * 7.0;
        float core = 1.0 - smoothstep(r * 0.35, r, best);
        float halo = 1.0 - smoothstep(r, r * 2.4, best);
        col = mix(col, col * float3(0.62, 0.44, 0.25), on * core * 0.6);
        col = mix(col, col * float3(0.86, 0.76, 0.6), on * halo * 0.35);
    }
    if (folds > 0.0) {
        float dx = (p.x - size.x * 0.5) / 2.5; float dy = (p.y - size.y * 0.5) / 2.5;
        float crease = max(exp(-dx * dx), exp(-dy * dy) * 0.8);
        float lit = max(exp(-(dx + 1.6) * (dx + 1.6)), exp(-(dy + 1.6) * (dy + 1.6)) * 0.8);
        col *= 1.0 - folds * (0.12 * crease - 0.05 * lit);
        float soil = max(exp(-dx * dx / 400.0), exp(-dy * dy / 400.0));
        col *= 1.0 - folds * 0.04 * soil;
    }
    if (plate > 0.0) {
        float2 qd = abs(p - size * 0.5) - (size * 0.5 - 46.0);
        float sdf = max(qd.x, qd.y);
        col *= 1.0 - plate * 0.03 * step(sdf, 0.0);
        float ridge = 1.0 - smoothstep(0.0, 2.2, abs(sdf));
        float side = (p.x < size.x * 0.5 ? 1.0 : -1.0) * step(abs(qd.y), abs(qd.x)) + (p.y < size.y * 0.5 ? 1.0 : -1.0) * step(abs(qd.x), abs(qd.y));
        col *= 1.0 + plate * ridge * 0.06 * side;
    }
    float e = min(min(p.x, size.x - p.x), min(p.y, size.y - p.y));
    float wear = (1.0 - smoothstep(0.0, 200.0, e + 60.0 * (fbm(p / 90.0) - 0.5))) * edgeWear;
    col = mix(col, col * float3(0.72, 0.58, 0.40), wear * 0.55);
    col += (hash(floor(p)) - 0.5) * 0.025;
    return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> OldMapEffect = new(() =>
        SKRuntimeEffect.CreateShader(OldMapSource, out var errors) ?? throw new InvalidOperationException(errors));

    private static SKShader OldMapShader(SKImage ground, SKImage mountains, SKImage geo, SKImage tensor, OldMap map)
    {
        var builder = new SKRuntimeShaderBuilder(OldMapEffect.Value);
        var linear = new SKSamplingOptions(SKFilterMode.Linear);
        using var cGround = ground.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cMount = mountains.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var cGeo = geo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        using var cTensor = tensor.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, linear);
        builder.Children["ground"] = cGround; builder.Children["mount"] = cMount;
        builder.Children["geo"] = cGeo; builder.Children["tensor"] = cTensor;
        builder.Uniforms["variant"] = (float)map.Variant;
        builder.Uniforms["size"] = new float[] { W, H };
        builder.Uniforms["paper"] = Stylize.Hex(map.Paper); builder.Uniforms["ink"] = Stylize.Hex(map.Ink);
        builder.Uniforms["cA"] = Stylize.Hex(map.A); builder.Uniforms["cB"] = Stylize.Hex(map.B);
        builder.Uniforms["cC"] = Stylize.Hex(map.C); builder.Uniforms["cD"] = Stylize.Hex(map.D);
        builder.Uniforms["laid"] = map.Laid; builder.Uniforms["foxing"] = map.Foxing; builder.Uniforms["folds"] = map.Folds;
        builder.Uniforms["edgeWear"] = map.Edge; builder.Uniforms["plate"] = map.Plate;
        return builder.Build();
    }

    /// <summary>The atlas border of a Dutch map: a double neatline and a degree scale of black and white blocks.</summary>
    private static void DrawAtlasBorder(SKCanvas canvas)
    {
        var ink = new SKColor(0x22, 0x1c, 0x17);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = ink, StrokeWidth = 2.2f };
        var outer = SKRect.Create(64, 64, W - 128, H - 128);
        var inner = SKRect.Create(84, 84, W - 168, H - 168);
        canvas.DrawRect(outer, line);
        line.StrokeWidth = 1.4f;
        canvas.DrawRect(inner, line);
        using var block = new SKPaint { IsAntialias = true, Color = ink.WithAlpha(235) };
        for (var x = 84f; x < W - 84; x += 64)
            if (((int)((x - 84) / 64)) % 2 == 0)
            {
                canvas.DrawRect(SKRect.Create(x, 66, Math.Min(64, W - 84 - x), 16), block);
                canvas.DrawRect(SKRect.Create(x, H - 82, Math.Min(64, W - 84 - x), 16), block);
            }
        for (var y = 84f; y < H - 84; y += 64)
            if (((int)((y - 84) / 64)) % 2 == 0)
            {
                canvas.DrawRect(SKRect.Create(66, y, 16, Math.Min(64, H - 84 - y)), block);
                canvas.DrawRect(SKRect.Create(W - 82, y, 16, Math.Min(64, H - 84 - y)), block);
            }
    }

    /// <summary>A portolan wind rose: sixteen points in gold, black, green and red, a fleur-de-lis to the north.</summary>
    private static void DrawWindRose(SKCanvas canvas, SKPoint c, float r)
    {
        var gold = new SKColor(0xc9, 0xa2, 0x4a); var black = new SKColor(0x2a, 0x24, 0x20);
        var green = new SKColor(0x4f, 0x7a, 0x4a); var red = new SKColor(0xb2, 0x3a, 0x2a);
        using var fill = new SKPaint { IsAntialias = true };
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = black };
        for (var pass = 2; pass >= 0; pass--)
            for (var i = 0; i < 16; i++)
            {
                var rank = i % 4 == 0 ? 0 : i % 2 == 0 ? 1 : 2;
                if (rank != pass) continue;
                var a = i * Math.PI / 8 - Math.PI / 2;
                var len = r * (rank == 0 ? 1f : rank == 1 ? 0.72f : 0.5f);
                var w = r * (rank == 0 ? 0.16f : 0.12f);
                var tip = new SKPoint(c.X + (float)Math.Cos(a) * len, c.Y + (float)Math.Sin(a) * len);
                var l = new SKPoint(c.X + (float)Math.Cos(a - Math.PI / 2) * w, c.Y + (float)Math.Sin(a - Math.PI / 2) * w);
                var rr = new SKPoint(c.X + (float)Math.Cos(a + Math.PI / 2) * w, c.Y + (float)Math.Sin(a + Math.PI / 2) * w);
                using var half1 = new SKPath(); half1.MoveTo(c); half1.LineTo(l); half1.LineTo(tip); half1.Close();
                using var half2 = new SKPath(); half2.MoveTo(c); half2.LineTo(rr); half2.LineTo(tip); half2.Close();
                var color = rank == 0 ? (i % 8 == 0 ? gold : black) : rank == 1 ? green : red;
                fill.Color = color.WithAlpha(230); canvas.DrawPath(half1, fill);
                fill.Color = new SKColor((byte)(color.Red * 0.7), (byte)(color.Green * 0.7), (byte)(color.Blue * 0.7), 230); canvas.DrawPath(half2, fill);
                canvas.DrawPath(half1, edge); canvas.DrawPath(half2, edge);
            }
        canvas.DrawCircle(c, r * 0.1f, edge);
        // Fleur-de-lis to the north: three petals.
        using var lis = new SKPath();
        var n = new SKPoint(c.X, c.Y - r * 1.02f);
        lis.MoveTo(n.X, n.Y - r * 0.28f);
        lis.CubicTo(n.X + r * 0.1f, n.Y - r * 0.15f, n.X + r * 0.06f, n.Y - r * 0.02f, n.X, n.Y);
        lis.CubicTo(n.X - r * 0.06f, n.Y - r * 0.02f, n.X - r * 0.1f, n.Y - r * 0.15f, n.X, n.Y - r * 0.28f);
        lis.MoveTo(n.X - r * 0.02f, n.Y - r * 0.05f);
        lis.CubicTo(n.X - r * 0.2f, n.Y - r * 0.2f, n.X - r * 0.22f, n.Y - r * 0.02f, n.X - r * 0.08f, n.Y + r * 0.02f);
        lis.MoveTo(n.X + r * 0.02f, n.Y - r * 0.05f);
        lis.CubicTo(n.X + r * 0.2f, n.Y - r * 0.2f, n.X + r * 0.22f, n.Y - r * 0.02f, n.X + r * 0.08f, n.Y + r * 0.02f);
        fill.Color = gold; canvas.DrawPath(lis, fill); canvas.DrawPath(lis, edge);
    }

    /// <summary>The hairline neatline of a survey sheet.</summary>
    private static void DrawNeatline(SKCanvas canvas)
    {
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0x2a, 0x23, 0x1d), StrokeWidth = 3 };
        canvas.DrawRect(SKRect.Create(56, 56, W - 112, H - 112), line);
        line.StrokeWidth = 1;
        canvas.DrawRect(SKRect.Create(66, 66, W - 132, H - 132), line);
    }
}
