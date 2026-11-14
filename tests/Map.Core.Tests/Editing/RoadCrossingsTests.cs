using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R06: crossings are found from the geometry, follow the nodes and keep a chosen kind.</summary>
public sealed class RoadCrossingsTests
{
    private static River River(params VectorPoint[] points) => new()
    {
        Name = "Река", Style = RiverStyle.Illustrated(),
        Channels = [RiverEditor.NewChannel(points, 30)],
    };

    [Fact]
    public void A_road_over_a_river_gets_one_bridge_where_they_meet()
    {
        var road = RoadEditor.NewRoad([new(0, 100), new(400, 100)], 20);
        var river = River(new(200, 0), new(200, 200));
        var crossings = RoadCrossings.Detect([road], [river]);
        var crossing = Assert.Single(crossings);
        Assert.Equal(RoadCrossingKind.Bridge, crossing.Kind);
        Assert.Equal(200, crossing.X, 1);
        Assert.Equal(100, crossing.Y, 1);
        Assert.Equal(10, crossing.RoadHalfWidth, 1);
        Assert.Equal(15, crossing.RiverHalfWidth, 1);
        Assert.Equal(0, crossing.Ordinal);
        Assert.Equal(road.Branches[0].Id, crossing.BranchId);
        Assert.Equal(river.Channels[0].Id, crossing.ChannelId);
        // Squarely across: the deck spans the river's own width.
        Assert.Equal(30, crossing.SpanAlongRoad, 1);
    }

    [Fact]
    public void The_crossing_follows_a_moved_node_and_a_road_beside_the_river_has_none()
    {
        var road = RoadEditor.NewRoad([new(0, 100), new(400, 100)], 20);
        var river = River(new(200, 0), new(200, 200));
        var moved = road with { Nodes = [.. road.Nodes.Select((n, i) => i == 0 ? n with { Y = 60 } : n)] };
        var before = Assert.Single(RoadCrossings.Detect([road], [river]));
        var after = Assert.Single(RoadCrossings.Detect([moved], [river]));
        Assert.True(after.Y < before.Y, "the crossing must move with the node");
        Assert.Equal(200, after.X, 1);

        var beside = RoadEditor.NewRoad([new(0, 300), new(400, 300)], 20);
        Assert.Empty(RoadCrossings.Detect([beside], [river]));
        var lake = new River { Name = "Озеро", Style = RiverStyle.Illustrated(), Channels = [RiverEditor.NewChannel([new(150, 50), new(250, 50), new(250, 150), new(150, 150)], 10) with { Closed = true }] };
        Assert.Empty(RoadCrossings.Detect([road], [lake]));
    }

    [Fact]
    public void A_winding_road_crosses_the_same_river_twice_with_ordinals_along_the_road()
    {
        var road = RoadEditor.NewRoad([new(0, 100), new(300, 100), new(300, 300), new(0, 300)], 16);
        var river = River(new(150, 0), new(150, 400));
        var crossings = RoadCrossings.Detect([road], [river]).OrderBy(c => c.Ordinal).ToArray();
        Assert.Equal(2, crossings.Length);
        Assert.Equal(0, crossings[0].Ordinal); Assert.Equal(1, crossings[1].Ordinal);
        Assert.True(crossings[0].Distance < crossings[1].Distance);
        // The smoothed course bends towards the corners, so the meeting points sit near the straight lines.
        Assert.InRange(crossings[0].Y, 80, 120); Assert.InRange(crossings[1].Y, 280, 320);
    }

    [Fact]
    public void A_chosen_kind_is_kept_per_crossing_and_the_bridge_stores_nothing()
    {
        var road = RoadEditor.NewRoad([new(0, 100), new(300, 100), new(300, 300), new(0, 300)], 16);
        var river = River(new(150, 0), new(150, 400));
        var branch = road.Branches[0].Id; var channel = river.Channels[0].Id;
        var ford = RoadEditor.SetCrossingKind(road, branch, channel, 1, RoadCrossingKind.Ford);
        ford.Validate();
        Assert.True(ford.NeedsCrossingsSchema);
        var kinds = RoadCrossings.Detect([ford], [river]).OrderBy(c => c.Ordinal).Select(c => c.Kind).ToArray();
        Assert.Equal([RoadCrossingKind.Bridge, RoadCrossingKind.Ford], kinds);

        var back = RoadEditor.SetCrossingKind(ford, branch, channel, 1, RoadCrossingKind.Bridge);
        Assert.False(back.NeedsCrossingsSchema);
        Assert.All(RoadCrossings.Detect([back], [river]), c => Assert.Equal(RoadCrossingKind.Bridge, c.Kind));

        // New ids keep the choice on the renamed branch.
        var copy = ford.WithNewIds();
        var copied = Assert.Single(copy.CrossingOverrides);
        Assert.Equal(copy.Branches[0].Id, copied.BranchId);
        Assert.Equal(RoadCrossingKind.Ford, copied.Kind);
    }

    [Fact]
    public void Kept_crossings_of_unchanged_pairs_equal_a_fresh_detection_in_the_same_order()
    {
        var roads = new List<Road>();
        var rivers = new List<River>();
        for (var i = 0; i < 6; i++)
        {
            roads.Add(RoadEditor.NewRoad([new(0, 40 + 60 * i), new(200, 70 + 60 * i), new(420, 40 + 60 * i)], 12));
            rivers.Add(River(new(40 + 60 * i, 0), new(60 + 60 * i, 400)));
        }
        rivers.Add(River(new(0, 500), new(400, 520))); // crosses no road
        var first = RoadCrossings.Detect(roads, rivers);
        Assert.NotEmpty(first);
        // The same records again: the kept pairs. Equal copies are new records: a fresh detection.
        Assert.Equal(first, RoadCrossings.Detect(roads, rivers));
        Assert.Equal(first, RoadCrossings.Detect([.. roads.Select(r => r with { })], [.. rivers.Select(r => r with { })]));

        // One road edited (a new record): its crossings follow, the others stay exactly as they were.
        var branch = roads[2].Branches[0].Id;
        var hit = first.First(c => c.RoadId == roads[2].Id);
        roads[2] = RoadEditor.SetCrossingKind(roads[2], branch, hit.ChannelId, hit.Ordinal, RoadCrossingKind.Ferry);
        var after = RoadCrossings.Detect(roads, rivers);
        Assert.Equal(first.Count, after.Count);
        Assert.Equal(RoadCrossingKind.Ferry, after.Single(c => c.RoadId == hit.RoadId && c.ChannelId == hit.ChannelId && c.Ordinal == hit.Ordinal).Kind);
        Assert.Equal(first.Where(c => c.RoadId != hit.RoadId), after.Where(c => c.RoadId != hit.RoadId));
        // A hidden river is not crossed, kept or not.
        rivers[0] = rivers[0] with { Visible = false };
        Assert.DoesNotContain(RoadCrossings.Detect(roads, rivers), c => c.RiverId == rivers[0].Id);
    }
}
