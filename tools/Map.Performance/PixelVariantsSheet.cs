using Map.Core.Documents;
using SkiaSharp;
using static Map.Performance.WinterVariantsSheet;

namespace Map.Performance;

/// <summary>
/// R09 «Пиксельный атлас» (`--profile pixel-variants-sheet`): five pixel-art looks of the same small map with the owner's
/// mountains, drawn by the document renderer and then rebuilt on a coarse pixel grid. They differ in how the pixels are
/// made: a 16-colour fantasy palette with ordered dithering, outlines and wave glyphs; a four-green handheld screen; the
/// map's own colours reduced to 24 with error diffusion (a soft 16-bit look); console blocks - three colours per region
/// under a black outline with checker dithering; one bit - black and white, dither patterns, ruled sea. Names stay as they
/// are. Writes <c>&lt;output&gt;.pixel-&lt;id&gt;.jpg</c> and <c>…-zoom.jpg</c> (the mountains at full size).
/// </summary>
internal static class PixelVariantsSheet
{
    private static readonly int[] Bayer4 = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <summary>The map on the pixel grid: colour, land and mountain coverage per cell.</summary>
    private sealed record Grid(int Cols, int Rows, int Cell, float[] R, float[] G, float[] B, float[] Land, float[] Mountain);

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        var (document, source, images) = SampleMap();
        var bare = document.WithLayers([.. document.Layers.Select(l => l.Name == "Подписи" ? l with { Visible = false } : l)]);
        using var full = RenderMap(bare, source, SeaColor);
        using var mountains = RenderLayers(document, source, l => l.Name == "Горы", stampsOnly: true);
        using var land = RenderLayers(document, source, l => l is LandLayer);
        using var names = RenderLayers(document, source, l => l.Name == "Подписи");
        foreach (var image in images.Values) image.Dispose();
        var (r, g, b, _) = Channels(full);
        var (_, _, _, ma) = Channels(mountains);
        var (_, _, _, la) = Channels(land);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

