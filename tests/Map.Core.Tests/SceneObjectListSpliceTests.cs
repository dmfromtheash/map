using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests;

public sealed class SceneObjectListSpliceTests
{
    [Theory]
    [InlineData(17, false)]
    [InlineData(256, false)]
    [InlineData(1025, true)]
    public void Random_splices_match_list_and_preserve_all_snapshots(int seed, bool textOnly)
    {
        var random = new Random(seed);
        var model = Enumerable.Range(0, 3000).Select(i => seed == 17
            ? Item(i) with { Kind = SceneObjectKind.Rectangle, Text = null } : Item(i)).ToList();
        var storage = SceneObjectList.From(model);
        if (textOnly) _ = storage.IndexOfTextId(default); else _ = storage.IndexOfId(default);
        var history = new List<(SceneObjectList Storage, SceneObject[] Model)>();
        var removedIds = new List<ObjectId>();
        for (var step = 0; step < 240; step++)
        {
            history.Add((storage, model.ToArray()));
            var at = random.Next(model.Count + 1);
            switch (step % 6)
            {
                case 0:
                    var added = Enumerable.Range(step, random.Next(1, 400)).Select(Item).ToArray();
                    storage = storage.InsertRange(at, added);
                    model.InsertRange(at, added);
                    break;
                case 1:
                    var count = Math.Min(random.Next(1, 400), model.Count - at);
                    removedIds.AddRange(model.Skip(at).Take(count).Select(o => o.Id));
                    storage = storage.RemoveRange(at, count);
                    model.RemoveRange(at, count);
                    break;
                case 2:
                case 3:
                    // The same atomic API implements a move and a scattered reorder. Its
                    // keys name the reduced list, including insertions at both boundaries.
                    var positions = Enumerable.Range(0, 12).Select(_ => random.Next(model.Count)).Distinct().Order().ToArray();
                    var moved = positions.Select(i => model[i]).Reverse().ToArray();
                    for (var i = positions.Length - 1; i >= 0; i--) model.RemoveAt(positions[i]);
                    var batches = new Dictionary<int, IReadOnlyList<SceneObject>>();
                    if (step % 6 == 2)
                    {
                        at = random.Next(model.Count + 1);
                        batches.Add(at, moved);
                    }
                    else
                    {
                        batches.Add(0, moved[..(moved.Length / 2)]);
                        batches.Add(model.Count, moved[(moved.Length / 2)..]);
                    }
                    storage = storage.SplicePositions(positions, batches);
                    foreach (var batch in batches.OrderByDescending(p => p.Key)) model.InsertRange(batch.Key, batch.Value);
                    break;
                case 4:
                    at = random.Next(model.Count);
                    var replacement = Item(step) with { Id = model[at].Id };
                    storage = storage.ReplaceAt(at, replacement);
                    model[at] = replacement;
                    break;
                default:
                    var deleted = Enumerable.Range(0, 15).Select(_ => random.Next(model.Count)).Distinct().Order().ToArray();
                    removedIds.AddRange(deleted.Select(i => model[i].Id));
                    storage = storage.RemovePositions(deleted);
                    for (var i = deleted.Length - 1; i >= 0; i--) model.RemoveAt(deleted[i]);
                    break;
            }
            Verify(storage, model, textOnly);
            foreach (var id in removedIds.TakeLast(20))
                Assert.Equal(-1, textOnly ? storage.IndexOfTextId(id) : storage.IndexOfId(id));
        }
        foreach (var (snapshot, expected) in history) Verify(snapshot, expected, textOnly);
    }

    [Fact]
    public void Text_free_replacement_leaves_survive_splices_and_kind_changes()
    {
        var model = Enumerable.Range(0, 4096).Select(i => Item(i) with
            { Kind = SceneObjectKind.Rectangle, Text = null }).ToList();
        var storage = SceneObjectList.From(model);
        _ = storage.IndexOfId(default);
        var snapshots = new List<(SceneObjectList Storage, SceneObject[] Model)>();
        for (var step = 0; step < 100; step++)
        {
            snapshots.Add((storage, model.ToArray()));
            var at = 1024 + step % 32;
            var before = model[at];
            model[at] = before with { Name = step.ToString(), Kind = step % 11 == 0
                ? SceneObjectKind.Text : SceneObjectKind.Rectangle,
                Text = step % 11 == 0 ? new() { Content = "label", Importance = step % 5 + 1 } : null };
            storage = storage.ReplaceAt(at, model[at]);
            var added = Item(step) with { Kind = SceneObjectKind.Rectangle, Text = null };
            storage = storage.InsertRange(1020, [added]);
            model.Insert(1020, added);
            storage = storage.RemoveRange(1030, 1);
            model.RemoveAt(1030);
            Verify(storage, model, false);
        }
        foreach (var (snapshot, expected) in snapshots) Verify(snapshot, expected, false);
    }

