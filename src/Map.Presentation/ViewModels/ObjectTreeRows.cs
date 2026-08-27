using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>Compact type filter of the objects panel; Ctrl+A honours it together with search.</summary>
public enum ObjectTypeFilter
{
    All,
    Stamps,
    Text,
    Lines,
    Shapes,
    Other,
}

/// <summary>What one row of the objects panel is, for both the view and UI Automation.</summary>
public enum ObjectTreeRowKind
{
    /// <summary>An ungrouped top-level object.</summary>
    Object,

    /// <summary>One whole group as a single top-level row.</summary>
    Group,

    /// <summary>A member shown inside its open group.</summary>
    GroupChild,

    /// <summary>The way back out of an open group; also names the group.</summary>
    Breadcrumb,
}

/// <summary>
/// One row of the objects panel. The model is computed here, outside the view, so the row set,
/// its automation identities and its selected flags are testable without a window.
/// </summary>
public sealed record ObjectTreeRow(
    ObjectTreeRowKind Kind,
    string AutomationId,
    string Title,
    string? Subtitle,
    SceneObject? Object,
    Guid? GroupId,
    int ChildCount,
    bool Selected);

/// <summary>
/// Builds the row sequence of the objects panel. A group collapses into exactly one top-level
/// row at the position of its first member; an open group shows a breadcrumb and only its own
/// children. Search and the type filter apply to objects; a group stays visible while its name
/// or at least one member matches.
/// </summary>
public static class ObjectTreeRows
{
    public static IReadOnlyList<ObjectTreeRow> Build(
        MapDocument document,
        string searchText,
        ObjectTypeFilter filter,
        Guid? openGroupId,
        IReadOnlyCollection<ObjectId> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var query = (searchText ?? "").Trim();

        if (openGroupId is { } openGroup)
        {
            return BuildOpenGroup(document, query, filter, openGroup, selectedIds);
        }

        var rows = new List<ObjectTreeRow>();
        var emittedGroups = new HashSet<Guid>();
        foreach (var sceneObject in AllObjects(document))
        {
            if (sceneObject.GroupId is { } groupId)
            {
                if (emittedGroups.Add(groupId))
                {
                    var members = SceneGeometry.GroupMembers(document, groupId);
                    if (GroupMatches(members, query, filter))
                    {
                        rows.Add(GroupRow(groupId, members, selectedIds));
                    }
                }

                continue;
            }

            if (ObjectMatches(sceneObject, query, filter))
            {
                rows.Add(new ObjectTreeRow(
                    ObjectTreeRowKind.Object,
                    $"ObjectRow_{sceneObject.Id}",
                    sceneObject.Name,
                    EditorLabels.ObjectSubtitle(sceneObject),
                    sceneObject,
                    GroupId: null,
                    ChildCount: 0,
                    Selected: selectedIds.Contains(sceneObject.Id)));
            }
        }

        return rows;
    }

    /// <summary>True when the object passes both the search text and the type filter.</summary>
    public static bool ObjectMatches(SceneObject sceneObject, string searchText, ObjectTypeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(sceneObject);
        var query = (searchText ?? "").Trim();
        var searchOk = query.Length == 0
            || sceneObject.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        return searchOk && MatchesType(sceneObject, filter);
    }

    /// <summary>The user-facing name of a type filter choice.</summary>
    public static string FilterTitle(ObjectTypeFilter filter) => filter switch
    {
        ObjectTypeFilter.Stamps => "Штампы",
        ObjectTypeFilter.Text => "Текст",
        ObjectTypeFilter.Lines => "Линии",
        ObjectTypeFilter.Shapes => "Фигуры",
        ObjectTypeFilter.Other => "Прочее",
        _ => "Все",
    };

    public static string GroupTitle(IReadOnlyList<SceneObject> members) =>
        members.Count > 0 && members[0].GroupName is { Length: > 0 } name ? name : "Группа";

    private static ObjectTreeRow GroupRow(
        Guid groupId, IReadOnlyList<SceneObject> members, IReadOnlyCollection<ObjectId> selectedIds) => new(
        ObjectTreeRowKind.Group,
        $"GroupRow_{groupId:D}",
        GroupTitle(members),
        EditorLabels.Objects(members.Count),
        Object: null,
        GroupId: groupId,
        ChildCount: members.Count,
        Selected: members.Count > 0 && members.All(member => selectedIds.Contains(member.Id)));

    private static List<ObjectTreeRow> BuildOpenGroup(
        MapDocument document,
        string query,
        ObjectTypeFilter filter,
        Guid openGroup,
        IReadOnlyCollection<ObjectId> selectedIds)
    {
        var members = SceneGeometry.GroupMembers(document, openGroup);
        var rows = new List<ObjectTreeRow>
        {
            new(
                ObjectTreeRowKind.Breadcrumb,
                "GroupBreadcrumb",
                GroupTitle(members),
                EditorLabels.Objects(members.Count),
                Object: null,
                GroupId: openGroup,
                ChildCount: members.Count,
                Selected: false),
        };

        foreach (var member in members)
        {
            if (ObjectMatches(member, query, filter))
            {
                rows.Add(new ObjectTreeRow(
                    ObjectTreeRowKind.GroupChild,
                    $"GroupChildRow_{member.Id}",
                    member.Name,
                    EditorLabels.ObjectSubtitle(member),
                    member,
                    GroupId: openGroup,
                    ChildCount: 0,
                    Selected: selectedIds.Contains(member.Id)));
            }
        }

        return rows;
    }

    private static bool GroupMatches(
        IReadOnlyList<SceneObject> members, string query, ObjectTypeFilter filter)
    {
        var typeOk = filter == ObjectTypeFilter.All
            || members.Any(member => MatchesType(member, filter));
        if (!typeOk)
        {
            return false;
        }

        return query.Length == 0
            || GroupTitle(members).Contains(query, StringComparison.OrdinalIgnoreCase)
            || members.Any(member => member.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesType(SceneObject sceneObject, ObjectTypeFilter filter) => filter switch
    {
        ObjectTypeFilter.All => true,
        ObjectTypeFilter.Stamps => sceneObject.Kind == SceneObjectKind.Stamp,
        ObjectTypeFilter.Text => sceneObject.Kind == SceneObjectKind.Text,
        ObjectTypeFilter.Lines => sceneObject.Kind == SceneObjectKind.Line,
        ObjectTypeFilter.Shapes => sceneObject.Kind
            is SceneObjectKind.Shape or SceneObjectKind.Rectangle or SceneObjectKind.Ellipse,
        _ => sceneObject.Kind
            is not (SceneObjectKind.Stamp or SceneObjectKind.Text or SceneObjectKind.Line
                or SceneObjectKind.Shape or SceneObjectKind.Rectangle or SceneObjectKind.Ellipse),
    };

    private static IEnumerable<SceneObject> AllObjects(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects);
}
