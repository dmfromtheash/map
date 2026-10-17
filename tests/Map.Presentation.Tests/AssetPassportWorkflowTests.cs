using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>Per-asset default flags: the tool starts from them, the user may override them.</summary>
public sealed class AssetPassportWorkflowTests : IDisposable
{
    private readonly List<AssetCatalog> _catalogs = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-passport-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Choosing_an_asset_takes_its_flags_and_the_user_can_override_them()
    {
        var editor = Editor(out var catalog);
        var (rock, plain) = (catalog.Sets[0], catalog.Sets[1]);
        catalog.SetPlacementDefaults(rock.Id, new AssetPlacementDefaults { RandomRotation = true, RandomSizePercent = 35, NoShadow = true });
        editor.StampSizeJitterPercent = 10;

        editor.SelectAssetSet(rock.Id);
        Assert.Equal((180.0, 35.0), (editor.StampRotationJitterDegrees, editor.StampSizeJitterPercent));
        Assert.Equal(0, editor.StampToolSettings.Style!.ShadowOpacity);

        // Untouched passport values go back to the user's own when a card without a passport is chosen.
        editor.SelectAssetSet(plain.Id);
        Assert.Equal((0.0, 10.0), (editor.StampRotationJitterDegrees, editor.StampSizeJitterPercent));

        editor.SelectAssetSet(rock.Id);
        editor.StampRotationJitterDegrees = 30;
        editor.SelectAssetSet(rock.Id);
        Assert.Equal(30, editor.StampRotationJitterDegrees);
        editor.SelectAssetSet(plain.Id);
        Assert.Equal(30, editor.StampRotationJitterDegrees);
    }

    [Fact]
    public void Forbidden_mirroring_switches_random_flip_off_for_that_asset_only()
    {
        var editor = Editor(out var catalog);
        var (badge, plain) = (catalog.Sets[0], catalog.Sets[1]);
        editor.SelectAssetSet(badge.Id);
        editor.StampRandomFlip = true;
        Assert.True(editor.SetSelectedSetPlacement(p => p with { CanFlip = false }));
        Assert.False(editor.SelectedSetCanFlip);
        Assert.False(editor.StampRandomFlip);

        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.SetStampAreaRadius(200, persist: true);
        editor.SetStampDensity(200, persist: true);
        editor.StampRandomFlip = true;
        editor.UpdatePlacementPreview(500, 400);
        Assert.True(editor.CommitPendingPlacement());
        Assert.All(Objects(editor), stamp => Assert.False(stamp.Stamp!.FlipHorizontal || stamp.Stamp.FlipVertical));

        editor.SelectAssetSet(plain.Id);
        Assert.True(editor.SelectedSetCanFlip);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Editing_the_passport_of_the_chosen_asset_updates_the_tool_at_once()
    {
        var editor = Editor(out var catalog);
        editor.SelectAssetSet(catalog.Sets[0].Id);
        Assert.True(editor.SetSelectedSetPlacement(p => p with { RandomSizePercent = 50 }));
        Assert.Equal(50, editor.StampSizeJitterPercent);
        Assert.Equal(50, catalog.FindSet(catalog.Sets[0].Id)!.Placement!.RandomSizePercent);
        Assert.False(editor.SetSelectedSetPlacement(p => p));
    }

    private static IReadOnlyList<SceneObject> Objects(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).ToArray();

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        catalog = new AssetCatalog(Path.Combine(_root, "catalog-" + Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        catalog.ImportSet([Image("rock", SKColors.SlateGray)], "Камень", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        catalog.ImportSet([Image("house", SKColors.Brown)], "Дом", new AssetImportOptions(), DateTimeOffset.UnixEpoch.AddSeconds(1));
        var document = new MapDocument(DocumentId.New(), "passport", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), clock: () => DateTimeOffset.UnixEpoch, assetCatalog: catalog);
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
