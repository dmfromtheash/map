using SkiaSharp;

/// <summary>
/// Application logo concepts, second round. Built on the home picture's motif (crystal shards
/// holding painted worlds) and the interface's wood, brass and candle light, with the techniques
/// professional icon work relies on: one metaphor (at most two) as a centred focal point; a
/// silhouette that survives 16 px (small sizes are drawn simplified, not scaled); light from the
/// top left with all three value ranges; warm candle rim light against the cool crystal for
/// contrast; the emblem breaking out of its frame for depth; and double readings through shape
/// and negative space (a crystal that is also a map pin, peaks that are also an M).
/// </summary>
static class Logos
{
    static bool Small;

    static readonly SKColor Wood = SKColor.Parse("#120D0A");
    static readonly SKColor BrassLight = SKColor.Parse("#FBE9BC");
    static readonly SKColor Brass = SKColor.Parse("#D2A65A");
    static readonly SKColor BrassDeep = SKColor.Parse("#6E4F24");
    static readonly SKColor Ice = SKColor.Parse("#E6F4FD");
    static readonly SKColor IceMid = SKColor.Parse("#8CC6E8");
    static readonly SKColor IceDeep = SKColor.Parse("#2E6E9A");
    static readonly SKColor Candle = SKColor.Parse("#FFB04A");

    public static void Write(string output)
    {
        var concepts = new (string Key, Action<SKCanvas> Draw)[]
        {
            ("1", CrystalPin), ("2", ShardBeacon), ("3", NorthCrystal),
            ("4", PeaksM), ("5", OrbBreakout), ("6", Shattering),
        };
        foreach (var (key, draw) in concepts)
            foreach (var size in new[] { 1024, 256, 64, 32, 16 })
            {
                Small = size <= 32;
                Art.Save(Path.Combine(output, $"l-{key}-{size}.png"), size, draw);
            }
        Small = false;
    }

    // ------------------------------------------------------------------ helpers

