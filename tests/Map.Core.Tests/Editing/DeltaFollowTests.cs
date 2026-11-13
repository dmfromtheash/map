using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class DeltaFollowTests
{
    private static SceneObject Text() => new(ObjectId.New(), SceneObjectKind.Text, "label", 0, 0, 20, 20, 0,
        "#FF000000", "#FFFFFFFF", 0, true, false) { Text = new TextStyle { Content = "label", FontSize = 20 } };
    private static SceneObject Shape(double x) => VectorGeometry.CreateFreeShape(ObjectId.New(), "region",
        [new(x, 100), new(x + 300, 100), new(x + 300, 200), new(x, 200)]);
    private static SceneObject Region(SceneObject shape) => Text() with
        { Text = Text().Text! with { Region = new TextRegionBinding { TargetId = shape.Id.Value, Target = TextRegionTarget.Shape } } };
    private static MapDocument Doc(params SceneObject[] items) => new(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(2000, 1000, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "objects", true, false, 1, items)]);
    private static ObjectLayer Layer(MapDocument document) => document.Layers.OfType<ObjectLayer>().Single();
    private static SceneObject Find(MapDocument document, ObjectId id) => DocumentRevisionIndex.For(document).FindObject(id)!;

    [Fact]
    public void Stamp_deltas_share_empty_sets_and_old_snapshots_through_undo_redo()
    {
        var doc = Doc();
        var empty = BoundObjectIndex.For(Layer(doc));
        var session = new EditingSession(doc);
        var stamp = Text() with { Kind = SceneObjectKind.Stamp, Text = null, Stamp = new StampStyle(new string('a', 64)) };
        for (var i = 0; i < 70; i++)
        {
            session.Execute(new AddObjectCommand(Layer(doc).Id, stamp with { Id = ObjectId.New() }));
            Assert.Same(empty, BoundObjectIndex.For(Layer(session.Document)));
        }
        Assert.True(session.Undo());
        Assert.Same(empty, BoundObjectIndex.For(Layer(session.Document)));
        Assert.True(session.Redo());
        Assert.Same(empty, BoundObjectIndex.For(Layer(session.Document)));
        Assert.Empty(Layer(doc).Objects);
    }

    [Fact]
    public void Counts_and_reverse_links_follow_add_remove_rebind_undo_redo()
    {
        var doc = Doc();
        var layer = Layer(doc).Id;
        _ = BoundObjectIndex.For(Layer(doc));
        var session = new EditingSession(doc);
        var target = Guid.NewGuid();
        var path = Text() with { Text = Text().Text! with { Path = new TextPathBinding { TargetId = target } } };
        var region = Text() with { Text = Text().Text! with { Region = new TextRegionBinding { TargetId = target } } };
        var decoration = Text() with { Kind = SceneObjectKind.Decoration, Text = null,
            Decoration = new DecorationStyle { Kind = DecorationKind.ScaleBar } };
        session.Execute(new AddObjectCommand(layer, path));
        session.Execute(new AddObjectCommand(layer, region));
        session.Execute(new AddObjectCommand(layer, decoration));
        var index = BoundObjectIndex.For(Layer(session.Document));
        Assert.Single(index.Paths); Assert.Single(index.Regions); Assert.Single(index.Decorations);
        Assert.Contains(path.Id, index.PathTargets[target]);
        var before = Find(session.Document, region.Id);
        var nextTarget = Guid.NewGuid();
        session.Execute(new ReplaceObjectsCommand([before], [before with { Text = before.Text! with
            { Region = before.Text.Region! with { TargetId = nextTarget } } }], "rebind"));
        Assert.False(BoundObjectIndex.For(Layer(session.Document)).RegionTargets.ContainsKey(target));
        Assert.Contains(region.Id, BoundObjectIndex.For(Layer(session.Document)).RegionTargets[nextTarget]);
        Assert.True(session.Undo());
        Assert.Contains(region.Id, BoundObjectIndex.For(Layer(session.Document)).RegionTargets[target]);
        Assert.True(session.Redo());
        session.Execute(new DeleteObjectsCommand(session.Document, [path.Id, region.Id, decoration.Id]));
        Assert.Empty(BoundObjectIndex.For(Layer(session.Document)).Paths);
        Assert.Empty(BoundObjectIndex.For(Layer(session.Document)).Regions);
        Assert.False(MapDecorations.HasDecorations(session.Document));
        Assert.True(session.Undo());
        Assert.Single(BoundObjectIndex.For(Layer(session.Document)).Paths);
        Assert.True(MapDecorations.HasDecorations(session.Document));
        Assert.True(session.Redo());
        Assert.Empty(BoundObjectIndex.For(Layer(session.Document)).PathTargets);
        Assert.Single(index.RegionTargets); // immutable snapshot survived later changes
    }

    [Fact]
    public void Only_changed_region_targets_are_laid_out_and_deletion_detaches_with_undo_redo()
    {
        var a = Shape(100); var b = Shape(600);
        var aLabel = Region(a); var untouched = Region(b);
        var doc = Doc(a, b, aLabel, untouched);
        aLabel = RegionLabels.Layout(doc, aLabel);
        doc = DocumentEditor.ReplaceObject(doc, aLabel);
        var session = new EditingSession(doc);
        session.Execute(new TransformObjectCommand(a, SceneObjectClone.At(a, a.X + 100, a.Y)));
        Assert.Equal(aLabel.X + 100, Find(session.Document, aLabel.Id).X, 1);
        Assert.Same(untouched, Find(session.Document, untouched.Id));
        var moved = Find(session.Document, aLabel.Id);
        session.Execute(new DeleteObjectsCommand(session.Document, [a.Id]));
        Assert.True(Find(session.Document, aLabel.Id).Text!.Region!.Detached);
        Assert.True(session.Undo());
        Assert.Equal(moved, Find(session.Document, aLabel.Id));
        Assert.True(session.Redo());
        Assert.True(Find(session.Document, aLabel.Id).Text!.Region!.Detached);
        Assert.Same(untouched, Find(session.Document, untouched.Id));
    }

    [Fact]
    public void Nested_composition_labels_stay_local_and_unrelated_owner_edits_do_not_follow_them()
    {
        var shape = Shape(100);
        var nested = Region(shape);
        SceneObject Compose(SceneObject child) => Text() with { Kind = SceneObjectKind.Composition, Text = null,
            Composition = new EditableComposition(CompositionId.New(), DocumentId.New(), "nested", 500, 300, [child]) };
        var composition = Compose(Compose(nested));
        var label = Region(shape);
        var doc = Doc(shape, label, composition);
        label = RegionLabels.Layout(doc, label);
        doc = DocumentEditor.ReplaceObject(doc, label);
        var index = BoundObjectIndex.For(Layer(doc));
        Assert.Single(index.Regions);
        var session = new EditingSession(doc);
        session.Execute(new TransformObjectCommand(composition, SceneObjectClone.At(composition, 800, 400)));
        Assert.Same(label, Find(session.Document, label.Id));
        session.Execute(new TransformObjectCommand(shape, SceneObjectClone.At(shape, shape.X + 50, shape.Y)));
        Assert.Equal(label.X + 50, Find(session.Document, label.Id).X, 1);
        session.Execute(new DeleteObjectsCommand(session.Document, [composition.Id]));
        Assert.Single(BoundObjectIndex.For(Layer(session.Document)).Regions);
        Assert.True(session.Undo()); Assert.True(session.Redo()); Assert.True(session.Undo());
        Assert.Same(nested, Find(session.Document, composition.Id).Composition!.Items[0].Composition!.Items[0]);
        Assert.Same(composition.Composition, Find(session.Document, composition.Id).Composition);
    }

    [Fact]
    public void Coalesced_deltas_and_arbitrary_layer_replacement_keep_region_dependencies_correct()
    {
        var shape = Shape(100); var label = Region(shape);
        var doc = Doc(shape, label);
        label = RegionLabels.Layout(doc, label);
        doc = DocumentEditor.ReplaceObject(doc, label);
        _ = BoundObjectIndex.For(Layer(doc));
        var moved = SceneObjectClone.At(shape, shape.X + 80, shape.Y);
        var edited = new TransformObjectCommand(shape, moved).Apply(doc);
        edited = new AddObjectCommand(Layer(doc).Id, Text()).Apply(edited);
        var followed = RegionLabels.Follow(doc, edited);
        Assert.Equal(label.X + 80, Find(followed, label.Id).X, 1);
        var swap = doc.WithLayer(Layer(doc).Id, l => ((ObjectLayer)l) with { Objects = [moved, label] });
        Assert.Equal(label.X + 80, Find(RegionLabels.Follow(doc, swap), label.Id).X, 1);
    }

    [Fact]
    public void Deleting_a_target_layer_detaches_and_restores_its_labels()
    {
        var shape = Shape(100); var label = Region(shape);
        var doc = Doc(shape);
        var labels = new ObjectLayer(LayerId.New(), "labels", true, false, 1, [RegionLabels.Layout(doc, label)]);
        doc = doc.WithLayers([.. doc.Layers, labels]);
        var after = doc.WithLayers([doc.Layers[0], labels]);
        var followed = RegionLabels.Follow(doc, after);
        Assert.True(Find(followed, label.Id).Text!.Region!.Detached);
        Assert.False(Find(RegionLabels.Follow(followed, doc), label.Id).Text!.Region!.Detached);
    }

    [Fact]
    public void Moving_a_region_label_to_another_layer_keeps_its_hand_offset()
    {
        var shape = Shape(100); var label = Region(shape);
        var doc = Doc(shape, label);
        label = RegionLabels.Layout(doc, label);
        doc = DocumentEditor.ReplaceObject(doc, label);
        var moved = SceneObjectClone.At(label, label.X, label.Y + 30);
        var target = new ObjectLayer(LayerId.New(), "labels", true, false, 1, [moved]);
        var after = doc.WithLayers([doc.Layers[0], Layer(doc) with { Objects = [shape] }, target]);
        var followed = Find(RegionLabels.Follow(doc, after), label.Id);
        Assert.Equal(30, followed.Text!.Region!.OffsetY, 1);
        Assert.Equal(label.Y + 30, followed.Y, 1);
    }

    [Fact]
    public void Deleting_an_unrelated_layer_does_not_relayout_other_bound_labels()
    {
        var shape = Shape(100); var unlaid = Region(shape);
        var doc = Doc(shape, unlaid);
        var unrelated = new ObjectLayer(LayerId.New(), "other", true, false, 1, [Shape(700)]);
        var before = doc.WithLayers([.. doc.Layers, unrelated]);
        Assert.Same(doc, RegionLabels.Follow(before, doc));
        Assert.Same(unlaid, Find(doc, unlaid.Id));
    }

    [Fact]
    public void Path_rebinding_updates_reverse_dependencies_through_undo_redo()
    {
        River River(double y) => new() { Name = "river", Channels = [RiverEditor.NewChannel([new(100, y), new(900, y)], 20)] };
        var a = River(200); var b = River(400);
        var label = Text() with { Text = Text().Text! with { Path = new TextPathBinding
            { Target = TextPathTarget.River, TargetId = a.Id, PartId = a.Channels[0].Id } } };
        var doc = Doc(); var layer = Layer(doc) with { Rivers = [a, b] };
        doc = doc.WithLayer(layer.Id, _ => layer);
        var session = new EditingSession(doc);
        session.Execute(new AddObjectCommand(layer.Id, label));
        var before = Find(session.Document, label.Id);
        session.Execute(new ReplaceObjectsCommand([before], [before with { Text = before.Text! with
            { Path = before.Text.Path! with { TargetId = b.Id, PartId = b.Channels[0].Id } } }], "rebind"));
        Assert.False(BoundObjectIndex.For(Layer(session.Document)).PathTargets.ContainsKey(a.Id));
        Assert.True(session.Undo());
        Assert.Contains(label.Id, BoundObjectIndex.For(Layer(session.Document)).PathTargets[a.Id]);
        Assert.True(session.Redo());
        var rebound = Find(session.Document, label.Id);
        var movedA = a with { Channels = [a.Channels[0] with { Nodes = [.. a.Channels[0].Nodes.Select(n => n with { Y = n.Y + 50 })] }] };
        session.Execute(new RiverCommand(layer.Id, a.Id, movedA, "move unrelated"));
        Assert.Same(rebound, Find(session.Document, label.Id));
        var movedB = b with { Channels = [b.Channels[0] with { Nodes = [.. b.Channels[0].Nodes.Select(n => n with { Y = n.Y + 50 })] }] };
        session.Execute(new RiverCommand(layer.Id, b.Id, movedB, "move target"));
        Assert.Equal(rebound.Y + 50, Find(session.Document, label.Id).Y, 1);
        Assert.True(session.Undo()); Assert.Equal(rebound, Find(session.Document, label.Id));
        Assert.True(session.Redo()); Assert.Equal(rebound.Y + 50, Find(session.Document, label.Id).Y, 1);
    }

    [Fact]
    public void Area_geometry_changes_and_removal_follow_labels_without_object_deltas()
    {
        var sha = new string('a', 64);
        var area = new PlacementArea { Name = "area", Density = 0, Variants = [new(sha, 32, 32)], Style = new(sha), Nodes =
            [new(Guid.NewGuid(), 100, 100), new(Guid.NewGuid(), 700, 100), new(Guid.NewGuid(), 700, 300), new(Guid.NewGuid(), 100, 300)] };
        var label = Text() with { Text = Text().Text! with { Region = new TextRegionBinding
            { Target = TextRegionTarget.PlacementArea, TargetId = area.Id } } };
        var doc = Doc(label).WithAssets([new MapAssetReference(sha, "asset", "image/png", ".png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "test", catalogId: sha)]);
        doc = doc.WithLayer(Layer(doc).Id, l => ((ObjectLayer)l) with { PlacementAreas = [area] });
        label = RegionLabels.Layout(doc, label);
        doc = DocumentEditor.ReplaceObject(doc, label);
        var source = Layer(doc);
        var moved = area with { Nodes = [.. area.Nodes.Select(n => n with { X = n.X + 100 })] };
        var session = new EditingSession(doc);
        session.Execute(new SwapLayer(source, source with { PlacementAreas = [moved] }));
        Assert.Null(session.LastResourceError);
        Assert.Equal(label.X + 100, Find(session.Document, label.Id).X, 1);
        Assert.True(session.Undo()); Assert.Equal(label, Find(session.Document, label.Id));
        Assert.True(session.Redo()); Assert.Equal(label.X + 100, Find(session.Document, label.Id).X, 1);
        source = Layer(session.Document);
        session.Execute(new SwapLayer(source, source with { PlacementAreas = [] }));
        Assert.True(Find(session.Document, label.Id).Text!.Region!.Detached);
        Assert.True(session.Undo()); Assert.False(Find(session.Document, label.Id).Text!.Region!.Detached);
        Assert.True(session.Redo()); Assert.True(Find(session.Document, label.Id).Text!.Region!.Detached);
    }

    private sealed class SwapLayer(ObjectLayer before, ObjectLayer after) : IEditCommand
    {
        public string Name => "swap";
        public MapDocument Apply(MapDocument document) => document.WithLayer(before.Id, _ => after);
        public MapDocument Revert(MapDocument document) => document.WithLayer(before.Id, _ => before);
    }
}
