using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R09 «Зимний» (`--profile winter-variants-sheet`): five winter looks proposed to the owner before any code of the style
/// (owner 2026-11-11 «делай. а я скажу норм или нет по визуалу»). The same small map with the owner's mountains
/// (<see cref="OwnerMountainFixtures"/>) is drawn by the document renderer with the program's own frozen sea («Лёд»),
/// then redrawn in each medium from what the renderer gives: the value of every pixel (mountains from the owner's
/// painting, the snowfield from the relief, the ice), the mountains' outline and the coast. The looks differ in drawing:
/// engraved lines, washes with wet edges, flat gouache bands with long shadows, woodblock planes with key lines, charcoal
/// and chalk on toned paper. The names are drawn over it as they are. Writes <c>&lt;output&gt;.winter-&lt;id&gt;.jpg</c>
/// and <c>…-zoom.jpg</c> (the mountains at full size).
/// </summary>
internal static class WinterVariantsSheet
{
    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    private const int Width = 1600, Height = 1000;
    internal const string SeaColor = "#FF9DB7C2";
    private const float K = 1.5f;
    internal const int W = (int)(Width * K), H = (int)(Height * K);

    /// <summary>What every medium is drawn from.</summary>
    private sealed class Scene
    {
        public required float[] Value;     // darkness 0 (snow, ice) .. 1 (deepest shadow, open water)
        public required float[] Mountain;  // coverage of the owner's mountains
        public required float[] MountainL; // the mountains' own luminance
        public required float[] Land;      // land coverage
        public required float[] Outline;   // mountain silhouettes and the coast, 0..1
        public required float[] Gx, Gy;    // slope of the softened value (form direction)
    }

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        var (document, source, images) = SampleMap();
        var scene = Prepare(document, source);
        using var names = RenderLayers(document, source, l => l.Name == "Подписи");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

