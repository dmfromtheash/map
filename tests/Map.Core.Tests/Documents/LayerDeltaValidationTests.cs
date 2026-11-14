using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

/// <summary>
/// A layer-list revision of a prepared document is validated against the source's identity
/// indexes (only what enters is checked). These cases prove that it rejects exactly what the
/// whole-document constructor rejects, and that the derived lookups follow the delta.
/// </summary>
[Collection(nameof(DerivedIndexCounters))]
public sealed class LayerDeltaValidationTests
{
    private static readonly CanvasDefinition Canvas = new(4096, 4096, "px", "#FFFFFFFF");

    private static SceneObject Rect(string name = "object", Guid? group = null, string? groupName = null) =>
        new(ObjectId.New(), SceneObjectKind.Rectangle, name, 10, 20, 30, 40, 0, "#FF3E5C76", "#FF1B263B", 2, true, false)
        { GroupId = group, GroupName = groupName };

    private static SceneObject Shape() => VectorGeometry.CreateFreeShape(ObjectId.New(), "shape",
        [new(100, 100), new(300, 100), new(300, 300), new(100, 300)]);

    private sealed record Map(MapDocument Document, ObjectLayer Big, ObjectLayer Small, SceneObject Shape, SceneObject Grouped, Guid Group);

    private static Map Prepared(bool prepare = true)
    {
        var group = Guid.NewGuid();
        var shape = Shape();
        var grouped = Rect("member", group, "pair");
        var big = new ObjectLayer(LayerId.New(), "big", true, false, 1,
            [.. Enumerable.Range(0, 300).Select(i => Rect("r " + i)), shape, grouped, Rect("member 2", group, "pair")])
        { Rivers = [new River { Channels = [new RiverChannel { Nodes = [new(Guid.NewGuid(), 1, 1, 5), new(Guid.NewGuid(), 50, 50, 5)] }] }] };
        var small = new ObjectLayer(LayerId.New(), "small", true, false, 1, [Rect("small")]);
        var grid = new GridLayer(LayerId.New(), "grid", true, false, 1, [new(GridId.New(), GridGeometry.Square, 100)]);
        var document = new MapDocument(DocumentId.New(), "map", DateTimeOffset.UnixEpoch, Canvas,
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), big, small, grid]);
        if (prepare) DocumentDerivedIndexes.Prepare(document);
        big = (ObjectLayer)document.FindLayer(big.Id)!;
        small = (ObjectLayer)document.FindLayer(small.Id)!;
        return new(document, big, small, shape, grouped, group);
    }

    private static MapDocument WithAdded(MapDocument document, MapLayer layer) => document.WithLayers([.. document.Layers, layer]);

    public static TheoryData<string> InvalidAdditions => new()
    {
        "object id", "vector node id", "composition child id", "group name", "layer id", "grid id", "river id",
        "two new layers share an object", "replaced layer duplicates a kept object", "group name without id",
    };

    private static MapLayer[] Invalid(Map map, string kind)
    {
        var layers = map.Document.Layers.ToList();
        ObjectLayer NewLayer(params SceneObject[] objects) => new(LayerId.New(), "new", true, false, 1, objects);
        switch (kind)
        {
            case "object id": layers.Add(NewLayer(map.Big.Objects[5])); break;
            case "vector node id":
                var copy = VectorGeometry.WithNewIds(map.Shape);
                var stolen = copy with { Shape = copy.Shape! with { Regions = [copy.Shape!.Regions[0] with
                    { Nodes = [map.Shape.Shape!.Regions[0].Nodes[0], .. copy.Shape.Regions[0].Nodes.Skip(1)] }] } };
                layers.Add(NewLayer(stolen));
                break;
            case "composition child id":
                layers.Add(NewLayer(Rect() with { Kind = SceneObjectKind.Composition, Composition = new EditableComposition(
                    CompositionId.New(), DocumentId.New(), "c", 10, 10, [map.Small.Objects[0]]) }));
                break;
            case "group name": layers.Add(NewLayer(Rect("third", map.Group, "other name"))); break;
            case "group name without id": layers.Add(NewLayer(Rect("lonely") with { GroupName = "orphan" })); break;
            case "layer id": layers.Add(NewLayer(Rect()) with { Id = map.Small.Id }); break;
            case "grid id":
                var grid = (GridLayer)layers.Single(l => l is GridLayer);
                layers.Add(new GridLayer(LayerId.New(), "grid 2", true, false, 1, [grid.Grids[0]]));
                break;
            case "river id": layers.Add(NewLayer() with { Rivers = [map.Big.Rivers[0]] }); break;
            case "two new layers share an object":
                var shared = Rect();
                layers.Add(NewLayer(shared));
                layers.Add(NewLayer(shared));
                break;
            case "replaced layer duplicates a kept object":
                var at = layers.FindIndex(l => l.Id == map.Small.Id);
                layers[at] = map.Small with { Objects = [Rect(), map.Big.Objects[7]] };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return [.. layers];
    }

    [Theory]
    [MemberData(nameof(InvalidAdditions))]
    public void A_prepared_document_rejects_what_the_full_constructor_rejects(string kind)
    {
        var cold = Prepared(prepare: false);
        Assert.False(DocumentRevisionIndex.IsPrepared(cold.Document));
        Assert.ThrowsAny<ArgumentException>(() => cold.Document.WithLayers(Invalid(cold, kind)));

        var warm = Prepared();
        var roots = DocumentRevisionIndex.RootBuilds;
        Assert.ThrowsAny<ArgumentException>(() => warm.Document.WithLayers(Invalid(warm, kind)));
        Assert.Equal(roots, DocumentRevisionIndex.RootBuilds);
    }

    [Fact]
    public void Ids_that_leave_with_their_layer_may_enter_with_another()
    {
        var map = Prepared();
        var moved = new ObjectLayer(LayerId.New(), "moved", true, false, 1, [.. map.Small.Objects, Rect("fresh")]);
        var layers = map.Document.Layers.Where(l => l.Id != map.Small.Id).Append(moved).ToArray();
        var roots = (DocumentRevisionIndex.RootBuilds, ObjectNameIndex.RootBuilds);

        var target = map.Document.WithLayers(layers);

        Assert.Equal(roots, (DocumentRevisionIndex.RootBuilds, ObjectNameIndex.RootBuilds));
        AssertSameLookups(target);
        Assert.Equal(moved.Id, DocumentEditor.FindLayerOf(target, map.Small.Objects[0].Id)!.Id);
    }

    [Fact]
    public void Lookups_follow_add_delete_reorder_and_replace_through_undo()
    {
        var map = Prepared();
        var session = new EditingSession(map.Document);
        var added = new ObjectLayer(LayerId.New(), "added", true, false, 1, [Rect("a"), Rect("b", map.Group, "pair")]);
        session.Execute(new AddLayerCommand(added));
        AssertSameLookups(session.Document);
        Assert.Equal(3, DocumentRevisionIndex.For(session.Document).GroupMembers(map.Group).Count);
        session.Execute(new ReorderLayerCommand(added.Id, up: false));
        AssertSameLookups(session.Document);
        session.Execute(new DeleteLayerCommand(session.Document, map.Small.Id));
        AssertSameLookups(session.Document);
        Assert.Null(DocumentEditor.FindObject(session.Document, map.Small.Objects[0].Id));
        session.Execute(new ReplaceCityCommand((ObjectLayer)session.Document.FindLayer(added.Id)!,
            added with { Objects = [Rect("c"), added.Objects[0]] }, session.Document.Notes, session.Document.Notes));
        AssertSameLookups(session.Document);
        Assert.Null(DocumentEditor.FindObject(session.Document, added.Objects[1].Id));
        // A move inside the replaced (structural) layer and a duplicate of the whole small target.
        session.Execute(new DuplicateLayerCommand(added.Id));
        AssertSameLookups(session.Document);
        while (session.Undo()) AssertSameLookups(session.Document);
        Assert.Same(map.Small.Objects[0], DocumentEditor.FindObject(session.Document, map.Small.Objects[0].Id));
        while (session.Redo()) AssertSameLookups(session.Document);
    }

    [Fact]
    public void Map_wide_properties_share_the_layer_keyed_indexes()
    {
        var map = Prepared();
        var roots = (DocumentRevisionIndex.RootBuilds, ObjectNameIndex.RootBuilds);
        var changed = map.Document with { Fog = map.Document.Fog with { Enabled = true } };
        changed = changed.WithNotes([new MapNote(NoteId.New(), "n", "t", NoteAnchorKind.World)]);
        changed = changed.WithCanvas(changed.Canvas with { Scale = new MapScale(1, "km") });
        Assert.Same(DocumentRevisionIndex.For(map.Document), DocumentRevisionIndex.For(changed));
        Assert.True(ObjectNameIndex.IsPrepared(changed));
        Assert.Equal(roots, (DocumentRevisionIndex.RootBuilds, ObjectNameIndex.RootBuilds));
    }

    [Fact]
    public void Map_scale_keeps_the_map_wide_properties()
    {
        var map = Prepared();
        var styled = map.Document with { Style = new MapStyle(), Lighting = new MapLighting() };
        var scaled = new SetMapScaleCommand(styled.Canvas.Scale, new MapScale(2, "km")).Apply(styled);
        Assert.Equal(new MapScale(2, "km"), scaled.Canvas.Scale);
        Assert.Equal(styled.Style, scaled.Style);
        Assert.Equal(styled.Lighting, scaled.Lighting);
        Assert.Same(styled.Layers, scaled.Layers);
    }

    [Fact]
    public void A_restored_checkpoint_shares_the_indexes_of_its_source()
    {
        var map = Prepared();
        var edited = DocumentEditor.ReplaceObject(map.Document, map.Small.Objects[0] with { Name = "renamed" });
        var checkpoint = new MapCheckpoint(CheckpointId.New(), "v", DateTimeOffset.UnixEpoch, MapCheckpointSnapshot.Capture(edited), [1]);
        var document = map.Document.WithCheckpoints([checkpoint]);
        var roots = DocumentRevisionIndex.RootBuilds;
        var restored = checkpoint.Snapshot.Materialize(document.Id, document.Checkpoints);
        Assert.Equal("renamed", DocumentEditor.FindObject(restored, map.Small.Objects[0].Id)!.Name);
        Assert.Equal(roots, DocumentRevisionIndex.RootBuilds);
        Assert.Equal(document.Id, restored.Id);
        Assert.Single(restored.Checkpoints);
        Assert.Throws<ArgumentException>(() => checkpoint.Snapshot.Materialize(document.Id, [checkpoint, checkpoint]));
    }

    /// <summary>The derived index agrees with a cold index built from the same layers.</summary>
    private static void AssertSameLookups(MapDocument document)
    {
        var derived = DocumentRevisionIndex.For(document);
        var fresh = DocumentRevisionIndex.For(new MapDocument(DocumentId.New(), "fresh", DateTimeOffset.UnixEpoch,
            document.Canvas, [.. document.Layers]));
        Assert.Equal(fresh.ObjectCount, derived.ObjectCount);
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
            foreach (var item in layer.Objects)
            {
                Assert.True(derived.TryFind(item.Id, out var location), item.Name);
                Assert.True(fresh.TryFind(item.Id, out var expected));
                Assert.Equal(expected, location);
                Assert.Same(layer, derived.FindLayerOf(item.Id));
            }
    }
}
