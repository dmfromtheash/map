using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>R10: «Сохранить выделенное как ассет» puts one transparent picture of the selection into the catalog and leaves the map as it was.</summary>
public sealed class SaveSelectionAsAssetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-save-asset-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_selection_becomes_a_catalog_asset_and_the_map_is_unchanged()
    {
        var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var house = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Дом", 100, 100, 60, 40, 0, "#FF8E6B4E", "#FF3A2A1B", 2, true, false);
        var roof = new SceneObject(ObjectId.New(), SceneObjectKind.Ellipse, "Крыша", 110, 80, 40, 30, 0, "#FFAA3322", "#FF3A2A1B", 2, true, false);
        var group = Guid.NewGuid();
        var document = new MapDocument(DocumentId.New(), "Деревня", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(400, 300, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1,
                [house with { GroupId = group, GroupName = "Хутор" }, roof with { GroupId = group, GroupName = "Хутор" }])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
        editor.Session.SelectedObjectId = house.Id;
        var depth = editor.Session.UndoDepth;
        var before = editor.Session.Document;

        var record = editor.SaveSelectionAsCatalogAsset();

        Assert.NotNull(record);
        Assert.Equal("Хутор", record!.DisplayName);
        Assert.Contains(catalog.Records, r => r.Sha256 == record.Sha256);
        Assert.Same(before, editor.Session.Document);
        Assert.Equal(depth, editor.Session.UndoDepth);
        var stored = Path.Combine(_root, "catalog", record.StoredRelativePath!);
        using var bitmap = SKBitmap.Decode(stored);
        Assert.Equal(62, bitmap.Width);   // the group with its 2 px outline: x 99..161, y 79..141
        Assert.Equal(62, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(1, 1).Alpha);   // transparent outside the objects
        Assert.True(bitmap.GetPixel(30, 40).Alpha > 200);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
