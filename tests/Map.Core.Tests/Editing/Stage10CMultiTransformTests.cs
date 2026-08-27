using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// Stage 10C: pure geometry of a multi-object selection - combined bounds, proportional scale
/// around the shared centre, rotation of member centres, the shared frame's handles and the
/// marquee pick. All expectations are exact numbers, not "roughly moved".
/// </summary>
public class Stage10CMultiTransformTests
{
    private static SceneObject Rect(string name, double x, double y, double w = 100, double h = 50,
        double rotation = 0, bool visible = true, bool locked = false) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, y, w, h, rotation, "#FF3E5C76", "#FF1B263B", 0, visible, locked);

    // ---------------------------------------------------------------- bounds

    [Fact]
    public void Combined_bounds_are_the_union_of_member_bounds()
    {
        var a = Rect("A", 0, 0);          // 0..100 x 0..50
        var b = Rect("B", 300, 200);      // 300..400 x 200..250

        var bounds = MultiSelectionGeometry.CombinedBounds([a, b]);

        Assert.NotNull(bounds);
        Assert.Equal(0, bounds!.Value.Left);
        Assert.Equal(0, bounds.Value.Top);
        Assert.Equal(400, bounds.Value.Right);
        Assert.Equal(250, bounds.Value.Bottom);
    }

    [Fact]
    public void Combined_bounds_of_nothing_is_null()
    {
        Assert.Null(MultiSelectionGeometry.CombinedBounds([]));
    }

    // ---------------------------------------------------------------- translate

    [Fact]
    public void Translate_moves_every_member_exactly()
    {
        var a = Rect("A", 0, 0);
        var b = Rect("B", 300, 200);

        var moved = MultiSelectionGeometry.Translate([a, b], 15, -25);

        Assert.Equal(15, moved[0].X);
        Assert.Equal(-25, moved[0].Y);
        Assert.Equal(315, moved[1].X);
        Assert.Equal(175, moved[1].Y);
        Assert.Equal(a.Id, moved[0].Id);
    }

    // ---------------------------------------------------------------- scale

    [Fact]
    public void Scale_around_centre_moves_centres_and_sizes_proportionally()
    {
        // Centres at (50,25) and (350,225); shared centre chosen at (200,125).
        var a = Rect("A", 0, 0);
        var b = Rect("B", 300, 200);

        var scaled = MultiSelectionGeometry.ScaleAroundCentre([a, b], 2.0, 200, 125);

        // A: centre (50,25) -> (200 + 2*(50-200), 125 + 2*(25-125)) = (-100, -75); size 200x100.
        Assert.Equal(-200, scaled[0].X);
        Assert.Equal(-125, scaled[0].Y);
        Assert.Equal(200, scaled[0].Width);
        Assert.Equal(100, scaled[0].Height);

        // B: centre (350,225) -> (500, 325); size 200x100 => X = 400, Y = 275.
        Assert.Equal(400, scaled[1].X);
        Assert.Equal(275, scaled[1].Y);
    }

    [Fact]
    public void Scale_keeps_each_members_own_rotation()
    {
        var a = Rect("A", 0, 0, rotation: 30);

        var scaled = MultiSelectionGeometry.ScaleAroundCentre([a], 0.5, 50, 25);

        Assert.Equal(30, scaled[0].RotationDegrees);
        Assert.Equal(50, scaled[0].Width);
        Assert.Equal(25, scaled[0].Height);
    }

    [Fact]
    public void Scale_factor_is_floored_so_no_member_collapses()
    {
        var small = Rect("S", 0, 0, w: 10, h: 10);
        var large = Rect("L", 100, 100, w: 400, h: 400);

        var factor = MultiSelectionGeometry.ClampScaleFactor([small, large], 0.001);
        var scaled = MultiSelectionGeometry.ScaleAroundCentre([small, large], factor, 0, 0);

        Assert.True(scaled[0].Width >= SelectionHandles.MinSize - 1e-9);
        Assert.True(scaled[0].Height >= SelectionHandles.MinSize - 1e-9);
    }

    // ---------------------------------------------------------------- rotate

    [Fact]
    public void Rotate_turns_member_centres_around_the_shared_centre_clockwise()
    {
        // Centre of A is (100, 0) relative to the pivot (0,0). Screen Y grows downward, so a
        // +90 degree (clockwise on screen) turn brings it to (0, 100).
        var a = Rect("A", 50, -25); // centre (100, 0)

        var rotated = MultiSelectionGeometry.RotateAroundCentre([a], 90, 0, 0);

        Assert.Equal(0, rotated[0].X + (rotated[0].Width / 2), 6);
        Assert.Equal(100, rotated[0].Y + (rotated[0].Height / 2), 6);
        Assert.Equal(90, rotated[0].RotationDegrees, 6);
    }

    [Fact]
    public void Rotate_adds_the_delta_to_each_members_own_angle_and_normalizes()
    {
        var a = Rect("A", 0, 0, rotation: 170);

        var rotated = MultiSelectionGeometry.RotateAroundCentre([a], 20, 50, 25);

        Assert.Equal(-170, rotated[0].RotationDegrees, 6);
    }

    // ---------------------------------------------------------------- frame handles

    [Fact]
    public void Frame_handles_sit_on_the_frame_corners_edges_and_rotation_stem()
    {
        var frame = new WorldBounds(0, 0, 200, 100);

        var handles = MultiSelectionGeometry.ComputeFrameHandles(frame, 28);

        Assert.Contains(handles, h => h.Kind == HandleKind.TopLeft && h.X == 0 && h.Y == 0);
        Assert.Contains(handles, h => h.Kind == HandleKind.BottomRight && h.X == 200 && h.Y == 100);
        Assert.Contains(handles, h => h.Kind == HandleKind.Rotate && h.X == 100 && h.Y == -28);
    }

    [Fact]
    public void Frame_hit_test_works_at_negative_coordinates_and_extreme_zoom()
    {
        var frame = new WorldBounds(-5000, -4000, -4800, -3900);

        // A deep zoom-out means a large world-space grab radius.
        Assert.Equal(HandleKind.TopLeft,
            MultiSelectionGeometry.HitTestFrame(frame, -5001, -4001, radius: 160, rotationHandleOffset: 28));
        Assert.Null(
            MultiSelectionGeometry.HitTestFrame(frame, -4900, -3950, radius: 0.5, rotationHandleOffset: 28));
    }

    [Fact]
    public void Resize_factor_follows_the_cursor_distance_from_the_frame_centre()
    {
        var frame = new WorldBounds(0, 0, 200, 100); // centre (100, 50)

        // The bottom-right corner starts sqrt(100^2+50^2) from the centre; doubling that
        // distance doubles the frame.
        var factor = MultiSelectionGeometry.ResizeFactor(frame, HandleKind.BottomRight, 300, 150);

        Assert.Equal(2.0, factor, 6);
    }

    [Fact]
    public void Rotate_moves_an_off_axis_centre_by_the_exact_rotation_matrix()
    {
        // Pivot (10, 20), member centre (100, 50): the offset (90, 30) turns by 30 degrees
        // clockwise on screen: x' = x*cos - y*sin, y' = x*sin + y*cos. Pivot and both offset
        // components are non-zero, so any sign or operator slip in the matrix changes the answer.
        var a = Rect("A", 50, 25); // centre (100, 50)

        var rotated = MultiSelectionGeometry.RotateAroundCentre([a], 30, 10, 20);

        var cos = Math.Cos(30 * Math.PI / 180);
        var sin = Math.Sin(30 * Math.PI / 180);
        Assert.Equal(10 + (90 * cos) - (30 * sin), rotated[0].X + (rotated[0].Width / 2), 6);
        Assert.Equal(20 + (90 * sin) + (30 * cos), rotated[0].Y + (rotated[0].Height / 2), 6);
    }

    [Fact]
    public void Rotating_past_the_lower_bound_wraps_into_the_range()
    {
        var a = Rect("A", 0, 0, rotation: -170);

        var rotated = MultiSelectionGeometry.RotateAroundCentre([a], -20, 50, 25);

        Assert.Equal(170, rotated[0].RotationDegrees, 6);
    }

    [Fact]
    public void An_angle_of_exactly_minus_180_normalizes_to_plus_180()
    {
        // The contract is (-180, 180]: the lower bound itself must come out as +180.
        var a = Rect("A", 0, 0, rotation: -170);

        var rotated = MultiSelectionGeometry.RotateAroundCentre([a], -10, 50, 25);

        Assert.Equal(180, rotated[0].RotationDegrees, 6);
    }

    [Fact]
    public void Rotating_to_the_upper_bound_keeps_the_angle_at_180()
    {
        var a = Rect("A", 0, 0, rotation: 90);

        var rotated = MultiSelectionGeometry.RotateAroundCentre([a], 90, 50, 25);

        Assert.Equal(180, rotated[0].RotationDegrees, 6);
    }

    [Fact]
    public void Edge_handles_sit_on_the_frame_side_midpoints()
    {
        var frame = new WorldBounds(0, 0, 200, 100);

        var handles = MultiSelectionGeometry.ComputeFrameHandles(frame, 28);

        Assert.Contains(handles, h => h.Kind == HandleKind.Right && h.X == 200 && h.Y == 50);
        Assert.Contains(handles, h => h.Kind == HandleKind.Left && h.X == 0 && h.Y == 50);
        Assert.Contains(handles, h => h.Kind == HandleKind.Bottom && h.X == 100 && h.Y == 100);
    }

    [Fact]
    public void Frame_hit_test_honours_the_exact_grab_radius()
    {
        var frame = new WorldBounds(0, 0, 200, 100);

        // The bottom-right handle is at (200, 100). A purely horizontal offset at the exact
        // radius still counts as a grab; one unit further does not.
        Assert.Equal(HandleKind.BottomRight,
            MultiSelectionGeometry.HitTestFrame(frame, 205, 100, radius: 5, rotationHandleOffset: 28));
        Assert.Equal(HandleKind.BottomRight,
            MultiSelectionGeometry.HitTestFrame(frame, 204, 100, radius: 5, rotationHandleOffset: 28));
        Assert.Null(
            MultiSelectionGeometry.HitTestFrame(frame, 206.1, 100, radius: 5, rotationHandleOffset: 28));
    }

    [Fact]
    public void Clamp_scale_factor_uses_the_smallest_side_of_the_smallest_member()
    {
        // The smallest dimension over all members is 10 (the 10x20 member's width), so the floor
        // is exactly MinSize / 10 = 0.8. A mix-up of Min/Max or of the division flips the value.
        var narrow = Rect("N", 0, 0, w: 10, h: 20);
        var large = Rect("L", 100, 100, w: 400, h: 400);

        var factor = MultiSelectionGeometry.ClampScaleFactor([narrow, large], 0.001);

        Assert.Equal(SelectionHandles.MinSize / 10.0, factor, 9);
    }

    [Fact]
    public void Resize_factor_from_an_edge_handle_uses_that_handles_own_distance()
    {
        var frame = new WorldBounds(0, 0, 200, 100); // centre (100, 50)

        // The Right handle starts 100 units from the centre; corners start ~111.8 away. Pulling
        // to 250 gives exactly 1.5 only when the grabbed handle itself anchors the ratio.
        var factor = MultiSelectionGeometry.ResizeFactor(frame, HandleKind.Right, 250, 50);

        Assert.Equal(1.5, factor, 9);
    }

    // ---------------------------------------------------------------- marquee

    [Fact]
    public void Marquee_normalizes_any_corner_order_and_negative_coordinates()
    {
        var rect = MultiSelectionGeometry.MarqueeBounds(-10, 40, -110, -60);

        Assert.Equal(-110, rect.Left);
        Assert.Equal(-60, rect.Top);
        Assert.Equal(-10, rect.Right);
        Assert.Equal(40, rect.Bottom);
    }

    [Fact]
    public void Marquee_picks_intersecting_visible_unlocked_objects_only()
    {
        var inside = Rect("inside", -100, -50);
        var overlapping = Rect("overlap", -30, -20);
        var hidden = Rect("hidden", -100, -50, visible: false);
        var locked = Rect("locked", -100, -50, locked: true);
        var far = Rect("far", 5_000, 5_000);
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1.0,
                    [inside, overlapping, hidden, locked, far]),
            ]);

        var found = MultiSelectionGeometry.ObjectsInMarquee(
            document, new WorldBounds(-120, -60, -20, 10));

        Assert.Equal(["inside", "overlap"], found.Select(o => o.Name));
    }

    [Fact]
    public void Marquee_skips_objects_on_hidden_or_locked_layers()
    {
        var onHidden = Rect("on-hidden", 0, 0);
        var onLocked = Rect("on-locked", 0, 0);
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "hidden", visible: false, locked: false, 1.0, [onHidden]),
                new ObjectLayer(LayerId.New(), "locked", visible: true, locked: true, 1.0, [onLocked]),
            ]);

        var found = MultiSelectionGeometry.ObjectsInMarquee(document, new WorldBounds(-10, -10, 500, 500));

        Assert.Empty(found);
    }
}
