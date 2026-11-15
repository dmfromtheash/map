using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

public sealed class CoreHotPathEquivalenceTests
{
    [Fact]
    public void Clearance_bounds_keep_full_geometry_order_and_visibility_after_storage_edits()
    {
        var random = new Random(731);
        var layer = new ObjectLayer(LayerId.New(), "mixed", true, false, 1,
            Enumerable.Range(0, 1025).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "mixed",
                i * 13, i * 7, 24, 20, i % 180, "#FFFFFFFF", "#00000000", 0, i % 7 != 0, false)
            {
                Kind = i % 5 == 0 ? SceneObjectKind.Text : SceneObjectKind.Rectangle,
                Text = i % 11 == 0 ? null : new TextStyle { Content = "label", Importance = i % 9 - 1,
                    Blur = 2, ShadowOpacity = .5, ShadowOffsetX = -9, ShadowOffsetY = 7 },
            }).ToArray());
        var hidden = layer with { Id = LayerId.New(), Visible = false,
            Objects = [Item(0) with { Kind = SceneObjectKind.Text, Text = new() { Content = "hidden" } }] };
        var document = Document(layer, hidden) with { Labels = new() { ClearUnderLabels = true } };
        for (var step = 0; step < 60; step++)
        {
            var expected = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible)
                .SelectMany(l => l.Objects).Where(o => o.Kind == SceneObjectKind.Text && o.Visible && o.Text is not null)
                .Select(SceneGeometry.BoundsOf).ToArray();
            var visited = SceneObjectList.EnumeratedOnThisThread;
            Assert.Equal(expected, LabelPriority.LabelBounds(document));
            Assert.Equal(visited, SceneObjectList.EnumeratedOnThisThread);
            Assert.Empty(LabelPriority.LabelBounds(document with { Labels = new() { ClearUnderLabels = false } }));
            var at = random.Next(layer.Objects.Count);
            var replacement = Item(step) with { Id = layer.Objects[at].Id,
                Kind = step % 2 == 0 ? SceneObjectKind.Text : SceneObjectKind.Rectangle,
                Text = step % 3 == 0 ? null : new() { Content = "changed" } };
            var storage = layer.ObjectStorage.ReplaceAt(at, replacement);
            if (step % 4 == 0) storage = storage.RemoveRange(0, 1).InsertRange(500, [replacement with { Id = ObjectId.New() }]);
            layer = layer with { Objects = storage };
            document = document with { Layers = [document.Layers[0], layer, hidden] };
        }
    }

    [Fact]
    public void Feature_probes_match_original_scans_at_cell_edges_lakes_margins_and_revisions()
    {
        var river = new River { Channels = [RiverEditor.NewChannel([new(-2048, -1024), new(4096, -1024)], 31),
            RiverEditor.NewLake([new(1000, 1000), new(1100, 1000), new(1100, 1150), new(1000, 1150)])] };
        var road = RoadEditor.NewRoad([new(-2048, 1024), new(4096, 1024)], 23, seed: 9);
        var layer = new ObjectLayer(LayerId.New(), "features", true, false, 1, [])
        {
            Rivers = [river, RiverEditor.Translate(river.WithNewIds(), 0, 3000) with { Visible = false },
                new() { Channels = [RiverEditor.NewChannel([new(-90000, -4000), new(90000, -4000)], 15)] }],
            Roads = [road, RoadEditor.Translate(road.WithNewIds(), 0, 3000) with { Visible = false },
                RoadEditor.NewRoad([new(-90000, -6000), new(90000, -6000)], 15, seed: 2)],
        };
        var hidden = layer with { Id = LayerId.New(), Visible = false, Rivers = [RiverEditor.Translate(river.WithNewIds(), 0, 10000)],
            Roads = [RoadEditor.Translate(road.WithNewIds(), 0, 10000)] };
        var document = Document(layer, hidden);
        var random = new Random(519);
        var points = Enumerable.Range(0, 200).Select(_ => new VectorPoint(random.Next(-5000, 5000), random.Next(-7000, 15000)))
            .Concat(new[] { new VectorPoint(0, -1024), new(1024, 1024), new(1050, 1100), new(0, -4000), new(0, -6000),
                new(0, 1976), new(double.NaN, 0), new(double.PositiveInfinity, 0) }).ToArray();
        for (var revision = 0; revision < 3; revision++)
        {
            DocumentDerivedIndexes.Prepare(document);
            Assert.True(DocumentDerivedIndexes.IsPrepared(document));
            foreach (var margin in new[] { -3d, 0, 37, 10000 })
            foreach (var flags in Enumerable.Range(0, 8))
            {
                var rules = new PlacementAreaRules { AvoidWater = (flags & 1) != 0, OnlyOnWater = (flags & 2) != 0,
                    AvoidRoads = (flags & 4) != 0, RoadMargin = margin };
                var actual = new PlacementAreaRuleCheck(document, rules);
                foreach (var point in points)
                {
                    Assert.Equal(OriginalAllows(document, rules, point.X, point.Y), actual.Allows(point.X, point.Y));
                    var item = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "base", point.X, point.Y,
                        180, 200, 0, "#FFFFFFFF", "#00000000", 0, true, false);
                    Assert.Equal(OriginalBlocks(document, rules, item), actual.Blocks(item));
                }
            }
            layer = layer with { Rivers = layer.Rivers.SetItem(0, RiverEditor.Translate(river, 0, 1000 * (revision + 1))),
                Roads = layer.Roads.SetItem(0, RoadEditor.Translate(road, 0, 1000 * (revision + 1))) };
            document = document with { Layers = [document.Layers[0], layer, hidden] };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Persistent_replacement_directories_keep_snapshots_identity_and_text_summaries(bool textOnly)
    {
        var expected = Enumerable.Range(0, 1025).Select(i => Item(i) with
            { Kind = i % 3 == 0 ? SceneObjectKind.Text : SceneObjectKind.Rectangle,
                Text = i % 3 == 0 ? new() { Content = "label", Importance = i % 5 + 1 } : null }).ToArray();
        var storage = SceneObjectList.From(expected);
        if (textOnly) _ = storage.IndexOfTextId(default); else _ = storage.IndexOfId(default);
        var random = new Random(256);
        var history = new List<(SceneObjectList Storage, SceneObject[] Expected)>();
        for (var step = 0; step < 120; step++)
        {
            history.Add((storage, (SceneObject[])expected.Clone()));
            var replacements = new Dictionary<int, SceneObject>();
            foreach (var at in new[] { 0, 255, 256, 1024, random.Next(expected.Length) })
            {
                var item = Item(step) with { Id = step % 3 == 0 ? ObjectId.New() : expected[at].Id,
                    Kind = step % 2 == 0 ? SceneObjectKind.Text : SceneObjectKind.Rectangle,
                    Text = step % 2 == 0 ? new() { Content = "changed", Importance = step % 5 + 1 } : null };
                replacements[at] = item;
                expected[at] = item;
            }
            storage = storage.ReplaceMany(replacements);
            var labels = expected.Where(o => o.Kind == SceneObjectKind.Text).ToArray();
            Assert.Equal(labels, storage.TextObjects());
            Assert.Equal(labels.Length, storage.LabelCount(-1));
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Same(expected[i], storage[i]);
                if (!textOnly) Assert.Equal(i, storage.IndexOfId(expected[i].Id));
                else if (expected[i].Kind == SceneObjectKind.Text) Assert.Equal(i, storage.IndexOfTextId(expected[i].Id));
            }
        }
        foreach (var (snapshot, items) in history) Assert.Equal(items, snapshot);
    }

    // Frozen predicates from PlacementAreaRuleCheck before the spatial broad phase.
    private static bool OriginalAllows(MapDocument document, PlacementAreaRules rules, double x, double y)
    {
        var layers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToArray();
        var water = (rules.AvoidWater || rules.OnlyOnWater) && layers.SelectMany(l => l.Rivers).Where(r => r.Visible)
            .Select(r => RiverGeometry.Evaluate(r)).Any(r => r.Bounds.Intersects(new(x - 1, y - 1, x + 1, y + 1))
                && (RiverGeometry.TryClosest(r, x, y, 0, out _, out _, out _, out _, out _)
                    || r.Channels.Values.Any(g => RiverGeometry.InsideLake(g, x, y))));
        if (rules.AvoidWater && water || rules.OnlyOnWater && !water) return false;
        return !rules.AvoidRoads || !layers.SelectMany(l => l.Roads).Where(r => r.Visible).Select(r => RoadGeometry.Evaluate(r))
            .Any(r => r.Bounds.Intersects(new(x - rules.RoadMargin, y - rules.RoadMargin, x + rules.RoadMargin, y + rules.RoadMargin))
                && RoadGeometry.TryClosest(r, x, y, rules.RoadMargin, out _, out _, out _, out _, out _));
    }

    private static bool OriginalBlocks(MapDocument document, PlacementAreaRules rules, SceneObject item)
    {
        var foot = PlacementAreaRuleCheck.Foot(item);
        if (!OriginalAllows(document, rules, foot.X, foot.Y)) return true;
        if (!rules.AvoidWater && !rules.AvoidRoads) return false;
        var left = item.X + item.Width * .2; var right = item.X + item.Width * .8;
        var top = item.Y + item.Height * .55; var bottom = item.Y + item.Height * .95;
        return new[] { (left, bottom), (right, bottom), (left, top), (right, top), (foot.X, top), (foot.X, bottom) }
            .Any(p => !OriginalAllows(document, rules with { OnlyOnWater = false }, p.Item1, p.Item2));
    }

    private static SceneObject Item(int i) => new(ObjectId.New(), SceneObjectKind.Rectangle, "item",
        i * 13, i * 7, 24, 20, 0, "#FFFFFFFF", "#00000000", 0, true, false);

    private static MapDocument Document(params ObjectLayer[] layers) => new(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(100000, 100000, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), .. layers]);
}
