using Map.Core.Documents;
using SkiaSharp;

namespace Map.Export;

/// <summary>
/// The handout sheets (R09f, owner's choice 2026-11-11 from the variants page: 1, 2, 3В, 3Д, 4Г): the rendered map turned
/// into an old sheet lying on a dark table. «Найденная карта», «Обгоревший лист» and «Свиток тушью» are drawn here
/// (lived paper: crumple net, worn ink, blotches, uneven dark edge; pen and wash for the rolls and the seal);
/// «Свиток на костяном стержне» and «Портолан на коже» are blank sheets with their objects drawn by GPT-6.1-Sol
/// (embedded <c>Handouts/*.jpg</c>), the map laid into their blank area by Multiply as ink soaked into the paper.
/// </summary>
public static class HandoutSheet
{
    /// <summary>The long side of the map rendered for a handout; the sheet adds the table around it.</summary>
    public const int MapLongSide = 2400;

    /// <summary>The table around a drawn sheet.</summary>
    private const int Margin = 130;

    private static readonly SKColor Table = new(0x2B, 0x22, 0x19);

    /// <summary>True when the handout lays the map into a sheet drawn by Sol: the map is then rendered on white paper without
    /// the sea's colour, so only its strokes soak into the sheet.</summary>
    public static bool NeedsPaperMap(MapHandout handout) => handout is MapHandout.ScrollBone or MapHandout.PortolanSkin;

    /// <summary>The size to render the map at: <see cref="MapLongSide"/> on the long side, the canvas's proportions.</summary>
    public static (int Width, int Height) MapSize(int canvasWidth, int canvasHeight)
    {
        var aspect = Math.Clamp(canvasWidth / (double)Math.Max(1, canvasHeight), .4, 2.5);
        return aspect >= 1
            ? (MapLongSide, Math.Max(1, (int)Math.Round(MapLongSide / aspect)))
            : (Math.Max(1, (int)Math.Round(MapLongSide * aspect)), MapLongSide);
    }

    /// <summary>The whole handout: the sheet with <paramref name="map"/> on the table.</summary>
    public static SKImage Compose(MapHandout handout, SKImage map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (handout is MapHandout.ScrollBone or MapHandout.PortolanSkin)
        {
            var sheet = handout == MapHandout.ScrollBone ? ScrollBone : PortolanSkin;
            using var source = LoadSheet(sheet.Id);
            // Twice the drawn sheet's pixels: the map soaks in at the detail it was rendered with.
            using var big = Enlarged(source, 2);
            var at = new DrawnSheet(sheet.Id, new SKRectI(sheet.Area.Left * 2, sheet.Area.Top * 2, sheet.Area.Right * 2, sheet.Area.Bottom * 2),
                new SKPointI(sheet.Prop.X * 2, sheet.Prop.Y * 2));
            using var drawn = SKSurface.Create(new SKImageInfo(big.Width + 2 * Margin, big.Height + 2 * Margin));
            OnTable(drawn.Canvas);
            Drawn(drawn.Canvas, map, big, at);
            return drawn.Snapshot();
        }
        if (handout is not (MapHandout.Found or MapHandout.Burnt or MapHandout.ScrollInk))
            throw new ArgumentOutOfRangeException(nameof(handout), handout, "Нет хендаута.");
        using var surface = SKSurface.Create(new SKImageInfo(map.Width + 2 * Margin, map.Height + 2 * Margin));
        OnTable(surface.Canvas);
        var r = new SKRect(Margin, Margin, Margin + map.Width, Margin + map.Height);
        if (handout == MapHandout.Found) Found(surface.Canvas, r, map);
        else if (handout == MapHandout.Burnt) Burnt(surface.Canvas, r, map);
        else ScrollInk(surface.Canvas, r, map);
        return surface.Snapshot();
    }

    private static void OnTable(SKCanvas canvas)
    {
        canvas.Clear(Table);
        DrawTableGrain(canvas, canvas.DeviceClipBounds);
    }

    // ------------------------------------------------------------------ sheets drawn by GPT-6.1-Sol with the map laid on them

