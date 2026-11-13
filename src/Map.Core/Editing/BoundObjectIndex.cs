using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Small derived sets for the top-level objects evaluated by the follow passes. Composition
/// contents keep their own local coordinates and are not independent document labels.
/// Shares unchanged sets across revisions; never retains the layer or its stamp storage.
/// </summary>
internal sealed record BoundObjectIndex
{
    private static readonly ConditionalWeakTable<IReadOnlyList<SceneObject>, BoundObjectIndex> Cache = [];
    public ImmutableDictionary<ObjectId, SceneObject> Paths { get; private init; } = ImmutableDictionary<ObjectId, SceneObject>.Empty;
    public ImmutableDictionary<ObjectId, SceneObject> Regions { get; private init; } = ImmutableDictionary<ObjectId, SceneObject>.Empty;
    public ImmutableDictionary<ObjectId, SceneObject> Decorations { get; private init; } = ImmutableDictionary<ObjectId, SceneObject>.Empty;
    public ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> PathTargets { get; private init; } = ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>>.Empty;
    public ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> RegionTargets { get; private init; } = ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>>.Empty;
    public ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> RegionEntities { get; private init; } = ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>>.Empty;

    public static BoundObjectIndex For(ObjectLayer layer)
    {
        if (Cache.TryGetValue(layer.Objects, out var cached)) return cached;
        var pending = new Stack<(ObjectLayer Layer, ObjectLayerChange Delta)>();
        var source = layer;
        while (!Cache.TryGetValue(source.Objects, out cached) && ObjectLayerChanges.TryGet(source, out var delta)
            && !ReferenceEquals(source.Objects, delta.Source.Objects))
        {
            pending.Push((source, delta));
            source = delta.Source;
        }
        cached ??= Cache.GetValue(source.Objects, static objects =>
        {
            var result = new BoundObjectIndex();
            foreach (var item in objects) result = result.Change(item, add: true);
            return result;
        });
        while (pending.TryPop(out var entry))
        {
            var previous = cached;
            cached = Cache.GetValue(entry.Layer.Objects, _ => previous.Apply(entry.Delta, entry.Layer));
        }
        return cached;
    }

    // Called by the same delta handoff as spatial/name derivatives, including Undo and Redo.
    internal static void Register(ObjectLayer source, ObjectLayer target, IReadOnlyList<int> removed, IReadOnlyList<int> added)
    {
        if (ReferenceEquals(source.Objects, target.Objects) || !Cache.TryGetValue(source.Objects, out var previous)) return;
        Cache.GetValue(target.Objects, _ => previous.Apply(new(source, removed, added), target));
    }

    private BoundObjectIndex Apply(ObjectLayerChange delta, ObjectLayer layer)
    {
        var next = this;
        foreach (var i in delta.Removed) next = next.Change(delta.Source.Objects[i], add: false);
        foreach (var i in delta.Added) next = next.Change(layer.Objects[i], add: true);
        return next;
    }

    private BoundObjectIndex Change(SceneObject item, bool add)
    {
        var next = this;
        if (item.Text?.Path is { } path)
            next = next with { Paths = Set(Paths), PathTargets = Link(PathTargets, path.TargetId) };
        if (item.Text?.Region is { } region)
        {
            next = next with { Regions = Set(Regions), RegionTargets = Link(RegionTargets, region.TargetId) };
            if (region.EntityId is { } entity) next = next with { RegionEntities = Link(RegionEntities, entity) };
        }
        if (item.Kind == SceneObjectKind.Decoration) next = next with { Decorations = Set(Decorations) };
        return next;

        ImmutableDictionary<ObjectId, SceneObject> Set(ImmutableDictionary<ObjectId, SceneObject> map) =>
            add ? map.SetItem(item.Id, item) : map.Remove(item.Id);
        ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> Link(ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> map, Guid target)
        {
            var ids = map.GetValueOrDefault(target, ImmutableHashSet<ObjectId>.Empty);
            ids = add ? ids.Add(item.Id) : ids.Remove(item.Id);
            return ids.IsEmpty ? map.Remove(target) : map.SetItem(target, ids);
        }
    }

    /// <summary>IDs touched since a known list, across coalesced edits. No full-object fallback.</summary>
    internal static bool TryChangedIds(ObjectLayer? before, ObjectLayer after, HashSet<ObjectId> ids)
    {
        var current = after;
        while (before is not null && !ReferenceEquals(before.Objects, current.Objects))
        {
            if (!ObjectLayerChanges.TryGet(current, out var delta)) return false;
            foreach (var i in delta.Removed) ids.Add(delta.Source.Objects[i].Id);
            foreach (var i in delta.Added) ids.Add(current.Objects[i].Id);
            current = delta.Source;
        }
        return before is not null;
    }

    internal static HashSet<ObjectId> Candidates(ObjectLayer? before, ObjectLayer after,
        ImmutableDictionary<ObjectId, SceneObject> labels,
        ImmutableDictionary<ObjectId, SceneObject> previous,
        ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> targets, IReadOnlySet<Guid> changedTargets)
    {
        var touched = new HashSet<ObjectId>();
        if (TryChangedIds(before, after, touched)) touched.RemoveWhere(id => !labels.ContainsKey(id));
        else
            foreach (var (id, item) in labels)
                if (!previous.TryGetValue(id, out var was) || !ReferenceEquals(was, item)) touched.Add(id);
        foreach (var target in changedTargets)
            if (targets.TryGetValue(target, out var dependents)) touched.UnionWith(dependents);
        return touched;
    }

    internal static IEnumerable<SceneObject> ChangedLabels(ObjectLayer before, ObjectLayer after,
        ImmutableDictionary<ObjectId, SceneObject> labels)
    {
        var touched = new HashSet<ObjectId>();
        if (!TryChangedIds(before, after, touched)) return labels.Values;
        return touched.Where(labels.ContainsKey).Select(id => labels[id]);
    }

    internal static SceneObject? PreviousLabel(MapDocument document, ObjectId id, bool path)
    {
        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            var index = For(layer);
            if ((path ? index.Paths : index.Regions).TryGetValue(id, out var item)) return item;
        }
        return null;
    }
}
