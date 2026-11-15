using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class AssetKindDeltaCostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prefix_splice_preserves_layer_order_snapshots_and_unrelated_pictures(bool topLayer)
    {
        var key = new string('a', 64);
        var otherKey = new string('b', 64);
        var lower = new ObjectLayer(LayerId.New(), "lower", true, false, 1, [Stamp(key)]);
        var upper = new ObjectLayer(LayerId.New(), "upper", true, false, 1, [Stamp(key), Stamp(key)]);
        var source = new MapDocument(DocumentId.New(), "prefix rows", DateTimeOffset.UnixEpoch,
            new(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), lower, upper]);
        var original = Rows(source);
        var target = topLayer ? upper : lower;
        var extra = new[] { Stamp(key), Stamp(key) };
        var command = new AddObjectsCommand(target.Id, extra);
        var added = command.Apply(source);
        Assert.True(ObjectTreeRows.TryAssetKindPrefixChange(source, added, key, out var index, out var removed, out var count));
        Assert.Equal(topLayer ? 1 : 3, index);
        Assert.Equal(0, removed);
        Assert.Equal(2, count);
        var placed = Rows(added);
        Assert.Equal(extra[^1].Id, placed[index].Object!.Id);
        Assert.Equal(extra[0].Id, placed[index + 1].Object!.Id);
        Assert.Equal(upper.Objects[^1].Id, original[1].Object!.Id);
        Assert.Equal(lower.Objects[0].Id, original[3].Object!.Id);
        Assert.Equal(4, original.Count);
        var undone = command.Revert(added);
        Assert.True(ObjectTreeRows.TryAssetKindPrefixChange(added, undone, key, out var undoIndex, out var undoRemoved, out var undoAdded));
        Assert.Equal(index, undoIndex);
        Assert.Equal(2, undoRemoved);
        Assert.Equal(0, undoAdded);
        Assert.Equal(original, Rows(undone));
        var unrelated = new AddObjectsCommand(target.Id, [Stamp(otherKey)]).Apply(source);
        Assert.True(ObjectTreeRows.TryAssetKindPrefixChange(source, unrelated, key, out _, out var unrelatedRemoved, out var unrelatedAdded));
        Assert.Equal(0, unrelatedRemoved);
        Assert.Equal(0, unrelatedAdded);
        Assert.Equal(original, Rows(unrelated));
        var replaced = DocumentEditor.ReplaceObject(added, extra[^1] with { Name = "changed" });
        Assert.False(ObjectTreeRows.TryAssetKindPrefixChange(added, replaced, key, out _, out _, out _));

        IReadOnlyList<ObjectTreeRow> Rows(MapDocument document) =>
            ObjectTreeRows.Build(document, "", ObjectTypeFilter.Stamps, null, [], openAssetKey: key);
    }

    [Theory]
    [InlineData(10_000, false)]
    [InlineData(200_000, false)]
    [InlineData(10_000, true)]
    [InlineData(200_000, true)]
    public void Placement_and_undo_derive_only_changed_kind_metadata(int count, bool newKind)
    {
        // This is a metadata-index probe; no other pictures are created or rendered.
        var objects = Enumerable.Range(0, count).Select(i => Stamp(i.ToString("x64"))).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        var source = new MapDocument(DocumentId.New(), "kind metadata", DateTimeOffset.UnixEpoch,
            new(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), layer]);
        ObjectTreeRows.PrepareLayerIndexes(source);
        var key = newKind ? count.ToString("x64") : objects[^1].Stamp!.AssetSha256;
        var extra = Stamp(key);
        var command = new AddObjectsCommand(layer.Id, [extra]);
        var added = command.Apply(source);
        var placedBytes = Build(added, key, extra.Id, newKind ? 1 : 2, "place");
        var removed = command.Revert(added);
        var undoBytes = Build(removed, objects[^1].Stamp!.AssetSha256, objects[^1].Id, 1, "undo");
        var original = ObjectTreeRows.Build(source, "", ObjectTypeFilter.Stamps, null, [], openAssetKey: objects[^1].Stamp!.AssetSha256);
        Assert.Equal(1, original[0].ChildCount);
        Assert.Equal(objects[^1].Id, original[1].Object!.Id);
        Assert.InRange(placedBytes, 0, 65_536);
        Assert.InRange(undoBytes, 0, 65_536);

        long Build(MapDocument document, string open, ObjectId first, int expected, string route)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.GetTimestamp();
            var rows = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Stamps, null, [], openAssetKey: open);
            var ms = Stopwatch.GetElapsedTime(watch).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine(FormattableString.Invariant($"KIND-DELTA count={count} newKind={newKind} route={route} bytes={bytes} ms={ms:F4}"));
            Assert.Equal(expected + 1, rows.Count);
            Assert.Equal(expected, rows[0].ChildCount);
            Assert.Equal(first, rows[1].Object!.Id);
            return bytes;
        }
    }

    private static SceneObject Stamp(string key) => new(ObjectId.New(), SceneObjectKind.Stamp, "metadata",
        0, 0, 64, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(key) };
}
