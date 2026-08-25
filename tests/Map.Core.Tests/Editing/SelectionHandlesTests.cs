using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public class SelectionHandlesTests
{
    private static SceneObject Rect(
        double x = 100, double y = 100, double w = 200, double h = 100, double rotation = 0) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, "rect",
        x, y, w, h, rotation, "#FF3E5C76", "#FF1B263B", 2, visible: true, locked: false);

    [Fact]
    public void Handles_of_an_unrotated_object_sit_on_its_corners_edges_and_above_the_top()
    {
        var handles = SelectionHandles.Compute(Rect(), rotationHandleOffset: 30)
            .ToDictionary(h => h.Kind, h => (h.X, h.Y));

        Assert.Equal((100.0, 100.0), handles[HandleKind.TopLeft]);
        Assert.Equal((300.0, 200.0), handles[HandleKind.BottomRight]);
        Assert.Equal((200.0, 100.0), handles[HandleKind.Top]);
        Assert.Equal((300.0, 150.0), handles[HandleKind.Right]);
        Assert.Equal((200.0, 70.0), handles[HandleKind.Rotate]);
    }

    [Fact]
    public void Handles_follow_the_rotation()
    {
        // 90 degrees clockwise around the centre (200,150): the Top handle goes to (250,150).
        var handles = SelectionHandles.Compute(Rect(rotation: 90), rotationHandleOffset: 30)
            .ToDictionary(h => h.Kind, h => (h.X, h.Y));

        Assert.Equal(250.0, handles[HandleKind.Top].X, 6);
        Assert.Equal(150.0, handles[HandleKind.Top].Y, 6);
        Assert.Equal(280.0, handles[HandleKind.Rotate].X, 6);
        Assert.Equal(150.0, handles[HandleKind.Rotate].Y, 6);
    }

    [Fact]
    public void Corner_resize_keeps_the_opposite_corner_pinned()
    {
        var before = Rect();

        // Drag the bottom-right corner to (400, 260): top-left must stay at (100, 100).
        var after = SelectionHandles.Resize(before, HandleKind.BottomRight, 400, 260);

        Assert.Equal(100, after.X, 6);
        Assert.Equal(100, after.Y, 6);
        Assert.Equal(300, after.Width, 6);
        Assert.Equal(160, after.Height, 6);
        Assert.Equal(before.Id, after.Id);
    }

    [Fact]
    public void Edge_resize_changes_only_its_axis()
    {
        var before = Rect();

        var after = SelectionHandles.Resize(before, HandleKind.Right, 380, 999);

        Assert.Equal(before.Y, after.Y, 6);           // vertical placement untouched
        Assert.Equal(before.Height, after.Height, 6); // height untouched
        Assert.Equal(280, after.Width, 6);            // 380 - 100
        Assert.Equal(100, after.X, 6);                // left edge pinned
    }

    [Fact]
    public void Resize_clamps_at_the_minimum_size_instead_of_inverting()
    {
        var before = Rect();

        // Drag the right edge far past the left edge.
        var after = SelectionHandles.Resize(before, HandleKind.Right, -500, 150);

        Assert.Equal(SelectionHandles.MinSize, after.Width, 6);
        Assert.True(after.Height > 0);
    }

    [Fact]
    public void Rotated_resize_keeps_the_anchor_corner_stationary_in_world_space()
    {
        var before = Rect(rotation: 30);
        var anchorBefore = SelectionHandles.Compute(before, 0)
            .Single(h => h.Kind == HandleKind.TopLeft);

        var after = SelectionHandles.Resize(before, HandleKind.BottomRight, 420, 300);
        var anchorAfter = SelectionHandles.Compute(after, 0)
            .Single(h => h.Kind == HandleKind.TopLeft);

        Assert.Equal(anchorBefore.X, anchorAfter.X, 6);
        Assert.Equal(anchorBefore.Y, anchorAfter.Y, 6);
        Assert.Equal(30, after.RotationDegrees, 6);
    }

    [Fact]
    public void Rotation_follows_the_cursor_angle_and_keeps_geometry()
    {
        var before = Rect(); // centre (200, 150)

        // Start at the rotation handle straight above the centre, drag to the right of the centre:
        // that is +90 degrees clockwise.
        var after = SelectionHandles.Rotate(before, 200, 70, 280, 150);

        Assert.Equal(90, after.RotationDegrees, 2);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Width, after.Width, 6);
    }

    [Fact]
    public void Rotation_is_normalised_into_the_half_open_180_range()
    {
        var before = Rect(rotation: 170);
        var after = SelectionHandles.Rotate(before, 200, 70, 280, 150); // +90

        Assert.Equal(-100, after.RotationDegrees, 2);
    }

    [Fact]
    public void Handle_hit_test_finds_the_nearest_handle_and_misses_far_points()
    {
        var rect = Rect();

        Assert.Equal(HandleKind.BottomRight, SelectionHandles.HitTest(rect, 302, 198, 6, 30));
        Assert.Equal(HandleKind.Rotate, SelectionHandles.HitTest(rect, 201, 71, 6, 30));
        Assert.Null(SelectionHandles.HitTest(rect, 250, 250, 6, 30));
    }
}
