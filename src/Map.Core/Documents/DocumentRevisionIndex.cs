using System.Collections.Immutable;
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
/// child revision shares that root and persistent trees of changed/appended object locations;
/// nothing here participates in document equality or serialization.
/// </summary>
/// <remarks>
/// The index is a function of the layer list alone, so it is keyed by the document's immutable
/// <see cref="MapDocument.Layers"/> instance: every snapshot that differs only in map-wide
/// properties (fog, style, notes, checkpoints, foreground, a record <c>with</c>) shares it
/// without any registration, the way persistent trees share unchanged subtrees.
/// </remarks>
public sealed class DocumentRevisionIndex
{
    /// <summary>
    /// Carries the index to a revision whose layers keep the source's ids, order and object
    /// storage (only layer properties, rivers, masks or tiles differ). A retargeted copy, so that
    /// <see cref="FindLayerOf"/> answers with the target's own layer records.
    /// </summary>
    internal static void ShareUnchangedLayers(MapDocument source, MapDocument target)
    {
        if (ReferenceEquals(source.Layers, target.Layers) || !Cache.TryGetValue(source.Layers, out var index)) return;
        Publish(target, index.Retarget(target.Layers));
    }

    private static readonly ConditionalWeakTable<IReadOnlyList<MapLayer>, DocumentRevisionIndex> Cache = [];

    // A key that already has an index keeps it: both describe the same immutable layer list.
    private static void Publish(MapDocument target, DocumentRevisionIndex index) => Cache.TryAdd(target.Layers, index);

    private readonly IReadOnlyList<MapLayer> _layers;
    // Null while stored layer indexes are current; after a layer-list change (add, delete,
    // reorder, replace) the current position of each layer id, and absent ids are gone.
    private IReadOnlyDictionary<LayerId, int>? _layerPositions;
    // Every layer id any store of this index may point at. A layer id outside it is brand new:
    // no stale location can name it, so its appended locations are exact without identity slots.
    private System.Collections.Immutable.ImmutableHashSet<LayerId> _seenLayers = [];
    private readonly DocumentRevisionIndex _root;
    private readonly IReadOnlyDictionary<ObjectId, int> _rootPositions;
    private readonly IReadOnlyList<DocumentObjectLocation> _rootLocations;
    private readonly ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> _groups;
    private readonly IReadOnlyDictionary<IdentityKey, ObjectId> _rootIdentities;
    private readonly ImmutableDictionary<ObjectId, DocumentObjectLocation> _overrides;
    private readonly ImmutableDictionary<IdentityKey, ObjectId> _overrideIdentities;
    private readonly AppendSegment? _appendTail;
    private readonly int _appendedCount;
    private readonly int _appendDepth;
    // Layers whose order changed by removal or insertion: their stored positions are only hints,
    // and the layer's own identity slots give the current position (or absence) of an object.
    private System.Collections.Immutable.ImmutableHashSet<LayerId> _structuralLayers = [];

    /// <summary>Number of full revision indexes built (every object visited). Diagnostics and tests only.</summary>
    internal static long RootBuilds => Interlocked.Read(ref _rootBuilds);
    private static long _rootBuilds;

