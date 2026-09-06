using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PlacementWorkflowTests : IDisposable
{
    [Fact]
    public void Stage18_router_creation_preview_cancel_edit_parameters_detach_and_repeat_preserve_history()
    {
        var editor = Editor(out _); var router = new CanvasInputRouter(editor);
        editor.StampDensity = 3; editor.SetStampScalePercent(35); editor.StartEditableArea();
        var original = editor.Session.Document;
        void Point(double x, double y) { router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y); }
        Point(100, 100); Point(800, 100); Point(800, 600); Point(450, 400); Point(100, 600);
        Assert.True(editor.HasTransientEditorAction); Assert.Same(original, editor.Session.Document);
        var preview = editor.PlacementPreviewObjects.ToArray(); Assert.NotEmpty(preview);
        Assert.True(editor.FinishEditableArea(), editor.StatusDetail);
        Assert.Equal(1, editor.Session.UndoDepth);
        foreach (var item in preview) Assert.Equal(item, Map.Core.Editing.DocumentEditor.FindObject(editor.Session.Document, item.Id));
        var created = editor.Session.Document; var area = editor.SelectedEditableArea!;
        router.Press(CanvasPointerButton.Left, 800, 100); router.Move(700, 150);
        Assert.Same(created, editor.Session.Document); editor.HandleEscape(); router.Release(700, 150);
        Assert.Same(created, editor.Session.Document);
        router.Press(CanvasPointerButton.Left, 800, 100); router.Move(700, 150); router.Release(700, 150);
        Assert.Equal(2, editor.Session.UndoDepth); Assert.Equal(700, editor.SelectedEditableArea!.Nodes[1].X);
        editor.SetEditableAreaDensity(5); Assert.Equal(5, editor.SelectedEditableArea.Density);
        var member = editor.SelectedEditableArea.Members.First();
        editor.Session.SelectedObjectId = member.ObjectId; editor.DetachSelectedAreaObject();
        var detached = Map.Core.Editing.DocumentEditor.FindObject(editor.Session.Document, member.ObjectId);
        editor.SetEditableAreaSeed(1800); Assert.Same(detached, Map.Core.Editing.DocumentEditor.FindObject(editor.Session.Document, member.ObjectId));
        editor.InsertEditableAreaNode(); Point(100, 350); Assert.Equal(6, editor.SelectedEditableArea.Nodes.Length);
        editor.DeleteEditableAreaNode(); Assert.Equal(5, editor.SelectedEditableArea.Nodes.Length);
        editor.SetStampScalePercent(70); editor.SetStampRotationDegrees(345); editor.ApplyStampSettingsToEditableArea();
        Assert.Equal(70, editor.SelectedEditableArea.ScalePercent); Assert.Equal(345, editor.SelectedEditableArea.RotationDegrees);
        var edited = editor.Session.Document; editor.Undo(); editor.Redo(); Assert.Same(edited, editor.Session.Document);
        editor.StartEditableArea(); Point(150, 150); editor.HandleEscape(); Assert.Same(edited, editor.Session.Document);
        editor.SelectEditableArea(area.Id); editor.SetStampScalePercent(80); editor.SetStampRotationDegrees(90);
        editor.StartEditableArea(); Point(100, 100); Point(300, 100); Point(300, 300);
        Assert.True(editor.FinishEditableArea(), editor.StatusDetail); Assert.Equal(2, editor.EditableAreas.Count);
        Assert.Equal(80, editor.SelectedEditableArea!.ScalePercent); Assert.Equal(90, editor.SelectedEditableArea.RotationDegrees);
        Assert.Equal(70, editor.EditableAreas.Single(a => a.Id == area.Id).ScalePercent);
        var count = Objects(editor).Length; editor.StampPlacementMode = StampPlacementMode.One; Point(100, 100);
        Assert.Null(editor.SelectedEditableArea); Assert.Equal(count + 1, Objects(editor).Length);
    }

    [Fact]
    public void Stage17_long_draft_keeps_preview_at_the_new_end_after_the_first_64_stamps()
    {
        var editor = Editor(out _); var router = new CanvasInputRouter(editor);
        editor.SetStampScalePercent(25);
        editor.StartEditablePath();
        var before = editor.Session.Document;
        for (var i = 0; i < 10; i++)
        {
            var x = i % 2 == 0 ? 100 : 900; var y = 100 + i * 60;
            router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y);
        }
        var draft = editor.DisplayedEditablePath!;
        var lastSegment = draft.Nodes.Length - 2;
        var lastOrdinal = Map.Core.Editing.PlacementPathPlanner.SegmentCount(draft, lastSegment) - 1;
        var lastId = Map.Core.Editing.PlacementPathPlanner.ObjectIdFor(draft.Id,
            new(draft.Nodes[lastSegment].Id, lastOrdinal));
        Assert.Equal(64, editor.PlacementPreviewObjects.Count);
        Assert.Contains(editor.PlacementPreviewObjects, o => o.Id == lastId);
        Assert.Same(before, editor.Session.Document); Assert.Equal(0, editor.Session.UndoDepth);
        var preview = editor.PlacementPreviewObjects.ToArray();
        Assert.True(editor.FinishEditablePath(), editor.StatusDetail);
        Assert.True(editor.SelectedEditablePath!.Members.Length > 64);
        foreach (var sample in preview)
            Assert.Equal(sample, Map.Core.Editing.DocumentEditor.FindObject(editor.Session.Document, sample.Id));
    }

    [Fact]
    public void Stage17_eight_separate_paths_have_no_four_path_limit()
    {
        var editor = Editor(out _); var router = new CanvasInputRouter(editor);
        for (var i = 0; i < 8; i++)
        {
            editor.StartEditablePath();
            router.Press(CanvasPointerButton.Left, 100, 100 + i * 70); router.Release(100, 100 + i * 70);
            router.Press(CanvasPointerButton.Left, 700, 100 + i * 70); router.Release(700, 100 + i * 70);
            Assert.True(editor.FinishEditablePath(), editor.StatusDetail);
            Assert.Equal(i + 1, editor.EditablePaths.Count);
        }
        Assert.Equal(8, editor.Session.UndoDepth);
    }

    [Fact]
    public void Stage17_router_creates_edits_cancels_and_detaches_a_saved_path_as_single_gestures()
    {
        var editor = Editor(out _); var router = new CanvasInputRouter(editor);
        editor.StartEditablePath();
        foreach (var point in new[] { (100, 100), (350, 160), (650, 100) })
        { router.Press(CanvasPointerButton.Left, point.Item1, point.Item2); router.Release(point.Item1, point.Item2); }
        Assert.True(editor.HasTransientEditorAction); Assert.Empty(Objects(editor));
        Assert.True(editor.FinishEditablePath()); Assert.Equal(1, editor.Session.UndoDepth);
        var created = editor.Session.Document; var path = editor.SelectedEditablePath!;
        Assert.Equal(3, path.Nodes.Length);
        router.Press(CanvasPointerButton.Left, 350, 160); router.Move(350, 250);
        Assert.Same(created, editor.Session.Document); Assert.True(editor.HandleEscape());
        router.Release(350, 250); Assert.Same(created, editor.Session.Document);
        router.Press(CanvasPointerButton.Left, 350, 160); router.Move(350, 210); router.Release(350, 210);
        Assert.Equal(2, editor.Session.UndoDepth); Assert.Equal(210, editor.SelectedEditablePath!.Nodes[1].Y);
        editor.SetEditablePathSpacing(50);
        var member = editor.SelectedEditablePath!.Members[1];
        editor.Session.SelectedObjectId = member.ObjectId; editor.DetachSelectedPathObject();
        var detached = editor.Session.Document;
        editor.SetEditablePathSeed(888);
        Assert.Equal(Map.Core.Editing.DocumentEditor.FindObject(detached, member.ObjectId),
            Map.Core.Editing.DocumentEditor.FindObject(editor.Session.Document, member.ObjectId));
        editor.Undo(); Assert.Same(detached, editor.Session.Document); editor.Redo();
        editor.InsertEditablePathNode();
        Assert.True(editor.PressEditablePath(225, 155));
        Assert.Equal(4, editor.SelectedEditablePath!.Nodes.Length);
        editor.DeleteEditablePathNode(); Assert.Equal(3, editor.SelectedEditablePath!.Nodes.Length);
        editor.SelectEditablePath(path.Id);
        editor.SetStampScalePercent(130); editor.SetStampRotationDegrees(25);
        editor.ApplyStampSettingsToEditablePath();
        Assert.Equal(130, editor.SelectedEditablePath!.ScalePercent);
        Assert.Equal(25, editor.SelectedEditablePath.RotationDegrees);
        editor.Undo(); Assert.Equal(100, editor.StampScalePercent);
        editor.Redo(); Assert.Equal(130, editor.StampScalePercent);
        var previousCount = Objects(editor).Length;
        editor.StampPlacementMode = StampPlacementMode.One;
        Assert.Null(editor.SelectedEditablePath);
        router.Press(CanvasPointerButton.Left, 100, 100); router.Release(100, 100);
        Assert.Equal(previousCount + 1, Objects(editor).Length);
        Assert.False(editor.IsDraggingPathNode);
    }

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
