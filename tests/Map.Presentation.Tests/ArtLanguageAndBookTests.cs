using System.Collections.Immutable;
using Map.Assets;
using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>R40: a map redrawn in another art language by roles (one undo step); the atlas book in one PDF.</summary>
public sealed class ArtLanguageAndBookTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-artlang-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Stamps_are_redrawn_with_the_same_roles_from_another_collection_in_one_step()
    {
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        _catalogs.Add(catalog);
        AssetSet Import(string name, SKColor color, string pack, AssetRole role)
        {
            var set = catalog.ImportSet([Image(name, color)], name, new AssetImportOptions { Pack = pack }, DateTimeOffset.UnixEpoch);
            catalog.SetPlacementDefaults(set.Id, new AssetPlacementDefaults { Role = role });
            return set;
        }
        var treeA = Import("tree-a", SKColors.DarkRed, "Акварель", AssetRole.Tree);
        var houseA = Import("house-a", SKColors.Brown, "Акварель", AssetRole.House);
        var treeB = Import("tree-b", SKColors.DarkGreen, "Гравюра", AssetRole.Tree);
        Import("house-b", SKColors.Gray, "Гравюра", AssetRole.House);
        var plain = catalog.ImportSet([Image("rock", SKColors.SlateGray)], "Камень", new AssetImportOptions { Pack = "Акварель" }, DateTimeOffset.UnixEpoch);

        var document = new MapDocument(DocumentId.New(), "lang", DateTimeOffset.UnixEpoch, new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
        foreach (var (set, x) in new[] { (treeA, 200.0), (houseA, 500.0), (plain, 800.0) })
        {
            editor.SelectAssetSet(set.Id);
            editor.UpdatePlacementPreview(x, 300);
            Assert.True(editor.CommitPendingPlacement());
        }
        string Sha(string id) => catalog.FindVariant(catalog.FindSet(id)!.VariantIds[0])!.Sha256;
        string[] Stamps() => [.. editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Select(o => o.Stamp!.AssetSha256)];
        var before = Stamps();
        var gravure = Assert.Single(editor.ArtLanguages, c => c.Name == "Гравюра");
        Assert.Contains(editor.ArtLanguages, c => c.Name == "Акварель");

        var plan = editor.PreviewArtLanguage(gravure.Id);
        Assert.Equal((2, 1), (plan.Replaced, plan.WithoutRole));
        var depth = editor.Session.UndoDepth;
        editor.ChangeArtLanguage(gravure.Id);
        var after = Stamps();
        Assert.Equal(Sha(treeB.Id), after[0]);
        Assert.NotEqual(before[1], after[1]);
        Assert.Equal(before[2], after[2]);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(0, editor.PreviewArtLanguage(gravure.Id).Replaced);
        editor.Undo();
        Assert.Equal(before, Stamps());

        // R40: language and style at once, one undo for both.
        editor.ChangeArtLanguage(gravure.Id, MapStylePreset.EngravingAquatint);
        Assert.Equal(Sha(treeB.Id), Stamps()[0]);
        Assert.Equal(MapStylePreset.EngravingAquatint, editor.Style.Preset);
        Assert.True(editor.Style.Enabled);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        editor.Undo();
        Assert.Equal(before, Stamps());
        Assert.False(editor.Style.Enabled);
    }

    [Fact]
    public void The_atlas_book_has_contents_maps_keys_and_an_index()
    {
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "Ильград", 100, 100, 140, 40, 0, "#FF20242B", "#FFFFFFFF", 0, true, false)
        { Text = new TextStyle { Content = "Ильград" } };
        MapDocument Doc(string title, params SceneObject[] objects) => new(DocumentId.New(), title, DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FFEFE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEFE6D2"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)]);
        var world = Doc("Мир", label).WithNotes([new MapNote(NoteId.New(), "Таверна", "Хозяин знает всё.", NoteAnchorKind.World, 50, 50)]);
        var city = Doc("Ильград");
        var atlas = new MapAtlas(Guid.NewGuid(), [new AtlasMap(city.Id.Value, "city.mapproj", "Ильград"), new AtlasMap(world.Id.Value, "world.mapproj", "Мир")],
            [new AtlasLink(world.Id.Value, city.Id.Value, new AtlasTransform(80, 80, 200, 150, 1024, 768), "region")]);
        Assert.Equal(["Мир", "Ильград"], AtlasBook.Order(atlas).Select(m => m.Title));

        var path = Path.Combine(Path.GetTempPath(), "map-book-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var pages = AtlasBook.Write(path, "Мир", [(world.Id.Value, world), (city.Id.Value, city)], atlas, players: false, stamps: null);
            // Contents, the world, its key, the city, the index.
            Assert.Equal(5, pages);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
            Assert.Equal(4, AtlasBook.Write(path, "Мир", [(world.Id.Value, world), (city.Id.Value, city)], atlas, players: true, stamps: null));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Every_map_of_the_book_draws_its_own_embedded_pictures()
    {
        // The detail sheet holds one of the owner's mountains embedded in its own file; the parent map has none.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-02.png"));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var mountain = new MapAssetReference(sha, "Двуглавая гора", "image/png", "png", ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "test", "Map", bytes);
        var peak = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", 300, 200, 200, 200, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(sha) };
        MapDocument Doc(string title, SceneObject[] objects, MapAssetReference[] assets) => new(DocumentId.New(), title, DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFEFE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEFE6D2"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)], assets);
        var world = Doc("Мир", [], []);
        var range = Doc("Хребет", [peak], [mountain]);
        var atlas = new MapAtlas(Guid.NewGuid(), [new AtlasMap(world.Id.Value, "world.mapproj", "Мир"), new AtlasMap(range.Id.Value, "range.mapproj", "Хребет")],
            [new AtlasLink(world.Id.Value, range.Id.Value, new AtlasTransform(80, 80, 200, 150, 1024, 768), "region")]);

        var asked = new List<(Guid Id, string Title, bool HasMountain)>();
        var path = Path.Combine(Path.GetTempPath(), "map-book-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            AtlasBook.Write(path, "Мир", [(world.Id.Value, world), (range.Id.Value, range)], atlas, players: false, (id, document) =>
            {
                var source = new Map.Presentation.Services.ExportStampImageSource(document, null, null);
                asked.Add((id, document.Title, source.TryGetImage(sha) is not null));
                return source;
            });
            Assert.Equal([(world.Id.Value, "Мир", false), (range.Id.Value, "Хребет", true)], asked);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void The_players_book_leaves_out_the_cards_under_the_fog_until_it_opens()
    {
        // DEC-077: a card meant for the players, bound to a place they have not reached.
        var card = new MapEntity { Name = "Логово дракона", PlayerVisible = true, Bindings = [new EntityBinding(null, EntityTargetKind.Point, Guid.Empty, 700, 500)] };
        var island = new MapDocument(DocumentId.New(), "Остров", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FFEFE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEFE6D2"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])])
        { Fog = new MapFog { Enabled = true }, Entities = [card] };
        var path = Path.Combine(Path.GetTempPath(), "map-book-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            // Under the fog: contents and the map, no key and no index. The master's book has both.
            Assert.Equal(2, AtlasBook.Write(path, "Остров", [(island.Id.Value, island)], null, players: true, stamps: null));
            Assert.Equal(4, AtlasBook.Write(path, "Остров", [(island.Id.Value, island)], null, players: false, stamps: null));
            var opened = island with { Fog = Map.Core.Editing.MapFogEditor.RevealAll(island.Fog, 1024, 768) };
            Assert.Equal(4, AtlasBook.Write(path, "Остров", [(opened.Id.Value, opened)], null, players: true, stamps: null));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private string Image(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N") + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(30, 20))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
