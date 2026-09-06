using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>Stable location of one top-level scene object inside an immutable document revision.</summary>
public readonly record struct DocumentObjectLocation(
    SceneObject Object,
    LayerId LayerId,
    int LayerIndex,
    int ObjectIndex);

/// <summary>
/// Revision-local derived lookup. A loaded/constructed snapshot builds a full index once. A
/// replacement-only revision shares that root and overlays only the changed object locations;
/// nothing here participates in document equality or serialization.
/// </summary>
public sealed class DocumentRevisionIndex
{
    private const int MaxOverlayObjects = 1_024;
    internal static void ShareUnchangedLayers(MapDocument source, MapDocument target)
    {
        if (Cache.TryGetValue(source, out var index)) Cache.Add(target, index);
    }
    private const int MaxAppendDepth = 256;
    private static readonly ConditionalWeakTable<MapDocument, DocumentRevisionIndex> Cache = [];

    private readonly MapDocument _document;
    private readonly DocumentRevisionIndex _root;
    private readonly IReadOnlyDictionary<ObjectId, int> _rootPositions;
    private readonly IReadOnlyList<DocumentObjectLocation> _rootLocations;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<ObjectId>> _rootGroups;
    private readonly IReadOnlyDictionary<IdentityKey, ObjectId> _rootIdentities;
    private readonly Dictionary<ObjectId, DocumentObjectLocation> _overrides;
    private readonly IReadOnlyDictionary<IdentityKey, ObjectId> _overrideIdentities;
    private readonly AppendSegment? _appendTail;
    private readonly int _appendedCount;
    private readonly int _appendDepth;
    private IReadOnlyDictionary<LayerId, ObjectLayer> _structuralLayers = new Dictionary<LayerId, ObjectLayer>();
    private int _removedCount;

