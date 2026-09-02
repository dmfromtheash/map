using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Regressions for the two reported defects of this round: the Shape tool whose modes did not draw
/// what they named, and colour that could only ever be one flat value picked at creation time.
/// </summary>
public sealed class EditorGradientAndShapeToolTests
{
    // ------------------------------------------------------------------ gradient domain

    [Fact]
    public void A_gradient_keeps_its_stops_in_ramp_order_whatever_order_they_arrive_in()
    {
        var gradient = new GradientFill(GradientKind.Linear,
        [
            new GradientStopStyle(0.8, "#FF102030"),
            new GradientStopStyle(0.1, "#FFAABBCC"),
        ]);

        Assert.Equal([0.1, 0.8], gradient.Stops.Select(stop => stop.Offset));
    }

    [Fact]
    public void A_gradient_refuses_the_states_the_renderer_could_not_draw()
    {
        Assert.Throws<ArgumentException>(() => new GradientFill(
            GradientKind.Linear, [new GradientStopStyle(0, "#FF000000")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientStopStyle(1.5, "#FF000000"));
        Assert.Throws<ArgumentException>(() => new GradientStopStyle(0.5, "не цвет"));
        Assert.Throws<ArgumentException>(() => new GradientFill(
            GradientKind.Linear,
            [.. Enumerable.Range(0, GradientFill.MaxStops + 1)
                .Select(index => new GradientStopStyle(index / 20.0, "#FF000000"))]));
    }

    // -------------------------------------------------------------- gradient editing

    [Fact]
    public void Turning_the_gradient_on_seeds_it_from_the_colour_the_shape_already_had()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);

        editor.SetGradientMode(GradientKind.Linear);

        var gradient = Assert.IsType<GradientFill>(editor.SelectedGradient);
        Assert.Equal(shape.Shape!.Fill.Color, gradient.Stops[0].Color);
        Assert.Equal(2, gradient.Stops.Count);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Choosing_radial_on_a_flat_shape_is_one_undo_entry_not_two()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);

        editor.SetGradientMode(GradientKind.Radial);

        Assert.Equal(GradientKind.Radial, editor.SelectedGradient!.Kind);
        Assert.Equal(1, editor.Session.UndoDepth);