    private DocumentRevisionIndex(IReadOnlyList<MapLayer> documentLayers)
    {
        Interlocked.Increment(ref _rootBuilds);
        _layers = documentLayers;
        _root = this;
        // Presized: on a million objects the doubling of these two alone allocated (and promoted)
        // about as much again as their final size, in large-object arrays, during the first gesture.
        var total = 0;
        foreach (var candidate in documentLayers)
            if (candidate is ObjectLayer counted) total += counted.Objects.Count;
        var positions = new Dictionary<ObjectId, int>(total);
        var locations = new List<DocumentObjectLocation>(total);
        var groups = new Dictionary<Guid, List<ObjectId>>();
        var identities = new Dictionary<IdentityKey, ObjectId>();
        for (var layerIndex = 0; layerIndex < documentLayers.Count; layerIndex++)
        {
            if (documentLayers[layerIndex] is not ObjectLayer layer)
            {
                continue;
            }

            // Warm stable storage locations with the cold document index, so later local
            // removals never build an identity table for the whole layer during a gesture.
            if (layer.Objects.Count > 0) _ = layer.ObjectStorage.IndexOfId(layer.Objects[0].Id);

            for (var objectIndex = 0; objectIndex < layer.Objects.Count; objectIndex++)
            {
                // A background build pauses here while the user pans or zooms (no-op elsewhere).
                if ((objectIndex & 0xFFFF) == 0xFFFF) Resources.BackgroundWork.YieldToInteraction();
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
        _groups = groups.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableHashSet());
        _rootIdentities = identities;
        _seenLayers = [.. documentLayers.Select(layer => layer.Id)];
        _overrides = [];
        _overrideIdentities = ImmutableDictionary<IdentityKey, ObjectId>.Empty;
        _appendTail = null;
        _appendedCount = 0;
        _appendDepth = 0;
    }

    private DocumentRevisionIndex(
        IReadOnlyList<MapLayer> document,
        DocumentRevisionIndex root,
        ImmutableDictionary<ObjectId, DocumentObjectLocation> overrides,
        ImmutableDictionary<IdentityKey, ObjectId> overrideIdentities,
        AppendSegment? appendTail,
        int appendedCount,
        int appendDepth,
        ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> groups)
    {
        _layers = document;
        _root = root;
        _rootPositions = root._rootPositions;
        _rootLocations = root._rootLocations;
        _groups = groups;
        _rootIdentities = root._rootIdentities;
        _overrides = overrides;
        _overrideIdentities = overrideIdentities;
        _appendTail = appendTail;
        _appendedCount = appendedCount;
        _appendDepth = appendDepth;
    }

    /// <summary>The same stores over another layer list (same ids, order and object storage).</summary>
    private DocumentRevisionIndex Retarget(IReadOnlyList<MapLayer> layers) =>
        new(layers, _root, _overrides, _overrideIdentities, _appendTail, _appendedCount, _appendDepth, _groups)
        { _structuralLayers = _structuralLayers, _layerPositions = _layerPositions, _seenLayers = _seenLayers };

    public static DocumentRevisionIndex For(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Cache.GetValue(document.Layers, static layers => new DocumentRevisionIndex(layers));
    }

    internal static bool IsPrepared(MapDocument document) => Cache.TryGetValue(document.Layers, out _);

    internal static bool TryGetPrepared(MapDocument document, out DocumentRevisionIndex index) =>
        Cache.TryGetValue(document.Layers, out index!);

    /// <summary>Top-level objects of the revision, from the stored layer counts.</summary>
    public int ObjectCount
    {
        get
        {
            var count = 0;
            foreach (var layer in _layers)
                if (layer is ObjectLayer objects) count = checked(count + objects.Objects.Count);
            return count;
        }
    }

    public int OverlayObjectCount => _overrides.Count;

    /// <summary>Append deltas registered since the root; no linked lookup chain. Diagnostics only.</summary>
    internal int AppendDepth => _appendDepth;
    internal int AppendedObjectCount => _appendTail?.LocationsById.Count ?? 0;

    public bool TryFind(ObjectId id, out DocumentObjectLocation location) => TryLocate(id, out location, out _);

    /// <summary>
    /// Reads overrides, the persistent append lookup and the root. A location on a structurally changed layer
    /// is checked against that layer's identity slots; when the object has left it (deleted, or
    /// moved to another layer) the walk continues to the older stores instead of giving up.
    /// </summary>
    private bool TryLocate(ObjectId id, out DocumentObjectLocation location, out SceneObject? stored)
    {
        if (_overrides.TryGetValue(id, out location) && Resolve(id, ref location, out stored)) return true;
        if (_appendTail is { } tail && tail.LocationsById.TryGetValue(id, out location)
            && Resolve(id, ref location, out stored)) return true;

        if (_rootPositions.TryGetValue(id, out var position))
        {
            location = _rootLocations[position];
            if (Resolve(id, ref location, out stored)) return true;
        }

        location = default;
        stored = null;
        return false;
    }

    private bool Resolve(ObjectId id, ref DocumentObjectLocation location, out SceneObject? stored)
    {
        stored = location.Object;
        if (_layerPositions is not null)
        {
            // The layer list changed since this location was stored: a layer that left takes
            // its objects with it, and a moved layer answers at its current position.
            if (!_layerPositions.TryGetValue(location.LayerId, out var layerIndex)
                || _layers[layerIndex] is not ObjectLayer) return false;
            if (layerIndex != location.LayerIndex) location = location with { LayerIndex = layerIndex };
        }
        if (_structuralLayers.IsEmpty || !_structuralLayers.Contains(location.LayerId)) return true;
        // The current layer may include subsequent point replacements/appends.
        var layer = (ObjectLayer)_layers[location.LayerIndex];
        var position = layer.ObjectStorage.IndexOfId(id);
        if (position < 0) return false;
        location = location with { Object = layer.Objects[position], ObjectIndex = position };
        return true;
    }

    public SceneObject? FindObject(ObjectId id) =>
        TryFind(id, out var location) ? location.Object : null;

    public ObjectLayer? FindLayerOf(ObjectId id)
    {
        if (!TryFind(id, out var location)
            || location.LayerIndex < 0
            || location.LayerIndex >= _layers.Count)
        {
            return null;
        }

        return _layers[location.LayerIndex] is ObjectLayer layer
            && layer.Id == location.LayerId
            ? layer
            : _layers.FirstOrDefault(candidate => candidate.Id == location.LayerId) as ObjectLayer;
    }

    /// <summary>Current members in document order, including group-id changes in this overlay.</summary>
    public IReadOnlyList<SceneObject> GroupMembers(Guid groupId)
    {
        var ids = new HashSet<ObjectId>();
        var members = new List<DocumentObjectLocation>();
        if (_groups.TryGetValue(groupId, out var rootMembers))
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
        var delta = proposed;

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
                    && TryFind(new ObjectId(identity.Value), out _))
                {
                    throw new ArgumentException(
                        "A composition child id collides with a top-level object.",
                        nameof(replacements));
                }
                if (TryFindIdentity(identity, out var rootOwner)
                    && rootOwner != owner
                    && !delta.ContainsKey(rootOwner)
                    && TryFind(rootOwner, out _))
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
        if (!Cache.TryGetValue(source.Layers, out var sourceIndex))
        {
            return;
        }

        var overrides = sourceIndex._overrides.ToBuilder();
        foreach (var replacement in replacements)
        {
            overrides[replacement.Object.Id] = replacement;
        }

        Publish(target, new DocumentRevisionIndex(
            target.Layers,
            sourceIndex._root,
            overrides.ToImmutable(),
            UpdateIdentities(sourceIndex, replacements),
            sourceIndex._appendTail,
            sourceIndex._appendedCount,
            sourceIndex._appendDepth, UpdateGroups(sourceIndex, replacements)) { _structuralLayers = sourceIndex._structuralLayers, _layerPositions = sourceIndex._layerPositions, _seenLayers = sourceIndex._seenLayers });
    }

