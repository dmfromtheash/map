using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

/// <summary>«Комната»: rounded corners, caps along the wall, and ordinary shapes left untouched.</summary>
public sealed class RoomGeometryTests
{
    [Fact]
    public void An_ordinary_shape_keeps_its_exact_outline()
    {
        var shape = Square(room: null);
        var region = shape.Shape!.Regions[0];
        Assert.Equal(VectorGeometry.Flatten(shape, region.Nodes, closed: true), VectorGeometry.RegionOutline(shape, region));
        Assert.Empty(RoomGeometry.Caps(shape, region));
        Assert.False(RoomGeometry.CanRound(shape, region));
    }

    [Fact]
    public void Rounded_corners_are_arcs_tangent_to_both_walls()
    {
        var room = Square(new RoomStyle { CornerRadius = 20 });
        var outline = VectorGeometry.RegionOutline(room, room.Shape!.Regions[0]);
        Assert.Equal(outline[0], outline[^1]);
        // Every point stays inside the 100×100 square and no corner point survives.
        Assert.All(outline, p => Assert.InRange(p.X, 100 - 1e-9, 200 + 1e-9));
        Assert.DoesNotContain(outline, p => Math.Abs(p.X - 100) < 1e-6 && Math.Abs(p.Y - 100) < 1e-6);
        // The first corner (100,100) is replaced by an arc from (100,120) to (120,100) around (120,120), radius 20.
        var arc = outline.Where(p => p.X < 121 && p.Y < 121).ToArray();
        Assert.All(arc, p => Assert.Equal(20, Math.Sqrt(((p.X - 120) * (p.X - 120)) + ((p.Y - 120) * (p.Y - 120))), 6));
    }

    [Fact]
    public void Radius_is_limited_by_short_sides_and_reduced_on_sharp_spikes()
    {
        var corners = new[] { new VectorPoint(0, 0), new VectorPoint(10, 0), new VectorPoint(10, 10), new VectorPoint(0, 10) };
        var outline = RoomGeometry.RoundedOutline(corners, 500);
        Assert.All(outline, p => Assert.InRange(p.X, -1e-9, 10 + 1e-9));
        // A 20° spike gets less than half the requested rounding.
        var spike = new[] { new VectorPoint(0, 0), new VectorPoint(1000, 0), new VectorPoint(0, Math.Tan(20 * Math.PI / 180) * 1000) };
        var tip = RoomGeometry.RoundedOutline(spike, 40).Where(p => p.X > 500).Max(p => p.X);
        var full = 40 / Math.Tan(10 * Math.PI / 180);
        Assert.True(1000 - tip < full / 2, $"tip pulled back by {1000 - tip:0.#}, full rounding would be {full:0.#}");
    }

    [Fact]
    public void Caps_sit_on_every_corner_turned_along_the_wall()
    {
        var room = Square(new RoomStyle { CornerRadius = 0, CapAssetSha256 = new string('c', 64), CapAngleDegrees = 10 });
        var caps = RoomGeometry.Caps(room, room.Shape!.Regions[0]);
        Assert.Equal(4, caps.Count);
        Assert.Equal(new VectorPoint(100, 100), caps[0].Centre);
        // Corner (200,100): wall comes in along +X and leaves along +Y - halfway is 45°, plus the 10° cap angle.
        Assert.Equal(55, caps[1].AngleDegrees, 6);
        var rounded = Square(new RoomStyle { CornerRadius = 20 });
        var roundedCap = RoomGeometry.Caps(rounded, rounded.Shape!.Regions[0])[0];
        Assert.True(roundedCap.Centre.X > 100 && roundedCap.Centre.Y > 100, "on a rounded corner the cap sits on the arc");
    }

    [Fact]
    public void Room_cap_counts_as_a_used_asset_and_room_bounds_include_caps()
    {
        var hash = new string('d', 64);
        var room = Square(new RoomStyle { CapAssetSha256 = hash, CapScale = 4 });
        Assert.Contains(hash, CompositionTransfer.UsedAssetHashes([room]));
        var plain = SceneGeometry.BoundsOf(Square(new RoomStyle()));
        var capped = SceneGeometry.BoundsOf(room);
        Assert.True(capped.Left < plain.Left - 10);
    }

    private static SceneObject Square(RoomStyle? room) =>
        VectorGeometry.CreateShape(ObjectId.New(), "room", ShapePrimitiveKind.Polygon, new WorldBounds(100, 100, 200, 200),
            new ShapeFillStyle(), new VectorStrokeStyle { Width = 8 }) with { Room = room };
}
