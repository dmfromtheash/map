using SkiaSharp;

/// <summary>
/// Application icon concepts drawn from the program's own artwork: the home picture's crystal
/// shards, each holding a painted little world (snowy peaks, forest, sea, lava, desert), the
/// floating islands and the icy light - set into the interface's dark wood, brass and candle glow.
/// </summary>
static class Worlds
{
    static readonly SKColor Wood = SKColor.Parse("#17110D");
    static readonly SKColor BrassLight = SKColor.Parse("#F6E2B0");
    static readonly SKColor Brass = SKColor.Parse("#C9A15A");
    static readonly SKColor BrassDeep = SKColor.Parse("#7A5A2C");
    static readonly SKColor Ice = SKColor.Parse("#DDEFFB");
    static readonly SKColor IceDeep = SKColor.Parse("#7FB2D3");

    public static void Write(string output)
    {
        var concepts = new (string Key, Action<SKCanvas> Draw)[]
        {
            ("1", ShardOfWorld), ("2", ShatteredMap), ("3", SetCrystal),
            ("4", FloatingIsland), ("5", CrystalCompass), ("6", WorldInGlass),
        };
        foreach (var (key, draw) in concepts)
            foreach (var size in new[] { 1024, 256, 64, 32, 16 })
                Art.Save(Path.Combine(output, $"w-{key}-{size}.png"), size, draw);
    }

    // ------------------------------------------------------------------ painting helpers