    /// <summary>
    /// Validates only objects appended by one command. Existing document identities were already
    /// proven by the source revision; checking the delta avoids reconstructing every global set.
    /// </summary>
    internal void ValidateAppends(IReadOnlyList<SceneObject> additions, IReadOnlySet<ObjectId>? removed = null)
    {
        ArgumentNullException.ThrowIfNull(additions);
        var topLevelIds = new HashSet<ObjectId>();
        var nestedIdentities = new Dictionary<IdentityKey, ObjectId>();
        var groupNames = new Dictionary<Guid, string?>();

        foreach (var sceneObject in additions)
        {
            if (!topLevelIds.Add(sceneObject.Id) || TryFind(sceneObject.Id, out _) && removed?.Contains(sceneObject.Id) != true)
            {
                throw new ArgumentException($"Duplicate object id {sceneObject.Id}.", nameof(additions));
            }

            var topLevelIdentity = new IdentityKey(IdentityKind.Object, sceneObject.Id.Value);
            if (TryFindIdentity(topLevelIdentity, out var topOwner) && removed?.Contains(topOwner) != true
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
                    if (removed is not null) existingMembers = existingMembers.Where(o => !removed.Contains(o.Id)).ToArray();
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
                    && (TryFind(new ObjectId(identity.Value), out _) && removed?.Contains(new ObjectId(identity.Value)) != true
                        || topLevelIds.Contains(new ObjectId(identity.Value)));
                if (nestedObjectCollides
                    || TryFindIdentity(identity, out var owner) && removed?.Contains(owner) != true
                    || !nestedIdentities.TryAdd(identity, sceneObject.Id))
                {
                    throw new ArgumentException(
                        "Appended vector/composition identities must stay unique.", nameof(additions));
                }
            }
        }
    }

