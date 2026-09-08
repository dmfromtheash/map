namespace Map.Assets;

/// <summary>Original, individually authored image-generation outputs; no runtime artwork generation.</summary>
public static class AtlasArtPack
{
    public const string ProvenanceSource = "map-original-ai-atlas-20260908";
    public sealed record Artwork(string StyleId, string Style, string Subject, string Category, string Title, string Sha256);
    public static IReadOnlyList<Artwork> Artworks { get; } = Array.AsReadOnly<Artwork>([
        new("ink_atlas", "Атлас пером", "mountain", "Горы", "Светлый скальный хребет", "4ce43dd4159da5f6618c51737e3d0dd33b19d81662337b8a6d7ea1335bac5495"),
        new("ink_atlas", "Атлас пером", "house", "Дома", "Дом с черепичной крышей", "d8336de2812a17bc0c534a603a452a44e55fd9578db30a1f7e873cc0c95a5c39"),
        new("ink_atlas", "Атлас пером", "tree", "Деревья", "Дуб атласный", "58e09e21bfb722d40b6f777cf33c9330058afd7983e963a9038ca7ec41544b29"),
        new("drawn_map", "Рисованная карта", "mountain", "Горы", "Длинная горная гряда", "9c1a3dc9787d47f8095bcbf9d6f394e60acf0505fa369f24e71c6df499a38d93"),
        new("drawn_map", "Рисованная карта", "house", "Дома", "Каменный дом", "908405b0ee5743f0ea4b7fb71b8ff673a64d4ae5c5d605d8d3ec51508d45a3d3"),
        new("drawn_map", "Рисованная карта", "tree", "Деревья", "Раскидистый дуб", "038c0384eec2b98a5cd20961a197dbe9e2f47f863aa834ac3cd2d78cc715c002"),
        new("pixel_rpg", "Изометрия RPG", "mountain", "Горы", "Снежная вершина", "57f365d1f73bf075e12905204a4bda781ac3dd5d81fed11f9a188049ce3c8aca"),
        new("pixel_rpg", "Изометрия RPG", "house", "Дома", "Фахверковый дом", "ec0168c833eebd586667d2b6289fb2dc604e93635a1ce9e65952787a82da6b78"),
        new("pixel_rpg", "Изометрия RPG", "tree", "Деревья", "Изумрудный дуб", "3a2f04486bf1999b61e7ec91ddab9530c7093b183807a304f3fc0d52e28c8f7a"),
    ]);

    public static void EnsureInstalled(AssetCatalog catalog, DateTimeOffset installedUtc)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Status == AssetLibraryStatus.Damaged) return;
        foreach (var art in Artworks)
        {
            // Preserve any user edits to titles, tags, collections and favourites on subsequent starts.
            if (catalog.Find(art.Sha256) is not null) continue;
            var directory = Path.Combine(catalog.Root, "atlas-art-source", art.StyleId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, art.Title + ".png");
            using (var resource = typeof(AtlasArtPack).Assembly.GetManifestResourceStream(
                $"Map.Assets.AtlasArt.{art.StyleId}.{art.Subject}.png")
                ?? throw new InvalidOperationException($"Отсутствует изображение: {art.Title}"))
            using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                resource.CopyTo(target);
            var pack = art.Style + " / " + art.Category;
            catalog.Import(path, new AssetImportOptions
            {
                Pack = pack,
                Tags = ["Стиль/" + art.Style, "Тема/" + art.Category, "Карта/Региональная", art.Subject],
                Provenance = new AssetProvenance
                {
                    Source = ProvenanceSource, Author = "Map · OpenAI image generation",
                    LicenseId = "Original AI-generated artwork for Map",
                    LicenseStatus = AssetLicenseStatus.UserOwned,
                },
            }, installedUtc);
            catalog.NestBuiltInCollection(pack, art.Style, art.Category);
        }
    }
}
