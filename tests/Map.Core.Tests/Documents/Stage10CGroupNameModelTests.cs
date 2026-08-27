using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

/// <summary>
/// Stage 10C: the document model owns the group-name invariants. Every member of one GroupId
/// carries the same GroupName, and a name can never exist without a group.
/// </summary>
public class Stage10CGroupNameModelTests
{
    private static SceneObject Rectangle(string name, Guid? groupId = null, string? groupName = null) =>
        new(
            ObjectId.New(), SceneObjectKind.Rectangle, name,
            10, 10, 80, 60, 0, "#FF3E5C76", "#FF1B263B", 2, visible: true, locked: false)
        {
            GroupId = groupId,
            GroupName = groupName,
        };

    private static MapDocument Build(params SceneObject[] objects) => new(
        DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1.0, objects),
        ]);

    [Fact]
    public void Members_of_one_group_share_one_name()
    {
        var groupId = Guid.NewGuid();
        var document = Build(
            Rectangle("A", groupId, "Лес"),
            Rectangle("B", groupId, "Лес"));

        Assert.Equal("Лес", ((ObjectLayer)document.Layers[1]).Objects[0].GroupName);
    }

    [Fact]
    public void Different_names_for_one_group_id_are_rejected()
    {
        var groupId = Guid.NewGuid();

        var ex = Assert.Throws<ArgumentException>(() => Build(
            Rectangle("A", groupId, "Лес"),
            Rectangle("B", groupId, "Горы")));
        Assert.Contains("group", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_without_a_group_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build(Rectangle("A", groupId: null, groupName: "Лес")));
    }

    [Fact]
    public void Members_split_across_layers_still_share_one_name()
    {
        var groupId = Guid.NewGuid();
        var ex = Assert.Throws<ArgumentException>(() => new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "lower", true, false, 1.0, [Rectangle("A", groupId, "Лес")]),
                new ObjectLayer(LayerId.New(), "upper", true, false, 1.0, [Rectangle("B", groupId, "Поле")]),
            ]));
        Assert.Contains("group", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_group_without_a_name_stays_valid_for_old_files()
    {
        var groupId = Guid.NewGuid();
        var document = Build(
            Rectangle("A", groupId),
            Rectangle("B", groupId));

        Assert.Null(((ObjectLayer)document.Layers[1]).Objects[0].GroupName);
    }
}