    /// <summary>One drawn sheet: its resource id, the blank area for the map (in the image's pixels), where its object lies,
    /// and an optional band of rows stretched taller.</summary>
    private sealed record DrawnSheet(string Id, SKRectI Area, SKPointI Prop, (int From, int To, float Factor)? Stretch = null);

    private static readonly DrawnSheet ScrollBone = new("scroll-bone", new SKRectI(180, 110, 1500, 870), new SKPointI(1435, 805));
    private static readonly DrawnSheet PortolanSkin = new("portolan-skin", new SKRectI(170, 80, 1500, 920), new SKPointI(268, 215));

    private static SKBitmap LoadSheet(string id)
    {
        using var stream = typeof(HandoutSheet).Assembly.GetManifestResourceStream("Map.Export.Handouts." + id + ".jpg")
            ?? throw new InvalidOperationException("Нет листа хендаута " + id + ".");
        return SKBitmap.Decode(stream) ?? throw new InvalidOperationException("Лист хендаута " + id + " не читается.");
    }

    private static SKBitmap Enlarged(SKBitmap source, int factor)
    {
        var big = new SKBitmap(source.Width * factor, source.Height * factor);
        using var canvas = new SKCanvas(big);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, SKRect.Create(big.Width, big.Height), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        return big;
    }

    private static SKPoint? Drawn(SKCanvas canvas, SKImage map, SKBitmap source, DrawnSheet sheet)
    {
        var area = sheet.Area;
        var prop = sheet.Prop;
        SKBitmap bitmap;
        if (sheet.Stretch is { } s)
        {
            var extra = (int)((s.To - s.From) * (s.Factor - 1));
            bitmap = new SKBitmap(source.Width, source.Height + extra);
            using var bc = new SKCanvas(bitmap);
            var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var full = SKImage.FromBitmap(source);
            bc.DrawImage(full, SKRect.Create(0, 0, source.Width, s.From), SKRect.Create(0, 0, source.Width, s.From), sampling, null);
            bc.DrawImage(full, SKRect.Create(0, s.From, source.Width, s.To - s.From), SKRect.Create(0, s.From, source.Width, (s.To - s.From) * s.Factor), sampling, null);
            bc.DrawImage(full, SKRect.Create(0, s.To, source.Width, source.Height - s.To), SKRect.Create(0, s.To + extra, source.Width, source.Height - s.To), sampling, null);
            area = new SKRectI(area.Left, area.Top, area.Right, area.Bottom + extra);
            if (prop.Y > s.To) prop = new SKPointI(prop.X, prop.Y + extra);
        }
        else bitmap = source.Copy();

        int bw = bitmap.Width, bh = bitmap.Height;
        var pixels = bitmap.Pixels;

        // Where the map may go: inside the fitted map rectangle, fading at its edge, not over objects.
        var fit = Math.Min(area.Width * .94f / map.Width, area.Height * .94f / map.Height);
        var place = SKRect.Create(area.MidX - map.Width * fit / 2, area.MidY - map.Height * fit / 2, map.Width * fit, map.Height * fit);
        var mask = new float[bw * bh];
        // The paper's own warmth (median red minus blue over the area): objects are what is much warmer than it.
        var warmth = new List<int>();
        for (var y = (int)place.Top; y < (int)place.Bottom; y += 4)
            for (var x = (int)place.Left; x < (int)place.Right; x += 4)
                warmth.Add(pixels[y * bw + x].Red - pixels[y * bw + x].Blue);
        warmth.Sort();
        var paperWarmth = warmth[warmth.Count / 2];
        for (var y = (int)place.Top; y < (int)place.Bottom; y++)
            for (var x = (int)place.Left; x < (int)place.Right; x++)
            {
                var c = pixels[y * bw + x];
                float lum = .3f * c.Red + .59f * c.Green + .11f * c.Blue;
                var saturated = Math.Clamp((c.Red - c.Blue - Math.Max(70, paperWarmth + 55)) / 40f, 0, 1);
                var green = Math.Clamp((c.Green - c.Red - 10) / 30f, 0, 1);
                var dark = Math.Clamp((95 - lum) / 40f, 0, 1);
                var keep = Math.Max(saturated, Math.Max(green, dark));
                var edge = Math.Min(Math.Min(x - place.Left, place.Right - x), Math.Min(y - place.Top, place.Bottom - y));
                mask[y * bw + x] = (1 - keep) * Smooth(0, 60, edge);
            }
        var maskBytes = new byte[bw * bh * 4];
        for (var i = 0; i < mask.Length; i++) maskBytes[i * 4 + 3] = (byte)(255 * mask[i]);
        using var maskData = SKData.CreateCopy(maskBytes);
        using var maskImage = SKImage.FromPixels(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul), maskData, bw * 4);

