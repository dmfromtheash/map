using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// The road defects recorded by the 2026-09-08 review and fixed in the audit: a merge keeps the
/// width the user saw, and moving one node rebuilds only the branches through that node.
/// </summary>
public sealed class Stage20RoadAuditTests
{
    [Fact]
    public void Absorbing_a_road_with_another_width_scale_keeps_its_effective_width()
    {
        var target = RoadEditor.NewRoad([new(0, 0), new(300, 0)], 20, RoadStyle.Country(), "Тракт") with { WidthScale = 1 };
        var source = RoadEditor.NewRoad([new(300, 0), new(300, 300)], 20, RoadStyle.Country(), "Просёлок") with { WidthScale = 2 };
        // Drawn on its own the source is 40 units wide (20 × 2).
        var sourceHalf = RoadGeometry.Evaluate(source).Branches[source.Branches[0].Id].Samples.Max(s => s.HalfWidth);
        Assert.Equal(20, sourceHalf, 1);

        var merged = RoadEditor.Absorb(target, source);
        merged.Validate();
        var absorbed = merged.Branches[^1];
        Assert.Equal(source.Branches[0].Id, absorbed.Id);
        var mergedHalf = RoadGeometry.Evaluate(merged).Branches[absorbed.Id].Samples.Max(s => s.HalfWidth);
        Assert.Equal(sourceHalf, mergedHalf, 1);
        Assert.All(absorbed.Vertices, v => Assert.Equal(40, v.Width, 6));
    }

    [Fact]
    public void Moving_one_node_rebuilds_only_the_branches_through_it()
    {
        var road = RoadEditor.NewRoad([new(100, 400), new(400, 380), new(700, 440), new(1000, 400)], 20, RoadStyle.Country(), "Тракт", seed: 5);
        var main = road.Branches[0].Id;
        var geometry = RoadGeometry.Evaluate(road).Branches[main];
        road = RoadEditor.InsertVertex(road, main, 0, 0.5, RoadGeometry.PointAt(geometry, 0, 0.5), out var pathNode);
        road = RoadEditor.AddBranch(road, pathNode, [new(260, 250), new(240, 120)], null, 6, out var path, "Тропа");
        var farNode = road.Nodes.First(n => n.X == 1000 && n.Y == 400).Id;
        road = RoadEditor.AddBranch(road, farNode, [new(1100, 500), new(1200, 640)], null, 12, out var far, "Дальняя");
        road.Validate();
        var before = RoadGeometry.Evaluate(road);

        // The path's free end moves: the path is rebuilt, the main road and the far branch are not.
        var tip = road.FindBranch(path)!.Vertices[^1].NodeId;
        var moved = RoadEditor.MoveNode(road, tip, 200, 100);
        var after = RoadGeometry.Evaluate(moved);
        Assert.NotSame(before.Branches[path], after.Branches[path]);
        Assert.Same(before.Branches[main], after.Branches[main]);
        Assert.Same(before.Branches[far], after.Branches[far]);
        Assert.Equal(200, after.Branches[path].End.X, 3);

        // A shared node moves: both branches through it are rebuilt, the far branch still is not.
        var junction = RoadEditor.MoveNode(moved, pathNode, 300, 300);
        var joined = RoadGeometry.Evaluate(junction);
        Assert.NotSame(after.Branches[main], joined.Branches[main]);
        Assert.NotSame(after.Branches[path], joined.Branches[path]);
        Assert.Same(after.Branches[far], joined.Branches[far]);

        // A width change at the far end of the main road changes what the far branch meets there.
        var widened = RoadEditor.SetVertexWidth(junction, main, junction.FindBranch(main)!.Vertices.Length - 1, 60);
        var reflared = RoadGeometry.Evaluate(widened);
        Assert.NotSame(joined.Branches[far], reflared.Branches[far]);
        Assert.Same(joined.Branches[path], reflared.Branches[path]);
    }
}