    /// <summary>
    /// Carries an append delta into persistent lookup trees. Old snapshots remain usable and
    /// no edit drops the prepared root, regardless of the number of preceding appends.
    /// </summary>
    internal static void RegisterAppends(
        MapDocument source,
        MapDocument target,
        IReadOnlyList<DocumentObjectLocation> additions, ObjectLayer? structuralLayer = null)
    {
        if (additions.Count == 0
            || !Cache.TryGetValue(source.Layers, out var sourceIndex))
        {
            return;
        }

        var segment = Append(sourceIndex._appendTail, additions);
        var structural = sourceIndex._structuralLayers;
        if (structuralLayer is not null) structural = structural.Add(structuralLayer.Id);
        Publish(target, new DocumentRevisionIndex(
            target.Layers,
            sourceIndex._root,
            sourceIndex._overrides,
            sourceIndex._overrideIdentities,
            segment,
            checked(sourceIndex._appendedCount + additions.Count),
            sourceIndex._appendDepth + 1, UpdateGroups(sourceIndex, additions)) { _structuralLayers = structural, _layerPositions = sourceIndex._layerPositions, _seenLayers = sourceIndex._seenLayers });
    }

    /// <summary>
    /// Tail undo uses the same identity-slot resolution as any removal. The persistent stores
    /// do not retain a chain of older index versions solely to undo an append.
    /// </summary>
    internal static void RegisterTailRemoval(
        MapDocument source,
        MapDocument target,
        IReadOnlyList<SceneObject> removals,
        LayerId layerId)
    {
        if (removals.Count > 0) RegisterRemoval(source, target, layerId, removals);
    }

    /// <summary>
    /// Objects left one layer (delete, move-to-layer source, path regeneration). Nothing stored is
    /// rewritten in the root: identity slots decide presence and position. Delta entries for the
    /// removed objects are released, so a live index does not retain every historical append.
    /// </summary>
    internal static void RegisterRemoval(MapDocument source, MapDocument target, LayerId layerId, IReadOnlyList<SceneObject> removed)
    {
        if (!Cache.TryGetValue(source.Layers, out var previous)) return;
        var stores = previous.Without(removed);
        Publish(target, new DocumentRevisionIndex(target.Layers, previous._root, stores.Overrides,
            stores.Identities, stores.Appends, previous._appendedCount, previous._appendDepth, stores.Groups)
        { _structuralLayers = previous._structuralLayers.Add(layerId), _layerPositions = previous._layerPositions, _seenLayers = previous._seenLayers });
    }

    /// <summary>A single structural splice, including new objects or changed reused identities.</summary>
    internal static void RegisterSplice(MapDocument source, MapDocument target, LayerId layerId,
        IReadOnlyList<DocumentObjectLocation> additions)
    {
        if (!Cache.TryGetValue(source.Layers, out var previous)) return;
        var structural = previous._structuralLayers.Add(layerId);
        var candidate = new DocumentRevisionIndex(target.Layers, previous._root, previous._overrides,
            previous._overrideIdentities, previous._appendTail, previous._appendedCount, previous._appendDepth, previous._groups)
        { _structuralLayers = structural, _layerPositions = previous._layerPositions, _seenLayers = previous._seenLayers };
        var overrides = previous._overrides.ToBuilder();
        var replaced = new List<DocumentObjectLocation>();
        var entering = new List<DocumentObjectLocation>();
        foreach (var addition in additions)
        {
            if (!candidate.TryLocate(addition.Object.Id, out _, out var stored)) entering.Add(addition);
            else if (!ReferenceEquals(stored, addition.Object))
            {
                replaced.Add(addition);
                overrides[addition.Object.Id] = addition;
            }
        }
        var tail = entering.Count == 0 ? previous._appendTail : Append(previous._appendTail, entering);
        Publish(target, new DocumentRevisionIndex(target.Layers, previous._root, overrides.ToImmutable(),
            UpdateIdentities(previous, replaced), tail,
            previous._appendedCount + entering.Count, previous._appendDepth + (entering.Count > 0 ? 1 : 0), UpdateGroups(previous, additions))
        { _structuralLayers = structural, _layerPositions = previous._layerPositions, _seenLayers = previous._seenLayers });
    }

