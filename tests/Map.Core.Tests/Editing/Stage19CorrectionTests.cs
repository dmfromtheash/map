using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;

namespace Map.Core.Tests.Editing;

/// <summary>
/// The Stage 19 corrections the owner review asked for, at the level where they are decided:
/// admission by the real cost of a small vector edit, shape control that does not multiply nodes,
/// picking by the drawn body, and the land clip that used to swallow a whole lake in silence.
/// </summary>
public sealed class Stage19CorrectionTests
{
    private const long MiB = WorkingMemoryBudget.MiB;
    private const long GiB = 1024 * MiB;

    private static MapDocument DocumentWithLayer(out ObjectLayer layer)
    {
        layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        return new MapDocument(DocumentId.New(), "Карта", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFEDE3CD"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD"), layer]);
    }

    private static River FourNodeRiver() => new()
    {
        Name = "Река",
        Channels = [RiverEditor.NewChannel(
            [new VectorPoint(120, 220), new VectorPoint(380, 320), new VectorPoint(640, 280), new VectorPoint(900, 600)], 12)],
    };

    [Fact]
    public void A_four_node_river_is_admitted_where_the_flat_edit_reserve_refused_it()
    {
        // The failure the owner hit in a real window: Windows reported 506 MiB free, and the
        // shared 16 MiB edit reserve demanded 512 MiB of idle RAM on top of it.
        var memory = new WorkingMemorySnapshot(16 * GiB, 506 * MiB, GiB);
        var budget = new WorkingMemoryBudget(() => memory);
        var document = DocumentWithLayer(out var layer);
        var session = new EditingSession(document, budget);
        var river = FourNodeRiver();
        var command = new RiverCommand(layer.Id, river.Id, river, "Создать реку");

        Assert.NotNull(command.MeasuredWorkingBytes);
        Assert.True(command.MeasuredWorkingBytes < WorkingMemoryBudget.EditReserveBytes);

        session.Execute(command);
        Assert.Null(session.LastResourceError);
        Assert.Equal(1, session.UndoDepth);
        Assert.Single(session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == layer.Id).Rivers);

        // Moving and deleting the same small river stay admitted at the same free memory, and one
        // undo each puts the map back exactly as it was.
        var moved = RiverEditor.Translate(river, 25, -10);
        session.Execute(new RiverCommand(layer.Id, river.Id, moved, "Переместить воду"));
        Assert.Equal(2, session.UndoDepth);
        session.Execute(new RiverCommand(layer.Id, river.Id, null, "Удалить воду"));
        Assert.Empty(session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == layer.Id).Rivers);
        Assert.True(session.Undo());
        Assert.Single(session.Document.Layers.OfType<ObjectLayer>().Single(l => l.Id == layer.Id).Rivers);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void A_measured_operation_never_falls_off_a_cliff_when_it_grows_by_one_object()
    {
        // The owner's second report: a map of one rectangle refused to save at 506 MiB free,
        // because the estimate crossed a fixed 4 MiB boundary and inherited the 512 MiB floor.
        // The reserve a measured operation owes has to be continuous in its size.
        var memory = new WorkingMemorySnapshot(16 * GiB, 506 * MiB, GiB);
        var budget = new WorkingMemoryBudget(() => memory);
        foreach (var bytes in new[] { 64 * 1024L, MiB, 4 * MiB, 4 * MiB + 4096, 8 * MiB, 32 * MiB, 64 * MiB })
        {
            Assert.True(budget.TryReserve(bytes, out var lease, out var reason, measured: true),
                $"{bytes / 1024} KiB of measured work was refused at 506 MiB free: {reason}");
            lease!.Dispose();
        }

        // Work of unknown size at the same free memory still answers to the cautious floor, and
        // measured work large enough to matter still does too.
        Assert.False(budget.TryReserve(WorkingMemoryBudget.EditReserveBytes, out _, out _));
        Assert.False(budget.TryReserve(200 * MiB, out _, out _, measured: true));
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void A_rubber_band_over_the_visible_water_finds_the_river()
    {
        // The band crosses the drawn surface of a 80-wide river without reaching its centreline.
        var wide = RiverEditor.NewChannel([new VectorPoint(0, 200), new VectorPoint(400, 200)], 80);
        var river = new River { Name = "Река", Channels = [wide] };
        river.Validate();
        var set = RiverGeometry.Evaluate(river);

        Assert.True(RiverGeometry.IntersectsRect(set, new WorldBounds(100, 150, 300, 175)));
        Assert.True(RiverGeometry.IntersectsRect(set, new WorldBounds(100, 225, 300, 260)));
        // Clear of the body on both sides: nothing is selected by accident.
        Assert.False(RiverGeometry.IntersectsRect(set, new WorldBounds(100, 100, 300, 155)));
        Assert.False(RiverGeometry.IntersectsRect(set, new WorldBounds(100, 246, 300, 300)));

        // A band drawn entirely inside a lake is still a selection of that lake.
        var lake = new River
        {
            Name = "Озеро",
            Channels = [RiverEditor.NewLake(
                [new VectorPoint(600, 100), new VectorPoint(800, 120), new VectorPoint(760, 300), new VectorPoint(600, 260)])],
        };
        lake.Validate();
        var lakeSet = RiverGeometry.Evaluate(lake);
        Assert.True(RiverGeometry.IntersectsRect(lakeSet, new WorldBounds(660, 160, 700, 200)));
        Assert.False(RiverGeometry.IntersectsRect(lakeSet, new WorldBounds(900, 400, 950, 450)));
    }

    [Fact]
    public void Work_of_unknown_cost_still_obeys_the_ordinary_reserve()
    {
        // The tiny tier is not a way around the policy: only a command that states a small cost
        // gets it, and a command that states nothing keeps the shared edit reserve.
        var memory = new WorkingMemorySnapshot(16 * GiB, 506 * MiB, GiB);
        var budget = new WorkingMemoryBudget(() => memory);
        var document = DocumentWithLayer(out _);
        var session = new EditingSession(document, budget);
        session.Execute(new RenameCommand("Переименовать"));
        Assert.NotNull(session.LastResourceError);
        Assert.Equal(0, session.UndoDepth);
        Assert.Same(document, session.Document);
    }

    [Fact]
    public void Derived_caches_are_asked_to_let_go_before_an_edit_is_refused()
    {
        var memory = new WorkingMemorySnapshot(16 * GiB, 400 * MiB, GiB);
        var budget = new WorkingMemoryBudget(() => memory);
        var released = 0;
        void Release()
        {
            released++;
            memory = memory with { AvailableBytes = 4 * GiB };
        }

        WorkingMemoryBudget.ReleaseDerivedCaches += Release;
        try
        {
            Assert.True(budget.TryReserve(WorkingMemoryBudget.EditReserveBytes, out var lease, out var reason));
            Assert.Null(reason);
            Assert.Equal(1, released);
            lease!.Dispose();
        }
        finally { WorkingMemoryBudget.ReleaseDerivedCaches -= Release; }
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void Smoothing_never_adds_nodes_and_curvature_changes_the_drawn_course()
    {
        var river = FourNodeRiver();
        var channelId = river.Channels[0].Id;
        var straightened = RiverEditor.SetChannelTension(river, channelId, 0);
        var relaxed = RiverEditor.Smooth(RiverEditor.Smooth(RiverEditor.Smooth(river, channelId), channelId), channelId);
        Assert.Equal(4, relaxed.FindChannel(channelId)!.Nodes.Length);

        var wide = RiverEditor.SetChannelTension(river, channelId, 2.2);
        wide.Validate(); straightened.Validate();
        Assert.Equal(4, wide.FindChannel(channelId)!.Nodes.Length);
        var midStraight = MidPoint(straightened, channelId);
        var midWide = MidPoint(wide, channelId);
        Assert.True(Math.Abs(midStraight.X - midWide.X) + Math.Abs(midStraight.Y - midWide.Y) > 1,
            "the curvature handle has to move the evaluated course");

        // A sharp node is a corner whatever the stored tension says.
        var sharp = RiverEditor.SetNodeSharp(wide, channelId, 1, true);
        Assert.Equal(0, sharp.FindChannel(channelId)!.Nodes[1].EffectiveTension);
    }

    private static VectorPoint MidPoint(River river, Guid channelId)
    {
        var samples = RiverGeometry.Evaluate(river).Channels[channelId].Samples;
        var sample = samples[samples.Length / 2];
        return new VectorPoint(sample.X, sample.Y);
    }

    [Fact]
    public void Simplifying_a_stroke_keeps_the_stated_accuracy_and_the_junctions()
    {
        var points = new List<VectorPoint>();
        for (var i = 0; i <= 40; i++) points.Add(new VectorPoint(100 + i * 10, 300 + (i % 2 == 0 ? 0.2 : -0.2)));
        var main = RiverEditor.NewChannel(points, 10);
        var tributary = RiverEditor.NewChannel([new VectorPoint(300, 200), new VectorPoint(320, 290)], 4)
            with { Mouth = new RiverJunction(main.Id, main.Nodes[20].Id, 0.5) };
        var river = new River { Name = "Река", Channels = [main, tributary] };
        river.Validate();
        var mouthBefore = RiverGeometry.Evaluate(river).Channels[tributary.Id].End;

        var simplified = RiverEditor.SimplifyChannel(river, main.Id, 2);
        simplified.Validate();
        var kept = simplified.FindChannel(main.Id)!;
        Assert.True(kept.Nodes.Length < points.Count / 4, $"expected a real reduction, got {kept.Nodes.Length}");
        Assert.Equal(2, simplified.Channels.Length);
        var mouthAfter = RiverGeometry.Evaluate(simplified).Channels[tributary.Id].End;
        Assert.True(Math.Abs(mouthBefore.X - mouthAfter.X) + Math.Abs(mouthBefore.Y - mouthAfter.Y) < 6,
            "the tributary keeps its place on the simplified parent");
    }

    [Fact]
    public void A_click_inside_a_wide_river_picks_it_over_a_nearer_thin_centreline()
    {
        var wide = RiverEditor.NewChannel([new VectorPoint(0, 100), new VectorPoint(400, 100)], 80);
        var thin = RiverEditor.NewChannel([new VectorPoint(0, 160), new VectorPoint(400, 160)], 2);
        var river = new River { Name = "Река", Channels = [wide, thin] };
        river.Validate();
        var set = RiverGeometry.Evaluate(river);

        // 130 is 30 units from the wide centreline but well inside its 40-unit half width, and
        // 30 units from the thin channel, which it is outside of.
        Assert.True(RiverGeometry.TryClosest(set, 200, 130, 6, out var hit, out _, out _, out _, out _));
        Assert.Equal(wide.Id, hit);

        // Just outside both bodies the nearer one still wins.
        Assert.True(RiverGeometry.TryClosest(set, 200, 158, 6, out var near, out _, out _, out _, out _));
        Assert.Equal(thin.Id, near);
    }

    [Fact]
    public void A_lake_drawn_off_the_land_is_reported_as_hidden_by_the_clip()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), coverage);
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, new LandEdgeStyle());
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Карта", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFEDE3CD"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD"), land, objects]);

        var offLand = new River
        {
            Name = "Озеро",
            Channels = [RiverEditor.NewLake([new VectorPoint(600, 600), new VectorPoint(700, 600), new VectorPoint(650, 700)])],
        };
        offLand.Validate();
        Assert.True(RiverVisibility.IsHiddenByLandClip(document, offLand));
        Assert.False(RiverVisibility.IsHiddenByLandClip(document, offLand with { Style = offLand.Style with { ClipToLand = false } }));

        var onLand = new River
        {
            Name = "Озеро на суше",
            Channels = [RiverEditor.NewLake([new VectorPoint(40, 40), new VectorPoint(140, 40), new VectorPoint(90, 140)])],
        };
        onLand.Validate();
        Assert.False(RiverVisibility.IsHiddenByLandClip(document, onLand));

        // With no land at all the clip has nothing to hide behind and never reports a problem.
        var noLand = new MapDocument(DocumentId.New(), "Карта", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFEDE3CD"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD"), objects]);
        Assert.False(RiverVisibility.IsHiddenByLandClip(noLand, offLand));
    }

    [Fact]
    public void Moving_a_whole_system_keeps_every_junction_in_place()
    {
        var main = RiverEditor.NewChannel([new VectorPoint(100, 100), new VectorPoint(300, 140), new VectorPoint(500, 100)], 12);
        var tributary = RiverEditor.NewChannel([new VectorPoint(280, 20), new VectorPoint(300, 90)], 5)
            with { Mouth = new RiverJunction(main.Id, main.Nodes[0].Id, 0.9) };
        var river = new River { Name = "Река", Channels = [main, tributary] };
        river.Validate();

        var moved = RiverEditor.Translate(river, 40, -15);
        moved.Validate();
        Assert.Equal(river.Channels.Select(c => c.Mouth), moved.Channels.Select(c => c.Mouth));
        var before = RiverGeometry.Evaluate(river).Channels[tributary.Id].End;
        var after = RiverGeometry.Evaluate(moved).Channels[tributary.Id].End;
        Assert.Equal(before.X + 40, after.X, 6);
        Assert.Equal(before.Y - 15, after.Y, 6);
    }

    private sealed class RenameCommand(string name) : IEditCommand
    {
        private MapDocument? _before;
        public string Name => name;
        public MapDocument Apply(MapDocument document) { _before ??= document; return document with { Title = name }; }
        public MapDocument Revert(MapDocument document) => _before!;
    }
}
