using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

[Collection(nameof(DerivedIndexCounters))]
public sealed class PersistentRevisionIndexTests
{
    [Fact]
    public void Long_append_and_replacement_history_keeps_indexes_and_exact_undo_redo()
    {
        var initial = Enumerable.Range(0, 1400).Select(i => Rect("r " + i)).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, initial);
        var document = Map(layer);
        DocumentDerivedIndexes.Prepare(document);
        var originalIndex = DocumentRevisionIndex.For(document);
        var roots = DocumentRevisionIndex.RootBuilds;
        var session = new EditingSession(document);
        var appended = Enumerable.Range(0, 600).Select(i => Rect("a " + i)).ToArray();
        foreach (var item in appended) session.Execute(new AddObjectCommand(layer.Id, item));
        foreach (var item in initial.Take(1200))
            session.Execute(new ReplaceObjectsCommand([item], [item with { Name = item.Name + " edited" }], "rename"));
        var after = session.Document;
        var expected = ((ObjectLayer)after.FindLayer(layer.Id)!).Objects.ToArray();
        Check(after, expected);
        Assert.True(DocumentRevisionIndex.For(after).OverlayObjectCount >= 1200);
        foreach (var item in initial) Assert.Same(item, originalIndex.FindObject(item.Id));
        foreach (var item in appended) Assert.Null(originalIndex.FindObject(item.Id));
        for (var i = 0; i < 1800; i++) Assert.True(session.Undo());
        Check(session.Document, initial);
        Assert.Equal(0, DocumentRevisionIndex.For(session.Document).AppendedObjectCount);
        for (var i = 0; i < 1800; i++) Assert.True(session.Redo());
        Check(session.Document, expected);
        Assert.Equal(roots, DocumentRevisionIndex.RootBuilds);
    }

    [Fact]
    public void Deleted_added_layers_release_their_append_entries()
    {
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, [Rect("initial")]);
        var document = Map(layer);
        DocumentDerivedIndexes.Prepare(document);
        var roots = DocumentRevisionIndex.RootBuilds;
        for (var i = 0; i < 300; i++)
        {
            var added = new ObjectLayer(LayerId.New(), "temporary", true, false, 1, [Rect("added")]);
            document = new AddLayerCommand(added).Apply(document);
            Assert.Equal(1, DocumentRevisionIndex.For(document).AppendedObjectCount);
            document = new DeleteLayerCommand(document, added.Id).Apply(document);
            Assert.Equal(0, DocumentRevisionIndex.For(document).AppendedObjectCount);
        }
        Assert.Equal(roots, DocumentRevisionIndex.RootBuilds);
    }

    [Fact]
    public void Appended_nested_identities_are_checked_and_released_by_replacements()
    {
        var first = Shape();
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, [first]);
        var document = Map(layer);
        DocumentDerivedIndexes.Prepare(document);
        var added = Shape();
        document = new AddObjectCommand(layer.Id, added).Apply(document);
        var duplicate = first with { Shape = added.Shape };
        Assert.Throws<ArgumentException>(() => new ReplaceObjectsCommand([first], [duplicate], "collision").Apply(document));
        var released = added with { Shape = Shape().Shape };
        var changed = new ReplaceObjectsCommand([added], [released], "release").Apply(document);
        var reused = new ReplaceObjectsCommand([first], [duplicate], "reuse").Apply(changed);
        Assert.Same(duplicate, DocumentRevisionIndex.For(reused).FindObject(first.Id));
        Assert.Same(added, DocumentRevisionIndex.For(document).FindObject(added.Id));
        var conflict = new AddObjectCommand(layer.Id, Shape() with { Shape = duplicate.Shape });
        Assert.Throws<ArgumentException>(() => conflict.Apply(reused));
        var deleted = new RemoveObjectsCommand(reused, layer.Id, [duplicate.Id]).Apply(reused);
        var replacementOwner = Shape() with { Shape = duplicate.Shape };
        var returned = new AddObjectCommand(layer.Id, replacementOwner).Apply(deleted);
        var renamed = replacementOwner with { Name = "renamed after reusing deleted identities" };
        var edited = new ReplaceObjectsCommand([replacementOwner], [renamed], "rename").Apply(returned);
        Assert.Same(renamed, DocumentRevisionIndex.For(edited).FindObject(renamed.Id));
    }

    [Fact]
    public void Repeated_splices_release_outgoing_append_entries_and_keep_reused_identities()
    {
        var root = Shape();
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, [root]);
        var original = Map(layer);
        DocumentDerivedIndexes.Prepare(original);
        var roots = DocumentRevisionIndex.RootBuilds;
        var current = Shape() with { GroupId = Guid.NewGuid(), GroupName = "one" };
        var document = new AddObjectCommand(layer.Id, current).Apply(original);
        var snapshot = document;
        var first = current;
        for (var i = 0; i < 300; i++)
        {
            var replacement = Shape() with { GroupId = current.GroupId, GroupName = current.GroupName };
            document = DocumentEditor.SpliceObjects(document, layer.Id, new HashSet<ObjectId> { current.Id },
                new Dictionary<int, IReadOnlyList<SceneObject>> { [1] = new[] { replacement } });
            var index = DocumentRevisionIndex.For(document);
            Assert.Equal(1, index.AppendedObjectCount);
            Assert.Null(index.FindObject(current.Id));
            Assert.Equal(new[] { replacement }, index.GroupMembers(replacement.GroupId!.Value));
            current = replacement;
        }
        var reused = current with { Shape = first.Shape, Name = "reused nested identity" };
        document = DocumentEditor.SpliceObjects(document, layer.Id, new HashSet<ObjectId> { current.Id },
            new Dictionary<int, IReadOnlyList<SceneObject>> { [1] = new[] { reused } });
        Assert.Equal(1, DocumentRevisionIndex.For(document).AppendedObjectCount);
        Assert.Throws<ArgumentException>(() => new AddObjectCommand(layer.Id, Shape() with { Shape = first.Shape }).Apply(document));
        Assert.Same(first, DocumentRevisionIndex.For(snapshot).FindObject(first.Id));
        Check(document, [root, reused]);
        Check(original, [root]);
        Assert.Equal(roots, DocumentRevisionIndex.RootBuilds);
    }

    private static void Check(MapDocument document, IReadOnlyList<SceneObject> expected)
    {
        var layer = document.Layers.OfType<ObjectLayer>().Single();
        var index = DocumentRevisionIndex.For(document);
        Assert.Equal(expected.Count, index.ObjectCount);
        Assert.Equal(expected, layer.Objects.ToArray());
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(index.TryFind(expected[i].Id, out var location));
            Assert.Same(expected[i], location.Object);
            Assert.Equal(i, location.ObjectIndex);
            Assert.Equal(layer.Id, location.LayerId);
        }
    }
    private static MapDocument Map(ObjectLayer layer) => new(DocumentId.New(), "index", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), layer]);
    private static SceneObject Rect(string name) => new(ObjectId.New(), SceneObjectKind.Rectangle, name,
        1, 2, 10, 10, 0, "#FFFFFFFF", "#00000000", 0, true, false);
    private static SceneObject Shape() => VectorGeometry.CreateFreeShape(ObjectId.New(), "shape",
        [new(10, 10), new(50, 10), new(50, 50), new(10, 50)]);
}
