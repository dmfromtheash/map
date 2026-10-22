using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Локальный контракт комплекта. PNG обеих ориентаций используют размеры HexTileArt.PixelSize.</summary>
public sealed record HexTileSetManifest
{
    public int Version { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<HexUserTile> Tiles { get; init; } = [];
}

public sealed record HexUserTile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "Местность";
    public HexPaintTarget Target { get; init; }
    public string File { get; init; } = "";
    public string FlatFile { get; init; } = "";
    public double? TravelCost { get; init; } = 1;
    public bool Rotatable { get; init; } = true;
    public double Overhang { get; init; }
    /// <summary>Шесть меток граней в порядке HexCoord.Neighbor(0..5); «*» совместима с любой.</summary>
    public string[] Edges { get; init; } = ["*", "*", "*", "*", "*", "*"];
}

public sealed record HexUserTileSet(string Directory, HexTileSetManifest Manifest)
{
    public string TileId(HexUserTile tile) => $"user/{Manifest.Id}/{tile.Id}";
}

/// <summary>
/// Импорт в user/&lt;id&gt; атомарным переименованием проверенного каталога. Существующие id не заменяются:
/// открытые документы и кэши всегда видят одну версию PNG. Новая редакция набора получает новый id.
/// </summary>
public static class HexUserTileSets
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, HexTileDefinition> Definitions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string, HexOrientation), SKImage> Images = new();
    private static readonly List<HexUserTileSet> Sets = [];
    public static IReadOnlyList<HexUserTileSet> All => Sets.AsReadOnly();
    public static string? Root { get; private set; }
    public static string LoadWarning { get; private set; } = "";
    private static long _revision;
    public static long Revision => Interlocked.Read(ref _revision);
    public static HexTileDefinition? Find(string id) => Definitions.GetValueOrDefault(id);
    public static SKImage? Image(string id, HexOrientation orientation) => Images.GetValueOrDefault((id, orientation));
    public static bool IsUserId(string id) => id.StartsWith("user/", StringComparison.Ordinal);
    public static double MaxOverhang { get; private set; }

    public static void Initialize(string root)
    {
        root = Path.GetFullPath(root);
        if (Root == root) return;
        // Initialization happens before any editor/cache is constructed.
        foreach (var image in Images.Values) image.Dispose();
        Images.Clear(); Definitions.Clear(); Sets.Clear(); MaxOverhang = 0; Root = root; LoadWarning = "";
        Interlocked.Increment(ref _revision);
        if (!System.IO.Directory.Exists(root)) return;
        RejectLink(root);
        foreach (var directory in System.IO.Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(directory).StartsWith('.')) continue;
            try { Register(Read(directory)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            { LoadWarning += $"Набор «{Path.GetFileName(directory)}» не загружен: {e.Message}\n"; }
        }
    }

    public static HexUserTileSet Import(string source)
    {
        if (Root is null) throw new InvalidOperationException("Хранилище гекс-комплектов не открыто.");
        var set = Read(source);
        if (Sets.Any(s => s.Manifest.Id == set.Manifest.Id)) throw new InvalidDataException("Такой набор уже установлен. Для новой редакции задайте новый Id.");
        CheckCapacity(set);
        System.IO.Directory.CreateDirectory(Root);
        RejectLink(Root);
        var destination = Path.Combine(Root, set.Manifest.Id);
        if (System.IO.Directory.Exists(destination) || System.IO.File.Exists(destination)) throw new InvalidDataException("Каталог с таким Id уже существует.");
        var staging = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            foreach (var file in set.Manifest.Tiles.SelectMany(t => new[] { t.File, t.FlatFile }).Distinct(StringComparer.OrdinalIgnoreCase))
                System.IO.File.Copy(Path.Combine(set.Directory, file), Path.Combine(staging, file));
            System.IO.File.WriteAllText(Path.Combine(staging, "set.json"), JsonSerializer.Serialize(set.Manifest, Json));
            _ = Read(staging); // Copy is checked too, before becoming visible.
            System.IO.Directory.Move(staging, destination);
            set = set with { Directory = destination };
            Register(set);
            return set;
        }
        finally { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true); }
    }

    private static void RejectLink(string path)
    {
        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Ссылки и перенаправления каталогов в комплекте не поддерживаются.");
    }

    private static bool Id(string? id) => id is { Length: > 0 and <= 48 } && Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant);
    private static string PngPath(string directory, string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 100 || file != Path.GetFileName(file)
            || file.IndexOfAny(['/', '\\', ':']) >= 0 || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PNG должен находиться рядом с set.json; пути вне комплекта запрещены.");
        var path = Path.Combine(directory, file);
        RejectLink(path);
        if (new FileInfo(path).Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("Размер PNG должен быть от 1 байта до 4 МиБ.");
        return path;
    }

    public static HexUserTileSet Read(string directory)
    {
        directory = Path.GetFullPath(directory); RejectLink(directory);
        var manifestPath = Path.Combine(directory, "set.json"); RejectLink(manifestPath);
        if (new FileInfo(manifestPath).Length > 256 * 1024) throw new InvalidDataException("set.json больше 256 КиБ.");
        var manifest = JsonSerializer.Deserialize<HexTileSetManifest>(System.IO.File.ReadAllText(manifestPath), Json)
            ?? throw new InvalidDataException("Пустой set.json.");
        if (manifest.Version != 1 || !Id(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 120
            || manifest.Tiles is not { Count: > 0 and <= 64 }) throw new InvalidDataException("Неверная версия, Id, имя или число плиток (1…64).");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long pixels = 0;
        foreach (var tile in manifest.Tiles)
        {
            if (tile is null || !Id(tile.Id) || !ids.Add(tile.Id) || string.IsNullOrWhiteSpace(tile.Name) || tile.Name.Length > 120
                || string.IsNullOrWhiteSpace(tile.Category) || tile.Category.Length > 80 || tile.Target is not (HexPaintTarget.Terrain or HexPaintTarget.Feature)
                || !double.IsFinite(tile.Overhang) || tile.Overhang is < 0 or > 2
                || tile.TravelCost is { } cost && (!double.IsFinite(cost) || cost is < .1 or > 100)
                || tile.Edges is not { Length: 6 } || tile.Edges.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 48))
                throw new InvalidDataException("Неверные свойства плитки: нужны уникальный Id, имя, 6 краёв, TravelCost 0,1…100 и Overhang 0…2.");
            foreach (var orientation in Enum.GetValues<HexOrientation>())
            {
                var path = PngPath(directory, orientation == HexOrientation.Pointy ? tile.File : tile.FlatFile);
                using var data = SKData.Create(path);
                using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("PNG не читается: " + Path.GetFileName(path));
                var size = HexTileArt.PixelSize(orientation);
                var height = size.Height + HexTileArt.OverhangPixels(orientation, tile.Overhang);
                if (codec.EncodedFormat != SKEncodedImageFormat.Png || codec.Info.Width != size.Width || codec.Info.Height != height)
                    throw new InvalidDataException($"«{Path.GetFileName(path)}»: нужен PNG {size.Width}×{height}, гекс внизу изображения.");
                pixels += (long)codec.Info.Width * codec.Info.Height;
                if (pixels * 4 > 48L * 1024 * 1024) throw new InvalidDataException("Комплект превышает 48 МиБ декодированных изображений.");
                using var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Повреждён PNG: " + Path.GetFileName(path));
            }
        }
        return new(directory, manifest);
    }

    private static void CheckCapacity(HexUserTileSet set)
    {
        if (Sets.Count >= 16 || Images.Values.Sum(i => (long)i.Width * i.Height * 4) + set.Manifest.Tiles.Sum(t =>
            Enum.GetValues<HexOrientation>().Sum(o => (long)HexTileArt.PixelSize(o).Width * (HexTileArt.PixelSize(o).Height + HexTileArt.OverhangPixels(o, t.Overhang)) * 4)) > 64L * 1024 * 1024)
            throw new InvalidDataException("Библиотека комплектов достигла предела: 16 наборов или 64 МиБ PNG в памяти.");
    }

    private static void Register(HexUserTileSet set)
    {
        if (Sets.Any(s => s.Manifest.Id == set.Manifest.Id)) throw new InvalidDataException("Повторный Id комплекта.");
        CheckCapacity(set);
        var loaded = new Dictionary<(string, HexOrientation), SKImage>();
        try
        {
            foreach (var tile in set.Manifest.Tiles)
                foreach (var orientation in Enum.GetValues<HexOrientation>())
                {
                    using var bitmap = SKBitmap.Decode(PngPath(set.Directory, orientation == HexOrientation.Pointy ? tile.File : tile.FlatFile));
                    loaded.Add((set.TileId(tile), orientation), SKImage.FromBitmap(bitmap));
                }
            foreach (var tile in set.Manifest.Tiles)
            {
                using var bitmap = SKBitmap.FromImage(loaded[(set.TileId(tile), HexOrientation.Pointy)]);
                var average = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height - HexTileArt.TilePixels / 2);
                Definitions[set.TileId(tile)] = new(set.TileId(tile), tile.Name, tile.Category, tile.Target, 1, average)
                { Overhang = tile.Overhang, Rotatable = tile.Rotatable, TravelCost = tile.TravelCost, Blends = false, Hint = "Комплект «" + set.Manifest.Name + "»" };
            }
            foreach (var pair in loaded) Images[pair.Key] = pair.Value;
            MaxOverhang = Math.Max(MaxOverhang, set.Manifest.Tiles.Max(t => t.Overhang));
            Sets.Add(set);
            Interlocked.Increment(ref _revision);
        }
        catch { foreach (var image in loaded.Values) image.Dispose(); throw; }
    }

    /// <summary>Тестовые картинки получены исключительно публичным API встроенной отрисовки.</summary>
    public static void CreateProcedural(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var tiles = new List<HexUserTile>();
        foreach (var id in new[] { "grass", "canyon", "peaks" })
        {
            var source = HexStarterTiles.Find("starter/" + id)!;
            foreach (var orientation in Enum.GetValues<HexOrientation>())
            {
                using var png = HexTileArt.Image(source.Id, 0, orientation)!.Encode(SKEncodedImageFormat.Png, 100);
                System.IO.File.WriteAllBytes(Path.Combine(directory, id + (orientation == HexOrientation.Flat ? "-flat" : "") + ".png"), png.ToArray());
            }
            tiles.Add(new() { Id = id, Name = source.Name, Category = source.Group, Target = source.Target,
                File = id + ".png", FlatFile = id + "-flat.png", Rotatable = source.Rotatable, Overhang = source.Overhang,
                TravelCost = source.TravelCost, Edges = id == "canyon" ? ["ущелье", "земля", "земля", "ущелье", "земля", "земля"] : ["земля", "земля", "земля", "земля", "земля", "земля"] });
        }
        System.IO.File.WriteAllText(Path.Combine(directory, "set.json"), JsonSerializer.Serialize(new HexTileSetManifest { Id = "procedural", Name = "Пробный комплект", Tiles = tiles }, Json));
    }

    public static void ExportTemplate(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        foreach (var orientation in Enum.GetValues<HexOrientation>())
        {
            var size = HexTileArt.PixelSize(orientation);
            using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height));
            surface.Canvas.Clear(SKColors.Transparent);
            using var path = HexTileArt.HexPath(orientation, 0, 0, size.Width, size.Height);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            surface.Canvas.DrawPath(path, paint);
            using var image = surface.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            System.IO.File.WriteAllBytes(Path.Combine(directory, orientation == HexOrientation.Pointy ? "tile.png" : "tile-flat.png"), png.ToArray());
        }
        System.IO.File.WriteAllText(Path.Combine(directory, "set.json"), JsonSerializer.Serialize(new HexTileSetManifest
        { Id = "my-set", Name = "Мой комплект", Tiles = [new() { Id = "tile", Name = "Моя плитка", File = "tile.png", FlatFile = "tile-flat.png" }] }, Json));
        System.IO.File.WriteAllText(Path.Combine(directory, "Размеры.txt"), "Шаблон комплекта Map, версия 1.\r\nДва PNG: tile.png — 222×256 (вершина сверху), tile-flat.png — 256×222 (грань сверху).\r\nБелое — маска гекса, прозрачное — за его пределами. Рисуйте собственную плитку по маске.\r\nПри Overhang добавьте сверху round(высота гекса × Overhang) строк; гекс остаётся внизу.\r\nOverhang: 0…2; Rotatable: false для плитки с верхом. TravelCost: 0,1…100 или null (непроходима).\r\nEdges: шесть меток по направлениям Neighbor 0…5: восток/северо-восток/северо-запад/запад/юго-запад/юго-восток для острого гекса; у плоского на 30° по часовой стрелке.\r\nОдинаковые метки краёв стыкуются; * допускает любой край. Категория — подпись группы. Target: 0 местность, 1 надстройка.\r\nId набора и плитки: латинские строчные буквы, цифры, дефис, подчёркивание, до 48 знаков. PNG и set.json лежат рядом.\r\nДля нового издания задайте новый Id; установленный набор не заменяется.\r\n");
    }
}
