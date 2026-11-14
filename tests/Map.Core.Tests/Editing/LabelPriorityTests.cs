using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class LabelPriorityTests
{
    [Fact]
    public void Text_order_slots_survive_kind_changes_and_later_rewrites_without_general_identity()
    {
        var first = Label(0, 0, 3, 20);
        var stamp = first with { Id = ObjectId.New(), Kind = SceneObjectKind.Stamp, Text = null };
        var layer = new ObjectLayer(LayerId.New(), "mixed", true, false, 1, [first, stamp]);
        var document = Document(layer);
        LabelPriority.Prepare(document);
        var asText = stamp with { Kind = SceneObjectKind.Text, Text = first.Text };
        Replace(1, asText);
        Replace(0, SceneObjectClone.At(first, 1, first.Y));
        Replace(0, SceneObjectClone.At(first, 2, first.Y));
        Assert.Equal(0, layer.ObjectStorage.IndexOfTextId(first.Id));
        Assert.Equal(1, layer.ObjectStorage.IndexOfTextId(stamp.Id));
        Assert.False(layer.ObjectStorage.HasIdentityTable);
        Assert.True(Original(document, 1).SetEquals(LabelPriority.Hidden(document, 1)));

        void Replace(int position, SceneObject replacement)
        {
            var source = layer;
            layer = source with { Objects = source.ObjectStorage.ReplaceAt(position, replacement) };
            ObjectLayerChanges.RegisterDerived(source, layer, [position], [position]);
            document = document with { Layers = [document.Layers[0], layer] };
        }
    }

    [Fact]
    public void Cold_label_preparation_reads_only_text_chunks_and_needs_no_stamp_identity_table()
    {
        var labels = Enumerable.Range(0, 200).Select(i => Label(i * 60, 0, 3, 20)).ToArray();
        var stamp = Label(0, 0, 3, 20) with { Kind = SceneObjectKind.Stamp, Text = null };
        var layer = new ObjectLayer(LayerId.New(), "mixed", true, false, 1,
            Enumerable.Range(0, 100_000).Select(_ => stamp with { Id = ObjectId.New() }).Concat(labels).ToArray());
        var document = Document(layer);
        var enumerated = SceneObjectList.EnumeratedOnThisThread;
        LabelPriority.Prepare(document);
        Assert.True(LabelPriority.IsPrepared(document));
        var result = LabelPriority.Hidden(document, 1);
        for (var i = labels.Length - 1; i >= 0; i--) Assert.Equal(i % 2 == 1, result.Contains(labels[i].Id));
        Assert.Equal(enumerated, SceneObjectList.EnumeratedOnThisThread);
        Assert.False(layer.ObjectStorage.HasIdentityTable);
    }

    [Fact]
    public void Pending_frame_has_exact_scale_and_cap_and_defers_only_unresolved_overlaps()
    {
        var labels = Enumerable.Range(0, 4000).Select(i => Label(i * 512, 0, 3, 20)).ToArray();
        var document = Document(new ObjectLayer(LayerId.New(), "labels", true, false, 1, labels));
        var frame = new LabelPriority.FrameVisibility(document, -1);
        Assert.True(frame.IsPending);
        Assert.False(frame.Contains(labels[0]));
        Assert.False(frame.Contains(labels[100]));
        Assert.True(frame.Contains(labels[^1])); // bounded work, exact decision not ready yet
        Assert.False(frame.Contains(labels[0])); // already known decisions stay exact
        LabelPriority.Prepare(document);
        Assert.False(LabelPriority.ForFrame(document, 1, () => { }).Contains(labels[^1]));

        var crowded = Enumerable.Range(0, 4001).Select(_ => Label(0, 0, 3, 20)).ToArray();
        var capped = Document(new ObjectLayer(LayerId.New(), "labels", true, false, 1, crowded));
        var capFrame = new LabelPriority.FrameVisibility(capped, -1);
        Assert.False(capFrame.IsPending);
        Assert.All(crowded, label => Assert.False(capFrame.Contains(label)));
        var scaleFrame = new LabelPriority.FrameVisibility(capped, 1 << 5);
        Assert.True(scaleFrame.Contains(crowded[0]));
    }

    [Fact]
    public async Task Pending_frame_requests_refinement_and_preparation_is_shared_with_editor_lookups()
    {
        var labels = Enumerable.Range(0, 4000).Select(i => Label(i * 60, 0, 3, 20)).ToArray();
        var document = Document(new ObjectLayer(LayerId.New(), "labels", true, false, 1, labels));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = LabelPriority.ForFrame(document, 1, () => ready.TrySetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(LabelPriority.IsPrepared(document));
        var settled = LabelPriority.ForFrame(document, 1, () => { });
        var expected = Original(document, 1);
        Assert.All(labels, label => Assert.Equal(expected.Contains(label.Id), settled.Contains(label)));
        DocumentDerivedIndexes.Prepare(document);
        Assert.True(DocumentDerivedIndexes.IsPrepared(document));
    }

    [Fact]
    public void Randomized_membership_and_enumeration_match_the_original_greedy_algorithm()
    {
        for (var seed = 0; seed < 64; seed++)
        {
            var random = new Random(seed);
            var layers = Enumerable.Range(0, 3).Select(l => new ObjectLayer(LayerId.New(), "labels", l != 2 || seed % 3 != 0,
                false, 1, Enumerable.Range(0, 90).Select(_ => RandomObject(random)).ToArray())).ToArray();
            var document = Document(layers);
            for (var edit = 0; edit < 12; edit++)
            {
                AssertEquivalent(document, random);
                var at = random.Next(layers.Length);
                var source = layers[at];
                var position = random.Next(source.Objects.Count);
                var replacement = RandomObject(random) with { Id = source.Objects[position].Id };
                layers[at] = source with { Objects = source.ObjectStorage.ReplaceAt(position, replacement) };
                ObjectLayerChanges.RegisterDerived(source, layers[at], [position], [position]);
                document = document with { Layers = [document.Layers[0], .. layers] };
            }
        }
    }

    [Fact]
    public void Coalesced_structural_edits_layer_order_visibility_settings_and_undo_preserve_ties()
    {
        var labels = Enumerable.Range(0, 30).Select(i => Label(i * 12, 0, 2, 20)).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "labels", true, false, 1, labels);
        var document = Document(layer);
        var random = new Random(99);
        AssertEquivalent(document, random);
        var session = new EditingSession(document);
        session.Execute(new DeleteObjectsCommand(session.Document, [labels[0].Id, labels[5].Id, labels[12].Id]));
        session.Execute(new AddObjectCommand(layer.Id, Label(0, 0, 2, 20)));
        AssertEquivalent(session.Document, random);
        Assert.True(session.Undo());
        AssertEquivalent(session.Document, random);
        Assert.True(session.Undo());
        AssertEquivalent(session.Document, random);
        Assert.True(session.Redo());
        AssertEquivalent(session.Document, random);

        // Reinsert at the front, with equal priority; old indices have all shifted.
        var removed = (ObjectLayer)session.Document.Layers[1];
        var inserted = removed with { Objects = removed.ObjectStorage.InsertRange(0, [Label(0, 0, 2, 20)]) };
        ObjectLayerChanges.RegisterDerived(removed, inserted, [], [0], orderCompacted: true);
        AssertEquivalent(document with { Layers = [document.Layers[0], inserted] }, random);
        var other = layer with { Id = LayerId.New(), Objects = [Label(0, 0, 2, 20)] };
        AssertEquivalent(Document(layer, other), random);
        AssertEquivalent(Document(other, layer), random);
        AssertEquivalent(Document(layer with { Visible = false }, other), random);
    }

    [Fact]
    public void Cap_is_applied_after_scale_filtering_and_includes_exactly_4000_candidates()
    {
        foreach (var count in new[] { 3999, 4000, 4001, 10000, 50000 })
        {
            var labels = Enumerable.Range(0, count).Select(i => Label(0, 0, i < 3 ? 5 : 1, 20)).ToArray();
            var document = Document(new ObjectLayer(LayerId.New(), "labels", true, false, 1, labels));
            foreach (var scale in new[] { 1d, .1 })
            {
                var expected = Original(document, scale);
                var actual = LabelPriority.Hidden(document, scale);
                Assert.Equal(expected.Contains(labels[1].Id), actual.Contains(labels[1].Id));
                Assert.True(expected.SetEquals(actual));
            }
        }
    }

    [Fact]
    public void View_queries_keep_offscreen_dependencies_and_do_not_recurse_on_the_call_stack()
    {
        // Inset width is 80, step is 60: each box meets only its neighbours. The last
        // label depends on the whole chain, including labels outside a hypothetical view.
        var labels = Enumerable.Range(0, 4000).Select(i => Label(i * 60, 0, 3, 20)).ToArray();
        var document = Document(new ObjectLayer(LayerId.New(), "labels", true, false, 1, labels));
        var actual = LabelPriority.Hidden(document, 1);
        for (var i = labels.Length - 1; i >= 0; i--) Assert.Equal(i % 2 == 1, actual.Contains(labels[i].Id));
        Assert.True(Original(document, 1).SetEquals(actual));
    }

    [Fact]
    public void Grid_edges_oversized_negative_tiny_and_nonfinite_boxes_match_original()
    {
        SceneObject[] labels =
        [
            Label(-266, -10, 3, 20), Label(-186, -10, 3, 20), // inset boxes touch at -176
            Label(246, 0, 3, 20), Label(326, 0, 3, 20),
            SceneObjectClone.At(Label(0, 0, 4, 20), -50000, -50000, 100000, 100000),
            SceneObjectClone.At(Label(0, 0, 5, 20), 0, 0, .001, .001),
            Label(double.MaxValue, 0, 1, 20), Label(double.NaN, 0, 3, 20),
            Label(100, 100, 3, double.NaN), Label(110, 110, 3, 20),
        ];
        AssertEquivalent(Document(new ObjectLayer(LayerId.New(), "labels", true, false, 1, labels)), new Random(7));
    }

    [Fact]
    public void Set_contract_and_concurrent_membership_are_consistent()
    {
        var labels = Enumerable.Range(0, 100).Select(i => Label(i * 50, 0, 1 + i % 5, 20)).ToArray();
        var document = Document(new ObjectLayer(LayerId.New(), "labels", true, false, 1, labels));
        var expected = Original(document, .2);
        var actual = LabelPriority.Hidden(document, .2);
        Parallel.For(0, labels.Length, i => Assert.Equal(expected.Contains(labels[i].Id), actual.Contains(labels[i].Id)));
        Assert.Equal(expected.Count, actual.Count);
        Assert.True(actual.SetEquals(expected));
        Assert.True(actual.IsSubsetOf(expected));
        Assert.True(actual.IsSupersetOf(expected));
        Assert.False(actual.IsProperSubsetOf(expected));
        Assert.False(actual.IsProperSupersetOf(expected));
        Assert.True(actual.Overlaps(expected));
        Assert.Same(actual, LabelPriority.Hidden(document, .21));
        Assert.False(actual.Contains(ObjectId.New()));
    }

    private static void AssertEquivalent(MapDocument document, Random random)
    {
        foreach (var settings in new[]
        {
            new LabelSettings(), new LabelSettings { ByScale = true },
            new LabelSettings { ResolveOverlaps = true },
            new LabelSettings { ByScale = true, ResolveOverlaps = true },
            new LabelSettings { ByScale = true, ResolveOverlaps = true, MinScales = [.2, .8, 0, .4, .1] },
        })
        {
            var snapshot = document with { Labels = settings };
            foreach (var scale in new[] { -.1, .08, .18, .35, .6, 1, double.NaN })
            {
                var expected = Original(snapshot, scale);
                var actual = LabelPriority.Hidden(snapshot, scale);
                var labels = snapshot.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).OrderBy(_ => random.Next()).ToArray();
                // Query in random view order BEFORE Count/enumeration can fill the cache.
                foreach (var label in labels) Assert.Equal(expected.Contains(label.Id), actual.Contains(label.Id));
                Assert.True(expected.SetEquals(actual));
            }
        }
    }

    // Frozen reference: the original production algorithm, including its 4000-label cap.
    internal static HashSet<ObjectId> Original(MapDocument document, double scale)
    {
        var settings = document.Labels;
        var hidden = new HashSet<ObjectId>();
        if (!settings.ByScale && !settings.ResolveOverlaps) return hidden;
        var candidates = new List<(SceneObject Label, int Importance, int Order)>();
        var order = 0;
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Visible) continue;
            foreach (var item in layer.Objects)
            {
                if (item.Kind != SceneObjectKind.Text || item.Text is not { } text || !item.Visible) continue;
                var importance = Math.Clamp(text.Importance, LabelSettings.MinImportance, LabelSettings.MaxImportance);
                if (settings.ByScale && !(scale >= settings.MinScale(importance))) { hidden.Add(item.Id); continue; }
                candidates.Add((item, importance, order++));
            }
        }
        if (settings.ResolveOverlaps && candidates.Count <= 4000)
        {
            var kept = new List<WorldBounds>();
            foreach (var (label, _, _) in candidates.OrderByDescending(c => c.Importance).ThenByDescending(c => c.Label.Text!.FontSize).ThenBy(c => c.Order))
            {
                var box = SceneGeometry.BoundsOf(label);
                var dx = (box.Right - box.Left) * .1;
                var dy = (box.Bottom - box.Top) * .1;
                var bounds = new WorldBounds(box.Left + dx, box.Top + dy, box.Right - dx, box.Bottom - dy);
                if (kept.Any(other => other.Intersects(bounds))) { hidden.Add(label.Id); continue; }
                kept.Add(bounds);
            }
        }
        return hidden;
    }

    private static SceneObject RandomObject(Random random)
    {
        var label = new SceneObject(ObjectId.New(), SceneObjectKind.Text, "label", random.Next(-1000, 1000), random.Next(-1000, 1000),
            random.Next(1, 600), random.Next(1, 120), random.Next(-180, 180), "#FF000000", "#FF000000", random.Next(0, 10), random.Next(8) != 0, false)
        { Text = new TextStyle { Content = "label", Importance = random.Next(-2, 9), FontSize = random.Next(1, 5) * 12 } };
        return random.Next(6) switch
        {
            0 => label with { Kind = SceneObjectKind.Stamp },
            1 => label with { Text = null },
            _ => label with { Text = label.Text! with { Blur = random.Next(0, 3), ShadowOpacity = .5, ShadowOffsetX = 5, ShadowOffsetY = -7 } },
        };
    }

    internal static SceneObject Label(double x, double y, int importance, double size) =>
        new SceneObject(ObjectId.New(), SceneObjectKind.Text, "label", x, y, 100, 20, 0, "#FF000000", "#FF000000", 0, true, false)
        { Text = new TextStyle { Content = "label", FontSize = size, Importance = importance } };

    internal static MapDocument Document(params ObjectLayer[] layers) =>
        new(DocumentId.New(), "labels", DateTimeOffset.UnixEpoch, new CanvasDefinition(100000, 100000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"), .. layers])
        { Labels = new LabelSettings { ByScale = true, ResolveOverlaps = true } };
}
