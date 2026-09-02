using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Regressions for the canvas input contract itself.
/// <para>
/// The reported defects all lived in routing, not in any single editor method: a press with the
/// Shape tool active never reached a hit-test, a finished gesture left an object selected but
/// invisible in the panel, and no tool switch or capture loss ever cleared the gesture flags.
/// Every existing test called ViewModel methods directly, so all of that stayed green. These
/// tests go through <see cref="CanvasInputRouter"/> - the same production type the Avalonia
/// control now delegates to - so a press is routed here exactly as it is by a real pointer.
/// </para>
/// </summary>
public sealed class EditorIntegrityInputRoutingTests
{
    [Fact]
    public void Shape_tool_click_on_an_existing_shape_selects_that_object()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.ActiveTool = EditorTool.Shape;
        Assert.Empty(editor.Session.SelectedObjectIds);

        var revision = editor.Session.Revision;
        var (x, y) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, x, y);
        router.Release(x, y);

        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);
        Assert.Equal(SceneObjectKind.Rectangle, editor.SelectionKind);

        // Selecting is not an edit: the click must not add an object or a history entry.
        Assert.Equal(revision, editor.Session.Revision);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Single(ObjectsOf(editor));
    }

    [Fact]
    public void Shape_tool_click_on_empty_canvas_clears_the_selection_and_writes_no_history()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.SelectObject(shape.Id);
        editor.ActiveTool = EditorTool.Shape;
        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);

        var revision = editor.Session.Revision;
        var (x, y) = Screen(editor, 700, 600);
        router.Press(CanvasPointerButton.Left, x, y);
        router.Release(x, y);

        Assert.Empty(editor.Session.SelectedObjectIds);
        Assert.Equal(revision, editor.Session.Revision);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Single(ObjectsOf(editor));
    }

    [Fact]
    public void Shape_tool_drag_still_draws_and_the_new_shape_becomes_the_selected_object()
    {
        var editor = Editor();
        var router = Router(editor);
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Rectangle;

        var (fromX, fromY) = Screen(editor, 120, 120);
        var (toX, toY) = Screen(editor, 320, 260);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Move(toX, toY);
        router.Release(toX, toY);

        var created = Assert.Single(ObjectsOf(editor));
        Assert.Equal(SceneObjectKind.Shape, created.Kind);
        Assert.Equal(created.Id, editor.Session.SelectedObjectId);
        Assert.Equal(1, editor.Session.UndoDepth);

        // The panel has to show that object even though the Shape tool is still the active one.
        Assert.Equal(
            EditorContextPanelKind.ObjectInspector,
            EditorContextPanel.Resolve(editor.ActiveTool, editor.SelectionCount, true, false));
    }

    [Fact]
    public void Line_tool_click_selects_and_line_tool_drag_draws()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.ActiveTool = EditorTool.Line;

        var (x, y) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, x, y);
        router.Release(x, y);
        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);
        Assert.Single(ObjectsOf(editor));

        var (toX, toY) = Screen(editor, 420, 380);
        router.Press(CanvasPointerButton.Left, x, y);
        router.Move(toX, toY);
        router.Release(toX, toY);
        Assert.Equal(2, ObjectsOf(editor).Count);
        Assert.Equal(SceneObjectKind.Line, ObjectsOf(editor)[^1].Kind);
    }

    [Fact]
    public void A_selection_handle_still_transforms_while_a_drawing_tool_is_active()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.SelectObject(shape.Id);
        editor.ActiveTool = EditorTool.Shape;

        // The bottom-right gizmo handle of the selection, in world units.
        var (grabX, grabY) = Screen(editor, 300, 260);
        var (dropX, dropY) = Screen(editor, 360, 320);
        router.Press(CanvasPointerButton.Left, grabX, grabY);
        router.Move(dropX, dropY);
        router.Release(dropX, dropY);

        var after = Assert.Single(ObjectsOf(editor));
        Assert.Equal(shape.Id, after.Id);
        Assert.True(after.Width > shape.Width, "the handle grab must resize, not draw a new shape");
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Switching_tools_mid_gesture_leaves_nothing_that_can_commit_later()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.SelectObject(shape.Id);

        var (fromX, fromY) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        var (toX, toY) = Screen(editor, 260, 210);
        router.Move(toX, toY);
        Assert.NotNull(editor.DragPreview);

        // The window-level hotkey fires even though the pointer is still down.
        editor.ActiveTool = EditorTool.Shape;
        Assert.Null(editor.DragPreview);
        Assert.Equal(CanvasInputGesture.None, router.ActiveGesture);

        router.Release(toX, toY);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(shape.X, Assert.Single(ObjectsOf(editor)).X);
    }

    [Fact]
    public void Losing_the_pointer_capture_abandons_the_drag_without_history()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.SelectObject(shape.Id);

        var (fromX, fromY) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Move(fromX + 60, fromY + 40);
        Assert.True(router.Cancel());

        Assert.Null(editor.DragPreview);
        Assert.Equal(CanvasInputGesture.None, router.ActiveGesture);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(shape.X, Assert.Single(ObjectsOf(editor)).X);
    }

    [Fact]
    public void One_release_ends_exactly_one_gesture()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.SelectObject(shape.Id);

        var (fromX, fromY) = Screen(editor, 150, 130);
        var (toX, toY) = Screen(editor, 250, 220);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Move(toX, toY);
        router.Release(toX, toY);
        Assert.Equal(1, editor.Session.UndoDepth);

        // A second release with no press behind it must do nothing at all.
        router.Release(toX, toY);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Hit_testing_after_a_move_follows_the_object_to_its_new_place()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.SelectObject(shape.Id);

        var (fromX, fromY) = Screen(editor, 150, 130);
        var (toX, toY) = Screen(editor, 550, 430);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Move(toX, toY);
        router.Release(toX, toY);

        editor.ActiveTool = EditorTool.Shape;
        editor.Session.ClearSelection();

        // The old position is now empty, the new one holds the object - both through the router.
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Release(fromX, fromY);
        Assert.Empty(editor.Session.SelectedObjectIds);

        router.Press(CanvasPointerButton.Left, toX, toY);
        router.Release(toX, toY);
        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);

        editor.Undo();
        editor.Session.ClearSelection();
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Release(fromX, fromY);
        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);
    }

    [Fact]
    public void A_locked_object_is_selectable_but_says_why_it_will_not_move()
    {
        var shape = Rectangle(100, 100, 200, 160, locked: true);
        var editor = Editor(shape);
        var router = Router(editor);

        var (fromX, fromY) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Move(fromX + 90, fromY + 70);
        router.Release(fromX + 90, fromY + 70);

        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);
        Assert.Contains("заблокирован", editor.StatusDetail);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(shape.X, Assert.Single(ObjectsOf(editor)).X);
    }

    [Fact]
    public void An_armed_placement_commits_once_per_press_and_the_object_is_selected()
    {
        var editor = Editor();
        var router = Router(editor);
        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        Assert.True(editor.HasPendingPlacement);
        Assert.Empty(ObjectsOf(editor));

        var (x, y) = Screen(editor, 300, 240);
        router.Move(x, y);
        Assert.Empty(ObjectsOf(editor));
        Assert.Equal(0, editor.Session.UndoDepth);

        router.Press(CanvasPointerButton.Left, x, y);
        router.Release(x, y);

        var created = Assert.Single(ObjectsOf(editor));
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Equal(created.Id, editor.Session.SelectedObjectId);
        Assert.False(editor.HasPendingPlacement);
    }

    [Fact]
    public void A_secondary_press_cancels_a_pending_placement_without_touching_the_document()
    {
        var editor = Editor();
        var router = Router(editor);
        editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);
        var (x, y) = Screen(editor, 260, 200);
        router.Move(x, y);

        var result = router.Press(CanvasPointerButton.Right, x, y);

        Assert.True(result.Handled);
        Assert.False(editor.HasPendingPlacement);
        Assert.Empty(ObjectsOf(editor));
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void The_notes_tool_anchors_a_note_where_the_user_pointed()
    {
        var editor = Editor();
        var router = Router(editor);
        editor.ActiveTool = EditorTool.Notes;

        var (x, y) = Screen(editor, 420, 300);
        router.Press(CanvasPointerButton.Left, x, y);
        router.Release(x, y);

        var note = Assert.Single(editor.Notes);
        Assert.Equal(NoteAnchorKind.World, note.AnchorKind);
        Assert.Equal(420, note.WorldX, 3);
        Assert.Equal(300, note.WorldY, 3);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void A_press_that_starts_no_tool_gesture_still_reaches_selection()
    {
        // A locked object layer refuses a new shape. The press used to be swallowed whole, which
        // left the existing objects unreachable; it must fall through to plain selection instead.
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape, lockedLayer: true);
        var router = Router(editor);
        editor.ActiveTool = EditorTool.Shape;

        var (x, y) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, x, y);
        router.Release(x, y);

        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);
        Assert.Empty(editor.PlacementPreviewObjects);
        Assert.Contains("слоя объектов", editor.StatusDetail);
    }

    // ----------------------------------------------------------------- escape

    [Fact]
    public void Escape_abandons_a_running_gesture_and_the_router_forgets_it_too()
    {
        var editor = Editor();
        var router = Router(editor);
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Rectangle;

        var (fromX, fromY) = Screen(editor, 120, 120);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Move(fromX + 120, fromY + 90);
        Assert.Equal(CanvasInputGesture.VectorDraw, router.ActiveGesture);

        Assert.True(editor.HandleEscape());
        Assert.Equal(CanvasInputGesture.None, router.ActiveGesture);
        Assert.Empty(editor.PlacementPreviewObjects);

        // The release that arrives afterwards must not resurrect the abandoned gesture.
        router.Release(fromX + 120, fromY + 90);
        Assert.Empty(ObjectsOf(editor));
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void Escape_walks_back_to_plain_pointer_control_one_step_at_a_time()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.ActiveTool = EditorTool.Shape;
        editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);

        // 1: the armed placement.
        Assert.True(editor.HandleEscape());
        Assert.False(editor.HasPendingPlacement);

        // 2: the selection.
        editor.SelectObject(shape.Id);
        editor.ActiveTool = EditorTool.Shape;
        Assert.True(editor.HandleEscape());
        Assert.Empty(editor.Session.SelectedObjectIds);

        // 3: the drawing tool itself - after this the pointer plainly selects again.
        Assert.True(editor.HandleEscape());
        Assert.Equal(EditorTool.Select, editor.ActiveTool);
        Assert.False(editor.HandleEscape());

        var (x, y) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, x, y);
        router.Release(x, y);
        Assert.Equal(shape.Id, editor.Session.SelectedObjectId);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void Escape_abandons_a_drag_without_leaving_the_move_behind()
    {
        var shape = Rectangle(100, 100, 200, 160);
        var editor = Editor(shape);
        var router = Router(editor);
        editor.SelectObject(shape.Id);

        var (fromX, fromY) = Screen(editor, 150, 130);
        router.Press(CanvasPointerButton.Left, fromX, fromY);
        router.Move(fromX + 80, fromY + 60);
        Assert.NotNull(editor.DragPreview);

        Assert.True(editor.HandleEscape());
        Assert.Null(editor.DragPreview);
        Assert.Equal(CanvasInputGesture.None, router.ActiveGesture);

        router.Release(fromX + 80, fromY + 60);
        Assert.Equal(shape.X, Assert.Single(ObjectsOf(editor)).X);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    // ------------------------------------------------------------------ setup

    private static CanvasInputRouter Router(EditorViewModel editor)
    {
        editor.Session.Viewport.Resize(800, 600);
        return new CanvasInputRouter(editor);
    }

    private static (double X, double Y) Screen(EditorViewModel editor, double worldX, double worldY)
        => editor.Session.Viewport.WorldToScreen(worldX, worldY);

    private static IReadOnlyList<SceneObject> ObjectsOf(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).ToList();

    /// <summary>A plain rectangle object at x/y with the given size; its handles sit on its bounds.</summary>
    private static SceneObject Rectangle(
        double x, double y, double width, double height, bool locked = false) =>
        new(ObjectId.New(), SceneObjectKind.Rectangle, "Прямоугольник 1",
            x, y, width, height, 0, "#FF4C90B8", "#FF203040", 2, true, locked);

    private static EditorViewModel Editor(params SceneObject[] objects) => Editor(objects, false);

    private static EditorViewModel Editor(SceneObject shape, bool lockedLayer) =>
        Editor([shape], lockedLayer);

    private static EditorViewModel Editor(SceneObject[] objects, bool lockedLayer)
    {
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, lockedLayer, 1, objects);
        var document = new MapDocument(DocumentId.New(), "integrity", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
        return new EditorViewModel(document, null, true, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
    }
}