        var looks = new (string Id, int Cell, Func<Grid, uint[]> Paint)[]
        {
            ("fantasy16", 6, Fantasy16),
            ("handheld", 6, Handheld),
            ("soft16bit", 6, Soft16Bit),
            ("console", 7, Console),
            ("onebit", 3, OneBit),
        };
        foreach (var (id, cell, paint) in looks)
        {
            var grid = Downsample(r, g, b, la, ma, cell);
            var colours = paint(grid);
            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            using (var pixels = Upscale(grid, colours)) surface.Canvas.DrawImage(pixels, 0, 0);
            surface.Canvas.DrawImage(names, 0, 0);
            using var sheet = surface.Snapshot();
            Save(report, options, "pixel-" + id, sheet, 1400, null);
            Save(report, options, "pixel-" + id + "-zoom", sheet, 1400, new SKRectI(380, 170, 1780, 1045));
        }
        report.GatePassed = report.Images.Count == looks.Length * 2;
        return report;
    }

    // ------------------------------------------------------------------ 1 · Фэнтези, 16 цветов

    /// <summary>A 16-colour fantasy palette, ordered (Bayer) dithering, a dark outline round the land and the mountains,
    /// small wave glyphs on the open sea.</summary>
    private static uint[] Fantasy16(Grid g)
    {
        uint[] palette = [0x1a1c2c, 0x5d275d, 0xb13e53, 0xef7d57, 0xffcd75, 0xa7f070, 0x38b764, 0x257179,
                          0x29366f, 0x3b5dc9, 0x41a6f6, 0x73eff7, 0xf4f4f4, 0x94b0c2, 0x566c86, 0x333c57];
        var o = Dithered(g, palette, 48);
        Outline(g, o, 0x1a1c2c, mountains: true, coast: true);
        Waves(g, o, 0x73eff7, 0.035f);
        return o;
    }

    // ------------------------------------------------------------------ 2 · Карманная консоль

    /// <summary>Four greens of an old handheld screen by luminance, a 2×2 dither between them, the darkest as the outline.</summary>
    private static uint[] Handheld(Grid g)
    {
        uint[] shades = [0x0f380f, 0x306230, 0x8bac0f, 0x9bbc0f];
        var o = new uint[g.Cols * g.Rows];
        for (var y = 0; y < g.Rows; y++)
            for (var x = 0; x < g.Cols; x++)
            {
                var i = y * g.Cols + x;
                var l = Lum(g, i) * 3 + ((x + y) % 2 == 0 ? .25f : -.25f);
                o[i] = shades[Math.Clamp((int)MathF.Round(l), 0, 3)];
            }
        Outline(g, o, shades[0], mountains: true, coast: true);
        return o;
    }

    // ------------------------------------------------------------------ 3 · Мягкие 16 бит

    /// <summary>The map's own colours reduced to 16 (median cut), spread by error diffusion: a soft 16-bit picture of the
    /// painting, no outline.</summary>
    private static uint[] Soft16Bit(Grid g)
    {
        var palette = MedianCut(g, 16);
        var n = g.Cols * g.Rows;
        float[] r = (float[])g.R.Clone(), gg = (float[])g.G.Clone(), b = (float[])g.B.Clone();
        var o = new uint[n];
        for (var y = 0; y < g.Rows; y++)
            for (var x = 0; x < g.Cols; x++)
            {
                var i = y * g.Cols + x;
                var c = Nearest(palette, r[i], gg[i], b[i]);
                o[i] = c;
                float er = r[i] - ((c >> 16) & 255) / 255f, eg = gg[i] - ((c >> 8) & 255) / 255f, eb = b[i] - (c & 255) / 255f;
                void Push(int dx, int dy, float w)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= g.Cols || ny >= g.Rows) return;
                    var j = ny * g.Cols + nx;
                    r[j] += er * w; gg[j] += eg * w; b[j] += eb * w;
                }
                Push(1, 0, 7 / 16f); Push(-1, 1, 3 / 16f); Push(0, 1, 5 / 16f); Push(1, 1, 1 / 16f);
            }
        return o;
    }

    // ------------------------------------------------------------------ 4 · Приставка

    /// <summary>Three colours per region - sea, land, mountains - by luminance, a checker between steps, and a black
    /// outline one cell thick round the land and every mountain.</summary>
    private static uint[] Console(Grid g)
    {
        uint[] sea = [0x1b2a6b, 0x2f5bd1, 0x7aa8f0], ground = [0x3a6b1e, 0x7bb33a, 0xd8e08a], rock = [0x4a4a52, 0x9a9aa2, 0xf0f0f0];
        var o = new uint[g.Cols * g.Rows];
        for (var y = 0; y < g.Rows; y++)
            for (var x = 0; x < g.Cols; x++)
            {
                var i = y * g.Cols + x;
                var set = g.Mountain[i] > .5f ? rock : g.Land[i] > .5f ? ground : sea;
                var l = Lum(g, i);
                var level = g.Mountain[i] > .5f ? l * 2.6f - .2f : g.Land[i] > .5f ? l * 2.2f - .4f : l * 2.4f - .5f;
                level += (x + y) % 2 == 0 ? .25f : -.25f;
                o[i] = set[Math.Clamp((int)MathF.Round(level), 0, 2)];
            }
        Outline(g, o, 0x000000, mountains: true, coast: true);
        Waves(g, o, 0x7aa8f0, .02f);
        return o;
    }

    // ------------------------------------------------------------------ 5 · Один бит

    /// <summary>Black and white: land and mountains by a 4×4 dither of their luminance, the sea ruled in horizontal lines
    /// that close up away from the shore, a black outline.</summary>
    private static uint[] OneBit(Grid g)
    {
        var o = new uint[g.Cols * g.Rows];
        var shore = ShoreDistance(g);
        for (var y = 0; y < g.Rows; y++)
            for (var x = 0; x < g.Cols; x++)
            {
                var i = y * g.Cols + x;
                bool black;
                if (g.Land[i] > .5f || g.Mountain[i] > .5f)
                {
                    var t = (Bayer4[(y % 4) * 4 + x % 4] + .5f) / 16f;
                    black = Lum(g, i) * 1.15f - .05f < t;
                }
                else
                {
                    // A dense band hugging the shore, then sparse rules fading out to open sea.
                    var d = shore[i];
                    var every = d < 4 ? 2 : d < 12 ? 4 : 6;
                    black = y % every == 0 && d > 1 && d < 40;
                }
                o[i] = black ? 0x111111u : 0xf2f0e8u;
            }
        Outline(g, o, 0x111111, mountains: true, coast: true);
        return o;
    }

    // ------------------------------------------------------------------ the grid

    private static Grid Downsample(float[] r, float[] g, float[] b, float[] land, float[] mountain, int cell)
    {
        int cols = W / cell, rows = H / cell, n = cols * rows;
        float[] gr = new float[n], gg = new float[n], gb = new float[n], gl = new float[n], gm = new float[n];
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
            {
                float sr = 0, sg = 0, sb = 0, sl = 0, sm = 0;
                for (var dy = 0; dy < cell; dy++)
                    for (var dx = 0; dx < cell; dx++)
                    {
                        var i = (y * cell + dy) * W + x * cell + dx;
                        sr += r[i]; sg += g[i]; sb += b[i]; sl += land[i]; sm += mountain[i];
                    }
                var k = 1f / (cell * cell);
                var j = y * cols + x;
                gr[j] = sr * k; gg[j] = sg * k; gb[j] = sb * k; gl[j] = sl * k; gm[j] = sm * k;
            }
        return new Grid(cols, rows, cell, gr, gg, gb, gl, gm);
    }

    private static SKImage Upscale(Grid g, uint[] colours)
    {
        var bytes = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                int cx = Math.Min(x / g.Cell, g.Cols - 1), cy = Math.Min(y / g.Cell, g.Rows - 1);
                var c = colours[cy * g.Cols + cx];
                var i = (y * W + x) * 4;
                bytes[i] = (byte)(c >> 16); bytes[i + 1] = (byte)(c >> 8); bytes[i + 2] = (byte)c; bytes[i + 3] = 255;
            }
        using var data = SKData.CreateCopy(bytes);
        return SKImage.FromPixels(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul), data, W * 4);
    }

    private static float Lum(Grid g, int i) => .3f * g.R[i] + .59f * g.G[i] + .11f * g.B[i];

    /// <summary>Nearest palette colour after an ordered (Bayer 4×4) offset of the given spread (0..255).</summary>
    private static uint[] Dithered(Grid g, uint[] palette, float spread)
    {
        var o = new uint[g.Cols * g.Rows];
        for (var y = 0; y < g.Rows; y++)
            for (var x = 0; x < g.Cols; x++)
            {
                var i = y * g.Cols + x;
                // The sea is dithered lightly: a calm surface under the waves, the land and mountains fully.
                var amount = g.Land[i] > .5f || g.Mountain[i] > .5f ? spread : spread * .3f;
                var t = ((Bayer4[(y % 4) * 4 + x % 4] + .5f) / 16f - .5f) * amount / 255f;
                o[i] = Nearest(palette, g.R[i] + t, g.G[i] + t, g.B[i] + t);
            }
        return o;
    }

    private static uint Nearest(uint[] palette, float r, float g, float b)
    {
        var best = palette[0]; var bestD = float.MaxValue;
        foreach (var c in palette)
        {
            float dr = r - ((c >> 16) & 255) / 255f, dg = g - ((c >> 8) & 255) / 255f, db = b - (c & 255) / 255f;
            // Weighted as the eye sees: green counts most.
            var d = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>A dark line one cell thick where the land meets the sea and round every mountain.</summary>
    private static void Outline(Grid g, uint[] o, uint colour, bool mountains, bool coast)
    {
        var copy = (uint[])o.Clone();
        for (var y = 1; y < g.Rows - 1; y++)
            for (var x = 1; x < g.Cols - 1; x++)
            {
                var i = y * g.Cols + x;
                bool Edge(float[] f) => f[i] > .5f && (f[i - 1] <= .5f || f[i + 1] <= .5f || f[i - g.Cols] <= .5f || f[i + g.Cols] <= .5f);
                if ((coast && Edge(g.Land) && g.Mountain[i] <= .5f) || (mountains && Edge(g.Mountain))) copy[i] = colour;
            }
        Array.Copy(copy, o, o.Length);
    }

    /// <summary>Small wave glyphs (a three-cell arc) scattered on the open sea, away from the shore.</summary>
    private static void Waves(Grid g, uint[] o, uint colour, float density)
    {
        var shore = ShoreDistance(g);
        var random = new Random(20261113);
        for (var y = 1; y < g.Rows - 2; y++)
            for (var x = 1; x < g.Cols - 2; x++)
            {
                var i = y * g.Cols + x;
                if (shore[i] < 4 || random.NextDouble() > density) continue;
                // «~»: low, high, low.
                o[i] = colour; o[i - g.Cols + 1] = colour; o[i + 2] = colour;
            }
    }

    /// <summary>Cells from the nearest land cell (city-block distance), 0 on land.</summary>
    private static int[] ShoreDistance(Grid g)
    {
        var n = g.Cols * g.Rows;
        var d = new int[n];
        var queue = new Queue<int>();
        for (var i = 0; i < n; i++)
            if (g.Land[i] > .5f || g.Mountain[i] > .5f) { d[i] = 0; queue.Enqueue(i); } else d[i] = int.MaxValue;
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            int x = i % g.Cols, y = i / g.Cols;
            void Visit(int j) { if (d[j] > d[i] + 1) { d[j] = d[i] + 1; queue.Enqueue(j); } }
            if (x > 0) Visit(i - 1);
            if (x < g.Cols - 1) Visit(i + 1);
            if (y > 0) Visit(i - g.Cols);
            if (y < g.Rows - 1) Visit(i + g.Cols);
        }
        return d;
    }

    /// <summary>The palette of the map's own colours: boxes split along their widest channel at the median.</summary>
    private static uint[] MedianCut(Grid g, int count)
    {
        var all = Enumerable.Range(0, g.Cols * g.Rows).Select(i => (g.R[i], g.G[i], g.B[i])).ToList();
        var boxes = new List<List<(float R, float G, float B)>> { all };
        while (boxes.Count < count)
        {
            var box = boxes.OrderByDescending(Spread).First();
            if (box.Count < 2) break;
            boxes.Remove(box);
            var channel = Widest(box);
            box.Sort((a, b) => channel == 0 ? a.R.CompareTo(b.R) : channel == 1 ? a.G.CompareTo(b.G) : a.B.CompareTo(b.B));
            boxes.Add(box[..(box.Count / 2)]); boxes.Add(box[(box.Count / 2)..]);
        }
        return [.. boxes.Select(b =>
        {
            float r = b.Average(c => c.R), gg = b.Average(c => c.G), bb = b.Average(c => c.B);
            return ((uint)(r * 255) << 16) | ((uint)(gg * 255) << 8) | (uint)(bb * 255);
        })];

        static float Spread(List<(float R, float G, float B)> box) =>
            box.Count < 2 ? 0 : Math.Max(box.Max(c => c.R) - box.Min(c => c.R), Math.Max(box.Max(c => c.G) - box.Min(c => c.G), box.Max(c => c.B) - box.Min(c => c.B))) * MathF.Sqrt(box.Count);

        static int Widest(List<(float R, float G, float B)> box)
        {
            float r = box.Max(c => c.R) - box.Min(c => c.R), g = box.Max(c => c.G) - box.Min(c => c.G), b = box.Max(c => c.B) - box.Min(c => c.B);
            return r >= g && r >= b ? 0 : g >= b ? 1 : 2;
        }
    }
}
