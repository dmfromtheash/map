using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

[Collection(nameof(DerivedIndexCounters))]
public sealed class LocalObjectSpliceTests
{
    [Fact]
    public void Selection_moves_match_the_original_order_algorithm_for_every_small_selection()
    {
        foreach (var bands in new[] { new int[7], new[] { 0, 0, 1, 1, 1, 0, 0 }, new[] { 0, 1, 0, 1, 0, 1, 0 } })
        {
            var objects = bands.Select((band, i) => Rect(i) with { Sublayer = band }).ToArray();
            var document = Document(objects);
            var layer = Layer(document);
            for (var mask = 1; mask < 1 << objects.Length; mask++)
            foreach (var direction in new[] { -1, 1 })
            foreach (var extreme in new[] { false, true })
            {
                var ids = objects.Where((_, i) => (mask & (1 << i)) != 0).Select(o => o.Id).ToHashSet();
                var expected = objects.ToList();
                if (extreme)
                {
                    var selected = expected.Where(o => ids.Contains(o.Id)).ToArray();
                    expected.RemoveAll(o => ids.Contains(o.Id));
                    expected.InsertRange(direction > 0 ? expected.Count : 0, selected);
                }
                else
                {
                    for (var j = 0; j < objects.Length - 1; j++)
                    {
                        var at = direction > 0 ? objects.Length - 2 - j : j + 1;
                        var neighbour = at + direction;
                        if (ids.Contains(expected[at].Id) && !ids.Contains(expected[neighbour].Id)
                            && expected[at].Sublayer == expected[neighbour].Sublayer)
                            (expected[at], expected[neighbour]) = (expected[neighbour], expected[at]);
                    }
                }
                var command = ReorderObjectsCommand.MoveSelection(document, layer.Id, ids, direction, extreme, "order");
                Assert.Equal(!objects.SequenceEqual(expected), command.HasChanges);
                var applied = command.Apply(document);
                AssertObjects(applied, expected);
                AssertObjects(command.Revert(applied), objects);
                AssertObjects(command.Apply(command.Revert(applied)), expected);
            }
        }
    }

    [Fact]
    public void Splices_match_a_list_model_at_chunk_boundaries_and_keep_both_branches_immutable()
    {
        var random = new Random(111);
        foreach (var count in new[] { 0, 1, 255, 256, 257, 769 })
        {
            var original = Enumerable.Range(0, count).Select(Rect).ToArray();
            var document = Document(original);
            for (var sample = 0; sample < 30; sample++)
            {
                var removed = original.Where(_ => sample == 0 || random.Next(8) == 0).Select(o => o.Id).ToHashSet();
                var expected = original.Where(o => !removed.Contains(o.Id)).ToList();
                var batches = new Dictionary<int, IReadOnlyList<SceneObject>>();
                for (var j = 0; j < 5; j++) batches[random.Next(expected.Count + 1)] = [Rect(1000 + j), Rect(2000 + j)];
                foreach (var batch in batches.OrderByDescending(p => p.Key)) expected.InsertRange(batch.Key, batch.Value);
                var result = DocumentEditor.SpliceObjects(document, Layer(document).Id, removed, batches);
                AssertObjects(result, expected);
                AssertObjects(document, original);
                foreach (var id in removed) Assert.Null(DocumentEditor.FindObject(result, id));
            }
        }
    }

    [Fact]
    public void Reordering_resolves_live_instances_and_preserves_layer_metadata()
    {
        var objects = Enumerable.Range(0, 600).Select(Rect).ToArray();
        var original = Document(objects);
        var layer = Layer(original);
        var command = new ReorderObjectsCommand(original, layer.Id,
            [objects[^1].Id, objects[0].Id], [0, objects.Length - 1], "swap");
        var edited = DocumentEditor.ReplaceObject(original, SceneObjectClone.At(objects[0] with { Name = "live" }, 150, 150));
        edited = edited.WithLayer(layer.Id, l => l with { Name = "live layer", Opacity = .5 });
        var expected = Layer(edited).Objects.ToArray();
        (expected[0], expected[^1]) = (expected[^1], expected[0]);
        var applied = command.Apply(edited);
        AssertObjects(applied, expected);
        AssertObjects(command.Revert(applied), Layer(edited).Objects);
        Assert.Equal("live layer", Layer(applied).Name);
        Assert.Equal(.5, Layer(applied).Opacity);
    }

