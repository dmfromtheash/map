using System.Text.Json;
using System.Text.Json.Serialization;
using Map.Assets;

namespace Map.Presentation.Services;

/// <summary>An asset of a data mod: a picture file inside the mod's folder, its name and role (R41).</summary>
public sealed record ModAsset
{
    [JsonPropertyName("file")] public string File { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = "None";
}

/// <summary>An encounter table of a data mod: a terrain and its entries (R41).</summary>
public sealed record ModEncounters
{
    [JsonPropertyName("terrain")] public string Terrain { get; init; } = "";
    [JsonPropertyName("entries")] public List<string> Entries { get; init; } = [];
}

/// <summary>
/// A data mod (R41): a folder in «Mods» with a «mod.json» - no code, only data: assets with roles
/// (they go into the catalog as a collection named after the mod) and encounter tables (they go
/// into a hex map). The format has a version; anything unknown or unsafe is refused with a reason.
/// </summary>
public sealed record DataMod
{
    public const int Format = 1;
    public const int MaxAssets = 2000, MaxTables = 200, MaxEntries = 500;

    [JsonPropertyName("format")] public int FormatVersion { get; init; }
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("assets")] public List<ModAsset> Assets { get; init; } = [];
    [JsonPropertyName("encounters")] public List<ModEncounters> Encounters { get; init; } = [];

    [JsonIgnore] public string Folder { get; init; } = "";
    [JsonIgnore] public IReadOnlyList<string> Problems { get; init; } = [];
    [JsonIgnore] public bool IsUsable => Problems.Count == 0;
}

public static class DataMods
{
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Map", "Mods");

    private static readonly string[] Pictures = [".png", ".webp", ".jpg", ".jpeg"];

    /// <summary>Every mod folder of <paramref name="root"/>, read and checked; a broken one comes back with its problems.</summary>
    public static IReadOnlyList<DataMod> Scan(string root)
    {
        if (!Directory.Exists(root)) return [];
        var mods = new List<DataMod>();
        foreach (var folder in Directory.EnumerateDirectories(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            mods.Add(Read(folder));
        return mods;
    }

    public static DataMod Read(string folder)
    {
        var manifest = Path.Combine(folder, "mod.json");
        if (!File.Exists(manifest)) return new DataMod { Name = Path.GetFileName(folder), Folder = folder, Problems = ["нет файла mod.json"] };
        DataMod? mod;
        try
        {
            if (new FileInfo(manifest).Length > 2 * 1024 * 1024) return new DataMod { Name = Path.GetFileName(folder), Folder = folder, Problems = ["mod.json больше 2 МБ"] };
            mod = JsonSerializer.Deserialize<DataMod>(File.ReadAllText(manifest));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { return new DataMod { Name = Path.GetFileName(folder), Folder = folder, Problems = ["mod.json не читается: " + ex.Message] }; }
        if (mod is null) return new DataMod { Name = Path.GetFileName(folder), Folder = folder, Problems = ["mod.json пуст"] };

        var problems = new List<string>();
        if (mod.FormatVersion != DataMod.Format) problems.Add($"формат {mod.FormatVersion}, а Map знает формат {DataMod.Format}");
        if (string.IsNullOrWhiteSpace(mod.Id) || mod.Id.Length > 80) problems.Add("нет id");
        if (string.IsNullOrWhiteSpace(mod.Name) || mod.Name.Length > 120) problems.Add("нет названия");
        if (mod.Assets.Count > DataMod.MaxAssets) problems.Add($"ассетов больше {DataMod.MaxAssets}");
        if (mod.Encounters.Count > DataMod.MaxTables) problems.Add($"таблиц больше {DataMod.MaxTables}");
        var full = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var asset in mod.Assets)
        {
            if (!Enum.TryParse<AssetRole>(asset.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role)) problems.Add($"неизвестная роль «{asset.Role}»");
            var path = Path.GetFullPath(Path.Combine(folder, asset.File ?? ""));
            if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase)) { problems.Add($"«{asset.File}» вне папки мода"); continue; }
            if (!Pictures.Contains(Path.GetExtension(path).ToLowerInvariant())) problems.Add($"«{asset.File}» — не картинка");
            else if (!File.Exists(path)) problems.Add($"нет файла «{asset.File}»");
        }
        foreach (var table in mod.Encounters)
            if (string.IsNullOrWhiteSpace(table.Terrain) || table.Entries.Count == 0 || table.Entries.Count > DataMod.MaxEntries || table.Entries.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 300))
                problems.Add($"таблица «{table.Terrain}» пуста или слишком велика");
        return mod with { Folder = folder, Problems = problems };
    }

    /// <summary>The mod's assets into the catalog, as a collection named after the mod, each with its role. Returns how many.</summary>
    public static int ImportAssets(DataMod mod, AssetCatalog catalog, DateTimeOffset now)
    {
        if (!mod.IsUsable) throw new InvalidOperationException("Мод с ошибками не подключается.");
        var count = 0;
        foreach (var asset in mod.Assets)
        {
            var path = Path.Combine(mod.Folder, asset.File);
            var set = catalog.ImportSet([path], string.IsNullOrWhiteSpace(asset.Name) ? Path.GetFileNameWithoutExtension(path) : asset.Name,
                new AssetImportOptions { Pack = mod.Name, Tags = ["мод", mod.Id] }, now);
            // A single picture is named after its file; the mod's name for it wins.
            if (!string.IsNullOrWhiteSpace(asset.Name) && set.Name != asset.Name.Trim()) catalog.RenameSet(set.Id, asset.Name.Trim());
            var role = Enum.Parse<AssetRole>(asset.Role, ignoreCase: true);
            if (role != AssetRole.None) catalog.SetPlacementDefaults(set.Id, (catalog.FindSet(set.Id)?.Placement ?? new AssetPlacementDefaults()) with { Role = role });
            count++;
        }
        return count;
    }
}
