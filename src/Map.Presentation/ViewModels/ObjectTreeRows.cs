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

    /// <summary>One water system (river with its channels, or a lake) of the map.</summary>
    Water,

    /// <summary>One piece of land (a Land layer) of the map.</summary>
    Land,
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

    /// <summary>Set on a <see cref="ObjectTreeRowKind.Water"/> row: the river or lake it stands for.</summary>
    public Guid? WaterId { get; init; }

    /// <summary>Water rows carry the two switches the panel shows, without a second lookup.</summary>
    public bool WaterVisible { get; init; } = true;
    public bool WaterLocked { get; init; }

    /// <summary>Set on a <see cref="ObjectTreeRowKind.Land"/> row: the land layer it stands for.</summary>
    public LayerId? LandId { get; init; }

    /// <summary>The terrain level of a water or land row (see TerrainOrder); zero for the rest.</summary>
    public int Level { get; init; }
}

/// <summary>
/// Builds the row sequence of the objects panel. A group collapses into exactly one top-level
/// row at the position of its first member; an open group shows a breadcrumb and only its own
/// children. Search and the type filter apply to objects; a group stays visible while its name
/// or at least one member matches.
/// </summary>
public static class ObjectTreeRows
{
    private sealed record GroupCount(int Value);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ObjectLayer, GroupCount> GroupCounts = [];
    private static int GroupedCount(ObjectLayer layer) => GroupCounts.GetValue(layer, static current =>
    {
        if (ObjectLayerChanges.TryGet(current, out var delta))
            return new(GroupedCount(delta.Source) - delta.Removed.Count(i => delta.Source.Objects[i].GroupId is not null)
                + delta.Added.Count(i => current.Objects[i].GroupId is not null));
        return new(current.Objects.Count(o => o.GroupId is not null));
    }).Value;

    public static IReadOnlyList<ObjectTreeRow> Build(
        MapDocument document,
        string searchText,
        ObjectTypeFilter filter,
        Guid? openGroupId,
        IReadOnlyCollection<ObjectId> selectedIds,
        IReadOnlyCollection<Guid>? selectedWaterIds = null,
        LayerId? selectedLandId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var query = (searchText ?? "").Trim();
        var layers = document.Layers.OfType<ObjectLayer>().ToArray();
        // Water systems and pieces of land are objects of the map like any other, so they belong
        // in this list. They are listed above the ordinary rows - water, then land, each from the
        // highest level down - and only while a group is not open, which is a view into one group
        // and nothing else.
        var water = openGroupId is null
            ? [.. WaterRows(layers, query, filter, selectedWaterIds), .. LandRows(document, query, filter, selectedLandId)]
            : (IReadOnlyList<ObjectTreeRow>)[];
        IReadOnlyList<ObjectTreeRow> rest;
        if (query.Length == 0 && filter == ObjectTypeFilter.All && openGroupId is null
            && layers.All(layer => GroupedCount(layer) == 0))
            rest = new VirtualRowList(new UngroupedEntries(layers), selectedIds);
        else
        {
            var objects = AllObjects(document).ToList();
            var groups = BuildGroupIndex(objects);

            // Every panel mode is a virtual row source. Search/filter still scan the document once,
            // but only compact entries are retained; Avalonia creates a heavy row model on demand.
            rest = BuildVirtualRows(objects, groups, openGroupId, selectedIds, query, filter);
        }
        return water.Count == 0 ? rest : new PrefixedRowList(water, rest);
    }

