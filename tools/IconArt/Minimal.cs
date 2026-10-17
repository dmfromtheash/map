using SkiaSharp;

/// <summary>
/// Minimal logo marks in the program's colours (owner: "простым, минималистичным, в наших цветах
/// и при этом броским и красивым"). Each mark is one idea built from few shapes - brass on dark
/// wood, a cream accent, at most a spark of candle orange - so it reads at 16 px and still has a
/// clever second reading: a folded map whose outline is an M, peaks with a river cut out of the
/// valley, a candle flame that is a map pin, a compass of quills, a seal split by a coastline.
/// </summary>
static class Minimal
{
    static readonly SKColor Ground = SKColor.Parse("#1A140F");
    static readonly SKColor GroundTop = SKColor.Parse("#2A2017");
    static readonly SKColor BrassLight = SKColor.Parse("#F1D594");
    static readonly SKColor Brass = SKColor.Parse("#C9A15A");
    static readonly SKColor BrassDeep = SKColor.Parse("#94723C");
    static readonly SKColor Cream = SKColor.Parse("#F3E3BF");
    static readonly SKColor Flame = SKColor.Parse("#FFB347");

    public static void Write(string output)
    {
        var marks = new (string Key, Action<SKCanvas> Draw)[]
        {
            ("1", FoldedM), ("2", PeaksRiver), ("3", CandlePin),
            ("4", QuillCompass), ("5", CoastSeal), ("6", PinMountain),
        };
        foreach (var (key, draw) in marks)
            foreach (var size in new[] { 1024, 256, 64, 32, 16 })
                Art.Save(Path.Combine(output, $"m-{key}-{size}.png"), size, c => { Tile(c); draw(c); });
    }

