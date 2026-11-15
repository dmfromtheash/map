using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class PlacementFeatureIndexTests
{
    [Fact]
    public void Indexed_rules_match_the_original_scan_including_lakes_visibility_and_margins()
    {
        var river = new River { Channels = [RiverEditor.NewChannel([new(-1500, -1024), new(1600, -1024)], 80)] };
        var lake = new River { Channels = [RiverEditor.NewChannel(
            [new(950, 950), new(1400, 950), new(1400, 1400), new(950, 1400)], 20) with { Closed = true }] };
        var road = RoadEditor.NewRoad([new(-1400, 1024), new(1600, 1024)], 32);
        var layer = new ObjectLayer(LayerId.New(), "features", true, false, 1, [])
        {
            Rivers = [river, lake, RiverEditor.Translate(river, 0, 1000) with { Id = Guid.NewGuid(), Visible = false }],
            Roads = [road, RoadEditor.Translate(road, 0, -1000) with { Id = Guid.NewGuid(), Visible = false }],
        };
        var hidden = layer with { Id = LayerId.New(), Visible = false,
            Rivers = [RiverEditor.Translate(river, 0, 1800) with { Id = Guid.NewGuid() }], Roads = [] };
        var document = Document(layer, hidden);
        DocumentDerivedIndexes.Prepare(document);
        foreach (var rules in new PlacementAreaRules[]
        {
            new() { AvoidWater = true }, new() { OnlyOnWater = true },
            new() { AvoidRoads = true }, new() { AvoidWater = true, AvoidRoads = true, RoadMargin = 150 },
            new() { AvoidRoads = true, RoadMargin = 100_000 },
        })
        {
            var indexed = new PlacementAreaRuleCheck(document, rules);
            for (var y = -1300; y < 1600; y += 73)
            for (var x = -1600; x < 1800; x += 97)
            {
                Assert.Equal(OriginalAllows(document, rules, x, y), indexed.Allows(x, y));
                var item = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "probe", x, y,
                    80, 100, 0, "#FFFFFFFF", "#00000000", 0, true, false);
                var foot = PlacementAreaRuleCheck.Foot(item);
                var blocked = !OriginalAllows(document, rules, foot.X, foot.Y);
                if (rules.AvoidWater || rules.AvoidRoads)
                {
                    var avoidance = rules with { OnlyOnWater = false };
                    foreach (var (px, py) in new[] { (x + 16d, y + 95d), (x + 64d, y + 95d),
                        (x + 16d, y + 55d), (x + 64d, y + 55d), (foot.X, y + 55d), (foot.X, y + 95d) })
                        blocked |= !OriginalAllows(document, avoidance, px, py);
                }
                Assert.Equal(blocked, indexed.Blocks(item));
            }
        }
    }

    [Fact]
    public void Prepared_feature_edits_update_the_index_and_history_keeps_both_branches()
    {
        var river = new River { Channels = [RiverEditor.NewChannel([new(0, 100), new(500, 100)], 40)] };
        var road = RoadEditor.NewRoad([new(0, 700), new(500, 700)], 40);
        var layer = new ObjectLayer(LayerId.New(), "features", true, false, 1, []) { Rivers = [river], Roads = [road] };
        var document = Document(layer);
        DocumentDerivedIndexes.Prepare(document);
        var session = new EditingSession(document);
        session.Execute(new RiverCommand(layer.Id, river.Id, RiverEditor.Translate(river, 0, 1000), "move"));
        var changed = (ObjectLayer)session.Document.FindLayer(layer.Id)!;
        Assert.True(changed.PlacementFeaturesPrepared);
        Assert.Same(layer.PlacementRoads, changed.PlacementRoads);
        Assert.False(new PlacementAreaRuleCheck(session.Document, new() { AvoidWater = true }).IsWater(200, 100));
        Assert.True(new PlacementAreaRuleCheck(session.Document, new() { AvoidWater = true }).IsWater(200, 1100));
        Assert.True(new PlacementAreaRuleCheck(document, new() { AvoidWater = true }).IsWater(200, 100));
        Assert.True(session.Undo());
        Assert.True(new PlacementAreaRuleCheck(session.Document, new() { AvoidWater = true }).IsWater(200, 100));
        Assert.True(session.Redo());
        session.Execute(new RoadCommand(layer.Id, road.Id, RoadEditor.Translate(road, 0, 1000), "move"));
        changed = (ObjectLayer)session.Document.FindLayer(layer.Id)!;
        Assert.True(changed.PlacementFeaturesPrepared);
        var check = new PlacementAreaRuleCheck(session.Document, new() { AvoidRoads = true });
        Assert.True(check.Allows(200, 700)); Assert.False(check.Allows(200, 1700));
        Assert.True(session.Undo());
        Assert.False(new PlacementAreaRuleCheck(session.Document, new() { AvoidRoads = true }).Allows(200, 700));
        Assert.True(session.Redo());
        session.Execute(new RiverCommand(layer.Id, river.Id, RiverEditor.Translate(river, 0, 1000) with { Visible = false }, "hide"));
        Assert.False(new PlacementAreaRuleCheck(session.Document, new() { AvoidWater = true }).IsWater(200, 1100));
        Assert.True(session.Undo());
        Assert.True(new PlacementAreaRuleCheck(session.Document, new() { AvoidWater = true }).IsWater(200, 1100));
    }

    [Fact]
    public void Dense_buckets_and_oversized_bounds_derive_without_mutating_the_source()
    {
        var items = Enumerable.Range(0, 3000).Select(i => new Box(Guid.NewGuid(), new(0, 0, 10, 10))).ToArray();
        var huge = new Box(Guid.NewGuid(), new(-100_000, -100_000, 100_000, 100_000));
        var source = FeatureSpatialIndex<Box>.Build(items.Append(huge), b => b.Id, b => b.Bounds);
        var moved = items[0] with { Bounds = new(4096, 4096, 4100, 4100) };
        var target = source.Change([items[0].Id, huge.Id], [moved], b => b.Id, b => b.Bounds);
        var local = new WorldBounds(0, 0, 1, 1);
        Assert.True(source.Any(local, items[0].Id, static (b, id) => b.Id == id));
        Assert.False(target.Any(local, items[0].Id, static (b, id) => b.Id == id));
        Assert.True(source.Any(local, huge.Id, static (b, id) => b.Id == id));
        Assert.False(target.Any(local, huge.Id, static (b, id) => b.Id == id));
        Assert.True(target.Any(new(4096, 4096, 4097, 4097), moved, static (b, expected) => ReferenceEquals(b, expected)));
        foreach (var item in items.Skip(1))
            Assert.True(target.Any(local, item.Id, static (b, id) => b.Id == id));
        Assert.True(target.Any(new(-100_000, -100_000, 100_000, 100_000), moved, static (b, expected) => ReferenceEquals(b, expected)));
    }

    private sealed record Box(Guid Id, WorldBounds Bounds);

    private static bool OriginalAllows(MapDocument document, PlacementAreaRules rules, double x, double y)
    {
        var layers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToArray();
        var water = layers.SelectMany(l => l.Rivers).Where(r => r.Visible).Select(r => RiverGeometry.Evaluate(r))
            .Any(r => r.Bounds.Intersects(new(x - 1, y - 1, x + 1, y + 1))
                && (RiverGeometry.TryClosest(r, x, y, 0, out _, out _, out _, out _, out _)
                    || r.Channels.Values.Any(c => RiverGeometry.InsideLake(c, x, y))));
        if (rules.AvoidWater && water || rules.OnlyOnWater && !water) return false;
        return !rules.AvoidRoads || !layers.SelectMany(l => l.Roads).Where(r => r.Visible).Select(r => RoadGeometry.Evaluate(r))
            .Any(r => r.Bounds.Intersects(new(x - rules.RoadMargin, y - rules.RoadMargin, x + rules.RoadMargin, y + rules.RoadMargin))
                && RoadGeometry.TryClosest(r, x, y, rules.RoadMargin, out _, out _, out _, out _, out _));
    }

    private static MapDocument Document(params ObjectLayer[] layers) => new(DocumentId.New(), "features", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(100_000, 100_000, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), .. layers]);
}
