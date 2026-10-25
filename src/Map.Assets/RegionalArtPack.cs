using System.Security.Cryptography;
using System.Text.Json;

namespace Map.Assets;

public sealed record RegionalArtItem(string Id, string Group, string Name, string File,
    string Sha256, double Width, double AnchorY = .9);
public sealed record RegionalArtGroup(string Id, string Name, string[] Tags);
public sealed record RegionalArtManifest(string Id, string Name, RegionalArtGroup[] Groups,
    RegionalArtItem[] Assets, string? Source = null, string? Author = null, string? License = null);
public sealed record RegionalArtImport(RegionalArtManifest Manifest,
    IReadOnlyDictionary<string, AssetSet> Sets, IReadOnlyDictionary<string, AssetRecord> Records);

/// <summary>An explicitly selected, original local pack. Opening a library or an old map never imports it.</summary>
public static class RegionalArtPack
{
    private static AssetRole? RoleForGroup(string group) => group switch
    {
        "trees" or "groves" => AssetRole.Tree,
        "mountains" => AssetRole.Peak,
        "hills" => AssetRole.Slope,
        "buildings" or "settlements" => AssetRole.House,
        "landmarks" => AssetRole.Sign,
        _ => null,
    };

    /// <summary>Migration only: identify installed built-in content, never names or owner tags.</summary>
    public static void AssignDefaultRoles(AssetLibraryDocument document, RegionalArtManifest? manifest = null)
    {
        if (manifest is null)
        {
            if (!IsAvailable) return;
            try { manifest = Load(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return; }
        }
        var variants = document.Variants.ToDictionary(v => v.Id);
        for (var i = 0; i < document.Sets.Count; i++)
        {
            var set = document.Sets[i];
            if (set.Placement?.Role is not null) continue;
            var hashes = set.VariantIds.Select(id => variants[id].Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var group = manifest.Groups.FirstOrDefault(g => hashes.IsSubsetOf(manifest.Assets.Where(a => a.Group == g.Id)
                .Select(a => a.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase)));
            if (group is null || RoleForGroup(group.Id) is not { } role) continue;
            document.Sets[i] = set with { Placement = DefaultPlacement(set.Placement, role) };
        }
    }

    private static AssetPlacementDefaults DefaultPlacement(AssetPlacementDefaults? placement, AssetRole role) =>
        placement is null ? new() { Role = role, AllowedRotations = [0], DepthSort = true }
            : placement with { Role = role };

    public static string Root => Path.Combine(AppContext.BaseDirectory, "RegionalAtlas");
    public static bool IsAvailable => File.Exists(Path.Combine(Root, "manifest.json"));
    public static RegionalArtManifest Load(string? root = null) =>
        JsonSerializer.Deserialize<RegionalArtManifest>(File.ReadAllText(Path.Combine(root ?? Root, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Не найдено описание регионального комплекта.");

    public static RegionalArtImport Import(AssetCatalog catalog, string? root = null)
    {
        root = Path.GetFullPath(root ?? Root);
        var pack = Load(root);
        if (catalog.Status == AssetLibraryStatus.Damaged) throw new InvalidDataException(catalog.Diagnostic);
        var paths = new Dictionary<string, string>();
        // Validate the complete source before touching the catalog; IDs are new content hashes.
        foreach (var item in pack.Assets)
        {
            var path = Path.GetFullPath(Path.Combine(root, item.File));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                    item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Изображение комплекта отсутствует или изменено: " + item.Name);
            paths.Add(item.Id, path);
        }
        var sets = new Dictionary<string, AssetSet>();
        var records = new Dictionary<string, AssetRecord>();
        foreach (var group in pack.Groups)
        {
            var items = pack.Assets.Where(a => a.Group == group.Id).ToArray();
            if (items.Length == 0) continue;
            var hashes = items.Select(a => a.Sha256).ToArray();
            // Preserve owner-renamed sets, favorites, variant order and placement metadata on repeat import.
            var set = catalog.Sets.FirstOrDefault(s => s.VariantIds.Count == hashes.Length
                && s.VariantIds.Select(catalog.FindVariant).OfType<AssetVariant>().Select(v => v.Sha256)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(hashes));
            if (set is null)
            {
                set = catalog.ImportSet(items.Select(a => paths[a.Id]).ToArray(), group.Name,
                    new AssetImportOptions
                    {
                        Pack = pack.Name, Tags = group.Tags,
                        Provenance = new AssetProvenance
                        {
                            // Reference-derived reconstruction; the per-item sources and editable masters
                            // are recorded in the authoring manifest. Do not claim freehand originality.
                            // A pack states its own origin; the defaults describe the original Regional Atlas.
                            Source = pack.Source ?? "Map Regional Atlas · reference-derived vector reconstruction and layered painting · content/Regional.Atlas/authoring.json · 2026-09-23",
                            Author = pack.Author ?? "Перерисовка референсов для владельца Map",
                            LicenseId = pack.License ?? "Local reference-derived artwork; source provenance in content/Regional.Atlas/authoring.json",
                            LicenseStatus = AssetLicenseStatus.UserOwned,
                        },
                    }, DateTimeOffset.UtcNow);
                // A one-file import is named after its file; the pack's group name is the set's name.
                if (set.Name != group.Name) catalog.RenameSet(set.Id, group.Name);
                foreach (var item in items)
                {
                    var variant = set.VariantIds.Select(catalog.FindVariant).Single(v => v?.Sha256 == item.Sha256)!;
                    catalog.SetVariantPresentation(variant.Id, item.Name, .5, item.AnchorY, item.Width / 256);
                }
                set = catalog.FindSet(set.Id)!;
            }
            if (set.Placement?.Role is null && RoleForGroup(group.Id) is { } role)
            {
                catalog.SetPlacementDefaults(set.Id, DefaultPlacement(set.Placement, role));
                set = catalog.FindSet(set.Id)!;
            }
            sets.Add(group.Id, set);
            foreach (var item in items) records.Add(item.Id, catalog.Find(item.Sha256)!);
        }
        return new(pack, sets, records);
    }
}
