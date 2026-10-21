using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Stage 10C: the object tree's row model. A group is exactly one top-level row that names
/// itself and its child count; children are never duplicated next to it; entering a group shows
/// a breadcrumb and child rows with their own stable automation identities.
/// </summary>
public class Stage10CObjectTreeRowTests
{
    [Fact]
    public void Large_layers_keep_the_same_order_as_the_canvas()
    {
        var objects = Enumerable.Range(0, 6000).Select(index => Object($"item-{index}") with { Sublayer = index % 21 - 10 }).ToArray();
        var document = Build(objects);
        var rows = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, null, []);
        Assert.Equal(objects.OrderBy(o => o.Sublayer).Reverse().Select(o => o.Id), rows.Select(row => row.Object!.Id));
    }

    private static readonly Guid GroupId = Guid.Parse("12345678-1234-1234-1234-123456789012");

    private static SceneObject Object(
        string name, SceneObjectKind kind = SceneObjectKind.Rectangle,
        Guid? groupId = null, string? groupName = null) =>
        new(
            ObjectId.New(), kind, name, 0, 0, 100, 50, 0,
            "#FF3E5C76", "#FF1B263B", 0, visible: true, locked: false)
        {
            GroupId = groupId,
            GroupName = groupName,
        };

    private static MapDocument Build(params SceneObject[] objects) => new(
        DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
        [
            new BaseColorLayer(LayerId.New(), "Фон", true, false, 1.0, "#FF1E2430"),
            new ObjectLayer(LayerId.New(), "Объекты", true, false, 1.0, objects),
        ]);

    [Fact]
    public void A_group_is_one_top_level_row_with_name_and_child_count()
    {
        var a = Object("A", groupId: GroupId, groupName: "Тестовая группа");
        var b = Object("B", groupId: GroupId, groupName: "Тестовая группа");
        var c = Object("C");
        var document = Build(a, b, c);

        var rows = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, openGroupId: null, []);

        // The list reads top to bottom like the map does: C was placed last, so it is on top
        // and comes first; the group sits where its topmost member is.
        Assert.Equal(2, rows.Count);
        Assert.Equal(ObjectTreeRowKind.Object, rows[0].Kind);
        Assert.Equal("C", rows[0].Title);
        Assert.StartsWith("ObjectRow_", rows[0].AutomationId);

        var groupRow = rows[1];
        Assert.Equal(ObjectTreeRowKind.Group, groupRow.Kind);
        Assert.Contains("Тестовая группа", groupRow.Title);
        Assert.Contains("2", groupRow.Subtitle);
        Assert.Equal($"GroupRow_{GroupId:D}", groupRow.AutomationId);
    }

    [Fact]
    public void Rows_list_the_topmost_object_first_by_layer_level_and_order()
    {
        // Draw order: bottom layer first, low level first, list order inside a level. The panel
        // shows the reverse, so what covers everything on the map is the first row.
        var low = Object("Низ");
        var high = Object("Верх") with { Sublayer = 3 };
        var later = Object("Позже");
        var upper = Object("Верхний слой");
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1.0, "#FF1E2430"),
                new ObjectLayer(LayerId.New(), "Нижний", true, false, 1.0, [low, high, later]),
                new ObjectLayer(LayerId.New(), "Верхний", true, false, 1.0, [upper]),
            ]);

        var plain = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, null, []);
        Assert.Equal(["Верхний слой", "Верх", "Позже", "Низ"], plain.Select(row => row.Title).ToArray());

        // The filtered path walks the same order.
        var filtered = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Shapes, null, []);
        Assert.Equal(["Верхний слой", "Верх", "Позже", "Низ"], filtered.Select(row => row.Title).ToArray());
    }

    [Fact]
    public void Unfiltered_rows_expose_the_non_generic_list_contract_used_by_Avalonia()
    {
        var document = Build(Object("A"), Object("B"));

        var rows = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, null, []);

        var indexed = Assert.IsAssignableFrom<System.Collections.IList>(rows);
        Assert.True(indexed.IsReadOnly);
        Assert.True(indexed.IsFixedSize);
        // Top first: A was placed first, so it lies under B and is the second row.
        Assert.Equal("A", Assert.IsType<ObjectTreeRow>(indexed[1]).Title);
    }

    [Fact]
    public void Filtered_rows_keep_the_same_virtual_list_contract()
    {
        var document = Build(Object("Tree"), Object("Mountain"));

        var rows = ObjectTreeRows.Build(document, "mount", ObjectTypeFilter.All, null, []);

        var indexed = Assert.IsAssignableFrom<System.Collections.IList>(rows);
        Assert.Single(indexed.Cast<object>());
        Assert.Equal("Mountain", Assert.IsType<ObjectTreeRow>(indexed[0]).Title);
    }

    [Fact]
    public void An_open_group_shows_a_breadcrumb_and_child_rows_with_their_own_roles()
    {
        var a = Object("A", groupId: GroupId, groupName: "Тестовая группа");
        var b = Object("B", groupId: GroupId, groupName: "Тестовая группа");
        var c = Object("C");
        var document = Build(a, b, c);

        var rows = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, GroupId, [a.Id]);

        Assert.Equal(3, rows.Count);
        Assert.Equal(ObjectTreeRowKind.Breadcrumb, rows[0].Kind);
        Assert.Equal("GroupBreadcrumb", rows[0].AutomationId);
        Assert.Contains("Тестовая группа", rows[0].Title);

        // Children read top to bottom as well: B covers A, so B comes first.
        Assert.Equal(ObjectTreeRowKind.GroupChild, rows[1].Kind);
        Assert.Equal($"GroupChildRow_{b.Id}", rows[1].AutomationId);
        Assert.False(rows[1].Selected);
        Assert.Equal($"GroupChildRow_{a.Id}", rows[2].AutomationId);
        Assert.True(rows[2].Selected);

        // The outsider C is not shown while the group is open.
        Assert.DoesNotContain(rows, row => row.Title == "C");
    }

    [Fact]
    public void Group_row_and_child_row_identities_never_collide()
    {
        var a = Object("A", groupId: GroupId, groupName: "Лес");
        var b = Object("B", groupId: GroupId, groupName: "Лес");
        var document = Build(a, b);

        var closed = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, null, []);
        var open = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, GroupId, []);

        var all = closed.Concat(open).Select(row => row.AutomationId).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Search_matches_the_group_name_or_any_member()
    {
        var a = Object("Дуб", groupId: GroupId, groupName: "Лес");
        var b = Object("Ель", groupId: GroupId, groupName: "Лес");
        var c = Object("Камень");
        var document = Build(a, b, c);

        var byGroupName = ObjectTreeRows.Build(document, "лес", ObjectTypeFilter.All, null, []);
        Assert.Single(byGroupName);
        Assert.Equal(ObjectTreeRowKind.Group, byGroupName[0].Kind);

        var byMember = ObjectTreeRows.Build(document, "дуб", ObjectTypeFilter.All, null, []);
        Assert.Single(byMember);
        Assert.Equal(ObjectTreeRowKind.Group, byMember[0].Kind);

        var missing = ObjectTreeRows.Build(document, "болото", ObjectTypeFilter.All, null, []);
        Assert.Empty(missing);
    }

    [Fact]
    public void Type_filter_keeps_groups_with_at_least_one_matching_member()
    {
        var stamp = Object("Дуб", SceneObjectKind.Stamp, GroupId, "Лес");
        var text = Object("Подпись", SceneObjectKind.Text, GroupId, "Лес");
        var plainText = Object("Заголовок", SceneObjectKind.Text);
        var document = Build(stamp, text, plainText);

        // The Assets section lists placed assets by picture (2026-10-18): one asset-kind row that
        // still knows the grouped stamp as its member; the group itself is kept whole by Ctrl+A.
        var stamps = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Stamps, null, []);
        Assert.Single(stamps);
        Assert.Equal(ObjectTreeRowKind.AssetKind, stamps[0].Kind);
        Assert.Contains(stamp.Id, stamps[0].MemberIds!);

        var texts = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Text, null, []);
        Assert.Equal(2, texts.Count);

        var lines = ObjectTreeRows.Build(document, "", ObjectTypeFilter.Lines, null, []);
        Assert.Empty(lines);
    }

    [Fact]
    public void A_group_row_reads_selected_only_when_every_member_is_selected()
    {
        var a = Object("A", groupId: GroupId, groupName: "Лес");
        var b = Object("B", groupId: GroupId, groupName: "Лес");
        var document = Build(a, b);

        var partial = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, null, [a.Id]);
        Assert.False(partial[0].Selected);

        var full = ObjectTreeRows.Build(document, "", ObjectTypeFilter.All, null, [a.Id, b.Id]);
        Assert.True(full[0].Selected);
    }
}
