using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>R41: data mods are checked (format, paths, roles), their assets go into the catalog with roles, their tables into the hex map.</summary>
public sealed class DataModTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-mods-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssetCatalog> _catalogs = [];

    [Fact]
    public void A_good_mod_is_read_and_a_broken_one_says_why()
    {
        Mod("forest", """{"format":1,"id":"forest","name":"Лесной набор","version":"1.0","assets":[{"file":"oak.png","name":"Дуб","role":"Tree"}],"encounters":[{"terrain":"forest","entries":["Волки","Охотники"]}]}""", "oak.png");
        Mod("broken", """{"format":2,"id":"","name":"Сломанный","assets":[{"file":"../../evil.png","role":"Dragon"},{"file":"none.png","role":"Tree"}]}""");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        var mods = DataMods.Scan(_root);
        Assert.Equal(3, mods.Count);
        var broken = mods.Single(m => m.Folder.EndsWith("broken", StringComparison.Ordinal));
        Assert.False(broken.IsUsable);
        Assert.Contains(broken.Problems, p => p.StartsWith("формат 2", StringComparison.Ordinal));
        Assert.Contains(broken.Problems, p => p.Contains("вне папки мода", StringComparison.Ordinal));
        Assert.Contains(broken.Problems, p => p.Contains("Dragon", StringComparison.Ordinal));
        Assert.Contains(broken.Problems, p => p.Contains("нет файла «none.png»", StringComparison.Ordinal));
        Assert.Equal(["нет файла mod.json"], mods.Single(m => m.Folder.EndsWith("empty", StringComparison.Ordinal)).Problems);
        Assert.True(mods.Single(m => m.Id == "forest").IsUsable);
    }

    [Fact]
    public void A_mod_brings_assets_with_roles_and_encounter_tables()
    {
        Mod("forest", """{"format":1,"id":"forest","name":"Лесной набор","version":"1.0","assets":[{"file":"oak.png","name":"Дуб","role":"Tree"}],"encounters":[{"terrain":"Forest","entries":["Волки","Охотники"]}]}""", "oak.png");
        var catalog = new AssetCatalog(Path.Combine(_root, "_catalog"));
        _catalogs.Add(catalog);
        var editor = new EditorViewModel(HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Flat, 40, 6, 6, "#FF8FB0BA"),
            null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
            clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog) { ModsFolder = _root };
        var mod = Assert.Single(editor.ScanMods(), m => m.Id == "forest");
        Assert.Equal(1, editor.ImportModAssets(mod));
        var set = Assert.Single(catalog.Sets, s => s.Name == "Дуб");
        Assert.Equal(AssetRole.Tree, set.Placement!.Role);
        Assert.Contains(editor.ArtLanguages, c => c.Name == "Лесной набор");
        Assert.Equal(1, editor.AddModEncounters(mod));
        var table = Assert.Single(editor.SectorLayer!.Journey.Encounters);
        Assert.Equal(("forest", 2), (table.Terrain, table.Entries.Length));
        editor.Undo();
        Assert.Empty(editor.SectorLayer!.Journey.Encounters);
    }

    private void Mod(string folder, string json, params string[] pictures)
    {
        var path = Path.Combine(_root, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "mod.json"), json);
        foreach (var picture in pictures)
        {
            using var surface = SKSurface.Create(new SKImageInfo(24, 24))!;
            surface.Canvas.Clear(SKColors.DarkGreen);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(path, picture), data.ToArray());
        }
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
