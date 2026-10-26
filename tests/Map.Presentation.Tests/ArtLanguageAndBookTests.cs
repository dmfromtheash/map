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
