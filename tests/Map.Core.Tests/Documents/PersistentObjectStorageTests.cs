using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

public sealed class PersistentObjectStorageTests
{
    [Fact]
    public void Mixed_edits_preserve_order_identity_labels_and_visible_asset_first_occurrences()
    {
        var random = new Random(111);
        var expected = Enumerable.Range(0, 4096).Select(Item).ToList();
        var actual = SceneObjectList.From(expected);
        var original = actual;
        var originalItems = expected.ToArray();
        var names = Enumerable.Range(0, 4).ToDictionary(i => new string((char)('a' + i), 64), i => i < 2 ? "tree" : "kind " + i);
        _ = actual.IndexOfId(expected[0].Id);
        for (var step = 0; step < 300; step++)
        {
            var at = random.Next(expected.Count);
            switch (step % 5)
            {
                case 0:
                    var old = expected[at];
                    var item = new SceneObject(old.Id, old.Kind, old.Name, old.X, old.Y, old.Width, old.Height,
                        old.RotationDegrees, old.FillColor, old.StrokeColor, old.StrokeWidth, !old.Visible, old.Locked)
                        { Stamp = old.Stamp, Text = old.Text };
                    expected[at] = item;
                    actual = actual.ReplaceAt(at, item);
                    break;
                case 1:
                    var added = Enumerable.Range(step, 3).Select(Item).ToArray();
                    expected.InsertRange(at, added);
                    actual = actual.InsertRange(at, added);
                    break;
                case 2:
                    var count = Math.Min(270, expected.Count - at);
                    expected.RemoveRange(at, count);
                    actual = actual.RemoveRange(at, count);
                    break;
                case 3:
                    var positions = new[] { 0, expected.Count / 2, expected.Count - 1 };
                    var inserts = positions.ToDictionary(i => i, _ => (IReadOnlyList<SceneObject>)new[] { Item(step) });
                    actual = actual.InsertMany(inserts);
                    foreach (var i in positions.Reverse()) expected.Insert(i, inserts[i][0]);
                    break;
                default:
                    var removed = new[] { 0, expected.Count / 2, expected.Count - 1 };
                    var ids = removed.Select(i => expected[i].Id).ToArray();
                    actual = actual.RemovePositions(removed);
                    foreach (var i in removed.Reverse()) expected.RemoveAt(i);
                    foreach (var id in ids) Assert.Equal(-1, actual.IndexOfId(id));
                    break;
            }
            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected, actual.ToArray());
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Same(expected[i], actual[i]);
                Assert.Equal(i, actual.IndexOfId(expected[i].Id));
                if (expected[i].Kind == SceneObjectKind.Text) Assert.Equal(i, actual.IndexOfTextId(expected[i].Id));
            }
            Assert.Equal(expected.Where(o => o.Kind == SceneObjectKind.Text && o.Visible), actual.TextObjects());
            Assert.Equal(expected.Count(o => o.Kind == SceneObjectKind.Text && o.Visible), actual.LabelCount(-1));
            Assert.Equal(expected.Where(o => o.Kind == SceneObjectKind.Stamp && o.Visible)
                .Select(o => o.Stamp!.AssetSha256).Distinct(StringComparer.OrdinalIgnoreCase), actual.VisibleStampAssets);
            Assert.Equal(expected.Where(o => o.Kind == SceneObjectKind.Stamp && o.Visible)
                .Select(o => o.Stamp!.AssetSha256).DistinctBy(hash => names[hash]), actual.VisibleLegendAssets(names));
            if (expected.Count < 400) // keep exercising mixed edits without exhausting the list
            {
                var tail = Enumerable.Range(0, 3000).Select(Item).ToArray();
                expected.AddRange(tail);
                actual = actual.AppendRange(tail);
            }
        }
        Assert.Equal(originalItems, original.ToArray());
        for (var i = 0; i < original.Count; i++) Assert.Equal(i, original.IndexOfId(original[i].Id));
    }

    [Fact]
    public void Repeated_insertions_at_one_boundary_keep_old_snapshots_and_untouched_chunks()
    {
        var original = SceneObjectList.From(Enumerable.Range(0, 8192).Select(Item).ToArray());
        var expected = original.ToList();
        var changed = original;
        _ = original.IndexOfId(original[0].Id);
        for (var i = 0; i < 1100; i++)
        {
            var item = Item(i);
            changed = changed.InsertRange(256, new[] { item });
            expected.Insert(256, item);
        }
        Assert.Equal(expected, changed.ToArray());
        Assert.Contains(original.StorageChunks[^1], changed.StorageChunks);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(i, changed.IndexOfId(expected[i].Id));
        Assert.Equal(8192, original.Count);
    }

    private static SceneObject Item(int i) => new(ObjectId.New(),
        i % 11 == 0 ? SceneObjectKind.Text : SceneObjectKind.Stamp, "item", i, i, 10, 10, 0,
        "#FFFFFFFF", "#00000000", 0, i % 7 != 0, false)
    {
        Stamp = i % 11 == 0 ? null : new StampStyle(new string((char)('a' + i % 4), 64)),
        Text = i % 11 == 0 ? new TextStyle { Content = "label" } : null,
    };
}
