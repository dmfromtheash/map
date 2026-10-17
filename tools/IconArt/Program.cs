// Map's icon art in the program's own style ("Свеча и орнамент" + the tavern palette): every emblem
// is the same solid silhouette vocabulary as the tool rail (MapIcons.Solid*), cast in brass - light
// at the top, deep at the bottom, a dark engraved edge and engraved detail lines - and, where an
// icon needs a ground, it sits on dark wood framed like the program's panels (a brass hairline, a
// dark gap and a fainter inner brass line) with the warm candle glow from below.
//   dotnet run --project tools/IconArt -- <output folder> [<display font .ttf>]
// Writes the tool icons and map icons the program uses (Assets/Atlas) and six application icons.
using SkiaSharp;

var output = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "icon-art");
Directory.CreateDirectory(output);
var fontPath = args.Length > 1 ? args[1] : "";

// Tool icons shown large in panels (the rail draws the same shapes as vector glyphs).
foreach (var (name, glyph) in Glyphs.Tools)
    Art.Save(Path.Combine(output, name + ".png"), 512, c => Art.Cast(c, glyph, new SKRect(28, 28, 228, 228)));

// Map icons of the library and of the create-map dialog: a folded map plate; the finer its
// engraved grid, the bigger the canvas.
Art.Save(Path.Combine(output, "map.png"), 512, c => Art.MapPlate(c, 0));
Art.Save(Path.Combine(output, "map-custom.png"), 512, c => { Art.MapPlate(c, 0); Art.Corners(c); });
Art.Save(Path.Combine(output, "map-new.png"), 512, c => { Art.MapPlate(c, 0); Art.PlusSeal(c); });
foreach (var (name, level) in new[] { ("standard", 1), ("large", 2), ("huge", 3), ("max", 4) })
    Art.Save(Path.Combine(output, $"map-{name}.png"), 512, c => Art.MapPlate(c, level));

var apps = new (string Key, Action<SKCanvas> Emblem)[]
{
    ("1", Art.EmblemCompass),
    ("2", c => Art.MapPlate(c, 0, new SKRect(52, 62, 204, 194))),
    ("3", Art.EmblemQuill),
    ("4", c => Art.Cast(c, Glyphs.Keep, new SKRect(62, 54, 194, 196))),
    ("5", Art.EmblemLand),
    ("6", c => Art.EmblemMonogram(c, fontPath)),
};
foreach (var (key, emblem) in apps)
{
    void Draw(SKCanvas c) { Art.Board(c); emblem(c); }
    Art.Save(Path.Combine(output, $"app-{key}.png"), 1024, Draw);
    Art.Save(Path.Combine(output, $"app-{key}-256.png"), 256, Draw);
    Art.Save(Path.Combine(output, $"app-{key}-64.png"), 64, Draw);
    Art.Save(Path.Combine(output, $"app-{key}-32.png"), 32, Draw);
    Art.Save(Path.Combine(output, $"app-{key}-16.png"), 16, Draw);
}
// Six style directions to choose from (Styles.cs).
Styles.Write(output);
Console.WriteLine("written to " + output);

/// <summary>The rail's solid silhouettes (24-unit grid, from MapIcons), plus the emblems' own.</summary>
static class Glyphs
{
    public record Glyph(string Fill, string? Stroke = null, float StrokeWidth = 2.6f);

