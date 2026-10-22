using System.Collections.Concurrent;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>What a "living terrain" tile scatters over its hexes (the HexLands / Worldographer dynamic terrain).</summary>
public enum HexDynamicIcon
{
    None,
    Trees,
    Pines,
    Hills,
    Mountains,
}

/// <summary>One tile of the hex tile library: a ready painted hex the hex tool puts on the board.</summary>
/// <param name="Id">Stable id written into documents (<see cref="HexCell.Terrain"/> / <see cref="HexCell.Feature"/>).</param>
/// <param name="Name">Name shown in the palette.</param>
/// <param name="Group">Palette group heading.</param>
/// <param name="Target">Terrain tiles cover the hex; feature tiles stand on terrain.</param>
/// <param name="VariantCount">Pictures of this tile; random painting picks among them.</param>
/// <param name="Average">Colour of the tile seen from far away (overview level of detail).</param>
public sealed record HexTileDefinition(
    string Id, string Name, string Group, HexPaintTarget Target, int VariantCount, SKColor Average)
{
    /// <summary>
    /// How far the picture rises above the top of its hex, as a share of the hex height: tall
    /// mountains and old trees hang over the row behind them (the 2.5D look of Hex Kit sets).
    /// </summary>
    public double Overhang { get; init; }

    /// <summary>The picture may be turned in 60° steps (tiles with a top, tall tiles and buildings never are).</summary>
    public bool Rotatable { get; init; } = true;

    /// <summary>
    /// Who draws over whom where two terrains meet: a higher priority spills its ground onto the
    /// neighbour of lower priority (the Wesnoth transition order).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>Water: land next to it draws a coast with surf on its side.</summary>
    public bool IsWater { get; init; }

    /// <summary>Takes part in neighbour transitions and coasts.</summary>
    public bool Blends { get; init; } = true;

    /// <summary>Time to cross one hex relative to open plains (1); null when a party on foot cannot enter it.</summary>
    public double? TravelCost { get; init; } = 1;

    /// <summary>A living terrain tile: a flat ground with icons scattered across all its neighbouring hexes.</summary>
    public HexDynamicIcon Dynamic { get; init; }

    /// <summary>Short explanation in the palette's tooltip.</summary>
    public string? Hint { get; init; }
}

/// <summary>
/// The starter hex tile set: Map's own procedurally painted hexes, built in like the fill
/// materials, so a hex map works on a fresh install. Owner-painted tile sets are a later stage;
/// they will be referenced the same way, by id. The set also carries test tiles for every
/// workshop rule: tall tiles that hang over the row behind, tiles that turn, living terrain.
/// </summary>
public static class HexStarterTiles
{
    public const string Prefix = "starter/";

    private const string Tall = "Высокие (нависают)";

    private const string Living = "Живая местность";

