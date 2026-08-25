using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public class EditorViewModelTests
{
    private static (EditorViewModel Editor, FakeProjectStore Store, FakeRecentMaps Recent,
        FakeFilePicker Picker, FakePrompts Prompts) NewEditor(string? path = null, bool startDirty = true)
    {
        var store = new FakeProjectStore();
        var recent = new FakeRecentMaps();
        var picker = new FakeFilePicker();
        var prompts = new FakePrompts();

        var editor = new EditorViewModel(
            TestData.Document(), path, startDirty,
            store, recent, picker, prompts, new FakeLog(),
            clock: () => new DateTimeOffset(2026, 8, 25, 15, 0, 0, TimeSpan.Zero));

        return (editor, store, recent, picker, prompts);
    }

    [Fact]
    public void A_new_document_starts_unsaved_and_add_object_keeps_it_dirty()
    {
        var (editor, _, _, _, _) = NewEditor();
        Assert.Equal(SaveState.Unsaved, editor.SaveState);

        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.Equal(SaveState.Unsaved, editor.SaveState);
        Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects);
        Assert.NotNull(editor.Session.SelectedObjectId);
    }

    [Fact]
    public async Task Save_success_marks_saved_touches_recent_and_stores_a_real_thumbnail()
    {
        var (editor, store, recent, picker, _) = NewEditor();
        picker.NextSavePath = @"C:\maps\one.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);

        Assert.True(await editor.SaveAsync());

        Assert.Equal(SaveState.Saved, editor.SaveState);
        Assert.Equal("Сохранено", editor.SaveStateText);
        Assert.False(editor.Session.IsDirty);
        Assert.Equal(@"C:\maps\one.mapproj", editor.DocumentPath);

        var (Document, Metadata, Preview) = store.Saved[@"C:\maps\one.mapproj"];
        Assert.Equal(editor.Session.Document.Id, Document.Id);

        // The preview is a real PNG from the renderer, not placeholder bytes.
        Assert.True(Preview.Length > 100);
        Assert.Equal(0x89, Preview[0]);
        Assert.Equal((byte)'P', Preview[1]);

        Assert.Single(recent.Items);
        Assert.Equal(@"C:\maps\one.mapproj", recent.Items[0].Path);
    }

    [Fact]
    public async Task Save_failure_keeps_the_document_dirty_and_shows_the_recovery_path()
    {
        var (editor, store, _, picker, _) = NewEditor();
        picker.NextSavePath = @"C:\maps\fail.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);

        store.NextSaveFailures.Enqueue(new MapProjectSaveException(
            "did not complete", @"C:\maps\fail.mapproj", @"C:\maps\fail.mapproj.tmp-x", new IOException()));

        Assert.False(await editor.SaveAsync());

        Assert.Equal(SaveState.SaveFailed, editor.SaveState);
        Assert.True(editor.Session.IsDirty);
        Assert.Contains("recovery сохранён здесь", editor.StatusDetail);
        Assert.Contains(".tmp-x", editor.StatusDetail);

        // A later retry can still succeed.
        Assert.True(await editor.SaveAsync());
        Assert.Equal(SaveState.Saved, editor.SaveState);
    }

    [Fact]
    public async Task An_edit_during_a_background_save_keeps_the_document_dirty()
    {
        var (editor, _, _, picker, _) = NewEditor();
        picker.NextSavePath = @"C:\maps\during.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);
        var task = editor.SaveAsync();

        // This lands after the snapshot/revision were captured, regardless of scheduling.
        editor.AddObject(SceneObjectKind.Ellipse);
        Assert.True(await task);

        Assert.True(editor.Session.IsDirty);
        Assert.Equal(SaveState.Unsaved, editor.SaveState);
    }

    [Fact]
    public async Task Close_flow_honours_save_dontsave_and_cancel()
    {
        var (editor, _, _, picker, prompts) = NewEditor();
        picker.NextSavePath = @"C:\maps\close.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);

        prompts.NextCloseChoice = CloseChoice.Cancel;
        Assert.False(await editor.ConfirmCloseAsync());
        Assert.True(editor.Session.IsDirty);

        prompts.NextCloseChoice = CloseChoice.Save;
        Assert.True(await editor.ConfirmCloseAsync());
        Assert.False(editor.Session.IsDirty);

        editor.AddObject(SceneObjectKind.Ellipse);
        prompts.NextCloseChoice = CloseChoice.DontSave;
        Assert.True(await editor.ConfirmCloseAsync());
        Assert.Equal(3, prompts.CloseAsked);

        // A clean document closes without asking.
        var (cleanEditor, _, _, _, cleanPrompts) = NewEditor(startDirty: false);
        Assert.True(await cleanEditor.ConfirmCloseAsync());
        Assert.Equal(0, cleanPrompts.CloseAsked);
    }

    [Fact]
    public void Inspector_commit_is_one_history_entry_and_rejects_nonsense()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var depthAfterAdd = editor.Session.UndoDepth;

        editor.XText = "500";
        editor.YText = "400";
        editor.WidthText = "300";
        editor.HeightText = "200";
        editor.RotationText = "30";
        Assert.True(editor.CommitInspector());

        Assert.Equal(depthAfterAdd + 1, editor.Session.UndoDepth);
        var transformed = editor.Session.SelectedObject!;
        Assert.Equal(500, transformed.X);
        Assert.Equal(30, transformed.RotationDegrees);

        // Nonsense is refused with a message and without a history entry.
        editor.WidthText = "-5";
        Assert.False(editor.CommitInspector());
        Assert.NotEqual("", editor.InspectorError);
        Assert.Equal(depthAfterAdd + 1, editor.Session.UndoDepth);

        // Committing identical values adds nothing either.
        editor.RefreshInspector();
        Assert.True(editor.CommitInspector());
        Assert.Equal(depthAfterAdd + 1, editor.Session.UndoDepth);
    }

    [Fact]
    public void A_drag_is_one_history_entry_and_a_locked_object_cannot_be_dragged()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var depthAfterAdd = editor.Session.UndoDepth;
        var before = editor.Session.SelectedObject!;

        Assert.True(editor.BeginDrag());
        editor.UpdateDrag(10, 5);
        editor.UpdateDrag(60, 45);
        editor.UpdateDrag(100, 80); // many previews...
        editor.EndDrag();           // ...one command

        Assert.Equal(depthAfterAdd + 1, editor.Session.UndoDepth);
        Assert.Equal(before.X + 100, editor.Session.SelectedObject!.X);

        editor.Undo();
        Assert.Equal(before.X, editor.Session.SelectedObject!.X);

        // Lock the object through a direct snapshot edit, then try to drag it.
        var locked = new SceneObject(
            before.Id, before.Kind, before.Name, before.X, before.Y, before.Width, before.Height,
            before.RotationDegrees, before.FillColor, before.StrokeColor, before.StrokeWidth,
            visible: true, locked: true);
        editor.Session.Execute(new Map.Core.Editing.TransformObjectCommand(before, locked));

        Assert.False(editor.BeginDrag());
        Assert.False(editor.HasEditableSelection);
        Assert.False(editor.CommitInspector());
    }

    [Fact]
    public void Drag_preview_renders_without_touching_the_session_snapshot()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var before = editor.Session.SelectedObject!;

        editor.BeginDrag();
        editor.UpdateDrag(50, 50);

        Assert.Equal(before.X, editor.Session.SelectedObject!.X); // snapshot untouched
        var previewObject = editor.RenderDocument.Layers.OfType<ObjectLayer>()
            .Single().Objects.Single(o => o.Id == before.Id);
        Assert.Equal(before.X + 50, previewObject.X); // but the canvas sees the preview

        editor.CancelDrag();
        Assert.Same(editor.Session.Document, editor.RenderDocument);
    }

    [Fact]
    public void A_handle_resize_drag_is_one_history_entry_with_the_anchor_pinned()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var before = editor.Session.SelectedObject!;
        var depth = editor.Session.UndoDepth;

        // Grab the bottom-right handle exactly where the gizmo puts it, then drag outward.
        var (Kind, X, Y) = Map.Core.Editing.SelectionHandles
            .Compute(before, EditorViewModel.RotationHandleOffset(1.0))
            .Single(h => h.Kind == Map.Core.Editing.HandleKind.BottomRight);

        Assert.True(editor.BeginCanvasInteraction(X, Y, 1.0));
        editor.UpdateDragTo(X + 50, Y + 30);
        editor.UpdateDragTo(X + 100, Y + 60); // many previews...
        editor.EndDrag();                                    // ...one command

        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var after = editor.Session.SelectedObject!;
        Assert.Equal(before.Width + 100, after.Width, 6);
        Assert.Equal(before.Height + 60, after.Height, 6);
        Assert.Equal(before.X, after.X, 6); // opposite corner pinned
        Assert.Equal(before.Y, after.Y, 6);

        editor.Undo();
        Assert.Equal(before.Width, editor.Session.SelectedObject!.Width, 6);
    }

    [Fact]
    public void A_rotation_handle_drag_is_one_history_entry_and_only_rotates()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var before = editor.Session.SelectedObject!;
        var depth = editor.Session.UndoDepth;

        var (Kind, X, Y) = Map.Core.Editing.SelectionHandles
            .Compute(before, EditorViewModel.RotationHandleOffset(1.0))
            .Single(h => h.Kind == Map.Core.Editing.HandleKind.Rotate);

        Assert.True(editor.BeginCanvasInteraction(X, Y, 1.0));

        // Drag the handle from above the centre to the right of the centre: +90 degrees.
        var centreX = before.X + (before.Width / 2);
        var centreY = before.Y + (before.Height / 2);
        editor.UpdateDragTo(centreX + 40, centreY - 40);
        editor.UpdateDragTo(centreX + 80, centreY);
        editor.EndDrag();

        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        var after = editor.Session.SelectedObject!;
        Assert.Equal(90, after.RotationDegrees, 1);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Width, after.Width, 6);
    }

    [Fact]
    public void A_press_outside_the_gizmo_still_selects_and_moves_as_before()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var before = editor.Session.SelectedObject!;
        var centreX = before.X + (before.Width / 2);
        var centreY = before.Y + (before.Height / 2);

        Assert.True(editor.BeginCanvasInteraction(centreX, centreY, 1.0));
        editor.UpdateDragTo(centreX + 70, centreY + 20);
        editor.EndDrag();

        Assert.Equal(before.X + 70, editor.Session.SelectedObject!.X, 6);
        Assert.Equal(before.Y + 20, editor.Session.SelectedObject!.Y, 6);
    }

    [Fact]
    public async Task Cancelled_save_as_changes_nothing()
    {
        var (editor, store, _, picker, _) = NewEditor();
        picker.NextSavePath = null;
        editor.AddObject(SceneObjectKind.Rectangle);

        Assert.False(await editor.SaveAsync());
        Assert.Equal(SaveState.Unsaved, editor.SaveState);
        Assert.Empty(store.Saved);
        Assert.Null(editor.DocumentPath);
    }

    [Fact]
    public void Stage6_vector_tools_edit_nodes_portals_boolean_and_free_draw_as_commands()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddLine();
        var lineId = editor.Session.SelectedObjectId;
        editor.AddNodeToSelectedLine();
        editor.ToggleSelectedNodeCurve();
        editor.AddPortalToSelected();
        editor.MoveSelectedPortal();
        editor.CycleSelectedVectorStroke();
        var line = DocumentEditor.FindObject(editor.Session.Document, lineId!.Value)!;
        Assert.Equal(4, line.Line!.Nodes.Count);
        Assert.Single(line.Line.Portals);
        Assert.Equal(12, line.Line.Stroke.Offset);

        editor.AddShape(ShapePrimitiveKind.Rectangle);
        editor.AddShape(ShapePrimitiveKind.Ellipse);
        editor.ApplyShapeBoolean(ShapeBooleanOperation.Subtract);
        Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects, o => o.Shape is not null);

        var depth = editor.Session.UndoDepth;
        editor.ActiveTool = EditorTool.Line;
        Assert.True(editor.BeginVectorDraw(10, 10));
        editor.ContinueVectorDraw(50, 30);
        editor.ContinueVectorDraw(100, 80);
        editor.EndVectorDraw();
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.Equal(SceneObjectKind.Line, editor.Session.SelectedObject!.Kind);
    }
}
