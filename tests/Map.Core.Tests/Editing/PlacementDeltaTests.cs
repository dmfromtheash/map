using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class PlacementDeltaTests
{
    [Fact]
    public void Naming_keeps_gaps_custom_names_and_snapshot_occupancy_across_rename_and_undo()
    {
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1,
            [Item("Дерево", 0), Item("Дерево 1", 30), Item("Дерево 3", 60), Item("Дерево 01", 90)]);
        var source = Document(layer);
        var names = EntityNameGenerator.Objects(source, SceneObjectKind.Stamp, 3, "  Дерево  ");
        Assert.Equal(["Дерево 2", "Дерево 4", "Дерево 5"], names);
        var command = new AddObjectsCommand(layer.Id, names.Select(n => Item(n, 2048)).ToArray());
        var appended = command.Apply(source);
        Assert.Equal("Дерево 6", EntityNameGenerator.Object(appended, SceneObjectKind.Stamp, "Дерево"));
        Assert.Equal("Дерево 2", EntityNameGenerator.Object(source, SceneObjectKind.Stamp, "Дерево"));
        var renamed = DocumentEditor.ReplaceObject(appended, layer.Objects[1] with { Name = "Моё имя" });
        Assert.Equal("Дерево 1", EntityNameGenerator.Object(renamed, SceneObjectKind.Stamp, "Дерево"));
        var undone = command.Revert(appended);
        Assert.Equal(names, EntityNameGenerator.Objects(undone, SceneObjectKind.Stamp, 3, "Дерево"));
        Assert.Equal("Дерево 6", EntityNameGenerator.Object(command.Apply(undone), SceneObjectKind.Stamp, "Дерево"));
    }

    [Fact]
    public void Spatial_delta_moves_between_cells_and_tail_undo_preserves_order_and_old_snapshot()
    {
        var first = Item("first", 100);
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [first]);
        var source = Document(layer);
        var oldIndex = ObjectSpatialIndex.For(layer);
        var second = Item("second", 1050);
        var command = new AddObjectsCommand(layer.Id, [second]);
        var added = command.Apply(source);
        var addedLayer = added.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal([first, second], ObjectSpatialIndex.For(addedLayer).Query(new(0, 0, 5000, 5000)));
        var moved = MultiSelectionGeometry.Translate([second], 4096, 0)[0];
        var replaced = DocumentEditor.ReplaceObject(added, moved);
        var movedIndex = ObjectSpatialIndex.For(replaced.Layers.OfType<ObjectLayer>().Single());
        Assert.Empty(movedIndex.QueryPoint(1055, 15));
        Assert.Equal(moved.Id, Assert.Single(movedIndex.QueryPoint(5151, 15)).Id);
        Assert.Equal([first], oldIndex.Query(new(0, 0, 8000, 8000)));
        var undone = command.Revert(added);
        Assert.Equal([first], ObjectSpatialIndex.For(undone.Layers.OfType<ObjectLayer>().Single()).Query(new(0, 0, 8000, 8000)));
    }

    private static SceneObject Item(string name, double x) => new(ObjectId.New(), SceneObjectKind.Rectangle,
        name, x, 10, 20, 20, 0, "#FFFFFFFF", "#FF000000", 1, true, false);
    private static MapDocument Document(ObjectLayer layer) => new(DocumentId.New(), "Test", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer]);
}
