using System.Collections.Concurrent;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>One tile of the hex tile library: a ready painted hex the hex tool puts on the board.</summary>
/// <param name="Id">Stable id written into documents (<see cref="HexCell.Terrain"/> / <see cref="HexCell.Feature"/>).</param>
/// <param name="Name">Name shown in the palette.</param>
/// <param name="Group">Palette group heading.</param>
/// <param name="Target">Terrain tiles cover the hex; feature tiles stand on terrain.</param>
/// <param name="VariantCount">Pictures of this tile; random painting picks among them.</param>
/// <param name="Average">Colour of the tile seen from far away (overview level of detail).</param>
public sealed record HexTileDefinition(
    string Id, string Name, string Group, HexPaintTarget Target, int VariantCount, SKColor Average);

/// <summary>
/// The starter hex tile set: Map's own procedurally painted hexes, built in like the fill
/// materials, so a hex map works on a fresh install. Owner-painted tile sets are a later stage;
/// they will be referenced the same way, by id.
/// </summary>
public static class HexStarterTiles
{
    public const string Prefix = "starter/";

    public static IReadOnlyList<HexTileDefinition> All { get; } =
    [
        new(Prefix + "grass", "Равнина", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x9C, 0xB8, 0x5E)),
        new(Prefix + "farmland", "Поля", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xC4, 0xBE, 0x6A)),
        new(Prefix + "forest", "Лес", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x4E, 0x80, 0x3C)),
        new(Prefix + "pines", "Хвойный лес", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x3A, 0x66, 0x40)),
        new(Prefix + "hills", "Холмы", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xA6, 0xA8, 0x62)),
        new(Prefix + "mountains", "Горы", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x8E, 0x86, 0x7C)),
        new(Prefix + "desert", "Пустыня", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xE0, 0xC6, 0x86)),
        new(Prefix + "swamp", "Болото", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x6E, 0x7A, 0x4C)),
        new(Prefix + "snow", "Снега", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xE6, 0xEE, 0xF2)),
        new(Prefix + "shallows", "Мелководье", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0x6C, 0xB4, 0xBE)),
        new(Prefix + "sea", "Море", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0x2E, 0x62, 0x8C)),
        new(Prefix + "village", "Деревня", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0xB4, 0x6A, 0x4A)),
        new(Prefix + "city", "Город", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x9A, 0x8C, 0x80)),
        new(Prefix + "castle", "Замок", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x88, 0x84, 0x80)),
        new(Prefix + "tower", "Башня", "Места", HexPaintTarget.Feature, 2, new SKColor(0x8C, 0x7E, 0x70)),
        new(Prefix + "ruins", "Руины", "Места", HexPaintTarget.Feature, 2, new SKColor(0xA0, 0x9A, 0x8E)),
        new(Prefix + "cave", "Пещера", "Места", HexPaintTarget.Feature, 2, new SKColor(0x5A, 0x50, 0x48)),
    ];

    private static readonly Dictionary<string, HexTileDefinition> ById = All.ToDictionary(tile => tile.Id, StringComparer.Ordinal);

    public static HexTileDefinition? Find(string id) => ById.GetValueOrDefault(id);
}

/// <summary>
/// Paints and keeps the pictures of hex tiles. Every picture is painted once per tile, variant and
/// orientation at <see cref="TilePixels"/> and shared by the canvas, the palette and export.
/// </summary>
public static class HexTileArt
{
    /// <summary>The long side of a painted tile picture.</summary>
    public const int TilePixels = 256;

    private static readonly ConcurrentDictionary<(string Id, int Variant, HexOrientation Orientation), SKImage?> Images = new();

    /// <summary>The picture of a tile variant, or null for an id this build does not know.</summary>
    public static SKImage? Image(string id, int variant, HexOrientation orientation) =>
        Images.GetOrAdd((id, variant, orientation), key =>
        {
            var tile = HexStarterTiles.Find(key.Id);
            return tile is null ? null : Paint(tile, Math.Clamp(key.Variant, 0, tile.VariantCount - 1), key.Orientation);
        });