        using var surface = SKSurface.Create(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul));
        var sc = surface.Canvas;
        using (var baseImage = SKImage.FromBitmap(bitmap)) sc.DrawImage(baseImage, 0, 0);
        using (var multiply = new SKPaint { BlendMode = SKBlendMode.Multiply })
        {
            sc.SaveLayer(multiply);
            sc.Clear(SKColors.White);
            using (var aged = new SKPaint { ColorFilter = Sepia(.55f) })
                sc.DrawImage(map, place, new SKSamplingOptions(SKCubicResampler.Mitchell), aged);
            // Outside the mask the layer must be white (no change under Multiply): draw white through the inverted mask.
            using (var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn }) sc.DrawImage(maskImage, 0, 0, dstIn);
            using (var under = new SKPaint { BlendMode = SKBlendMode.DstOver, Color = SKColors.White }) sc.DrawRect(SKRect.Create(bw, bh), under);
            sc.Restore();
        }
        bitmap.Dispose();

        // The white background off: flood over light, neutral pixels from the border; they keep a shadow's darkness.
        using var composed = surface.Snapshot();
        using var result = SKBitmap.FromImage(composed);
        var px = result.Pixels;
        var background = new bool[bw * bh];
        var queue = new Queue<int>();
        bool Light(SKColor c)
        {
            int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            return min > 175 && max - min < 16;
        }
        for (var x = 0; x < bw; x++) { queue.Enqueue(x); queue.Enqueue((bh - 1) * bw + x); }
        for (var y = 0; y < bh; y++) { queue.Enqueue(y * bw); queue.Enqueue(y * bw + bw - 1); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            if (background[i] || !Light(px[i])) continue;
            background[i] = true;
            int x = i % bw, y = i / bw;
            if (x > 0) queue.Enqueue(i - 1);
            if (x < bw - 1) queue.Enqueue(i + 1);
            if (y > 0) queue.Enqueue(i - bw);
            if (y < bh - 1) queue.Enqueue(i + bw);
        }
        var outBytes = new byte[bw * bh * 4];
        for (var i = 0; i < px.Length; i++)
        {
            var c = px[i];
            // Also near-white pixels the flood could not reach (inside a loop of the cord).
            if (background[i] || (Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 232 && Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) < 12))
            {
                float lum = .3f * c.Red + .59f * c.Green + .11f * c.Blue;
                outBytes[i * 4 + 3] = (byte)(255 * Math.Clamp((246 - lum) / 90f, 0, .8f));
                continue;
            }
            outBytes[i * 4] = c.Red; outBytes[i * 4 + 1] = c.Green; outBytes[i * 4 + 2] = c.Blue; outBytes[i * 4 + 3] = 255;
        }
        using var outData = SKData.CreateCopy(outBytes);
        using var cut = SKImage.FromPixels(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul), outData, bw * 4);

        // On the table: as large as fits with a margin.
        var bounds = canvas.DeviceClipBounds;
        var scale = Math.Min((bounds.Width - 60f) / bw, (bounds.Height - 60f) / bh);
        var dest = SKRect.Create(bounds.MidX - bw * scale / 2, bounds.MidY - bh * scale / 2, bw * scale, bh * scale);
        canvas.DrawImage(cut, dest, new SKSamplingOptions(SKCubicResampler.Mitchell));
        return new SKPoint(dest.Left + prop.X * scale, dest.Top + prop.Y * scale);
    }

    // ------------------------------------------------------------------ Claude's sheets: objects in ink and wash

    private static readonly SKColor Ink = new(0x2A, 0x20, 0x18);

    private static SKPaint Pen(float width, byte alpha, Random random) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        Color = Ink.WithAlpha(alpha), PathEffect = SKPathEffect.CreateDiscrete(9, .8f, (uint)random.Next()),
    };

    /// <summary>A watercolour wash: the colour laid by Multiply, darker towards its edge as a dried wash is, uneven inside.</summary>
    private static void Wash(SKCanvas canvas, SKPath shape, SKColor color, byte alpha, Random random)
    {
        canvas.Save();
        canvas.ClipPath(shape, antialias: true);
        using (var fill = new SKPaint { IsAntialias = true, Color = color.WithAlpha(alpha), BlendMode = SKBlendMode.Multiply })
            canvas.DrawPath(shape, fill);
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, Color = color.WithAlpha((byte)(alpha * .7f)),
                   BlendMode = SKBlendMode.Multiply, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3) })
            canvas.DrawPath(shape, rim);
        var b = shape.Bounds;
        using (var grain = Noise(Math.Max(2, (int)b.Width / 4), Math.Max(2, (int)b.Height / 4), random.Next(), .75f))
        using (var multiply = new SKPaint { BlendMode = SKBlendMode.Multiply })
            canvas.DrawImage(grain, b, new SKSamplingOptions(SKFilterMode.Linear), multiply);
        canvas.Restore();
    }

    /// <summary>Hatching inside a shape: parallel pen lines at an angle, only over the shaded part (the side away from
    /// the light, given as a half-plane through <paramref name="from"/> towards <paramref name="towards"/>).</summary>
    private static void Hatch(SKCanvas canvas, SKPath shape, float angle, float spacing, SKPoint from, SKPoint towards, Random random)
    {
        canvas.Save();
        canvas.ClipPath(shape, antialias: true);
        var b = shape.Bounds;
        float ux = MathF.Cos(angle), uy = MathF.Sin(angle), nx = -uy, ny = ux;
        float dx = towards.X - from.X, dy = towards.Y - from.Y, dl = MathF.Sqrt(dx * dx + dy * dy);
        var reach = MathF.Sqrt(b.Width * b.Width + b.Height * b.Height);
        using var pen = Pen(1.3f, 170, random);
        for (var s = -reach; s <= reach; s += spacing)
        {
            var cx = b.MidX + nx * s; var cy = b.MidY + ny * s;
            // Only where the line runs through the shaded side; it thins out toward the light.
            var t = ((cx - from.X) * dx + (cy - from.Y) * dy) / (dl * dl);
            if (t < .1f) continue;
            pen.Color = Ink.WithAlpha((byte)(Math.Clamp(t, 0, 1) * 170));
            canvas.DrawLine(cx - ux * reach, cy - uy * reach, cx + ux * reach, cy + uy * reach, pen);
        }
        canvas.Restore();
    }

    /// <summary>Scroll in ink: the lived paper, rolls drawn as cylinders in pen line with hatched undersides and a
    /// spiral at each end, a wax seal in red wash with a pen outline and ribbons.</summary>
    private static void ScrollInk(SKCanvas canvas, SKRect r, SKImage map)
    {
        var random = new Random(61);
        var paper = new SKRect(r.Left + 40, r.Top + 60, r.Right - 40, r.Bottom - 60);
        using var edge = Jagged(paper, random, amplitude: 4, waves: 30, notches: 3, notchDepth: 14);
        Sheet(canvas, edge, paper, map, tone: new SKColor(0xEA, 0xD6, 0xA8), toneAlpha: 95, edgeDark: 130, () => { },
            life: new Life(61, Crumple: .5f, CreaseSize: 1.3f, Wear: .35f, Blotch: .8f), mapRect: r);
        foreach (var y in new[] { paper.Top, paper.Bottom })
        {
            var radius = 52f;
            var body = new SKRect(paper.Left - 30, y - radius, paper.Right + 30, y + radius);
            using var roll = new SKPath();
            roll.AddRoundRect(body, radius * .35f, radius);
            using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 90), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) })
            { canvas.Save(); canvas.Translate(6, 12); canvas.DrawPath(roll, shadow); canvas.Restore(); }
            using (var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xE4, 0xCF, 0x9E) }) canvas.DrawPath(roll, fill);
            Wash(canvas, roll, new SKColor(0xC8, 0xA8, 0x70), 120, random);
            Hatch(canvas, roll, -.35f, 6, new SKPoint(body.MidX, body.MidY), new SKPoint(body.MidX, body.Bottom), random);
            using var pen = Pen(2.4f, 230, random);
            canvas.DrawPath(roll, pen);
            // The outer layer's free edge and a few lines of the layers along the roll.
            using var lip = new SKPath();
            lip.MoveTo(body.Left + 40, y + radius * .25f);
            for (var x = body.Left + 40; x < body.Right - 40; x += 60) lip.LineTo(x, y + radius * .25f + (float)(random.NextDouble() - .5) * 6);
            canvas.DrawPath(lip, Pen(1.6f, 200, random));
            foreach (var cx in new[] { body.Left + radius * .35f, body.Right - radius * .35f })
            {
                using var spiral = new SKPath();
                for (var t = 0f; t <= 3.5f * MathF.Tau; t += .08f)
                {
                    var k = .1f + .9f * t / (3.5f * MathF.Tau);
                    var p = new SKPoint(cx + radius * .3f * k * MathF.Cos(t), y + radius * .92f * k * MathF.Sin(t));
                    if (t == 0) spiral.MoveTo(p); else spiral.LineTo(p);
                }
                canvas.DrawPath(spiral, Pen(1.5f, 210, random));
            }
        }
        // Seal and ribbons, in wash and pen.
        var c = new SKPoint(paper.Right - 240, paper.Bottom + 10);
        foreach (var (dx, len) in new[] { (-60f, 190f), (70f, 170f) })
        {
            using var ribbon = new SKPath();
            ribbon.MoveTo(c.X - 14, c.Y);
            ribbon.CubicTo(c.X + dx * .3f - 10, c.Y + len * .4f, c.X + dx * .8f - 20, c.Y + len * .6f, c.X + dx - 16, c.Y + len);
            ribbon.LineTo(c.X + dx, c.Y + len - 18);
            ribbon.LineTo(c.X + dx + 16, c.Y + len + 4);
            ribbon.CubicTo(c.X + dx * .8f + 18, c.Y + len * .6f, c.X + dx * .3f + 14, c.Y + len * .4f, c.X + 14, c.Y);
            ribbon.Close();
            using (var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xE8, 0xD8, 0xB8) }) canvas.DrawPath(ribbon, fill);
            Wash(canvas, ribbon, new SKColor(0x9A, 0x22, 0x1A), 210, random);
            Hatch(canvas, ribbon, 1.2f, 5, new SKPoint(c.X + dx * .5f, c.Y), new SKPoint(c.X + dx * .5f + 20, c.Y + 10), random);
            canvas.DrawPath(ribbon, Pen(1.8f, 230, random));
        }
        InkSeal(canvas, c, 78, random, broken: false);
    }

    /// <summary>A wax seal drawn by hand: an uneven blob in red wash, the pressed disc as a second pen ring with hatching
    /// on its shaded half, a small mountain emblem; or the same cracked in two with crumbs.</summary>
    private static void InkSeal(SKCanvas canvas, SKPoint c, float radius, Random random, bool broken)
    {
        void One(SKPoint at, SKPath? clip)
        {
            canvas.Save();
            if (clip is not null) canvas.ClipPath(clip, antialias: true);
            using var blob = Blob(at, radius, random);
            using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 80), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
            { canvas.Save(); canvas.Translate(5, 8); canvas.DrawPath(blob, shadow); canvas.Restore(); }
            using (var fill = new SKPaint { IsAntialias = true, Color = new SKColor(0xEC, 0xDC, 0xC0) }) canvas.DrawPath(blob, fill);
            Wash(canvas, blob, new SKColor(0xA0, 0x24, 0x1A), 235, random);
            using var disc = new SKPath(); disc.AddCircle(at.X, at.Y, radius * .6f);
            Wash(canvas, disc, new SKColor(0xB0, 0x40, 0x34), 90, random);
            Hatch(canvas, blob, .8f, 5, at, new SKPoint(at.X + radius, at.Y + radius), random);
            canvas.DrawPath(blob, Pen(2.4f, 235, random));
            canvas.DrawPath(disc, Pen(1.8f, 220, random));
            using var peaks = new SKPath();
            peaks.MoveTo(at.X - radius * .38f, at.Y + radius * .2f);
            peaks.LineTo(at.X - radius * .15f, at.Y - radius * .12f); peaks.LineTo(at.X - radius * .02f, at.Y + radius * .04f);
            peaks.LineTo(at.X + radius * .14f, at.Y - radius * .3f); peaks.LineTo(at.X + radius * .38f, at.Y + radius * .2f);
            canvas.DrawPath(peaks, Pen(1.8f, 230, random));
            canvas.Restore();
        }
        if (!broken) { One(c, null); return; }
        var crack = new List<SKPoint>();
        for (var y = -radius * 1.6f; y <= radius * 1.6f; y += radius * .16f)
            crack.Add(new SKPoint(c.X + (float)(random.NextDouble() - .5) * radius * .22f + y * .25f, c.Y + y));
        foreach (var side in new[] { -1f, 1f })
        {
            using var half = new SKPath();
            half.AddPoly([.. crack, new(c.X + side * radius * 3, crack[^1].Y), new(c.X + side * radius * 3, crack[0].Y)]);
            half.Offset(side * 8, side * -3);
            One(new SKPoint(c.X + side * 8, c.Y - side * 3), half);
            using var line = new SKPath(); line.AddPoly([.. crack], close: false); line.Offset(side * 8, side * -3);
            canvas.Save(); canvas.ClipPath(half, antialias: true);
            canvas.DrawPath(line, Pen(2f, 230, random));
            canvas.Restore();
        }
        using var crumb = new SKPaint { IsAntialias = true, Color = new SKColor(0x8A, 0x20, 0x18) };
        for (var k = 0; k < 6; k++)
        {
            var p = new SKPoint(c.X + (float)(random.NextDouble() - .3) * radius * 1.6f, c.Y + (float)(random.NextDouble() < .5 ? -1 : 1) * radius * (1.1f + .4f * (float)random.NextDouble()));
            using var bit = Blob(p, 3 + (float)random.NextDouble() * 3, random);
            canvas.DrawPath(bit, crumb);
            canvas.DrawPath(bit, Pen(1f, 200, random));
        }
    }

    // ------------------------------------------------------------------ «Найденная карта» and «Обгоревший лист»

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

    // ------------------------------------------------------------------ the sheet

    /// <summary>The map clipped to the sheet's edge, on its shadow, aged: paper tone, grain, darker towards the edge.</summary>
    private static void Sheet(SKCanvas canvas, SKPath edge, SKRect r, SKImage map, SKColor tone, byte toneAlpha, byte edgeDark, Action inside, float age = .6f, Life? life = null, SKRect? mapRect = null)
    {
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 150), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16) })
        {
            canvas.Save(); canvas.Translate(10, 16); canvas.DrawPath(edge, shadow); canvas.Restore();
        }
        canvas.Save();
        canvas.ClipPath(edge, antialias: true);
        using (var aged = new SKPaint { ColorFilter = Sepia(age) })
            canvas.DrawImage(map, mapRect ?? r, new SKSamplingOptions(SKFilterMode.Linear), aged);
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
        // The way was laid on a 1600 x 1000 map; on another map it keeps its place in fractions of the sheet.
        SKPoint P((double X, double Y) p) => new((float)(r.Left + p.X / 1600 * r.Width), (float)(r.Top + p.Y / 1000 * r.Height));
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
}