    /// <summary>One row per piece of land, highest level first, matching the search and the type filter.</summary>
    private static IReadOnlyList<ObjectTreeRow> LandRows(
        MapDocument document, string query, ObjectTypeFilter filter, LayerId? selected)
    {
        if (filter is not (ObjectTypeFilter.All or ObjectTypeFilter.Other)) return [];
        var rows = new List<ObjectTreeRow>();
        var lands = TerrainOrder.Lands(document);
        for (var i = lands.Count - 1; i >= 0; i--)
        {
            var land = lands[i];
            if (query.Length > 0 && !land.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(new ObjectTreeRow(
                ObjectTreeRowKind.Land,
                $"LandRow_{land.Id}",
                land.Name,
                "Земля",
                Object: null,
                GroupId: null,
                ChildCount: 0,
                Selected: selected is { } id && id == land.Id)
            { LandId = land.Id, Level = land.Level, WaterVisible = land.Visible, WaterLocked = land.Locked });
        }
        return rows;
    }

    /// <summary>One row per river or lake, highest level first, matching the panel search and the type filter.</summary>
    private static IReadOnlyList<ObjectTreeRow> WaterRows(
        IReadOnlyList<ObjectLayer> layers, string query, ObjectTypeFilter filter, IReadOnlyCollection<Guid>? selected)
    {
        if (filter is not (ObjectTypeFilter.All or ObjectTypeFilter.Other)) return [];
        var rows = new List<ObjectTreeRow>();
        foreach (var layer in layers.Reverse())
            foreach (var river in layer.Rivers.Reverse())
            {
                if (query.Length > 0 && !river.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var lakes = river.Channels.Count(c => c.Closed);
                var courses = river.Channels.Length - lakes;
                var subtitle = courses == 0
                    ? (lakes == 1 ? "Озеро" : $"Озёра: {lakes}")
                    : lakes == 0 ? $"Русел: {courses}" : $"Русел: {courses}, озёр: {lakes}";
                rows.Add(new ObjectTreeRow(
                    ObjectTreeRowKind.Water,
                    $"WaterRow_{river.Id:D}",
                    river.Name,
                    subtitle,
                    Object: null,
                    GroupId: null,
                    ChildCount: river.Channels.Length,
                    Selected: selected is not null && selected.Contains(river.Id))
                { WaterId = river.Id, WaterVisible = river.Visible, WaterLocked = river.Locked, Level = river.Level });
            }
        return [.. rows.OrderByDescending(row => row.Level)];
    }

    /// <summary>A fixed set of rows in front of a virtual list, without materializing the list.</summary>
    private sealed class PrefixedRowList(IReadOnlyList<ObjectTreeRow> prefix, IReadOnlyList<ObjectTreeRow> rest)
        : IReadOnlyList<ObjectTreeRow>
    {
        public int Count => prefix.Count + rest.Count;
        public ObjectTreeRow this[int index] => index < prefix.Count ? prefix[index] : rest[index - prefix.Count];
        public IEnumerator<ObjectTreeRow> GetEnumerator()
        {
            foreach (var row in prefix) yield return row;
            foreach (var row in rest) yield return row;
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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

    /// <summary>
    /// Every object of the document as the panel lists them: the topmost first. A layers panel
    /// reads top to bottom, and so does this list - the layer that covers the others leads, and
    /// inside a layer the object drawn last (the one on top) leads. Below this size the order
    /// is the true draw order, sublayer included; a larger layer keeps its list order reversed,
    /// so a million-object scene never sorts on every refresh, and the level badge of each row
    /// still says where it sits.
    /// </summary>
    private static IEnumerable<SceneObject> AllObjects(MapDocument document)
    {
        var layers = document.Layers.OfType<ObjectLayer>().ToArray();
        for (var l = layers.Length - 1; l >= 0; l--)
        {
            var ordered = StackOrder(layers[l]);
            for (var i = ordered.Count - 1; i >= 0; i--) yield return ordered[i];
        }
    }

    /// <summary>Objects of one layer bottom to top; the exact draw order while sorting stays cheap.</summary>
    internal const int DrawOrderSortLimit = 5000;
    private static IReadOnlyList<SceneObject> StackOrder(ObjectLayer layer) =>
        layer.Objects.Count <= DrawOrderSortLimit ? SceneGeometry.InDrawOrder(layer) : layer.Objects;

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
        IReadOnlyCollection<ObjectId> selectedIds,
        string query,
        ObjectTypeFilter filter)
    {
        var entries = new List<VirtualRowEntry>();
        if (openGroupId is { } openGroup)
        {
            groups.TryGetValue(openGroup, out var members);
            members ??= [];
            entries.EnsureCapacity(members.Count + 1);
            entries.Add(VirtualRowEntry.ForBreadcrumb(openGroup, members));
            entries.AddRange(members
                .Where(member => ObjectMatches(member, query, filter))
                .Select(member => VirtualRowEntry.ForChild(openGroup, member)));
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
                    var members = groups[groupId];
                    if (GroupMatches(members, query, filter))
                    {
                        entries.Add(VirtualRowEntry.ForGroup(groupId, members));
                    }
                }

                continue;
            }

            if (ObjectMatches(sceneObject, query, filter))
            {
                entries.Add(VirtualRowEntry.ForObject(sceneObject));
            }
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

    /// <summary>Top-first rows over the layers without copying them; see <see cref="AllObjects"/>.</summary>
    private sealed class UngroupedEntries : IReadOnlyList<VirtualRowEntry>
    {
        private readonly IReadOnlyList<SceneObject>[] _stacks;

        public UngroupedEntries(ObjectLayer[] layers)
        {
            _stacks = new IReadOnlyList<SceneObject>[layers.Length];
            for (var l = 0; l < layers.Length; l++) _stacks[l] = StackOrder(layers[layers.Length - 1 - l]);
            Count = _stacks.Sum(stack => stack.Count);
        }

        public int Count { get; }

        public VirtualRowEntry this[int index]
        {
            get
            {
                foreach (var stack in _stacks)
                {
                    if (index < stack.Count) return VirtualRowEntry.ForObject(stack[stack.Count - 1 - index]);
                    index -= stack.Count;
                }
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        public IEnumerator<VirtualRowEntry> GetEnumerator()
        {
            foreach (var stack in _stacks)
                for (var i = stack.Count - 1; i >= 0; i--) yield return VirtualRowEntry.ForObject(stack[i]);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class VirtualRowList(
        IReadOnlyList<VirtualRowEntry> entries,
        IReadOnlyCollection<ObjectId> selectedIds) : IReadOnlyList<ObjectTreeRow>, System.Collections.IList
    {
        private readonly HashSet<ObjectId> _selected = [.. selectedIds];

        public int Count => entries.Count;

        public ObjectTreeRow this[int index] => Create(entries[index]);

        object? System.Collections.IList.this[int index]
        {
            get => this[index];
            set => throw ReadOnly();
        }

        bool System.Collections.IList.IsFixedSize => true;

        bool System.Collections.IList.IsReadOnly => true;

        bool System.Collections.ICollection.IsSynchronized => false;

        object System.Collections.ICollection.SyncRoot => this;

        public IEnumerator<ObjectTreeRow> GetEnumerator()
        {
            for (var index = 0; index < entries.Count; index++)
            {
                yield return Create(entries[index]);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        int System.Collections.IList.Add(object? value) => throw ReadOnly();

        void System.Collections.IList.Clear() => throw ReadOnly();

        bool System.Collections.IList.Contains(object? value) => IndexOf(value) >= 0;

        int System.Collections.IList.IndexOf(object? value) => IndexOf(value);

        void System.Collections.IList.Insert(int index, object? value) => throw ReadOnly();

        void System.Collections.IList.Remove(object? value) => throw ReadOnly();

        void System.Collections.IList.RemoveAt(int index) => throw ReadOnly();

        void System.Collections.ICollection.CopyTo(Array array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);
            for (var sourceIndex = 0; sourceIndex < Count; sourceIndex++)
            {
                array.SetValue(this[sourceIndex], index + sourceIndex);
            }
        }

        private int IndexOf(object? value)
        {
            if (value is not ObjectTreeRow wanted)
            {
                return -1;
            }

            for (var index = 0; index < Count; index++)
            {
                if (this[index] == wanted)
                {
                    return index;
                }
            }

            return -1;
        }

        private static NotSupportedException ReadOnly() =>
            new("The virtual object row source is read-only.");

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