    static SKPaint Fill(SKColor c) => new() { IsAntialias = true, Color = c };
    static SKPaint Pen(SKColor c, float w) => new() { IsAntialias = true, Color = c, Style = SKPaintStyle.Stroke, StrokeWidth = w, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    static SKShader V(SKRect r, params SKColor[] c) => SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), c, null, SKShaderTileMode.Clamp);

    static SKPath Poly(params (float X, float Y)[] p)
    {
        var path = new SKPath(); path.MoveTo(p[0].X, p[0].Y);
        foreach (var q in p.Skip(1)) path.LineTo(q.X, q.Y);
        path.Close(); return path;
    }

    static void Shadow(SKCanvas c, SKPath p, float dy = 5, float blur = 6, byte a = 150)
    {
        using var s = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(a), ImageFilter = SKImageFilter.CreateBlur(blur, blur) };
        c.Save(); c.Translate(0, dy); c.DrawPath(p, s); c.Restore();
    }

    static void Glow(SKCanvas c, SKPoint o, float r, SKColor color, byte a)
    {
        using var p = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(o, r, [color.WithAlpha(a), color.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawCircle(o, r, p);
    }

    /// <summary>A snowy peak: lit left face, shaded right face, a snow cap.</summary>
    static void Peak(SKCanvas c, float x, float baseY, float w, float h, SKColor rock)
    {
        using var lit = Poly((x - w, baseY), (x, baseY - h), (x + w * .05f, baseY));
        using var shade = Poly((x + w * .05f, baseY), (x, baseY - h), (x + w, baseY));
        using var litPaint = Fill(rock); c.DrawPath(lit, litPaint);
        using var shadePaint = Fill(rock.WithRed((byte)(rock.Red * .62f)).WithGreen((byte)(rock.Green * .62f)).WithBlue((byte)(rock.Blue * .7f)));
        c.DrawPath(shade, shadePaint);
        using var snow = Poly((x - w * .36f, baseY - h * .62f), (x, baseY - h), (x + w * .38f, baseY - h * .6f), (x + w * .14f, baseY - h * .66f),
            (x, baseY - h * .56f), (x - w * .16f, baseY - h * .68f));
        using var snowPaint = Fill(SKColor.Parse("#F4F8FC")); c.DrawPath(snow, snowPaint);
    }

    static void Trees(SKCanvas c, SKRect area, int count, int seed, SKColor dark, SKColor light, float size)
    {
        var rnd = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var x = area.Left + (float)rnd.NextDouble() * area.Width;
            var y = area.Top + (float)rnd.NextDouble() * area.Height;
            var s = size * (.7f + (float)rnd.NextDouble() * .6f);
            using var tree = Poly((x - s * .5f, y), (x, y - s * 1.4f), (x + s * .5f, y));
            using var paint = Fill(rnd.Next(3) == 0 ? light : dark); c.DrawPath(tree, paint);
        }
    }

    static void Sea(SKCanvas c, SKRect r, float k = 1)
    {
        using var water = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#5FB7C9"), SKColor.Parse("#1E6E8E"), SKColor.Parse("#174E6B")) };
        c.DrawRect(r, water);
        using var wave = Pen(SKColor.Parse("#BFE8F0").WithAlpha(150), 1.6f * k);
        for (var y = r.Top + 8 * k; y < r.Bottom; y += 9 * k)
            for (var x = r.Left + (y % 18) - 4; x < r.Right; x += 26 * k)
                c.DrawLine(x, y, x + 9 * k, y, wave);
    }

    /// <summary>The home picture's world in small: sea, a green coast, forest, snowy range under an ice-blue sky.</summary>
    static void WorldScene(SKCanvas c, SKRect r, int seed = 3)
    {
        var k = r.Width / 160f;
        using var sky = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#EAF4FB"), SKColor.Parse("#B9D7EA")) };
        c.DrawRect(r, sky);
        Sea(c, new SKRect(r.Left, r.Top + r.Height * .62f, r.Right, r.Bottom), k);
        using var land = new SKPath();
        land.MoveTo(r.Left - 5, r.Top + r.Height * .5f);
        land.CubicTo(r.Left + r.Width * .3f, r.Top + r.Height * .42f, r.Left + r.Width * .55f, r.Top + r.Height * .5f, r.Right + 5, r.Top + r.Height * .44f);
        land.LineTo(r.Right + 5, r.Top + r.Height * .66f);
        land.CubicTo(r.Left + r.Width * .7f, r.Top + r.Height * .78f, r.Left + r.Width * .4f, r.Top + r.Height * .66f, r.Left - 5, r.Top + r.Height * .76f);
        land.Close();
        using var grass = new SKPaint { IsAntialias = true, Shader = V(new SKRect(0, r.Top + r.Height * .42f, 0, r.Top + r.Height * .78f), SKColor.Parse("#9CC26B"), SKColor.Parse("#4E7A3A")) };
        c.DrawPath(land, grass);
        using var beach = Pen(SKColor.Parse("#E9D9A6"), 2.6f * k);
        using var shore = new SKPath();
        shore.MoveTo(r.Right + 5, r.Top + r.Height * .66f);
        shore.CubicTo(r.Left + r.Width * .7f, r.Top + r.Height * .78f, r.Left + r.Width * .4f, r.Top + r.Height * .66f, r.Left - 5, r.Top + r.Height * .76f);
        c.DrawPath(shore, beach);
        var rock = SKColor.Parse("#8C98A6");
        Peak(c, r.Left + r.Width * .3f, r.Top + r.Height * .5f, r.Width * .2f, r.Height * .36f, rock);
        Peak(c, r.Left + r.Width * .58f, r.Top + r.Height * .48f, r.Width * .26f, r.Height * .44f, rock);
        Peak(c, r.Left + r.Width * .84f, r.Top + r.Height * .47f, r.Width * .17f, r.Height * .3f, rock);
        Trees(c, new SKRect(r.Left + r.Width * .05f, r.Top + r.Height * .55f, r.Right - r.Width * .1f, r.Top + r.Height * .68f), 26, seed,
            SKColor.Parse("#2F5A34"), SKColor.Parse("#5E8C45"), 7 * k);
    }

    static void LavaScene(SKCanvas c, SKRect r)
    {
        using var rock = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#3B3330"), SKColor.Parse("#1D1716")) };
        c.DrawRect(r, rock);
        using var lava = Pen(SKColor.Parse("#FF8A2B"), r.Width * .045f);
        using var core = Pen(SKColor.Parse("#FFE08A"), r.Width * .015f);
        using var crack = new SKPath();
        crack.MoveTo(r.Left, r.Top + r.Height * .3f); crack.LineTo(r.Left + r.Width * .35f, r.Top + r.Height * .45f);
        crack.LineTo(r.Left + r.Width * .5f, r.Top + r.Height * .8f); crack.MoveTo(r.Left + r.Width * .35f, r.Top + r.Height * .45f);
        crack.LineTo(r.Right, r.Top + r.Height * .35f);
        using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = r.Width * .12f, Color = SKColor.Parse("#FF6A1A").WithAlpha(90),
            ImageFilter = SKImageFilter.CreateBlur(r.Width * .04f, r.Width * .04f) };
        c.DrawPath(crack, glow); c.DrawPath(crack, lava); c.DrawPath(crack, core);
    }

    static void DesertScene(SKCanvas c, SKRect r)
    {
        using var sand = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#F1D79A"), SKColor.Parse("#C99A57")) };
        c.DrawRect(r, sand);
        using var dune = Pen(SKColor.Parse("#A9773C").WithAlpha(140), r.Width * .02f);
        for (var i = 0; i < 4; i++)
        {
            using var p = new SKPath(); var y = r.Top + r.Height * (.25f + i * .2f);
            p.MoveTo(r.Left, y); p.QuadTo(r.MidX, y - r.Height * .12f, r.Right, y + r.Height * .04f); c.DrawPath(p, dune);
        }
        using var dome = Fill(SKColor.Parse("#E8E1D3"));
        c.DrawCircle(r.Left + r.Width * .62f, r.Top + r.Height * .55f, r.Width * .1f, dome);
        using var wall = Fill(SKColor.Parse("#D2C3A5"));
        c.DrawRect(r.Left + r.Width * .5f, r.Top + r.Height * .55f, r.Width * .24f, r.Height * .14f, wall);
    }

    static void ForestScene(SKCanvas c, SKRect r, int seed)
    {
        using var ground = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#7FAE5A"), SKColor.Parse("#3D6A35")) };
        c.DrawRect(r, ground);
        using var river = Pen(SKColor.Parse("#4FA9C2"), r.Width * .07f);
        using var p = new SKPath(); p.MoveTo(r.Left, r.Top + r.Height * .2f); p.CubicTo(r.MidX, r.Top + r.Height * .3f, r.Left + r.Width * .3f, r.Top + r.Height * .8f, r.Right, r.Bottom);
        c.DrawPath(p, river);
        Trees(c, r, 34, seed, SKColor.Parse("#24492B"), SKColor.Parse("#6C9A48"), r.Width * .09f);
    }

    /// <summary>
    /// A crystal: the scene seen through it, facets lit and shaded from its centre, white glints
    /// on the edges and an icy glow around it - the shards of the home picture.
    /// </summary>
    static void Crystal(SKCanvas c, (float X, float Y)[] pts, Action<SKCanvas, SKRect> scene, float edge = 2.4f, bool glow = true)
    {
        using var shape = Poly(pts);
        var b = shape.Bounds;
        if (glow)
        {
            using var halo = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = edge * 4, Color = SKColor.Parse("#BFE6FF").WithAlpha(110),
                ImageFilter = SKImageFilter.CreateBlur(edge * 3, edge * 3), StrokeJoin = SKStrokeJoin.Round };
            c.DrawPath(shape, halo);
        }
        Shadow(c, shape, 4, 5, 120);
        c.Save(); c.ClipPath(shape, antialias: true);
        scene(c, b);
        // facets: triangles from an off-centre point to each edge, alternately frosted and clear
        var o = new SKPoint(b.Left + b.Width * .42f, b.Top + b.Height * .38f);
        for (var i = 0; i < pts.Length; i++)
        {
            var a = pts[i]; var z = pts[(i + 1) % pts.Length];
            using var facet = Poly((o.X, o.Y), a, z);
            var alpha = (byte)(i % 3 == 0 ? 70 : i % 3 == 1 ? 20 : 45);
            using var frost = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(o, new SKPoint((a.X + z.X) / 2, (a.Y + z.Y) / 2),
                [SKColors.White.WithAlpha(alpha), Ice.WithAlpha((byte)(alpha / 3))], SKShaderTileMode.Clamp) };
            c.DrawPath(facet, frost);
            using var seam = Pen(SKColors.White.WithAlpha(70), edge * .45f);
            c.DrawLine(o.X, o.Y, a.X, a.Y, seam);
        }
        // a bright diagonal glint
        using var glint = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(b.Left, b.Top), new SKPoint(b.Right, b.Bottom),
            [SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(110), SKColors.White.WithAlpha(0)], [.25f, .32f, .4f], SKShaderTileMode.Clamp) };
        c.DrawRect(b, glint);
        c.Restore();
        using var rim = Pen(SKColors.White.WithAlpha(235), edge); c.DrawPath(shape, rim);
        using var rimIce = Pen(IceDeep.WithAlpha(160), edge * .5f); c.Save(); c.Translate(edge * .5f, edge * .5f); c.DrawPath(shape, rimIce); c.Restore();
    }

    static void BrassRing(SKCanvas c, SKPoint o, float r, float w)
    {
        using var ring = new SKPath(); ring.AddCircle(o.X, o.Y, r + w / 2); ring.AddCircle(o.X, o.Y, r - w / 2); ring.FillType = SKPathFillType.EvenOdd;
        Art.CastPath(c, ring, 1.6f);
    }

    /// <summary>The board of every concept: dark wood in a double brass frame, lit by candle from below and ice from the crystal.</summary>
    static void Board(SKCanvas c, bool icy = true)
    {
        Art.Board(c);
        if (!icy) return;
        c.Save(); c.ClipRoundRect(new SKRoundRect(new SKRect(19, 19, 237, 237), 26, 26), antialias: true);
        Glow(c, new SKPoint(128, 120), 120, SKColor.Parse("#9FD4F5"), 70);
        c.Restore();
    }

    // ------------------------------------------------------------------ the six concepts

    /// <summary>1 - one tall crystal shard holding a world, rising from the board.</summary>
    static void ShardOfWorld(SKCanvas c)
    {
        Board(c);
        Crystal(c, [(118, 28), (176, 70), (190, 150), (150, 228), (88, 214), (62, 132), (80, 64)], (cv, r) => WorldScene(cv, r, 5), 3);
        // two splinters beside it
        Crystal(c, [(42, 70), (60, 58), (66, 92), (48, 100)], (cv, r) => { using var p = new SKPaint { Shader = V(r, Ice, IceDeep) }; cv.DrawRect(r, p); }, 1.6f, false);
        Crystal(c, [(204, 168), (222, 160), (226, 190), (208, 196)], (cv, r) => { using var p = new SKPaint { Shader = V(r, Ice, IceDeep) }; cv.DrawRect(r, p); }, 1.6f, false);
    }

    /// <summary>2 - a map broken into shards, each another world, as on the home picture.</summary>
    static void ShatteredMap(SKCanvas c)
    {
        Board(c);
        Crystal(c, [(40, 50), (124, 40), (118, 118), (52, 130)], (cv, r) => WorldScene(cv, r, 2), 2.2f);
        Crystal(c, [(132, 38), (216, 46), (206, 112), (126, 122)], (cv, r) => DesertScene(cv, r), 2.2f);
        Crystal(c, [(48, 140), (114, 128), (130, 214), (44, 218)], (cv, r) => ForestScene(cv, r, 9), 2.2f);
        Crystal(c, [(124, 132), (206, 122), (214, 210), (140, 218)], (cv, r) => LavaScene(cv, r), 2.2f);
    }

    /// <summary>3 - a cut gem with an island inside, held by brass claws.</summary>
    static void SetCrystal(SKCanvas c)
    {
        Board(c, icy: false);
        Glow(c, new SKPoint(128, 132), 104, SKColor.Parse("#9FD4F5"), 80);
        var o = new SKPoint(128, 132);
        var gem = Enumerable.Range(0, 8).Select(i => { var a = i * MathF.PI / 4 + MathF.PI / 8; return (o.X + MathF.Cos(a) * 84, o.Y + MathF.Sin(a) * 84); }).ToArray();
        // claws behind the stone's edge
        foreach (var i in new[] { 0, 2, 4, 6 })
        {
            var a = i * MathF.PI / 4 + MathF.PI / 4 - MathF.PI / 8;
            var tip = new SKPoint(o.X + MathF.Cos(a) * 96, o.Y + MathF.Sin(a) * 96);
            using var claw = new SKPath(); claw.AddCircle(tip.X, tip.Y, 11);
            Art.CastPath(c, claw, 1.6f);
        }
        Crystal(c, gem, (cv, r) =>
        {
            Sea(cv, r, r.Width / 160f);
            var island = new SKRect(r.Left + r.Width * .2f, r.Top + r.Height * .18f, r.Right - r.Width * .18f, r.Bottom - r.Height * .2f);
            using var landPath = new SKPath(); landPath.AddOval(new SKRect(island.Left, island.MidY - island.Height * .05f, island.Right, island.Bottom));
            using var grass = Fill(SKColor.Parse("#6E9E4C")); cv.DrawPath(landPath, grass);
            Peak(cv, island.MidX, island.MidY + island.Height * .2f, island.Width * .32f, island.Height * .62f, SKColor.Parse("#8C98A6"));
            Trees(cv, new SKRect(island.Left + 10, island.MidY + island.Height * .18f, island.Right - 10, island.Bottom - 10), 16, 4, SKColor.Parse("#2F5A34"), SKColor.Parse("#5E8C45"), 7);
        }, 3);
        foreach (var i in new[] { 0, 2, 4, 6 })
        {
            var a = i * MathF.PI / 4 + MathF.PI / 4 - MathF.PI / 8;
            var tip = new SKPoint(o.X + MathF.Cos(a) * 84, o.Y + MathF.Sin(a) * 84);
            using var prong = new SKPath(); prong.AddCircle(tip.X, tip.Y, 7);
            Art.CastPath(c, prong, 1.4f);
        }
    }

    /// <summary>4 - a floating island over clouds, seen through a round brass window.</summary>
    static void FloatingIsland(SKCanvas c)
    {
        Board(c, icy: false);
        var o = new SKPoint(128, 128);
        using var window = new SKPath(); window.AddCircle(o.X, o.Y, 92);
        c.Save(); c.ClipPath(window, antialias: true);
        var r = new SKRect(36, 36, 220, 220);
        using var sky = new SKPaint { IsAntialias = true, Shader = V(r, SKColor.Parse("#F4F9FD"), SKColor.Parse("#A9CDE6"), SKColor.Parse("#DCEAF4")) };
        c.DrawRect(r, sky);
        using var cloud = Fill(SKColors.White.WithAlpha(210));
        foreach (var (x, y, s) in new[] { (60f, 190f, 26f), (96f, 200f, 32f), (150f, 196f, 30f), (196f, 186f, 24f), (176f, 200f, 28f) }) c.DrawCircle(x, y, s, cloud);
        // the island: a green top, a rocky underside tapering down, a waterfall
        using var under = Poly((70, 128), (186, 124), (162, 152), (140, 186), (122, 170), (100, 160));
        using var rock = new SKPaint { IsAntialias = true, Shader = V(new SKRect(0, 124, 0, 186), SKColor.Parse("#8A7663"), SKColor.Parse("#4B3B2E")) };
        c.DrawPath(under, rock);
        using var top = new SKPath(); top.AddOval(new SKRect(66, 112, 190, 138));
        using var grass = Fill(SKColor.Parse("#6FA04E")); c.DrawPath(top, grass);
        Peak(c, 140, 126, 34, 62, SKColor.Parse("#8C98A6"));
        Peak(c, 108, 126, 22, 38, SKColor.Parse("#98A3AF"));
        Trees(c, new SKRect(76, 118, 184, 130), 12, 7, SKColor.Parse("#2F5A34"), SKColor.Parse("#5E8C45"), 7);
        using var fall = Pen(SKColor.Parse("#DDF2FA"), 4); c.DrawLine(170, 130, 168, 176, fall);
        // a second, small island far away
        using var small = Poly((50, 86), (78, 84), (66, 100));
        using var smallRock = Fill(SKColor.Parse("#A3B4C2")); c.DrawPath(small, smallRock);
        c.Restore();
        BrassRing(c, o, 96, 12);
        using var tick = Pen(SKColor.Parse("#2A1D12").WithAlpha(160), 1.6f);
        for (var i = 0; i < 24; i++) { var a = i * MathF.PI / 12; c.DrawLine(o.X + MathF.Cos(a) * 92, o.Y + MathF.Sin(a) * 92, o.X + MathF.Cos(a) * 99, o.Y + MathF.Sin(a) * 99, tick); }
    }

    /// <summary>5 - a brass compass rose whose heart is a glowing crystal; splinters circle it.</summary>
    static void CrystalCompass(SKCanvas c)
    {
        Board(c, icy: false);
        var o = new SKPoint(128, 128);
        Glow(c, o, 96, SKColor.Parse("#9FD4F5"), 90);
        BrassRing(c, o, 84, 8);
        using var rose = new SKPath();
        for (var i = 0; i < 8; i++)
        {
            var a = i * MathF.PI / 4 - MathF.PI / 2; var len = i % 2 == 0 ? 104f : 60f;
            rose.MoveTo(o);
            rose.LineTo(o.X + MathF.Cos(a - .35f) * 22, o.Y + MathF.Sin(a - .35f) * 22);
            rose.LineTo(o.X + MathF.Cos(a) * len, o.Y + MathF.Sin(a) * len);
            rose.LineTo(o.X + MathF.Cos(a + .35f) * 22, o.Y + MathF.Sin(a + .35f) * 22);
            rose.Close();
        }
        Art.CastPath(c, rose, 1.8f);
        Crystal(c, [(128, 92), (152, 116), (148, 150), (128, 166), (106, 148), (104, 114)], (cv, r) => WorldScene(cv, r, 8), 2.2f);
        foreach (var a in new[] { 40f, 140f, 220f, 320f })
        {
            var rad = a * MathF.PI / 180; var p = new SKPoint(o.X + MathF.Cos(rad) * 100, o.Y + MathF.Sin(rad) * 100);
            Crystal(c, [(p.X, p.Y - 9), (p.X + 6, p.Y), (p.X, p.Y + 9), (p.X - 6, p.Y)],
                (cv, r) => { using var ice = new SKPaint { Shader = V(r, Ice, IceDeep) }; cv.DrawRect(r, ice); }, 1.2f, false);
        }
    }

    /// <summary>6 - a world inside a glass orb on a brass stand.</summary>
    static void WorldInGlass(SKCanvas c)
    {
        Board(c, icy: false);
        var o = new SKPoint(128, 112);
        Glow(c, o, 104, SKColor.Parse("#9FD4F5"), 80);
        // the stand
        using var foot = new SKPath(); foot.AddRoundRect(new SKRect(70, 196, 186, 218), 8, 8);
        Art.CastPath(c, foot, 1.8f);
        using var neck = Poly((96, 196), (160, 196), (148, 176), (108, 176));
        Art.CastPath(c, neck, 1.8f);
        using var orb = new SKPath(); orb.AddCircle(o.X, o.Y, 76);
        Shadow(c, orb, 4, 6, 120);
        c.Save(); c.ClipPath(orb, antialias: true);
        WorldScene(c, new SKRect(52, 36, 204, 188), 6);
        // glass: a darker rim, a large soft reflection and a sharp highlight
        using var rimShade = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(o, 76,
            [SKColors.Transparent, SKColors.Transparent, SKColor.Parse("#0E2A3A").WithAlpha(120)], [0, .7f, 1], SKShaderTileMode.Clamp) };
        c.DrawCircle(o, 76, rimShade);
        using var reflection = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(70, 50), new SKPoint(130, 110),
            [SKColors.White.WithAlpha(150), SKColors.White.WithAlpha(0)], SKShaderTileMode.Clamp) };
        using var arc = new SKPath(); arc.AddOval(new SKRect(62, 46, 150, 110));
        c.DrawPath(arc, reflection);
        c.Restore();
        using var edge = Pen(SKColors.White.WithAlpha(200), 2.4f); c.DrawCircle(o, 76, edge);
        using var spark = Fill(SKColors.White); c.DrawCircle(92, 72, 5, spark);
        BrassRing(c, new SKPoint(128, 176), 0.1f, 0.1f);
        using var collar = new SKPath(); collar.AddOval(new SKRect(92, 170, 164, 184));
        Art.CastPath(c, collar, 1.6f);
    }
}
