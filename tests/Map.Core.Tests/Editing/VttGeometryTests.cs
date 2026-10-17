using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>Walls and doors for the dd2vtt export.</summary>
public sealed class VttGeometryTests
{
    [Fact]
    public void A_wall_line_with_a_portal_becomes_two_walls_and_one_door()
    {
        var wall = Line(0, 100, 400, 100, blocks: true, portals: [new LinePortal(PortalId.New(), 0.5, 40)]);
        var result = VttGeometry.Collect(Document(wall));

        Assert.Equal(2, result.Walls.Count);
        Assert.Equal((0.0, 100.0), (result.Walls[0][0].X, result.Walls[0][0].Y));
        Assert.Equal(180, result.Walls[0][^1].X, 6);
        Assert.Equal(220, result.Walls[1][0].X, 6);
        Assert.Equal(400, result.Walls[1][^1].X, 6);
        var door = Assert.Single(result.Portals);
        Assert.Equal(180, door.Start.X, 6);
        Assert.Equal(220, door.End.X, 6);
        Assert.Equal(200, door.Centre.X, 6);
    }

    [Fact]
    public void Only_visible_wall_strokes_and_wall_roads_count()
    {
        var plain = Line(0, 0, 100, 0, blocks: false);
        var hidden = Line(0, 50, 100, 50, blocks: true, visible: false);
        var road = new Road
        {
            Nodes = [new RoadNode(Guid.NewGuid(), 0, 300), new RoadNode(Guid.NewGuid(), 300, 300)],
            Style = new RoadStyle { BlocksLight = true },
        };
        road = road with { Branches = [new RoadBranch { Vertices = [new RoadVertex(road.Nodes[0].Id, 10), new RoadVertex(road.Nodes[1].Id, 10)] }] };
        var result = VttGeometry.Collect(Document([plain, hidden], [road]));

        var wall = Assert.Single(result.Walls);
        Assert.All(wall, p => Assert.Equal(300, p.Y, 3));
        Assert.Empty(result.Portals);
    }

    [Fact]
    public void A_turned_object_turns_its_wall_and_a_shape_outline_is_closed()
    {
        var turned = Line(0, 0, 200, 0, blocks: true, rotation: 90);
        var square = new SceneObject(ObjectId.New(), SceneObjectKind.Shape, "room", 0, 0, 100, 100, 0,
            "#FFFFFFFF", "#FF000000", 1, true, false)
        {
            Shape = new ShapeStyle(
                [new ShapeRegion(ShapeRegionId.New(), ShapeCombineMode.Add, ShapePrimitiveKind.Polygon,
                    [Node(0, 0), Node(1, 0), Node(1, 1), Node(0, 1)])],
                new ShapeFillStyle(), new VectorStrokeStyle { BlocksLight = true }),
        };
        var result = VttGeometry.Collect(Document([turned, square]));

        Assert.Equal(2, result.Walls.Count);
        // The line ran along y = 0 from x 0..200 inside a 200×1 box; turned 90° about its centre (100, 0.5) it runs vertically.
        Assert.All(result.Walls[0], p => Assert.Equal(100.5, p.X, 3));
        var outline = result.Walls[1];
        Assert.Equal(outline[0], outline[^1]);
        Assert.Equal(5, outline.Count);
    }

    private static VectorNode Node(double x, double y) => new(VectorNodeId.New(), x, y);

    private static SceneObject Line(double x0, double y0, double x1, double y1, bool blocks,
        IReadOnlyList<LinePortal>? portals = null, bool visible = true, double rotation = 0)
    {
        var width = Math.Max(1, Math.Abs(x1 - x0));
        var height = Math.Max(1, Math.Abs(y1 - y0));
        var x = Math.Min(x0, x1);
        var y = Math.Min(y0, y1);
        return new SceneObject(ObjectId.New(), SceneObjectKind.Line, "wall", x, y, width, height, rotation,
            "#00000000", "#FF000000", 1, visible, false)
        {
            Line = new LineStyle(
                [Node((x0 - x) / width, (y0 - y) / height), Node((x1 - x) / width, (y1 - y) / height)],
                new VectorStrokeStyle { BlocksLight = blocks }, portals),
        };
    }

    private static MapDocument Document(SceneObject item) => Document([item]);

    private static MapDocument Document(IReadOnlyList<SceneObject> items, IReadOnlyList<Road>? roads = null) => new(
        DocumentId.New(), "vtt", DateTimeOffset.UnixEpoch, new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "bg", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "walls", true, false, 1, items) { Roads = [.. roads ?? []] },
        ]);
}
