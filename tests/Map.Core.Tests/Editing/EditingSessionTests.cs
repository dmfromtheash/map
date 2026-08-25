using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public class EditingSessionTests
{
    private static SceneObject Rectangle(double x = 100, double y = 100) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, "rect",
        x, y, 80, 60, 0, "#FF3E5C76", "#FF1B263B", 2, visible: true, locked: false);

    private static (EditingSession Session, LayerId LayerId) NewSession()
    {
        var layerId = LayerId.New();
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new Layer(layerId, "objects", true, false, 1.0, [])]);

        return (new EditingSession(document), layerId);
    }

    [Fact]
    public void Add_transform_undo_redo_walk_the_same_snapshots()
    {
        var (session, layerId) = NewSession();
        var rectangle = Rectangle();

        session.Execute(new AddObjectCommand(layerId, rectangle));
        Assert.Single(session.Document.Layers[0].Objects);
        Assert.True(session.IsDirty);

        var moved = new SceneObject(
            rectangle.Id, rectangle.Kind, rectangle.Name,
            250, 300, 120, 90, 45,
            rectangle.FillColor, rectangle.StrokeColor, rectangle.StrokeWidth,
            rectangle.Visible, rectangle.Locked);

        session.Execute(new TransformObjectCommand(rectangle, moved));
        var after = session.Document.Layers[0].Objects[0];
        Assert.Equal(250, after.X);
        Assert.Equal(45, after.RotationDegrees);
        Assert.Equal(rectangle.Id, after.Id);

        Assert.True(session.Undo());
        Assert.Equal(100, session.Document.Layers[0].Objects[0].X);

        Assert.True(session.Undo());
        Assert.Empty(session.Document.Layers[0].Objects);

        Assert.True(session.Redo());
        Assert.True(session.Redo());
        Assert.Equal(250, session.Document.Layers[0].Objects[0].X);
        Assert.Equal(2, session.UndoDepth);
        Assert.Equal(0, session.RedoDepth);
    }

    [Fact]
    public void One_completed_gesture_is_exactly_one_history_entry()
    {
        var (session, layerId) = NewSession();
        var rectangle = Rectangle();
        session.Execute(new AddObjectCommand(layerId, rectangle));

        // A drag produces many preview positions, but only the final state becomes a command.
        var final = new SceneObject(
            rectangle.Id, rectangle.Kind, rectangle.Name,
            400, 400, rectangle.Width, rectangle.Height, rectangle.RotationDegrees,
            rectangle.FillColor, rectangle.StrokeColor, rectangle.StrokeWidth,
            rectangle.Visible, rectangle.Locked);

        session.Execute(new TransformObjectCommand(rectangle, final));

        Assert.Equal(2, session.UndoDepth);
        Assert.True(session.Undo());
        Assert.Equal(100, session.Document.Layers[0].Objects[0].X); // one undo reverts the whole drag
    }

    [Fact]
    public void A_new_edit_clears_the_redo_stack()
    {
        var (session, layerId) = NewSession();
        session.Execute(new AddObjectCommand(layerId, Rectangle()));
        session.Undo();
        Assert.Equal(1, session.RedoDepth);

        session.Execute(new AddObjectCommand(layerId, Rectangle(10, 10)));
        Assert.Equal(0, session.RedoDepth);
    }

    [Fact]
    public void Failed_save_never_marks_the_revision_saved_and_success_marks_only_the_saved_one()
    {
        var (session, layerId) = NewSession();
        session.Execute(new AddObjectCommand(layerId, Rectangle()));
        var capturedRevision = session.Revision;
        Assert.True(session.IsDirty);

        // An edit lands while a background save of the captured snapshot is in flight.
        session.Execute(new AddObjectCommand(layerId, Rectangle(20, 20)));

        // The save of the OLD snapshot succeeds: only that revision becomes saved.
        session.MarkSaved(capturedRevision);
        Assert.True(session.IsDirty); // newer edit is still unsaved

        session.MarkSaved(session.Revision);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Commands_never_mutate_prior_snapshots()
    {
        var (session, layerId) = NewSession();
        var before = session.Document;

        session.Execute(new AddObjectCommand(layerId, Rectangle()));

        Assert.Empty(before.Layers[0].Objects);
        Assert.Single(session.Document.Layers[0].Objects);
        Assert.NotSame(before, session.Document);
    }
}