    public static readonly Glyph Select = new("M5 2.5 L5 19.6 L9.7 15.3 L12.7 21.7 L15.8 20.2 L12.8 13.9 L19.2 13.6 Z");
    public static readonly Glyph Keep = new(
        "M3.6 21.6 V6.4 H6.4 V8.8 H8.6 V6.4 H11 V8.8 H13 V6.4 H15.4 V8.8 H17.6 V6.4 H20.4 V21.6 H14.6 V17.2 " +
        "A2.6 2.6 0 0 0 9.4 17.2 V21.6 Z M10.9 11.2 H13.1 V14 H10.9 Z M11.4 1.6 V6 H12.6 V3.9 L15.4 3.1 L12.6 2.3 V1.6 Z");
    public static readonly Glyph Land = new(
        "M2.8 14.6 C2.8 10.5 6.1 8.4 9 8.9 C10.3 5.2 15.5 4 18.3 6.9 C21.1 9.6 20.8 14.6 17.4 16.4 " +
        "C14.9 17.7 12.5 17 10.2 18.3 C7 20 2.8 18.8 2.8 14.6 Z M7.9 14.4 L10.5 10.8 L12.3 13 L14 10.8 L16.8 14.4 Z");
    public static readonly Glyph Line = new(
        "M4.4 15.6 A2.6 2.6 0 1 0 4.4 20.8 A2.6 2.6 0 1 0 4.4 15.6 Z M19.6 3.6 A2.6 2.6 0 1 0 19.6 8.8 A2.6 2.6 0 1 0 19.6 3.6 Z",
        "M4.4 18.2 C8.2 8.6 12.2 19.8 19.6 6.2", 2.6f);
    public static readonly Glyph Shape = new(
        "M12 2.2 L20.4 5.2 V11.4 C20.4 16.6 16.8 20.2 12 22 C7.2 20.2 3.6 16.6 3.6 11.4 V5.2 Z " +
        "M12 4.8 V19.4 C8.6 17.8 6.1 15.2 6.1 11.4 V7 Z");
    public static readonly Glyph Quill = new(
        "M21.2 2.4 C14.2 3.2 9.4 7.6 7.6 14 L6.3 18.6 L7.9 19.1 L9.3 15.4 C13.8 15 17.8 12 18.9 8.1 L15.8 8.6 " +
        "L19.5 5.9 C20.3 4.8 20.8 3.7 21.2 2.4 Z", "M3 21.6 H13", 1.8f);
    public static readonly Glyph Scroll = new(
        "M7.2 2.6 H18.2 A2.6 2.6 0 0 1 18.2 7.8 H17 V18.8 A2.8 2.8 0 0 1 14.2 21.6 H5.4 A2.6 2.6 0 0 1 5.4 16.4 H6.6 V3.2 " +
        "C6.6 2.9 6.9 2.6 7.2 2.6 Z M9.2 7.6 H14.6 V9.2 H9.2 Z M9.2 11 H14.6 V12.6 H9.2 Z M9.2 14.4 H13 V16 H9.2 Z");
    public static readonly Glyph Brush = new(
        "M21 2.1 C21.9 3 21.4 4.4 20.4 5.4 L13.5 12.7 L11.3 10.5 L18.6 3.6 C19.6 2.6 20.2 1.3 21 2.1 Z " +
        "M10.4 11.5 L12.5 13.6 C12.4 17 10.2 19.8 6.4 20.6 C5.1 20.9 3.7 21 2.4 20.8 C3.5 19.8 4 18.5 4.3 17 " +
        "C4.8 13.9 7.1 11.7 10.4 11.5 Z");
    public static readonly Glyph Mask = new(
        "M2.2 7.6 C5.8 6 9.4 7.1 12 9.1 C14.6 7.1 18.2 6 21.8 7.6 C21.9 12.8 19.6 16.4 16.2 16.4 " +
        "C14.4 16.4 13 15.2 12 13.6 C11 15.2 9.6 16.4 7.8 16.4 C4.4 16.4 2.1 12.8 2.2 7.6 Z " +
        "M5.6 10.6 C6.8 9.6 8.6 9.6 9.8 10.8 C8.6 12.2 6.8 12.2 5.6 10.6 Z " +
        "M18.4 10.6 C17.2 9.6 15.4 9.6 14.2 10.8 C15.4 12.2 17.2 12.2 18.4 10.6 Z");

    public static readonly (string Name, Glyph Glyph)[] Tools =
    [
        ("select", Select), ("land", Land), ("line", Line), ("shape", Shape),
        ("text", Quill), ("notes", Scroll), ("brush", Brush), ("mask", Mask),
    ];
}

