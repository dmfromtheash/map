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
        }
        report.GatePassed = report.Images.Count == looks.Length * 2;
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
            Panels(canvas, r, 2, 2);
            Fold(canvas, new SKPoint(r.MidX, r.Top), new SKPoint(r.MidX, r.Bottom));
            Fold(canvas, new SKPoint(r.Left, r.MidY), new SKPoint(r.Right, r.MidY));
            Stain(canvas, new SKPoint(r.Left + r.Width * .78f, r.Top + r.Height * .24f), 95, 6);
            Stain(canvas, new SKPoint(r.Left + r.Width * .83f, r.Top + r.Height * .30f), 60, 4);
            Stain(canvas, new SKPoint(r.Left + r.Width * .14f, r.Top + r.Height * .80f), 120, 7);
            Way(canvas, r, [(330, 640), (500, 600), (680, 640), (860, 590), (980, 700), (1010, 840)], (1010, 860));
        }, age: .7f);
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
        });
        Roll(canvas, paper.Left - 26, paper.Right + 26, paper.Top, 46);
        Roll(canvas, paper.Left - 26, paper.Right + 26, paper.Bottom, 46);
        Seal(canvas, new SKPoint(paper.Right - 210, paper.Bottom - 30), 74, random, ribbons: true, broken: false);
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
            SKColor[] inks = [new(0x22, 0x1E, 0x1A, 105), new(0x3C, 0x6A, 0x3A, 90), new(0xA0, 0x2C, 0x1E, 90)];
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, BlendMode = SKBlendMode.Multiply };
            foreach (var hub in hubs)
                for (var k = 0; k < 32; k++)
                {
                    var a = k * MathF.PI / 16;
                    line.Color = inks[k % 4 == 0 ? 0 : k % 2 == 0 ? 1 : 2];
                    canvas.DrawLine(hub, new SKPoint(hub.X + 5000 * MathF.Cos(a), hub.Y + 5000 * MathF.Sin(a)), line);
                }
            Rose(canvas, centre, 92);
        });
    }

    /// <summary>A message by courier: a clean sheet folded in three, a broken seal, a note in the margin.</summary>
    private static void Letter(SKCanvas canvas, SKRect r, SKImage map)
    {
        var random = new Random(53);
        using var edge = Jagged(r, random, amplitude: 1.2, waves: 40, notches: 0, notchDepth: 0);
        Sheet(canvas, edge, r, map, tone: new SKColor(0xF0, 0xE4, 0xC6), toneAlpha: 70, edgeDark: 70, () =>
        {
            Panels(canvas, r, 1, 3);
            Fold(canvas, new SKPoint(r.Left, r.Top + r.Height / 3), new SKPoint(r.Right, r.Top + r.Height / 3));
            Fold(canvas, new SKPoint(r.Left, r.Top + r.Height * 2 / 3), new SKPoint(r.Right, r.Top + r.Height * 2 / 3));
            using var typeface = SKTypeface.FromFile(Path.Combine(Environment.CurrentDirectory, "src", "Map.App", "Assets", "Fonts", "Cormorant", "CormorantGaramond-MediumItalic.ttf"))
                ?? SKTypeface.FromFamilyName("Georgia", SKFontStyle.Italic);
            using var font = new SKFont(typeface, 54);
            using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0x3A, 0x22, 0x0E, 225) };
            canvas.Save();
            canvas.RotateDegrees(-2.5f, r.Left + 1250, r.Top + 120);
            canvas.DrawText("До перевала три дня пути.", r.Left + 1250, r.Top + 110, font, ink);
            canvas.DrawText("Сжечь по прочтении. — Р.", r.Left + 1290, r.Top + 170, font, ink);
            canvas.Restore();
        }, age: .4f);
        Seal(canvas, new SKPoint(r.Right - 150, r.Top + r.Height * 2 / 3), 62, random, ribbons: false, broken: true);
    }

    // ------------------------------------------------------------------ the sheet

    /// <summary>The map clipped to the sheet's edge, on its shadow, aged: paper tone, grain, darker towards the edge.</summary>
    private static void Sheet(SKCanvas canvas, SKPath edge, SKRect r, SKImage map, SKColor tone, byte toneAlpha, byte edgeDark, Action inside, float age = .6f)
    {
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16) })
        {
            canvas.Save(); canvas.Translate(10, 16); canvas.DrawPath(edge, shadow); canvas.Restore();
        }
        canvas.Save();
        canvas.ClipPath(edge, antialias: true);
        using (var aged = new SKPaint { ColorFilter = Sepia(age) })
            canvas.DrawImage(map, new SKRect(Margin, Margin, Margin + map.Width, Margin + map.Height), new SKSamplingOptions(SKFilterMode.Linear), aged);
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

    /// <summary>The panels between folds differ a little in tone, as paper bent different ways catches light.</summary>
    private static void Panels(SKCanvas canvas, SKRect r, int columns, int rows)
    {
        var random = new Random(columns * 10 + rows);
        for (var i = 0; i < columns; i++)
            for (var j = 0; j < rows; j++)
            {
                var cell = new SKRect(r.Left + r.Width * i / columns, r.Top + r.Height * j / rows, r.Left + r.Width * (i + 1) / columns, r.Top + r.Height * (j + 1) / rows);
                using var paint = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(cell.Left, cell.Top), new SKPoint(cell.Right, cell.Bottom),
                        [new SKColor(0, 0, 0, (byte)random.Next(0, 26)), new SKColor(0, 0, 0, (byte)random.Next(0, 26))], SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(cell, paint);
            }
    }

    /// <summary>A crease: a soft valley, a dark line in it and a light ridge beside.</summary>
    private static void Fold(SKCanvas canvas, SKPoint a, SKPoint b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y; var len = MathF.Sqrt(dx * dx + dy * dy);
        var nx = -dy / len; var ny = dx / len;
        using var valley = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(a.X - nx * 34, a.Y - ny * 34), new SKPoint(a.X + nx * 34, a.Y + ny * 34),
                [SKColors.Transparent, new SKColor(0x40, 0x2C, 0x14, 70), new SKColor(255, 250, 235, 60), SKColors.Transparent], [0, .48f, .56f, 1], SKShaderTileMode.Clamp),
        };
        using var band = new SKPath();
        band.AddPoly([new(a.X - nx * 34, a.Y - ny * 34), new(b.X - nx * 34, b.Y - ny * 34), new(b.X + nx * 34, b.Y + ny * 34), new(a.X + nx * 34, a.Y + ny * 34)]);
        canvas.DrawPath(band, valley);
        using var dark = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, Color = new SKColor(0x4A, 0x34, 0x1C, 130), BlendMode = SKBlendMode.Multiply };
        canvas.DrawLine(a, b, dark);
        using var light = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(255, 252, 240, 110) };
        canvas.DrawLine(new SKPoint(a.X + nx * 3, a.Y + ny * 3), new SKPoint(b.X + nx * 3, b.Y + ny * 3), light);
    }

    /// <summary>The ring a wet cup leaves.</summary>
    private static void Stain(SKCanvas canvas, SKPoint c, float radius, float width)
    {
        using var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0x8A, 0x5A, 0x2A, 26), BlendMode = SKBlendMode.Multiply };
        canvas.DrawCircle(c, radius, fill);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = new SKColor(0x6B, 0x40, 0x18, 90),
            BlendMode = SKBlendMode.Multiply, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.6f) };
        canvas.DrawCircle(c, radius, ring);
        canvas.DrawArc(new SKRect(c.X - radius - 9, c.Y - radius - 7, c.X + radius + 9, c.Y + radius + 7), 200, 110, false, ring);
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

    /// <summary>A rolled end of the scroll: a shaded cylinder with the spiral at both ends.</summary>
    private static void Roll(SKCanvas canvas, float left, float right, float y, float radius)
    {
        var body = new SKRect(left + radius * .4f, y - radius, right - radius * .4f, y + radius);
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) })
            canvas.DrawRoundRect(new SKRect(body.Left, body.Top + 12, body.Right, body.Bottom + 12), radius, radius, shadow);
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, body.Top), new SKPoint(0, body.Bottom),
                [new SKColor(0x8A, 0x66, 0x36), new SKColor(0xF4, 0xE2, 0xB8), new SKColor(0xD6, 0xB8, 0x80), new SKColor(0x6E, 0x4E, 0x26)],
                [0, .28f, .55f, 1], SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(body, paint);
        foreach (var x in new[] { body.Left, body.Right })
        {
            using var end = new SKPaint { IsAntialias = true, Color = new SKColor(0xC8, 0xA8, 0x72) };
            canvas.DrawOval(new SKRect(x - radius * .4f, y - radius, x + radius * .4f, y + radius), end);
            using var spiral = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, Color = new SKColor(0x6A, 0x4A, 0x22, 200) };
            for (var k = 1; k <= 4; k++)
                canvas.DrawOval(new SKRect(x - radius * .4f * k / 4.5f, y - radius * k / 4.5f, x + radius * .4f * k / 4.5f, y + radius * k / 4.5f), spiral);
        }
    }

    /// <summary>Red sealing wax: an uneven disc, a pressed ring and star; whole with ribbons, or broken in two.</summary>
    private static void Seal(SKCanvas canvas, SKPoint c, float radius, Random random, bool ribbons, bool broken)
    {
        if (ribbons)
        {
            using var ribbon = new SKPaint { IsAntialias = true, Color = new SKColor(0x6E, 0x14, 0x10) };
            using var tailA = new SKPath(); tailA.AddPoly([new(c.X - 20, c.Y), new(c.X + 10, c.Y), new(c.X - 30, c.Y + 170), new(c.X - 60, c.Y + 150)]);
            using var tailB = new SKPath(); tailB.AddPoly([new(c.X - 5, c.Y), new(c.X + 25, c.Y), new(c.X + 70, c.Y + 160), new(c.X + 40, c.Y + 178)]);
            canvas.DrawPath(tailA, ribbon); canvas.DrawPath(tailB, ribbon);
        }
        void Half(SKPoint centre, float clipFrom, float clipTo)
        {
            using var disc = Blob(centre, radius, random);
            canvas.Save();
            if (broken) canvas.ClipRect(new SKRect(centre.X + clipFrom, centre.Y - radius * 2, centre.X + clipTo, centre.Y + radius * 2));
            using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
            { canvas.Save(); canvas.Translate(4, 7); canvas.DrawPath(disc, shadow); canvas.Restore(); }
            using (var wax = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(centre.X - radius * .3f, centre.Y - radius * .35f), radius * 1.4f,
                    [new SKColor(0xD2, 0x3A, 0x2E), new SKColor(0x9C, 0x1A, 0x14), new SKColor(0x5E, 0x0C, 0x0A)], [0, .55f, 1], SKShaderTileMode.Clamp),
            })
                canvas.DrawPath(disc, wax);
            using var pressed = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = new SKColor(0x4A, 0x08, 0x06, 200) };
            canvas.DrawCircle(centre, radius * .62f, pressed);
            using var shine = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = new SKColor(0xF2, 0x8A, 0x7A, 160) };
            canvas.DrawArc(new SKRect(centre.X - radius * .62f + 2, centre.Y - radius * .62f + 2, centre.X + radius * .62f + 2, centre.Y + radius * .62f + 2), 200, 120, false, shine);
            using var star = new SKPath();
            for (var i = 0; i < 10; i++)
            {
                var a = -MathF.PI / 2 + i * MathF.PI / 5; var rr = i % 2 == 0 ? radius * .42f : radius * .17f;
                var p = new SKPoint(centre.X + rr * MathF.Cos(a), centre.Y + rr * MathF.Sin(a));
                if (i == 0) star.MoveTo(p); else star.LineTo(p);
            }
            star.Close();
            using var starPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0x58, 0x0A, 0x08, 210) };
            canvas.DrawPath(star, starPaint);
            canvas.Restore();
        }
        if (!broken) { Half(c, 0, 0); return; }
        // Broken along a crack: the halves pulled apart, each keeping its own side.
        Half(new SKPoint(c.X - 14, c.Y + 4), -radius * 2, 4);
        Half(new SKPoint(c.X + 14, c.Y - 3), -4, radius * 2);
    }

    /// <summary>A sixteen-point wind rose in the chart's three inks.</summary>
    private static void Rose(SKCanvas canvas, SKPoint c, float radius)
    {
        SKColor[] inks = [new(0x22, 0x1E, 0x1A), new(0xA0, 0x2C, 0x1E), new(0x3C, 0x6A, 0x3A)];
        for (var layer = 2; layer >= 0; layer--)
        {
            var count = layer == 0 ? 4 : layer == 1 ? 4 : 8;
            var offset = layer == 1 ? MathF.PI / 4 : layer == 2 ? MathF.PI / 8 : 0;
            var length = radius * (layer == 0 ? 1f : layer == 1 ? .72f : .5f);
            for (var i = 0; i < count; i++)
            {
                var a = offset + i * MathF.Tau / count - MathF.PI / 2;
                var tip = new SKPoint(c.X + length * MathF.Cos(a), c.Y + length * MathF.Sin(a));
                var side = radius * .13f;
                using var half1 = new SKPath(); half1.AddPoly([c, tip, new(c.X + side * MathF.Cos(a + MathF.PI / 2), c.Y + side * MathF.Sin(a + MathF.PI / 2))]);
                using var half2 = new SKPath(); half2.AddPoly([c, tip, new(c.X + side * MathF.Cos(a - MathF.PI / 2), c.Y + side * MathF.Sin(a - MathF.PI / 2))]);
                using var dark = new SKPaint { IsAntialias = true, Color = inks[layer] };
                using var paper = new SKPaint { IsAntialias = true, Color = new SKColor(0xEC, 0xDA, 0xB0) };
                using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = inks[0] };
                canvas.DrawPath(half1, dark); canvas.DrawPath(half2, paper); canvas.DrawPath(half1, outline); canvas.DrawPath(half2, outline);
            }
        }
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = inks[0] };
        canvas.DrawCircle(c, radius * .2f, ring);
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
