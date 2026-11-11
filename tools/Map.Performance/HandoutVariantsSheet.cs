using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R09f (`--profile handout-variants-sheet`): five handout looks proposed to the owner before any code of the handouts -
/// «Найденная карта», «Обгоревший лист», «Свиток с печатью», «Портолан», «Письмо гонца». Each is the same small map with
/// the owner's mountains (<see cref="OwnerMountainFixtures"/>), drawn by the document renderer, then turned into a sheet
/// lying on a table: its edge, the ageing of the paper and what belongs to the look (folds, stains, burn, rolls, seal,
/// rhumb lines, a note). The looks differ in drawing, not in colour. Writes <c>&lt;output&gt;.handout-&lt;id&gt;.jpg</c>
/// and <c>…-zoom.jpg</c> (a corner at full size).
/// </summary>
internal static class HandoutVariantsSheet
{
    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    private const int Width = 1600, Height = 1000;
    private const string SeaColor = "#FF9DB7C2";
    private const float K = 1.5f;          // output pixels per map pixel
    private const int Margin = 130;        // the table around the sheet

    private static readonly SKColor Table = new(0x2B, 0x22, 0x19);

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        var (document, source, images) = SampleMap();
        int w = (int)(Width * K), h = (int)(Height * K);
        using var map = RenderMap(document, source, w, h);
        foreach (var image in images.Values) image.Dispose();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        (string Id, Action<SKCanvas, SKRect, SKImage> Draw)[] looks =
        [
            ("found", Found), ("burnt", Burnt), ("scroll", Scroll), ("portolan", Portolan), ("letter", Letter),
        ];
        foreach (var (id, draw) in looks)
        {
            using var surface = SKSurface.Create(new SKImageInfo(w + 2 * Margin, h + 2 * Margin));
            var canvas = surface.Canvas;
            canvas.Clear(Table);
            DrawTableGrain(canvas, surface.Canvas.DeviceClipBounds);
            draw(canvas, new SKRect(Margin, Margin, Margin + w, Margin + h), map);
            using var sheet = surface.Snapshot();
            Save(report, options, "handout-" + id, sheet, 1400, null);
            Save(report, options, "handout-" + id + "-zoom", sheet, 1400, new SKRectI(0, 0, 1400, 875));
            // The look's objects up close: where the seal, the roll end or the rose lies.
            SKPointI? prop = id switch
            {
                "scroll" => new SKPointI(2230, 1580),
                "portolan" => new SKPointI(1714, 820),
                "letter" => new SKPointI(2300, 1130),
                _ => null,
            };
            if (prop is { } p)
            {
                int x0 = Math.Clamp(p.X - 450, 0, sheet.Width - 900), y0 = Math.Clamp(p.Y - 280, 0, sheet.Height - 560);
                Save(report, options, "handout-" + id + "-prop", sheet, 1400, new SKRectI(x0, y0, x0 + 900, y0 + 560));
            }
            if (id == "scroll")
                Save(report, options, "handout-scroll-roll", sheet, 1400, new SKRectI(0, 0, 900, 560));
        }
        report.GatePassed = report.Images.Count == looks.Length * 2 + 4;
        return report;
    }

    // ------------------------------------------------------------------ the five looks

    /// <summary>Folded in four and carried for years: torn edges, creases, stains, a dotted way to an X.</summary>
    private static void Found(SKCanvas canvas, SKRect r, SKImage map)
    {
        var random = new Random(11);
        using var edge = Jagged(r, random, amplitude: 7, waves: 9, notches: 7, notchDepth: 26);
        Sheet(canvas, edge, r, map, tone: new SKColor(0xE6, 0xCF, 0x9E), toneAlpha: 95, edgeDark: 150, () =>
        {
            Tide(canvas, new SKPoint(r.Left + r.Width * .78f, r.Top + r.Height * .24f), 95, random);
            Tide(canvas, new SKPoint(r.Left + r.Width * .14f, r.Top + r.Height * .80f), 130, random);
            Way(canvas, r, [(330, 640), (500, 600), (680, 640), (860, 590), (980, 700), (1010, 840)], (1010, 860));
        }, age: .7f, life: new Life(11, Crumple: 1f, CreaseSize: 1f, Wear: .55f, Blotch: 1f));
    }

    /// <summary>Snatched from the fire: a charred, uneven edge with scorch and two burnt holes.</summary>
    private static void Burnt(SKCanvas canvas, SKRect r, SKImage map)
    {
        var random = new Random(23);
        using var outline = Jagged(r, random, amplitude: 34, waves: 14, notches: 11, notchDepth: 70);
        using var holeA = Blob(new SKPoint(r.Left + r.Width * .70f, r.Top + r.Height * .18f), 46, random);
        using var holeB = Blob(new SKPoint(r.Left + r.Width * .22f, r.Top + r.Height * .66f), 30, random);
        using var withA = outline.Op(holeA, SKPathOp.Difference);
        using var edge = withA.Op(holeB, SKPathOp.Difference);
        Sheet(canvas, edge, r, map, tone: new SKColor(0xE2, 0xC6, 0x8C), toneAlpha: 110, edgeDark: 60, () =>
        {
            // Scorch: a wide brown band, then the black rim right at the burnt edge.
            using var scorch = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 120, Color = new SKColor(0x7A, 0x44, 0x14, 170),
                BlendMode = SKBlendMode.Multiply, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 34) };
            canvas.DrawPath(edge, scorch);
            using var char1 = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 34, Color = new SKColor(0x3A, 0x1C, 0x06, 230),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9) };
            canvas.DrawPath(edge, char1);
            using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9, Color = new SKColor(0x12, 0x0A, 0x05, 255) };
            canvas.DrawPath(edge, rim);
        });
    }

    /// <summary>A scroll held open: rolled top and bottom, deckled sides, a red wax seal with ribbons.</summary>
    private static void Scroll(SKCanvas canvas, SKRect r, SKImage map)
    {
        var random = new Random(37);
        var paper = new SKRect(r.Left + 30, r.Top + 34, r.Right - 30, r.Bottom - 34);
        using var edge = Jagged(paper, random, amplitude: 4, waves: 30, notches: 0, notchDepth: 0);
        Sheet(canvas, edge, paper, map, tone: new SKColor(0xEC, 0xD8, 0xAA), toneAlpha: 90, edgeDark: 120, () =>
        {
            // The shadow each roll throws on the open paper.
            foreach (var (y, down) in new[] { (paper.Top, true), (paper.Bottom, false) })
            {
                using var shade = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, y), new SKPoint(0, y + (down ? 90 : -90)),
                        [new SKColor(0x3A, 0x26, 0x10, 150), SKColors.Transparent], SKShaderTileMode.Clamp),
                    BlendMode = SKBlendMode.Multiply,
                };
                canvas.DrawRect(new SKRect(paper.Left, down ? y : y - 90, paper.Right, down ? y + 90 : y), shade);
            }
        }, life: new Life(37, Crumple: .45f, CreaseSize: 1.6f, Wear: .35f, Blotch: .7f));
        Roll(canvas, paper.Left - 26, paper.Right + 26, paper.Top, 46, random);
        Roll(canvas, paper.Left - 26, paper.Right + 26, paper.Bottom, 46, random);
        Seal(canvas, new SKPoint(paper.Right - 220, paper.Bottom - 40), 80, random, ribbons: true, broken: false);
    }

    /// <summary>A sea chart on a whole skin: the neck at one side, a web of rhumb lines from a wind rose.</summary>
    private static void Portolan(SKCanvas canvas, SKRect r, SKImage map)
    {
        var random = new Random(41);
        using var edge = Skin(r, random);
        Sheet(canvas, edge, r, map, tone: new SKColor(0xD9, 0xBE, 0x8A), toneAlpha: 120, edgeDark: 170, () =>
        {
            var centre = new SKPoint(r.Left + r.Width * .66f, r.Top + r.Height * .46f);
            var radius = r.Height * .40f;
            var hubs = new List<SKPoint> { centre };
            for (var i = 0; i < 16; i++)
            {
                var a = i * MathF.PI / 8 + MathF.PI / 16;
                hubs.Add(new SKPoint(centre.X + radius * MathF.Cos(a), centre.Y + radius * MathF.Sin(a)));
            }
            SKColor[] inks = [new(0x22, 0x1E, 0x1A, 60), new(0x3C, 0x6A, 0x3A, 55), new(0xA0, 0x2C, 0x1E, 55)];
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, BlendMode = SKBlendMode.Multiply };
            foreach (var hub in hubs)
                for (var k = 0; k < 32; k++)
                {
                    var a = k * MathF.PI / 16;
                    line.Color = inks[k % 4 == 0 ? 0 : k % 2 == 0 ? 1 : 2];
                    canvas.DrawLine(hub, new SKPoint(hub.X + 5000 * MathF.Cos(a), hub.Y + 5000 * MathF.Sin(a)), line);
                }
            Rose(canvas, centre, 110, random);
        }, life: new Life(41, Crumple: .35f, CreaseSize: 1.8f, Wear: .5f, Blotch: .9f));
    }

    /// <summary>A message by courier: a clean sheet folded in three, a broken seal, a note in the margin.</summary>
    private static void Letter(SKCanvas canvas, SKRect r, SKImage map)
    {
        var random = new Random(53);
        using var edge = Jagged(r, random, amplitude: 1.2, waves: 40, notches: 0, notchDepth: 0);
        Sheet(canvas, edge, r, map, tone: new SKColor(0xEE, 0xDC, 0xB4), toneAlpha: 100, edgeDark: 80, () =>
        {
            using var typeface = SKTypeface.FromFile(Path.Combine(Environment.CurrentDirectory, "src", "Map.App", "Assets", "Fonts", "Cormorant", "CormorantGaramond-MediumItalic.ttf"))
                ?? SKTypeface.FromFamilyName("Georgia", SKFontStyle.Italic);
            using var font = new SKFont(typeface, 54);
            canvas.Save();
            canvas.RotateDegrees(-2.5f, r.Left + 1250, r.Top + 120);
            Handwriting(canvas, "До перевала три дня пути.", r.Left + 1250, r.Top + 110, font, random);
            Handwriting(canvas, "Сжечь по прочтении. — Р.", r.Left + 1290, r.Top + 170, font, random);
            canvas.Restore();
        }, age: .55f, life: new Life(53, Crumple: .6f, CreaseSize: 1.2f, Wear: .2f, Blotch: .35f));
        Seal(canvas, new SKPoint(r.Right - 150, r.Top + r.Height * 2 / 3), 62, random, ribbons: false, broken: true);
    }

    // ------------------------------------------------------------------ the sheet

    /// <summary>The map clipped to the sheet's edge, on its shadow, aged: paper tone, grain, darker towards the edge.</summary>
    private static void Sheet(SKCanvas canvas, SKPath edge, SKRect r, SKImage map, SKColor tone, byte toneAlpha, byte edgeDark, Action inside, float age = .6f, Life? life = null)
    {
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16) })
        {
            canvas.Save(); canvas.Translate(10, 16); canvas.DrawPath(edge, shadow); canvas.Restore();
        }
        canvas.Save();
        canvas.ClipPath(edge, antialias: true);
        using (var aged = new SKPaint { ColorFilter = Sepia(age) })
            canvas.DrawImage(map, new SKRect(Margin, Margin, Margin + map.Width, Margin + map.Height), new SKSamplingOptions(SKFilterMode.Linear), aged);
        if (life is not null)
        {
            inside();
            Lived(canvas, edge, r, tone, toneAlpha, edgeDark, life);
            canvas.Restore();
            return;
        }
        using (var paper = new SKPaint { Color = tone.WithAlpha(toneAlpha), BlendMode = SKBlendMode.Multiply })
            canvas.DrawRect(canvas.LocalClipBounds, paper);
        using (var grain = Noise((int)(r.Width / 3), (int)(r.Height / 3), 7, .80f))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Multiply })
            canvas.DrawImage(grain, r, new SKSamplingOptions(SKFilterMode.Linear), paint);
        using (var dark = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 90, Color = new SKColor(0x6E, 0x50, 0x28, edgeDark),
                   BlendMode = SKBlendMode.Multiply, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 40) })
            canvas.DrawPath(edge, dark);
        inside();
        canvas.Restore();
    }

    /// <summary>How a sheet lived (Pinterest references, 2026-11-11): crumple strength and crease size, ink worn off in
    /// patches, brown blotches and foxing. One seed for all of it.</summary>
    private sealed record Life(int Seed, float Crumple, float CreaseSize, float Wear, float Blotch);

    /// <summary>Ageing as real paper shows it: uneven, in patches - ink worn off, mottled tone, blotches, an edge that darkens
    /// unevenly, a net of short creases lit softly from the top left (matte: lit sides brighten far less than shaded ones
    /// darken), foxing spots.</summary>
    private static void Lived(SKCanvas canvas, SKPath edge, SKRect r, SKColor tone, byte toneAlpha, byte edgeDark, Life life)
    {
        int w = (int)(r.Width / 2), h = (int)(r.Height / 2);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear);
        var wear = Field(w, h, life.Seed + 1, [(110, .5f), (36, .32f), (10, .18f)]);
        using (var worn = Tinted(w, h, new SKColor(0xEE, 0xDE, 0xBA), i => Smooth(.62f, .92f, wear[i]) * life.Wear * .55f))
            canvas.DrawImage(worn, r, sampling);
        using (var paper = new SKPaint { Color = tone.WithAlpha(toneAlpha), BlendMode = SKBlendMode.Multiply })
            canvas.DrawRect(canvas.LocalClipBounds, paper);
        using (var grain = Noise((int)(r.Width / 3), (int)(r.Height / 3), 7, .86f))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Multiply })
            canvas.DrawImage(grain, r, sampling, paint);

        // Blotches: a warped field, so the patches run in uneven tongues rather than round clouds.
        var a = Field(w, h, life.Seed + 2, [(170, .6f), (55, .4f)]);
        var b = Field(w, h, life.Seed + 3, [(170, .6f), (55, .4f)]);
        var c = Field(w, h, life.Seed + 4, [(80, .5f), (24, .3f), (7, .2f)]);
        var blot = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var v = Sample(c, w, h, x + 90 * (a[i] - .5f), y + 90 * (b[i] - .5f));
                // Paper browns most where hands and air reached it: towards the edges.
                float ex = 2f * x / w - 1, ey = 2f * y / h - 1;
                var rim = Math.Clamp(ex * ex * ex * ex + ey * ey * ey * ey, 0, 1);
                blot[i] = (.25f * v + .75f * Smooth(.55f, .85f, v)) * (.35f + .65f * rim) * life.Blotch;
            }
        using (var blotches = Tinted(w, h, new SKColor(0x8A, 0x58, 0x22), i => blot[i] * .38f))
        using (var paint = new SKPaint { BlendMode = SKBlendMode.Multiply })
            canvas.DrawImage(blotches, r, sampling, paint);

        // The edge darkens unevenly: the dark band masked by a field.
        var e = Field(w, h, life.Seed + 5, [(130, .6f), (30, .4f)]);
        using (var layer = new SKPaint { BlendMode = SKBlendMode.Multiply })
        {
            canvas.SaveLayer(layer);
            using (var dark = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 120, Color = new SKColor(0x6E, 0x50, 0x28, edgeDark),
                       MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 48) })
                canvas.DrawPath(edge, dark);
            using (var mask = Tinted(w, h, SKColors.Black, i => .2f + .8f * Smooth(.3f, .8f, e[i])))
            using (var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn })
                canvas.DrawImage(mask, r, sampling, dstIn);
            canvas.Restore();
        }

        if (life.Crumple > 0)
            using (var crumple = Crumple((int)r.Width, (int)r.Height, life))
            using (var overlay = new SKPaint { BlendMode = SKBlendMode.Overlay })
                canvas.DrawImage(crumple, r, sampling, overlay);

        // Foxing: small rust spots in a few loose clusters.
        var random = new Random(life.Seed + 9);
        using var spot = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Multiply, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f) };
        for (var k = 0; k < 4; k++)
        {
            var centre = new SKPoint(r.Left + (float)random.NextDouble() * r.Width, r.Top + (float)random.NextDouble() * r.Height);
            var count = (int)(10 * life.Blotch) + 2;
            for (var j = 0; j < count; j++)
            {
                spot.Color = new SKColor(0x8A, 0x4E, 0x1E, (byte)random.Next(40, 120));
                var p = new SKPoint(centre.X + (float)Gauss(random) * 110, centre.Y + (float)Gauss(random) * 80);
                canvas.DrawCircle(p, 1.5f + (float)random.NextDouble() * 5, spot);
            }
        }
    }

    /// <summary>A crumpled sheet as a lighting layer for Overlay (grey 0.5 = untouched): long creases right across, chains
    /// of medium creases turning at random, many small wrinkles; each a ridge or a valley that fades out at its ends.
    /// The slopes add up into facets; the light is soft and matte.</summary>
    private static SKImage Crumple(int w, int h, Life life)
    {
        var random = new Random(life.Seed + 7);
        var gx = new float[w * h];
        var gy = new float[w * h];
        var size = life.CreaseSize;
        float Sign() => random.Next(2) == 0 ? -1 : 1;
        float R() => (float)random.NextDouble();

        void Crease(float px, float py, float angle, float half, float reach, float slope)
        {
            float ux = MathF.Cos(angle), uy = MathF.Sin(angle), nx = -uy, ny = ux;
            var soft = half * .3f + 6;
            var ext = half + 2.5f * soft;
            var spanX = MathF.Abs(ux) * ext + MathF.Abs(nx) * reach;
            var spanY = MathF.Abs(uy) * ext + MathF.Abs(ny) * reach;
            int x0 = Math.Max(0, (int)(px - spanX)), x1 = Math.Min(w - 1, (int)(px + spanX));
            int y0 = Math.Max(0, (int)(py - spanY)), y1 = Math.Min(h - 1, (int)(py + spanY));
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                {
                    float dx = x - px, dy = y - py, d = dx * nx + dy * ny, ad = MathF.Abs(d);
                    if (ad >= reach) continue;
                    var over = MathF.Abs(dx * ux + dy * uy) - half;
                    var fade = over <= 0 ? 1 : MathF.Exp(-over * over / (soft * soft));
                    if (fade < .01f) continue;
                    // Sharp at the crease itself, dying out smoothly towards the reach (no second line there).
                    var q = 1 - ad / reach;
                    var k = slope * fade * q * q * (d > 0 ? -1 : 1);
                    var i = y * w + x;
                    gx[i] += k * nx; gy[i] += k * ny;
                }
        }

        for (var i = 0; i < 4; i++)
            Crease(R() * w, R() * h, R() * MathF.PI, w * (.2f + .3f * R()), (80 + 80 * R()) * size, (.05f + .05f * R()) * Sign() * life.Crumple);
        for (var chain = 0; chain < 40; chain++)
        {
            float x = R() * w, y = R() * h, angle = R() * MathF.PI;
            for (var s = 0; s < 6; s++)
            {
                var half = (18 + 50 * R()) * size;
                Crease(x, y, angle, half, (14 + 30 * R()) * size, (.12f + .16f * R()) * Sign() * life.Crumple);
                x += MathF.Cos(angle) * half * 1.6f; y += MathF.Sin(angle) * half * 1.6f;
                angle += Sign() * (.5f + .8f * R());
            }
        }
        // Small wrinkles, also chained: a crumple is a connected net of short zigzags, not separate dashes.
        var wrinkles = (int)(w * (long)h / (32000 * size * size));
        for (var chain = 0; chain < wrinkles; chain++)
        {
            float x = R() * w, y = R() * h, angle = R() * MathF.PI;
            for (var s = 0; s < 7; s++)
            {
                var half = (7 + 18 * R()) * size;
                Crease(x, y, angle, half, (5 + 9 * R()) * size, (.12f + .16f * R()) * Sign() * life.Crumple);
                x += MathF.Cos(angle) * half * 1.8f; y += MathF.Sin(angle) * half * 1.8f;
                angle += Sign() * (.4f + 1f * R());
            }
        }

        float lx = -.5f, ly = -.62f, lz = .6f;
        var ln = MathF.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ln; ly /= ln; lz /= ln;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var v = (-gx[i] * lx - gy[i] * ly + lz) / MathF.Sqrt(gx[i] * gx[i] + gy[i] * gy[i] + 1) / lz;
            var g = v < 1 ? .5f + .5f * (v - 1) : .5f + .2f * (v - 1);
            var level = (byte)(255 * Math.Clamp(g, .2f, .8f));
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = level; pixels[i * 4 + 3] = 255;
        }
        using var data = SKData.CreateCopy(pixels);
        return SKImage.FromPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), data, w * 4);
    }

    /// <summary>Value noise in octaves (cell size, weight), stretched to 0..1.</summary>
    private static float[] Field(int width, int height, int seed, (int Cell, float Weight)[] octaves)
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

    private static float Sample(float[] field, int w, int h, float x, float y)
    {
        x = Math.Clamp(x, 0, w - 1.001f); y = Math.Clamp(y, 0, h - 1.001f);
        int ix = (int)x, iy = (int)y; float tx = x - ix, ty = y - iy;
        var top = field[iy * w + ix] * (1 - tx) + field[iy * w + ix + 1] * tx;
        var bottom = field[(iy + 1) * w + ix] * (1 - tx) + field[(iy + 1) * w + ix + 1] * tx;
        return top * (1 - ty) + bottom * ty;
    }

    /// <summary>One colour with a per-pixel opacity (0..1).</summary>
    private static SKImage Tinted(int w, int h, SKColor color, Func<int, float> alpha)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var a = Math.Clamp(alpha(i), 0, 1);
            pixels[i * 4] = (byte)(color.Red * a); pixels[i * 4 + 1] = (byte)(color.Green * a);
            pixels[i * 4 + 2] = (byte)(color.Blue * a); pixels[i * 4 + 3] = (byte)(255 * a);
        }
        using var data = SKData.CreateCopy(pixels);
        return SKImage.FromPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), data, w * 4);
    }

    private static float Smooth(float from, float to, float v)
    {
        var t = Math.Clamp((v - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Gauss(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(Math.Tau * random.NextDouble());

    /// <summary>A rectangle whose edge wavers and has a few tears.</summary>
    private static SKPath Jagged(SKRect r, Random random, double amplitude, int waves, int notches, double notchDepth)
    {
        var perimeter = 2 * (r.Width + r.Height);
        var phases = Enumerable.Range(0, 3).Select(_ => random.NextDouble() * Math.Tau).ToArray();
        var cuts = Enumerable.Range(0, notches).Select(_ => (At: random.NextDouble() * perimeter, Width: 18 + random.NextDouble() * 40, Depth: notchDepth * (.5 + random.NextDouble() * .5))).ToArray();
        var path = new SKPath();
        for (double s = 0; s < perimeter; s += 5)
        {
            var t = s / perimeter * Math.Tau;
            var inward = amplitude * (1 + .55 * Math.Sin(t * waves + phases[0]) + .3 * Math.Sin(t * waves * 2.7 + phases[1]) + .15 * Math.Sin(t * waves * 6.1 + phases[2]))
                + amplitude * .25 * random.NextDouble();
            foreach (var cut in cuts)
            {
                var d = Math.Abs(s - cut.At);
                if (d < cut.Width) inward += cut.Depth * (1 - d / cut.Width);
            }
            var (x, y, nx, ny) = OnRect(r, s);
            var p = new SKPoint((float)(x + nx * inward), (float)(y + ny * inward));
            if (s == 0) path.MoveTo(p); else path.LineTo(p);
        }
        path.Close();
        return path;
    }

    /// <summary>The point at distance <paramref name="s"/> along the rectangle (clockwise from the top left) and the inward normal.</summary>
    private static (double X, double Y, double Nx, double Ny) OnRect(SKRect r, double s)
    {
        if (s < r.Width) return (r.Left + s, r.Top, 0, 1);
        s -= r.Width;
        if (s < r.Height) return (r.Right, r.Top + s, -1, 0);
        s -= r.Height;
        if (s < r.Width) return (r.Right - s, r.Bottom, 0, -1);
        s -= r.Width;
        return (r.Left, r.Bottom - s, 1, 0);
    }

    /// <summary>A whole skin: a rounded body with gently wavy sides and the neck sticking out at the left.</summary>
    private static SKPath Skin(SKRect r, Random random)
    {
        var body = new SKRect(r.Left + 110, r.Top + 12, r.Right - 12, r.Bottom - 12);
        double cx = body.MidX, cy = body.MidY, a = body.Width / 2, b = body.Height / 2;
        var phase = random.NextDouble() * Math.Tau;
        var points = new List<SKPoint>();
        for (var i = 0; i < 720; i++)
        {
            var t = i * Math.Tau / 720;
            double c = Math.Cos(t), s = Math.Sin(t);
            // A superellipse: straight-ish sides, round corners.
            var x = a * Math.Sign(c) * Math.Pow(Math.Abs(c), 2.0 / 7);
            var y = b * Math.Sign(s) * Math.Pow(Math.Abs(s), 2.0 / 7);
            var wave = 1 - (.012 * Math.Sin(t * 6 + phase) + .006 * Math.Sin(t * 15 + phase * 2));
            x *= wave; y *= wave;
            // The neck: the left side reaches out around its middle.
            if (c < 0) x -= 100 * Math.Exp(-Math.Pow(s / .22, 2)) * Math.Pow(-c, 3);
            points.Add(new SKPoint((float)(cx + x), (float)(cy + y)));
        }
        var path = new SKPath();
        path.AddPoly([.. points], close: true);
        return path;
    }

    /// <summary>Old paper: the colours pulled towards brown by <paramref name="amount"/> (0 - as drawn, 1 - full sepia).</summary>
    private static SKColorFilter Sepia(float amount)
    {
        float[] s = [.393f, .769f, .189f, .349f, .686f, .168f, .272f, .534f, .131f];
        var m = new float[20];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                m[row * 5 + col] = (1 - amount) * (row == col ? 1 : 0) + amount * s[row * 3 + col];
        m[18] = 1;
        return SKColorFilter.CreateColorMatrix(m);
    }

    private static SKPath Blob(SKPoint c, float radius, Random random)
    {
        var path = new SKPath();
        var points = new List<SKPoint>();
        var phases = Enumerable.Range(0, 3).Select(_ => random.NextDouble() * Math.Tau).ToArray();
        for (var i = 0; i < 72; i++)
        {
            var a = i * Math.Tau / 72;
            var rr = radius * (1 + .22 * Math.Sin(3 * a + phases[0]) + .12 * Math.Sin(7 * a + phases[1]) + .06 * Math.Sin(13 * a + phases[2]));
            points.Add(new SKPoint((float)(c.X + rr * Math.Cos(a)), (float)(c.Y + rr * Math.Sin(a))));
        }
        path.AddPoly([.. points], close: true);
        return path;
    }

    // ------------------------------------------------------------------ parts

    /// <summary>A dried water stain: a pale uneven patch with a thin, broken tide line and a fainter one just inside.</summary>
    private static void Tide(SKCanvas canvas, SKPoint c, float radius, Random random)
    {
        using var blot = Blob(c, radius, random);
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0x9A, 0x6A, 0x30, 24), BlendMode = SKBlendMode.Multiply,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4) };
        canvas.DrawPath(blot, fill);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, Color = new SKColor(0x6B, 0x40, 0x18, 110),
            BlendMode = SKBlendMode.Multiply, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f),
            PathEffect = SKPathEffect.CreateDiscrete(12, 2f, (uint)random.Next()) };
        canvas.DrawPath(blot, line);
        using var inner = new SKPath(blot);
        inner.Transform(SKMatrix.CreateScale(.93f, .9f, c.X, c.Y));
        line.Color = new SKColor(0x6B, 0x40, 0x18, 50);
        canvas.DrawPath(inner, line);
    }

    /// <summary>A dotted way in red ink through map points (map pixels) ending at an X.</summary>
    private static void Way(SKCanvas canvas, SKRect r, (double X, double Y)[] points, (double X, double Y) target)
    {
        SKPoint P((double X, double Y) p) => new((float)(r.Left + p.X * K), (float)(r.Top + p.Y * K));
        using var path = new SKPath();
        path.MoveTo(P(points[0]));
        for (var i = 1; i < points.Length; i++)
        {
            var a = P(points[i - 1]); var b = P(points[i]);
            path.QuadTo(new SKPoint((a.X + b.X) / 2 + (b.Y - a.Y) * .15f, (a.Y + b.Y) / 2 - (b.X - a.X) * .15f), b);
        }
        using var dots = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round,
            Color = new SKColor(0x8C, 0x1C, 0x10, 225), PathEffect = SKPathEffect.CreateDash([2, 17], 0) };
        canvas.DrawPath(path, dots);
        var x = P(target);
        using var cross = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, StrokeCap = SKStrokeCap.Round, Color = new SKColor(0x8C, 0x1C, 0x10, 235) };
        canvas.DrawLine(x.X - 26, x.Y - 26, x.X + 26, x.Y + 26, cross);
        canvas.DrawLine(x.X - 26, x.Y + 24, x.X + 24, x.Y - 26, cross);
    }

    /// <summary>A rolled end of the scroll - the same paper rolled up, not a stick: an uneven cylinder lit from the top left
    /// (computed per pixel), paper grain, ends browned, the free edge of the outer layer with its little shadow, and at
    /// each end the spiral of layers round a dark hollow.</summary>
    private static void Roll(SKCanvas canvas, float left, float right, float y, float radius, Random random)
    {
        int w = (int)(right - left), h = (int)(radius * 2.6f);
        var top = y - h / 2f;
        float p1 = (float)(random.NextDouble() * Math.Tau), p2 = (float)(random.NextDouble() * Math.Tau), p3 = (float)(random.NextDouble() * Math.Tau);
        float R(int x) => radius * (1 + .05f * MathF.Sin(x * .004f + p1) + .025f * MathF.Sin(x * .013f + p2));
        float Cy(int x) => h / 2f + radius * .04f * MathF.Sin(x * .003f + p3);
        float Edge(int x) => -.18f + .07f * MathF.Sin(x * .011f + p2) + .03f * MathF.Sin(x * .05f + p1);

        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12) })
            canvas.DrawRoundRect(new SKRect(left + 6, y - radius + 14, right - 6, y + radius + 14), radius, radius, shadow);

        var grain = Field(w, h, random.Next(), [(40, .35f), (9, .35f), (3, .3f)]);
        float lx = -.3f, ly = -.78f, lz = .55f;
        var ln = MathF.Sqrt(lx * lx + ly * ly + lz * lz); ly /= ln; lz /= ln;
        var pixels = new byte[w * h * 4];
        for (var x = 0; x < w; x++)
        {
            float r = R(x), cy = Cy(x), edge = Edge(x);
            var end = Math.Clamp(Math.Min(x, w - x) / (radius * 2.5f), 0, 1);
            for (var py = 0; py < h; py++)
            {
                var v = (py - cy) / r;
                var cover = Math.Clamp((1 - MathF.Abs(v)) * r, 0, 1);
                if (cover <= 0) continue;
                var dot = ly * v + lz * MathF.Sqrt(Math.Max(0, 1 - v * v));
                var shade = .4f + .7f * Math.Max(0, dot);
                var brown = (1 - end) * .4f + .12f * Math.Max(0, v);
                float cr = 0xDA + (0x8A - 0xDA) * brown, cg = 0xC3 + (0x62 - 0xC3) * brown, cb = 0x92 + (0x34 - 0x92) * brown;
                var k = shade * (.84f + .16f * grain[py * w + x]);
                var step = v - edge;
                if (step is > 0 and < .07f) k *= .8f + .2f * step / .07f;     // shadow under the outer layer's free edge
                else if (step is > -.025f and <= 0) k *= 1.05f;
                var i = (py * w + x) * 4;
                pixels[i] = (byte)Math.Clamp(cr * k * cover, 0, 255); pixels[i + 1] = (byte)Math.Clamp(cg * k * cover, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(cb * k * cover, 0, 255); pixels[i + 3] = (byte)(255 * cover);
            }
        }
        using (var data = SKData.CreateCopy(pixels))
        using (var image = SKImage.FromPixels(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul), data, w * 4))
            canvas.DrawImage(image, left, top);

        // The ends: layers of paper wound round a hollow, the outline deckled like the sheet's sides.
        foreach (var (x, cx) in new[] { (0, left), (w - 1, right) })
        {
            float r = R(x), cy = top + Cy(x), rx = r * .34f;
            using var capPath = new SKPath();
            capPath.AddOval(new SKRect(cx - rx, cy - r, cx + rx, cy + r));
            using (var cap = new SKPaint { IsAntialias = true, Color = new SKColor(0xB6, 0x98, 0x66), PathEffect = SKPathEffect.CreateDiscrete(5, 1.2f, (uint)random.Next()) })
                canvas.DrawPath(capPath, cap);
            using var layer = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f };
            for (var k = 1; k <= 9; k++)
            {
                var f = .18f + .8f * k / 9f;
                layer.Color = new SKColor(0x5A, 0x40, 0x20, (byte)random.Next(90, 170));
                var start = (float)random.NextDouble() * 360;
                canvas.DrawArc(new SKRect(cx - rx * f, cy - r * f, cx + rx * f, cy + r * f), start, 220 + (float)random.NextDouble() * 120, false, layer);
            }
            using var hollow = new SKPaint { IsAntialias = true, Color = new SKColor(0x2E, 0x20, 0x10), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.2f) };
            canvas.DrawOval(new SKRect(cx - rx * .16f, cy - r * .16f, cx + rx * .16f, cy + r * .16f), hollow);
        }
    }

    /// <summary>A wax seal from a height map, lit per pixel: a thick puddle with a rounded, swollen edge and squeezed-out
    /// lobes, the pressed disc sunk in it with a lip round it, the die's relief (a ring of beads, mountains, a star),
    /// a satin sheen rather than a gloss, occlusion in the hollows, the paper oily round it. Whole with two silk ribbons
    /// under it, or cracked in two along a jagged line with chips about.</summary>
    private static void Seal(SKCanvas canvas, SKPoint c, float radius, Random random, bool ribbons, bool broken)
    {
        using (var halo = new SKPaint { IsAntialias = true, Color = new SKColor(0x7A, 0x4A, 0x20, 45), BlendMode = SKBlendMode.Multiply,
                   MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12) })
            canvas.DrawCircle(c, radius * 1.25f, halo);
        if (ribbons)
        {
            Ribbon(canvas, new SKPoint(c.X - 12, c.Y + 6), new SKPoint(c.X - 85, c.Y + 200), 34, random);
            Ribbon(canvas, new SKPoint(c.X + 12, c.Y + 6), new SKPoint(c.X + 95, c.Y + 180), 34, random);
        }
        using var wax = WaxImage(radius, random);
        var corner = new SKPoint(c.X - wax.Width / 2f, c.Y - wax.Height / 2f);
        void Draw(SKPoint at, SKPath? clip)
        {
            canvas.Save();
            if (clip is not null) canvas.ClipPath(clip, antialias: true);
            using (var shadow = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0, 0, 0, 140), SKBlendMode.SrcIn),
                       ImageFilter = SKImageFilter.CreateBlur(5, 5) })
                canvas.DrawImage(wax, at.X + 4, at.Y + 7, shadow);
            canvas.DrawImage(wax, at.X, at.Y);
            canvas.Restore();
        }
        if (!broken) { Draw(corner, null); return; }

        // The crack: a jagged line through the middle; each half moves away a little and shows its broken face.
        var angle = 1.15f + (float)random.NextDouble() * .3f;
        var u = new SKPoint(MathF.Cos(angle), MathF.Sin(angle));
        var n = new SKPoint(-u.Y, u.X);
        var crack = new List<SKPoint>();
        for (var s = -radius * 1.8f; s <= radius * 1.8f; s += radius * .14f)
        {
            var off = ((float)random.NextDouble() - .5f) * radius * .16f;
            crack.Add(new SKPoint(c.X + u.X * s + n.X * off, c.Y + u.Y * s + n.Y * off));
        }
        foreach (var side in new[] { -1, 1 })
        {
            using var half = new SKPath();
            half.AddPoly([.. crack, new(crack[^1].X + n.X * side * radius * 3, crack[^1].Y + n.Y * side * radius * 3),
                new(crack[0].X + n.X * side * radius * 3, crack[0].Y + n.Y * side * radius * 3)]);
            var shift = new SKPoint(n.X * side * 7 + u.X * side * 2, n.Y * side * 7 + u.Y * side * 2);
            half.Offset(shift);
            Draw(new SKPoint(corner.X + shift.X, corner.Y + shift.Y), half);
            // The broken face: a narrow matte band of lighter wax along the crack, inside the wax only.
            using var face = new SKPath();
            face.AddPoly([.. crack], close: false);
            face.Offset(shift);
            canvas.Save();
            canvas.ClipPath(half, antialias: true);
            canvas.ClipRect(SKRect.Create(corner.X + shift.X, corner.Y + shift.Y, wax.Width, wax.Height));
            canvas.SaveLayer();
            using (var band = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = new SKColor(0x9C, 0x30, 0x26, 200) })
                canvas.DrawPath(face, band);
            using (var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn })
                canvas.DrawImage(wax, corner.X + shift.X, corner.Y + shift.Y, dstIn);
            canvas.Restore();
            canvas.Restore();
        }
        // A few chips of wax that broke off, lying close to the crack's ends.
        using var chip = new SKPaint { IsAntialias = true, Color = new SKColor(0x6E, 0x16, 0x12) };
        using var chipShadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.5f) };
        for (var k = 0; k < 4; k++)
        {
            var at = (float)(random.NextDouble() < .5 ? -1 : 1) * radius * (1.05f + .25f * (float)random.NextDouble());
            var p = new SKPoint(c.X + u.X * at + n.X * ((float)random.NextDouble() - .5f) * radius * .5f, c.Y + u.Y * at + n.Y * ((float)random.NextDouble() - .5f) * radius * .5f);
            using var bit = Blob(p, 3 + (float)random.NextDouble() * 4, random);
            using var shadowBit = new SKPath(bit); shadowBit.Offset(1.5f, 2);
            canvas.DrawPath(shadowBit, chipShadow);
            canvas.DrawPath(bit, chip);
        }
    }

    private static SKImage WaxImage(float radius, Random random)
    {
        var size = (int)(radius * 3);
        float cx = size / 2f, cy = size / 2f;
        var harmonics = Enumerable.Range(2, 7).Select(k => (K: k, Amp: .012f + .03f * (float)random.NextDouble() / k * 2, Phase: (float)(random.NextDouble() * Math.Tau))).ToArray();
        var lobes = Enumerable.Range(0, 3).Select(_ => (At: (float)(random.NextDouble() * Math.Tau), Width: .3f + .3f * (float)random.NextDouble(), Amount: .08f + .14f * (float)random.NextDouble())).ToArray();
        float Rim(float a)
        {
            var r = 1f;
            foreach (var (k, amp, phase) in harmonics) r += amp * MathF.Sin(k * a + phase);
            foreach (var (at, width, amount) in lobes)
            {
                var d = MathF.IEEERemainder(a - at, MathF.Tau);
                r += amount * MathF.Exp(-d * d / (width * width));
            }
            return radius * 1.02f * r;
        }

        // The die's relief, rasterised: a bead ring, a thin ring, three mountains, a star.
        var design = new float[size * size];
        float rs = radius * .62f;
        using (var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Premul)))
        {
            using (var dc = new SKCanvas(bitmap))
            {
                dc.Clear(SKColors.Transparent);
                using var pen = new SKPaint { IsAntialias = true, Color = SKColors.Black, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, .9f) };
                pen.Style = SKPaintStyle.Stroke; pen.StrokeWidth = radius * .035f;
                dc.DrawCircle(cx, cy, rs * .9f, pen);
                pen.Style = SKPaintStyle.Fill;
                for (var k = 0; k < 28; k++)
                {
                    var a = k * MathF.Tau / 28;
                    dc.DrawCircle(cx + rs * .78f * MathF.Cos(a), cy + rs * .78f * MathF.Sin(a), radius * .028f, pen);
                }
                using var peaks = new SKPath();
                float baseY = cy + rs * .32f, s = rs * .58f;
                peaks.MoveTo(cx - s, baseY);
                peaks.LineTo(cx - s * .55f, cy - rs * .05f); peaks.LineTo(cx - s * .32f, cy + rs * .08f);
                peaks.LineTo(cx, cy - rs * .36f); peaks.LineTo(cx + s * .3f, cy + rs * .02f);
                peaks.LineTo(cx + s * .55f, cy - rs * .12f); peaks.LineTo(cx + s, baseY);
                peaks.Close();
                dc.DrawPath(peaks, pen);
                using var star = new SKPath();
                for (var i = 0; i < 10; i++)
                {
                    var a = -MathF.PI / 2 + i * MathF.PI / 5; var rr = i % 2 == 0 ? rs * .13f : rs * .05f;
                    var p = new SKPoint(cx + s * .62f + rr * MathF.Cos(a), cy - rs * .45f + rr * MathF.Sin(a));
                    if (i == 0) star.MoveTo(p); else star.LineTo(p);
                }
                star.Close();
                dc.DrawPath(star, pen);
                pen.Style = SKPaintStyle.Stroke; pen.StrokeWidth = radius * .03f;
                dc.DrawLine(cx - s * 1.05f, baseY + rs * .1f, cx + s * 1.05f, baseY + rs * .1f, pen);
            }
            var bytes = bitmap.Bytes;
            for (var i = 0; i < design.Length; i++) design[i] = bytes[i] / 255f;
        }

        var height = new float[size * size];
        var cover = new float[size * size];
        float top = radius * .22f, depth = radius * .07f;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float dx = x - cx, dy = y - cy, d = MathF.Sqrt(dx * dx + dy * dy);
                var rim = Rim(MathF.Atan2(dy, dx));
                var i = y * size + x;
                cover[i] = Math.Clamp(rim - d + .5f, 0, 1);
                if (cover[i] <= 0) continue;
                var t = Math.Min(d / rim, 1);
                var h = top * MathF.Sqrt(Math.Max(0, 1 - MathF.Pow(t, 5)));
                h -= depth * Smooth(rs + 1.5f, rs - 1.5f, d);
                h += radius * .03f * MathF.Exp(-MathF.Pow((d - rs - 4) / 4.5f, 2));
                if (d < rs) h += design[i] * radius * .05f;
                height[i] = h;
            }
        // Occlusion: how far a pixel sits below its neighbourhood.
        var blurred = BoxBlur(BoxBlur(height, size, 6), size, 6);
        var grain = Field(size, size, random.Next(), [(6, .6f), (2, .4f)]);
        float lx = -.45f, ly = -.65f, lz = .62f;
        var ln = MathF.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ln; ly /= ln; lz /= ln;
        float hx = lx, hy = ly, hz = lz + 1;
        var hn = MathF.Sqrt(hx * hx + hy * hy + hz * hz); hx /= hn; hy /= hn; hz /= hn;
        var pixels = new byte[size * size * 4];
        for (var y = 1; y < size - 1; y++)
            for (var x = 1; x < size - 1; x++)
            {
                var i = y * size + x;
                if (cover[i] <= 0) continue;
                var gx = (height[i + 1] - height[i - 1]) / 2;
                var gy = (height[i + size] - height[i - size]) / 2;
                var nl = MathF.Sqrt(gx * gx + gy * gy + 1);
                float nx = -gx / nl, ny = -gy / nl, nz = 1 / nl;
                var diffuse = Math.Max(0, nx * lx + ny * ly + nz * lz);
                var sheen = MathF.Pow(Math.Max(0, nx * hx + ny * hy + nz * hz), 22) * .22f;
                var occlusion = Math.Clamp((blurred[i] - height[i]) / (radius * .04f), 0, 1);
                var thin = 1 - Smooth(0, top * .6f, height[i]);
                float r = 0x7C + (0xB6 - 0x7C) * .4f * thin, g = 0x14 + (0x30 - 0x14) * .4f * thin, b = 0x12 + (0x24 - 0x12) * .4f * thin;
                var k = (.26f + .92f * diffuse) * (1 - .5f * occlusion) * (.9f + .1f * grain[i]);
                var a = cover[i];
                var o = i * 4;
                pixels[o] = (byte)Math.Clamp((r * k + 255 * sheen) * a, 0, 255);
                pixels[o + 1] = (byte)Math.Clamp((g * k + 220 * sheen) * a, 0, 255);
                pixels[o + 2] = (byte)Math.Clamp((b * k + 205 * sheen) * a, 0, 255);
                pixels[o + 3] = (byte)(255 * a);
            }
        using var data = SKData.CreateCopy(pixels);
        return SKImage.FromPixels(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul), data, size * 4);
    }

    private static float[] BoxBlur(float[] source, int size, int radius)
    {
        var result = new float[source.Length];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float sum = 0; var count = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var xx = x + k;
                    if (xx < 0 || xx >= size) continue;
                    sum += source[y * size + xx]; count++;
                }
                result[x * size + y] = sum / count;   // transposed: the second pass blurs the other axis
            }
        return result;
    }

    /// <summary>A silk ribbon from under the seal: a swaying centre line, the ribbon turning about it (narrower and darker
    /// where it shows its edge or its back), a V-cut end, its shadow on the paper.</summary>
    private static void Ribbon(SKCanvas canvas, SKPoint from, SKPoint to, float width, Random random)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, len = MathF.Sqrt(dx * dx + dy * dy);
        float nx = -dy / len, ny = dx / len;
        float sway1 = ((float)random.NextDouble() - .5f) * width * 3, sway2 = ((float)random.NextDouble() - .5f) * width * 3;
        SKPoint c1 = new(from.X + dx * .33f + nx * sway1, from.Y + dy * .33f + ny * sway1), c2 = new(from.X + dx * .66f + nx * sway2, from.Y + dy * .66f + ny * sway2);
        const int N = 60;
        var phase = (float)(random.NextDouble() * Math.Tau);
        var turns = .55f + .4f * (float)random.NextDouble();
        var left = new SKPoint[N]; var right = new SKPoint[N]; var light = new float[N];
        var centre = new SKPoint[N];
        for (var i = 0; i < N; i++)
        {
            var t = i / (N - 1f); var m = 1 - t;
            var p = new SKPoint(m * m * m * from.X + 3 * m * m * t * c1.X + 3 * m * t * t * c2.X + t * t * t * to.X,
                m * m * m * from.Y + 3 * m * m * t * c1.Y + 3 * m * t * t * c2.Y + t * t * t * to.Y);
            var tx = 3 * m * m * (c1.X - from.X) + 6 * m * t * (c2.X - c1.X) + 3 * t * t * (to.X - c2.X);
            var ty = 3 * m * m * (c1.Y - from.Y) + 6 * m * t * (c2.Y - c1.Y) + 3 * t * t * (to.Y - c2.Y);
            var tl = MathF.Sqrt(tx * tx + ty * ty);
            float px = -ty / tl, py = tx / tl;
            var theta = phase + t * MathF.Tau * turns * .5f;
            var cos = MathF.Cos(theta);
            var half = width / 2 * (.28f + .72f * MathF.Abs(cos));
            centre[i] = p;
            left[i] = new SKPoint(p.X + px * half, p.Y + py * half);
            right[i] = new SKPoint(p.X - px * half, p.Y - py * half);
            light[i] = cos >= 0 ? .62f + .45f * cos : .42f + .15f * -cos;
        }
        var (ex, ey) = (centre[^1].X - centre[^2].X, centre[^1].Y - centre[^2].Y);
        var el = MathF.Sqrt(ex * ex + ey * ey);
        var notch = new SKPoint(centre[^1].X - ex / el * width * .5f, centre[^1].Y - ey / el * width * .5f);
        using var strip = new SKPath();
        strip.AddPoly([.. left, notch, .. right.Reverse()]);
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 100), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) })
        { canvas.Save(); canvas.Translate(4, 7); canvas.DrawPath(strip, shadow); canvas.Restore(); }
        using (var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0x5E, 0x12, 0x0E) })
            canvas.DrawPath(strip, fill);
        canvas.Save();
        canvas.ClipPath(strip, antialias: true);
        using var quad = new SKPaint { IsAntialias = false };
        for (var i = 0; i < N - 1; i++)
        {
            var k = (light[i] + light[i + 1]) / 2;
            quad.Color = new SKColor((byte)Math.Min(255, 0x92 * k), (byte)(0x22 * k), (byte)(0x1A * k));
            using var q = new SKPath();
            q.AddPoly([left[i], left[i + 1], right[i + 1], right[i]]);
            canvas.DrawPath(q, quad);
        }
        // Silk: fine lengthwise threads.
        using (var thread = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = .8f, Color = new SKColor(0, 0, 0, 30), BlendMode = SKBlendMode.Multiply })
            for (var s = -3; s <= 3; s++)
            {
                using var line = new SKPath();
                for (var i = 0; i < N; i++)
                {
                    var f = .5f + s / 8f;
                    var p = new SKPoint(left[i].X * f + right[i].X * (1 - f), left[i].Y * f + right[i].Y * (1 - f));
                    if (i == 0) line.MoveTo(p); else line.LineTo(p);
                }
                canvas.DrawPath(line, thread);
            }
        canvas.Restore();
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0x30, 0x08, 0x06, 150) })
            canvas.DrawPath(strip, edge);
    }

    /// <summary>A painted portolan rose: 32 points - the cardinal ones in gold, the others in blue, green and red - each
    /// split into a painted half and a hatched paper half, a lily at the north, a gilt disc in the middle, a ring with
    /// ticks; the lines wobble a little as from a pen, the paint is uneven.</summary>
    private static void Rose(SKCanvas canvas, SKPoint c, float radius, Random random)
    {
        var ink = new SKColor(0x2A, 0x22, 0x1A);
        var paper = new SKColor(0xE6, 0xD4, 0xA8);
        canvas.SaveLayer();
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.1f, Color = ink,
            PathEffect = SKPathEffect.CreateDiscrete(7, .5f, (uint)random.Next()) };
        using var fill = new SKPaint { IsAntialias = true };
        using var hatch = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = .8f, Color = ink.WithAlpha(150) };

        // The ring: two circles with ticks between them.
        canvas.DrawCircle(c, radius * 1.06f, outline);
        canvas.DrawCircle(c, radius * .98f, outline);
        for (var k = 0; k < 64; k++)
        {
            var a = k * MathF.Tau / 64;
            var r0 = radius * (k % 2 == 0 ? .98f : 1.0f);
            canvas.DrawLine(c.X + r0 * MathF.Cos(a), c.Y + r0 * MathF.Sin(a), c.X + radius * 1.06f * MathF.Cos(a), c.Y + radius * 1.06f * MathF.Sin(a), hatch);
        }

        void Point(float angle, float length, float halfWidth, SKColor paint)
        {
            var u = new SKPoint(MathF.Cos(angle), MathF.Sin(angle));
            var tip = new SKPoint(c.X + u.X * length, c.Y + u.Y * length);
            var sideA = new SKPoint(c.X - u.Y * halfWidth, c.Y + u.X * halfWidth);
            var sideB = new SKPoint(c.X + u.Y * halfWidth, c.Y - u.X * halfWidth);
            using var painted = new SKPath(); painted.AddPoly([c, tip, sideA]);
            using var plain = new SKPath(); plain.AddPoly([c, tip, sideB]);
            fill.Color = paper; canvas.DrawPath(plain, fill);
            // The paint misses the outline a little, as by hand.
            canvas.Save(); canvas.Translate(.8f, .6f);
            fill.Color = paint.WithAlpha(225); canvas.DrawPath(painted, fill);
            canvas.Restore();
            canvas.Save(); canvas.ClipPath(plain, antialias: true);
            for (var k = 1; k <= 5; k++)
            {
                var f = k / 6f;
                canvas.DrawLine(c.X + (sideB.X - c.X) * f, c.Y + (sideB.Y - c.Y) * f, tip.X + (sideB.X - tip.X) * f * .2f, tip.Y + (sideB.Y - tip.Y) * f * .2f, hatch);
            }
            canvas.Restore();
            canvas.DrawPath(painted, outline); canvas.DrawPath(plain, outline);
        }
        var up = -MathF.PI / 2;
        for (var k = 0; k < 16; k++) Point(up + MathF.PI / 16 + k * MathF.PI / 8, radius * .5f, radius * .05f, new SKColor(0xA0, 0x2C, 0x1E));
        for (var k = 0; k < 8; k++) Point(up + MathF.PI / 8 + k * MathF.PI / 4, radius * .66f, radius * .08f, new SKColor(0x3C, 0x6A, 0x3A));
        for (var k = 0; k < 4; k++) Point(up + MathF.PI / 4 + k * MathF.PI / 2, radius * .8f, radius * .11f, new SKColor(0x2C, 0x3C, 0x5E));
        for (var k = 1; k < 4; k++) Point(up + k * MathF.PI / 2, radius * .98f, radius * .14f, new SKColor(0xB0, 0x82, 0x2C));
        Point(up, radius * .9f, radius * .14f, new SKColor(0xB0, 0x82, 0x2C));

        // The lily at the north.
        var b = new SKPoint(c.X, c.Y - radius * .9f);
        var s = radius * .3f;
        using var lily = new SKPath();
        lily.MoveTo(b.X, b.Y - s * .15f);
        lily.CubicTo(b.X - s * .32f, b.Y - s * .55f, b.X - s * .1f, b.Y - s * .95f, b.X, b.Y - s * 1.2f);
        lily.CubicTo(b.X + s * .1f, b.Y - s * .95f, b.X + s * .32f, b.Y - s * .55f, b.X, b.Y - s * .15f);
        foreach (var side in new[] { -1f, 1f })
        {
            lily.MoveTo(b.X + side * s * .06f, b.Y - s * .3f);
            lily.CubicTo(b.X + side * s * .5f, b.Y - s * .75f, b.X + side * s * .85f, b.Y - s * .35f, b.X + side * s * .55f, b.Y - s * .02f);
            lily.CubicTo(b.X + side * s * .48f, b.Y - s * .22f, b.X + side * s * .25f, b.Y - s * .28f, b.X + side * s * .06f, b.Y - s * .12f);
            lily.Close();
        }
        lily.AddRect(new SKRect(b.X - s * .28f, b.Y - s * .2f, b.X + s * .28f, b.Y - s * .08f));
        fill.Color = new SKColor(0xB0, 0x82, 0x2C, 230); canvas.DrawPath(lily, fill); canvas.DrawPath(lily, outline);

        // The gilt disc with a small rosette.
        fill.Color = new SKColor(0xC2, 0x96, 0x40); canvas.DrawCircle(c, radius * .13f, fill);
        canvas.DrawCircle(c, radius * .13f, outline);
        for (var k = 0; k < 8; k++)
        {
            var a = k * MathF.Tau / 8;
            canvas.Save(); canvas.RotateRadians(a, c.X, c.Y);
            canvas.DrawOval(new SKRect(c.X - radius * .02f, c.Y - radius * .11f, c.X + radius * .02f, c.Y - radius * .03f), outline);
            canvas.Restore();
        }

        // Uneven pigment over everything drawn in the layer.
        using (var grain = Noise((int)(radius * 1.2f), (int)(radius * 1.2f), random.Next(), .78f))
        using (var modulate = new SKPaint { BlendMode = SKBlendMode.Modulate })
            canvas.DrawImage(grain, new SKRect(c.X - radius * 1.5f, c.Y - radius * 1.5f, c.X + radius * 1.5f, c.Y + radius * 1.5f), new SKSamplingOptions(SKFilterMode.Linear), modulate);
        canvas.Restore();
    }

    /// <summary>Text as if by hand with a pen: each letter turned and lifted a little, the ink heavier and lighter.</summary>
    private static void Handwriting(SKCanvas canvas, string text, float x, float y, SKFont font, Random random)
    {
        using var ink = new SKPaint { IsAntialias = true };
        foreach (var ch in text)
        {
            var glyph = ch.ToString();
            canvas.Save();
            canvas.Translate(x, y + ((float)random.NextDouble() - .5f) * 2.6f);
            canvas.RotateDegrees(((float)random.NextDouble() - .5f) * 7);
            ink.Color = new SKColor(0x4A, 0x2A, 0x12, (byte)random.Next(160, 235));
            canvas.DrawText(glyph, 0, 0, font, ink);
            canvas.Restore();
            x += font.MeasureText(glyph) * (.95f + .08f * (float)random.NextDouble());
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Smooth grey value noise (light), drawn multiplied over the paper as its grain and blotches.</summary>
    private static SKImage Noise(int width, int height, int seed, float floor)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height];
        var field = new float[width * height];
        foreach (var (cell, weight) in new[] { (64, .5f), (24, .3f), (8, .2f) })
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
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(255 * (floor + (1 - floor) * field[i]));
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var data = SKData.CreateCopy(pixels);
        return SKImage.FromPixels(info, data, width);
    }

    private static void DrawTableGrain(SKCanvas canvas, SKRectI bounds)
    {
        using var grain = Noise(bounds.Width / 6, bounds.Height / 6, 3, .55f);
        using var paint = new SKPaint { BlendMode = SKBlendMode.Multiply };
        canvas.DrawImage(grain, SKRect.Create(bounds.Width, bounds.Height), new SKSamplingOptions(SKFilterMode.Linear), paint);
    }

    private static SKImage RenderMap(MapDocument document, IStampImageSource source, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColor.Parse(SeaColor));
        surface.Canvas.Scale(K);
        var view = new WorldBounds(0, 0, Width, Height);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, Stamps = source, VisibleWorld = view, ViewScale = K });
        return surface.Snapshot();
    }

    private static void Save(AreaBeautySheetReport report, PerformanceOptions options, string key, SKImage sheet, int width, SKRectI? crop)
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

    /// <summary>The sheet's map: an island with a plateau, a river, a road, three names and the owner's mountains.</summary>
    private static (MapDocument Document, IStampImageSource Source, Dictionary<string, SKImage> Images) SampleMap()
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
                ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "r09f-sheet", "Map", png));
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
        var objectLayer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects, .. labels]) { Rivers = [river], Roads = [road] };
        var map = new MapDocument(DocumentId.New(), "Хендауты", DateTimeOffset.UnixEpoch, new CanvasDefinition(Width, Height, "px", SeaColor),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, SeaColor) { Sea = SeaStyle.NewMapDefault }, plain, plateau, objectLayer], [.. assets]);
        return (map, new Images(images), images);
    }
}
