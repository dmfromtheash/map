using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>Stage 20: a road network of shared nodes - connections, widths, edits, order and history.</summary>
public sealed class Stage20RoadTests
{
    private static EditingSession Session(out LayerId layer)
    {
        layer = LayerId.New();
        return new EditingSession(new MapDocument(DocumentId.New(), "Roads", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), new ObjectLayer(layer, "Объекты", true, false, 1, [])]));
    }

    /// <summary>A wide main road with a narrow path attached to the middle of its first stretch and a fork at its third node.</summary>
    private static Road Network(out Guid main, out Guid path, out Guid fork, out Guid pathNode, out Guid forkNode)
    {
        var road = RoadEditor.NewRoad([new(100, 400), new(400, 380), new(700, 440), new(1000, 400)], 20, RoadStyle.Country(), "Тракт", seed: 5);
        main = road.Branches[0].Id;
        var geometry = RoadGeometry.Evaluate(road).Branches[main];
        road = RoadEditor.InsertVertex(road, main, 0, 0.5, RoadGeometry.PointAt(geometry, 0, 0.5), out pathNode);
        road = RoadEditor.AddBranch(road, pathNode, [new(260, 250), new(240, 120)], null, 6, out path, "Тропа");
        forkNode = road.Branches[0].Vertices[3].NodeId;
        road = RoadEditor.AddBranch(road, forkNode, [new(820, 600), new(900, 720)], null, 12, out fork, "Развилка");
        road.Validate();
        return road;
    }

    [Fact]
    public void A_branch_attached_to_the_middle_of_a_stretch_shares_a_real_node_that_moves_every_branch()
    {
        var road = Network(out var main, out var path, out var fork, out var pathNode, out var forkNode);
        Assert.Equal(5, road.FindBranch(main)!.Vertices.Length);
        Assert.True(road.IsShared(pathNode)); Assert.True(road.IsShared(forkNode));
        Assert.Equal(3, road.Degree(pathNode)); Assert.Equal(3, road.Degree(forkNode));
        // The inserted node carries the interpolated width of the wide road; the path keeps its own.
        Assert.InRange(road.FindBranch(main)!.Vertices[1].Width, 19, 21);
        Assert.Equal(6, road.FindBranch(path)!.Vertices[0].Width);

        var moved = RoadEditor.MoveNode(road, pathNode, 300, 300);
        moved.Validate();
        var set = RoadGeometry.Evaluate(moved);
        Assert.Equal((300.0, 300.0), (set.Branches[path].Start.X, set.Branches[path].Start.Y));
        var mainGeometry = set.Branches[main];
        var atNode = mainGeometry.Samples[mainGeometry.NodeSampleIndex[1]];
        Assert.Equal((300.0, 300.0), (atNode.X, atNode.Y));
        // The other branches were not rebuilt: their vertices and nodes are the same records.
        Assert.Equal(road.FindBranch(fork)!.Vertices, moved.FindBranch(fork)!.Vertices);
        Assert.Same(road.FindNode(forkNode), moved.FindNode(forkNode));
    }

    [Fact]
    public void Width_is_interpolated_along_each_branch_and_a_narrow_branch_opens_into_the_wide_one()
    {
        var road = Network(out var main, out var path, out _, out _, out _);
        var profiled = RoadEditor.SetWidthProfile(road, main, 10, 30);
        var vertices = profiled.FindBranch(main)!.Vertices;
        Assert.Equal(10, vertices[0].Width); Assert.Equal(30, vertices[^1].Width);
        Assert.True(vertices[1].Width < vertices[2].Width && vertices[2].Width < vertices[3].Width);
        var geometry = RoadGeometry.Evaluate(profiled).Branches[main];
        for (var i = 1; i < geometry.Samples.Length; i++)
            Assert.True(geometry.Samples[i].HalfWidth >= geometry.Samples[i - 1].HalfWidth - 1e-9, "width grows monotonically along a growing profile");
        // The path is 6 wide, but at its shared node it has opened towards the wide road, and it is 3 again further on.
        var pathGeometry = RoadGeometry.Evaluate(profiled).Branches[path];
        Assert.NotNull(pathGeometry.StartJoin);
        Assert.True(pathGeometry.Samples[0].HalfWidth > 3.5, $"flare at the junction: {pathGeometry.Samples[0].HalfWidth}");
        Assert.InRange(pathGeometry.Samples[^1].HalfWidth, 2.99, 3.01);
        // The other branch's width is the caller's business only.
        Assert.Equal(road.FindBranch(path)!.Vertices, profiled.FindBranch(path)!.Vertices);
    }

    [Fact]
    public void Delete_detach_split_and_close_keep_the_rest_of_the_network()
    {
        var road = Network(out var main, out var path, out var fork, out var pathNode, out var forkNode);
        var without = RoadEditor.DeleteBranch(road, path);
        without.Validate();
        Assert.Null(without.FindBranch(path));
        Assert.Contains(without.Nodes, n => n.Id == pathNode); // the main road still passes through it
        Assert.Equal(road.Nodes.Length - 2, without.Nodes.Length); // the path's own two nodes are gone
        Assert.False(without.IsShared(pathNode));

        var detached = RoadEditor.DetachVertex(road, fork, 0);
        detached.Validate();
        Assert.False(detached.IsShared(forkNode));
        Assert.NotEqual(forkNode, detached.FindBranch(fork)!.Vertices[0].NodeId);
        var freed = detached.FindNode(detached.FindBranch(fork)!.Vertices[0].NodeId)!;
        var original = road.FindNode(forkNode)!;
        Assert.Equal((original.X, original.Y), (freed.X, freed.Y));

        var split = RoadEditor.SplitBranch(road, main, 2, out var second);
        split.Validate();
        Assert.Equal(3, split.FindBranch(main)!.Vertices.Length);
        Assert.Equal(3, split.FindBranch(second)!.Vertices.Length);
        Assert.Equal(split.FindBranch(main)!.Vertices[^1].NodeId, split.FindBranch(second)!.Vertices[0].NodeId);

        var (remaining, separate) = RoadEditor.DetachBranchAsRoad(road, fork, "Отдельная");
        remaining.Validate(); separate.Validate();
        Assert.Null(remaining.FindBranch(fork));
        Assert.Contains(remaining.Nodes, n => n.Id == forkNode);
        Assert.Single(separate.Branches);
        Assert.DoesNotContain(separate.Nodes, n => n.Id == forkNode);

        var loop = RoadEditor.NewRoad([new(50, 50), new(150, 40), new(160, 140), new(60, 150)], 8);
        var closed = RoadEditor.SetClosed(loop, loop.Branches[0].Id, true);
        closed.Validate();
        Assert.True(closed.Branches[0].Closed);
        var geometry = RoadGeometry.Evaluate(closed).Branches[loop.Branches[0].Id];
        Assert.Equal(5, geometry.NodeSampleIndex.Length);
        Assert.Equal((geometry.Start.X, geometry.Start.Y), (geometry.End.X, geometry.End.Y));
        Assert.Throws<InvalidOperationException>(() => RoadEditor.DeleteBranch(loop, loop.Branches[0].Id));
        Assert.Throws<InvalidOperationException>(() => RoadEditor.DeleteVertex(loop, loop.Branches[0].Id, 0).Let(r => RoadEditor.DeleteVertex(r, loop.Branches[0].Id, 0)).Let(r => RoadEditor.DeleteVertex(r, loop.Branches[0].Id, 0)));
    }

    [Fact]
    public void Joining_two_roads_keeps_each_look_and_a_geometric_crossing_is_not_a_connection()
    {
        var country = RoadEditor.NewRoad([new(100, 300), new(600, 300)], 18, RoadStyle.Country(), "A");
        var pen = RoadEditor.NewRoad([new(350, 100), new(350, 500)], 8, RoadStyle.PenRuts(), "B");
        // Two roads crossing on the canvas: still two roads, no node in common.
        Assert.False(country.Nodes.Any(n => pen.Nodes.Any(m => m.Id == n.Id)));
        var geometry = RoadGeometry.Evaluate(country).Branches[country.Branches[0].Id];
        var merged = RoadEditor.Absorb(RoadEditor.InsertVertex(country, country.Branches[0].Id, 0, 0.5, RoadGeometry.PointAt(geometry, 0, 0.5), out var node), pen);
        merged = RoadEditor.JoinEnd(merged, pen.Branches[0].Id, atEnd: true, node);
        merged.Validate();
        Assert.Equal(2, merged.Branches.Length);
        Assert.True(merged.IsShared(node));
        Assert.Equal(RoadStyle.PenRuts(), merged.FindBranch(pen.Branches[0].Id)!.Style);
        Assert.Null(merged.FindBranch(country.Branches[0].Id)!.Style);
        Assert.Equal(RoadStyle.Country(), merged.Style);
    }

    [Fact]
    public void Validation_rejects_dangling_vertices_orphan_nodes_and_bad_values()
    {
        var road = Network(out var main, out _, out _, out _, out _);
        road.Validate();
        Assert.Throws<ArgumentException>(() => RoadEditor.ReplaceBranch(road, road.FindBranch(main)! with
        { Vertices = road.FindBranch(main)!.Vertices.SetItem(0, new RoadVertex(Guid.NewGuid(), 10)) }).Validate());
        Assert.Throws<ArgumentException>(() => (road with { Nodes = road.Nodes.Add(new RoadNode(Guid.NewGuid(), 1, 1)) }).Validate());
        Assert.Throws<ArgumentException>(() => RoadEditor.SetVertexWidth(road, main, 0, 0).Validate());
        Assert.Throws<ArgumentException>(() => (road with { Style = road.Style with { Opacity = 2 } }).Validate());
        Assert.Throws<ArgumentException>(() => (road with { Level = 99 }).Validate());
        var copy = road.WithNewIds(); copy.Validate();
        Assert.NotEqual(road.Id, copy.Id);
        Assert.Equal(road.Branches.Length, copy.Branches.Length);
        Assert.True(copy.IsShared(copy.Branches[0].Vertices[1].NodeId));
        Assert.Empty(copy.Nodes.Select(n => n.Id).Intersect(road.Nodes.Select(n => n.Id)));
    }

    [Fact]
    public void Commands_keep_exact_history_and_the_surface_order_of_the_layer()
    {
        var session = Session(out var layer);
        var road = Network(out var main, out _, out _, out var pathNode, out _);
        session.Execute(new RoadCommand(layer, road.Id, road, "create"));
        Assert.Null(session.LastResourceError);
        var created = session.Document;
        Assert.Same(road, ((ObjectLayer)created.FindLayer(layer)!).Roads.Single());

        var moved = RoadEditor.MoveNode(road, pathNode, 320, 330);
        session.Execute(new RoadCommand(layer, road.Id, moved, "move"));
        var edited = session.Document;
        Assert.True(session.Undo()); Assert.Same(created, session.Document);
        Assert.True(session.Redo()); Assert.Same(edited, session.Document);

        // Water on the same layer: the default order puts the road under it; an explicit order is kept and undone exactly.
        var river = new River { Channels = [RiverEditor.NewChannel([new(500, 50), new(520, 700)], 20)] };
        session.Execute(new RiverCommand(layer, river.Id, river, "river"));
        var objects = (ObjectLayer)session.Document.FindLayer(layer)!;
        Assert.Equal([new SurfaceEntry(SurfaceKind.Road, road.Id), new SurfaceEntry(SurfaceKind.Water, river.Id)], LayerSurfaceOrder.Resolve(objects));
        Assert.True(objects.SurfaceOrder.IsDefaultOrEmpty);
        var before = session.Document;
        session.Execute(new ReorderSurfaceCommand(layer, [river.Id, road.Id], "road on top"));
        objects = (ObjectLayer)session.Document.FindLayer(layer)!;
        Assert.Equal([new SurfaceEntry(SurfaceKind.Water, river.Id), new SurfaceEntry(SurfaceKind.Road, road.Id)], LayerSurfaceOrder.Resolve(objects));
        Assert.False(objects.SurfaceOrder.IsDefaultOrEmpty);
        var reordered = session.Document;
        Assert.True(session.Undo()); Assert.Same(before, session.Document);
        Assert.True(session.Redo()); Assert.Same(reordered, session.Document);

        // A second road joins below the water, as a road does by default, even with an explicit order present.
        var second = RoadEditor.NewRoad([new(50, 700), new(400, 720)], 10, name: "Вторая");
        session.Execute(new RoadCommand(layer, second.Id, second, "second"));
        objects = (ObjectLayer)session.Document.FindLayer(layer)!;
        Assert.Equal([SurfaceKind.Road, SurfaceKind.Water, SurfaceKind.Road], LayerSurfaceOrder.Resolve(objects).Select(e => e.Kind));
        Assert.Equal(second.Id, LayerSurfaceOrder.Resolve(objects)[0].Id);

        session.Execute(new RoadCommand(layer, road.Id, null, "delete"));
        objects = (ObjectLayer)session.Document.FindLayer(layer)!;
        Assert.Single(objects.Roads);
        Assert.DoesNotContain(road.Id, objects.SurfaceOrder);
        Assert.Throws<InvalidOperationException>(() => new RoadCommand(layer, Guid.NewGuid(), null, "missing").Apply(session.Document));
    }

    [Fact]
    public void Geometry_is_stable_on_sharp_turns_and_coincident_points_and_fills_fork_corners()
    {
        // A hairpin with a sharp vertex, two coincident nodes and a tiny segment.
        var road = RoadEditor.NewRoad([new(100, 100), new(400, 100), new(400.2, 100.1), new(120, 130), new(120, 130.05)], 24);
        road = RoadEditor.SetVertexSharp(road, road.Branches[0].Id, 1, true);
        road.Validate();
        var set = RoadGeometry.Evaluate(road);
        var geometry = set.Branches[road.Branches[0].Id];
        Assert.All(geometry.Samples, s => Assert.True(double.IsFinite(s.X) && double.IsFinite(s.Y) && double.IsFinite(s.TangentX) && s.HalfWidth > 0));
        var outline = RoadGeometry.Outline(geometry, road.Seed);
        Assert.All(outline.Contours.SelectMany(c => c), p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));
        // No edge point lies further from the axis than the local half width allows (no spike), and none crosses the axis (no fold).
        foreach (var contour in outline.Contours)
            foreach (var point in contour)
            {
                Assert.True(RoadGeometry.TryClosest(set, point.X, point.Y, 30, out _, out _, out _, out var distance, out var half));
                Assert.True(distance <= half * 1.6 + 2, $"edge point {distance:0.0} from the axis with half width {half:0.0}");
            }
        var fork = RoadEditor.AddBranch(road, road.Branches[0].Vertices[3].NodeId, [new(300, 300)], null, 10, out _);
        var wedges = RoadGeometry.Fillets(RoadGeometry.Evaluate(fork));
        Assert.NotEmpty(wedges);
        Assert.All(wedges, w => Assert.True(w.Count >= 4));
    }
}

file static class Chain
{
    public static T Let<T>(this T value, Func<T, T> next) => next(value);
}