    public static IReadOnlyList<HexTileDefinition> All { get; } =
    [
        new(Prefix + "grass", "Равнина", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x9C, 0xB8, 0x5E)) { Priority = 50 },
        new(Prefix + "farmland", "Поля", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xC4, 0xBE, 0x6A)) { Priority = 45 },
        new(Prefix + "forest", "Лес", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x4E, 0x80, 0x3C)) { Priority = 60, TravelCost = 2 },
        new(Prefix + "pines", "Хвойный лес", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x3A, 0x66, 0x40)) { Priority = 61, TravelCost = 2 },
        new(Prefix + "jungle", "Джунгли", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x2E, 0x74, 0x3A)) { Priority = 62, TravelCost = 3 },
        new(Prefix + "hills", "Холмы", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xA6, 0xA8, 0x62)) { Priority = 70, TravelCost = 2, Rotatable = false },
        new(Prefix + "mountains", "Горы", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x8E, 0x86, 0x7C)) { Priority = 80, TravelCost = 3, Rotatable = false },
        new(Prefix + "desert", "Пустыня", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xE0, 0xC6, 0x86)) { Priority = 30, TravelCost = 1.5 },
        new(Prefix + "swamp", "Болото", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x6E, 0x7A, 0x4C)) { Priority = 20, TravelCost = 3 },
        new(Prefix + "snow", "Снега", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xE6, 0xEE, 0xF2)) { Priority = 75, TravelCost = 2 },
        new(Prefix + "canyon", "Каньон", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xC2, 0x8A, 0x5E))
        {
            Priority = 35, TravelCost = 3, Hint = "Поворотный тайл: ущелье идёт от грани к грани, поверните его клавишами [ и ].",
        },
        new(Prefix + "cliffs", "Обрыв", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xA8, 0x9A, 0x84))
        {
            Priority = 72, TravelCost = 3, Hint = "Поворотный тайл: стена обрыва вдоль одной грани.",
        },
        new(Prefix + "shallows", "Мелководье", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0x6C, 0xB4, 0xBE)) { Priority = 10, IsWater = true, TravelCost = 4 },
        new(Prefix + "sea", "Море", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0x2E, 0x62, 0x8C)) { Priority = 0, IsWater = true, TravelCost = null },
        new(Prefix + "lake", "Озеро", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0x4C, 0x8C, 0xB0)) { Priority = 5, IsWater = true, TravelCost = null },
        new(Prefix + "peaks", "Высокие горы", Tall, HexPaintTarget.Terrain, 3, new SKColor(0x84, 0x7C, 0x74))
        {
            Priority = 85, TravelCost = 4, Overhang = 0.62, Rotatable = false,
            Hint = "Высокий тайл: вершины нависают над рядом позади (2.5D).",
        },
        new(Prefix + "old-forest", "Вековой лес", Tall, HexPaintTarget.Terrain, 3, new SKColor(0x3C, 0x6E, 0x36))
        {
            Priority = 63, TravelCost = 2.5, Overhang = 0.42, Rotatable = false,
            Hint = "Высокий тайл: кроны нависают над рядом позади.",
        },
        new(Prefix + "volcano", "Вулкан", Tall, HexPaintTarget.Terrain, 2, new SKColor(0x6A, 0x56, 0x4E))
        {
            Priority = 86, TravelCost = 5, Overhang = 0.55, Rotatable = false,
        },
        new(Prefix + "live-forest", "Живой лес", Living, HexPaintTarget.Terrain, 3, new SKColor(0x6A, 0x94, 0x4C))
        {
            Priority = 58, TravelCost = 2, Dynamic = HexDynamicIcon.Trees,
            Hint = "Живая местность: деревья сами раскладываются по всей области, не повторяя сетку. «Перемешать» даёт новый узор.",
        },
        new(Prefix + "live-pines", "Живая тайга", Living, HexPaintTarget.Terrain, 3, new SKColor(0x5C, 0x86, 0x58))
        {
            Priority = 59, TravelCost = 2, Dynamic = HexDynamicIcon.Pines,
        },
        new(Prefix + "live-hills", "Живые холмы", Living, HexPaintTarget.Terrain, 3, new SKColor(0xAE, 0xB0, 0x6C))
        {
            Priority = 68, TravelCost = 2, Dynamic = HexDynamicIcon.Hills,
        },
        new(Prefix + "live-mountains", "Живой хребет", Living, HexPaintTarget.Terrain, 3, new SKColor(0xA2, 0x9C, 0x90))
        {
            Priority = 78, TravelCost = 3, Dynamic = HexDynamicIcon.Mountains,
            Hint = "Живая местность: чем больше гор рядом, тем выше хребет.",
        },
        new(Prefix + "village", "Деревня", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0xB4, 0x6A, 0x4A)) { Rotatable = false },
        new(Prefix + "city", "Город", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x9A, 0x8C, 0x80)) { Rotatable = false },
        new(Prefix + "castle", "Замок", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x88, 0x84, 0x80)) { Rotatable = false },
        new(Prefix + "port", "Порт", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x8A, 0x6A, 0x4A)) { Rotatable = false },
        new(Prefix + "camp", "Лагерь", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0xC8, 0xA8, 0x70)) { Rotatable = false },
        new(Prefix + "tower", "Башня", "Места", HexPaintTarget.Feature, 2, new SKColor(0x8C, 0x7E, 0x70)) { Rotatable = false },
        new(Prefix + "ruins", "Руины", "Места", HexPaintTarget.Feature, 2, new SKColor(0xA0, 0x9A, 0x8E)) { Rotatable = false },
        new(Prefix + "cave", "Пещера", "Места", HexPaintTarget.Feature, 2, new SKColor(0x5A, 0x50, 0x48)) { Rotatable = false },
        new(Prefix + "temple", "Храм", "Места", HexPaintTarget.Feature, 2, new SKColor(0xD8, 0xD0, 0xBC)) { Rotatable = false },
        new(Prefix + "mine", "Шахта", "Места", HexPaintTarget.Feature, 2, new SKColor(0x6A, 0x5A, 0x4A)) { Rotatable = false },
    ];

    private static readonly Dictionary<string, HexTileDefinition> ById = All.ToDictionary(tile => tile.Id, StringComparer.Ordinal);

    /// <summary>Highest overhang of any tile: chunk pictures leave this much room above their top row.</summary>
    public static double MaxOverhang => Math.Max(All.Max(tile => tile.Overhang), HexUserTileSets.MaxOverhang);

    public static HexTileDefinition? Find(string id) => ById.GetValueOrDefault(id) ?? HexUserTileSets.Find(id);
}

/// <summary>
/// Paints and keeps the pictures of hex tiles. Every picture is painted once per tile, variant and
/// orientation at <see cref="TilePixels"/> and shared by the canvas, the palette and export.
/// A tall tile's picture is taller than its hex by <see cref="HexTileDefinition.Overhang"/>; the
/// hex sits at the bottom of the picture.
/// </summary>
public static class HexTileArt
{
    /// <summary>The long side of a painted tile picture.</summary>
    public const int TilePixels = 256;

    /// <summary>The side of a living-terrain icon picture; the icon stands on its bottom middle.</summary>
    public const int IconPixels = 128;

    private static readonly ConcurrentDictionary<(string Id, int Variant, HexOrientation Orientation), SKImage?> Images = new();

    private static readonly ConcurrentDictionary<(string Id, HexOrientation Orientation), SKImage?> Grounds = new();

    private static readonly ConcurrentDictionary<(HexDynamicIcon Kind, int Variant), SKImage> Icons = new();

    /// <summary>Pictures per living-terrain icon kind.</summary>
    public const int IconVariants = 4;

    /// <summary>The picture of a tile variant, or null for an id this build does not know.</summary>
    public static SKImage? Image(string id, int variant, HexOrientation orientation) =>
        HexUserTileSets.IsUserId(id) ? HexUserTileSets.Image(id, orientation) : Images.GetOrAdd((id, variant, orientation), key =>
        {
            var tile = HexStarterTiles.Find(key.Id);
            return tile is null ? null : Paint(tile, Math.Clamp(key.Variant, 0, tile.VariantCount - 1), key.Orientation);
        });

    /// <summary>
    /// The bare ground of a terrain tile without its trees, peaks or houses, hex-sized: what a
    /// transition spills onto the neighbouring hex. Null for an unknown id.
    /// </summary>
    public static SKImage? Ground(string id, HexOrientation orientation) =>
        Grounds.GetOrAdd((id, orientation), key =>
        {
            var tile = HexStarterTiles.Find(key.Id);
            return tile is null || tile.Target != HexPaintTarget.Terrain ? null : PaintGround(tile, key.Orientation);
        });

    private static readonly ConcurrentDictionary<(SKImage Image, int Halvings), SKImage> ReducedCopies = new();

