using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PlacementWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-placement-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssetCatalog> _catalogs = [];

    [Fact]
    public void Series_distance_is_independent_of_event_count_and_zoom_and_is_one_undo()
    {
        var sparse = Editor(out _); var dense = Editor(out _);
        var a = Draw(sparse, 1, 1); var b = Draw(dense, .2, 40);
        Assert.True(a.Length > 1);
        Assert.Equal(a.Select(o => (o.X, o.Y, o.Width, o.Height, o.Stamp)),
            b.Select(o => (o.X, o.Y, o.Width, o.Height, o.Stamp)));
        Assert.Equal(1, sparse.Session.UndoDepth);
        Assert.True(sparse.Session.Undo());
        Assert.Empty(Objects(sparse));
        Assert.True(sparse.Session.Redo());
        Assert.Equal(a, Objects(sparse));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("leave")]
    [InlineData("escape")]
    [InlineData("tool")]
    public void Interrupted_series_never_commits_a_partial_gesture(string reason)
    {
        var editor = Editor(out _);
        editor.StampPlacementMode = StampPlacementMode.Series;
        var router = new CanvasInputRouter(editor);
        router.Press(CanvasPointerButton.Left, 100, 100);
        router.Move(400, 100);
        Assert.True(editor.HasTransientEditorAction);
        Assert.Empty(Objects(editor));
        if (reason == "capture") router.Cancel();
        if (reason == "leave") router.PointerLeft();
        if (reason == "escape") editor.CancelPlacement();
        if (reason == "tool") editor.ActiveTool = EditorTool.Select;
        router.Release(400, 100);
        Assert.False(editor.IsStampSeriesActive);
        Assert.Empty(Objects(editor));
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public async Task One_is_one_per_press_armed_stamp_allows_autosave_and_viewing_is_not_usage()
    {
        var editor = Editor(out var catalog);
        Assert.Empty(catalog.Recent);
        Assert.True(editor.ChooseGalleryVariant(catalog.Sets[0].Id, null));
        Assert.Empty(catalog.Recent);
        Assert.False(editor.HasTransientEditorAction);
        var router = new CanvasInputRouter(editor);
        router.Press(CanvasPointerButton.Left, 100, 100);
        router.Move(400, 100);
        router.Release(400, 100);
        Assert.Single(Objects(editor));
        Assert.True(editor.HasPendingPlacement);
        Assert.True(await editor.TryAutosaveAsync());
        Assert.Single(catalog.Recent);
        editor.Session.Undo();
        Assert.Single(catalog.Recent);
    }

    [Fact]
    public void Pointer_preview_does_not_invalidate_catalog_or_tool_settings()
    {
        var editor = Editor(out _);
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        for (var i = 0; i < 20; i++) editor.UpdatePlacementPreview(100 + i, 200);
        Assert.DoesNotContain(nameof(editor.LibraryRows), changed);
        Assert.DoesNotContain(nameof(editor.CompactRecentSets), changed);
        Assert.DoesNotContain(nameof(editor.StampToolSettings), changed);
        Assert.DoesNotContain(nameof(editor.SelectedSetVariants), changed);
    }

    [Fact]
    public void Browsing_variant_for_selected_object_preserves_tool_document_selection_and_history()
    {
        var editor = Editor(out var catalog);
        var router = new CanvasInputRouter(editor);
        router.Press(CanvasPointerButton.Left, 100, 100); router.Release(100, 100);
        var placed = Objects(editor).Single();
        editor.ActiveTool = EditorTool.Select;
        editor.Session.SetSelection([placed.Id]);
        var before = editor.Session.Document;
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.ChooseGalleryVariant(catalog.Sets[0].Id, catalog.Variants[0].Id, armPlacement: false));
        Assert.Same(before, editor.Session.Document);
        Assert.Equal(placed.Id, editor.Session.SelectedObjectId);
        Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Equal(EditorTool.Select, editor.ActiveTool);
        Assert.False(editor.HasPendingPlacement);
        Assert.Equal(catalog.Variants[0].Id, editor.CurrentVariant?.Id);
        Assert.True(editor.ChooseGalleryVariant(catalog.Sets[0].Id, catalog.Variants[0].Id));
        Assert.True(editor.HasPendingPlacement);
        Assert.Equal(EditorTool.Stamp, editor.ActiveTool);
    }

    private static SceneObject[] Draw(EditorViewModel editor, double zoom, int events)
    {
        editor.Session.Viewport.Set(0, 0, zoom);
        editor.StampPlacementMode = StampPlacementMode.Series;
        editor.SetSeriesSpacingPercent(100, true);
        var router = new CanvasInputRouter(editor);
        router.Press(CanvasPointerButton.Left, 100 * zoom, 100 * zoom);
        for (var i = 1; i <= events; i++) router.Move((100 + 320d * i / events) * zoom, 100 * zoom);
        Assert.Empty(Objects(editor));
        router.Release(420 * zoom, 100 * zoom);
        return Objects(editor);
    }

    private EditorViewModel Editor(out AssetCatalog catalog)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "own.png");
        using (var surface = SKSurface.Create(new SKImageInfo(32, 32))!)
        {
            surface.Canvas.Clear(SKColors.ForestGreen);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        catalog = new AssetCatalog(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        _catalogs.Add(catalog);
        catalog.ImportSet([path], "Дерево", new(), DateTimeOffset.UnixEpoch);
        var document = new MapDocument(DocumentId.New(), "Placement", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog);
        editor.SelectAssetSet(catalog.Sets[0].Id);
        editor.PlacementAutoSelect = PlacementAutoSelectPolicy.Off;
        editor.StampPlacementMode = StampPlacementMode.One;
        return editor;
    }
    private static SceneObject[] Objects(EditorViewModel editor) => editor.Session.Document.Layers
        .OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
    public void Dispose()
    {
        foreach (var catalog in _catalogs) catalog.FlushMetadataAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