        editor.Undo();
        Assert.Null(editor.SelectedGradient);
    }

    [Fact]
    public void Turning_the_gradient_off_leaves_the_flat_colour_untouched()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        var flat = shape.Shape!.Fill.Color;

        editor.SetGradientMode(GradientKind.Linear);
        editor.SetGradientMode(null);

        Assert.Null(editor.SelectedGradient);
        Assert.Equal(flat, Current(editor).Shape!.Fill.Color);
    }

    [Fact]
    public void Dragging_a_gradient_stop_previews_without_history_and_commits_once()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        editor.SetGradientMode(GradientKind.Linear);
        var afterEnable = editor.Session.UndoDepth;

        editor.SetGradientStopOffset(1, 0.6, commit: false);
        editor.SetGradientStopOffset(1, 0.4, commit: false);
        Assert.NotNull(editor.DragPreview);
        Assert.Equal(afterEnable, editor.Session.UndoDepth);
        Assert.Equal(1, editor.SelectedGradient!.Stops[1].Offset);

        editor.SetGradientStopOffset(1, 0.4, commit: true);
        Assert.Null(editor.DragPreview);
        Assert.Equal(afterEnable + 1, editor.Session.UndoDepth);
        Assert.Equal(0.4, editor.SelectedGradient!.Stops[1].Offset, 6);
    }

    [Fact]
    public void A_stop_is_added_in_the_widest_gap_and_the_bounds_are_enforced()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        editor.SetGradientMode(GradientKind.Linear);

        editor.AddGradientStop();
        Assert.Equal(3, editor.SelectedGradient!.Stops.Count);
        Assert.Equal(0.5, editor.SelectedGradient.Stops[1].Offset, 6);

        while (editor.SelectedGradient!.Stops.Count < GradientFill.MaxStops)
        {
            editor.AddGradientStop();
        }

        editor.AddGradientStop();
        Assert.Equal(GradientFill.MaxStops, editor.SelectedGradient!.Stops.Count);

        while (editor.SelectedGradient!.Stops.Count > GradientFill.MinStops)
        {
            editor.RemoveGradientStop(0);
        }

        editor.RemoveGradientStop(0);
        Assert.Equal(GradientFill.MinStops, editor.SelectedGradient!.Stops.Count);
    }

    [Fact]
    public void An_invalid_stop_colour_never_reaches_the_object()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        editor.SetGradientMode(GradientKind.Linear);
        var before = editor.SelectedGradient!.Stops[0].Color;

        editor.SetGradientStopColor(0, "не цвет");

        Assert.Equal(before, editor.SelectedGradient!.Stops[0].Color);
    }

    [Fact]
    public void A_moved_or_hidden_object_keeps_its_gradient()
    {
        // Every whole-object rebuild used to list the styles by hand, so a new one was one missed
        // line away from being dropped by a move or a visibility toggle.
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        editor.SetGradientMode(GradientKind.Radial);

        editor.NudgeSelectedObject(5, 5);
        Assert.NotNull(editor.SelectedGradient);

        editor.ToggleSelectedObjectVisibility();
        Assert.NotNull(editor.SelectedGradient);
    }

    // ------------------------------------------------------- plain primitive objects

    [Fact]
    public void A_placed_rectangle_can_finally_change_its_own_colours()
    {
        var editor = EditorWithPrimitive(out var rectangle);
        editor.SelectObject(rectangle.Id);

        editor.SetPrimitiveFillColor("#FF204060");
        editor.SetPrimitiveStrokeColor("#FF806040");
        editor.SetPrimitiveStrokeWidth(6);

        Assert.Equal("#FF204060", Current(editor).FillColor);
        Assert.Equal("#FF806040", Current(editor).StrokeColor);
        Assert.Equal(6, Current(editor).StrokeWidth);
        Assert.Equal(3, editor.Session.UndoDepth);

        editor.Undo();
        Assert.Equal(rectangle.StrokeWidth, Current(editor).StrokeWidth);
    }

    [Fact]
    public void A_plain_primitive_takes_a_gradient_too()
    {
        var editor = EditorWithPrimitive(out var rectangle);
        editor.SelectObject(rectangle.Id);

        Assert.True(editor.SelectionSupportsGradient);
        editor.SetGradientMode(GradientKind.Linear);

        Assert.NotNull(Current(editor).FillGradient);
        Assert.Equal(rectangle.FillColor, Current(editor).FillGradient!.Stops[0].Color);
    }

    // ------------------------------------------------------ appearance gesture ownership

    [Fact]
    public void An_unfinished_colour_gesture_never_leaks_into_the_next_object()
    {
        // A preview that was never committed or cancelled used to stay pending, and the next
        // appearance edit - on a different object - was applied to the stale one instead. In the
        // real editor that showed up as a colour landing on the shape edited before this one.
        var first = VectorGeometry.CreateShape(
            ObjectId.New(), "Фигура 1", ShapePrimitiveKind.Rectangle,
            new WorldBounds(100, 100, 300, 260));
        var second = VectorGeometry.CreateShape(
            ObjectId.New(), "Фигура 2", ShapePrimitiveKind.Rectangle,
            new WorldBounds(400, 100, 600, 260));
        var editor = EditorWith(first, second);

        editor.SelectObject(first.Id);
        editor.SetShapeFillColor("#FF884422", commit: false);

        editor.SelectObject(second.Id);
        editor.SetGradientMode(GradientKind.Linear);

        var objects = editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects;
        Assert.NotNull(objects.Single(item => item.Id == second.Id).Shape!.Fill.Gradient);
        Assert.Null(objects.Single(item => item.Id == first.Id).Shape!.Fill.Gradient);
        Assert.Equal(
            first.Shape!.Fill.Color,
            objects.Single(item => item.Id == first.Id).Shape!.Fill.Color);
    }

    // ------------------------------------------------------------------ shape tool

    [Fact]
    public void The_polygon_mode_draws_the_number_of_sides_the_panel_shows()
    {
        // It was a literal 6 inside the gesture: the mode could only ever make hexagons.
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Polygon;
        editor.ShapePolygonSides = 5;

        DrawFromTo(editor, 100, 100, 200, 200);

        Assert.Equal(5, Current(editor).Shape!.Regions[0].Nodes.Count);
    }

    [Fact]
    public void The_pen_mode_builds_the_path_the_user_clicked()
    {
        // It used to ignore every click but the first and last and always emit the same right
        // triangle, so the mode was unusable whatever the user did.
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Pen;

        Assert.True(editor.IsPenMode);
        Assert.True(editor.AddPenNode(100, 100));
        Assert.True(editor.AddPenNode(300, 120));
        Assert.True(editor.AddPenNode(280, 300));
        Assert.True(editor.AddPenNode(120, 260));
        Assert.True(editor.PenPathActive);
        Assert.Equal(4, editor.PenNodeCount);
        Assert.Equal(0, editor.Session.UndoDepth);

        Assert.True(editor.FinishPenPath());

        Assert.False(editor.PenPathActive);
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Equal(4, Current(editor).Shape!.Regions[0].Nodes.Count);
    }

    [Fact]
    public void An_abandoned_pen_path_writes_nothing_at_all()
    {
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Pen;
        editor.AddPenNode(100, 100);
        editor.AddPenNode(200, 150);

        editor.CancelPenPath();

        Assert.False(editor.PenPathActive);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Empty(editor.PlacementPreviewObjects);
    }

    [Fact]
    public void A_pen_path_too_short_to_be_a_shape_is_refused_and_says_so()
    {
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Pen;
        editor.AddPenNode(100, 100);
        editor.AddPenNode(200, 150);

        Assert.False(editor.FinishPenPath());

        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Contains("трёх узлов", editor.StatusDetail);
    }

    [Fact]
    public void Switching_tool_drops_an_unfinished_pen_path()
    {
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Pen;
        editor.AddPenNode(100, 100);
        editor.AddPenNode(200, 150);

        editor.ActiveTool = EditorTool.Select;

        Assert.False(editor.PenPathActive);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void The_brush_mode_sweeps_a_band_instead_of_repeating_free_draw()
    {
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Brush;
        editor.ShapeBrushWidth = 40;

        editor.BeginVectorDraw(100, 200);
        editor.ContinueVectorDraw(200, 200);
        editor.ContinueVectorDraw(300, 200);
        editor.EndVectorDraw();

        // A hairline path along one row would have no height at all; the band has the brush width.
        Assert.Equal(40, Current(editor).Height, 1);
        Assert.True(Current(editor).Shape!.Regions[0].Nodes.Count >= 6);
    }

    [Fact]
    public void Shift_makes_a_square_and_alt_draws_from_the_centre()
    {
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Rectangle;

        editor.VectorConstrainProportions = true;
        DrawFromTo(editor, 100, 100, 300, 160);
        Assert.Equal(Current(editor).Width, Current(editor).Height, 3);

        editor.VectorConstrainProportions = false;
        editor.VectorDrawFromCenter = true;
        DrawFromTo(editor, 200, 200, 260, 240);
        Assert.Equal(140, Current(editor).X, 3);
        Assert.Equal(160, Current(editor).Y, 3);
        Assert.Equal(120, Current(editor).Width, 3);
        Assert.Equal(80, Current(editor).Height, 3);
    }

    [Fact]
    public void A_new_shape_uses_the_colours_the_tool_panel_shows()
    {
        var editor = EditorForDrawing();
        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Rectangle;
        editor.NewShapeFillColor = "#8033AA55";
        editor.NewShapeStrokeColor = "#FF221100";
        editor.NewShapeStrokeWidth = 7;

        DrawFromTo(editor, 100, 100, 200, 180);

        Assert.Equal("#8033AA55", Current(editor).Shape!.Fill.Color);
        Assert.Equal("#FF221100", Current(editor).Shape!.Stroke.Color);
        Assert.Equal(7, Current(editor).Shape!.Stroke.Width);
    }

    // ------------------------------------------------------------------------ setup

    private static SceneObject Current(EditorViewModel editor) => editor.Session.SelectedObject!;

    private static void DrawFromTo(
        EditorViewModel editor, double fromX, double fromY, double toX, double toY)
    {
        Assert.True(editor.BeginVectorDraw(fromX, fromY));
        editor.ContinueVectorDraw(toX, toY);
        editor.EndVectorDraw();
    }

    private static EditorViewModel EditorWithShape(out SceneObject shape)
    {
        shape = VectorGeometry.CreateShape(
            ObjectId.New(), "Фигура 1", ShapePrimitiveKind.Rectangle,
            new WorldBounds(100, 100, 300, 260));
        return EditorWith(shape);
    }

    private static EditorViewModel EditorWithPrimitive(out SceneObject rectangle)
    {
        rectangle = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "Прямоугольник 1",
            100, 100, 200, 160, 0, "#FF3355AA", "#FF102030", 2, true, false);
        return EditorWith(rectangle);
    }

    private static EditorViewModel EditorForDrawing() => EditorWith();

    private static EditorViewModel EditorWith(params SceneObject[] content)
    {
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, content);
        var document = new MapDocument(DocumentId.New(), "gradient", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
        return new EditorViewModel(document, null, true, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
    }
}