    private (ImmutableDictionary<ObjectId, DocumentObjectLocation> Overrides,
        ImmutableDictionary<IdentityKey, ObjectId> Identities, AppendSegment? Appends,
        ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> Groups) Without(IReadOnlyList<SceneObject> removed)
    {
        var overrides = _overrides.ToBuilder();
        var identities = _overrideIdentities.ToBuilder();
        var appends = _appendTail?.LocationsById.ToBuilder();
        var appendedIdentities = _appendTail?.Identities.ToBuilder();
        var groups = _groups.ToBuilder();
        foreach (var item in removed)
        {
            if (overrides.Remove(item.Id, out var old)) Release(identities, old.Object);
            if (appends is not null && appends.Remove(item.Id, out var added)) Release(appendedIdentities!, added.Object);
            if (item.GroupId is { } group && groups.TryGetValue(group, out var members))
            {
                members = members.Remove(item.Id);
                if (members.IsEmpty) groups.Remove(group);
                else groups[group] = members;
            }
        }
        return (overrides.ToImmutable(), identities.ToImmutable(), appends is null || appends.Count == 0 ? null
            : new AppendSegment(appends.ToImmutable(), appendedIdentities!.ToImmutable()), groups.ToImmutable());

        static void Release(ImmutableDictionary<IdentityKey, ObjectId>.Builder map, SceneObject item)
        {
            foreach (var identity in NestedIdentitiesOf(item))
                if (map.TryGetValue(identity, out var owner) && owner == item.Id) map.Remove(identity);
        }
    }

    /// <summary>
    /// Undo of a removal: the very objects return to the structural layer they left. When every
    /// one of them is again what the stored locations lead to, the source state is shared as it
    /// is (no new append entries). Returns false when that cannot be proven; the caller then
    /// registers the returning objects in the persistent append lookup.
    /// </summary>
    internal static bool RegisterReinsertion(MapDocument source, MapDocument target, LayerId layerId,
        IReadOnlyList<SceneObject> additions)
    {
        if (additions.Count == 0 || !Cache.TryGetValue(source.Layers, out var previous)
            || !previous._structuralLayers.Contains(layerId))
        {
            return false;
        }

        var candidate = new DocumentRevisionIndex(target.Layers, previous._root, previous._overrides,
            previous._overrideIdentities, previous._appendTail, previous._appendedCount, previous._appendDepth, previous._groups)
        { _structuralLayers = previous._structuralLayers, _layerPositions = previous._layerPositions, _seenLayers = previous._seenLayers };
        var locations = new List<DocumentObjectLocation>(additions.Count);
        foreach (var addition in additions)
        {
            if (!candidate.TryLocate(addition.Id, out var location, out var stored)
                || location.LayerId != layerId
                || !ReferenceEquals(stored, addition)
                || !ReferenceEquals(location.Object, addition))
            {
                return false;
            }
            locations.Add(location);
        }

        Publish(target, new DocumentRevisionIndex(target.Layers, previous._root, previous._overrides,
            previous._overrideIdentities, previous._appendTail, previous._appendedCount, previous._appendDepth,
            UpdateGroups(previous, locations))
        { _structuralLayers = previous._structuralLayers, _layerPositions = previous._layerPositions, _seenLayers = previous._seenLayers });
        return true;
    }

