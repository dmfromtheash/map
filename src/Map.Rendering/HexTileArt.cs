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

    /// <summary>
    /// The picture may be turned in 60° steps (tiles with a top, tall tiles, standing trees and
    /// buildings never are: their light comes from the north-west).
    /// </summary>
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
        new(Prefix + "grass", "Равнина", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xAA, 0xB5, 0x84)) { Priority = 50 },
        new(Prefix + "farmland", "Поля", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xB0, 0xA8, 0x7B)) { Priority = 45 },
        new(Prefix + "forest", "Лес", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x7D, 0x8D, 0x67)) { Priority = 60, TravelCost = 2, Rotatable = false },
        new(Prefix + "pines", "Хвойный лес", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x80, 0x8E, 0x6D)) { Priority = 61, TravelCost = 2, Rotatable = false },
        new(Prefix + "jungle", "Джунгли", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x63, 0x7A, 0x56)) { Priority = 62, TravelCost = 3, Rotatable = false },
        new(Prefix + "hills", "Холмы", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xA1, 0xAD, 0x7E)) { Priority = 70, TravelCost = 2, Rotatable = false },
        new(Prefix + "mountains", "Горы", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xC4, 0xD1, 0xD9)) { Priority = 80, TravelCost = 3, Rotatable = false },
        new(Prefix + "desert", "Пустыня", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xD6, 0xC4, 0x9D)) { Priority = 30, TravelCost = 1.5 },
        new(Prefix + "swamp", "Болото", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0x83, 0x8E, 0x78)) { Priority = 20, TravelCost = 3 },
        new(Prefix + "snow", "Снега", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xE0, 0xE9, 0xED)) { Priority = 75, TravelCost = 2 },
        new(Prefix + "canyon", "Каньон", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xC6, 0xA4, 0x83))
        {
            Priority = 35, TravelCost = 3, Hint = "Поворотный тайл: ущелье идёт от грани к грани, поверните его клавишами [ и ].",
        },
        new(Prefix + "cliffs", "Обрыв", "Местность", HexPaintTarget.Terrain, 3, new SKColor(0xA0, 0xA4, 0x83))
        {
            Priority = 72, TravelCost = 3, Hint = "Поворотный тайл: стена обрыва вдоль одной грани.",
        },
        new(Prefix + "shallows", "Мелководье", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0xB5, 0xCD, 0xD3)) { Priority = 10, IsWater = true, TravelCost = 4 },
        new(Prefix + "sea", "Море", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0x89, 0xA4, 0xB8)) { Priority = 0, IsWater = true, TravelCost = null },
        new(Prefix + "lake", "Озеро", "Вода", HexPaintTarget.Terrain, 3, new SKColor(0x9C, 0xB4, 0xC5)) { Priority = 5, IsWater = true, TravelCost = null },
        new(Prefix + "peaks", "Высокие горы", Tall, HexPaintTarget.Terrain, 3, new SKColor(0x91, 0xA3, 0xB2))
        {
            Priority = 85, TravelCost = 4, Overhang = 0.62, Rotatable = false,
            Hint = "Высокий тайл: вершины нависают над рядом позади (2.5D).",
        },
        new(Prefix + "old-forest", "Вековой лес", Tall, HexPaintTarget.Terrain, 3, new SKColor(0x7D, 0x8E, 0x67))
        {
            Priority = 63, TravelCost = 2.5, Overhang = 0.42, Rotatable = false,
            Hint = "Высокий тайл: кроны нависают над рядом позади.",
        },
        new(Prefix + "volcano", "Вулкан", Tall, HexPaintTarget.Terrain, 2, new SKColor(0x6D, 0x62, 0x60))
        {
            Priority = 86, TravelCost = 5, Overhang = 0.55, Rotatable = false,
        },
        new(Prefix + "live-forest", "Живой лес", Living, HexPaintTarget.Terrain, 3, new SKColor(0xAA, 0xB5, 0x83))
        {
            Priority = 58, TravelCost = 2, Dynamic = HexDynamicIcon.Trees,
            Hint = "Живая местность: деревья сами раскладываются по всей области, не повторяя сетку. «Перемешать» даёт новый узор.",
        },
        new(Prefix + "live-pines", "Живая тайга", Living, HexPaintTarget.Terrain, 3, new SKColor(0xA3, 0xAE, 0x7B))
        {
            Priority = 59, TravelCost = 2, Dynamic = HexDynamicIcon.Pines,
        },
        new(Prefix + "live-hills", "Живые холмы", Living, HexPaintTarget.Terrain, 3, new SKColor(0xAA, 0xB5, 0x84))
        {
            Priority = 68, TravelCost = 2, Dynamic = HexDynamicIcon.Hills,
        },
        new(Prefix + "live-mountains", "Живой хребет", Living, HexPaintTarget.Terrain, 3, new SKColor(0xE8, 0xF0, 0xF4))
        {
            Priority = 78, TravelCost = 3, Dynamic = HexDynamicIcon.Mountains,
            Hint = "Живая местность: чем больше гор рядом, тем выше хребет.",
        },
        new(Prefix + "village", "Деревня", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x8D, 0x77, 0x69)) { Rotatable = false },
        new(Prefix + "city", "Город", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x79, 0x70, 0x69)) { Rotatable = false },
        new(Prefix + "castle", "Замок", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x80, 0x7F, 0x7B)) { Rotatable = false },
        new(Prefix + "port", "Порт", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x7A, 0x7A, 0x7A)) { Rotatable = false },
        new(Prefix + "camp", "Лагерь", "Поселения", HexPaintTarget.Feature, 2, new SKColor(0x82, 0x80, 0x75)) { Rotatable = false },
        new(Prefix + "tower", "Башня", "Места", HexPaintTarget.Feature, 2, new SKColor(0x6D, 0x6D, 0x6B)) { Rotatable = false },
        new(Prefix + "ruins", "Руины", "Места", HexPaintTarget.Feature, 2, new SKColor(0x70, 0x72, 0x70)) { Rotatable = false },
        new(Prefix + "cave", "Пещера", "Места", HexPaintTarget.Feature, 2, new SKColor(0x79, 0x76, 0x70)) { Rotatable = false },
        new(Prefix + "temple", "Храм", "Места", HexPaintTarget.Feature, 2, new SKColor(0x8E, 0x8A, 0x7E)) { Rotatable = false },
        new(Prefix + "mine", "Шахта", "Места", HexPaintTarget.Feature, 2, new SKColor(0x7D, 0x6E, 0x58)) { Rotatable = false },
    ];

    private static readonly Dictionary<string, HexTileDefinition> ById = All.ToDictionary(tile => tile.Id, StringComparer.Ordinal);

    /// <summary>Highest overhang of any tile: chunk pictures leave this much room above their top row.</summary>
    public static double MaxOverhang => Math.Max(All.Max(tile => tile.Overhang), HexUserTileSets.MaxOverhang);

    public static HexTileDefinition? Find(string id) => ById.GetValueOrDefault(id) ?? HexUserTileSets.Find(id);
}