    private DocumentRevisionIndex(MapDocument document)
    {
        _document = document;
        _root = this;
        var positions = new Dictionary<ObjectId, int>();
        var locations = new List<DocumentObjectLocation>();
        var groups = new Dictionary<Guid, List<ObjectId>>();
        var identities = new Dictionary<IdentityKey, ObjectId>();
        for (var layerIndex = 0; layerIndex < document.Layers.Count; layerIndex++)
        {
            if (document.Layers[layerIndex] is not ObjectLayer layer)
            {
                continue;
            }

            // Warm stable storage locations with the cold document index, so later local
            // removals never build an identity table for the whole layer during a gesture.
            if (layer.Objects.Count > 0) _ = layer.ObjectStorage.IndexOfId(layer.Objects[0].Id);

            for (var objectIndex = 0; objectIndex < layer.Objects.Count; objectIndex++)
            {
                var sceneObject = layer.Objects[objectIndex];
                positions.Add(sceneObject.Id, locations.Count);
                locations.Add(new DocumentObjectLocation(
                    sceneObject, layer.Id, layerIndex, objectIndex));
                if (sceneObject.Composition is not null
                    || sceneObject.Line is not null
                    || sceneObject.Shape is not null)
                {
                    foreach (var identity in NestedIdentitiesOf(sceneObject))
                    {
                        identities.Add(identity, sceneObject.Id);
                    }
                }
                if (sceneObject.GroupId is { } groupId)
                {
                    if (!groups.TryGetValue(groupId, out var members))
                    {
                        members = [];
                        groups.Add(groupId, members);
                    }

                    members.Add(sceneObject.Id);
                }
            }
        }

        _rootPositions = positions;
        _rootLocations = locations;
        _rootGroups = groups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ObjectId>)pair.Value);
        _rootIdentities = identities;
        _overrides = [];
        _overrideIdentities = new Dictionary<IdentityKey, ObjectId>();
        _appendTail = null;
        _appendedCount = 0;
        _appendDepth = 0;
    }

    private DocumentRevisionIndex(
        MapDocument document,
        DocumentRevisionIndex root,
        Dictionary<ObjectId, DocumentObjectLocation> overrides,
        IReadOnlyDictionary<IdentityKey, ObjectId> overrideIdentities,
        AppendSegment? appendTail,
        int appendedCount,
        int appendDepth)
    {
        _document = document;
        _root = root;
        _rootPositions = root._rootPositions;
        _rootLocations = root._rootLocations;
        _rootGroups = root._rootGroups;
        _rootIdentities = root._rootIdentities;
        _overrides = overrides;
        _overrideIdentities = overrideIdentities;
        _appendTail = appendTail;
        _appendedCount = appendedCount;
        _appendDepth = appendDepth;
    }

    public static DocumentRevisionIndex For(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Cache.GetValue(document, static snapshot => new DocumentRevisionIndex(snapshot));
    }

    public int ObjectCount => checked(_rootPositions.Count + _appendedCount - _removedCount);

    public int OverlayObjectCount => _overrides.Count;

    public bool TryFind(ObjectId id, out DocumentObjectLocation location)
    {
        if (!TryFindStored(id, out location)) return false;
        if (!_structuralLayers.TryGetValue(location.LayerId, out var layer)) return true;
        // The current layer may include subsequent point replacements/appends.
        layer = (ObjectLayer)_document.Layers[location.LayerIndex];
        var position = layer.ObjectStorage.IndexOfId(id);
        if (position < 0) { location = default; return false; }
        location = location with { Object = layer.Objects[position], ObjectIndex = position };
        return true;
    }

    private bool TryFindStored(ObjectId id, out DocumentObjectLocation location)
    {
        if (_overrides.TryGetValue(id, out location))
        {
            return true;
        }

        for (var segment = _appendTail; segment is not null; segment = segment.Parent)
        {
            if (segment.LocationsById.TryGetValue(id, out location))
            {
                return true;
            }
        }

        if (_rootPositions.TryGetValue(id, out var position))
        {
            location = _rootLocations[position];
            return true;
        }

        location = default;
        return false;
    }

    public SceneObject? FindObject(ObjectId id) =>
        TryFind(id, out var location) ? location.Object : null;

    public ObjectLayer? FindLayerOf(ObjectId id)
    {
        if (!TryFind(id, out var location)
            || location.LayerIndex < 0
            || location.LayerIndex >= _document.Layers.Count)
        {
            return null;
        }

        return _document.Layers[location.LayerIndex] is ObjectLayer layer
            && layer.Id == location.LayerId
            ? layer
            : _document.FindLayer(location.LayerId) as ObjectLayer;
    }

    /// <summary>Current members in document order, including group-id changes in this overlay.</summary>
    public IReadOnlyList<SceneObject> GroupMembers(Guid groupId)
    {
        var ids = new HashSet<ObjectId>();
        var members = new List<DocumentObjectLocation>();
        if (_rootGroups.TryGetValue(groupId, out var rootMembers))
        {
            foreach (var id in rootMembers)
            {
                if (TryFind(id, out var current)
                    && current.Object.GroupId == groupId
                    && ids.Add(id))
                {
                    members.Add(current);
                }
            }
        }

        foreach (var current in _overrides.Values)
        {
            if (current.Object.GroupId == groupId && ids.Add(current.Object.Id))
            {
                members.Add(current);
            }
        }

        if (_appendTail is not null)
        {
            var orderedSegments = new Stack<AppendSegment>();
            for (var segment = _appendTail; segment is not null; segment = segment.Parent)
            {
                orderedSegments.Push(segment);
            }

            while (orderedSegments.TryPop(out var segment))
            {
                foreach (var appended in segment.LocationsInOrder)
                {
                    if (TryFind(appended.Object.Id, out var current)
                        && current.Object.GroupId == groupId
                        && ids.Add(current.Object.Id))
                    {
                        members.Add(current);
                    }
                }
            }
        }

        members.Sort(static (left, right) =>
        {
            var layer = left.LayerIndex.CompareTo(right.LayerIndex);
            return layer != 0 ? layer : left.ObjectIndex.CompareTo(right.ObjectIndex);
        });
        return [.. members.Select(location => location.Object)];
    }

    /// <summary>
    /// Validates only identities and group names touched by a replacement delta. Unchanged root
    /// invariants were already proven at construction/load and are not rescanned.
    /// </summary>
    internal void ValidateReplacements(IReadOnlyList<SceneObject> replacements)
    {
        var proposed = replacements.ToDictionary(sceneObject => sceneObject.Id);
        var delta = new Dictionary<ObjectId, SceneObject>();
        foreach (var location in _overrides.Values)
        {
            delta[location.Object.Id] = location.Object;
        }
        foreach (var replacement in replacements)
        {
            delta[replacement.Id] = replacement;
        }

        var deltaIdentities = new Dictionary<IdentityKey, ObjectId>();
        foreach (var (owner, sceneObject) in delta)
        {
            var local = new HashSet<IdentityKey>();
            foreach (var identity in NestedIdentitiesOf(sceneObject))
            {
                if (!local.Add(identity)
                    || deltaIdentities.TryGetValue(identity, out var deltaOwner) && deltaOwner != owner)
                {
                    throw new ArgumentException("Replacement vector/composition identities must stay unique.",
                        nameof(replacements));
                }

                deltaIdentities[identity] = owner;
                if (identity.Kind == IdentityKind.Object
                    && _rootPositions.ContainsKey(new ObjectId(identity.Value)))
                {
                    throw new ArgumentException(
                        "A composition child id collides with a top-level object.",
                        nameof(replacements));
                }
                if (_rootIdentities.TryGetValue(identity, out var rootOwner)
                    && rootOwner != owner
                    && !delta.ContainsKey(rootOwner))
                {
                    throw new ArgumentException("Replacement identities collide with an unchanged object.",
                        nameof(replacements));
                }
            }
        }

        var affectedGroups = new HashSet<Guid>();
        foreach (var replacement in replacements)
        {
            if (TryFind(replacement.Id, out var before) && before.Object.GroupId is { } oldGroup)
            {
                affectedGroups.Add(oldGroup);
            }
            if (replacement.GroupId is { } newGroup)
            {
                affectedGroups.Add(newGroup);
            }
            if (replacement.GroupId is null && replacement.GroupName is not null)
            {
                throw new ArgumentException("A group name requires a group id.", nameof(replacements));
            }
        }

        foreach (var groupId in affectedGroups)
        {
            string? expectedName = null;
            var hasMember = false;
            var memberIds = GroupMembers(groupId).Select(item => item.Id)
                .Concat(replacements.Where(item => item.GroupId == groupId).Select(item => item.Id))
                .Distinct();
            foreach (var id in memberIds)
            {
                var member = proposed.TryGetValue(id, out var changed)
                    ? changed
                    : FindObject(id);
                if (member?.GroupId != groupId)
                {
                    continue;
                }

                if (!hasMember)
                {
                    expectedName = member.GroupName;
                    hasMember = true;
                }
                else if (!string.Equals(expectedName, member.GroupName, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Members of group {groupId} disagree on the group name.",
                        nameof(replacements));
                }
            }
        }
    }

    /// <summary>
    /// Registers a replacement-only child revision when the source already has a derived index.
    /// If it does not, the child will simply build its own cold index on first use.
    /// </summary>
    internal static void RegisterReplacements(
        MapDocument source,
        MapDocument target,
        IReadOnlyList<DocumentObjectLocation> replacements)
    {
        if (!Cache.TryGetValue(source, out var sourceIndex))
        {
            return;
        }

        var overrides = new Dictionary<ObjectId, DocumentObjectLocation>(sourceIndex._overrides);
        foreach (var replacement in replacements)
        {
            overrides[replacement.Object.Id] = replacement;
        }

        if (overrides.Count > MaxOverlayObjects
            && !source.Layers.OfType<ObjectLayer>().Any(l => !l.PlacementPaths.IsEmpty || !l.PlacementAreas.IsEmpty))
        {
            return;
        }

        Cache.Add(target, new DocumentRevisionIndex(
            target,
            sourceIndex._root,
            overrides,
            BuildIdentityIndex(overrides.Values),
            sourceIndex._appendTail,
            sourceIndex._appendedCount,
            sourceIndex._appendDepth) { _structuralLayers = sourceIndex._structuralLayers, _removedCount = sourceIndex._removedCount });
    }

    /// <summary>
    /// Validates only objects appended by one command. Existing document identities were already
    /// proven by the source revision; checking the delta avoids reconstructing every global set.
    /// </summary>
    internal void ValidateAppends(IReadOnlyList<SceneObject> additions)
    {
        ArgumentNullException.ThrowIfNull(additions);
        var topLevelIds = new HashSet<ObjectId>();
        var nestedIdentities = new Dictionary<IdentityKey, ObjectId>();
        var groupNames = new Dictionary<Guid, string?>();

        foreach (var sceneObject in additions)
        {
            if (!topLevelIds.Add(sceneObject.Id) || TryFind(sceneObject.Id, out _))
            {
                throw new ArgumentException($"Duplicate object id {sceneObject.Id}.", nameof(additions));
            }

            var topLevelIdentity = new IdentityKey(IdentityKind.Object, sceneObject.Id.Value);
            if (TryFindIdentity(topLevelIdentity, out _)
                || nestedIdentities.ContainsKey(topLevelIdentity))
            {
                throw new ArgumentException(
                    "An appended object id collides with a composition child id.", nameof(additions));
            }

            if (sceneObject.GroupId is not { } groupId)
            {
                if (sceneObject.GroupName is not null)
                {
                    throw new ArgumentException(
                        "A group name requires a group id.", nameof(additions));
                }
            }
            else
            {
                if (!groupNames.TryGetValue(groupId, out var expectedName))
                {
                    var existingMembers = GroupMembers(groupId);
                    expectedName = existingMembers.Count > 0
                        ? existingMembers[0].GroupName
                        : sceneObject.GroupName;
                    groupNames.Add(groupId, expectedName);
                }

                if (!string.Equals(groupNames[groupId], sceneObject.GroupName, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Members of group {groupId} disagree on the group name.", nameof(additions));
                }
            }

            foreach (var identity in NestedIdentitiesOf(sceneObject))
            {
                var nestedObjectCollides = identity.Kind == IdentityKind.Object
                    && (TryFind(new ObjectId(identity.Value), out _)
                        || topLevelIds.Contains(new ObjectId(identity.Value)));
                if (nestedObjectCollides
                    || TryFindIdentity(identity, out _)
                    || !nestedIdentities.TryAdd(identity, sceneObject.Id))
                {
                    throw new ArgumentException(
                        "Appended vector/composition identities must stay unique.", nameof(additions));
                }
            }
        }
    }

    /// <summary>
    /// Carries a bounded append delta into the child revision. Once the chain becomes long it is
    /// deliberately not registered; the next lookup rebuilds one compact root and starts over.
    /// </summary>
    internal static void RegisterAppends(
        MapDocument source,
        MapDocument target,
        IReadOnlyList<DocumentObjectLocation> additions)
    {
        if (additions.Count == 0
            || !Cache.TryGetValue(source, out var sourceIndex)
            || sourceIndex._appendDepth >= MaxAppendDepth)
        {
            return;
        }

        var locations = additions.ToDictionary(location => location.Object.Id);
        var identities = new Dictionary<IdentityKey, ObjectId>();
        foreach (var addition in additions)
        {
            foreach (var identity in NestedIdentitiesOf(addition.Object))
            {
                identities.Add(identity, addition.Object.Id);
            }
        }

        var segment = new AppendSegment(
            sourceIndex._appendTail, locations, [.. additions], identities);
        Cache.Add(target, new DocumentRevisionIndex(
            target,
            sourceIndex._root,
            sourceIndex._overrides,
            sourceIndex._overrideIdentities,
            segment,
            checked(sourceIndex._appendedCount + additions.Count),
            sourceIndex._appendDepth + 1) { _structuralLayers = sourceIndex._structuralLayers, _removedCount = sourceIndex._removedCount });
    }

    /// <summary>
    /// Carries the previous append-chain state through a normal LIFO undo. If the caller is not
    /// removing exactly the latest registered segment, the target safely falls back to a cold
    /// compact index on first use.
    /// </summary>
    internal static void RegisterTailRemoval(
        MapDocument source,
        MapDocument target,
        IReadOnlyList<SceneObject> removals)
    {
        if (removals.Count == 0
            || !Cache.TryGetValue(source, out var sourceIndex)
            || sourceIndex._appendTail is not { } tail
            || tail.LocationsInOrder.Count != removals.Count)
        {
            return;
        }

        for (var index = 0; index < removals.Count; index++)
        {
            var id = removals[index].Id;
            if (tail.LocationsInOrder[index].Object.Id != id
                || sourceIndex._overrides.ContainsKey(id))
            {
                return;
            }
        }

        Cache.Add(target, new DocumentRevisionIndex(
            target,
            sourceIndex._root,
            sourceIndex._overrides,
            sourceIndex._overrideIdentities,
            tail.Parent,
            sourceIndex._appendedCount - removals.Count,
            sourceIndex._appendDepth - 1) { _structuralLayers = sourceIndex._structuralLayers, _removedCount = sourceIndex._removedCount });
    }

    internal static void RegisterPathRemoval(MapDocument source, MapDocument target, ObjectLayer layer, int removed)
    {
        var previous = For(source);
        var structural = previous._structuralLayers.ToDictionary(p => p.Key, p => p.Value);
        structural[layer.Id] = layer;
        Cache.Add(target, new DocumentRevisionIndex(target, previous._root, previous._overrides,
            previous._overrideIdentities, previous._appendTail, previous._appendedCount, previous._appendDepth)
        { _structuralLayers = structural, _removedCount = previous._removedCount + removed });
    }

    private bool TryFindIdentity(IdentityKey identity, out ObjectId owner)
    {
        if (_overrideIdentities.TryGetValue(identity, out owner))
        {
            return true;
        }

        for (var segment = _appendTail; segment is not null; segment = segment.Parent)
        {
            if (segment.Identities.TryGetValue(identity, out owner)
                && !_overrides.ContainsKey(owner))
            {
                return true;
            }
        }

        if (_rootIdentities.TryGetValue(identity, out owner)
            && !_overrides.ContainsKey(owner))
        {
            return true;
        }

        owner = default;
        return false;
    }

    private static IReadOnlyDictionary<IdentityKey, ObjectId> BuildIdentityIndex(
        IEnumerable<DocumentObjectLocation> locations)
    {
        var identities = new Dictionary<IdentityKey, ObjectId>();
        foreach (var location in locations)
        {
            foreach (var identity in NestedIdentitiesOf(location.Object))
            {
                identities.Add(identity, location.Object.Id);
            }
        }

        return identities;
    }

    private static IEnumerable<IdentityKey> NestedIdentitiesOf(SceneObject owner)
    {
        if (owner.Composition is { } composition)
        {
            yield return new IdentityKey(IdentityKind.Composition, composition.Id.Value);
            foreach (var child in composition.Items)
            {
                yield return new IdentityKey(IdentityKind.Object, child.Id.Value);
                foreach (var identity in NestedIdentitiesOf(child))
                {
                    yield return identity;
                }
            }
        }

        foreach (var node in owner.Line?.Nodes
                 ?? owner.Shape?.Regions.SelectMany(region => region.Nodes).ToList()
                 ?? [])
        {
            yield return new IdentityKey(IdentityKind.VectorNode, node.Id.Value);
        }
        foreach (var portal in owner.Line?.Portals ?? owner.Shape?.Portals ?? [])
        {
            yield return new IdentityKey(IdentityKind.Portal, portal.Id.Value);
        }
        foreach (var region in owner.Shape?.Regions ?? [])
        {
            yield return new IdentityKey(IdentityKind.ShapeRegion, region.Id.Value);
        }
    }

    private enum IdentityKind
    {
        Object,
        Composition,
        VectorNode,
        Portal,
        ShapeRegion,
    }

    private sealed record AppendSegment(
        AppendSegment? Parent,
        IReadOnlyDictionary<ObjectId, DocumentObjectLocation> LocationsById,
        IReadOnlyList<DocumentObjectLocation> LocationsInOrder,
        IReadOnlyDictionary<IdentityKey, ObjectId> Identities);

    private readonly record struct IdentityKey(IdentityKind Kind, Guid Value);
}