    /// <summary>
    /// Validates a layer-list revision against this (source) index in time proportional to the
    /// objects that enter it: the objects of new layers and of layers whose object storage was
    /// replaced. Objects of layers whose storage is kept were proven unique when they entered;
    /// objects of layers that left or were replaced no longer own anything.
    /// </summary>
    /// <param name="keptObjectLayers">Target object layers whose storage is the source's own.</param>
    /// <param name="entering">Every top-level object of the other target object layers.</param>
    internal void ValidateLayerDelta(IReadOnlySet<LayerId> keptObjectLayers, IReadOnlyList<SceneObject> entering)
    {
        ArgumentNullException.ThrowIfNull(keptObjectLayers);
        ArgumentNullException.ThrowIfNull(entering);
        bool Stays(ObjectId id) => TryFind(id, out var location) && keptObjectLayers.Contains(location.LayerId);
        var topLevel = new HashSet<ObjectId>(entering.Count);
        foreach (var sceneObject in entering)
        {
            if (!topLevel.Add(sceneObject.Id) || Stays(sceneObject.Id))
                throw new ArgumentException($"Duplicate object id {sceneObject.Id}.", nameof(entering));
        }

        var nested = new HashSet<IdentityKey>();
        var groupNames = new Dictionary<Guid, string?>();
        foreach (var sceneObject in entering)
        {
            if (TryFindIdentity(new IdentityKey(IdentityKind.Object, sceneObject.Id.Value), out var holder) && Stays(holder))
                throw new ArgumentException("Composition internal object ids cannot collide.", nameof(entering));
            foreach (var identity in NestedIdentitiesOf(sceneObject))
            {
                if (!nested.Add(identity)
                    || identity.Kind == IdentityKind.Object
                        && (topLevel.Contains(new ObjectId(identity.Value)) || Stays(new ObjectId(identity.Value)))
                    || TryFindIdentity(identity, out var owner) && Stays(owner))
                    throw new ArgumentException("Vector node, portal and region ids are unique across the document.", nameof(entering));
            }

            if (sceneObject.GroupId is not { } groupId)
            {
                if (sceneObject.GroupName is not null)
                    throw new ArgumentException(
                        $"Object {sceneObject.Id} carries a group name without a group id.", nameof(entering));
                continue;
            }

            if (!groupNames.TryGetValue(groupId, out var expected))
            {
                expected = sceneObject.GroupName;
                foreach (var member in GroupMembers(groupId))
                {
                    if (!Stays(member.Id)) continue;
                    expected = member.GroupName;
                    break;
                }

                groupNames.Add(groupId, expected);
            }

            if (!string.Equals(expected, sceneObject.GroupName, StringComparison.Ordinal))
                throw new ArgumentException($"Members of group {groupId} disagree on the group name.", nameof(entering));
        }
    }

    /// <summary>
    /// Derives the index of a layer-list revision (layers added, deleted, reordered or replaced).
    /// Stored locations are kept and read through the new layer positions; a layer that left
    /// takes its objects with it, and the objects of new or replaced object layers are appended
    /// in the persistent append lookup (an object already known under another record becomes an override, so its
    /// old nested identities are released). Work is proportional to those entering objects.
    /// </summary>
    internal static void RegisterLayerDelta(MapDocument source, MapDocument target,
        IReadOnlySet<LayerId> keptObjectLayers)
    {
        if (!Cache.TryGetValue(source.Layers, out var previous)) return;
        var layers = target.Layers;
        var positions = new Dictionary<LayerId, int>(layers.Count);
        for (var index = 0; index < layers.Count; index++) positions.Add(layers[index].Id, index);
        var structural = previous._structuralLayers;
        ImmutableDictionary<ObjectId, DocumentObjectLocation>.Builder? overrides = null;
        var appended = new List<DocumentObjectLocation>();
        var replaced = new List<DocumentObjectLocation>();
        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            if (layers[layerIndex] is not ObjectLayer layer || keptObjectLayers.Contains(layer.Id)) continue;
            // A replaced layer, or one that returns (undo of a delete), may still be named by
            // older stores: its own identity slots decide presence there. A brand-new id cannot.
            if (previous._seenLayers.Contains(layer.Id)) structural = structural.Add(layer.Id);
            for (var objectIndex = 0; objectIndex < layer.Objects.Count; objectIndex++)
            {
                var sceneObject = layer.Objects[objectIndex];
                var location = new DocumentObjectLocation(sceneObject, layer.Id, layerIndex, objectIndex);
                if (previous.TryLocate(sceneObject.Id, out _, out var known) && !ReferenceEquals(known, sceneObject))
                {
                    overrides ??= previous._overrides.ToBuilder();
                    overrides[sceneObject.Id] = location;
                    replaced.Add(location);
                }
                else
                {
                    appended.Add(location);
                }
            }
        }

        var tail = previous._appendTail;
        var appendedCount = previous._appendedCount;
        var depth = previous._appendDepth;
        if (appended.Count > 0)
        {
            tail = Append(tail, appended);
            appendedCount = checked(appendedCount + appended.Count);
            depth++;
        }

