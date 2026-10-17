// Map's own icon art in the tavern palette (dark wood, brass, parchment, ink), drawn with SkiaSharp.
// Writes: three variants of the map icons of the "Создать карту" dialog (the main map + one per
// canvas size) and six variants of the application icon, plus contact sheets for choosing.
//   dotnet run --project tools/IconArt -- <output folder>
using SkiaSharp;

var output = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "icon-art");
Directory.CreateDirectory(output);

var sizes = new[] { "standard", "large", "huge", "max" };
var sets = new (string Key, string Title, Action<SKCanvas, int> Draw)[]
{
    ("a", "Пергамент и латунь", (c, level) => Art.FoldedMap(c, level)),
    ("b", "Свиток", (c, level) => Art.Scroll(c, level)),
    ("c", "Медальон", (c, level) => Art.Medallion(c, level)),
};
foreach (var (key, _, draw) in sets)
{
    Art.Save(Path.Combine(output, $"map-{key}-plain.png"), 512, c => draw(c, 0));
    Art.Save(Path.Combine(output, $"map-{key}-main.png"), 512, c => { draw(c, 0); Art.PlusBadge(c); });
    for (var i = 0; i < sizes.Length; i++)
    {
        var level = i + 1;
        Art.Save(Path.Combine(output, $"map-{key}-{sizes[i]}.png"), 512, c => draw(c, level));
    }
}

var apps = new (string Key, string Title, Action<SKCanvas> Draw)[]
{
    ("1", "Латунная роза ветров", Art.AppCompass),
    ("2", "Сложенная карта", Art.AppFoldedMap),
    ("3", "Перо и пергамент", Art.AppQuill),
    ("4", "Сургучная печать", Art.AppSeal),
    ("5", "Гравюра-медальон", Art.AppEngraving),
    ("6", "Свеча над картой", Art.AppCandle),
};
foreach (var (key, _, draw) in apps)
{
    Art.Save(Path.Combine(output, $"app-{key}.png"), 1024, draw);
    Art.Save(Path.Combine(output, $"app-{key}-64.png"), 64, draw);
    Art.Save(Path.Combine(output, $"app-{key}-32.png"), 32, draw);
}
Console.WriteLine("written to " + output);

static class Art
{
    // Tavern palette.
    public static readonly SKColor Wood = SKColor.Parse("#1A140F");
    public static readonly SKColor WoodLight = SKColor.Parse("#3A2B1E");
    public static readonly SKColor Brass = SKColor.Parse("#C9A15A");
    public static readonly SKColor BrassLight = SKColor.Parse("#F6E2B0");
    public static readonly SKColor BrassDeep = SKColor.Parse("#7A5A2C");
    public static readonly SKColor Parchment = SKColor.Parse("#EEDDB5");
    public static readonly SKColor ParchmentDeep = SKColor.Parse("#C9AE7C");
    public static readonly SKColor Ink = SKColor.Parse("#3B2A1A");
    public static readonly SKColor Water = SKColor.Parse("#6F8F86");
    public static readonly SKColor Wax = SKColor.Parse("#8E2F22");

    public static void Save(string path, int size, Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / 256f);
        draw(canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    static SKPaint Fill(SKColor color) => new() { IsAntialias = true, Color = color, Style = SKPaintStyle.Fill };
    static SKPaint Stroke(SKColor color, float width) => new()
    { IsAntialias = true, Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    static SKShader Vertical(SKRect r, params SKColor[] colors) =>
        SKShader.CreateLinearGradient(new SKPoint(r.MidX, r.Top), new SKPoint(r.MidX, r.Bottom), colors, null, SKShaderTileMode.Clamp);

    static SKShader BrassShader(SKRect r) => SKShader.CreateLinearGradient(
        new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom),
        [BrassLight, Brass, BrassDeep, Brass], [0, .35f, .75f, 1], SKShaderTileMode.Clamp);

    /// <summary>Parchment: warm radial body with a fine fibre noise.</summary>
    static void ParchmentFill(SKCanvas c, SKPath shape, SKRect r)
    {
        using var body = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(
            new SKPoint(r.MidX - r.Width * .15f, r.MidY - r.Height * .2f), Math.Max(r.Width, r.Height) * .8f,
            [SKColor.Parse("#F7EBCB"), Parchment, ParchmentDeep], [0, .55f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(shape, body);
        using var noise = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Multiply,
            Shader = SKShader.CreatePerlinNoiseFractalNoise(.06f, .09f, 3, 7), Color = SKColors.White.WithAlpha(40) };
        c.Save(); c.ClipPath(shape, antialias: true);
        using var tint = new SKPaint { Shader = SKShader.CreateCompose(
            SKShader.CreatePerlinNoiseFractalNoise(.05f, .08f, 3, 11),
            SKShader.CreateColor(SKColor.Parse("#5A3C1E")), SKBlendMode.DstIn) };
        tint.Color = tint.Color.WithAlpha(255);
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha(46) };
        c.SaveLayer(layer);
        c.DrawRect(r, tint);
        c.Restore();
        c.Restore();
    }

    static void Shadow(SKCanvas c, SKPath shape, float dy = 6, float blur = 7, byte alpha = 150)
    {
        using var p = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(alpha),
            ImageFilter = SKImageFilter.CreateBlur(blur, blur) };
        c.Save(); c.Translate(0, dy); c.DrawPath(shape, p); c.Restore();
    }