    /// <summary>
    /// A starter picture halved <paramref name="halvings"/> times (kept): what a CPU canvas samples
    /// when the picture is shown at under half its size. Safe on any thread.
    /// </summary>
    public static SKImage Reduced(SKImage image, int halvings)
    {
        ArgumentNullException.ThrowIfNull(image);
        halvings = Math.Clamp(halvings, 0, 6);
        if (halvings == 0) return image;
        return ReducedCopies.GetOrAdd((image, halvings), static key =>
        {
            var width = Math.Max(1, key.Image.Width >> key.Halvings);
            var height = Math.Max(1, key.Image.Height >> key.Halvings);
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.DrawImage(key.Image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return surface.Snapshot();
        });
    }

    /// <summary>One living-terrain icon (a tree, a pine, a hill, a peak) standing on the bottom middle of its picture.</summary>
    public static SKImage Icon(HexDynamicIcon kind, int variant) =>
        Icons.GetOrAdd((kind, ((variant % IconVariants) + IconVariants) % IconVariants), key => PaintIcon(key.Kind, key.Variant));

    /// <summary>
    /// Paints every starter picture of an orientation ahead of need (safe on any thread): the first
    /// zoom to painted hexes otherwise paints them all inside one frame (about 0.3 s).
    /// </summary>
    public static void Warm(HexOrientation orientation)
    {
        foreach (var tile in HexStarterTiles.All)
        {
            for (var variant = 0; variant < tile.VariantCount; variant++) Image(tile.Id, variant, orientation);
            Ground(tile.Id, orientation);
        }

        foreach (var kind in Enum.GetValues<HexDynamicIcon>())
        {
            if (kind == HexDynamicIcon.None) continue;
            for (var variant = 0; variant < IconVariants; variant++) Icon(kind, variant);
        }
    }

    /// <summary>Picture size for an orientation: flat hexes are wider than tall, pointy ones taller.</summary>
    public static (int Width, int Height) PixelSize(HexOrientation orientation) => orientation == HexOrientation.Flat
        ? (TilePixels, (int)Math.Round(TilePixels * HexLayout.Sqrt3 / 2))
        : ((int)Math.Round(TilePixels * HexLayout.Sqrt3 / 2), TilePixels);

    /// <summary>Extra picture height above the hex of a tile with the given overhang, in picture pixels.</summary>
    public static int OverhangPixels(HexOrientation orientation, double overhang) =>
        (int)Math.Round(PixelSize(orientation).Height * Math.Max(0, overhang));

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
        var above = OverhangPixels(orientation, tile.Overhang);
        using var surface = SKSurface.Create(new SKImageInfo(width, height + above, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(0, above);
        using var hex = HexPath(orientation, 0, 0, width, height);
        var random = new Random(StableSeed(tile.Id, variant * 7919));
        var name = tile.Id[HexStarterTiles.Prefix.Length..];
        canvas.Save();
        canvas.ClipPath(hex, antialias: true);
        // The art is painted in a 256 × 256 box centred on the hex; the hex clips it.
        canvas.Translate((width - TilePixels) / 2f, (height - TilePixels) / 2f);
        var painter = new TilePainter(canvas, random) { FlatTop = orientation == HexOrientation.Flat };
        if (tile.Target == HexPaintTarget.Terrain)
        {
            painter.Ground(GroundColour(tile));
        }

        switch (name)
        {
            case "grass": painter.Grass(tile.Average); break;
            case "farmland": painter.Farmland(); break;
            case "forest": painter.Forest(); break;
            case "pines": painter.Pines(); break;
            case "jungle": painter.Jungle(); break;
            case "hills": painter.Hills(tile.Average); break;
            case "mountains": painter.Mountains(); break;
            case "desert": painter.Desert(tile.Average); break;
            case "swamp": painter.Swamp(); break;
            case "snow": painter.Snow(); break;
            case "canyon": painter.Canyon(); break;
            case "cliffs": painter.Cliffs(); break;
            case "shallows": painter.Waves(new SKColor(0xD8, 0xF2, 0xF0), 5); break;
            case "sea": painter.Waves(new SKColor(0xA8, 0xD0, 0xEC), 9); break;
            case "lake": painter.Lake(); break;
            case "live-forest" or "live-pines" or "live-hills" or "live-mountains": painter.LivingGround(tile.Average); break;
            case "peaks" or "old-forest" or "volcano": painter.TallGround(name); break;
            case "village": painter.Village(); break;
            case "city": painter.City(); break;
            case "castle": painter.Castle(); break;
            case "port": painter.Port(); break;
            case "camp": painter.Camp(); break;
            case "tower": painter.Tower(); break;
            case "ruins": painter.Ruins(); break;
            case "cave": painter.Cave(); break;
            case "temple": painter.Temple(); break;
            case "mine": painter.Mine(); break;
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

        if (tile.Overhang > 0)
        {
            // The tall part stands on the hex and rises out of it: drawn without the hex clip.
            canvas.Save();
            canvas.Translate((width - TilePixels) / 2f, (height - TilePixels) / 2f);
            new TilePainter(canvas, new Random(StableSeed(name, variant * 104729))).Tall(name, (TilePixels - height) / 2f, above);
            canvas.Restore();
        }

        return surface.Snapshot();
    }

    private static SKColor GroundColour(HexTileDefinition tile) => tile.Id[HexStarterTiles.Prefix.Length..] switch
    {
        "forest" or "pines" or "old-forest" => new SKColor(0x5E, 0x84, 0x44),
        "jungle" => new SKColor(0x3C, 0x6E, 0x38),
        "mountains" or "peaks" => new SKColor(0x9C, 0x98, 0x7E),
        "volcano" => new SKColor(0x5A, 0x4C, 0x46),
        _ => tile.Average,
    };

    private static SKImage PaintGround(HexTileDefinition tile, HexOrientation orientation)
    {
        var (width, height) = PixelSize(orientation);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate((width - TilePixels) / 2f, (height - TilePixels) / 2f);
        var painter = new TilePainter(canvas, new Random(StableSeed(tile.Id, 31)));
        painter.Ground(GroundColour(tile));
        switch (tile.Id[HexStarterTiles.Prefix.Length..])
        {
            case "grass": painter.Grass(tile.Average); break;
            case "shallows": painter.Waves(new SKColor(0xD8, 0xF2, 0xF0), 5); break;
            case "sea": painter.Waves(new SKColor(0xA8, 0xD0, 0xEC), 9); break;
        }

        return surface.Snapshot();
    }

    /// <summary>
    /// The seed of a tile's brushwork. string.GetHashCode and HashCode.Combine change with every
    /// start of the program, so the same map was painted differently after each launch and in each
    /// export; FNV-1a of the name is the same everywhere.
    /// </summary>
    public static int StableSeed(string name, int salt)
    {
        var hash = 2166136261u;
        foreach (var c in name) hash = (hash ^ c) * 16777619u;
        return unchecked((int)(hash ^ (uint)salt * 0x9E3779B1u));
    }

    private static SKImage PaintIcon(HexDynamicIcon kind, int variant)
    {
        using var surface = SKSurface.Create(new SKImageInfo(IconPixels, IconPixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        // Icons are painted in the 256 box like tiles and scaled into their picture.
        canvas.Scale(IconPixels / 256f);
        var painter = new TilePainter(canvas, new Random(StableSeed(kind.ToString(), variant * 15485863)));
        painter.Icon(kind);
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

        /// <summary>Set for flat-top hexes: directional art runs from edge to edge of the hex it is painted for.</summary>
        public bool FlatTop { get; init; }

        public void Jungle()
        {
            using var undergrowth = Fill(new SKColor(0x24, 0x52, 0x2C, 200));
            foreach (var point in Scatter(18, 98, 16)) canvas.DrawCircle(point, Next(10, 16), undergrowth);
            foreach (var point in Scatter(16, 92, 22))
            {
                var green = new SKColor((byte)(0x2A + random.Next(20)), (byte)(0x6E + random.Next(40)), (byte)(0x2C + random.Next(16)));
                for (var leaf = 0; leaf < 5; leaf++)
                {
                    var angle = -MathF.PI / 2 + (leaf - 2) * 0.62f + Next(-0.15f, 0.15f);
                    var length = Next(16, 24);
                    using var frond = new SKPath();
                    var tip = new SKPoint(point.X + MathF.Cos(angle) * length, point.Y + MathF.Sin(angle) * length);
                    var side = new SKPoint(-MathF.Sin(angle) * 6, MathF.Cos(angle) * 6);
                    frond.MoveTo(point);
                    frond.QuadTo(point.X + (tip.X - point.X) / 2 + side.X, point.Y + (tip.Y - point.Y) / 2 + side.Y, tip.X, tip.Y);
                    frond.QuadTo(point.X + (tip.X - point.X) / 2 - side.X, point.Y + (tip.Y - point.Y) / 2 - side.Y, point.X, point.Y);
                    using var fill = Fill(leaf % 2 == 0 ? green : Shade(green, 1.3f));
                    canvas.DrawPath(frond, fill);
                    using var ink = Line(Ink.WithAlpha(120), 1.1f);
                    canvas.DrawPath(frond, ink);
                }
            }

            using var flower = Fill(new SKColor(0xE8, 0x5A, 0x6A, 220));
            for (var i = 0; i < 5; i++) canvas.DrawCircle(Next(50, 206), Next(50, 206), 2.6f, flower);
        }

        /// <summary>A gorge running across the hex from one edge to the opposite one: a tile that is meant to be turned.</summary>
        public void Canyon()
        {
            canvas.Save();
            // Flat hexes have edges at top and bottom, pointy ones at left and right.
            if (!FlatTop) canvas.RotateDegrees(90, C, C);
            using var floor = new SKPath();
            using var left = new SKPath();
            using var right = new SKPath();
            var wobble = new float[7];
            for (var i = 0; i < wobble.Length; i++) wobble[i] = Next(-10, 10);
            floor.MoveTo(C - 18 + wobble[0], -10);
            for (var i = 1; i < 7; i++) floor.LineTo(C - 20 + wobble[i], -10 + i * 46);
            for (var i = 6; i >= 0; i--) floor.LineTo(C + 20 + wobble[i] * 0.7f, -10 + i * 46);
            floor.Close();
            using var shadowSide = Fill(new SKColor(0x7A, 0x4C, 0x30));
            using var litSide = Fill(new SKColor(0xD8, 0xA4, 0x74));
            using var bed = Fill(new SKColor(0x5A, 0x3A, 0x28));
            for (var i = 0; i < 7; i++)
            {
                var y = -10 + i * 46;
                left.AddRect(SKRect.Create(C - 44 + wobble[i], y, 26, 46));
                right.AddRect(SKRect.Create(C + 18 + wobble[i] * 0.7f, y, 24, 46));
            }

            canvas.DrawPath(left, shadowSide);
            canvas.DrawPath(right, litSide);
            canvas.DrawPath(floor, bed);
            using var ink = Line(Ink.WithAlpha(200), 2);
            canvas.DrawPath(floor, ink);
            using var strata = Line(new SKColor(0x6A, 0x40, 0x28, 140), 1.4f);
            for (var y = 4f; y < 256; y += 13) canvas.DrawLine(C + 20, y, C + 40, y + 3, strata);
            canvas.Restore();
        }

        /// <summary>A cliff wall along the lower half: the high ground is up. Meant to be turned to face a neighbour.</summary>
        public void Cliffs()
        {
            using var high = Fill(new SKColor(0xB4, 0xA8, 0x8E));
            canvas.DrawRect(0, 0, 256, C + 6, high);
            using var face = new SKPath();
            face.MoveTo(-10, C + 6);
            for (var x = 0; x <= 270; x += 18) face.LineTo(x, C + 6 + Next(-6, 6));
            for (var x = 270; x >= -10; x -= 18) face.LineTo(x, C + 46 + Next(-8, 8));
            face.Close();
            using var rock = Fill(new SKColor(0x6E, 0x62, 0x56));
            canvas.DrawPath(face, rock);
            using var crack = Line(new SKColor(0x3A, 0x32, 0x2C, 200), 1.8f);
            for (var x = 8f; x < 256; x += Next(14, 24)) canvas.DrawLine(x, C + 10, x + Next(-6, 6), C + 40, crack);
            using var ink = Line(Ink.WithAlpha(220), 2.4f);
            canvas.DrawPath(face, ink);
            using var scree = Fill(new SKColor(0x8C, 0x82, 0x74));
            for (var i = 0; i < 14; i++) canvas.DrawCircle(Next(10, 246), C + Next(50, 70), Next(2, 4.5f), scree);
        }

        public void Lake()
        {
            using var deep = Fill(new SKColor(0x2E, 0x6C, 0x9A, 120));
            canvas.DrawOval(SKRect.Create(C - 70, C - 50, 140, 100), deep);
            using var ripple = Line(new SKColor(0xDC, 0xF0, 0xF8, 170), 1.8f);
            foreach (var point in Scatter(6, 70, 30))
            {
                canvas.DrawArc(SKRect.Create(point.X - 12, point.Y - 4, 24, 8), 200, 140, false, ripple);
            }
        }

        public void LivingGround(SKColor color)
        {
            // The icons come from the renderer; the ground only gets a few tufts so close-ups are not bare.
            using var tuft = Line(Shade(color, 0.7f).WithAlpha(150), 1.6f);
            foreach (var point in Scatter(14, 100, 20))
            {
                canvas.DrawLine(point.X, point.Y, point.X - 2, point.Y - 6, tuft);
                canvas.DrawLine(point.X, point.Y, point.X + 2, point.Y - 7, tuft);
            }
        }

        public void TallGround(string name)
        {
            switch (name)
            {
                case "volcano":
                {
                    using var crack = Line(new SKColor(0xF0, 0x70, 0x30, 210), 2.2f);
                    for (var i = 0; i < 6; i++)
                    {
                        var x = Next(40, 216);
                        var y = Next(140, 220);
                        canvas.DrawLine(x, y, x + Next(-20, 20), y + Next(6, 18), crack);
                    }

                    break;
                }

                default:
                {
                    using var stone = Fill(new SKColor(0, 0, 0, 40));
                    foreach (var point in Scatter(16, 100, 14)) canvas.DrawCircle(point, Next(2, 5), stone);
                    break;
                }
            }
        }

        /// <summary>The tall part of a tall tile: it stands on the hex and rises <paramref name="above"/> pixels over its top.</summary>
        public void Tall(string name, float hexTop, float above)
        {
            var sky = hexTop - above;
            switch (name)
            {
                case "peaks":
                {
                    var main = new SKPoint(C + Next(-8, 8), C + 70);
                    var peaks = new List<(SKPoint Bottom, float W, float H)>
                    {
                        (new SKPoint(C - Next(52, 64), C + 56), Next(96, 118), C + 56 - (sky + above * Next(0.35f, 0.5f))),
                        (main, Next(150, 170), main.Y - sky - Next(4, 14)),
                        (new SKPoint(C + Next(54, 66), C + 74), Next(90, 110), C + 74 - (sky + above * Next(0.45f, 0.65f))),
                    };
                    peaks.Sort((a, b) => a.Bottom.Y.CompareTo(b.Bottom.Y));
                    foreach (var (bottom, w, h) in peaks) Peak(bottom, w, h, snow: true);
                    break;
                }

                case "old-forest":
                {
                    var trees = new List<SKPoint>();
                    for (var i = 0; i < 5; i++) trees.Add(new SKPoint(C - 72 + i * 36 + Next(-6, 6), C + Next(20, 74)));
                    trees.Sort((a, b) => a.Y.CompareTo(b.Y));
                    foreach (var foot in trees)
                    {
                        var height = foot.Y - (sky + above * Next(0.05f, 0.4f));
                        TallTree(foot, height);
                    }

                    break;
                }

                case "volcano":
                {
                    var bottom = new SKPoint(C, C + 76);
                    var top = sky + above * 0.42f;
                    using var cone = new SKPath();
                    cone.MoveTo(bottom.X - 112, bottom.Y);
                    cone.LineTo(C - 28, top);
                    cone.LineTo(C + 28, top);
                    cone.LineTo(bottom.X + 112, bottom.Y);
                    cone.Close();
                    using var body = Fill(new SKColor(0x6A, 0x5A, 0x52));
                    canvas.DrawPath(cone, body);
                    using var shade = new SKPath();
                    shade.MoveTo(C + 4, top);
                    shade.LineTo(C + 28, top);
                    shade.LineTo(bottom.X + 112, bottom.Y);
                    shade.LineTo(C + 10, bottom.Y);
                    shade.Close();
                    using var dark = Fill(new SKColor(0x3E, 0x34, 0x30));
                    canvas.DrawPath(shade, dark);
                    using var lava = Line(new SKColor(0xF4, 0x6A, 0x24), 5);
                    canvas.DrawLine(C - 6, top + 4, C - 18 - Next(0, 14), bottom.Y - 30, lava);
                    canvas.DrawLine(C + 8, top + 4, C + 22 + Next(0, 10), bottom.Y - 50, lava);
                    using var glow = Fill(new SKColor(0xFF, 0xA0, 0x40));
                    canvas.DrawOval(SKRect.Create(C - 26, top - 5, 52, 12), glow);
                    using var ink = Line(Ink.WithAlpha(220), 2.6f);
                    canvas.DrawPath(cone, ink);
                    using var smoke = Fill(new SKColor(0x60, 0x58, 0x56, 150));
                    for (var i = 0; i < 6; i++)
                    {
                        var y = top - 10 - i * (above * 0.11f);
                        canvas.DrawCircle(C + Next(-10, 10) + i * 5, y, 10 + i * 3.2f, smoke);
                    }

                    break;
                }
            }
        }

        private void Peak(SKPoint bottom, float w, float h, bool snow)
        {
            var apex = new SKPoint(bottom.X + Next(-6, 6), bottom.Y - h);
            using var mountain = new SKPath();
            mountain.MoveTo(bottom.X - w / 2, bottom.Y);
            mountain.LineTo(apex.X - w * 0.14f, apex.Y + h * 0.24f);
            mountain.LineTo(apex);
            mountain.LineTo(apex.X + w * 0.15f, apex.Y + h * 0.3f);
            mountain.LineTo(bottom.X + w / 2, bottom.Y);
            mountain.Close();
            using var lit = Fill(new SKColor(0xB0, 0xA6, 0x98));
            canvas.DrawPath(mountain, lit);
            using var shade = new SKPath();
            shade.MoveTo(apex);
            shade.LineTo(apex.X + w * 0.15f, apex.Y + h * 0.3f);
            shade.LineTo(bottom.X + w / 2, bottom.Y);
            shade.LineTo(apex.X + w * 0.05f, bottom.Y);
            shade.LineTo(apex.X - w * 0.03f, apex.Y + h * 0.5f);
            shade.Close();
            using var dark = Fill(new SKColor(0x66, 0x5E, 0x58));
            canvas.DrawPath(shade, dark);
            if (snow)
            {
                using var cap = new SKPath();
                cap.MoveTo(apex);
                cap.LineTo(apex.X - w * 0.14f, apex.Y + h * 0.24f);
                cap.LineTo(apex.X - w * 0.04f, apex.Y + h * 0.18f);
                cap.LineTo(apex.X + w * 0.05f, apex.Y + h * 0.27f);
                cap.LineTo(apex.X + w * 0.15f, apex.Y + h * 0.3f);
                cap.Close();
                using var white = Fill(new SKColor(0xF4, 0xF6, 0xF8));
                canvas.DrawPath(cap, white);
            }

            using var ink = Line(Ink.WithAlpha(220), 2.4f);
            canvas.DrawPath(mountain, ink);
            using var ridge = Line(Ink.WithAlpha(110), 1.4f);
            canvas.DrawLine(apex.X, apex.Y, apex.X - w * 0.03f, apex.Y + h * 0.5f, ridge);
        }

        private void TallTree(SKPoint foot, float height)
        {
            using var trunk = Fill(new SKColor(0x5A, 0x40, 0x2A));
            canvas.DrawRect(foot.X - 4, foot.Y - height * 0.45f, 8, height * 0.45f, trunk);
            var crown = height * 0.32f;
            var green = new SKColor((byte)(0x36 + random.Next(16)), (byte)(0x6A + random.Next(24)), (byte)(0x30 + random.Next(12)));
            for (var i = 0; i < 3; i++)
            {
                var center = new SKPoint(foot.X + Next(-6, 6), foot.Y - height + crown * (0.9f + i * 0.55f));
                var r = crown * (0.75f + i * 0.12f);
                using var shader = SKShader.CreateRadialGradient(
                    new SKPoint(center.X - r * 0.35f, center.Y - r * 0.4f), r * 1.4f,
                    [Shade(green, 1.35f), green, Shade(green, 0.6f)], [0, 0.5f, 1], SKShaderTileMode.Clamp);
                using var fill = new SKPaint { IsAntialias = true, Shader = shader };
                canvas.DrawCircle(center, r, fill);
                using var ink = Line(Ink.WithAlpha(190), 2);
                canvas.DrawCircle(center, r, ink);
            }
        }

        /// <summary>One living-terrain icon standing on (128, 244) of the 256 box.</summary>
        public void Icon(HexDynamicIcon kind)
        {
            var foot = new SKPoint(C, 244);
            using var shadow = Fill(new SKColor(0, 0, 0, 55));
            switch (kind)
            {
                case HexDynamicIcon.Trees:
                {
                    canvas.DrawOval(SKRect.Create(foot.X - 60, foot.Y - 12, 120, 22), shadow);
                    using var trunk = Fill(new SKColor(0x6A, 0x4A, 0x30));
                    canvas.DrawRect(foot.X - 9, foot.Y - 70, 18, 70, trunk);
                    var green = new SKColor((byte)(0x3E + random.Next(20)), (byte)(0x78 + random.Next(30)), (byte)(0x34 + random.Next(16)));
                    var crown = new SKPoint(foot.X + Next(-6, 6), foot.Y - 130);
                    var r = Next(68, 80);
                    using var shader = SKShader.CreateRadialGradient(
                        new SKPoint(crown.X - r * 0.35f, crown.Y - r * 0.4f), r * 1.4f,
                        [Shade(green, 1.35f), green, Shade(green, 0.6f)], [0, 0.5f, 1], SKShaderTileMode.Clamp);
                    using var fill = new SKPaint { IsAntialias = true, Shader = shader };
                    canvas.DrawCircle(crown, r, fill);
                    using var ink = Line(Ink.WithAlpha(210), 6);
                    canvas.DrawCircle(crown, r, ink);
                    break;
                }

                case HexDynamicIcon.Pines:
                {
                    canvas.DrawOval(SKRect.Create(foot.X - 50, foot.Y - 10, 100, 18), shadow);
                    var h = Next(200, 228);
                    var w = h * 0.42f;
                    using var trunk = Fill(new SKColor(0x5A, 0x3E, 0x2A));
                    canvas.DrawRect(foot.X - 7, foot.Y - 26, 14, 26, trunk);
                    for (var tier = 0; tier < 3; tier++)
                    {
                        var top = foot.Y - h + tier * h * 0.22f;
                        var bottom = foot.Y - 18 - h * 0.18f * (2 - tier);
                        var half = w * (0.55f + tier * 0.25f);
                        using var path = new SKPath();
                        path.MoveTo(foot.X, top);
                        path.LineTo(foot.X + half, bottom);
                        path.LineTo(foot.X - half, bottom);
                        path.Close();
                        using var dark = Fill(new SKColor(0x26, 0x52, 0x34));
                        canvas.DrawPath(path, dark);
                        using var lit = new SKPath();
                        lit.MoveTo(foot.X, top);
                        lit.LineTo(foot.X - half, bottom);
                        lit.LineTo(foot.X, bottom);
                        lit.Close();
                        using var light = Fill(new SKColor(0x46, 0x80, 0x4E));
                        canvas.DrawPath(lit, light);
                        using var ink = Line(Ink.WithAlpha(200), 5);
                        canvas.DrawPath(path, ink);
                    }

                    break;
                }

                case HexDynamicIcon.Hills:
                {
                    var w = Next(200, 236);
                    var h = Next(80, 104);
                    var color = new SKColor(0xA6, 0xA8, 0x62);
                    using var hill = new SKPath();
                    hill.MoveTo(foot.X - w / 2, foot.Y);
                    hill.CubicTo(foot.X - w * 0.3f, foot.Y - h * 1.3f, foot.X + w * 0.3f, foot.Y - h * 1.3f, foot.X + w / 2, foot.Y);
                    hill.Close();
                    using var body = Fill(Shade(color, 1.08f));
                    canvas.DrawPath(hill, body);
                    using var side = new SKPath();
                    side.MoveTo(foot.X + w * 0.05f, foot.Y - h * 0.95f);
                    side.CubicTo(foot.X + w * 0.3f, foot.Y - h * 0.8f, foot.X + w * 0.42f, foot.Y - h * 0.3f, foot.X + w / 2, foot.Y);
                    side.LineTo(foot.X + w * 0.1f, foot.Y);
                    side.Close();
                    using var dark = Fill(Shade(color, 0.72f));
                    canvas.DrawPath(side, dark);
                    using var ink = Line(Ink.WithAlpha(210), 6);
                    using var top = new SKPath();
                    top.MoveTo(foot.X - w / 2, foot.Y);
                    top.CubicTo(foot.X - w * 0.3f, foot.Y - h * 1.3f, foot.X + w * 0.3f, foot.Y - h * 1.3f, foot.X + w / 2, foot.Y);
                    canvas.DrawPath(top, ink);
                    break;
                }

                case HexDynamicIcon.Mountains:
                {
                    using var paintShadow = Fill(new SKColor(0, 0, 0, 50));
                    canvas.DrawOval(SKRect.Create(foot.X - 100, foot.Y - 12, 200, 22), paintShadow);
                    canvas.Save();
                    canvas.Translate(0, 0);
                    // A peak drawn with the tile brush but filling the whole icon.
                    var apex = new SKPoint(foot.X + Next(-10, 10), foot.Y - Next(200, 232));
                    var w = Next(208, 240);
                    var h = foot.Y - apex.Y;
                    using var mountain = new SKPath();
                    mountain.MoveTo(foot.X - w / 2, foot.Y);
                    mountain.LineTo(apex.X - w * 0.14f, apex.Y + h * 0.24f);
                    mountain.LineTo(apex);
                    mountain.LineTo(apex.X + w * 0.15f, apex.Y + h * 0.3f);
                    mountain.LineTo(foot.X + w / 2, foot.Y);
                    mountain.Close();
                    using var lit = Fill(new SKColor(0xB4, 0xAA, 0x9C));
                    canvas.DrawPath(mountain, lit);
                    using var shade = new SKPath();
                    shade.MoveTo(apex);
                    shade.LineTo(apex.X + w * 0.15f, apex.Y + h * 0.3f);
                    shade.LineTo(foot.X + w / 2, foot.Y);
                    shade.LineTo(apex.X + w * 0.05f, foot.Y);
                    shade.LineTo(apex.X - w * 0.03f, apex.Y + h * 0.5f);
                    shade.Close();
                    using var dark = Fill(new SKColor(0x6E, 0x66, 0x5E));
                    canvas.DrawPath(shade, dark);
                    using var cap = new SKPath();
                    cap.MoveTo(apex);
                    cap.LineTo(apex.X - w * 0.14f, apex.Y + h * 0.24f);
                    cap.LineTo(apex.X - w * 0.04f, apex.Y + h * 0.18f);
                    cap.LineTo(apex.X + w * 0.05f, apex.Y + h * 0.27f);
                    cap.LineTo(apex.X + w * 0.15f, apex.Y + h * 0.3f);
                    cap.Close();
                    using var white = Fill(new SKColor(0xF4, 0xF6, 0xF8));
                    canvas.DrawPath(cap, white);
                    using var ink = Line(Ink.WithAlpha(220), 6);
                    canvas.DrawPath(mountain, ink);
                    canvas.Restore();
                    break;
                }
            }
        }

        public void Port()
        {
            using var water = Fill(new SKColor(0x4C, 0x8C, 0xB0, 200));
            canvas.DrawOval(SKRect.Create(C - 10, C + 6, 100, 50), water);
            using var plank = Fill(new SKColor(0x8A, 0x62, 0x3E));
            using var ink = Line(Ink, 2);
            var pier = SKRect.Create(C + 4, C + 2, 70, 12);
            canvas.DrawRect(pier, plank);
            canvas.DrawRect(pier, ink);
            for (var x = C + 10; x < C + 74; x += 14) canvas.DrawLine(x, C + 14, x, C + 26, ink);
            using var hull = new SKPath();
            hull.MoveTo(C + 30, C + 34);
            hull.LineTo(C + 74, C + 34);
            hull.LineTo(C + 64, C + 46);
            hull.LineTo(C + 38, C + 46);
            hull.Close();
            using var wood = Fill(new SKColor(0x6A, 0x46, 0x2C));
            canvas.DrawPath(hull, wood);
            canvas.DrawPath(hull, ink);
            canvas.DrawLine(C + 52, C + 34, C + 52, C - 2, ink);
            using var sail = new SKPath();
            sail.MoveTo(C + 52, C);
            sail.LineTo(C + 70, C + 28);
            sail.LineTo(C + 52, C + 28);
            sail.Close();
            using var cloth = Fill(new SKColor(0xF0, 0xE6, 0xCC));
            canvas.DrawPath(sail, cloth);
            canvas.DrawPath(sail, ink);
            House(C - 40, C + 16, 30, 22, new SKColor(0xA0, 0x5C, 0x34));
            House(C - 6, C - 6, 26, 20, new SKColor(0x8C, 0x3E, 0x30));
        }

        public void Camp()
        {
            Shadow(C, C + 36, 120);
            foreach (var (x, y, w) in new[] { (C - 34f, C + 26f, 44f), (C + 30f, C + 18f, 40f) })
            {
                using var tent = new SKPath();
                tent.MoveTo(x - w / 2, y);
                tent.LineTo(x, y - w * 0.8f);
                tent.LineTo(x + w / 2, y);
                tent.Close();
                using var canvasFill = Fill(new SKColor(0xD8, 0xC0, 0x8C));
                canvas.DrawPath(tent, canvasFill);
                using var door = new SKPath();
                door.MoveTo(x - 6, y);
                door.LineTo(x, y - w * 0.45f);
                door.LineTo(x + 6, y);
                door.Close();
                using var dark = Fill(new SKColor(0x5A, 0x40, 0x28));
                canvas.DrawPath(door, dark);
                using var ink = Line(Ink, 2);
                canvas.DrawPath(tent, ink);
            }

            using var logs = Line(new SKColor(0x5A, 0x3C, 0x24), 4);
            canvas.DrawLine(C - 10, C + 46, C + 10, C + 40, logs);
            canvas.DrawLine(C - 10, C + 40, C + 10, C + 46, logs);
            using var fire = Fill(new SKColor(0xF4, 0x8A, 0x2C));
            using var flame = new SKPath();
            flame.MoveTo(C - 7, C + 42);
            flame.QuadTo(C - 6, C + 28, C, C + 22);
            flame.QuadTo(C + 6, C + 28, C + 7, C + 42);
            flame.Close();
            canvas.DrawPath(flame, fire);
        }

        public void Temple()
        {
            Shadow(C, C + 44, 140);
            using var stone = Fill(new SKColor(0xE6, 0xDE, 0xCA));
            using var side = Fill(new SKColor(0xB8, 0xAE, 0x9A));
            using var ink = Line(Ink, 2.2f);
            var step = SKRect.Create(C - 62, C + 30, 124, 12);
            canvas.DrawRect(step, side);
            canvas.DrawRect(step, ink);
            for (var i = 0; i < 5; i++)
            {
                var column = SKRect.Create(C - 52 + i * 24, C - 22, 10, 52);
                canvas.DrawRect(column, stone);
                canvas.DrawRect(column, ink);
            }

            var beam = SKRect.Create(C - 60, C - 32, 120, 10);
            canvas.DrawRect(beam, stone);
            canvas.DrawRect(beam, ink);
            using var roof = new SKPath();
            roof.MoveTo(C - 66, C - 32);
            roof.LineTo(C, C - 62);
            roof.LineTo(C + 66, C - 32);
            roof.Close();
            canvas.DrawPath(roof, stone);
            canvas.DrawPath(roof, ink);
        }

        public void Mine()
        {
            Shadow(C, C + 40, 130);
            using var heap = new SKPath();
            heap.MoveTo(C - 70, C + 40);
            heap.CubicTo(C - 50, C - 20, C + 50, C - 26, C + 70, C + 40);
            heap.Close();
            using var earth = Fill(new SKColor(0x8A, 0x76, 0x60));
            using var ink = Line(Ink, 2.4f);
            canvas.DrawPath(heap, earth);
            canvas.DrawPath(heap, ink);
            var mouth = SKRect.Create(C - 20, C + 4, 40, 36);
            using var dark = Fill(new SKColor(0x1A, 0x16, 0x12));
            canvas.DrawRect(mouth, dark);
            using var beam = Line(new SKColor(0x7A, 0x52, 0x30), 6);
            canvas.DrawLine(C - 22, C + 40, C - 22, C + 2, beam);
            canvas.DrawLine(C + 22, C + 40, C + 22, C + 2, beam);
            canvas.DrawLine(C - 28, C + 2, C + 28, C + 2, beam);
            using var rail = Line(new SKColor(0x5A, 0x5A, 0x5A), 2);
            canvas.DrawLine(C - 8, C + 40, C - 30, C + 70, rail);
            canvas.DrawLine(C + 8, C + 40, C + 30, C + 70, rail);
            using var cart = Fill(new SKColor(0x6A, 0x5A, 0x50));
            canvas.DrawRect(SKRect.Create(C + 10, C + 50, 26, 14), cart);
            canvas.DrawRect(SKRect.Create(C + 10, C + 50, 26, 14), ink);
        }
    }
}