    /// <summary>The tile: dark wood, a hint of light from the top, one brass hairline - the program's panels, reduced.</summary>
    static void Tile(SKCanvas c)
    {
        var r = new SKRect(8, 8, 248, 248);
        using var body = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 8), new SKPoint(0, 248), [GroundTop, Ground], SKShaderTileMode.Clamp) };
        c.DrawRoundRect(r, 54, 54, body);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Brass.WithAlpha(150) };
        c.DrawRoundRect(new SKRect(13, 13, 243, 243), 49, 49, line);
    }

    /// <summary>Brass with a single soft step: light at the top left, deeper at the bottom right (120°).</summary>
    static SKPaint BrassPaint(SKRect b) => new()
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(new SKPoint(b.Left, b.Top), new SKPoint(b.Right, b.Bottom), [BrassLight, Brass, BrassDeep], [0, .55f, 1], SKShaderTileMode.Clamp),
    };

    static SKPath Poly(params (float X, float Y)[] p)
    {
        var path = new SKPath(); path.MoveTo(p[0].X, p[0].Y);
        foreach (var q in p.Skip(1)) path.LineTo(q.X, q.Y);
        path.Close(); return path;
    }

    // 1 ------------------------------------------------------------- a folded map whose outline is an M
    static void FoldedM(SKCanvas c)
    {
        // four panels of a folded sheet; the top edge zig-zags into the letter
        (float X, float Y)[] top = [(50, 56), (96, 116), (128, 70), (160, 116), (206, 56)];
        float bottom = 196;
        for (var i = 0; i < 4; i++)
        {
            var a = top[i]; var b = top[i + 1];
            using var panel = Poly(a, b, (b.X, bottom + (i % 2 == 0 ? -12 : 12) * -1 + (b.Y - 90) * 0), (a.X, bottom + (i % 2 == 0 ? 0 : 0)));
            panel.Reset();
            var ab = i % 2 == 0 ? 0f : 0f;
            panel.MoveTo(a.X, a.Y); panel.LineTo(b.X, b.Y); panel.LineTo(b.X, bottom + (b.Y - 86) * .35f); panel.LineTo(a.X, bottom + (a.Y - 86) * .35f); panel.Close();
            var lit = i % 2 == 0;
            using var paint = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(a.X, a.Y), new SKPoint(b.X, bottom),
                lit ? [BrassLight, Brass] : [Brass, BrassDeep], SKShaderTileMode.Clamp) };
            c.DrawPath(panel, paint);
        }
        // a single cream route across the sheet, ending in a dot: it is a map, not only a letter
        using var route = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round, Color = Cream,
            PathEffect = SKPathEffect.CreateDash([1, 14], 0) };
        using var path = new SKPath(); path.MoveTo(68, 176); path.CubicTo(100, 156, 138, 194, 182, 158);
        c.DrawPath(path, route);
        using var dot = new SKPaint { IsAntialias = true, Color = Flame }; c.DrawCircle(184, 156, 9, dot);
    }

    // 2 ------------------------------------------------------------- peaks that are an M, a river cut out of the valley
    static void PeaksRiver(SKCanvas c)
    {
        using var m = Poly((40, 196), (94, 64), (128, 132), (162, 64), (216, 196));
        using var river = new SKPath();
        river.MoveTo(128, 132); river.CubicTo(140, 156, 108, 170, 122, 196); river.LineTo(150, 196); river.CubicTo(132, 172, 160, 154, 138, 128); river.Close();
        using var shape = m.Op(river, SKPathOp.Difference)!;
        c.DrawPath(shape, BrassPaint(shape.Bounds));
        // snow caps in cream
        using var snowL = Poly((78, 104), (94, 64), (110, 102), (101, 96), (94, 106), (86, 96));
        using var snowR = Poly((146, 102), (162, 64), (178, 104), (170, 96), (162, 106), (155, 96));
        using var snow = new SKPaint { IsAntialias = true, Color = Cream };
        c.DrawPath(snowL, snow); c.DrawPath(snowR, snow);
    }

    // 3 ------------------------------------------------------------- a candle flame that is a map pin
    static void CandlePin(SKCanvas c)
    {
        // the pin/flame: a drop with a pointed top bent like a flame, standing on its tip
        using var flame = new SKPath();
        flame.MoveTo(128, 216);
        flame.CubicTo(96, 170, 70, 140, 74, 108);
        flame.CubicTo(78, 66, 112, 48, 134, 28);
        flame.CubicTo(138, 52, 184, 70, 182, 112);
        flame.CubicTo(180, 144, 158, 172, 128, 216);
        flame.Close();
        using var glow = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(128, 120), 110,
            [Flame.WithAlpha(70), Flame.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawCircle(128, 120, 110, glow);
        c.DrawPath(flame, BrassPaint(flame.Bounds));
        // the pin's eye is the flame's bright core
        using var core = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(126, 112), 30, [SKColors.White, Cream, Flame], [0, .45f, 1], SKShaderTileMode.Clamp) };
        using var eye = new SKPath(); eye.AddCircle(127, 112, 26);
        c.DrawPath(eye, core);
    }

    // 4 ------------------------------------------------------------- a compass star: lit and shaded halves, a long north
    static void QuillCompass(SKCanvas c)
    {
        var o = new SKPoint(128, 136);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Color = Brass.WithAlpha(200) };
        c.DrawCircle(o, 64, ring);
        for (var i = 0; i < 4; i++)
        {
            var a = -MathF.PI / 2 + i * MathF.PI / 2;
            var len = i == 0 ? 112f : 84f;
            var tip = new SKPoint(o.X + MathF.Cos(a) * len, o.Y + MathF.Sin(a) * len);
            var l = new SKPoint(o.X + MathF.Cos(a - MathF.PI / 4) * 20, o.Y + MathF.Sin(a - MathF.PI / 4) * 20);
            var r = new SKPoint(o.X + MathF.Cos(a + MathF.PI / 4) * 20, o.Y + MathF.Sin(a + MathF.PI / 4) * 20);
            using var lit = new SKPath(); lit.MoveTo(o); lit.LineTo(l); lit.LineTo(tip); lit.Close();
            using var dark = new SKPath(); dark.MoveTo(o); dark.LineTo(r); dark.LineTo(tip); dark.Close();
            using var litPaint = new SKPaint { IsAntialias = true, Color = i == 0 ? Cream : BrassLight };
            using var darkPaint = new SKPaint { IsAntialias = true, Color = i == 0 ? Brass : BrassDeep };
            c.DrawPath(lit, litPaint); c.DrawPath(dark, darkPaint);
        }
        using var hub = new SKPaint { IsAntialias = true, Color = Flame }; c.DrawCircle(o, 8, hub);
    }

    // 5 ------------------------------------------------------------- a seal split by a coastline, one north star
    static void CoastSeal(SKCanvas c)
    {
        var o = new SKPoint(128, 132);
        using var disc = new SKPath(); disc.AddCircle(o.X, o.Y, 92);
        using var land = new SKPath();
        land.MoveTo(20, 150); land.CubicTo(60, 120, 84, 160, 112, 136); land.CubicTo(134, 118, 150, 150, 176, 128); land.CubicTo(196, 112, 214, 118, 236, 104);
        land.LineTo(236, 240); land.LineTo(20, 240); land.Close();
        using var landIn = disc.Op(land, SKPathOp.Intersect)!;
        using var sea = disc.Op(land, SKPathOp.Difference)!;
        using var seaPaint = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#33281D") };
        c.DrawPath(sea, seaPaint);
        c.DrawPath(landIn, BrassPaint(landIn.Bounds));
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, Color = Brass };
        c.DrawCircle(o, 92, ring);
        // the north star over the sea
        using var star = new SKPath();
        var s = new SKPoint(128, 76);
        for (var i = 0; i < 8; i++)
        {
            var a = -MathF.PI / 2 + i * MathF.PI / 4; var len = i % 2 == 0 ? 26f : 8f;
            var p = new SKPoint(s.X + MathF.Cos(a) * len, s.Y + MathF.Sin(a) * len);
            if (i == 0) star.MoveTo(p); else star.LineTo(p);
        }
        star.Close();
        using var starPaint = new SKPaint { IsAntialias = true, Color = Cream }; c.DrawPath(star, starPaint);
    }

    // 6 ------------------------------------------------------------- a map pin with a peak and a river cut through it
    static void PinMountain(SKCanvas c)
    {
        using var pin = new SKPath();
        pin.MoveTo(128, 222); pin.CubicTo(96, 178, 58, 142, 58, 104); pin.CubicTo(58, 64, 90, 34, 128, 34);
        pin.CubicTo(166, 34, 198, 64, 198, 104); pin.CubicTo(198, 142, 160, 178, 128, 222); pin.Close();
        using var peak = Poly((84, 132), (124, 76), (146, 104), (158, 92), (176, 132));
        using var river = new SKPath();
        river.MoveTo(118, 132); river.CubicTo(112, 146, 140, 150, 128, 166); river.LineTo(138, 166); river.CubicTo(150, 150, 124, 144, 130, 132); river.Close();
        using var cut1 = pin.Op(peak, SKPathOp.Difference)!;
        using var cut = cut1.Op(river, SKPathOp.Difference)!;
        c.DrawPath(cut, BrassPaint(pin.Bounds));
        using var snow = Poly((112, 93), (124, 76), (134, 90), (128, 96), (122, 90));
        using var cream = new SKPaint { IsAntialias = true, Color = Cream }; c.DrawPath(snow, cream);
    }
}