/// <summary>
/// Keeps the pictures of hex tiles. The art is painted once, outside the program, in the manner of
/// the owner's mountains (tools/HexArt: pen contour, flat brushy tones, light from the north-west,
/// paper grain) and built into the assembly as one square picture per tile variant (the 256 box a
/// hex is cut from); a tall tile adds a picture of what rises over the row behind. Pictures are
/// cut to a hex per orientation at <see cref="TilePixels"/> and shared by the canvas, the palette
/// and export. A tall tile's picture is taller than its hex by <see cref="HexTileDefinition.Overhang"/>;
/// the hex sits at the bottom of the picture.
/// </summary>
public static class HexTileArt
{
    /// <summary>The long side of a painted tile picture.</summary>
    public const int TilePixels = 256;

    /// <summary>The side of a living-terrain icon picture; the icon stands on its bottom middle.</summary>
    public const int IconPixels = 128;

    /// <summary>Box pixels of a tall picture above its 256 box.</summary>
    private const int TallRoom = 128;

    private const string ResourcePrefix = "Map.Rendering.HexArt.";

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
    /// Prepares every starter picture of an orientation ahead of need (safe on any thread): the
    /// first zoom to painted hexes otherwise decodes and cuts them all inside one frame.
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

    /// <summary>
    /// One built-in art picture decoded into a raster image, or null when the build carries no
    /// such picture. Not kept: each picture is cut once per orientation and the cut is kept, so
    /// keeping the source as well only held 30 MB more.
    /// </summary>
    private static SKImage? Load(string file)
    {
        using var stream = typeof(HexTileArt).Assembly.GetManifestResourceStream(ResourcePrefix + file);
        if (stream is null) return null;
        using var codec = SKCodec.Create(stream);
        if (codec is null) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(codec, info);
        if (bitmap is null) return null;
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    private static string NameOf(HexTileDefinition tile) => tile.Id[HexStarterTiles.Prefix.Length..];

    private static SKImage Paint(HexTileDefinition tile, int variant, HexOrientation orientation)
    {
        var (width, height) = PixelSize(orientation);
        var above = OverhangPixels(orientation, tile.Overhang);
        using var surface = SKSurface.Create(new SKImageInfo(width, height + above, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(0, above);
        using var hex = HexPath(orientation, 0, 0, width, height);
        var name = NameOf(tile);
        using var paint = new SKPaint { IsAntialias = true };
        using var art = Load($"{name}_{variant}.webp");
        canvas.Save();
        canvas.ClipPath(hex, antialias: true);
        // The art is a 256 x 256 box centred on the hex; the hex cuts it.
        canvas.Translate((width - TilePixels) / 2f, (height - TilePixels) / 2f);
        if (art is not null)
        {
            // A gorge runs from edge to edge: flat hexes have edges at top and bottom, pointy ones
            // at left and right, so the pointy picture is turned a quarter.
            if (name == "canyon" && orientation == HexOrientation.Pointy) canvas.RotateDegrees(90, TilePixels / 2f, TilePixels / 2f);
            canvas.DrawImage(art, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        }

        canvas.Restore();
        if (tile.Target == HexPaintTarget.Terrain)
        {
            // A soft inner rim in the pen's colour: neighbouring hexes stay readable without a hard grid line.
            using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, Color = new SKColor(0x1F, 0x2A, 0x36, 30) };
            canvas.Save();
            canvas.ClipPath(hex, antialias: true);
            canvas.DrawPath(hex, rim);
            canvas.Restore();
        }

        using var tall = tile.Overhang > 0 ? Load($"{name}_{variant}_tall.webp") : null;
        if (tall is not null)
        {
            // The tall part stands on the hex and rises out of it: drawn without the hex clip.
            canvas.Save();
            canvas.Translate((width - TilePixels) / 2f, (height - TilePixels) / 2f - TallRoom);
            canvas.DrawImage(tall, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
            canvas.Restore();
        }

        return surface.Snapshot();
    }

    private static SKImage PaintGround(HexTileDefinition tile, HexOrientation orientation)
    {
        var (width, height) = PixelSize(orientation);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate((width - TilePixels) / 2f, (height - TilePixels) / 2f);
        using var ground = Load($"{NameOf(tile)}_ground.webp");
        if (ground is not null)
        {
            canvas.DrawImage(ground, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        else
        {
            canvas.Clear(tile.Average);
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
        using var art = Load($"icon_{kind}_{variant}.webp");
        if (art is not null)
        {
            canvas.DrawImage(art, new SKRect(0, 0, IconPixels, IconPixels), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        return surface.Snapshot();
    }
}
