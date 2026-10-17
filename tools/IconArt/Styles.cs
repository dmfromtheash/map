using SkiaSharp;

/// <summary>
/// Six style directions for choosing, all inside the program's look (dark wood, brass, parchment,
/// candle light). Each writes an application icon and four sample icons - new map, land, text
/// (quill), assets (keep) - so the whole family can be judged, not one picture.
/// </summary>
static class Styles
{
    static readonly SKColor Wood = SKColor.Parse("#17110D");
    static readonly SKColor WoodWarm = SKColor.Parse("#3A2B1E");
    static readonly SKColor BrassLight = SKColor.Parse("#F6E2B0");
    static readonly SKColor Brass = SKColor.Parse("#C9A15A");
    static readonly SKColor BrassDeep = SKColor.Parse("#7A5A2C");
    static readonly SKColor Ink = SKColor.Parse("#3B2A1A");
    static readonly SKColor ParchmentLight = SKColor.Parse("#F7EBCB");
    static readonly SKColor Parchment = SKColor.Parse("#E8D6AC");
    static readonly SKColor ParchmentDeep = SKColor.Parse("#C9AE7C");

    /// <summary>How one direction draws a body (a filled emblem) and its detail lines, and its application board.</summary>
    sealed record Style(string Key, Action<SKCanvas, SKPath, SKPath?> Body, Action<SKCanvas> Board, SKRect AppBox);

    public static void Write(string output)
    {
        var styles = new[]
        {
            new Style("1", CastBrass, WoodBoard, new SKRect(54, 64, 202, 192)),
            new Style("2", InkEngraving, ParchmentRoundel, new SKRect(62, 74, 194, 184)),
            new Style("3", GlowingOutline, CandleMedallion, new SKRect(64, 76, 192, 182)),
            new Style("4", Enamel, EnamelBoard, new SKRect(58, 70, 198, 188)),
            new Style("5", Wax, SealOnParchment, new SKRect(78, 86, 178, 170)),
            new Style("6", Leather, LeatherBoard, new SKRect(56, 66, 200, 190)),
        };
        foreach (var style in styles)
        {
            void App(SKCanvas c) { style.Board(c); var (body, detail) = MapShape(style.AppBox); style.Body(c, body, detail); }
            foreach (var size in new[] { 256, 64, 32, 16 })
                Art.Save(Path.Combine(output, $"ex-{style.Key}-app-{size}.png"), size, App);
            Art.Save(Path.Combine(output, $"ex-{style.Key}-map.png"), 256, c =>
            {
                var (body, detail) = MapShape(new SKRect(26, 54, 230, 202));
                style.Body(c, body, detail);
                using var seal = new SKPath(); seal.AddCircle(200, 194, 34);
                using var plus = new SKPath(); plus.MoveTo(184, 194); plus.LineTo(216, 194); plus.MoveTo(200, 178); plus.LineTo(200, 210);
                style.Body(c, seal, plus);
            });
            Art.Save(Path.Combine(output, $"ex-{style.Key}-land.png"), 256, c => Glyph(c, style, Glyphs.Land));
            Art.Save(Path.Combine(output, $"ex-{style.Key}-text.png"), 256, c => Glyph(c, style, Glyphs.Quill));
            Art.Save(Path.Combine(output, $"ex-{style.Key}-assets.png"), 256, c => Glyph(c, style, Glyphs.Keep));
        }
    }

    static void Glyph(SKCanvas c, Style style, Glyphs.Glyph glyph) => style.Body(c, GlyphPath(glyph, new SKRect(36, 36, 220, 220)), null);