    // ------------------------------------------------------------------ map content

    /// <summary>The drawing on the sheet; <paramref name="level"/> 0 = plain, 1..4 = canvas sizes (more of the world, finer grid).</summary>
    static void Landscape(SKCanvas c, SKRect r, int level, SKColor ink, float scale = 1)
    {
        using var river = Stroke(Water.WithAlpha(230), 5.5f * scale);
        using var path = new SKPath();
        path.MoveTo(r.Left + r.Width * .12f, r.Top + r.Height * .18f);
        path.CubicTo(r.Left + r.Width * .45f, r.Top + r.Height * .25f, r.Left + r.Width * .2f, r.Top + r.Height * .6f,
            r.Left + r.Width * .5f, r.Top + r.Height * .66f);
        path.CubicTo(r.Left + r.Width * .75f, r.Top + r.Height * .72f, r.Left + r.Width * .7f, r.Top + r.Height * .9f,
            r.Left + r.Width * .9f, r.Top + r.Height * .92f);
        c.DrawPath(path, river);

        void Mountain(float x, float y, float s)
        {
            using var m = new SKPath();
            m.MoveTo(x - s, y); m.LineTo(x, y - s * 1.25f); m.LineTo(x + s, y);
            using var body = Fill(SKColor.Parse("#B7976A"));
            c.DrawPath(m, body);
            using var shade = new SKPath(); shade.MoveTo(x, y - s * 1.25f); shade.LineTo(x + s, y); shade.LineTo(x + s * .2f, y);
            using var dark = Fill(ink.WithAlpha(90)); c.DrawPath(shade, dark);
            using var line = Stroke(ink, 2.4f * scale); c.DrawPath(m, line);
        }
        Mountain(r.Left + r.Width * .66f, r.Top + r.Height * .38f, r.Width * .13f);
        Mountain(r.Left + r.Width * .82f, r.Top + r.Height * .44f, r.Width * .09f);
        if (level != 1) Mountain(r.Left + r.Width * .3f, r.Top + r.Height * .86f, r.Width * .08f);

        if (level >= 2)
        {
            // The finer the grid, the bigger the world on the sheet.
            var cells = level switch { 2 => 2, 3 => 3, _ => 4 };
            using var grid = Stroke(ink.WithAlpha((byte)(level >= 4 ? 120 : 140)), 1.6f * scale);
            grid.PathEffect = SKPathEffect.CreateDash([5 * scale, 4 * scale], 0);
            for (var i = 1; i < cells; i++)
            {
                c.DrawLine(r.Left + r.Width * i / cells, r.Top + 4, r.Left + r.Width * i / cells, r.Bottom - 4, grid);
                c.DrawLine(r.Left + 4, r.Top + r.Height * i / cells, r.Right - 4, r.Top + r.Height * i / cells, grid);
            }
        }
        if (level >= 4) Rose(c, new SKPoint(r.Left + r.Width * .24f, r.Top + r.Height * .3f), r.Width * .14f, ink);
    }