    [Fact]
    public void Repeated_boundary_edits_share_distant_chunks_and_keep_chunks_bounded()
    {
        var storage = SceneObjectList.From(Enumerable.Range(0, 100_000).Select(Item).ToArray());
        var snapshot = storage;
        var distant = storage.StorageChunks[100];
        for (var step = 0; step < 600; step++)
        {
            storage = storage.InsertRange(step % 2 == 0 ? 0 : storage.Count, [Item(step)]);
            storage = storage.RemoveRange(storage.Count / 2, 1);
            storage.ValidateDirectory();
        }
        Assert.Contains(storage.StorageChunks, chunk => ReferenceEquals(distant, chunk));
        Assert.Equal(100_000, snapshot.Count);
        Assert.Equal(100_000, storage.Count);
        Assert.All(storage.StorageChunks, chunk => Assert.InRange(chunk.Length, 1, SceneObjectList.ChunkSize));
        for (var i = 1; i < storage.StorageChunks.Count; i++)
            Assert.True(storage.StorageChunks[i - 1].Length >= SceneObjectList.ChunkSize / 2
                || storage.StorageChunks[i].Length >= SceneObjectList.ChunkSize / 2);
    }

    [Fact]
    public void Empty_full_removal_and_invalid_positions_preserve_contract()
    {
        var items = Enumerable.Range(0, 513).Select(Item).ToArray();
        var storage = SceneObjectList.Empty.AppendRange(items);
        Assert.Same(storage, storage.InsertRange(0, []));
        Assert.Same(SceneObjectList.Empty, storage.RemoveRange(0, items.Length));
        Assert.Equal(items, storage);
        Assert.Throws<ArgumentOutOfRangeException>(() => storage[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => storage[storage.Count]);
        Assert.Throws<ArgumentOutOfRangeException>(() => storage.RemoveRange(510, 4));
        Assert.Throws<ArgumentException>(() => storage.RemovePositions([2, 2]));
        Assert.Throws<ArgumentException>(() => storage.RemovePositions([256, 1]));
    }

    [Fact]
    public void Mixed_command_history_undo_redo_matches_every_snapshot()
    {
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1,
            Enumerable.Range(0, 2000).Select(Item).ToArray());
        var document = new MapDocument(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(100000, 100000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), layer]);
        var session = new EditingSession(document);
        var snapshots = new List<SceneObject[]> { layer.Objects.ToArray() };
        for (var step = 0; step < 36; step++)
        {
            var current = (ObjectLayer)session.Document.FindLayer(layer.Id)!;
            IEditCommand command = (step % 3) switch
            {
                0 => new AddObjectsCommand(layer.Id, Enumerable.Range(step, 17).Select(Item).ToArray()),
                1 => new RemoveObjectsCommand(session.Document, layer.Id, [current.Objects[256].Id, current.Objects[^1].Id]),
                _ => new ReorderObjectsCommand(session.Document, layer.Id,
                    [current.Objects[^1].Id, current.Objects[0].Id], [0, current.Objects.Count - 1], "swap"),
            };
            session.Execute(command);
            Assert.Null(session.LastResourceError);
            snapshots.Add(((ObjectLayer)session.Document.FindLayer(layer.Id)!).Objects.ToArray());
        }
        for (var i = snapshots.Count - 2; i >= 0; i--)
        {
            Assert.True(session.Undo());
            Verify(((ObjectLayer)session.Document.FindLayer(layer.Id)!).ObjectStorage, snapshots[i], false);
        }
        for (var i = 1; i < snapshots.Count; i++)
        {
            Assert.True(session.Redo());
            Verify(((ObjectLayer)session.Document.FindLayer(layer.Id)!).ObjectStorage, snapshots[i], false);
        }
        Assert.Equal(snapshots[0], layer.Objects);
    }

    private static void Verify(SceneObjectList storage, IReadOnlyList<SceneObject> expected, bool textOnly)
    {
        storage.ValidateDirectory();
        Assert.Equal(expected.Count, storage.Count);
        Assert.True(expected.SequenceEqual(storage));
        Assert.Equal(expected.Where(o => o.Kind == SceneObjectKind.Text && o.Visible), storage.TextObjects());
        for (var importance = LabelSettings.MinImportance; importance <= LabelSettings.MaxImportance; importance++)
            Assert.Equal(expected.Count(o => o.Kind == SceneObjectKind.Text && o.Visible && o.Text!.Importance == importance),
                storage.LabelCount(1 << importance));
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i], storage[i]);
            if (!textOnly) Assert.Equal(i, storage.IndexOfId(expected[i].Id));
            else if (expected[i].Kind == SceneObjectKind.Text) Assert.Equal(i, storage.IndexOfTextId(expected[i].Id));
        }
    }

    private static SceneObject Item(int i) => new(ObjectId.New(), i % 5 == 0 ? SceneObjectKind.Text : SceneObjectKind.Rectangle,
        "item", i, i, 24, 20, 0, "#FFFFFFFF", "#00000000", 0, i % 7 != 0, false)
        { Text = i % 5 == 0 ? new() { Content = "label", Importance = i / 5 % 5 + 1 } : null };
}