    [Fact]
    public void Vector_and_composition_replacements_restore_exact_nonadjacent_slots_through_repeated_history()
    {
        var objects = Enumerable.Range(0, 520).Select(Rect).ToArray();
        var first = Shape("first"); var second = Shape("second");
        objects[255] = first; objects[513] = second;
        var document = Document(objects); var layer = Layer(document);
        var pieces = new[] { first with { Name = "retained id" }, Shape("piece") };
        var boolean = new ShapeBooleanCommand(document, layer.Id, second.Id, first.Id, ShapeBooleanOperation.Subtract);
        var package = CompositionTransfer.Copy(document, [objects[1].Id, first.Id, second.Id]);
        var composition = CompositionTransfer.CreateInstance(package, 100, 50);
        var cases = new (IEditCommand Command, SceneObject[] Removed, SceneObject[] Added, int Index)[]
        {
            (new ReplaceObjectWithManyCommand(document, layer.Id, first.Id, pieces), [first], pieces, 255),
            (boolean, [first, second], [boolean.Result], 255),
            (new ReplaceObjectsWithCompositionCommand(document, layer.Id,
                [second.Id, objects[1].Id, first.Id, first.Id, ObjectId.New()], composition),
                [objects[1], first, second], [composition], 1),
        };
        foreach (var c in cases)
        {
            var expected = objects.Except(c.Removed).ToList();
            expected.InsertRange(c.Index, c.Added);
            var session = new EditingSession(document);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                if (iteration == 0) session.Execute(c.Command); else Assert.True(session.Redo());
                AssertObjects(session.Document, expected);
                Assert.True(session.Undo());
                AssertObjects(session.Document, objects);
            }
        }
    }

    [Fact]
    public void Reused_owner_ids_publish_the_new_nested_identities_and_release_the_old_ones()
    {
        var before = Shape("before"); var after = Shape("after") with { Id = before.Id };
        var document = Document([before]); var layer = Layer(document);
        var command = new ReplaceObjectWithManyCommand(document, layer.Id, before.Id, [after, Rect(2)]);
        var applied = command.Apply(document);
        // New nested identities are now occupied; the old nested identities were released.
        Assert.Throws<ArgumentException>(() => new AddObjectCommand(layer.Id, after with { Id = ObjectId.New() }).Apply(applied));
        var withOldGeometry = new AddObjectCommand(layer.Id, before with { Id = ObjectId.New() }).Apply(applied);
        Assert.Equal(3, Layer(withOldGeometry).Objects.Count);
        var undone = command.Revert(applied);
        Assert.Throws<ArgumentException>(() => new AddObjectCommand(layer.Id, before with { Id = ObjectId.New() }).Apply(undone));
        Assert.Equal(2, Layer(new AddObjectCommand(layer.Id, after with { Id = ObjectId.New() }).Apply(undone)).Objects.Count);
    }

    [Fact]
    public void Splice_validation_rejects_collisions_with_survivors_and_accepts_transferred_identities()
    {
        var first = Shape("first"); var second = Shape("second");
        var document = Document([first, second]); var layer = Layer(document);
        Assert.Throws<ArgumentException>(() => new ReplaceObjectWithManyCommand(document, layer.Id, first.Id,
            [second, Rect(1)]).Apply(document));
        Assert.Throws<ArgumentException>(() => new ReplaceObjectWithManyCommand(document, layer.Id, first.Id,
            [second with { Id = ObjectId.New() }, Rect(1)]).Apply(document));
        var transfer = new ReplaceObjectWithManyCommand(document, layer.Id, first.Id,
            [first with { Id = ObjectId.New() }, Rect(1)]);
        AssertObjects(transfer.Revert(transfer.Apply(document)), [first, second]);
        var group = Guid.NewGuid();
        var member = Rect(3) with { GroupId = group, GroupName = "original" };
        var grouped = Document([member, Rect(4) with { GroupId = group, GroupName = "original" }]);
        Assert.Throws<ArgumentException>(() => new ReplaceObjectWithManyCommand(grouped, Layer(grouped).Id, member.Id,
            [member with { GroupName = "conflict" }, Rect(5)]).Apply(grouped));
    }

    [Fact]
    public void Paste_undo_outside_LIFO_removes_only_the_pasted_ids()
    {
        var original = Rect(0); var pasted = Rect(1); var later = Rect(2);
        var document = Document([original]); var layer = Layer(document);
        var paste = new PasteObjectsCommand(layer.Id, [pasted]);
        var applied = new AddObjectCommand(layer.Id, later).Apply(paste.Apply(document));
        AssertObjects(paste.Revert(applied), [original, later]);
    }

    private static void AssertObjects(MapDocument document, IReadOnlyList<SceneObject> expected)
    {
        var layer = Layer(document);
        Assert.Equal(expected, layer.Objects);
        var revision = DocumentRevisionIndex.For(document);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(i, layer.ObjectStorage.IndexOfId(expected[i].Id));
            Assert.True(revision.TryFind(expected[i].Id, out var location));
            Assert.Equal(i, location.ObjectIndex);
            Assert.Same(expected[i], location.Object);
        }
        DocumentDerivedIndexes.Prepare(document);
        Assert.True(DocumentDerivedIndexes.IsPrepared(document));
    }

    private static ObjectLayer Layer(MapDocument document) => document.Layers.OfType<ObjectLayer>().Single();
    private static MapDocument Document(IReadOnlyList<SceneObject> objects)
    {
        var document = new MapDocument(DocumentId.New(), "splices", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects)]);
        DocumentDerivedIndexes.Prepare(document);
        return document;
    }
    private static SceneObject Rect(int i) => new(ObjectId.New(), SceneObjectKind.Rectangle, "object " + i,
        i, i, 20, 20, 0, "#FFFFFFFF", "#00000000", 0, true, false);
    private static SceneObject Shape(string name) => VectorGeometry.CreateFreeShape(ObjectId.New(), name,
        [new(100, 100), new(300, 100), new(300, 300), new(100, 300)]);
}