    static void Rose(SKCanvas c, SKPoint o, float s, SKColor ink)
    {
        using var p = new SKPath();
        for (var i = 0; i < 4; i++)
        {
            var a = i * MathF.PI / 2 - MathF.PI / 2;
            var tip = new SKPoint(o.X + MathF.Cos(a) * s, o.Y + MathF.Sin(a) * s);
            var l = new SKPoint(o.X + MathF.Cos(a - MathF.PI / 4) * s * .28f, o.Y + MathF.Sin(a - MathF.PI / 4) * s * .28f);
            var rr = new SKPoint(o.X + MathF.Cos(a + MathF.PI / 4) * s * .28f, o.Y + MathF.Sin(a + MathF.PI / 4) * s * .28f);
            p.MoveTo(o); p.LineTo(l); p.LineTo(tip); p.LineTo(rr); p.Close();
        }
        using var body = Fill(Brass); c.DrawPath(p, body);
        using var line = Stroke(ink, 1.6f); c.DrawPath(p, line);
    }

    // ------------------------------------------------------------------ variant A: folded map

    public static void FoldedMap(SKCanvas c, int level)
    {
        var r = new SKRect(22, 46, 234, 214);
        var w = r.Width / 3;
        using var sheet = new SKPath();
        sheet.MoveTo(r.Left, r.Top + 10); sheet.LineTo(r.Left + w, r.Top); sheet.LineTo(r.Left + 2 * w, r.Top + 10);
        sheet.LineTo(r.Right, r.Top); sheet.LineTo(r.Right, r.Bottom - 10); sheet.LineTo(r.Left + 2 * w, r.Bottom);
        sheet.LineTo(r.Left + w, r.Bottom - 10); sheet.LineTo(r.Left, r.Bottom); sheet.Close();
        Shadow(c, sheet);
        ParchmentFill(c, sheet, r);
        c.Save(); c.ClipPath(sheet, antialias: true);
        Landscape(c, new SKRect(r.Left + 8, r.Top + 14, r.Right - 8, r.Bottom - 14), level, Ink);
        // folds: the middle panel is lit, the outer two are in shade
        using var fold = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#5A3C1E").WithAlpha(46) };
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + w, r.Bottom), fold);
        c.DrawRect(new SKRect(r.Left + 2 * w, r.Top, r.Right, r.Bottom), fold);
        c.Restore();
        using var crease = Stroke(SKColor.Parse("#8A6A3A").WithAlpha(160), 1.6f);
        c.DrawLine(r.Left + w, r.Top, r.Left + w, r.Bottom - 10, crease);
        c.DrawLine(r.Left + 2 * w, r.Top + 10, r.Left + 2 * w, r.Bottom, crease);
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, StrokeJoin = SKStrokeJoin.Round, Shader = BrassShader(r) };
        c.DrawPath(sheet, rim);
        using var inner = Stroke(Wood.WithAlpha(170), 1.4f);
        c.DrawPath(sheet, inner);
    }

    // ------------------------------------------------------------------ variant B: scroll

    public static void Scroll(SKCanvas c, int level)
    {
        var r = new SKRect(44, 44, 212, 212);
        using var sheet = new SKPath();
        sheet.MoveTo(r.Left, r.Top); sheet.CubicTo(r.Left + 60, r.Top + 10, r.Right - 60, r.Top - 8, r.Right, r.Top);
        sheet.LineTo(r.Right, r.Bottom); sheet.CubicTo(r.Right - 60, r.Bottom - 10, r.Left + 60, r.Bottom + 8, r.Left, r.Bottom); sheet.Close();
        Shadow(c, sheet);
        ParchmentFill(c, sheet, r);
        c.Save(); c.ClipPath(sheet, antialias: true);
        Landscape(c, new SKRect(r.Left + 6, r.Top + 14, r.Right - 6, r.Bottom - 14), level, Ink);
        c.Restore();
        using var edge = Stroke(SKColor.Parse("#8A6A3A"), 2.2f); c.DrawPath(sheet, edge);
        foreach (var y in new[] { r.Top, r.Bottom })
        {
            var rod = new SKRect(r.Left - 22, y - 10, r.Right + 22, y + 10);
            using var rodPath = new SKPath(); rodPath.AddRoundRect(rod, 10, 10);
            Shadow(c, rodPath, 3, 4, 120);
            using var brass = new SKPaint { IsAntialias = true, Shader = Vertical(rod, BrassLight, Brass, BrassDeep) };
            c.DrawRoundRect(rod, 10, 10, brass);
            using var line = Stroke(Wood.WithAlpha(200), 1.6f); c.DrawRoundRect(rod, 10, 10, line);
            foreach (var x in new[] { rod.Left + 6, rod.Right - 6 })
            {
                using var knob = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(x - 2, y - 3), 10, [BrassLight, BrassDeep], SKShaderTileMode.Clamp) };
                c.DrawCircle(x, y, 9, knob); c.DrawCircle(x, y, 9, line);
            }
        }
    }

    // ------------------------------------------------------------------ variant C: medallion

    public static void Medallion(SKCanvas c, int level)
    {
        var o = new SKPoint(128, 128);
        using var disc = new SKPath(); disc.AddCircle(o.X, o.Y, 104);
        Shadow(c, disc);
        var rect = new SKRect(24, 24, 232, 232);
        using var rim = new SKPaint { IsAntialias = true, Shader = BrassShader(rect) };
        c.DrawCircle(o, 104, rim);
        using var rimLine = Stroke(Wood.WithAlpha(200), 2); c.DrawCircle(o, 104, rimLine);
        using var face = new SKPath(); face.AddCircle(o.X, o.Y, 86);
        ParchmentFill(c, face, new SKRect(42, 42, 214, 214));
        c.Save(); c.ClipPath(face, antialias: true);
        Landscape(c, new SKRect(52, 52, 204, 204), level, Ink);
        c.Restore();
        using var faceLine = Stroke(BrassDeep, 3); c.DrawCircle(o, 86, faceLine);
        using var ticks = Stroke(Wood.WithAlpha(160), 2);
        for (var i = 0; i < 24; i++)
        {
            var a = i * MathF.PI * 2 / 24;
            var r1 = i % 6 == 0 ? 90f : 94f;
            c.DrawLine(o.X + MathF.Cos(a) * r1, o.Y + MathF.Sin(a) * r1, o.X + MathF.Cos(a) * 100, o.Y + MathF.Sin(a) * 100, ticks);
        }
    }

    /// <summary>The "new" mark of the main map icon: a brass disc with a plus, lower right.</summary>
    public static void PlusBadge(SKCanvas c)
    {
        var o = new SKPoint(200, 196);
        using var p = new SKPath(); p.AddCircle(o.X, o.Y, 34);
        Shadow(c, p, 3, 4, 140);
        using var body = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(o.X - 10, o.Y - 12), 46,
            [BrassLight, Brass, BrassDeep], [0, .5f, 1], SKShaderTileMode.Clamp) };
        c.DrawCircle(o, 34, body);
        using var ring = Stroke(Wood, 3); c.DrawCircle(o, 34, ring);
        using var plus = Stroke(Wood, 9);
        c.DrawLine(o.X - 15, o.Y, o.X + 15, o.Y, plus); c.DrawLine(o.X, o.Y - 15, o.X, o.Y + 15, plus);
    }

    // ------------------------------------------------------------------ application icons

    /// <summary>The common tile: a dark wood squircle with a double brass frame.</summary>
    static void Tile(SKCanvas c)
    {
        var r = new SKRect(12, 12, 244, 244);
        using var shape = new SKPath(); shape.AddRoundRect(r, 52, 52);
        Shadow(c, shape, 5, 6, 120);
        using var wood = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(110, 90), 220,
            [WoodLight, Wood], SKShaderTileMode.Clamp) };
        c.DrawPath(shape, wood);
        using var grain = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Overlay,
            Shader = SKShader.CreatePerlinNoiseFractalNoise(.004f, .08f, 2, 3), Color = SKColors.White.WithAlpha(60) };
        c.Save(); c.ClipPath(shape, antialias: true); c.DrawRect(r, grain); c.Restore();
        using var outer = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Shader = BrassShader(r) };
        c.DrawRoundRect(new SKRect(16, 16, 240, 240), 48, 48, outer);
        using var innerLine = Stroke(Brass.WithAlpha(110), 1.6f);
        c.DrawRoundRect(new SKRect(25, 25, 231, 231), 40, 40, innerLine);
    }

    /// <summary>A soft light kept inside the tile's frame.</summary>
    static void Glow(SKCanvas c, SKPoint o, float radius, SKColor color, byte alpha)
    {
        using var p = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(o, radius,
            [color.WithAlpha(alpha), color.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.Save();
        c.ClipRoundRect(new SKRoundRect(new SKRect(19, 19, 237, 237), 45, 45), antialias: true);
        c.DrawCircle(o, radius, p);
        c.Restore();
    }

    public static void AppCompass(SKCanvas c)
    {
        Tile(c);
        var o = new SKPoint(128, 128);
        Glow(c, o, 110, BrassLight, 50);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, Shader = BrassShader(new SKRect(40, 40, 216, 216)) };
        c.DrawCircle(o, 82, ring);
        using var tick = Stroke(BrassLight.WithAlpha(200), 2.4f);
        for (var i = 0; i < 32; i++)
        {
            var a = i * MathF.PI * 2 / 32;
            var r1 = i % 4 == 0 ? 64f : 70f;
            c.DrawLine(o.X + MathF.Cos(a) * r1, o.Y + MathF.Sin(a) * r1, o.X + MathF.Cos(a) * 75, o.Y + MathF.Sin(a) * 75, tick);
        }
        // eight-point rose: long cardinal points, short diagonals, lit and shaded halves
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4 - MathF.PI / 2;
            var len = i % 2 == 0 ? 104f : 58f;
            var half = i % 2 == 0 ? 15f : 11f;
            var tip = new SKPoint(o.X + MathF.Cos(a) * len, o.Y + MathF.Sin(a) * len);
            var l = new SKPoint(o.X + MathF.Cos(a - MathF.PI / 2) * half, o.Y + MathF.Sin(a - MathF.PI / 2) * half);
            var r = new SKPoint(o.X + MathF.Cos(a + MathF.PI / 2) * half, o.Y + MathF.Sin(a + MathF.PI / 2) * half);
            using var lit = new SKPath(); lit.MoveTo(o); lit.LineTo(l); lit.LineTo(tip); lit.Close();
            using var dark = new SKPath(); dark.MoveTo(o); dark.LineTo(r); dark.LineTo(tip); dark.Close();
            using var litPaint = Fill(i == 0 ? SKColor.Parse("#F3D48A") : BrassLight);
            using var darkPaint = Fill(i == 0 ? SKColor.Parse("#A8432E") : BrassDeep);
            c.DrawPath(lit, litPaint); c.DrawPath(dark, darkPaint);
            using var edge = Stroke(Wood, 1.6f); c.DrawPath(lit, edge); c.DrawPath(dark, edge);
        }
        using var hub = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(124, 124), 14, [BrassLight, BrassDeep], SKShaderTileMode.Clamp) };
        c.DrawCircle(o, 11, hub);
        using var hubLine = Stroke(Wood, 2); c.DrawCircle(o, 11, hubLine);
    }

    public static void AppFoldedMap(SKCanvas c)
    {
        Tile(c);
        Glow(c, new SKPoint(128, 120), 120, BrassLight, 36);
        c.Save(); c.Translate(128, 132); c.Scale(.8f); c.Translate(-128, -128);
        FoldedMap(c, 0);
        c.Restore();
        c.Save(); c.Translate(186, 70); c.Scale(.5f); c.Translate(-128, -128);
        Rose(c, new SKPoint(128, 128), 70, Wood);
        c.Restore();
    }

    public static void AppQuill(SKCanvas c)
    {
        Tile(c);
        var r = new SKRect(52, 58, 204, 206);
        using var sheet = new SKPath();
        sheet.MoveTo(r.Left, r.Top + 6); sheet.LineTo(r.Right - 10, r.Top); sheet.LineTo(r.Right, r.Bottom - 6); sheet.LineTo(r.Left + 8, r.Bottom); sheet.Close();
        Shadow(c, sheet);
        ParchmentFill(c, sheet, r);
        c.Save(); c.ClipPath(sheet, antialias: true);
        using var coast = Stroke(Ink, 3);
        using var path = new SKPath();
        path.MoveTo(60, 160); path.CubicTo(80, 120, 110, 150, 120, 118); path.CubicTo(130, 90, 165, 108, 170, 84);
        c.DrawPath(path, coast);
        using var hatch = Stroke(Ink.WithAlpha(110), 1.6f);
        for (var i = 0; i < 6; i++) c.DrawLine(70 + i * 9, 186, 82 + i * 9, 172, hatch);
        c.Restore();
        using var edge = Stroke(SKColor.Parse("#8A6A3A"), 2); c.DrawPath(sheet, edge);
        // the quill: an ivory vane with a brass nib, laid across the sheet
        c.Save(); c.Translate(150, 120); c.RotateDegrees(-38);
        using var vane = new SKPath();
        vane.MoveTo(0, -98); vane.CubicTo(30, -60, 26, 20, 4, 70); vane.LineTo(-4, 70); vane.CubicTo(-22, 20, -26, -60, 0, -98); vane.Close();
        Shadow(c, vane, 4, 5, 120);
        using var feather = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(-24, 0), new SKPoint(24, 0),
            [SKColor.Parse("#F8F0DC"), SKColor.Parse("#D8C49C")], SKShaderTileMode.Clamp) };
        c.DrawPath(vane, feather);
        using var shaft = Stroke(BrassDeep, 2.6f); c.DrawLine(0, -92, 0, 92, shaft);
        using var barbs = Stroke(SKColor.Parse("#B39C72"), 1.2f);
        for (var y = -80; y < 60; y += 9) { c.DrawLine(0, y, 16, y - 10, barbs); c.DrawLine(0, y, -16, y - 10, barbs); }
        using var nibPath = new SKPath(); nibPath.MoveTo(-5, 70); nibPath.LineTo(5, 70); nibPath.LineTo(0, 100); nibPath.Close();
        using var nib = new SKPaint { IsAntialias = true, Shader = Vertical(new SKRect(-5, 70, 5, 100), BrassLight, BrassDeep) };
        c.DrawPath(nibPath, nib);
        using var outline = Stroke(Wood, 1.6f); c.DrawPath(vane, outline); c.DrawPath(nibPath, outline);
        c.Restore();
    }

    public static void AppSeal(SKCanvas c)
    {
        Tile(c);
        var r = new SKRect(50, 50, 206, 206);
        using var sheet = new SKPath(); sheet.AddRoundRect(r, 6, 6);
        Shadow(c, sheet);
        ParchmentFill(c, sheet, r);
        c.Save(); c.ClipPath(sheet, antialias: true);
        Landscape(c, new SKRect(r.Left + 6, r.Top + 6, r.Right - 6, r.Bottom - 6), 2, Ink.WithAlpha(150));
        c.Restore();
        using var edge = Stroke(SKColor.Parse("#8A6A3A"), 2); c.DrawPath(sheet, edge);
        // ribbon under the seal
        using var ribbon = new SKPath(); ribbon.MoveTo(150, 170); ribbon.LineTo(138, 232); ribbon.LineTo(152, 220); ribbon.LineTo(164, 234); ribbon.LineTo(170, 172); ribbon.Close();
        using var ribbonPaint = Fill(SKColor.Parse("#6E2318")); c.DrawPath(ribbon, ribbonPaint);
        var o = new SKPoint(160, 160);
        using var blob = new SKPath();
        for (var i = 0; i <= 24; i++)
        {
            var a = i * MathF.PI * 2 / 24;
            var rr = 40 + (i % 2 == 0 ? 3 : -2);
            var p = new SKPoint(o.X + MathF.Cos(a) * rr, o.Y + MathF.Sin(a) * rr);
            if (i == 0) blob.MoveTo(p); else blob.LineTo(p);
        }
        blob.Close();
        Shadow(c, blob, 4, 5, 150);
        using var wax = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(150, 148), 52,
            [SKColor.Parse("#C0503A"), Wax, SKColor.Parse("#4E1810")], [0, .55f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(blob, wax);
        using var press = Stroke(SKColor.Parse("#5E1E14"), 2.4f); c.DrawCircle(o, 29, press);
        using var letter = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#E8B08E").WithAlpha(220) };
        using var font = new SKFont(SKTypeface.FromFamilyName("Georgia", SKFontStyle.Bold), 38);
        c.DrawText("M", o.X, o.Y + 13, SKTextAlign.Center, font, letter);
    }

    public static void AppEngraving(SKCanvas c)
    {
        Tile(c);
        var o = new SKPoint(128, 128);
        var rect = new SKRect(34, 34, 222, 222);
        using var gold = new SKPaint { IsAntialias = true, Shader = BrassShader(rect) };
        c.DrawCircle(o, 92, gold);
        using var edge = Stroke(Wood, 2.4f); c.DrawCircle(o, 92, edge); c.DrawCircle(o, 78, edge);
        // engraved landscape in dark lines on the gold face
        c.Save(); using var clip = new SKPath(); clip.AddCircle(o.X, o.Y, 77); c.ClipPath(clip, antialias: true);
        using var line = Stroke(Wood, 3.2f);
        using var hatch = Stroke(Wood.WithAlpha(150), 1.6f);
        void Peak(float x, float y, float s)
        {
            using var m = new SKPath(); m.MoveTo(x - s, y); m.LineTo(x, y - s * 1.2f); m.LineTo(x + s, y);
            c.DrawPath(m, line);
            for (var i = 1; i < 5; i++) c.DrawLine(x + s * i / 6f, y - s * 1.2f * (1 - i / 6f), x + s * i / 6f + s * .25f, y, hatch);
        }
        Peak(100, 132, 40); Peak(150, 124, 32); Peak(180, 138, 22);
        using var river = Stroke(Wood, 3);
        using var path = new SKPath(); path.MoveTo(64, 172); path.CubicTo(100, 150, 120, 190, 150, 168); path.CubicTo(170, 154, 190, 176, 210, 160);
        c.DrawPath(path, river);
        for (var y = 182; y < 210; y += 7) c.DrawLine(50, y, 210, y - 4, hatch);
        c.Restore();
        using var star = Fill(BrassLight); c.DrawCircle(o.X, 58, 0, star);
        Rose(c, new SKPoint(o.X, 66), 13, Wood);
    }

    public static void AppCandle(SKCanvas c)
    {
        Tile(c);
        Glow(c, new SKPoint(150, 92), 120, SKColor.Parse("#FFC86A"), 90);
        var r = new SKRect(40, 120, 216, 214);
        using var sheet = new SKPath();
        sheet.MoveTo(r.Left, r.Top + 8); sheet.LineTo(r.Right, r.Top); sheet.LineTo(r.Right - 6, r.Bottom); sheet.LineTo(r.Left + 6, r.Bottom - 4); sheet.Close();
        Shadow(c, sheet);
        ParchmentFill(c, sheet, r);
        c.Save(); c.ClipPath(sheet, antialias: true);
        Landscape(c, new SKRect(r.Left + 4, r.Top + 4, r.Right - 4, r.Bottom - 4), 0, Ink);
        c.Restore();
        using var edge = Stroke(SKColor.Parse("#8A6A3A"), 2); c.DrawPath(sheet, edge);
        // candle in a brass holder
        var body = new SKRect(138, 60, 162, 132);
        using var holderPath = new SKPath(); holderPath.AddOval(new SKRect(116, 124, 184, 146));
        Shadow(c, holderPath, 3, 4, 140);
        using var holder = new SKPaint { IsAntialias = true, Shader = Vertical(new SKRect(116, 124, 184, 146), BrassLight, BrassDeep) };
        c.DrawPath(holderPath, holder);
        using var wax = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(body.Left, 0), new SKPoint(body.Right, 0),
            [SKColor.Parse("#FFF4DC"), SKColor.Parse("#E2CFA6")], SKShaderTileMode.Clamp) };
        c.DrawRoundRect(body, 4, 4, wax);
        using var outline = Stroke(Wood, 1.8f); c.DrawRoundRect(body, 4, 4, outline); c.DrawPath(holderPath, outline);
        using var wick = Stroke(Wood, 2); c.DrawLine(150, 60, 150, 52, wick);
        Glow(c, new SKPoint(150, 40), 26, SKColor.Parse("#FFE2A0"), 200);
        using var flame = new SKPath(); flame.MoveTo(150, 20); flame.CubicTo(162, 34, 160, 50, 150, 54); flame.CubicTo(140, 50, 138, 34, 150, 20); flame.Close();
        using var flamePaint = new SKPaint { IsAntialias = true, Shader = Vertical(new SKRect(140, 20, 160, 54), SKColor.Parse("#FFF6D8"), SKColor.Parse("#FFB347")) };
        c.DrawPath(flame, flamePaint);
    }
}
