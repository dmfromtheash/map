using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>«После размещения: выделять / не выделять» of the asset tool.</summary>
public sealed class SelectAfterPlacementTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-select-after-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void By_default_a_placed_asset_is_not_selected_and_the_tool_stays_armed()
    {
        var editor = Editor(out var catalog, out _);
        Assert.False(editor.SelectAfterPlacement);
        Assert.Equal(PlacementAutoSelectPolicy.Auto, new StampToolSettings().AutoSelect);
        editor.SelectAssetSet(catalog.Sets[0].Id);
        Assert.True(editor.PlaceCurrentVariantAt(200, 200));
        Assert.Empty(editor.Session.SelectedObjectIds);
        Assert.NotNull(editor.PendingPlacement);

        // A single shape keeps its long-standing behaviour: it is selected after placing.
        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        editor.UpdatePlacementPreview(400, 300);
        Assert.True(editor.CommitPendingPlacement());
        Assert.NotNull(editor.Session.SelectedObjectId);
    }

    [Theory]
    [InlineData(StampPlacementMode.One)]
    [InlineData(StampPlacementMode.Area)]
    public void Select_after_placement_selects_what_was_placed_and_is_remembered(StampPlacementMode mode)
    {
        var editor = Editor(out var catalog, out var saved);
        editor.SelectAssetSet(catalog.Sets[0].Id);
        editor.StampPlacementMode = mode;
        editor.SelectAfterPlacement = true;
        Assert.Equal(PlacementAutoSelectPolicy.Always, saved.Last().Stamp!.AutoSelect);

        editor.UpdatePlacementPreview(500, 400);
        Assert.True(editor.CommitPendingPlacement());
        var placed = editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        Assert.Contains(editor.Session.SelectedObjectId!.Value, placed.Select(o => o.Id));
        Assert.NotNull(editor.PendingPlacement);

        editor.SelectAfterPlacement = false;
        Assert.Equal(PlacementAutoSelectPolicy.Auto, editor.PlacementAutoSelect);
    }

    private EditorViewModel Editor(out AssetCatalog catalog, out List<EditorLocalPreferences> saved)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        using (var surface = SKSurface.Create(new SKImageInfo(24, 24))!)
        {
            surface.Canvas.Clear(SKColors.Teal);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        catalog.ImportSet([path], "Куст", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var list = new List<EditorLocalPreferences>();
        saved = list;
        var document = new MapDocument(DocumentId.New(), "select", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog,
            saveLocalPreferences: list.Add);
        editor.Session.Viewport.Resize(1200, 800);
        return editor;
    }

    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
