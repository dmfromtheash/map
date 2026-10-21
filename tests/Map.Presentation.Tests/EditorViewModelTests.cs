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
        FakeFilePicker Picker, FakePrompts Prompts) NewEditor(string? path = null, bool startDirty = true,
            Map.Core.Resources.WorkingMemoryBudget? memory = null)
    {
        var store = new FakeProjectStore();
        var recent = new FakeRecentMaps();
        var picker = new FakeFilePicker();
        var prompts = new FakePrompts();

        var editor = new EditorViewModel(
            TestData.Document(), path, startDirty,
            store, recent, picker, prompts, new FakeLog(),
            clock: () => new DateTimeOffset(2026, 8, 25, 15, 0, 0, TimeSpan.Zero), memory: memory);

        return (editor, store, recent, picker, prompts);
    }

    [Fact]
    public void Stage7_text_grid_snap_notes_and_shortcuts_are_available_from_native_view_model()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);
        editor.AddText();
        Assert.Equal(SceneObjectKind.Text, editor.Session.SelectedObject!.Kind);
        editor.CycleSelectedTextStyle();
        Assert.True(editor.Session.SelectedObject.Text!.OutlineWidth > 0);

        editor.AddGridLayer();
        editor.CycleActiveGridGeometry();
        editor.ToggleActiveGridExport();
        Assert.IsType<GridLayer>(editor.ActiveLayer);
        Assert.True(((GridLayer)editor.ActiveLayer).Grids[0].IncludeInExport);

        editor.AddNoteForSelection();
        Assert.Single(editor.SearchNotes("О "));
        editor.Shortcuts.Set("text", "Alt+T");
        Assert.Equal("Alt+T", editor.Shortcuts.Get("text"));
    }

    [Fact]
    public void Stage8_two_editors_share_editable_composition_and_explicit_flatten_is_undoable()
    {
        var (first, _, _, _, _) = NewEditor();
        var (second, _, _, _, _) = NewEditor();
        first.Session.Viewport.Resize(800, 600); second.Session.Viewport.Resize(800, 600);
        first.AddObject(SceneObjectKind.Ellipse);
        var originalId = first.Session.SelectedObjectId;
        first.CreateEditableComposition();
        Assert.Equal(SceneObjectKind.Composition, first.Session.SelectedObject!.Kind);
        first.CopySelectedComposition();

        second.PasteComposition();
        Assert.Equal(SceneObjectKind.Composition, second.Session.SelectedObject!.Kind);
        Assert.NotEqual(first.Session.SelectedObjectId, second.Session.SelectedObjectId);
        Assert.NotEqual(originalId, second.Session.SelectedObject.Composition!.Items.Single().Id);

        first.FlattenSelectedComposition();
        Assert.Equal(SceneObjectKind.Stamp, first.Session.SelectedObject!.Kind);
        first.Undo();
        Assert.Equal(SceneObjectKind.Composition, first.Session.SelectedObject!.Kind);

        second.AddFilterLayer(); second.ToggleActiveMask(); second.CycleActiveFilter();
        Assert.NotNull(((FilterLayer)second.ActiveLayer!).Mask);
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
    public async Task Autosave_protects_the_snapshot_without_claiming_the_main_file_is_saved()
    {
        var (editor, store, _, _, _) = NewEditor(path: @"C:\maps\source.mapproj");
        editor.AddObject(SceneObjectKind.Rectangle);
        var pending = editor.PendingChanges;

        Assert.True(await editor.TryAutosaveAsync());

        var autosave = Assert.Single(store.Autosaves);
        Assert.Same(editor.Session.Document, autosave.Document);
        Assert.Equal(@"C:\maps\source.mapproj", autosave.TargetPath);
        Assert.True(autosave.Preview.Length > 100);
        Assert.Equal(pending, editor.PendingChanges);
        Assert.True(editor.Session.IsDirty);
        Assert.Equal(SaveState.Unsaved, editor.SaveState);
        Assert.Equal(AutosaveState.Saved, editor.AutosaveState);
        Assert.StartsWith("Есть восстановление", editor.AutosaveSummary, StringComparison.Ordinal);
        Assert.Equal(@"C:\maps\source.mapproj", editor.DocumentPath);

        Assert.False(await editor.TryAutosaveAsync());
        Assert.Single(store.Autosaves);
    }

    [Fact]
    public async Task Autosave_failure_is_separate_from_manual_save_and_the_next_tick_can_retry()
    {
        var (editor, store, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        store.NextAutosaveFailures.Enqueue(new IOException("no space"));

        Assert.False(await editor.TryAutosaveAsync());
        Assert.Equal(AutosaveState.Failed, editor.AutosaveState);
        Assert.Equal(SaveState.Unsaved, editor.SaveState);
        Assert.True(editor.Session.IsDirty);

        Assert.True(await editor.TryAutosaveAsync());
        Assert.Equal(AutosaveState.Saved, editor.AutosaveState);
        Assert.Single(store.Autosaves);
    }

    [Fact]
    public async Task Returning_to_the_saved_state_clears_a_stale_autosave_without_writing_a_clean_one()
    {
        var (editor, store, _, _, _) = NewEditor(startDirty: false);
        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.True(await editor.TryAutosaveAsync());

        editor.Undo();
        Assert.False(editor.Session.IsDirty);
        Assert.False(await editor.TryAutosaveAsync());

        Assert.Equal([editor.Session.Document.Id.Value], store.ClearedRecovery);
        Assert.Equal(AutosaveState.Idle, editor.AutosaveState);
        Assert.Single(store.Autosaves);
    }

    [Fact]
    public async Task Clean_close_after_undo_waits_for_stale_autosave_cleanup()
    {
        var (editor, store, _, _, _) = NewEditor(startDirty: false);
        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.True(await editor.TryAutosaveAsync());
        editor.Undo();

        Assert.True(await editor.ConfirmCloseAsync());

        Assert.Equal([editor.Session.Document.Id.Value], store.ClearedRecovery);
        Assert.Equal(AutosaveState.Idle, editor.AutosaveState);
    }

    [Fact]
    public async Task Dont_save_is_an_explicit_recovery_discard()
    {
        var (editor, store, _, _, prompts) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.True(await editor.TryAutosaveAsync());
        prompts.NextCloseChoice = CloseChoice.DontSave;

        Assert.True(await editor.ConfirmCloseAsync());
        Assert.Equal([editor.Session.Document.Id.Value], store.ClearedRecovery);
        Assert.True(editor.Session.IsDirty);
    }

    [Fact]
    public async Task Autosave_tick_is_skipped_while_a_manual_save_owns_the_gate()
    {
        var (editor, store, _, picker, _) = NewEditor();
        picker.NextSavePath = @"C:\maps\manual-wins.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.ManualSaveEntered = entered;
        store.ManualSaveRelease = release;

        var manualSave = editor.SaveAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(await editor.TryAutosaveAsync());
        Assert.Empty(store.Autosaves);

        release.Set();
        Assert.True(await manualSave);
        Assert.Equal(SaveState.Saved, editor.SaveState);
    }

    [Fact]
    public async Task Autosave_returns_control_before_blocking_persistence_and_stays_under_100_ms()
    {
        var (editor, store, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.AutosaveEntered = entered;
        store.AutosaveRelease = release;

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var autosave = editor.TryAutosaveAsync();
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(100),
            $"TryAutosaveAsync blocked the caller for {timer.Elapsed.TotalMilliseconds:F3} ms.");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(autosave.IsCompleted);

        release.Set();
        Assert.True(await autosave);
    }

    [Fact]
    public async Task Save_failure_keeps_the_document_dirty_and_shows_the_recovery_path()
    {
        var (editor, store, _, picker, _) = NewEditor(memory: new(() => new(16L << 30, 8L << 30, 0)));
        picker.NextSavePath = @"C:\maps\fail.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);

        store.NextSaveFailures.Enqueue(new MapProjectSaveException(
            "did not complete", @"C:\maps\fail.mapproj", @"C:\maps\fail.mapproj.tmp-x", new IOException()));

        Assert.False(await editor.SaveAsync());

        Assert.Equal(SaveState.SaveFailed, editor.SaveState);
        Assert.True(editor.Session.IsDirty);
        Assert.Contains("копия для восстановления сохранена здесь", editor.StatusDetail);
        Assert.Contains(".tmp-x", editor.StatusDetail);

        // A later retry can still succeed.
        Assert.True(await editor.SaveAsync());
        Assert.Equal(SaveState.Saved, editor.SaveState);
    }

    [Fact]
    public async Task Save_resource_refusal_preserves_document_history_and_refuses_close_until_retry()
    {
        var memory = new Map.Core.Resources.WorkingMemorySnapshot(16L << 30, 8L << 30, 0);
        var (editor, store, recent, picker, prompts) = NewEditor(memory: new(() => memory));
        picker.NextSavePath = @"C:\maps\resource-refused.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);
        var snapshot = editor.Session.Document; var depth = editor.Session.UndoDepth;
        const string reason = "Недостаточно свободной памяти с резервом для Windows и открытых карт.";
        // Exercise the real admission before any thumbnail or store write, then restore headroom.
        // 512 MiB is no longer a refusal for a map of one rectangle: a save that measured itself
        // owes a reserve proportional to its own size (see Stage19CorrectionTests). The contract
        // this test guards - a refused save changes nothing at all - is proven where the machine
        // really has nothing left.
        memory = memory with { AvailableBytes = 32L << 20 };
        Assert.False(await editor.SaveAsync());
        Assert.Same(snapshot, editor.Session.Document); Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.True(editor.Session.IsDirty); Assert.Equal(SaveState.SaveFailed, editor.SaveState);
        Assert.Contains(reason, editor.StatusDetail); Assert.Null(editor.DocumentPath);
        Assert.Empty(store.Saved); Assert.Empty(recent.Items); Assert.Equal(0, editor.ActiveResourceOperations);
        prompts.NextCloseChoice = CloseChoice.Save;
        Assert.False(await editor.ConfirmCloseAsync()); Assert.Same(snapshot, editor.Session.Document);
        memory = memory with { AvailableBytes = 1L << 30 };
        Assert.True(await editor.SaveAsync()); Assert.False(editor.Session.IsDirty);
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
    public async Task Undo_to_the_saved_checkpoint_is_clean_and_redo_is_dirty_again()
    {
        var (editor, _, _, picker, _) = NewEditor(startDirty: false);
        picker.NextSavePath = @"C:\maps\undo-save.mapproj";
        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.True(await editor.SaveAsync());

        editor.AddObject(SceneObjectKind.Ellipse);
        Assert.Equal(1, editor.PendingChanges);
        editor.Undo();
        Assert.False(editor.Session.IsDirty);
        Assert.Equal(SaveState.Saved, editor.SaveState);
        Assert.Equal(0, editor.PendingChanges);

        editor.Redo();
        Assert.True(editor.Session.IsDirty);
        Assert.Equal(SaveState.Unsaved, editor.SaveState);
        Assert.Equal(1, editor.PendingChanges);
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
    public void Geometry_fields_and_direct_transforms_land_on_whole_values()
    {
        var (editor, _, _, _, _) = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var original = editor.Session.SelectedObject!;
        var fractional = new SceneObject(
            original.Id, original.Kind, original.Name,
            original.X + 0.4, original.Y + 0.6, original.Width + 0.4, original.Height + 0.6,
            12.6, original.FillColor, original.StrokeColor, original.StrokeWidth,
            original.Visible, original.Locked);
        editor.Session.Execute(new TransformObjectCommand(original, fractional));
        editor.RefreshInspector();

        Assert.Equal(
            Math.Round(fractional.X, MidpointRounding.AwayFromZero)
                .ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            editor.XText);
        Assert.DoesNotContain('.', editor.XText);
        Assert.DoesNotContain('.', editor.YText);
        Assert.DoesNotContain('.', editor.WidthText);
        Assert.DoesNotContain('.', editor.HeightText);
        Assert.DoesNotContain('.', editor.RotationText);

        var depth = editor.Session.UndoDepth;
        editor.XText = "123.5";
        Assert.False(editor.CommitInspector());
        Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Contains("целые числа", editor.InspectorError, StringComparison.Ordinal);

        editor.RefreshInspector();
        Assert.True(editor.CommitInspector());
        Assert.Equal(fractional, editor.Session.SelectedObject);
        Assert.Equal(depth, editor.Session.UndoDepth);

        var beforeDrag = editor.Session.SelectedObject!;
        Assert.True(editor.BeginDrag());
        editor.UpdateDrag(10.4, 5.6);
        editor.EndDrag();
        var afterDrag = editor.Session.SelectedObject!;
        Assert.Equal(Math.Round(beforeDrag.X + 10.4, MidpointRounding.AwayFromZero), afterDrag.X);
        Assert.Equal(Math.Round(beforeDrag.Y + 5.6, MidpointRounding.AwayFromZero), afterDrag.Y);
        Assert.Equal(fractional.Width, afterDrag.Width);
        Assert.Equal(fractional.Height, afterDrag.Height);
        Assert.Equal(fractional.RotationDegrees, afterDrag.RotationDegrees);

        editor.RotateSelectedObject(1, fineStep: true);
        Assert.Equal(14, editor.Session.SelectedObject!.RotationDegrees);
        Assert.Equal(fractional.Width, editor.Session.SelectedObject.Width);
        Assert.Equal(fractional.Height, editor.Session.SelectedObject.Height);
        editor.Undo();
        Assert.Equal(afterDrag, editor.Session.SelectedObject);
        editor.Undo();
        Assert.Equal(fractional, editor.Session.SelectedObject);
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

        Assert.Equal(CanvasGesture.Drag, editor.BeginCanvasInteraction(X, Y, 1.0));
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

        Assert.Equal(CanvasGesture.Drag, editor.BeginCanvasInteraction(X, Y, 1.0));

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

        Assert.Equal(CanvasGesture.Drag, editor.BeginCanvasInteraction(centreX, centreY, 1.0));
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
        var firstShape = editor.Session.SelectedObjectId!.Value;
        editor.AddShape(ShapePrimitiveKind.Ellipse);
        editor.SelectObject(firstShape, additive: true);
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