static class Art
{
    public static readonly SKColor Wood = SKColor.Parse("#17110D");
    public static readonly SKColor WoodWarm = SKColor.Parse("#3A2B1E");
    public static readonly SKColor BrassLight = SKColor.Parse("#F6E2B0");
    public static readonly SKColor Brass = SKColor.Parse("#C9A15A");
    public static readonly SKColor BrassDeep = SKColor.Parse("#7A5A2C");
    public static readonly SKColor Engrave = SKColor.Parse("#2A1D12");

    public static void Save(string path, int size, Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(size / 256f);
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    static SKPaint StrokePaint(SKColor color, float width) => new()
    { IsAntialias = true, Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    /// <summary>Cast brass: light from above, deep below, a warm middle - the program's brass gradient.</summary>
    static SKShader BrassFill(SKRect r) => SKShader.CreateLinearGradient(
        new SKPoint(r.MidX - r.Width * .15f, r.Top), new SKPoint(r.MidX + r.Width * .15f, r.Bottom),
        [BrassLight, SKColor.Parse("#E2BF78"), Brass, BrassDeep], [0, .3f, .62f, 1], SKShaderTileMode.Clamp);

    static void Shadow(SKCanvas c, SKPath shape, float dy = 4, float blur = 5, byte alpha = 150)
    {
        using var p = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(alpha), ImageFilter = SKImageFilter.CreateBlur(blur, blur) };
        c.Save(); c.Translate(0, dy); c.DrawPath(shape, p); c.Restore();
    }

    /// <summary>
    /// A shape cast in brass: the gradient body, an inner light along the upper edge, a dark
    /// engraved rim and a soft shadow. Every icon is built from this.
    /// </summary>
    public static void CastPath(SKCanvas c, SKPath shape, float rim = 2.2f)
    {
        var r = shape.Bounds;
        Shadow(c, shape);
        using var body = new SKPaint { IsAntialias = true, Shader = BrassFill(r) };
        c.DrawPath(shape, body);
        // the lit upper edge: the shape minus itself shifted down
        c.Save(); c.ClipPath(shape, antialias: true);
        using var shifted = new SKPath(shape); shifted.Transform(SKMatrix.CreateTranslation(0, 3));
        using var light = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#FFF4D6").WithAlpha(120) };
        using var lightPath = shape.Op(shifted, SKPathOp.Difference);
        if (lightPath is not null) c.DrawPath(lightPath, light);
        c.Restore();
        using var edge = StrokePaint(Engrave.WithAlpha(230), rim);
        c.DrawPath(shape, edge);
    }

    /// <summary>A rail glyph scaled into <paramref name="box"/> and cast in brass (its line parts as brass strokes).</summary>
    public static void Cast(SKCanvas c, Glyphs.Glyph glyph, SKRect box)
    {
        using var fill = SKPath.ParseSvgPathData(glyph.Fill)!;
        fill.FillType = SKPathFillType.EvenOdd;
        using var stroke = glyph.Stroke is { } s ? SKPath.ParseSvgPathData(s) : null;
        var bounds = fill.Bounds;
        if (stroke is not null) { var sb = stroke.Bounds; sb.Inflate(glyph.StrokeWidth / 2, glyph.StrokeWidth / 2); bounds.Union(sb); }
        var scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
        var matrix = SKMatrix.CreateTranslation(-bounds.MidX, -bounds.MidY)
            .PostConcat(SKMatrix.CreateScale(scale, scale))
            .PostConcat(SKMatrix.CreateTranslation(box.MidX, box.MidY));
        using var shape = new SKPath(fill); shape.Transform(matrix);
        if (stroke is not null)
        {
            using var line = new SKPath(stroke); line.Transform(matrix);
            using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = glyph.StrokeWidth * scale,
                StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using var stroked = outline.GetFillPath(line);
            using var union = stroked.Op(shape, SKPathOp.Union) ?? new SKPath(shape);
            CastPath(c, union, Math.Max(1.4f, scale * .12f));
            return;
        }
        CastPath(c, shape, Math.Max(1.4f, scale * .12f));
    }

    /// <summary>
    /// A folded map as one brass plate, its three panels creased, with an engraved river and
    /// mountains; <paramref name="level"/> 2..4 engraves a grid (2×2, 3×3, 4×4 and a rose).
    /// </summary>
    public static void MapPlate(SKCanvas c, int level, SKRect? area = null)
    {
        var r = area ?? new SKRect(26, 50, 230, 206);
        var w = r.Width / 3; var d = r.Height * .07f;
        using var sheet = new SKPath();
        sheet.MoveTo(r.Left, r.Top + d); sheet.LineTo(r.Left + w, r.Top); sheet.LineTo(r.Left + 2 * w, r.Top + d);
        sheet.LineTo(r.Right, r.Top); sheet.LineTo(r.Right, r.Bottom - d); sheet.LineTo(r.Left + 2 * w, r.Bottom);
        sheet.LineTo(r.Left + w, r.Bottom - d); sheet.LineTo(r.Left, r.Bottom); sheet.Close();
        CastPath(c, sheet, 2.4f);
        c.Save(); c.ClipPath(sheet, antialias: true);
        // the outer panels turn away from the light
        using var shade = new SKPaint { IsAntialias = true, Color = Engrave.WithAlpha(46) };
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + w, r.Bottom), shade);
        c.DrawRect(new SKRect(r.Left + 2 * w, r.Top, r.Right, r.Bottom), shade);
        var inner = new SKRect(r.Left + r.Width * .05f, r.Top + r.Height * .12f, r.Right - r.Width * .05f, r.Bottom - r.Height * .12f);
        Engraving(c, inner, level, r.Width / 204f);
        c.Restore();
        using var crease = StrokePaint(Engrave.WithAlpha(150), 1.6f * r.Width / 204f);
        c.DrawLine(r.Left + w, r.Top, r.Left + w, r.Bottom - d, crease);
        c.DrawLine(r.Left + 2 * w, r.Top + d, r.Left + 2 * w, r.Bottom, crease);
    }

    static void Engraving(SKCanvas c, SKRect r, int level, float k)
    {
        using var line = StrokePaint(Engrave.WithAlpha(215), 3.2f * k);
        using var thin = StrokePaint(Engrave.WithAlpha(150), 1.5f * k);
        using var river = new SKPath();
        river.MoveTo(r.Left + r.Width * .04f, r.Top + r.Height * .1f);
        river.CubicTo(r.Left + r.Width * .4f, r.Top + r.Height * .15f, r.Left + r.Width * .14f, r.Top + r.Height * .62f, r.Left + r.Width * .48f, r.Top + r.Height * .68f);
        river.CubicTo(r.Left + r.Width * .74f, r.Top + r.Height * .73f, r.Left + r.Width * .74f, r.Top + r.Height * .95f, r.Right - r.Width * .02f, r.Bottom);
        c.DrawPath(river, line);
        void Peak(float x, float y, float s)
        {
            using var m = new SKPath(); m.MoveTo(x - s, y); m.LineTo(x, y - s * 1.15f); m.LineTo(x + s, y);
            c.DrawPath(m, line);
            for (var i = 1; i <= 3; i++) c.DrawLine(x + s * i / 4.5f, y - s * 1.15f * (1 - i / 4.5f), x + s * i / 4.5f + s * .2f, y, thin);
        }
        Peak(r.Left + r.Width * .66f, r.Top + r.Height * .36f, r.Width * .12f);
        Peak(r.Left + r.Width * .84f, r.Top + r.Height * .42f, r.Width * .08f);
        Peak(r.Left + r.Width * .26f, r.Top + r.Height * .9f, r.Width * .07f);
        if (level >= 2)
        {
            var cells = level switch { 2 => 2, 3 => 3, _ => 4 };
            using var grid = StrokePaint(Engrave.WithAlpha(170), 1.8f * k);
            grid.PathEffect = SKPathEffect.CreateDash([6 * k, 4 * k], 0);
            for (var i = 1; i < cells; i++)
            {
                c.DrawLine(r.Left + r.Width * i / cells, r.Top - 10 * k, r.Left + r.Width * i / cells, r.Bottom + 10 * k, grid);
                c.DrawLine(r.Left - 6 * k, r.Top + r.Height * i / cells, r.Right + 6 * k, r.Top + r.Height * i / cells, grid);
            }
        }
        if (level >= 4)
        {
            using var rose = RosePath(new SKPoint(r.Left + r.Width * .2f, r.Top + r.Height * .34f), r.Width * .12f, 4, .26f);
            using var ink = new SKPaint { IsAntialias = true, Color = Engrave.WithAlpha(170) };
            c.DrawPath(rose, ink);
        }
    }

    static SKPath RosePath(SKPoint o, float s, int points, float waist)
    {
        var p = new SKPath();
        for (var i = 0; i < points; i++)
        {
            var a = i * MathF.PI * 2 / points - MathF.PI / 2;
            var len = points == 8 && i % 2 == 1 ? s * .55f : s;
            p.MoveTo(o);
            p.LineTo(o.X + MathF.Cos(a - MathF.PI / points) * s * waist, o.Y + MathF.Sin(a - MathF.PI / points) * s * waist);
            p.LineTo(o.X + MathF.Cos(a) * len, o.Y + MathF.Sin(a) * len);
            p.LineTo(o.X + MathF.Cos(a + MathF.PI / points) * s * waist, o.Y + MathF.Sin(a + MathF.PI / points) * s * waist);
            p.Close();
        }
        return p;
    }

    /// <summary>"Свой размер": brass corner marks around the plate.</summary>
    public static void Corners(SKCanvas c)
    {
        foreach (var (x, y, dx, dy) in new[] { (12f, 30f, 1, 1), (244f, 30f, -1, 1), (12f, 226f, 1, -1), (244f, 226f, -1, -1) })
        {
            using var mark = new SKPath();
            mark.MoveTo(x, y + dy * 30); mark.LineTo(x, y); mark.LineTo(x + dx * 30, y);
            using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using var band = pen.GetFillPath(mark);
            CastPath(c, band, 1.4f);
        }
    }

    /// <summary>The "new" mark: a brass seal with an engraved plus, lower right.</summary>
    public static void PlusSeal(SKCanvas c)
    {
        using var seal = new SKPath(); seal.AddCircle(198, 194, 40);
        CastPath(c, seal, 3);
        using var ring = StrokePaint(Engrave.WithAlpha(150), 2); c.DrawCircle(198, 194, 31, ring);
        using var plus = StrokePaint(Engrave, 9);
        c.DrawLine(181, 194, 215, 194, plus); c.DrawLine(198, 177, 198, 211, plus);
    }

    // ------------------------------------------------------------------ the application board

    /// <summary>
    /// Dark wood, framed like the program's panels - a brass hairline, a dark gap, a fainter inner
    /// brass line - with the candle's warm glow from below.
    /// </summary>
    public static void Board(SKCanvas c)
    {
        var outer = new SKRect(10, 10, 246, 246);
        using var shape = new SKPath(); shape.AddRoundRect(outer, 34, 34);
        Shadow(c, shape, 4, 6, 140);
        using var wood = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(128, 112), 190,
            [WoodWarm, SKColor.Parse("#221911"), Wood], [0, .55f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(shape, wood);
        c.Save(); c.ClipPath(shape, antialias: true);
        using var grain = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(46),
            Shader = SKShader.CreatePerlinNoiseFractalNoise(.004f, .09f, 2, 5) };
        c.DrawRect(outer, grain);
        using var glow = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(128, 250), 170,
            [SKColor.Parse("#FFB347").WithAlpha(92), SKColor.Parse("#FFB347").WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawRect(outer, glow);
        c.Restore();
        using var hairline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, Shader = BrassFill(outer) };
        c.DrawRoundRect(new SKRect(13, 13, 243, 243), 31, 31, hairline);
        using var gap = StrokePaint(Wood, 3); c.DrawRoundRect(new SKRect(18, 18, 238, 238), 27, 27, gap);
        using var inner = StrokePaint(Brass.WithAlpha(120), 1.6f); c.DrawRoundRect(new SKRect(21.5f, 21.5f, 234.5f, 234.5f), 24, 24, inner);
    }

    public static void EmblemCompass(SKCanvas c)
    {
        var o = new SKPoint(128, 128);
        using var ring = new SKPath(); ring.AddCircle(o.X, o.Y, 86); ring.AddCircle(o.X, o.Y, 74); ring.FillType = SKPathFillType.EvenOdd;
        CastPath(c, ring, 2);
        using var tick = StrokePaint(Engrave.WithAlpha(190), 2);
        for (var i = 0; i < 32; i++)
        {
            var a = i * MathF.PI * 2 / 32; var r1 = i % 4 == 0 ? 75f : 80f;
            c.DrawLine(o.X + MathF.Cos(a) * r1, o.Y + MathF.Sin(a) * r1, o.X + MathF.Cos(a) * 85, o.Y + MathF.Sin(a) * 85, tick);
        }
        using var rose = RosePath(o, 96, 8, .17f);
        CastPath(c, rose, 2);
        // each point split into a lit and a shaded side
        using var half = new SKPaint { IsAntialias = true, Color = Engrave.WithAlpha(80) };
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4 - MathF.PI / 2; var len = i % 2 == 1 ? 96 * .55f : 96;
            using var h = new SKPath(); h.MoveTo(o);
            h.LineTo(o.X + MathF.Cos(a + MathF.PI / 8) * 96 * .17f, o.Y + MathF.Sin(a + MathF.PI / 8) * 96 * .17f);
            h.LineTo(o.X + MathF.Cos(a) * len, o.Y + MathF.Sin(a) * len); h.Close();
            c.DrawPath(h, half);
        }
        using var hub = new SKPath(); hub.AddCircle(o.X, o.Y, 10);
        CastPath(c, hub, 2);
    }

    public static void EmblemQuill(SKCanvas c)
    {
        Cast(c, Glyphs.Scroll, new SKRect(52, 74, 156, 214));
        Cast(c, Glyphs.Quill, new SKRect(106, 40, 214, 172));
    }

    public static void EmblemLand(SKCanvas c)
    {
        Cast(c, Glyphs.Land, new SKRect(44, 52, 212, 172));
        using var river = new SKPath();
        river.MoveTo(54, 204); river.CubicTo(90, 184, 120, 212, 150, 194); river.CubicTo(170, 182, 190, 198, 206, 190);
        using var pen = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 11, StrokeCap = SKStrokeCap.Round };
        using var band = pen.GetFillPath(river);
        CastPath(c, band, 1.8f);
    }

    public static void EmblemMonogram(SKCanvas c, string fontPath)
    {
        var o = new SKPoint(128, 130);
        using var ring = new SKPath(); ring.AddCircle(o.X, o.Y, 88); ring.AddCircle(o.X, o.Y, 80); ring.FillType = SKPathFillType.EvenOdd;
        CastPath(c, ring, 1.6f);
        using var typeface = File.Exists(fontPath) ? SKTypeface.FromFile(fontPath) : SKTypeface.FromFamilyName("Georgia", SKFontStyle.Bold);
        using var font = new SKFont(typeface, 150);
        using var letter = font.GetTextPath("M", new SKPoint(0, 0));
        var b = letter.Bounds;
        letter.Transform(SKMatrix.CreateTranslation(o.X - b.MidX, o.Y - b.MidY));
        CastPath(c, letter, 2.2f);
        // four small diamonds on the ring - the program's ornament
        foreach (var a in new[] { 0f, 90f, 180f, 270f })
        {
            var rad = a * MathF.PI / 180;
            var p = new SKPoint(o.X + MathF.Cos(rad) * 84, o.Y + MathF.Sin(rad) * 84);
            using var dia = new SKPath(); dia.MoveTo(p.X, p.Y - 10); dia.LineTo(p.X + 7, p.Y); dia.LineTo(p.X, p.Y + 10); dia.LineTo(p.X - 7, p.Y); dia.Close();
            CastPath(c, dia, 1.4f);
        }
    }
}