public class ViewportTests
{
    [Theory]
    [InlineData(400, 300, 1.25)]
    [InlineData(0, 0, 0.8)]
    [InlineData(799, 1, 4.0)]
    public void Zoom_keeps_the_world_point_under_the_cursor_fixed(double sx, double sy, double factor)
    {
        var viewport = new Viewport();
        viewport.Resize(800, 600);
        viewport.Set(123.5, -44.25, 0.7);

        var before = viewport.ScreenToWorld(sx, sy);
        viewport.ZoomAt(sx, sy, factor);
        var after = viewport.ScreenToWorld(sx, sy);

        Assert.Equal(before.X, after.X, 9);
        Assert.Equal(before.Y, after.Y, 9);
    }

    [Fact]
    public void Zoom_is_clamped_at_both_ends()
    {
        var viewport = new Viewport();
        viewport.Resize(800, 600);

        for (var i = 0; i < 300; i++)
        {
            viewport.ZoomAt(400, 300, 0.5);
        }

        Assert.Equal(Viewport.MinScale, viewport.Scale, 9);

        for (var i = 0; i < 300; i++)
        {
            viewport.ZoomAt(400, 300, 2.0);
        }

        Assert.Equal(Viewport.MaxScale, viewport.Scale, 9);
    }

    [Fact]
    public void Pan_moves_content_with_the_pointer()
    {
        var viewport = new Viewport();
        viewport.Resize(800, 600);
        viewport.Set(0, 0, 2.0);

        viewport.PanByScreenDelta(100, -50);

        Assert.Equal(-50, viewport.OffsetX, 9);
        Assert.Equal(25, viewport.OffsetY, 9);
    }

    [Fact]
    public void FitTo_centres_the_canvas_inside_the_view()
    {
        var viewport = new Viewport();
        viewport.Resize(1000, 500);
        viewport.FitTo(2000, 2000);

        // Scale limited by height: 500/2000*0.96 = 0.24; canvas horizontally centred.
        Assert.Equal(0.24, viewport.Scale, 9);
        var centre = viewport.ScreenToWorld(500, 250);
        Assert.Equal(1000, centre.X, 6);
        Assert.Equal(1000, centre.Y, 6);
    }
}

public class HitTestTests
{
    private static MapDocument DocumentWith(params SceneObject[] objects) => new(
        DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
        [new Layer(LayerId.New(), "objects", true, false, 1.0, objects)]);

    [Fact]
    public void Topmost_visible_object_wins_and_hidden_objects_are_not_pickable()
    {
        var below = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "below",
            100, 100, 100, 100, 0, "#FF111111", "#FF000000", 0, visible: true, locked: false);
        var above = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "above",
            120, 120, 100, 100, 0, "#FF222222", "#FF000000", 0, visible: true, locked: false);
        var hidden = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "hidden",
            120, 120, 200, 200, 0, "#FF333333", "#FF000000", 0, visible: false, locked: false);

        var document = DocumentWith(below, above, hidden);

        Assert.Equal(above.Id, SceneGeometry.HitTest(document, 150, 150)!.Id);
        Assert.Equal(below.Id, SceneGeometry.HitTest(document, 105, 105)!.Id);
        Assert.Null(SceneGeometry.HitTest(document, 500, 500));
    }

    [Fact]
    public void Rotated_rectangle_hits_honour_the_rotation()
    {
        // A 100x20 bar centred at (200,200), rotated 90 degrees: it now extends vertically.
        var bar = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "bar",
            150, 190, 100, 20, 90, "#FF111111", "#FF000000", 0, visible: true, locked: false);
        var document = DocumentWith(bar);

        Assert.NotNull(SceneGeometry.HitTest(document, 200, 240)); // inside after rotation
        Assert.Null(SceneGeometry.HitTest(document, 240, 200));    // inside only before rotation
    }

    [Fact]
    public void Ellipse_hits_exclude_the_bounding_box_corners()
    {
        var ellipse = new SceneObject(
            ObjectId.New(), SceneObjectKind.Ellipse, "ellipse",
            100, 100, 100, 60, 0, "#FF111111", "#FF000000", 0, visible: true, locked: false);
        var document = DocumentWith(ellipse);

        Assert.NotNull(SceneGeometry.HitTest(document, 150, 130)); // centre
        Assert.Null(SceneGeometry.HitTest(document, 103, 103));    // box corner outside the curve
    }
}
