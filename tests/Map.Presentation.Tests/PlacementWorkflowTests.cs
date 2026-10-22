using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PlacementWorkflowTests : IDisposable
{
    [Fact]
    public void Stage18_mixed_selection_drags_from_empty_frame_space_and_nudges_as_one_undoable_edit()
    {
        var (editor, area, selected) = AreaSelection();
        var before = editor.Session.Document; var depth = editor.Session.UndoDepth;
        var ids = selected.Select(o => o.Id).ToArray(); editor.Session.SetSelection(ids);
        var frame = MultiSelectionGeometry.CombinedBounds(selected)!.Value;
        var x = selected[^1].X - 80; var y = frame.Top + 10;
        Assert.Null(SceneGeometry.HitTest(before, x, y));
        Assert.Equal(CanvasGesture.Drag, editor.BeginCanvasInteraction(x, y, 1));
        editor.EndDrag(); Assert.Same(before, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Equal(CanvasGesture.Drag, editor.BeginCanvasInteraction(x, y, 1));
        editor.UpdateDrag(40, 25); Assert.Same(before, editor.Session.Document);
        editor.CancelDrag(); Assert.Same(before, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Equal(CanvasGesture.Drag, editor.BeginCanvasInteraction(x, y, 1));
        editor.UpdateDrag(40, 25); editor.EndDrag();
        Assert.Null(editor.Session.LastResourceError); Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var moved = editor.Session.Document;
        foreach (var item in selected)
        {
            var actual = DocumentEditor.FindObject(moved, item.Id)!;
            Assert.InRange(Math.Abs(actual.X - item.X - 40), 0, .5);
            Assert.InRange(Math.Abs(actual.Y - item.Y - 25), 0, .5);
            Assert.Null(PlacementAreaOwnership.Owner(moved, item.Id));
        }
        var remaining = moved.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
        Assert.Equal(area.MemberCount - 2, remaining.MemberCount); remaining.ValidateMembers();
        Assert.All(remaining.Members, m => Assert.Same(DocumentEditor.FindObject(before, m.ObjectId), DocumentEditor.FindObject(moved, m.ObjectId)));
        editor.Undo(); Assert.Same(before, editor.Session.Document); editor.Redo(); Assert.Same(moved, editor.Session.Document);
        editor.Undo(); editor.Session.SetSelection(ids); editor.NudgeSelectedObject(10, -5);
        Assert.Null(editor.Session.LastResourceError); Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.All(selected, item => Assert.InRange(Math.Abs(DocumentEditor.FindObject(editor.Session.Document, item.Id)!.X - item.X - 10), 0, .5));
    }

    [Fact]
    public void Stage18_delete_and_layer_transfer_detach_only_selected_members_and_keep_exact_history()
    {
        var (editor, area, selected) = AreaSelection();
        var ids = selected.Select(o => o.Id).ToArray(); editor.Session.SetSelection(ids);
        var before = editor.Session.Document; var depth = editor.Session.UndoDepth;
        editor.DeleteSelectedObject(); var deleted = editor.Session.Document;
        Assert.Null(editor.Session.LastResourceError); Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Empty(editor.Session.SelectedObjectIds);
        Assert.All(ids, id => Assert.Null(DocumentEditor.FindObject(deleted, id)));
        var remaining = deleted.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
        Assert.Equal(area.MemberCount - 2, remaining.MemberCount); remaining.ValidateMembers();
        editor.Undo(); Assert.Same(before, editor.Session.Document); editor.Redo(); Assert.Same(deleted, editor.Session.Document);
        editor.Undo(); editor.AddObjectLayer(); var target = editor.ActiveLayer!.Id;
        editor.Session.SetSelection(ids); before = editor.Session.Document; depth = editor.Session.UndoDepth;
        Assert.True(editor.MoveSelectionToLayer(target), editor.StatusDetail);
        Assert.Equal(depth + 1, editor.Session.UndoDepth); var transferred = editor.Session.Document;
        Assert.All(selected, item => {
            Assert.Same(item, DocumentEditor.FindObject(transferred, item.Id));
            Assert.Equal(target, DocumentEditor.FindLayerOf(transferred, item.Id)!.Id);
            Assert.Null(PlacementAreaOwnership.Owner(transferred, item.Id));
        });
        transferred.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single().ValidateMembers();
        editor.Undo(); Assert.Same(before, editor.Session.Document); editor.Redo(); Assert.Same(transferred, editor.Session.Document);
    }

    [Fact]
    public void Stage18_refused_batch_keeps_selection_objects_and_area_links_untouched()
    {
        const long gib = 1024L * 1024 * 1024;
        var available = gib;
        var memory = new Map.Core.Resources.WorkingMemoryBudget(() => new(16 * gib, available, 100 * 1024 * 1024));
        var (editor, _, selected) = AreaSelection(memory);
        var ids = selected.Select(o => o.Id).ToArray(); editor.Session.SetSelection(ids);
        var before = editor.Session.Document; var depth = editor.Session.UndoDepth;
        // Stage 19 correction: a bounded delete of a handful of objects is admitted at 256 MiB of
        // free RAM - it costs kilobytes, and refusing it was the shared reserve talking, not the
        // work. The refusal contract is proven where the machine really has nothing left.
        available = 24 * 1024 * 1024; editor.DeleteSelectedObject();
        Assert.Same(before, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Equal(ids, editor.Session.SelectedObjectIds); Assert.NotNull(editor.Session.LastResourceError);
        available = 256 * 1024 * 1024;
        editor.DeleteSelectedObject();
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Null(editor.Session.LastResourceError);
        editor.Undo();
        Assert.Same(before, editor.Session.Document);
        editor.Session.SetSelection(ids);
        available = gib;
        var ordinary = selected[^1]; editor.Session.SetSelection([ordinary.Id]); editor.SetSelectedObjectLock(true);
        editor.Session.SetSelection(ids);
        before = editor.Session.Document; depth = editor.Session.UndoDepth;
        Assert.False(editor.BeginDrag()); editor.DeleteSelectedObject();
        Assert.Same(before, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Equal(ids, editor.Session.SelectedObjectIds);
    }

    private (EditorViewModel Editor, PlacementArea Area, SceneObject[] Selected) AreaSelection(Map.Core.Resources.WorkingMemoryBudget? memory = null)
    {
        var editor = Editor(out _, memory); editor.StampDensity = 8; editor.SetStampScalePercent(20); editor.StartEditableArea();
        editor.AddEditableAreaNode(100, 100); editor.AddEditableAreaNode(900, 100);
        editor.AddEditableAreaNode(900, 700); editor.AddEditableAreaNode(100, 700);
        Assert.True(editor.FinishEditableArea(), editor.StatusDetail);
        var area = editor.SelectedEditableArea!;
        var members = area.Members.Select(m => DocumentEditor.FindObject(editor.Session.Document, m.ObjectId)!).OrderBy(o => o.X).ToArray();
        Assert.True(members.Length >= 3);
        var ordinary = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Ручной объект", 1700, 100, 30, 30,
            0, "#FFFFFFFF", "#00000000", 0, true, false);
        editor.Session.Execute(new AddObjectCommand(editor.ActiveLayer!.Id, ordinary));
        editor.ActiveTool = EditorTool.Select;
        return (editor, area, [members[0], members[^1], ordinary]);
    }

    [Fact]
    public void Stage18_router_creation_preview_cancel_edit_parameters_detach_and_repeat_preserve_history()
    {
        var editor = Editor(out _); var router = new CanvasInputRouter(editor);
        editor.StampDensity = 3; editor.SetStampScalePercent(35); editor.StartEditableArea();
        var original = editor.Session.Document;
        Assert.False(editor.CanPlaceEditableArea);
        void Point(double x, double y) { router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y); }
        Point(100, 100); Point(800, 100);
        Assert.False(editor.CanPlaceEditableArea);
        Assert.False(editor.FinishEditableArea()); Assert.Same(original, editor.Session.Document);
        Point(800, 600); Point(450, 400); Point(100, 600);
        Assert.True(editor.CanPlaceEditableArea);
        Assert.True(editor.HasTransientEditorAction); Assert.Same(original, editor.Session.Document);
        var preview = editor.PlacementPreviewObjects.ToArray(); Assert.NotEmpty(preview);
        Assert.True(editor.FinishEditableArea(), editor.StatusDetail);
        Assert.False(editor.CanPlaceEditableArea); Assert.Contains("Размещено объектов:", editor.StatusDetail);
        Assert.Equal(1, editor.Session.UndoDepth);
        foreach (var item in preview) Assert.Equal(item, Map.Core.Editing.DocumentEditor.FindObject(editor.Session.Document, item.Id));
        var created = editor.Session.Document; var area = editor.SelectedEditableArea!;
        Assert.Equal(4, area.LayoutVersion);
        router.Press(CanvasPointerButton.Left, 800, 100); router.Move(700, 150);
        Assert.Same(created, editor.Session.Document); editor.HandleEscape(); router.Release(700, 150);
        Assert.Same(created, editor.Session.Document);
        router.Press(CanvasPointerButton.Left, 800, 100); router.Move(700, 150); router.Release(700, 150);
        Assert.Equal(2, editor.Session.UndoDepth); Assert.Equal(700, editor.SelectedEditableArea!.Nodes[1].X);
        Assert.Equal(area.CompositionOrigin, editor.SelectedEditableArea.CompositionOrigin);
        editor.SetEditableAreaDensity(80); Assert.Equal(80, editor.SelectedEditableArea.Density);
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
    public async Task Stage18_natural_density_and_sizes_are_independent_of_scatter_and_reroll_is_one_undoable_action()
    {
        var editor = Editor(out _); editor.StampDensity = 7; editor.SetStampScalePercent(25);
        editor.StartEditableArea(); editor.SetEditableAreaDensity(65); editor.SetEditableAreaSizeVariation(20);
        editor.AddEditableAreaNode(100, 100); editor.AddEditableAreaNode(900, 100);
        editor.AddEditableAreaNode(900, 700); editor.AddEditableAreaNode(100, 700);
        editor.AdjustStampDensity(1, true); Assert.Equal(75, editor.EditableAreaDensity);
        editor.SetEditableAreaDensity(65); Assert.Equal(7, editor.StampDensity);
        var original = editor.Session.Document; var firstSeed = editor.DisplayedEditableArea!.Seed;
        editor.TryAnotherEditableAreaArrangement();
        Assert.NotEqual(firstSeed, editor.DisplayedEditableArea!.Seed);
        Assert.Same(original, editor.Session.Document); Assert.Equal(0, editor.Session.UndoDepth);
        await editor.PendingEditableAreaCount;
        var predicted = editor.PlannedEditableAreaCount;
        Assert.True(predicted > 0); Assert.Contains($"{predicted:N0}", editor.EditableAreaResult);
        var preview = editor.PlacementPreviewObjects.ToArray();
        Assert.True(editor.FinishEditableArea(), editor.StatusDetail);
        var area = editor.SelectedEditableArea!; var placed = editor.Session.Document;
        Assert.Equal(predicted, area.MemberCount); Assert.True(area.JoinEdges);
        Assert.Equal(65, area.Density); Assert.Equal(20, area.SizeVariationPercent); Assert.Equal(7, editor.StampDensity);
        Assert.All(preview, item => Assert.Equal(item, DocumentEditor.FindObject(placed, item.Id)));
        editor.TryAnotherEditableAreaArrangement(); var changed = editor.Session.Document;
        Assert.Equal(2, editor.Session.UndoDepth); Assert.NotEqual(area.Seed, editor.SelectedEditableArea!.Seed);
        editor.Undo(); Assert.Same(placed, editor.Session.Document); editor.Redo(); Assert.Same(changed, editor.Session.Document);
        editor.SetEditableAreaSizeVariation(0);
        Assert.All(editor.SelectedEditableArea!.Members, m => Assert.Equal(area.Request.BaseWidth,
            DocumentEditor.FindObject(editor.Session.Document, m.ObjectId)!.Width));
        editor.SetStampScalePercent(28); editor.ApplyStampSettingsToEditableArea();
        Assert.Equal(65, editor.SelectedEditableArea!.Density);
        var beforeInvalid = editor.Session.Document; editor.SetEditableAreaDensity(101);
        Assert.Same(beforeInvalid, editor.Session.Document);
        // Loading/selecting an old area keeps its existing objects until the explicit upgrade.
        var layerId = editor.ActiveLayer!.Id; area = editor.SelectedEditableArea;
        editor.Session.Execute(new PlacementAreaCommand(layerId, area.Id, area with { LayoutVersion = 2, Density = 5 }, "legacy fixture"));
        var legacy = editor.Session.Document; editor.SelectEditableArea(area.Id);
        Assert.Same(legacy, editor.Session.Document); Assert.False(editor.UsesNaturalAreaLayout);
        editor.ArrangeEditableAreaNaturally(); Assert.Equal(4, editor.SelectedEditableArea!.LayoutVersion);
        Assert.Equal(65, editor.SelectedEditableArea.Density);
        editor.Undo(); Assert.Same(legacy, editor.Session.Document);
    }

    [Fact]
    public async Task Stage18_counter_drops_obsolete_drafts_and_escape_has_no_history()
    {
        var editor = Editor(out _); editor.SetStampScalePercent(30); editor.StartEditableArea();
        editor.AddEditableAreaNode(100, 100); editor.AddEditableAreaNode(800, 100); editor.AddEditableAreaNode(800, 600);
        var original = editor.Session.Document;
        editor.SetEditableAreaPattern(PlacementAreaPattern.Ridge); editor.SetEditableAreaDensity(35);
        editor.TryAnotherEditableAreaArrangement(); editor.SetEditableAreaDensity(80); editor.SetEditableAreaJoinEdges(false);
        await editor.PendingEditableAreaCount;
        Assert.Equal(PlacementAreaPlanner.Count(editor.DisplayedEditableArea!), editor.PlannedEditableAreaCount);
        Assert.Equal(0, editor.Session.UndoDepth); Assert.Same(original, editor.Session.Document);
        editor.SetEditableAreaDensity(60); editor.HandleEscape(); await editor.PendingEditableAreaCount;
        Assert.False(editor.HasAreaGesture); Assert.Null(editor.PlannedEditableAreaCount);
        Assert.DoesNotContain("Подсчёт", editor.EditableAreaResult); Assert.Same(original, editor.Session.Document);
    }

    [Fact]
    public void Stage18_alpha_measurement_reads_shared_pixels_and_keeps_a_filtering_fringe()
    {
        using var bitmap = new SKBitmap(20, 10, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        for (var y = 2; y < 8; y++) for (var x = 4; x < 16; x++) bitmap.SetPixel(x, y, SKColors.Green);
        using var image = SKImage.FromBitmap(bitmap);
        var measured = Map.Presentation.Services.StampContentMeasurer.Read(image);
        Assert.Equal(new StampContentBounds(.15, .1, .85, .9), measured.Bounds);
        Assert.NotNull(measured.Mask); measured.Mask.Validate();
        Assert.Equal(0u, measured.Mask.Rows[0]); Assert.Equal(0u, measured.Mask.Rows[31]);
        Assert.Equal(0x03ffffc0u, measured.Mask.Rows[16]);
        Assert.Equal(SKColors.Green, bitmap.GetPixel(4, 2)); Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void Stage18_invalid_outline_cannot_place_and_empty_result_explains_density()
    {
        var editor = Editor(out _); editor.StartEditableArea();
        var original = editor.Session.Document;
        editor.AddEditableAreaNode(100, 100); editor.AddEditableAreaNode(400, 100); editor.AddEditableAreaNode(700, 100);
        Assert.False(editor.CanPlaceEditableArea); Assert.False(editor.FinishEditableArea());
        Assert.Same(original, editor.Session.Document); Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(editor.StatusDetail, editor.EditableAreaPlacementHint);
        editor.DeleteEditableAreaNode(); editor.AddEditableAreaNode(400, 500); editor.SetEditableAreaDensity(0);
        Assert.True(editor.CanPlaceEditableArea); Assert.True(editor.FinishEditableArea(), editor.StatusDetail);
        Assert.Equal(1, editor.Session.UndoDepth); Assert.Equal(0, editor.SelectedEditableArea!.MemberCount);
        Assert.Contains("плотность равна 0", editor.EditableAreaPlacementHint);
        editor.Undo(); Assert.Same(original, editor.Session.Document);
    }

    [Fact]
    public async Task Stage18_temporary_preview_memory_refusal_keeps_place_retry_available_without_redrawing()
    {
        const long gib = 1024L * 1024 * 1024;
        var available = 512L * 1024 * 1024;
        var memory = new Map.Core.Resources.WorkingMemoryBudget(() => new(16 * gib, available, 100 * 1024 * 1024));
        var editor = Editor(out _, memory); editor.StampDensity = 20; editor.SetStampScalePercent(129); editor.StartEditableArea();
        var original = editor.Session.Document;
        editor.AddEditableAreaNode(100, 100); editor.AddEditableAreaNode(900, 100); editor.AddEditableAreaNode(500, 670);
        Assert.Empty(editor.PlacementPreviewObjects); Assert.True(editor.CanPlaceEditableArea);
        Assert.Contains("МиБ", editor.EditableAreaPlacementHint); Assert.Contains("повторить попытку", editor.EditableAreaPlacementHint);
        Assert.False(editor.FinishEditableArea()); Assert.Same(original, editor.Session.Document); Assert.Equal(0, editor.Session.UndoDepth);
        available = gib;
        Assert.True(editor.FinishEditableArea(), editor.StatusDetail);
        Assert.True(editor.SelectedEditableArea!.MemberCount > 0); Assert.Equal(1, editor.Session.UndoDepth);
        // Placement cancels the counter; its in-flight reservation ends when that task finishes.
        await editor.PendingEditableAreaCount;
        Assert.Equal(0, memory.ReservedBytes);
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

    private EditorViewModel Editor(out AssetCatalog catalog, Map.Core.Resources.WorkingMemoryBudget? memory = null)
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
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog, memory: memory);
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
