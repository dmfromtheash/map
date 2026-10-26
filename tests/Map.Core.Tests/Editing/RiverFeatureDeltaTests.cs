using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R12: waterfall, rapids and ford on a river node, and the «Дельта» macro, each one undoable step.</summary>
public sealed class RiverFeatureDeltaTests
{
    private static EditingSession Session(out LayerId layer)
    {
        layer = LayerId.New();
        return new EditingSession(new MapDocument(DocumentId.New(), "Rivers", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1200, 800, "px", "#FFEEE6D2"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEEE6D2"), new ObjectLayer(layer, "Объекты", true, false, 1, [])]));
    }

    private static River Straight() => new()
    {
        Name = "Река",
        Channels = [RiverEditor.NewChannel([new(100, 400), new(400, 400), new(700, 420), new(1000, 400)], 20)],
    };

    private static River Get(MapDocument document, LayerId layer) => ((ObjectLayer)document.FindLayer(layer)!).Rivers.Single();

    [Fact]
    public void A_node_feature_is_set_undone_and_redone()
    {
        var session = Session(out var layer);
        var river = Straight();
        session.Execute(new RiverCommand(layer, river.Id, river, "create"));
        var channel = river.Channels[0];
        var falls = RiverEditor.SetNodeFeature(river, channel.Id, 1, RiverNodeFeature.Waterfall);
        falls = RiverEditor.SetNodeFeature(falls, channel.Id, 2, RiverNodeFeature.Ford);
        session.Execute(new RiverCommand(layer, river.Id, falls, "features"));
        var nodes = Get(session.Document, layer).Channels[0].Nodes;
        Assert.Equal(RiverNodeFeature.Waterfall, nodes[1].Feature);
        Assert.Equal(RiverNodeFeature.Ford, nodes[2].Feature);
        Assert.True(Get(session.Document, layer).HasNodeFeatures);
        Assert.True(session.Undo());
        Assert.False(Get(session.Document, layer).HasNodeFeatures);
        Assert.True(session.Redo());
        Assert.Equal(RiverNodeFeature.Waterfall, Get(session.Document, layer).Channels[0].Nodes[1].Feature);
    }

    [Fact]
    public void A_lake_takes_no_feature_and_an_unknown_feature_fails_validation()
    {
        var lake = new River { Name = "Озеро", Channels = [RiverEditor.NewLake([new(0, 0), new(100, 0), new(50, 80)])] };
        Assert.Throws<InvalidOperationException>(() => RiverEditor.SetNodeFeature(lake, lake.Channels[0].Id, 0, RiverNodeFeature.Rapids));
        var river = Straight();
        var channel = river.Channels[0];
        var broken = RiverEditor.ReplaceChannel(river, channel with { Nodes = channel.Nodes.SetItem(1, channel.Nodes[1] with { Feature = (RiverNodeFeature)9 }) });
        Assert.Throws<ArgumentException>(broken.Validate);
    }

    [Fact]
    public void The_delta_fans_arms_from_one_split_and_is_one_undo_step()
    {
        var session = Session(out var layer);
        var river = Straight();
        session.Execute(new RiverCommand(layer, river.Id, river, "create"));
        var created = session.Document;
        var main = river.Channels[0];

        var delta = RiverEditor.Delta(river, main.Id, arms: 5, length: 300, spreadDegrees: 80, seed: 7);
        delta.Validate();
        session.Execute(new RiverCommand(layer, river.Id, delta, "Дельта"));

        var result = Get(session.Document, layer);
        Assert.Equal(5, result.Channels.Length);
        var arms = result.Channels.Where(c => c.Id != main.Id).ToList();
        Assert.All(arms, arm =>
        {
            Assert.Equal(main.Id, arm.Source!.ParentChannelId);
            Assert.Null(arm.Mouth);
            Assert.True(arm.Nodes[^1].X > 800, "every arm reaches out towards the sea");
        });
        // One split for all of them, and they fan to both sides of the channel.
        Assert.Single(arms.Select(a => (a.Source!.ParentNodeId, a.Source.Fraction)).Distinct());
        Assert.Contains(arms, a => a.Nodes[^1].Y < 380);
        Assert.Contains(arms, a => a.Nodes[^1].Y > 440);
        // The arms share the flow: each is narrower than the channel above the split.
        Assert.All(arms, arm => Assert.True(arm.Nodes[0].Width < 20));
        Assert.True(result.FindChannel(main.Id)!.Nodes[^1].Width < 20);

        Assert.True(session.Undo());
        Assert.Same(created, session.Document);
        Assert.True(session.Redo());
        Assert.Equal(5, Get(session.Document, layer).Channels.Length);
    }

    [Fact]
    public void The_delta_is_deterministic_and_refuses_lakes_and_tributaries()
    {
        var river = Straight();
        var main = river.Channels[0];
        var a = RiverEditor.Delta(river, main.Id, 3, 250, 60, 11);
        var b = RiverEditor.Delta(river, main.Id, 3, 250, 60, 11);
        Assert.Equal(a.Channels.Skip(1).SelectMany(c => c.Nodes).Select(n => (n.X, n.Y, n.Width)),
            b.Channels.Skip(1).SelectMany(c => c.Nodes).Select(n => (n.X, n.Y, n.Width)));
        // Fewer than two arms is not a delta: the macro makes at least one distributary.
        Assert.Equal(2, RiverEditor.Delta(river, main.Id, 1, 250, 60, 1).Channels.Length);

        var lake = new River { Name = "Озеро", Channels = [RiverEditor.NewLake([new(0, 0), new(100, 0), new(50, 80)])] };
        Assert.Throws<InvalidOperationException>(() => RiverEditor.Delta(lake, lake.Channels[0].Id, 3, 50, 60, 1));
        var tributary = RiverEditor.NewChannel([new(500, 100), new(500, 380)], 6) with
        {
            Mouth = new RiverJunction(main.Id, main.Nodes[1].Id, 0.5),
        };
        var system = river with { Channels = [main, tributary] };
        Assert.Throws<InvalidOperationException>(() => RiverEditor.Delta(system, tributary.Id, 3, 50, 60, 1));
    }
}
