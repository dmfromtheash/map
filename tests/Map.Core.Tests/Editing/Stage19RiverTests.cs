using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage19RiverTests
{
    private static EditingSession Session(out LayerId layer)
    {
        layer = LayerId.New();
        return new EditingSession(new MapDocument(DocumentId.New(), "Rivers", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), new ObjectLayer(layer, "Объекты", true, false, 1, [])]));
    }

    private static River Rivers(out RiverChannel main, out RiverChannel tributary)
    {
        main = new RiverChannel
        {
            Name = "Главное",
            Nodes = [new(Guid.NewGuid(), 100, 100, 6), new(Guid.NewGuid(), 400, 220, 14), new(Guid.NewGuid(), 700, 180, 22), new(Guid.NewGuid(), 1000, 400, 30), new(Guid.NewGuid(), 1150, 700, 34)],
        };
        tributary = new RiverChannel
        {
            Name = "Приток",
            Nodes = [new(Guid.NewGuid(), 500, 40, 4), new(Guid.NewGuid(), 560, 120, 8)],
            Mouth = new RiverJunction(main.Id, main.Nodes[1].Id, 0.5),
        };
        return new River { Name = "Тестовая", Channels = [main, tributary] };
    }

    private static River Get(MapDocument document, LayerId layer) => ((ObjectLayer)document.FindLayer(layer)!).Rivers.Single();

    [Fact]
    public void Width_profile_interpolates_by_length_and_scale_applies_to_every_node()
    {
        var river = Rivers(out var main, out _);
        var geometry = RiverGeometry.Evaluate(river).Channels[main.Id];
        var samples = geometry.Samples;
        Assert.Equal(6 / 2.0 * 0.2, samples[0].HalfWidth, 3); // tapered source thread
        Assert.Equal(14 / 2.0, samples[geometry.NodeSampleIndex[1]].HalfWidth, 6);
        Assert.Equal(34 / 2.0, samples[^1].HalfWidth, 6);
        var middle = geometry.NodeSampleIndex[1] + (geometry.NodeSampleIndex[2] - geometry.NodeSampleIndex[1]) / 2;
        Assert.InRange(samples[middle].HalfWidth, 7, 11);
        for (var i = geometry.NodeSampleIndex[1] + 1; i < samples.Length; i++)
            Assert.True(samples[i].HalfWidth >= samples[i - 1].HalfWidth - 1e-9, "width never shrinks downstream of a growing profile");
        var scaled = RiverGeometry.Evaluate(river with { WidthScale = 2 }).Channels[main.Id];
        Assert.Equal(34, scaled.Samples[^1].HalfWidth, 6);
        var profile = RiverEditor.SetWidthProfile(river, main.Id, 4, 40).FindChannel(main.Id)!;
        Assert.Equal(4, profile.Nodes[0].Width); Assert.Equal(40, profile.Nodes[^1].Width);
        Assert.True(profile.Nodes[1].Width < profile.Nodes[2].Width);
    }

    [Fact]
    public void Junction_lies_on_parent_and_only_moves_with_its_own_parent_segment()
    {
        var river = Rivers(out var main, out var tributary);
        var set = RiverGeometry.Evaluate(river);
        var parent = set.Channels[main.Id]; var child = set.Channels[tributary.Id];
        Assert.True(child.JoinsParent);
        var end = child.End;
        Assert.True(RiverGeometry.TryClosest(set, end.X, end.Y, 0.5, out var onChannel, out _, out _, out var distance, out _, c => c.Id == main.Id));
        Assert.Equal(main.Id, onChannel); Assert.InRange(distance, 0, 0.5);
        Assert.InRange(child.ParentHalfWidthAtMouth, 7, 11); // halfway between 14 and 22 wide

        // Moving a parent node two segments downstream leaves the junction where it was.
        var moved = RiverEditor.MoveNode(river, main.Id, 4, 1100, 760);
        var movedEnd = RiverGeometry.Evaluate(moved).Channels[tributary.Id].End;
        Assert.Equal(end.X, movedEnd.X, 6); Assert.Equal(end.Y, movedEnd.Y, 6);

        // Moving the mouth along the parent moves only the tributary evaluation.
        var slid = RiverEditor.SetMouth(river, tributary.Id, new RiverJunction(main.Id, main.Nodes[2].Id, 0.25));
        var slidSet = RiverGeometry.Evaluate(slid);
        Assert.Equal(parent.Samples.Select(s => (s.X, s.Y)), slidSet.Channels[main.Id].Samples.Select(s => (s.X, s.Y)));
        Assert.NotEqual(end, slidSet.Channels[tributary.Id].End);
        Assert.Throws<InvalidOperationException>(() => RiverEditor.SetMouth(river, main.Id, new RiverJunction(tributary.Id, tributary.Nodes[0].Id, 0.5)));
    }

    [Fact]
    public void Insert_and_delete_nodes_keep_tributaries_attached_to_the_same_place()
    {
        var river = Rivers(out var main, out var tributary);
        var before = RiverGeometry.Evaluate(river).Channels[tributary.Id].End;
        var inserted = RiverEditor.InsertNode(river, main.Id, 1, 0.25, RiverGeometry.PointAt(RiverGeometry.Evaluate(river).Channels[main.Id], 1, 0.25));
        inserted.Validate();
        Assert.Equal(6, inserted.FindChannel(main.Id)!.Nodes.Length);
        var afterInsert = RiverGeometry.Evaluate(inserted).Channels[tributary.Id].End;
        Assert.InRange(Math.Abs(afterInsert.X - before.X) + Math.Abs(afterInsert.Y - before.Y), 0, 6);
        var deleted = RiverEditor.DeleteNode(inserted, main.Id, 2);
        deleted.Validate();
        Assert.Equal(5, deleted.FindChannel(main.Id)!.Nodes.Length);
        var afterDelete = RiverGeometry.Evaluate(deleted).Channels[tributary.Id].End;
        Assert.InRange(Math.Abs(afterDelete.X - before.X) + Math.Abs(afterDelete.Y - before.Y), 0, 6);
        var freed = RiverEditor.DeleteChannel(river, main.Id);
        Assert.Null(freed.FindChannel(tributary.Id)!.Mouth); freed.Validate();
        Assert.Throws<InvalidOperationException>(() => RiverEditor.DeleteNode(river, tributary.Id, 0));
    }

    [Fact]
    public void Validation_rejects_cycles_dangling_mouths_and_bad_widths()
    {
        var river = Rivers(out var main, out var tributary);
        river.Validate();
        Assert.Throws<ArgumentException>(() => RiverEditor.ReplaceChannel(river, main with { Mouth = new RiverJunction(tributary.Id, tributary.Nodes[0].Id, 0.5) }).Validate());
        Assert.Throws<ArgumentException>(() => RiverEditor.ReplaceChannel(river, tributary with { Mouth = new RiverJunction(Guid.NewGuid(), main.Nodes[0].Id, 0.5) }).Validate());
        Assert.Throws<ArgumentException>(() => RiverEditor.ReplaceChannel(river, tributary with { Mouth = new RiverJunction(main.Id, main.Nodes[^1].Id, 0.5) }).Validate());
        Assert.Throws<ArgumentException>(() => RiverEditor.SetNodeWidth(river, main.Id, 0, 0).Validate());
        Assert.Throws<ArgumentException>(() => (river with { Style = river.Style with { Opacity = 2 } }).Validate());
        var copy = river.WithNewIds(); copy.Validate();
        Assert.NotEqual(river.Id, copy.Id);
        Assert.Equal(copy.Channels[0].Id, copy.Channels[1].Mouth!.ParentChannelId);
    }

    [Fact]
    public void Commands_create_edit_absorb_and_delete_with_exact_history()
    {
        var session = Session(out var layer);
        var river = Rivers(out var main, out var tributary);
        session.Execute(new RiverCommand(layer, river.Id, river, "create"));
        Assert.Null(session.LastResourceError);
        var created = session.Document;
        Assert.Same(river, Get(created, layer));
        var widened = RiverEditor.SetNodeWidth(river, main.Id, 3, 44);
        session.Execute(new RiverCommand(layer, river.Id, widened, "width"));
        var edited = session.Document;
        Assert.True(session.Undo()); Assert.Same(created, session.Document);
        Assert.True(session.Redo()); Assert.Same(edited, session.Document);

        var other = new River { Name = "Ручей", Channels = [RiverEditor.NewChannel([new(900, 50), new(950, 200)], 5)] };
        session.Execute(new RiverCommand(layer, other.Id, other, "second"));
        Assert.Equal(2, ((ObjectLayer)session.Document.FindLayer(layer)!).Rivers.Length);
        var merged = RiverEditor.Absorb(widened, other);
        merged = RiverEditor.SetMouth(merged, other.Channels[0].Id, new RiverJunction(main.Id, main.Nodes[2].Id, 0.5));
        session.Execute(new RiverCommand(layer, river.Id, merged, "attach", absorbedRiverId: other.Id));
        Assert.Null(session.LastResourceError);
        var single = ((ObjectLayer)session.Document.FindLayer(layer)!).Rivers.Single();
        Assert.Equal(3, single.Channels.Length);
        var beforeDelete = session.Document;
        session.Execute(new RiverCommand(layer, river.Id, null, "delete"));
        Assert.Empty(((ObjectLayer)session.Document.FindLayer(layer)!).Rivers);
        Assert.True(session.Undo()); Assert.Same(beforeDelete, session.Document);

        session.Execute(new RiverCommand(layer, river.Id, RiverEditor.ReplaceChannel(single, main with { Mouth = new RiverJunction(tributary.Id, tributary.Nodes[0].Id, 0.5) }), "cycle"));
        Assert.NotNull(session.LastResourceError);
        Assert.Same(beforeDelete, session.Document);
    }

    [Fact]
    public void Canvas_resize_and_duplicate_layer_carry_rivers()
    {
        var session = Session(out var layer);
        var river = Rivers(out var main, out _);
        session.Execute(new RiverCommand(layer, river.Id, river, "create"));
        var plan = CanvasTransformService.PlanResize(session.Document, 1400, 900, CanvasAnchor.BottomRight);
        var resized = CanvasTransformService.ApplyResize(session.Document, plan);
        var carried = Get(resized, layer);
        Assert.Equal(main.Nodes[0].X + plan.OffsetX, carried.FindChannel(main.Id)!.Nodes[0].X);
        Assert.Equal(river.Id, carried.Id);
        var duplicate = (ObjectLayer)session.Document.FindLayer(layer)!.WithNewIds();
        Assert.Single(duplicate.Rivers); Assert.NotEqual(river.Id, duplicate.Rivers[0].Id);
        new MapDocument(DocumentId.New(), "Both", DateTimeOffset.UnixEpoch, session.Document.Canvas,
            [.. session.Document.Layers, duplicate]).Layers.OfType<ObjectLayer>().Sum(l => l.Rivers.Length).Should(2);
    }

    [Fact]
    public void Split_trim_reverse_keep_junctions_in_place_and_detach_moves_the_downstream_system()
    {
        var river = Rivers(out var main, out var tributary);
        var before = RiverGeometry.Evaluate(river).Channels[tributary.Id].End;

        var split = RiverEditor.SplitChannel(river, main.Id, 2, out var downstreamId);
        split.Validate();
        Assert.Equal(3, split.Channels.Length);
        var upstream = split.FindChannel(main.Id)!; var downstream = split.FindChannel(downstreamId)!;
        Assert.Equal(3, upstream.Nodes.Length); Assert.Equal(3, downstream.Nodes.Length);
        Assert.Equal(downstreamId, upstream.Mouth!.ParentChannelId);
        var afterSplit = RiverGeometry.Evaluate(split).Channels[tributary.Id].End;
        // The split node becomes a curve end: the spline around it straightens slightly, and the junction follows its segment.
        Assert.InRange(Math.Abs(afterSplit.X - before.X) + Math.Abs(afterSplit.Y - before.Y), 0, 25);

        var (remaining, detached) = RiverEditor.SplitRiver(river, main.Id, 2, "Нижняя");
        remaining.Validate(); detached.Validate();
        Assert.Null(remaining.FindChannel(main.Id)!.Mouth);
        Assert.Single(detached.Channels);
        Assert.Equal(2, remaining.Channels.Length);

        var trimmed = RiverEditor.Trim(river, main.Id, 1, 4);
        trimmed.Validate();
        Assert.Equal(4, trimmed.FindChannel(main.Id)!.Nodes.Length);
        var afterTrim = RiverGeometry.Evaluate(trimmed).Channels[tributary.Id].End;
        // Removing the node upstream reshapes that spline segment; the junction re-projects onto the new curve.
        Assert.InRange(Math.Abs(afterTrim.X - before.X) + Math.Abs(afterTrim.Y - before.Y), 0, 25);

        var reversed = RiverEditor.Reverse(river, main.Id);
        reversed.Validate();
        Assert.Equal(main.Nodes[^1].Id, reversed.FindChannel(main.Id)!.Nodes[0].Id);
        var afterReverse = RiverGeometry.Evaluate(reversed).Channels[tributary.Id].End;
        Assert.InRange(Math.Abs(afterReverse.X - before.X) + Math.Abs(afterReverse.Y - before.Y), 0, 6);
    }

    [Fact]
    public void Distributary_lake_and_flow_widths_evaluate_and_validate()
    {
        var river = Rivers(out var main, out var tributary);
        var lake = RiverEditor.NewLake([new(300, 500), new(420, 470), new(480, 560), new(380, 640), new(280, 600)]);
        var branch = RiverEditor.NewChannel([new(760, 300), new(820, 420)], 5) with { Source = new RiverJunction(main.Id, main.Nodes[2].Id, 0.4) };
        var outflow = RiverEditor.NewChannel([new(500, 620), new(700, 700)], 6) with { Source = new RiverJunction(lake.Id, lake.Nodes[2].Id, 0.5) };
        var inflow = RiverEditor.NewChannel([new(150, 650), new(240, 610)], 4) with { Mouth = new RiverJunction(lake.Id, lake.Nodes[4].Id, 0.5) };
        var system = river with { Channels = [.. river.Channels, lake, branch, outflow, inflow] };
        system.Validate();
        var set = RiverGeometry.Evaluate(system);
        Assert.True(set.Channels[branch.Id].LeavesParent);
        Assert.True(set.Channels[outflow.Id].LeavesParent);
        Assert.True(set.Channels[inflow.Id].JoinsParent);
        Assert.True(set.Channels[lake.Id].Closed);
        Assert.True(RiverGeometry.InsideLake(set.Channels[lake.Id], 380, 560));
        Assert.False(RiverGeometry.InsideLake(set.Channels[lake.Id], 100, 100));
        Assert.True(RiverGeometry.Contains(system, 380, 560));
        var start = set.Channels[branch.Id].Start;
        Assert.True(RiverGeometry.TryClosest(set, start.X, start.Y, 0.5, out var on, out _, out _, out _, out _, c => c.Id == main.Id) && on == main.Id);
        Assert.Throws<InvalidOperationException>(() => RiverEditor.SetMouth(system, main.Id, new RiverJunction(branch.Id, branch.Nodes[0].Id, 0.5)));
        Assert.Throws<ArgumentException>(() => (system with { Channels = [.. system.Channels.Select(c => c.Id == lake.Id ? c with { Mouth = new RiverJunction(main.Id, main.Nodes[0].Id, 0) } : c)] }).Validate());

        var flow = RiverEditor.WidthByFlow(system, 4, 10);
        flow.Validate();
        var mainWidths = flow.FindChannel(main.Id)!.Nodes.Select(n => n.Width).ToArray();
        Assert.Equal(4, mainWidths[0]);
        Assert.True(mainWidths[2] > mainWidths[1], "width grows past the tributary junction on segment 1");
        Assert.True(mainWidths[^1] > mainWidths[2]);
        Assert.Equal(lake.Nodes[0].Width, flow.FindChannel(lake.Id)!.Nodes[0].Width);
        Assert.True(flow.FindChannel(branch.Id)!.Nodes[0].Width >= 4);

        var meandered = RiverEditor.Meander(system, main.Id, 0.2, 7);
        meandered.Validate();
        Assert.True(meandered.FindChannel(main.Id)!.Nodes.Length > main.Nodes.Length);
        var smoothed = RiverEditor.Smooth(system, lake.Id);
        smoothed.Validate();
        Assert.Equal(lake.Nodes.Length * 2, smoothed.FindChannel(lake.Id)!.Nodes.Length);
        var copy = system.WithNewIds(); copy.Validate();
        Assert.Equal(2, RiverGeometry.Simplify([new(0, 0), new(10, 0.1), new(20, -0.1), new(30, 0)], 1).Count);
        Assert.Equal(3, RiverGeometry.Simplify([new(0, 0), new(15, 20), new(30, 0)], 1).Count);
    }
}

internal static class AssertExtensions
{
    public static void Should(this int actual, int expected) => Assert.Equal(expected, actual);
}