        var looks = new (string Id, Func<Scene, float[]> Paint, Action<SKCanvas>? Over)[]
        {
            ("engraving", Engraving, null),
            ("blizzard", Blizzard, c => Snowfall(c, 2600, 0.8f, 3.2f, 150, 20261111, streaks: true)),
            ("gouache", Gouache, null),
            ("woodblock", Woodblock, c => Snowfall(c, 900, 2.2f, 6.5f, 235, 20261112, streaks: false)),
            ("charcoal", Charcoal, null),
        };
        foreach (var (id, paint, over) in looks)
        {
            var rgb = paint(scene);
            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            using (var picture = FromRgb(rgb)) surface.Canvas.DrawImage(picture, 0, 0);
            surface.Canvas.DrawImage(names, 0, 0);
            over?.Invoke(surface.Canvas);
            using var sheet = surface.Snapshot();
            Save(report, options, "winter-" + id, sheet, 1400, null);
            Save(report, options, "winter-" + id + "-zoom", sheet, 1400, new SKRectI(380, 170, 1780, 1045));
        }
        // The chosen looks as the program's own styles (DEC-097): the same map with the style on, through the export renderer.
        foreach (var preset in new[] { MapStylePreset.WinterEngraving, MapStylePreset.WinterBlizzard, MapStylePreset.WinterWoodblock })
        {
            var styled = document with { Style = MapStyle.For(preset) with { Enabled = true } };
            using var sheet = RenderMap(styled, source, SeaColor);
            Save(report, options, "style-" + preset, sheet, 1400, null);
            Save(report, options, "style-" + preset + "-zoom", sheet, 1400, new SKRectI(380, 170, 1780, 1045));
        }
        report.GatePassed = report.Images.Count == looks.Length * 2 + 6;
        foreach (var image in images.Values) image.Dispose();
        return report;
    }

    // ------------------------------------------------------------------ what the renderer gives

    private static Scene Prepare(MapDocument document, IStampImageSource source)
    {
        // The whole map with the program's frozen sea, no names.
        var frozen = document.WithLayers([.. document.Layers
            .Select(l => l is BaseColorLayer b ? b with { Sea = SeaStyle.For(SeaManner.Ice) } : l)
            .Select(l => l.Name == "Подписи" ? l with { Visible = false } : l)]);
        using var full = RenderMap(frozen, source, SeaColor);
        using var mountains = RenderLayers(document, source, l => l.Name == "Горы", stampsOnly: true);
        using var land = RenderLayers(document, source, l => l is LandLayer);
        var (fr, fg, fb, _) = Channels(full);
        var (mr, mg, mb, ma) = Channels(mountains);
        var (_, _, _, la) = Channels(land);
        var n = W * H;
        var lum = new float[n];
        var mountainL = new float[n];
        for (var i = 0; i < n; i++)
        {
            lum[i] = .3f * fr[i] + .59f * fg[i] + .11f * fb[i];
            mountainL[i] = .3f * mr[i] + .59f * mg[i] + .11f * mb[i];
        }
        var soft = Blur(lum, 2);
        // The snowfield: everything on land darker than its bright ground gets tone (cliffs, river, road, plateau edge).
        var landValues = new List<float>();
        for (var i = 0; i < n; i += 7) if (la[i] > .9f && ma[i] < .05f) landValues.Add(soft[i]);
        landValues.Sort();
        var bright = landValues.Count > 0 ? landValues[(int)(landValues.Count * .9)] : .8f;
        var value = new float[n];
        var seaSoft = Blur(lum, 5);
        for (var i = 0; i < n; i++)
        {
            var onLand = Smooth(.10f, .42f, bright - soft[i]);
            var sea = Math.Clamp((.95f - seaSoft[i]) * 1.1f, 0, 1) * .8f;
            var ground = la[i] * onLand + (1 - la[i]) * sea;
            // The owner's mountains: their light sides are the snow, their dark ridges the shadow.
            var peak = Math.Clamp((.92f - mountainL[i]) * 1.25f, 0, 1);
            value[i] = ma[i] * peak + (1 - ma[i]) * ground;
        }
        var softValue = Blur(value, 3);
        var gx = new float[n];
        var gy = new float[n];
        for (var y = 1; y < H - 1; y++)
            for (var x = 1; x < W - 1; x++)
            {
                var i = y * W + x;
                gx[i] = (softValue[i + 1] - softValue[i - 1]) * .5f;
                gy[i] = (softValue[i + W] - softValue[i - W]) * .5f;
            }
        var outline = new float[n];
        var edgeM = Blur(ma, 1);
        var edgeL = Blur(la, 1);
        for (var y = 1; y < H - 1; y++)
            for (var x = 1; x < W - 1; x++)
            {
                var i = y * W + x;
                var m = MathF.Abs(edgeM[i + 1] - edgeM[i - 1]) + MathF.Abs(edgeM[i + W] - edgeM[i - W]);
                var c = MathF.Abs(edgeL[i + 1] - edgeL[i - 1]) + MathF.Abs(edgeL[i + W] - edgeL[i - W]);
                // The coast passes behind the mountains, not over them.
                outline[i] = Math.Clamp(Math.Max(m * 1.6f, c * 1.4f * (1 - ma[i])), 0, 1);
            }
        return new Scene { Value = value, Mountain = ma, MountainL = mountainL, Land = la, Outline = outline, Gx = gx, Gy = gy };
    }

    // ------------------------------------------------------------------ 1 · Гравюра по снегу

    /// <summary>The snow is the bare paper. Tone is cut in cold blue lines: one direction on the snowfield and the ice,
    /// a steeper one on the mountains, crossed where it is darkest; the lines swell with the tone and waver a little.
    /// The silhouettes and the coast are one firm line.</summary>
    private static float[] Engraving(Scene s)
    {
        var rgb = new float[W * H * 3];
        var wobble = Field(W, H, 5, [(90, .7f), (25, .3f)]);
        var grain = Field(W, H, 6, [(3, 1f)]);
        const float spacing = 5.2f;
        float[] paper = [.965f, .958f, .94f], ink = [.12f, .17f, .30f];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                var t = s.Value[i];
                var onMountain = s.Mountain[i];
                // Lines follow a fixed burin direction; on the mountains they run steeper, along the slopes.
                var a = Lerp(-.55f, 1.05f, onMountain);
                var proj = (x * MathF.Cos(a) + y * MathF.Sin(a)) / spacing + (wobble[i] - .5f) * 1.6f;
                var cover = Line(proj, Math.Clamp((t - .05f) * 1.05f, 0, .9f));
                if (t > .5f)
                {
                    var b = a + 1.25f;
                    var cross = (x * MathF.Cos(b) + y * MathF.Sin(b)) / spacing + (wobble[i] - .5f) * 1.2f;
                    cover = Math.Max(cover, Line(cross, (t - .5f) * 1.5f));
                }
                cover = Math.Max(cover, Smooth(.18f, .5f, s.Outline[i]));
                var g = .975f + .025f * grain[i];
                for (var c = 0; c < 3; c++) rgb[i * 3 + c] = Lerp(paper[c] * g, ink[c], cover * .94f);
            }
        return rgb;
    }

    /// <summary>Coverage of an engraved line of relative width <paramref name="width"/> (0..1 of the spacing) at a position
    /// measured in spacings, antialiased over about one pixel.</summary>
    private static float Line(float proj, float width)
    {
        if (width <= 0) return 0;
        var d = MathF.Abs(proj - MathF.Floor(proj) - .5f) * 5.2f; // pixels from the line's middle
        var half = width * 5.2f / 2;
        return Math.Clamp(half - d + .5f, 0, 1);
    }

    // ------------------------------------------------------------------ 2 · Акварель метели

    /// <summary>Three cold washes laid one over another, each with the darker rim a drying wash leaves, pigment settling in
    /// the paper's grain; a veil of driven snow over the map in uneven drifts. The flakes and streaks come after.</summary>
    private static float[] Blizzard(Scene s)
    {
        var rgb = new float[W * H * 3];
        var v = Blur(s.Value, 4);
        var washes = new float[W * H];
        for (var i = 0; i < washes.Length; i++)
            washes[i] = (Smooth(.10f, .16f, v[i]) + Smooth(.34f, .40f, v[i]) + Smooth(.62f, .68f, v[i])) / 3;
        // The rim: where a wash ends, pigment gathers.
        var spread = Blur(washes, 6);
        var granule = Field(W, H, 11, [(4, .6f), (2, .4f)]);
        var veil = Field(W, H, 12, [(320, .6f), (110, .4f)]);
        var paperGrain = Field(W, H, 13, [(6, 1f)]);
        float[] pigment = [.42f, .52f, .68f];
        for (var i = 0; i < washes.Length; i++)
        {
            var rim = Math.Clamp((washes[i] - spread[i]) * 3.5f, 0, .35f);
            var amount = Math.Clamp(washes[i] + rim, 0, 1) * (.86f + .28f * granule[i]);
            // The mountains keep a little of the owner's drawing through the wash.
            var detail = Lerp(1, .62f + .45f * s.MountainL[i], s.Mountain[i]);
            var fog = Smooth(.45f, .85f, veil[i]) * .42f;
            for (var c = 0; c < 3; c++)
            {
                var paper = .975f - .02f * paperGrain[i];
                var col = paper * (1 - amount * (1 - pigment[c])) * detail;
                rgb[i * 3 + c] = Lerp(col, .985f, fog);
            }
        }
        return rgb;
    }

    // ------------------------------------------------------------------ 3 · Гуашь северного дня

    /// <summary>Opaque bands: warm sunlit snow, cool light, ultramarine shadow, deep shadow. A low sun from the left lights
    /// the mountains' slopes and casts their long shadows over the snowfield; the ice is broken into floes on dark water.
    /// The band edges are dragged by the brush.</summary>
    private static float[] Gouache(Scene s)
    {
        var rgb = new float[W * H * 3];
        var strokes = Stretched(W, H, 21, .35f);
        // Long shadows: the mountains' coverage carried along the light, fading with the distance.
        const float dx = 3.2f, dy = 1.1f;
        var shadow = new float[W * H];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                float best = 0;
                for (var k = 1; k <= 70; k++)
                {
                    int sx = (int)(x - dx * k), sy = (int)(y - dy * k);
                    if (sx < 0 || sy < 0) break;
                    var m = s.Mountain[sy * W + sx];
                    if (m > .3f) { best = Math.Max(best, m * (1 - k / 76f)); break; }
                }
                shadow[y * W + x] = best;
            }
        shadow = Blur(shadow, 3);
        float[][] bands = [[.985f, .97f, .93f], [.80f, .86f, .95f], [.42f, .52f, .80f], [.15f, .21f, .47f]];
        float[] water = [.09f, .24f, .33f];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                // Light from the low left on the mountains' slopes (their value as height), on the ground the shadows.
                var lit = Math.Clamp(.5f - (s.Gx[i] * -.85f + s.Gy[i] * -.3f) * 26, 0, 1);
                var onMountain = s.Mountain[i];
                var value = 1 - s.Value[i];
                var tone = onMountain > .02f
                    ? Lerp(value, .55f * value + .45f * lit, onMountain)
                    : value - shadow[i] * .75f * s.Land[i];
                tone += (strokes[i] - .5f) * .07f;
                float[] col;
                if (s.Land[i] < .5f && onMountain < .1f)
                    col = s.Value[i] > .36f + (strokes[i] - .5f) * .08f ? water : tone > .7f ? [.88f, .93f, .97f] : bands[1];
                else col = tone > .74f ? bands[0] : tone > .52f ? bands[1] : tone > .3f ? bands[2] : bands[3];
                var texture = .975f + .05f * strokes[i];
                for (var c = 0; c < 3; c++) rgb[i * 3 + c] = Math.Clamp(col[c] * texture, 0, 1);
            }
        return rgb;
    }

    // ------------------------------------------------------------------ 4 · Японская гравюра зимой

    /// <summary>Flat planes cut from three blocks - snow, grey-blue, slate - with a thin key line round the mountains and
    /// the coast, the wood grain showing through, the sky-grey sea darkening towards the top (bokashi). Heavy flakes after.</summary>
    private static float[] Woodblock(Scene s)
    {
        var rgb = new float[W * H * 3];
        var grain = Stretched(W, H, 31, 0f);
        var fibre = Field(W, H, 32, [(3, 1f)]);
        var v = Blur(s.Value, 2);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                var value = 1 - v[i];
                float[] col;
                if (s.Land[i] < .5f && s.Mountain[i] < .1f)
                {
                    var sky = .78f + .22f * Smooth(0, H * .55f, y);
                    col = value > .66f ? [.84f * sky, .87f * sky, .88f * sky] : [.34f * sky, .40f * sky, .47f * sky];
                }
                else col = value > .70f ? [.94f, .94f, .92f] : value > .44f ? [.64f, .68f, .75f] : [.30f, .34f, .43f];
                var key = Smooth(.22f, .45f, s.Outline[i]);
                var wood = .965f + .05f * grain[i] + .015f * fibre[i];
                for (var c = 0; c < 3; c++) rgb[i * 3 + c] = Lerp(Math.Clamp(col[c] * wood, 0, 1), .2f, key * .9f);
            }
        return rgb;
    }

    // ------------------------------------------------------------------ 5 · Уголь и мел

    /// <summary>Grey toned paper: the dark is charcoal caught on the paper's tooth, the snow white chalk riding its tops,
    /// both laid in diagonal strokes; the silhouettes in a charcoal line.</summary>
    private static float[] Charcoal(Scene s)
    {
        var rgb = new float[W * H * 3];
        var tooth = Field(W, H, 41, [(2, .6f), (5, .4f)]);
        var strokes = Stretched(W, H, 42, .6f);
        float[] paper = [.57f, .56f, .54f];
        for (var i = 0; i < W * H; i++)
        {
            var value = 1 - s.Value[i] + (strokes[i] - .5f) * .12f;
            var dark = Smooth(.58f, .12f, value);
            var charcoal = Math.Clamp(dark * 1.35f - (1 - tooth[i]) * .55f, 0, 1);
            charcoal = Math.Max(charcoal, Smooth(.25f, .55f, s.Outline[i]) * (.7f + .3f * tooth[i]));
            var light = Smooth(.6f, .93f, value);
            var chalk = Math.Clamp(light * 1.25f - (1 - tooth[i]) * .7f, 0, 1);
            for (var c = 0; c < 3; c++)
            {
                var col = Lerp(paper[c], .97f, chalk * .92f);
                rgb[i * 3 + c] = Lerp(col, .1f, charcoal * .93f);
            }
        }
        return rgb;
    }

    // ------------------------------------------------------------------ snow over the sheet

    /// <summary>Falling snow: soft white flakes of random size, and for a blizzard faint streaks driven by the wind.</summary>
    private static void Snowfall(SKCanvas canvas, int count, float min, float max, byte alpha, int seed, bool streaks)
    {
        var random = new Random(seed);
        using var flake = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, .9f) };
        for (var k = 0; k < count; k++)
        {
            var r = min + (max - min) * MathF.Pow((float)random.NextDouble(), 2.2f);
            flake.Color = new SKColor(255, 255, 255, (byte)(alpha * (.55f + .45f * (float)random.NextDouble())));
            canvas.DrawCircle((float)random.NextDouble() * W, (float)random.NextDouble() * H, r, flake);
        }
        if (!streaks) return;
        using var streak = new SKPaint { IsAntialias = true, StrokeWidth = 1.4f, StrokeCap = SKStrokeCap.Round,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f) };
        for (var k = 0; k < 420; k++)
        {
            var x = (float)random.NextDouble() * W; var y = (float)random.NextDouble() * H;
            var len = 20 + 60 * (float)random.NextDouble();
            streak.Color = new SKColor(255, 255, 255, (byte)random.Next(40, 110));
            canvas.DrawLine(x, y, x + len, y + len * .32f, streak);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static (float[] R, float[] G, float[] B, float[] A) Channels(SKImage image)
    {
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        image.ReadPixels(bitmap.PeekPixels(), 0, 0);
        var bytes = bitmap.Bytes;
        float[] r = new float[W * H], g = new float[W * H], b = new float[W * H], a = new float[W * H];
        for (var i = 0; i < W * H; i++)
        {
            r[i] = bytes[i * 4] / 255f; g[i] = bytes[i * 4 + 1] / 255f; b[i] = bytes[i * 4 + 2] / 255f; a[i] = bytes[i * 4 + 3] / 255f;
        }
        return (r, g, b, a);
    }

    private static SKImage FromRgb(float[] rgb)
    {
        var bytes = new byte[W * H * 4];
        for (var i = 0; i < W * H; i++)
        {
            bytes[i * 4] = (byte)(Math.Clamp(rgb[i * 3], 0, 1) * 255);
            bytes[i * 4 + 1] = (byte)(Math.Clamp(rgb[i * 3 + 1], 0, 1) * 255);
            bytes[i * 4 + 2] = (byte)(Math.Clamp(rgb[i * 3 + 2], 0, 1) * 255);
            bytes[i * 4 + 3] = 255;
        }
        using var data = SKData.CreateCopy(bytes);
        return SKImage.FromPixels(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul), data, W * 4);
    }

    /// <summary>Three box blurs of the given radius (close to a Gaussian).</summary>
    internal static float[] Blur(float[] source, int radius)
    {
        var a = (float[])source.Clone();
        var b = new float[a.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            for (var y = 0; y < H; y++)
            {
                float sum = 0; var row = y * W;
                for (var x = -radius; x <= radius; x++) sum += a[row + Math.Clamp(x, 0, W - 1)];
                for (var x = 0; x < W; x++)
                {
                    b[row + x] = sum / (2 * radius + 1);
                    sum += a[row + Math.Min(x + radius + 1, W - 1)] - a[row + Math.Max(x - radius, 0)];
                }
            }
            for (var x = 0; x < W; x++)
            {
                float sum = 0;
                for (var y = -radius; y <= radius; y++) sum += b[Math.Clamp(y, 0, H - 1) * W + x];
                for (var y = 0; y < H; y++)
                {
                    a[y * W + x] = sum / (2 * radius + 1);
                    sum += b[Math.Min(y + radius + 1, H - 1) * W + x] - b[Math.Max(y - radius, 0) * W + x];
                }
            }
        }
        return a;
    }

    /// <summary>Value noise in octaves (cell size, weight), stretched to 0..1.</summary>
    internal static float[] Field(int width, int height, int seed, (int Cell, float Weight)[] octaves)
    {
        var random = new Random(seed);
        var field = new float[width * height];
        foreach (var (cell, weight) in octaves)
        {
            int gw = width / cell + 2, gh = height / cell + 2;
            var grid = new float[gw * gh];
            for (var i = 0; i < grid.Length; i++) grid[i] = (float)random.NextDouble();
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    float fx = (float)x / cell, fy = (float)y / cell;
                    int ix = (int)fx, iy = (int)fy; float tx = fx - ix, ty = fy - iy;
                    tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
                    var top = grid[iy * gw + ix] * (1 - tx) + grid[iy * gw + ix + 1] * tx;
                    var bottom = grid[(iy + 1) * gw + ix] * (1 - tx) + grid[(iy + 1) * gw + ix + 1] * tx;
                    field[y * width + x] += weight * (top * (1 - ty) + bottom * ty);
                }
        }
        float min = field.Min(), max = field.Max();
        for (var i = 0; i < field.Length; i++) field[i] = (field[i] - min) / (max - min);
        return field;
    }

    /// <summary>Brush or wood texture: noise stretched along an angle (long in that direction, fine across it).</summary>
    private static float[] Stretched(int width, int height, int seed, float angle)
    {
        var random = new Random(seed);
        const int along = 40, across = 3;
        var reach = width + height;
        int gw = reach / along + 8, gh = reach / across + 8;
        var grid = new float[gw * gh];
        for (var i = 0; i < grid.Length; i++) grid[i] = (float)random.NextDouble();
        var field = new float[width * height];
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                // Rotate into the stroke's frame, then sample a grid that is long along it.
                var u = (x * c + y * s + reach / 2f) / along + 2;
                var v = (-x * s + y * c + reach / 2f) / across + 2;
                int iu = (int)u, iv = (int)v; float tu = u - iu, tv = v - iv;
                iu = Math.Clamp(iu, 0, gw - 2); iv = Math.Clamp(iv, 0, gh - 2);
                tu = tu * tu * (3 - 2 * tu);
                var a = grid[iv * gw + iu] * (1 - tu) + grid[iv * gw + iu + 1] * tu;
                var b = grid[(iv + 1) * gw + iu] * (1 - tu) + grid[(iv + 1) * gw + iu + 1] * tu;
                field[y * width + x] = a * (1 - tv) + b * tv;
            }
        return field;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smooth(float from, float to, float v)
    {
        var t = Math.Clamp((v - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static SKImage RenderMap(MapDocument document, IStampImageSource source, string page)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(SKColor.Parse(page));
        surface.Canvas.Scale(K);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, Stamps = source, VisibleWorld = new WorldBounds(0, 0, Width, Height), ViewScale = K });
        return surface.Snapshot();
    }

    /// <summary>Only the chosen layers on a transparent sheet (the page hidden); the mountain pass can drop rivers and roads.</summary>
    internal static SKImage RenderLayers(MapDocument document, IStampImageSource source, Func<MapLayer, bool> keep, bool stampsOnly = false)
    {
        var only = document.WithLayers([.. document.Layers.Select(l =>
            l is BaseColorLayer ? l with { Visible = false }
            : !keep(l) ? l with { Visible = false }
            : stampsOnly && l is ObjectLayer o ? o with { Rivers = [], Roads = [] } : l)]);
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(K);
        SceneRenderer.Render(surface.Canvas, only, new RenderOptions { IsExport = true, Stamps = source, VisibleWorld = new WorldBounds(0, 0, Width, Height), ViewScale = K });
        return surface.Snapshot();
    }

    internal static void Save(AreaBeautySheetReport report, PerformanceOptions options, string key, SKImage sheet, int width, SKRectI? crop)
    {
        using var part = crop is { } c ? sheet.Subset(c) : null;
        var image = part ?? sheet;
        var height = (int)Math.Round(image.Height * (double)width / image.Width);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.DrawImage(image, SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var output = surface.Snapshot(); using var data = output.Encode(SKEncodedImageFormat.Jpeg, 90);
        var path = Path.ChangeExtension(options.OutputPath, "." + key + ".jpg");
        using (var file = File.Create(path)) data.SaveTo(file);
        report.Images.Add(path);
    }

    /// <summary>The sample map: an island with a plateau, a river, a road, the owner's mountains; the names on their own layer.</summary>
    internal static (MapDocument Document, IStampImageSource Source, Dictionary<string, SKImage> Images) SampleMap()
    {
        var images = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<MapAssetReference>();
        var shas = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            var png = File.ReadAllBytes(OwnerMountainFixtures.Path(i));
            var sha = Convert.ToHexStringLower(SHA256.HashData(png));
            using var bitmap = SKBitmap.Decode(png);
            images[sha] = SKImage.FromBitmap(bitmap); shas.Add(sha);
            assets.Add(new MapAssetReference(sha, OwnerMountainFixtures.Names[i - 1], "image/png", "png",
                ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "r09-winter", "Map", png));
        }
        var plain = WaterVariantsSheet.Land("Остров", 0,
            [(760, 560, 560, 330, .08, 5), (1385, 235, 95, 62, .12, 4), (1290, 800, 60, 44, .1, 3)],
            [(1010, 850, 200, 120, .1, 4)]);
        var plateau = WaterVariantsSheet.Land("Плато", 1, [(360, 440, 260, 190, .1, 5)], []);
        var random = new Random(20261110);
        var objects = new List<SceneObject>();
        foreach (var (x, y, w) in new (double, double, double)[]
                 { (520, 300, 230), (700, 330, 190), (860, 290, 210), (620, 420, 170), (790, 450, 200), (960, 420, 160),
                   (1080, 360, 180), (300, 330, 170), (380, 420, 140) })
        {
            var sha = shas[random.Next(shas.Count)]; var image = images[sha];
            var h = w * image.Height / image.Width;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", x - (w / 2), y - h, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) });
        }
        objects.Sort((a, b) => (a.Y + a.Height).CompareTo(b.Y + b.Height));
        VectorPoint[] riverPath = [new(760, 470), new(830, 560), new(900, 610), new(950, 690), new(1000, 760)];
        VectorPoint[] roadPath = [new(330, 640), new(500, 600), new(680, 640), new(860, 590), new(1060, 620), new(1220, 560)];
        (string Text, double X, double Y, double Size)[] names = [("Северные горы", 560, 120, 46), ("Плато Ветров", 250, 560, 30), ("Ледяная бухта", 940, 930, 30)];
        var river = new River { Name = "Река", Style = CurrentLook.River(), Channels = [RiverEditor.NewChannel(riverPath, 16)] };
        var road = RoadEditor.NewRoad(roadPath, 10, RoadStyle.Painted(), "Дорога", seed: 7);
        var labels = names.Select(n => new SceneObject(ObjectId.New(), SceneObjectKind.Text, n.Text, n.X - n.Text.Length * n.Size * .36, n.Y - n.Size,
            n.Text.Length * n.Size * .72 + n.Size, n.Size * 1.5, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle
            {
                Content = n.Text, FontSize = n.Size, RequestedFontFamily = "Cormorant Garamond", FallbackFontFamily = "Georgia",
                FontWeight = 700, FillColor = "#FF20242B", OutlineColor = "#00000000",
            },
        });
        var mountainLayer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects]) { Rivers = [river], Roads = [road] };
        var nameLayer = new ObjectLayer(LayerId.New(), "Подписи", true, false, 1, [.. labels]);
        var map = new MapDocument(DocumentId.New(), "Зимний", DateTimeOffset.UnixEpoch, new CanvasDefinition(Width, Height, "px", SeaColor),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, SeaColor) { Sea = SeaStyle.NewMapDefault }, plain, plateau, mountainLayer, nameLayer], [.. assets]);
        return (map, new Images(images), images);
    }
}