    /// <summary>Picture size for an orientation: flat hexes are wider than tall, pointy ones taller.</summary>
    public static (int Width, int Height) PixelSize(HexOrientation orientation) => orientation == HexOrientation.Flat
        ? (TilePixels, (int)Math.Round(TilePixels * HexLayout.Sqrt3 / 2))
        : ((int)Math.Round(TilePixels * HexLayout.Sqrt3 / 2), TilePixels);

    /// <summary>The hex outline of a picture of the given size.</summary>
    public static SKPath HexPath(HexOrientation orientation, float left, float top, float width, float height)
    {
        var path = new SKPath();
        var cx = left + width / 2;
        var cy = top + height / 2;
        var radius = orientation == HexOrientation.Flat ? width / 2 : height / 2;
        for (var i = 0; i < 6; i++)
        {
            var degrees = 60 * i - (orientation == HexOrientation.Pointy ? 30 : 0);
            var radians = MathF.PI / 180 * degrees;
            var x = cx + radius * MathF.Cos(radians);
            var y = cy + radius * MathF.Sin(radians);
            if (i == 0) path.MoveTo(x, y);
            else path.LineTo(x, y);
        }

        path.Close();
        return path;
    }

    private static SKImage Paint(HexTileDefinition tile, int variant, HexOrientation orientation)
    {
        var (width, height) = PixelSize(orientation);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var hex = HexPath(orientation, 0, 0, width, height);
        canvas.Save();
        canvas.ClipPath(hex, antialias: true);
        // The art is painted in a 256 × 256 box centred on the hex; the hex clips it.
        canvas.Translate((width - TilePixels) / 2f, (height - TilePixels) / 2f);
        var random = new Random(HashCode.Combine(tile.Id.GetHashCode(StringComparison.Ordinal), variant * 7919));
        var painter = new TilePainter(canvas, random);
        if (tile.Target == HexPaintTarget.Terrain)
        {
            painter.Ground(tile.Average);
        }

        switch (tile.Id[HexStarterTiles.Prefix.Length..])
        {
            case "grass": painter.Grass(tile.Average); break;
            case "farmland": painter.Farmland(); break;
            case "forest": painter.Forest(); break;
            case "pines": painter.Pines(); break;
            case "hills": painter.Hills(tile.Average); break;
            case "mountains": painter.Mountains(); break;
            case "desert": painter.Desert(tile.Average); break;
            case "swamp": painter.Swamp(); break;
            case "snow": painter.Snow(); break;
            case "shallows": painter.Waves(new SKColor(0xD8, 0xF2, 0xF0), 5); break;
            case "sea": painter.Waves(new SKColor(0xA8, 0xD0, 0xEC), 9); break;
            case "village": painter.Village(); break;
            case "city": painter.City(); break;
            case "castle": painter.Castle(); break;
            case "tower": painter.Tower(); break;
            case "ruins": painter.Ruins(); break;
            case "cave": painter.Cave(); break;
        }

        canvas.Restore();
        if (tile.Target == HexPaintTarget.Terrain)
        {
            // A soft inner rim: neighbouring hexes stay readable without a hard grid line.
            using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, Color = new SKColor(0, 0, 0, 34) };
            canvas.Save();
            canvas.ClipPath(hex, antialias: true);
            canvas.DrawPath(hex, rim);
            canvas.Restore();
        }