        var changed = overrides?.ToImmutable();
        var next = new DocumentRevisionIndex(layers, previous._root, changed ?? previous._overrides,
            changed is null ? previous._overrideIdentities : UpdateIdentities(previous, replaced),
            tail, appendedCount, depth, UpdateGroups(previous, replaced.Concat(appended)))
        { _structuralLayers = structural, _layerPositions = positions, _seenLayers = previous._seenLayers.Union(positions.Keys) };
        var leaving = new List<SceneObject>();
        foreach (var layer in source.Layers)
            if (layer is ObjectLayer objects && !keptObjectLayers.Contains(layer.Id))
                foreach (var item in objects.Objects)
                    if (!next.TryFind(item.Id, out _)) leaving.Add(item);
        if (leaving.Count > 0)
        {
            var stores = next.Without(leaving);
            next = new DocumentRevisionIndex(layers, previous._root, stores.Overrides, stores.Identities,
                stores.Appends, appendedCount, depth, stores.Groups)
            { _structuralLayers = structural, _layerPositions = positions, _seenLayers = next._seenLayers };
        }
        Publish(target, next);
    }

    private bool TryFindIdentity(IdentityKey identity, out ObjectId owner)
    {
        // An identity is taken only while its owner is still part of the document: a deleted
        // vector or composition releases its nested ids, so Undo can bring them back.
        if (_overrideIdentities.TryGetValue(identity, out owner) && OwnerMatches(owner, _overrides[owner].Object))
        {
            return true;
        }

        if (_appendTail is { } segment)
        {
            if (segment.Identities.TryGetValue(identity, out owner)
                && OwnerMatches(owner, segment.LocationsById[owner].Object))
            {
                return true;
            }
        }

        if (_rootIdentities.TryGetValue(identity, out owner)
            && OwnerMatches(owner, _rootLocations[_rootPositions[owner]].Object))
        {
            return true;
        }

        owner = default;
        return false;
    }

    private bool OwnerMatches(ObjectId owner, SceneObject stored) =>
        TryFind(owner, out var current) && ReferenceEquals(current.Object, stored);

    private static AppendSegment Append(AppendSegment? previous, IReadOnlyList<DocumentObjectLocation> additions)
    {
        var locations = (previous?.LocationsById ?? ImmutableDictionary<ObjectId, DocumentObjectLocation>.Empty).ToBuilder();
        var identities = (previous?.Identities ?? ImmutableDictionary<IdentityKey, ObjectId>.Empty).ToBuilder();
        foreach (var addition in additions)
            if (locations.TryGetValue(addition.Object.Id, out var old))
                foreach (var identity in NestedIdentitiesOf(old.Object))
                    if (identities.TryGetValue(identity, out var owner) && owner == old.Object.Id) identities.Remove(identity);
        foreach (var addition in additions)
        {
            locations[addition.Object.Id] = addition;
            foreach (var identity in NestedIdentitiesOf(addition.Object)) identities[identity] = addition.Object.Id;
        }
        return new(locations.ToImmutable(), identities.ToImmutable());
    }

    private static ImmutableDictionary<IdentityKey, ObjectId> UpdateIdentities(
        DocumentRevisionIndex previous, IReadOnlyList<DocumentObjectLocation> locations)
    {
        var identities = previous._overrideIdentities.ToBuilder();
        foreach (var location in locations)
            if (previous._overrides.TryGetValue(location.Object.Id, out var old))
                foreach (var identity in NestedIdentitiesOf(old.Object))
                    if (identities.TryGetValue(identity, out var owner) && owner == old.Object.Id) identities.Remove(identity);
        foreach (var location in locations)
        {
            foreach (var identity in NestedIdentitiesOf(location.Object))
            {
                // Validation already rejected live collisions; this may replace a stale
                // identity of a deleted owner still present in the persistent stores.
                identities[identity] = location.Object.Id;
            }
        }

        return identities.ToImmutable();
    }

    private static ImmutableDictionary<Guid, ImmutableHashSet<ObjectId>> UpdateGroups(
        DocumentRevisionIndex previous, IEnumerable<DocumentObjectLocation> changes)
    {
        var groups = previous._groups.ToBuilder();
        foreach (var location in changes)
        {
            var item = location.Object;
            if (previous.TryFind(item.Id, out var old) && old.Object.GroupId is { } oldGroup)
            {
                var members = groups[oldGroup].Remove(item.Id);
                if (members.IsEmpty) groups.Remove(oldGroup);
                else groups[oldGroup] = members;
            }
            if (item.GroupId is { } group)
                groups[group] = groups.GetValueOrDefault(group, ImmutableHashSet<ObjectId>.Empty).Add(item.Id);
        }
        return groups.ToImmutable();
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
        ImmutableDictionary<ObjectId, DocumentObjectLocation> LocationsById,
        ImmutableDictionary<IdentityKey, ObjectId> Identities);

    private readonly record struct IdentityKey(IdentityKind Kind, Guid Value);
}