    static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Color = c };
    static SKPaint Pen(SKColor c, float w) => new() { IsAntialias = true, Color = c, Style = SKPaintStyle.Stroke, StrokeWidth = w, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    static SKShader V(SKRect r, params SKColor[] c) => SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), c, null, SKShaderTileMode.Clamp);
    static SKShader TopLeft(SKRect r, params SKColor[] c) => SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom), c, null, SKShaderTileMode.Clamp);

    static SKPath Poly(params (float X, float Y)[] p)
    {
        var path = new SKPath(); path.MoveTo(p[0].X, p[0].Y);
        foreach (var q in p.Skip(1)) path.LineTo(q.X, q.Y);
        path.Close(); return path;
    }

    static void Shadow(SKCanvas c, SKPath p, float dy = 6, float blur = 7, byte a = 170)
    {
        using var s = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(a), ImageFilter = SKImageFilter.CreateBlur(blur, blur) };
        c.Save(); c.Translate(dy * .3f, dy); c.DrawPath(p, s); c.Restore();
    }

    static void Glow(SKCanvas c, SKPoint o, float r, SKColor color, byte a)
    {
        // a glow cut by the image edge shows as a light box around the icon
        r = Math.Min(r, Math.Min(Math.Min(o.X, 256 - o.X), Math.Min(o.Y, 256 - o.Y)) - 1);
        if (r <= 1) return;
        using var p = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(o, r, [color.WithAlpha(a), color.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawCircle(o, r, p);
    }

    /// <summary>
    /// The plate behind every concept: a darker, smaller wood board than before, so the emblem can
    /// break out of it; a brass hairline frame; light from the top left and candle warmth below.
    /// </summary>
    static void Plate(SKCanvas c, float inset = 24)
    {
        var r = new SKRect(inset, inset, 256 - inset, 256 - inset);
        using var shape = new SKPath(); shape.AddRoundRect(r, 30, 30);
        Shadow(c, shape, 6, 8, 160);
        using var wood = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(r.Left + r.Width * .3f, r.Top + r.Height * .25f), r.Width * 1.05f,
            [SKColor.Parse("#3B2A1C"), SKColor.Parse("#1E1610"), Wood], [0, .5f, 1], SKShaderTileMode.Clamp) };
        c.DrawPath(shape, wood);
        c.Save(); c.ClipPath(shape, antialias: true);
        if (!Small)
        {
            using var grain = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Overlay, Color = SKColors.White.WithAlpha(40),
                Shader = SKShader.CreatePerlinNoiseFractalNoise(.004f, .09f, 2, 5) };
            c.DrawRect(r, grain);
        }
        Glow(c, new SKPoint(r.MidX + 30, r.Bottom + 20), r.Width * .8f, Candle, 70);
        c.Restore();
        using var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Small ? 7 : 4.5f,
            Shader = TopLeft(r, BrassLight, Brass, BrassDeep, Brass) };
        c.DrawRoundRect(r, 30, 30, frame);
        if (!Small) { using var inner = Pen(Brass.WithAlpha(90), 1.4f); c.DrawRoundRect(new SKRect(r.Left + 7, r.Top + 7, r.Right - 7, r.Bottom - 7), 24, 24, inner); }
    }

    /// <summary>A small painted world: ice sky, snowy peaks lit from the left, forest, sea.</summary>
    static void World(SKCanvas c, SKRect r, int seed = 3)
    {
        using var sky = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#F3F9FE"), SKColor.Parse("#B4D6EE")) };
        c.DrawRect(r, sky);
        var sea = new SKRect(r.Left, r.Top + r.Height * .66f, r.Right, r.Bottom);
        using var water = new SKPaint { IsAntialias = true, Shader = V(sea, SKColor.Parse("#52B5CC"), SKColor.Parse("#155C80")) };
        c.DrawRect(sea, water);
        using var land = new SKPath();
        land.MoveTo(r.Left - 4, r.Top + r.Height * .56f);
        land.CubicTo(r.Left + r.Width * .35f, r.Top + r.Height * .48f, r.Left + r.Width * .6f, r.Top + r.Height * .55f, r.Right + 4, r.Top + r.Height * .5f);
        land.LineTo(r.Right + 4, r.Top + r.Height * .7f);
        land.CubicTo(r.Left + r.Width * .65f, r.Top + r.Height * .8f, r.Left + r.Width * .35f, r.Top + r.Height * .68f, r.Left - 4, r.Top + r.Height * .78f);
        land.Close();
        using var grass = new SKPaint { IsAntialias = true, Shader = V(new SKRect(0, r.Top + r.Height * .48f, 0, r.Top + r.Height * .8f), SKColor.Parse("#A6CC6E"), SKColor.Parse("#477538")) };
        c.DrawPath(land, grass);
        void Peak(float fx, float fb, float fw, float fh)
        {
            float x = r.Left + r.Width * fx, b = r.Top + r.Height * fb, w = r.Width * fw, h = r.Height * fh;
            using var lit = Poly((x - w, b), (x, b - h), (x + w * .08f, b));
            using var dark = Poly((x + w * .08f, b), (x, b - h), (x + w, b));
            using var lp = Fill(SKColor.Parse("#9AA7B5")); c.DrawPath(lit, lp);
            using var dp = Fill(SKColor.Parse("#56677A")); c.DrawPath(dark, dp);
            using var snow = Poly((x - w * .4f, b - h * .6f), (x, b - h), (x + w * .42f, b - h * .58f), (x + w * .15f, b - h * .66f), (x, b - h * .55f), (x - w * .18f, b - h * .68f));
            using var sp = Fill(SKColors.White); c.DrawPath(snow, sp);
        }
        Peak(.3f, .56f, .22f, .38f); Peak(.6f, .55f, .3f, .5f); Peak(.86f, .53f, .18f, .3f);
        if (Small) return;
        var rnd = new Random(seed);
        for (var i = 0; i < 24; i++)
        {
            var x = r.Left + r.Width * (.05f + (float)rnd.NextDouble() * .85f);
            var y = r.Top + r.Height * (.6f + (float)rnd.NextDouble() * .1f);
            var s = r.Width * .045f * (.7f + (float)rnd.NextDouble() * .6f);
            using var tree = Poly((x - s * .5f, y), (x, y - s * 1.5f), (x + s * .5f, y));
            using var tp = Fill(rnd.Next(3) == 0 ? SKColor.Parse("#5E8C45") : SKColor.Parse("#24492B")); c.DrawPath(tree, tp);
        }
        using var wave = Pen(SKColor.Parse("#C6ECF4").WithAlpha(150), r.Width * .01f);
        for (var y = sea.Top + sea.Height * .25f; y < sea.Bottom; y += sea.Height * .25f)
            for (var x = sea.Left + (y % 9); x < sea.Right; x += r.Width * .18f)
                c.DrawLine(x, y, x + r.Width * .06f, y, wave);
    }

    /// <summary>
    /// A crystal seen through: the world inside, faceted light from the top left, a cool core, a
    /// warm candle rim along the lower right edge, white glints, and an icy halo.
    /// </summary>
    static void Crystal(SKCanvas c, SKPath shape, SKPoint facetCentre, Action<SKCanvas, SKRect> inside, float edge = 2.6f, bool halo = true)
    {
        var b = shape.Bounds;
        if (halo) Glow(c, new SKPoint(b.MidX, b.MidY), Math.Max(b.Width, b.Height) * .75f, SKColor.Parse("#A8DBFF"), 110);
        Shadow(c, shape, 6, 7, 150);
        c.Save(); c.ClipPath(shape, antialias: true);
        inside(c, b);
        // facets from an off-centre apex to each corner: alternately frosted and clear
        var pts = shape.Points;
        for (var i = 0; i < pts.Length; i++)
        {
            var a = pts[i]; var z = pts[(i + 1) % pts.Length];
            using var facet = Poly((facetCentre.X, facetCentre.Y), (a.X, a.Y), (z.X, z.Y));
            var lit = (a.X + z.X) / 2 < facetCentre.X || (a.Y + z.Y) / 2 < facetCentre.Y;
            var alpha = (byte)(lit ? (i % 2 == 0 ? 95 : 55) : (i % 2 == 0 ? 25 : 10));
            using var frost = Fill((lit ? SKColors.White : IceDeep).WithAlpha(alpha));
            c.DrawPath(facet, frost);
            if (!Small) { using var seam = Pen(SKColors.White.WithAlpha(90), edge * .4f); c.DrawLine(facetCentre.X, facetCentre.Y, a.X, a.Y, seam); }
        }
        // the warm rim: candle light catching the lower right
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = edge * 3.5f,
            Shader = SKShader.CreateLinearGradient(new SKPoint(b.Left, b.Top), new SKPoint(b.Right, b.Bottom),
                [Candle.WithAlpha(0), Candle.WithAlpha(0), Candle.WithAlpha(200)], [0, .55f, 1], SKShaderTileMode.Clamp),
            ImageFilter = SKImageFilter.CreateBlur(edge, edge) };
        c.DrawPath(shape, rim);
        c.Restore();
        using var outline = Pen(SKColors.White.WithAlpha(240), Small ? edge * 1.6f : edge); c.DrawPath(shape, outline);
        if (!Small)
        {
            // two sparkles on the lit corners
            var top = pts.OrderBy(p => p.Y + p.X * .3f).First();
            Sparkle(c, new SKPoint(top.X + 2, top.Y + 4), 12);
        }
    }

    static void Sparkle(SKCanvas c, SKPoint o, float s)
    {
        using var p = new SKPath();
        p.MoveTo(o.X, o.Y - s); p.QuadTo(o.X, o.Y, o.X + s, o.Y); p.QuadTo(o.X, o.Y, o.X, o.Y + s); p.QuadTo(o.X, o.Y, o.X - s, o.Y); p.QuadTo(o.X, o.Y, o.X, o.Y - s); p.Close();
        Glow(c, o, s * 1.4f, SKColors.White, 160);
        using var f = Fill(SKColors.White); c.DrawPath(p, f);
    }

    static void CastBrass(SKCanvas c, SKPath p, float rim = 2f) => Art.CastPath(c, p, Small ? rim * 1.8f : rim);

    // ------------------------------------------------------------------ concepts

    /// <summary>1 - a crystal map pin (where + world): its head holds a world, its point lands on a brass map.</summary>
    static void CrystalPin(SKCanvas c)
    {
        Plate(c);
        // the brass folded map the pin stands on, low in the plate
        using var sheet = Poly((52, 176), (98, 166), (148, 178), (204, 166), (212, 214), (156, 226), (106, 214), (48, 224));
        CastBrass(c, sheet, 2);
        if (!Small)
        {
            using var crease = Pen(SKColor.Parse("#2A1D12").WithAlpha(140), 1.6f);
            c.DrawLine(98, 166, 106, 214, crease); c.DrawLine(148, 178, 156, 226, crease);
            using var route = Pen(SKColor.Parse("#2A1D12").WithAlpha(170), 2.2f); route.PathEffect = SKPathEffect.CreateDash([5, 4], 0);
            using var path = new SKPath(); path.MoveTo(66, 212); path.CubicTo(90, 186, 112, 214, 128, 200); c.DrawPath(path, route);
        }
        using var ripple = Pen(BrassLight.WithAlpha(160), 2); c.DrawOval(new SKRect(108, 194, 148, 206), ripple);
        // the pin: a faceted teardrop breaking out over the top edge of the plate
        using var pin = new SKPath();
        pin.MoveTo(128, 202); pin.LineTo(84, 118); pin.LineTo(88, 62); pin.LineTo(128, 16); pin.LineTo(168, 62); pin.LineTo(172, 118); pin.Close();
        Crystal(c, pin, new SKPoint(118, 72), (cv, r) => World(cv, r, 11), 2.8f);
    }

    /// <summary>2 - one tall shard rising out of the plate like a beacon, a whole world in it, splinters orbiting.</summary>
    static void ShardBeacon(SKCanvas c)
    {
        Plate(c, 30);
        Glow(c, new SKPoint(128, 150), 110, SKColor.Parse("#7FC3F0"), 70);
        using var shard = Poly((122, 6), (178, 62), (186, 166), (140, 238), (84, 214), (66, 120), (86, 52));
        Crystal(c, shard, new SKPoint(116, 70), (cv, r) => World(cv, r, 5), 3);
        if (Small) return;
        foreach (var (x, y, s) in new[] { (44f, 86f, 12f), (212f, 120f, 9f), (204f, 200f, 7f) })
        {
            using var sp = Poly((x, y - s * 1.4f), (x + s, y), (x, y + s * 1.4f), (x - s, y));
            Crystal(c, sp, new SKPoint(x - s * .3f, y - s * .4f), (cv, r) => { using var ice = new SKPaint { Shader = V(r, Ice, IceMid) }; cv.DrawRect(r, ice); }, 1.4f, false);
        }
    }

    /// <summary>3 - a compass rose whose north point is a crystal shard that breaks out of the ring and the plate.</summary>
    static void NorthCrystal(SKCanvas c)
    {
        Plate(c);
        var o = new SKPoint(128, 140);
        using var ring = new SKPath(); ring.AddCircle(o.X, o.Y, 78); ring.AddCircle(o.X, o.Y, 68); ring.FillType = SKPathFillType.EvenOdd;
        CastBrass(c, ring, 1.6f);
        if (!Small)
        {
            using var tick = Pen(SKColor.Parse("#2A1D12").WithAlpha(170), 1.8f);
            for (var i = 0; i < 32; i++) { var a = i * MathF.PI / 16; c.DrawLine(o.X + MathF.Cos(a) * 69, o.Y + MathF.Sin(a) * 69, o.X + MathF.Cos(a) * (i % 4 == 0 ? 77 : 73), o.Y + MathF.Sin(a) * (i % 4 == 0 ? 77 : 73), tick); }
        }
        // east, south, west in brass
        using var points = new SKPath();
        foreach (var a in new[] { 0f, 90f, 180f })
        {
            var rad = a * MathF.PI / 180;
            var tip = new SKPoint(o.X + MathF.Cos(rad) * 92, o.Y + MathF.Sin(rad) * 92);
            var l = new SKPoint(o.X + MathF.Cos(rad - .5f) * 18, o.Y + MathF.Sin(rad - .5f) * 18);
            var r = new SKPoint(o.X + MathF.Cos(rad + .5f) * 18, o.Y + MathF.Sin(rad + .5f) * 18);
            points.MoveTo(o); points.LineTo(l); points.LineTo(tip); points.LineTo(r); points.Close();
        }
        CastBrass(c, points, 1.6f);
        // north: the crystal, far longer, out through the top
        using var north = Poly((128, 6), (150, 96), (128, 156), (106, 96));
        Crystal(c, north, new SKPoint(122, 60), (cv, r) => World(cv, new SKRect(r.Left - 30, r.Top, r.Right + 30, r.Bottom), 8), 2.6f);
        using var hub = new SKPath(); hub.AddCircle(o.X, o.Y, 11); CastBrass(c, hub, 1.6f);
    }

    /// <summary>4 - two snowy peaks that read as an M (the program's name) inside a cut crystal.</summary>
    static void PeaksM(SKCanvas c)
    {
        Plate(c);
        var o = new SKPoint(128, 128);
        var hex = Enumerable.Range(0, 6).Select(i => { var a = i * MathF.PI / 3 - MathF.PI / 2; return (o.X + MathF.Cos(a) * 104, o.Y + MathF.Sin(a) * 104); }).ToArray();
        using var gem = Poly(hex);
        Crystal(c, gem, new SKPoint(112, 96), (cv, r) =>
        {
            using var sky = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#F3F9FE"), SKColor.Parse("#9FCBEA")) };
            cv.DrawRect(r, sky);
            // the M: two peaks joined by a valley, the left faces lit, snow on both
            using var m = Poly((50, 188), (88, 70), (128, 158), (168, 70), (206, 188));
            using var mp = new SKPaint { IsAntialias = true, Shader = TopLeft(new SKRect(50, 82, 206, 188), SKColor.Parse("#A7B3C0"), SKColor.Parse("#4E5F72")) };
            cv.DrawPath(m, mp);
            using var shadeL = Poly((88, 70), (104, 188), (128, 158)); using var shadeR = Poly((168, 70), (182, 188), (206, 188));
            using var sh = Fill(SKColor.Parse("#3D4C5D").WithAlpha(150)); cv.DrawPath(shadeL, sh); cv.DrawPath(shadeR, sh);
            using var snowL = Poly((70, 118), (88, 70), (108, 112), (97, 104), (88, 116), (80, 106));
            using var snowR = Poly((148, 112), (168, 70), (188, 118), (177, 106), (168, 116), (159, 104));
            using var sn = Fill(SKColors.White); cv.DrawPath(snowL, sn); cv.DrawPath(snowR, sn);
            using var lake = new SKPaint { IsAntialias = true, Shader = V(new SKRect(0, 188, 0, 240), SKColor.Parse("#52B5CC"), SKColor.Parse("#155C80")) };
            cv.DrawRect(new SKRect(r.Left, 186, r.Right, r.Bottom), lake);
            if (!Small) { using var forest = Pen(SKColor.Parse("#24492B"), 6); forest.PathEffect = SKPathEffect.CreateDash([4, 3], 0); cv.DrawLine(60, 184, 196, 184, forest); }
        }, 3);
    }

    /// <summary>5 - a glass orb holding a world, so big it breaks out above the plate, on a brass stand.</summary>
    static void OrbBreakout(SKCanvas c)
    {
        Plate(c, 34);
        var o = new SKPoint(128, 104);
        Glow(c, o, 110, SKColor.Parse("#9FD4F5"), 90);
        using var foot = new SKPath(); foot.AddRoundRect(new SKRect(72, 204, 184, 228), 10, 10); CastBrass(c, foot, 2);
        using var neck = Poly((100, 204), (156, 204), (146, 178), (110, 178)); CastBrass(c, neck, 2);
        using var orb = new SKPath(); orb.AddCircle(o.X, o.Y, 86);
        Shadow(c, orb, 6, 8, 150);
        c.Save(); c.ClipPath(orb, antialias: true);
        World(c, new SKRect(42, 18, 214, 190), 6);
        using var rimShade = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(o, 86,
            [SKColors.Transparent, SKColors.Transparent, SKColor.Parse("#0A2333").WithAlpha(150)], [0, .68f, 1], SKShaderTileMode.Clamp) };
        c.DrawCircle(o, 86, rimShade);
        using var warm = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, Color = Candle.WithAlpha(150), ImageFilter = SKImageFilter.CreateBlur(4, 4) };
        c.DrawArc(new SKRect(42, 18, 214, 190), 20, 80, false, warm);
        using var reflection = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(60, 30), new SKPoint(120, 100),
            [SKColors.White.WithAlpha(170), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp) };
        using var arc = new SKPath(); arc.AddOval(new SKRect(54, 28, 146, 96)); c.DrawPath(arc, reflection);
        c.Restore();
        using var edge = Pen(SKColors.White.WithAlpha(220), Small ? 5 : 2.6f); c.DrawCircle(o, 86, edge);
        using var collar = new SKPath(); collar.AddOval(new SKRect(94, 172, 162, 186)); CastBrass(c, collar, 1.6f);
        if (!Small) Sparkle(c, new SKPoint(84, 52), 10);
    }

    /// <summary>6 - a map bursting into three crystal shards, light pouring out of the cracks.</summary>
    static void Shattering(SKCanvas c)
    {
        Plate(c);
        var o = new SKPoint(130, 132);
        Glow(c, o, 120, SKColor.Parse("#FFE2A6"), 120);
        Glow(c, o, 60, SKColors.White, 200);
        if (!Small)
        {
            using var rays = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(70), Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeCap = SKStrokeCap.Round,
                ImageFilter = SKImageFilter.CreateBlur(1.5f, 1.5f) };
            foreach (var a in new[] { -100f, -20f, 70f, 150f, 210f }) { var rad = a * MathF.PI / 180; c.DrawLine(o.X + MathF.Cos(rad) * 30, o.Y + MathF.Sin(rad) * 30, o.X + MathF.Cos(rad) * 110, o.Y + MathF.Sin(rad) * 110, rays); }
        }
        // three shards of one map, pushed apart from the centre, rotated a little
        void Piece((float X, float Y)[] pts, float dx, float dy, float deg, int seed)
        {
            c.Save(); c.Translate(dx, dy); c.RotateDegrees(deg, o.X, o.Y);
            using var shape = Poly(pts);
            Crystal(c, shape, new SKPoint(shape.Bounds.Left + shape.Bounds.Width * .35f, shape.Bounds.Top + shape.Bounds.Height * .3f),
                (cv, r) => World(cv, new SKRect(r.Left - 6, r.Top - r.Height * .25f, r.Right + 6, r.Bottom + 4), seed), 2.4f, false);
            c.Restore();
        }
        Piece([(36, 40), (150, 34), (126, 120), (40, 132)], -12, -12, -6, 2);
        Piece([(158, 34), (222, 44), (216, 150), (134, 124)], 14, -8, 7, 2);
        Piece([(40, 140), (128, 128), (218, 158), (210, 222), (46, 220)], 0, 14, -3, 2);
    }
}
