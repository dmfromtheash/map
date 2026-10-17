using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>«Без наложений» from the tool: the switch, the summary, the layer's stamps and the passport.</summary>
public sealed class StampAvoidOverlapWorkflowTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-avoid-overlap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Switch_spaces_a_scatter_out_and_keeps_away_from_stamps_already_on_the_layer()
    {
        var editor = Editor(out var catalog, out var preferences);
        var set = catalog.Sets.Single();
        editor.SelectAssetSet(set.Id);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.SetStampAreaRadius(160, persist: true);
        editor.SetStampDensity(250, persist: true);

        editor.UpdatePlacementPreview(400, 400);
        Assert.True(editor.CommitPendingPlacement());
        var dense = Objects(editor).Count;
        editor.Undo();

        editor.StampAvoidOverlap = true;
        Assert.True(preferences.Last().Stamp!.AvoidOverlap);
        Assert.Contains("без наложений", editor.StampAreaSummary);
        editor.UpdatePlacementPreview(400, 400);
        Assert.True(editor.CommitPendingPlacement());
        var first = Objects(editor).ToArray();
        Assert.InRange(first.Length, 2, dense - 1);
        AssertNoOverlap(first, StampCollision.DefaultFraction);

        // A second click that half covers the first circle fills only the free part.
        editor.UpdatePlacementPreview(520, 400);
        Assert.True(editor.CommitPendingPlacement());
        AssertNoOverlap(Objects(editor), StampCollision.DefaultFraction);
        Assert.True(Objects(editor).Count > first.Length);
        Assert.Equal(2, editor.Session.UndoDepth);
    }

    [Fact]
    public void Passport_fraction_changes_how_tightly_the_asset_packs()
    {
        var editor = Editor(out var catalog, out _);
        var set = catalog.Sets.Single();
        editor.SelectAssetSet(set.Id);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.SetStampAreaRadius(160, persist: true);
        editor.SetStampDensity(250, persist: true);
        editor.StampAvoidOverlap = true;

        catalog.SetPlacementDefaults(set.Id, new AssetPlacementDefaults { CollisionFraction = 0.9 });
        editor.UpdatePlacementPreview(400, 400);
        Assert.True(editor.CommitPendingPlacement());
        var loose = Objects(editor).Count;
        AssertNoOverlap(Objects(editor), 0.9);
        editor.Undo();

        catalog.SetPlacementDefaults(set.Id, new AssetPlacementDefaults { CollisionFraction = 0.1 });
        editor.UpdatePlacementPreview(400, 400);
        Assert.True(editor.CommitPendingPlacement());
        Assert.True(Objects(editor).Count > loose, $"tight {Objects(editor).Count}, loose {loose}");
    }

    private static void AssertNoOverlap(IReadOnlyList<SceneObject> stamps, double fraction)
    {
        var boxes = stamps.Select(item => StampCollision.Box(
            item.X + (item.Width / 2), item.Y + (item.Height / 2), item.Width, item.Height, item.RotationDegrees, fraction)).ToArray();
        for (var i = 0; i < boxes.Length; i++)
        for (var j = i + 1; j < boxes.Length; j++)
            Assert.False(StampCollision.Overlaps(boxes[i], boxes[j]), $"stamps {i} and {j} overlap");
    }

    private static IReadOnlyList<SceneObject> Objects(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).ToArray();

    private EditorViewModel Editor(out AssetCatalog catalog, out List<EditorLocalPreferences> preferences)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        catalog.ImportSet([Image("rock-a", SKColors.SlateGray, 32, 32), Image("rock-b", SKColors.DimGray, 40, 28)],
            "Камни", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var saved = new List<EditorLocalPreferences>();
        preferences = saved;
        var document = new MapDocument(DocumentId.New(), "overlap", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch,
            assetCatalog: catalog, saveLocalPreferences: saved.Add);
    }

    private string Image(string name, SKColor color, int width, int height)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N") + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
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
