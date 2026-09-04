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
    bool Selected)
{
    /// <summary>Ordered group membership for selection state without another document scan.</summary>
    public IReadOnlyList<ObjectId>? MemberIds { get; init; }
}

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
        var objects = AllObjects(document).ToList();
        var groups = BuildGroupIndex(objects);

        // The ordinary panel view is a virtual row source: it stores compact references and
        // creates ObjectTreeRow models only when the ItemsControl asks for a realized index.
        // Filtered views are expected user operations and keep the explicit materialized path.
        if (query.Length == 0 && filter == ObjectTypeFilter.All)
        {
            return BuildVirtualRows(objects, groups, openGroupId, selectedIds);
        }

        if (openGroupId is { } openGroup)
        {
            return BuildOpenGroup(groups, query, filter, openGroup, selectedIds);
        }

        var rows = new List<ObjectTreeRow>();
        var emittedGroups = new HashSet<Guid>();
        foreach (var sceneObject in objects)
        {
            if (sceneObject.GroupId is { } groupId)
            {
                if (emittedGroups.Add(groupId))
                {
                    var members = groups[groupId];
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
        ObjectTypeFilter.Stamps => "Ассеты",
        ObjectTypeFilter.Text => "Текст",
        ObjectTypeFilter.Lines => "Линии",
        ObjectTypeFilter.Shapes => "Фигуры",
        ObjectTypeFilter.Other => "Прочее",
        _ => "Все",
    };

    public static string GroupTitle(IReadOnlyList<SceneObject> members) =>
        members.Count > 0 && members[0].GroupName is { Length: > 0 } name ? name : "Группа";

    private static ObjectTreeRow GroupRow(
        Guid groupId, IReadOnlyList<SceneObject> members, IReadOnlyCollection<ObjectId> selectedIds) =>
        new(
            ObjectTreeRowKind.Group,
            $"GroupRow_{groupId:D}",
            GroupTitle(members),
            EditorLabels.Objects(members.Count),
            Object: null,
            GroupId: groupId,
            ChildCount: members.Count,
            Selected: members.Count > 0 && members.All(member => selectedIds.Contains(member.Id)))
        {
            MemberIds = [.. members.Select(member => member.Id)],
        };

    private static List<ObjectTreeRow> BuildOpenGroup(
        IReadOnlyDictionary<Guid, List<SceneObject>> groups,
        string query,
        ObjectTypeFilter filter,
        Guid openGroup,
        IReadOnlyCollection<ObjectId> selectedIds)
    {
        var members = groups.TryGetValue(openGroup, out var indexedMembers)
            ? indexedMembers
            : [];
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

    private static Dictionary<Guid, List<SceneObject>> BuildGroupIndex(
        IReadOnlyList<SceneObject> objects)
    {
        var groups = new Dictionary<Guid, List<SceneObject>>();
        foreach (var sceneObject in objects)
        {
            if (sceneObject.GroupId is not { } groupId)
            {
                continue;
            }

            if (!groups.TryGetValue(groupId, out var members))
            {
                members = [];
                groups.Add(groupId, members);
            }

            members.Add(sceneObject);
        }

        return groups;
    }

    private static IReadOnlyList<ObjectTreeRow> BuildVirtualRows(
        IReadOnlyList<SceneObject> objects,
        IReadOnlyDictionary<Guid, List<SceneObject>> groups,
        Guid? openGroupId,
        IReadOnlyCollection<ObjectId> selectedIds)
    {
        var entries = new List<VirtualRowEntry>();
        if (openGroupId is { } openGroup)
        {
            groups.TryGetValue(openGroup, out var members);
            members ??= [];
            entries.EnsureCapacity(members.Count + 1);
            entries.Add(VirtualRowEntry.ForBreadcrumb(openGroup, members));
            entries.AddRange(members.Select(member => VirtualRowEntry.ForChild(openGroup, member)));
            return new VirtualRowList(entries, selectedIds);
        }

        entries.EnsureCapacity(objects.Count);
        var emittedGroups = new HashSet<Guid>();
        foreach (var sceneObject in objects)
        {
            if (sceneObject.GroupId is { } groupId)
            {
                if (emittedGroups.Add(groupId))
                {
                    entries.Add(VirtualRowEntry.ForGroup(groupId, groups[groupId]));
                }

                continue;
            }

            entries.Add(VirtualRowEntry.ForObject(sceneObject));
        }

        return new VirtualRowList(entries, selectedIds);
    }

    private readonly record struct VirtualRowEntry(
        ObjectTreeRowKind Kind,
        SceneObject? Object,
        Guid? GroupId,
        IReadOnlyList<SceneObject>? Members)
    {
        public static VirtualRowEntry ForObject(SceneObject sceneObject) =>
            new(ObjectTreeRowKind.Object, sceneObject, null, null);

        public static VirtualRowEntry ForChild(Guid groupId, SceneObject sceneObject) =>
            new(ObjectTreeRowKind.GroupChild, sceneObject, groupId, null);

        public static VirtualRowEntry ForGroup(Guid groupId, IReadOnlyList<SceneObject> members) =>
            new(ObjectTreeRowKind.Group, null, groupId, members);

        public static VirtualRowEntry ForBreadcrumb(Guid groupId, IReadOnlyList<SceneObject> members) =>
            new(ObjectTreeRowKind.Breadcrumb, null, groupId, members);
    }

    private sealed class VirtualRowList(
        IReadOnlyList<VirtualRowEntry> entries,
        IReadOnlyCollection<ObjectId> selectedIds) : IReadOnlyList<ObjectTreeRow>
    {
        private readonly HashSet<ObjectId> _selected = [.. selectedIds];

        public int Count => entries.Count;

        public ObjectTreeRow this[int index] => Create(entries[index]);

        public IEnumerator<ObjectTreeRow> GetEnumerator()
        {
            for (var index = 0; index < entries.Count; index++)
            {
                yield return Create(entries[index]);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private ObjectTreeRow Create(VirtualRowEntry entry)
        {
            if (entry.Object is { } sceneObject)
            {
                return new ObjectTreeRow(
                    entry.Kind,
                    entry.Kind == ObjectTreeRowKind.GroupChild
                        ? $"GroupChildRow_{sceneObject.Id}"
                        : $"ObjectRow_{sceneObject.Id}",
                    sceneObject.Name,
                    EditorLabels.ObjectSubtitle(sceneObject),
                    sceneObject,
                    entry.GroupId,
                    ChildCount: 0,
                    Selected: _selected.Contains(sceneObject.Id));
            }

            var members = entry.Members ?? [];
            var title = GroupTitle(members);
            var row = new ObjectTreeRow(
                entry.Kind,
                entry.Kind == ObjectTreeRowKind.Breadcrumb
                    ? "GroupBreadcrumb"
                    : $"GroupRow_{entry.GroupId:D}",
                title,
                EditorLabels.Objects(members.Count),
                Object: null,
                entry.GroupId,
                ChildCount: members.Count,
                Selected: entry.Kind == ObjectTreeRowKind.Group
                    && members.Count > 0
                    && members.All(member => _selected.Contains(member.Id)));
            return entry.Kind == ObjectTreeRowKind.Group
                ? row with { MemberIds = [.. members.Select(member => member.Id)] }
                : row;
        }
    }
}