    /// <summary>A rail glyph as one filled path in <paramref name="box"/> (its line parts thickened into the fill).</summary>
    static SKPath GlyphPath(Glyphs.Glyph glyph, SKRect box)
    {
        var fill = SKPath.ParseSvgPathData(glyph.Fill)!;
        fill.FillType = SKPathFillType.EvenOdd;
        using var stroke = glyph.Stroke is { } s ? SKPath.ParseSvgPathData(s) : null;
        var bounds = fill.Bounds;
        if (stroke is not null) { var sb = stroke.Bounds; sb.Inflate(glyph.StrokeWidth / 2, glyph.StrokeWidth / 2); bounds.Union(sb); }
        var scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
        var matrix = SKMatrix.CreateTranslation(-bounds.MidX, -bounds.MidY).PostConcat(SKMatrix.CreateScale(scale, scale))
            .PostConcat(SKMatrix.CreateTranslation(box.MidX, box.MidY));
        fill.Transform(matrix);
        if (stroke is null) return fill;
        stroke.Transform(matrix);
        using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = glyph.StrokeWidth * scale, StrokeCap = SKStrokeCap.Round };
        using var band = pen.GetFillPath(stroke);
        var union = band.Op(fill, SKPathOp.Union) ?? fill;
        return union;
    }

    /// <summary>A folded map: the sheet as the body; creases, a river and peaks as detail lines.</summary>
    static (SKPath Body, SKPath Detail) MapShape(SKRect r)
    {
        var w = r.Width / 3; var d = r.Height * .08f;
        var sheet = new SKPath();
        sheet.MoveTo(r.Left, r.Top + d); sheet.LineTo(r.Left + w, r.Top); sheet.LineTo(r.Left + 2 * w, r.Top + d);
        sheet.LineTo(r.Right, r.Top); sheet.LineTo(r.Right, r.Bottom - d); sheet.LineTo(r.Left + 2 * w, r.Bottom);
        sheet.LineTo(r.Left + w, r.Bottom - d); sheet.LineTo(r.Left, r.Bottom); sheet.Close();
        var detail = new SKPath();
        detail.MoveTo(r.Left + w, r.Top + 3); detail.LineTo(r.Left + w, r.Bottom - d - 3);
        detail.MoveTo(r.Left + 2 * w, r.Top + d + 3); detail.LineTo(r.Left + 2 * w, r.Bottom - 3);
        float X(float f) => r.Left + r.Width * f; float Y(float f) => r.Top + r.Height * f;
        detail.MoveTo(X(.07f), Y(.2f)); detail.CubicTo(X(.4f), Y(.24f), X(.16f), Y(.64f), X(.48f), Y(.7f));
        detail.CubicTo(X(.72f), Y(.75f), X(.72f), Y(.9f), X(.93f), Y(.9f));
        void Peak(float x, float y, float s) { detail.MoveTo(X(x) - s, Y(y)); detail.LineTo(X(x), Y(y) - s * 1.15f); detail.LineTo(X(x) + s, Y(y)); }
        Peak(.66f, .42f, r.Width * .1f); Peak(.84f, .46f, r.Width * .07f); Peak(.24f, .86f, r.Width * .06f);
        return (sheet, detail);
    }

    static SKPaint Pen(SKColor color, float width) => new()
    { IsAntialias = true, Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    static void Shadow(SKCanvas c, SKPath p, float dy = 4, float blur = 5, byte alpha = 140)
    {
        using var s = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(alpha), ImageFilter = SKImageFilter.CreateBlur(blur, blur) };
        c.Save(); c.Translate(0, dy); c.DrawPath(p, s); c.Restore();
    }

    static float Unit(SKPath p) => Math.Max(1, Math.Max(p.Bounds.Width, p.Bounds.Height) / 180f);

    // ------------------------------------------------------------------ 1: cast brass

    static void CastBrass(SKCanvas c, SKPath body, SKPath? detail)
    {
        Art.CastPath(c, body, 2.2f * Unit(body));
        if (detail is not null) { using var line = Pen(SKColor.Parse("#2A1D12").WithAlpha(210), 3 * Unit(body)); c.DrawPath(detail, line); }
    }

    static void WoodBoard(SKCanvas c) => Art.Board(c);

    // ------------------------------------------------------------------ 2: ink engraving on parchment

    static void InkEngraving(SKCanvas c, SKPath body, SKPath? detail)
    {
        var u = Unit(body);
        using var paper = new SKPaint { IsAntialias = true, Color = ParchmentLight };
        c.DrawPath(body, paper);
        c.Save(); c.ClipPath(body, antialias: true);
        // cross-hatching on the shaded lower right, as an etching
        using var hatch = Pen(Ink.WithAlpha(120), 1.3f * u);
        var b = body.Bounds;
        for (var x = b.Left - b.Height; x < b.Right; x += 7 * u) c.DrawLine(x + b.Height * .55f, b.Bottom, x + b.Height * 1.1f, b.Top + b.Height * .45f, hatch);
        c.Restore();
        using var edge = Pen(Ink, 3.4f * u); c.DrawPath(body, edge);
        if (detail is not null) { using var line = Pen(Ink, 2.6f * u); c.DrawPath(detail, line); }
    }

    static void ParchmentRoundel(SKCanvas c)
    {
        var o = new SKPoint(128, 128);
        using var disc = new SKPath(); disc.AddCircle(o.X, o.Y, 116);
        Shadow(c, disc, 4, 6, 150);
        using var paper = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(110, 100), 150,
            [ParchmentLight, Parchment, ParchmentDeep], [0, .6f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(disc, paper);
        using var outer = Pen(Ink, 5); c.DrawCircle(o, 112, outer);
        using var inner = Pen(Ink, 1.6f); c.DrawCircle(o, 104, inner);
        using var dots = new SKPaint { IsAntialias = true, Color = Ink };
        for (var i = 0; i < 36; i++) { var a = i * MathF.PI * 2 / 36; c.DrawCircle(o.X + MathF.Cos(a) * 108, o.Y + MathF.Sin(a) * 108, 1.4f, dots); }
    }

    // ------------------------------------------------------------------ 3: glowing brass outline (candle)

    static void GlowingOutline(SKCanvas c, SKPath body, SKPath? detail)
    {
        var u = Unit(body);
        using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7 * u, Color = SKColor.Parse("#FFB347").WithAlpha(110),
            ImageFilter = SKImageFilter.CreateBlur(6 * u, 6 * u), StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(body, glow);
        using var tint = new SKPaint { IsAntialias = true, Color = Brass.WithAlpha(40) };
        c.DrawPath(body, tint);
        using var line = Pen(BrassLight, 3.2f * u); c.DrawPath(body, line);
        if (detail is not null) { using var thin = Pen(Brass, 2.2f * u); c.DrawPath(detail, thin); }
    }

    static void CandleMedallion(SKCanvas c)
    {
        var o = new SKPoint(128, 128);
        using var disc = new SKPath(); disc.AddCircle(o.X, o.Y, 116);
        Shadow(c, disc, 4, 6, 160);
        using var wood = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(128, 200), 170,
            [SKColor.Parse("#5A3A1C"), SKColor.Parse("#2A1E15"), Wood], [0, .5f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(disc, wood);
        using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, Color = SKColor.Parse("#FFB347").WithAlpha(90),
            ImageFilter = SKImageFilter.CreateBlur(7, 7) };
        c.DrawCircle(o, 110, halo);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 12), new SKPoint(0, 244), [BrassLight, Brass, BrassDeep], SKShaderTileMode.Clamp) };
        c.DrawCircle(o, 110, ring);
        using var hair = Pen(Brass.WithAlpha(110), 1.4f); c.DrawCircle(o, 101, hair);
    }

    // ------------------------------------------------------------------ 4: enamel in brass

    static readonly SKColor EnamelLight = SKColor.Parse("#3F7F78");
    static readonly SKColor EnamelDeep = SKColor.Parse("#173A37");

    static void Enamel(SKCanvas c, SKPath body, SKPath? detail)
    {
        var u = Unit(body);
        var b = body.Bounds;
        Shadow(c, body, 3 * u, 4 * u, 130);
        using var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(b.Left, b.Top), new SKPoint(b.Right, b.Bottom),
            [EnamelLight, SKColor.Parse("#24524D"), EnamelDeep], [0, .5f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(body, fill);
        // the glassy gloss on the upper half
        c.Save(); c.ClipPath(body, antialias: true);
        using var gloss = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.MidY),
            [SKColors.White.WithAlpha(70), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawRect(b, gloss);
        c.Restore();
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4.2f * u, StrokeJoin = SKStrokeJoin.Round,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.Bottom), [BrassLight, Brass, BrassDeep], SKShaderTileMode.Clamp) };
        c.DrawPath(body, rim);
        if (detail is not null)
        {
            using var wire = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f * u, StrokeCap = SKStrokeCap.Round,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.Bottom), [BrassLight, Brass], SKShaderTileMode.Clamp) };
            c.DrawPath(detail, wire);
        }
    }

    static void EnamelBoard(SKCanvas c)
    {
        var r = new SKRect(12, 12, 244, 244);
        using var shape = new SKPath(); shape.AddRoundRect(r, 40, 40);
        Shadow(c, shape, 4, 6, 150);
        using var brass = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 12), new SKPoint(0, 244),
            [BrassLight, Brass, BrassDeep], SKShaderTileMode.Clamp) };
        c.DrawPath(shape, brass);
        var field = new SKRect(26, 26, 230, 230);
        using var enamel = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(110, 90), 200,
            [SKColor.Parse("#2A1F18"), Wood], SKShaderTileMode.Clamp) };
        c.DrawRoundRect(field, 28, 28, enamel);
        using var edge = Pen(SKColor.Parse("#2A1D12"), 2); c.DrawRoundRect(field, 28, 28, edge);
    }

    // ------------------------------------------------------------------ 5: sealing wax

    static void Wax(SKCanvas c, SKPath body, SKPath? detail)
    {
        var u = Unit(body);
        var b = body.Bounds;
        Shadow(c, body, 3 * u, 4 * u, 150);
        using var wax = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(b.Left + b.Width * .35f, b.Top + b.Height * .3f),
            Math.Max(b.Width, b.Height) * .9f, [SKColor.Parse("#C4523C"), SKColor.Parse("#8E2F22"), SKColor.Parse("#4E1810")], [0, .55f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(body, wax);
        using var rim = Pen(SKColor.Parse("#3E120B"), 2.4f * u); c.DrawPath(body, rim);
        c.Save(); c.ClipPath(body, antialias: true);
        using var shine = new SKPath(body); shine.Transform(SKMatrix.CreateTranslation(0, 3 * u));
        using var light = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F2A487").WithAlpha(110) };
        using var lit = body.Op(shine, SKPathOp.Difference);
        if (lit is not null) c.DrawPath(lit, light);
        c.Restore();
        if (detail is not null)
        {
            using var press = Pen(SKColor.Parse("#4E1810"), 3 * u); c.Save(); c.Translate(0, .8f * u); c.DrawPath(detail, press); c.Restore();
            using var raised = Pen(SKColor.Parse("#E07A5E").WithAlpha(170), 1.4f * u); c.DrawPath(detail, raised);
        }
    }

    static void SealOnParchment(SKCanvas c)
    {
        var sheet = new SKRect(18, 22, 238, 234);
        using var paper = new SKPath(); paper.AddRoundRect(sheet, 10, 10);
        Shadow(c, paper, 4, 6, 150);
        using var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(100, 90), 220,
            [ParchmentLight, Parchment, ParchmentDeep], [0, .6f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(paper, fill);
        using var edge = Pen(BrassDeep, 2); c.DrawPath(paper, edge);
        // the wax seal the emblem is pressed into
        var o = new SKPoint(128, 128);
        using var blob = new SKPath();
        for (var i = 0; i <= 40; i++)
        {
            var a = i * MathF.PI * 2 / 40; var rr = 86 + (i % 2 == 0 ? 4 : -3) + MathF.Sin(i * 1.7f) * 2;
            var p = new SKPoint(o.X + MathF.Cos(a) * rr, o.Y + MathF.Sin(a) * rr);
            if (i == 0) blob.MoveTo(p); else blob.LineTo(p);
        }
        blob.Close();
        Wax(c, blob, null);
        using var ring = Pen(SKColor.Parse("#5E1E14"), 2.4f); c.DrawCircle(o, 68, ring);
    }

    // ------------------------------------------------------------------ 6: tooled leather

    static readonly SKColor Leather1 = SKColor.Parse("#6B4529");
    static readonly SKColor Leather2 = SKColor.Parse("#3E2717");

    static void Leather(SKCanvas c, SKPath body, SKPath? detail)
    {
        var u = Unit(body);
        var b = body.Bounds;
        // debossed: a dark pressed body with an inner shadow along its top
        using var pressed = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, b.Top), new SKPoint(0, b.Bottom),
            [SKColor.Parse("#2B1A0E"), SKColor.Parse("#4A2F1B")], SKShaderTileMode.Clamp) };
        c.DrawPath(body, pressed);
        c.Save(); c.ClipPath(body, antialias: true);
        using var inner = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8 * u, Color = SKColors.Black.WithAlpha(150),
            ImageFilter = SKImageFilter.CreateBlur(3 * u, 3 * u) };
        c.Save(); c.Translate(0, 3 * u); c.DrawPath(body, inner); c.Restore();
        c.Restore();
        using var lip = Pen(SKColor.Parse("#A4774C").WithAlpha(180), 1.6f * u);
        c.Save(); c.Translate(0, 1.5f * u); c.DrawPath(body, lip); c.Restore();
        // brass stitching just outside the edge
        using var stitch = Pen(Brass, 2.2f * u);
        stitch.PathEffect = SKPathEffect.CreateDash([5 * u, 4 * u], 0);
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9 * u };
        using var ring = outline.GetFillPath(body);
        using var outer = ring.Op(body, SKPathOp.Difference);
        if (outer is not null) c.DrawPath(outer, stitch);
        if (detail is not null) { using var line = Pen(SKColor.Parse("#C49A62"), 2.2f * u); c.DrawPath(detail, line); }
    }

    static void LeatherBoard(SKCanvas c)
    {
        var r = new SKRect(12, 12, 244, 244);
        using var shape = new SKPath(); shape.AddRoundRect(r, 30, 30);
        Shadow(c, shape, 4, 6, 150);
        using var hide = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(110, 90), 220,
            [Leather1, Leather2], SKShaderTileMode.Clamp) };
        c.DrawPath(shape, hide);
        c.Save(); c.ClipPath(shape, antialias: true);
        using var grain = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(70),
            Shader = SKShader.CreatePerlinNoiseFractalNoise(.06f, .06f, 3, 9) };
        c.DrawRect(r, grain);
        c.Restore();
        using var stitch = Pen(Brass, 2.4f); stitch.PathEffect = SKPathEffect.CreateDash([7, 5], 0);
        c.DrawRoundRect(new SKRect(24, 24, 232, 232), 20, 20, stitch);
        foreach (var (x, y) in new[] { (20f, 20f), (236f, 20f), (20f, 236f), (236f, 236f) })
        {
            using var cap = new SKPath(); cap.AddCircle(x, y, 9);
            Art.CastPath(c, cap, 1.4f);
        }
    }
}