        return surface.Snapshot();
    }

    /// <summary>The brushwork of the starter tiles: simple shapes with a light side, a shadow side and an ink line.</summary>
    private sealed class TilePainter(SKCanvas canvas, Random random)
    {
        private static readonly SKColor Ink = new(0x2A, 0x22, 0x1C);
        private const float C = 128;

        private float Next(float min, float max) => min + (float)random.NextDouble() * (max - min);

        private static SKPaint Fill(SKColor color) => new() { IsAntialias = true, Color = color };

        private static SKPaint Line(SKColor color, float width) => new()
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = color,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };

        private static SKColor Shade(SKColor color, float factor) => new(
            (byte)Math.Clamp(color.Red * factor, 0, 255),
            (byte)Math.Clamp(color.Green * factor, 0, 255),
            (byte)Math.Clamp(color.Blue * factor, 0, 255),
            color.Alpha);

        /// <summary>Jittered points inside the hex, sorted back to front.</summary>
        private List<SKPoint> Scatter(int count, float radius, float minDistance)
        {
            var points = new List<SKPoint>();
            for (var attempt = 0; attempt < count * 30 && points.Count < count; attempt++)
            {
                var angle = Next(0, MathF.Tau);
                var distance = radius * MathF.Sqrt(Next(0, 1));
                var point = new SKPoint(C + distance * MathF.Cos(angle), C + distance * MathF.Sin(angle) * 0.86f);
                if (points.All(other => SKPoint.Distance(other, point) >= minDistance)) points.Add(point);
            }

            points.Sort((a, b) => a.Y.CompareTo(b.Y));
            return points;
        }

        public void Ground(SKColor color)
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(40, 20), new SKPoint(216, 236),
                [Shade(color, 1.12f), color, Shade(color, 0.86f)], [0, 0.55f, 1], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 256, 256, paint);
            using var noise = SKShader.CreatePerlinNoiseFractalNoise(0.045f, 0.045f, 3, random.Next(1000));
            using var grain = new SKPaint { Shader = noise, BlendMode = SKBlendMode.Overlay, Color = new SKColor(255, 255, 255, 90) };
            canvas.DrawRect(0, 0, 256, 256, grain);
            using var speck = Fill(Shade(color, 0.78f).WithAlpha(70));
            for (var i = 0; i < 70; i++) canvas.DrawCircle(Next(10, 246), Next(10, 246), Next(0.8f, 2.2f), speck);
        }

        public void Grass(SKColor color)
        {
            using var dark = Line(Shade(color, 0.62f), 2.2f);
            using var light = Line(Shade(color, 1.25f), 1.6f);
            foreach (var point in Scatter(26, 100, 14))
            {
                var h = Next(6, 10);
                canvas.DrawLine(point.X, point.Y, point.X - 3, point.Y - h, dark);
                canvas.DrawLine(point.X, point.Y, point.X + 1, point.Y - h - 2, dark);
                canvas.DrawLine(point.X, point.Y, point.X + 4, point.Y - h + 1, light);
            }

            using var flower = Fill(new SKColor(0xF4, 0xEC, 0xA0, 200));
            for (var i = 0; i < 8; i++) canvas.DrawCircle(Next(40, 216), Next(40, 216), 2.2f, flower);
        }

        public void Farmland()
        {
            canvas.Save();
            canvas.RotateDegrees(Next(-35, 35), C, C);
            SKColor[] tones = [new(0xD8, 0xC2, 0x5C), new(0x9E, 0xB0, 0x52), new(0xC8, 0xA4, 0x58), new(0xB2, 0xC0, 0x66)];
            var y = -40f;
            var index = random.Next(tones.Length);
            while (y < 300)
            {
                var band = Next(34, 58);
                using var paint = Fill(tones[index++ % tones.Length]);
                canvas.DrawRect(-60, y, 380, band, paint);
                using var furrow = Line(new SKColor(0, 0, 0, 40), 1.4f);
                for (var f = y + 6; f < y + band; f += 7) canvas.DrawLine(-60, f, 320, f, furrow);
                using var hedge = Line(new SKColor(0x4A, 0x62, 0x30, 170), 3);
                canvas.DrawLine(-60, y, 320, y, hedge);
                y += band;
            }

            canvas.Restore();
        }

        public void Forest()
        {
            foreach (var point in Scatter(11, 92, 30))
            {
                var r = Next(15, 21);
                using var trunk = Fill(new SKColor(0x6A, 0x4A, 0x30));
                canvas.DrawRect(point.X - 2.5f, point.Y, 5, r * 0.75f, trunk);
                var crown = new SKPoint(point.X, point.Y - r * 0.2f);
                var green = new SKColor((byte)(0x3E + random.Next(18)), (byte)(0x7A + random.Next(26)), (byte)(0x34 + random.Next(14)));
                using var shader = SKShader.CreateRadialGradient(
                    new SKPoint(crown.X - r * 0.35f, crown.Y - r * 0.4f), r * 1.4f,
                    [Shade(green, 1.35f), green, Shade(green, 0.62f)], [0, 0.5f, 1], SKShaderTileMode.Clamp);
                using var fill = new SKPaint { IsAntialias = true, Shader = shader };
                canvas.DrawCircle(crown, r, fill);
                using var ink = Line(Ink.WithAlpha(200), 2);
                canvas.DrawCircle(crown, r, ink);
            }
        }

        public void Pines()
        {
            foreach (var point in Scatter(13, 94, 26))
            {
                var h = Next(30, 42);
                var w = h * 0.42f;
                using var trunk = Fill(new SKColor(0x5A, 0x3E, 0x2A));
                canvas.DrawRect(point.X - 2, point.Y - 4, 4, 8, trunk);
                for (var tier = 0; tier < 3; tier++)
                {
                    var top = point.Y - h + tier * h * 0.22f;
                    var bottom = point.Y - h * 0.18f * (2 - tier);
                    var half = w * (0.55f + tier * 0.25f);
                    using var path = new SKPath();
                    path.MoveTo(point.X, top);
                    path.LineTo(point.X + half, bottom);
                    path.LineTo(point.X - half, bottom);
                    path.Close();
                    using var dark = Fill(new SKColor(0x26, 0x52, 0x34));
                    canvas.DrawPath(path, dark);
                    using var lit = new SKPath();
                    lit.MoveTo(point.X, top);
                    lit.LineTo(point.X - half, bottom);
                    lit.LineTo(point.X, bottom);
                    lit.Close();
                    using var light = Fill(new SKColor(0x46, 0x80, 0x4E));
                    canvas.DrawPath(lit, light);
                    using var ink = Line(Ink.WithAlpha(190), 1.6f);
                    canvas.DrawPath(path, ink);
                }
            }
        }

        public void Hills(SKColor color)
        {
            var count = 2 + random.Next(2);
            var points = Scatter(count, 54, 46);
            foreach (var point in points)
            {
                var w = Next(52, 72);
                var h = Next(26, 36);
                using var hill = new SKPath();
                hill.MoveTo(point.X - w / 2, point.Y + h / 2);
                hill.CubicTo(point.X - w * 0.3f, point.Y - h * 0.7f, point.X + w * 0.3f, point.Y - h * 0.7f, point.X + w / 2, point.Y + h / 2);
                hill.Close();
                using var body = Fill(Shade(color, 1.1f));
                canvas.DrawPath(hill, body);
                using var shadow = new SKPath();
                shadow.MoveTo(point.X + w * 0.05f, point.Y - h * 0.28f);
                shadow.CubicTo(point.X + w * 0.3f, point.Y - h * 0.2f, point.X + w * 0.42f, point.Y + h * 0.2f, point.X + w / 2, point.Y + h / 2);
                shadow.LineTo(point.X + w * 0.1f, point.Y + h / 2);
                shadow.Close();
                using var dark = Fill(Shade(color, 0.72f));
                canvas.DrawPath(shadow, dark);
                using var ink = Line(Ink.WithAlpha(200), 2.2f);
                using var top = new SKPath();
                top.MoveTo(point.X - w / 2, point.Y + h / 2);
                top.CubicTo(point.X - w * 0.3f, point.Y - h * 0.7f, point.X + w * 0.3f, point.Y - h * 0.7f, point.X + w / 2, point.Y + h / 2);
                canvas.DrawPath(top, ink);
            }
        }

        public void Mountains()
        {
            var peaks = new List<(SKPoint Base, float W, float H)>
            {
                (new SKPoint(C + Next(-10, 10), C + 46), Next(110, 130), Next(100, 120)),
            };
            if (random.Next(3) > 0) peaks.Insert(0, (new SKPoint(C - Next(40, 52), C + 38), Next(64, 80), Next(56, 70)));
            if (random.Next(3) > 0) peaks.Add((new SKPoint(C + Next(44, 56), C + 52), Next(60, 76), Next(50, 62)));
            foreach (var (bottom, w, h) in peaks)
            {
                var apex = new SKPoint(bottom.X + Next(-8, 8), bottom.Y - h);
                using var mountain = new SKPath();
                mountain.MoveTo(bottom.X - w / 2, bottom.Y);
                mountain.LineTo(apex.X - w * 0.12f, apex.Y + h * 0.22f);
                mountain.LineTo(apex);
                mountain.LineTo(apex.X + w * 0.16f, apex.Y + h * 0.3f);
                mountain.LineTo(bottom.X + w / 2, bottom.Y);
                mountain.Close();
                using var lit = Fill(new SKColor(0xB4, 0xAA, 0x9C));
                canvas.DrawPath(mountain, lit);
                using var shade = new SKPath();
                shade.MoveTo(apex);
                shade.LineTo(apex.X + w * 0.16f, apex.Y + h * 0.3f);
                shade.LineTo(bottom.X + w / 2, bottom.Y);
                shade.LineTo(apex.X + w * 0.04f, bottom.Y);
                shade.LineTo(apex.X - w * 0.04f, apex.Y + h * 0.5f);
                shade.Close();
                using var dark = Fill(new SKColor(0x6E, 0x66, 0x5E));
                canvas.DrawPath(shade, dark);
                using var snow = new SKPath();
                snow.MoveTo(apex);
                snow.LineTo(apex.X - w * 0.12f, apex.Y + h * 0.22f);
                snow.LineTo(apex.X - w * 0.03f, apex.Y + h * 0.17f);
                snow.LineTo(apex.X + w * 0.05f, apex.Y + h * 0.26f);
                snow.LineTo(apex.X + w * 0.16f, apex.Y + h * 0.3f);
                snow.Close();
                using var white = Fill(new SKColor(0xF4, 0xF6, 0xF8));
                canvas.DrawPath(snow, white);
                using var ink = Line(Ink.WithAlpha(220), 2.4f);
                canvas.DrawPath(mountain, ink);
                using var ridge = Line(Ink.WithAlpha(120), 1.4f);
                canvas.DrawLine(apex.X, apex.Y, apex.X - w * 0.04f, apex.Y + h * 0.5f, ridge);
            }
        }

        public void Desert(SKColor color)
        {
            for (var i = 0; i < 4; i++)
            {
                var y = 60 + i * 42 + Next(-8, 8);
                var x = Next(20, 70);
                using var dune = new SKPath();
                dune.MoveTo(x, y + 10);
                dune.CubicTo(x + 40, y - 18, x + 90, y - 14, x + 150, y + 8);
                using var shadow = new SKPath();
                shadow.MoveTo(x + 50, y - 6);
                shadow.CubicTo(x + 90, y - 12, x + 120, y, x + 150, y + 8);
                shadow.LineTo(x + 70, y + 12);
                shadow.Close();
                using var dark = Fill(Shade(color, 0.8f).WithAlpha(170));
                canvas.DrawPath(shadow, dark);
                using var ridge = Line(Shade(color, 0.6f).WithAlpha(210), 2);
                canvas.DrawPath(dune, ridge);
            }
        }

        public void Swamp()
        {
            using var water = Fill(new SKColor(0x3E, 0x5E, 0x58, 220));
            using var rim = Line(new SKColor(0x2A, 0x3A, 0x2A, 150), 1.6f);
            foreach (var point in Scatter(6, 86, 34))
            {
                var rect = SKRect.Create(point.X - Next(14, 22), point.Y - Next(5, 8), Next(28, 44), Next(10, 16));
                canvas.DrawOval(rect, water);
                canvas.DrawOval(rect, rim);
            }

            using var reed = Line(new SKColor(0x4A, 0x5C, 0x2E), 2);
            using var head = Fill(new SKColor(0x6A, 0x48, 0x2C));
            foreach (var point in Scatter(14, 96, 16))
            {
                var h = Next(10, 16);
                canvas.DrawLine(point.X, point.Y, point.X + Next(-2, 2), point.Y - h, reed);
                canvas.DrawOval(SKRect.Create(point.X - 1.6f, point.Y - h - 3, 3.2f, 6), head);
            }
        }

        public void Snow()
        {
            using var shadow = Fill(new SKColor(0xB8, 0xC8, 0xD8, 150));
            using var light = Fill(new SKColor(0xFF, 0xFF, 0xFF, 220));
            foreach (var point in Scatter(6, 84, 40))
            {
                var w = Next(34, 52);
                canvas.DrawOval(SKRect.Create(point.X - w / 2, point.Y - 6, w, 14), shadow);
                canvas.DrawOval(SKRect.Create(point.X - w / 2 - 2, point.Y - 9, w * 0.8f, 11), light);
            }

            using var sparkle = Fill(new SKColor(0xFF, 0xFF, 0xFF, 240));
            for (var i = 0; i < 12; i++) canvas.DrawCircle(Next(30, 226), Next(30, 226), 1.6f, sparkle);
        }

        public void Waves(SKColor color, int count)
        {
            using var wave = Line(color.WithAlpha(190), 2.2f);
            foreach (var point in Scatter(count, 92, 28))
            {
                var w = Next(12, 18);
                using var path = new SKPath();
                path.MoveTo(point.X - w, point.Y);
                path.QuadTo(point.X - w / 2, point.Y - 5, point.X, point.Y);
                path.QuadTo(point.X + w / 2, point.Y - 5, point.X + w, point.Y);
                canvas.DrawPath(path, wave);
            }
        }

        private void House(float x, float y, float w, float h, SKColor roof)
        {
            using var wall = Fill(new SKColor(0xE8, 0xD8, 0xB4));
            using var ink = Line(Ink, 2);
            var walls = SKRect.Create(x - w / 2, y - h, w, h);
            canvas.DrawRect(walls, wall);
            canvas.DrawRect(walls, ink);
            using var roofPath = new SKPath();
            roofPath.MoveTo(x - w / 2 - 4, y - h);
            roofPath.LineTo(x, y - h - w * 0.62f);
            roofPath.LineTo(x + w / 2 + 4, y - h);
            roofPath.Close();
            using var roofFill = Fill(roof);
            canvas.DrawPath(roofPath, roofFill);
            canvas.DrawPath(roofPath, ink);
            using var door = Fill(new SKColor(0x5A, 0x3C, 0x28));
            canvas.DrawRect(x - w * 0.12f, y - h * 0.55f, w * 0.24f, h * 0.55f, door);
        }

        private void Shadow(float x, float y, float w)
        {
            using var paint = Fill(new SKColor(0, 0, 0, 60));
            canvas.DrawOval(SKRect.Create(x - w / 2, y - 5, w, 10), paint);
        }

        public void Village()
        {
            Shadow(C, C + 34, 120);
            var roofs = new[] { new SKColor(0xB4, 0x4A, 0x34), new SKColor(0xA0, 0x5C, 0x34), new SKColor(0x8C, 0x3E, 0x30) };
            House(C - 34, C + 6, 30, 22, roofs[random.Next(3)]);
            House(C + 30, C + 2, 32, 24, roofs[random.Next(3)]);
            House(C - 2, C + 34, 34, 24, roofs[random.Next(3)]);
        }

        private void TowerShape(float x, float bottom, float w, float h, bool roof)
        {
            using var stone = Fill(new SKColor(0xB8, 0xB0, 0xA4));
            using var side = Fill(new SKColor(0x8C, 0x84, 0x7A));
            using var ink = Line(Ink, 2.2f);
            var body = SKRect.Create(x - w / 2, bottom - h, w, h);
            canvas.DrawRect(body, stone);
            canvas.DrawRect(SKRect.Create(x + w * 0.1f, bottom - h, w * 0.4f, h), side);
            canvas.DrawRect(body, ink);
            if (roof)
            {
                using var cone = new SKPath();
                cone.MoveTo(x - w / 2 - 4, bottom - h);
                cone.LineTo(x, bottom - h - w * 1.1f);
                cone.LineTo(x + w / 2 + 4, bottom - h);
                cone.Close();
                using var roofFill = Fill(new SKColor(0x4A, 0x5A, 0x8A));
                canvas.DrawPath(cone, roofFill);
                canvas.DrawPath(cone, ink);
            }
            else
            {
                for (var i = 0; i < 3; i++)
                {
                    var merlon = SKRect.Create(x - w / 2 + i * w / 2.5f, bottom - h - 7, w / 5, 7);
                    canvas.DrawRect(merlon, stone);
                    canvas.DrawRect(merlon, ink);
                }
            }

            using var window = Fill(new SKColor(0x2A, 0x22, 0x1C));
            canvas.DrawRect(x - 2.5f, bottom - h * 0.7f, 5, 9, window);
        }

        public void City()
        {
            Shadow(C, C + 46, 160);
            using var wall = Fill(new SKColor(0xA8, 0xA0, 0x94));
            using var ink = Line(Ink, 2.2f);
            var ring = SKRect.Create(C - 70, C + 14, 140, 26);
            canvas.DrawRoundRect(ring, 6, 6, wall);
            canvas.DrawRoundRect(ring, 6, 6, ink);
            House(C - 40, C + 12, 26, 22, new SKColor(0xB4, 0x4A, 0x34));
            House(C + 36, C + 10, 26, 24, new SKColor(0x8C, 0x3E, 0x30));
            House(C - 8, C - 4, 30, 28, new SKColor(0xA0, 0x5C, 0x34));
            TowerShape(C + 12 + random.Next(6), C + 14, 18, 56, roof: true);
        }

        public void Castle()
        {
            Shadow(C, C + 44, 140);
            TowerShape(C, C + 40, 70, 46, roof: false);
            TowerShape(C - 44, C + 44, 22, 66, roof: random.Next(2) == 0);
            TowerShape(C + 44, C + 44, 22, 66, roof: true);
            using var pole = Line(Ink, 2);
            canvas.DrawLine(C, C - 14, C, C - 46, pole);
            using var flag = new SKPath();
            flag.MoveTo(C, C - 46);
            flag.LineTo(C + 20, C - 40);
            flag.LineTo(C, C - 34);
            flag.Close();
            using var red = Fill(new SKColor(0xB0, 0x30, 0x2C));
            canvas.DrawPath(flag, red);
        }

        public void Tower()
        {
            Shadow(C, C + 44, 70);
            TowerShape(C + Next(-6, 6), C + 44, 30, 96, roof: random.Next(2) == 0);
        }

        public void Ruins()
        {
            Shadow(C, C + 40, 140);
            using var stone = Fill(new SKColor(0xB4, 0xAC, 0xA0));
            using var ink = Line(Ink, 2);
            for (var i = 0; i < 5; i++)
            {
                var x = C - 60 + i * 30 + Next(-4, 4);
                var h = Next(14, 52);
                using var column = new SKPath();
                column.MoveTo(x - 7, C + 38);
                column.LineTo(x - 7, C + 38 - h);
                column.LineTo(x - 2, C + 38 - h - Next(2, 8));
                column.LineTo(x + 7, C + 38 - h + Next(0, 6));
                column.LineTo(x + 7, C + 38);
                column.Close();
                canvas.DrawPath(column, stone);
                canvas.DrawPath(column, ink);
            }

            for (var i = 0; i < 6; i++)
            {
                var rect = SKRect.Create(C + Next(-70, 60), C + Next(36, 50), Next(8, 14), Next(5, 8));
                canvas.DrawRect(rect, stone);
                canvas.DrawRect(rect, ink);
            }
        }

        public void Cave()
        {
            Shadow(C, C + 40, 130);
            using var rock = new SKPath();
            rock.MoveTo(C - 64, C + 40);
            rock.CubicTo(C - 60, C - 30, C + 60, C - 40, C + 64, C + 40);
            rock.Close();
            using var stone = Fill(new SKColor(0x8C, 0x82, 0x76));
            using var ink = Line(Ink, 2.4f);
            canvas.DrawPath(rock, stone);
            canvas.DrawPath(rock, ink);
            using var mouth = new SKPath();
            mouth.MoveTo(C - 22, C + 40);
            mouth.CubicTo(C - 22, C + 2, C + 22, C + 2, C + 22, C + 40);
            mouth.Close();
            using var dark = Fill(new SKColor(0x1E, 0x18, 0x14));
            canvas.DrawPath(mouth, dark);
            canvas.DrawPath(mouth, ink);
        }
    }
}
