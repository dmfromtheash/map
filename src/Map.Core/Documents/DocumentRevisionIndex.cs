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
/// <remarks>
/// The index is a function of the layer list alone, so it is keyed by the document's immutable
/// <see cref="MapDocument.Layers"/> instance: every snapshot that differs only in map-wide
/// properties (fog, style, notes, checkpoints, foreground, a record <c>with</c>) shares it
/// without any registration, the way persistent trees share unchanged subtrees.
/// </remarks>
public sealed class DocumentRevisionIndex
{
    private const int MaxOverlayObjects = 1_024;

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

    private const int MaxAppendDepth = 256;
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
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<ObjectId>> _rootGroups;
    private readonly IReadOnlyDictionary<IdentityKey, ObjectId> _rootIdentities;
    private readonly Dictionary<ObjectId, DocumentObjectLocation> _overrides;
    private readonly IReadOnlyDictionary<IdentityKey, ObjectId> _overrideIdentities;
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
        _rootGroups = groups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ObjectId>)pair.Value);
        _rootIdentities = identities;
        _seenLayers = [.. documentLayers.Select(layer => layer.Id)];
        _overrides = [];
        _overrideIdentities = new Dictionary<IdentityKey, ObjectId>();
        _appendTail = null;
        _appendedCount = 0;
        _appendDepth = 0;
    }

    private DocumentRevisionIndex(
        IReadOnlyList<MapLayer> document,
        DocumentRevisionIndex root,
        Dictionary<ObjectId, DocumentObjectLocation> overrides,
        IReadOnlyDictionary<IdentityKey, ObjectId> overrideIdentities,
        AppendSegment? appendTail,
        int appendedCount,
        int appendDepth)
    {
        _layers = document;
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

    /// <summary>The same stores over another layer list (same ids, order and object storage).</summary>
    private DocumentRevisionIndex Retarget(IReadOnlyList<MapLayer> layers) =>
        new(layers, _root, _overrides, _overrideIdentities, _appendTail, _appendedCount, _appendDepth)
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

    /// <summary>Append segments chained over the root. Diagnostics and tests only.</summary>
    internal int AppendDepth => _appendDepth;

    public bool TryFind(ObjectId id, out DocumentObjectLocation location) => TryLocate(id, out location, out _);

    /// <summary>
    /// Walks the stored locations newest store first. A location on a structurally changed layer
    /// is checked against that layer's identity slots; when the object has left it (deleted, or
    /// moved to another layer) the walk continues to the older stores instead of giving up.
    /// </summary>
    private bool TryLocate(ObjectId id, out DocumentObjectLocation location, out SceneObject? stored)
    {
        if (_overrides.TryGetValue(id, out location) && Resolve(id, ref location, out stored)) return true;
        for (var segment = _appendTail; segment is not null; segment = segment.Parent)
        {
            if (segment.LocationsById.TryGetValue(id, out location) && Resolve(id, ref location, out stored)) return true;
        }

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

        foreach (var stored in _overrides.Values)
        {
            // An override can outlive its object (deleted) or its layer (moved): read the live one.
            if (stored.Object.GroupId == groupId
                && TryFind(stored.Object.Id, out var current)
                && current.Object.GroupId == groupId
                && ids.Add(current.Object.Id))
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
            // Only overrides whose object still exists; a deleted one owns no identity any more.
            if (TryFind(location.Object.Id, out var live)) delta[location.Object.Id] = live.Object;
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
                    && TryFind(new ObjectId(identity.Value), out _))
                {
                    throw new ArgumentException(
                        "A composition child id collides with a top-level object.",
                        nameof(replacements));
                }
                if (_rootIdentities.TryGetValue(identity, out var rootOwner)
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

        Publish(target, new DocumentRevisionIndex(
            target.Layers,
            sourceIndex._root,
            overrides,
            BuildIdentityIndex(overrides.Values),
            sourceIndex._appendTail,
            sourceIndex._appendedCount,
            sourceIndex._appendDepth) { _structuralLayers = sourceIndex._structuralLayers, _layerPositions = sourceIndex._layerPositions, _seenLayers = sourceIndex._seenLayers });
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
        IReadOnlyList<DocumentObjectLocation> additions, ObjectLayer? structuralLayer = null)
    {
        if (additions.Count == 0
            || !Cache.TryGetValue(source.Layers, out var sourceIndex)
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
        var structural = sourceIndex._structuralLayers;
        if (structuralLayer is not null) structural = structural.Add(structuralLayer.Id);
        Publish(target, new DocumentRevisionIndex(
            target.Layers,
            sourceIndex._root,
            sourceIndex._overrides,
            sourceIndex._overrideIdentities,
            segment,
            checked(sourceIndex._appendedCount + additions.Count),
            sourceIndex._appendDepth + 1) { _structuralLayers = structural, _layerPositions = sourceIndex._layerPositions, _seenLayers = sourceIndex._seenLayers });
    }

    /// <summary>
    /// Carries the previous append-chain state through a normal LIFO undo. If the caller is not
    /// removing exactly the latest registered segment, the range is registered as an ordinary
    /// removal from its layer instead of leaving the target to a cold index over every object.
    /// </summary>
    internal static void RegisterTailRemoval(
        MapDocument source,
        MapDocument target,
        IReadOnlyList<SceneObject> removals,
        LayerId layerId)
    {
        if (removals.Count == 0 || !Cache.TryGetValue(source.Layers, out var sourceIndex))
        {
            return;
        }

        var tail = sourceIndex._appendTail;
        var matches = tail is not null && tail.LocationsInOrder.Count == removals.Count;
        for (var index = 0; matches && index < removals.Count; index++)
        {
            var id = removals[index].Id;
            matches = tail!.LocationsInOrder[index].Object.Id == id && !sourceIndex._overrides.ContainsKey(id);
        }

        if (!matches)
        {
            // Not the latest append segment (an edit of the appended objects came between, or a
            // move appended them): the tail range simply leaves its layer, located by identity.
            RegisterRemoval(source, target, layerId, removals.Count);
            return;
        }

        Publish(target, new DocumentRevisionIndex(
            target.Layers,
            sourceIndex._root,
            sourceIndex._overrides,
            sourceIndex._overrideIdentities,
            tail!.Parent,
            sourceIndex._appendedCount - removals.Count,
            sourceIndex._appendDepth - 1) { _structuralLayers = sourceIndex._structuralLayers, _layerPositions = sourceIndex._layerPositions, _seenLayers = sourceIndex._seenLayers });
    }

    /// <summary>
    /// Objects left one layer (delete, move-to-layer source, path regeneration). Nothing stored is
    /// rewritten: the layer becomes structural, so its identity slots decide presence and position.
    /// </summary>
    internal static void RegisterRemoval(MapDocument source, MapDocument target, LayerId layerId, int removed)
    {
        if (!Cache.TryGetValue(source.Layers, out var previous)) return;
        Publish(target, new DocumentRevisionIndex(target.Layers, previous._root, previous._overrides,
            previous._overrideIdentities, previous._appendTail, previous._appendedCount, previous._appendDepth)
        { _structuralLayers = previous._structuralLayers.Add(layerId), _layerPositions = previous._layerPositions, _seenLayers = previous._seenLayers });
    }

    /// <summary>
    /// Undo of a removal: the very objects return to the structural layer they left. When every
    /// one of them is again what the stored locations lead to, the source state is shared as it
    /// is (no new append segment, so delete/undo cycles never deepen the chain). Returns false
    /// when that cannot be proven; the caller then registers an ordinary append segment.
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
            previous._overrideIdentities, previous._appendTail, previous._appendedCount, previous._appendDepth)
        { _structuralLayers = previous._structuralLayers, _layerPositions = previous._layerPositions, _seenLayers = previous._seenLayers };
        foreach (var addition in additions)
        {
            if (!candidate.TryLocate(addition.Id, out var location, out var stored)
                || location.LayerId != layerId
                || !ReferenceEquals(stored, addition)
                || !ReferenceEquals(location.Object, addition))
            {
                return false;
            }
        }

        Publish(target, candidate);
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
    /// as one segment (an object already known under another record becomes an override, so its
    /// old nested identities are released). Work is proportional to those entering objects.
    /// </summary>
    internal static void RegisterLayerDelta(MapDocument source, MapDocument target,
        IReadOnlySet<LayerId> keptObjectLayers)
    {
        if (!Cache.TryGetValue(source.Layers, out var previous) || previous._appendDepth >= MaxAppendDepth) return;
        var layers = target.Layers;
        var positions = new Dictionary<LayerId, int>(layers.Count);
        for (var index = 0; index < layers.Count; index++) positions.Add(layers[index].Id, index);
        var structural = previous._structuralLayers;
        Dictionary<ObjectId, DocumentObjectLocation>? overrides = null;
        var appended = new List<DocumentObjectLocation>();
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
                    overrides ??= new(previous._overrides);
                    overrides[sceneObject.Id] = location;
                }
                else
                {
                    appended.Add(location);
                }
            }
        }

        if (overrides is not null && overrides.Count > MaxOverlayObjects
            && !layers.OfType<ObjectLayer>().Any(l => !l.PlacementPaths.IsEmpty || !l.PlacementAreas.IsEmpty))
            return;

        var tail = previous._appendTail;
        var appendedCount = previous._appendedCount;
        var depth = previous._appendDepth;
        if (appended.Count > 0)
        {
            var identities = new Dictionary<IdentityKey, ObjectId>();
            foreach (var addition in appended)
                foreach (var identity in NestedIdentitiesOf(addition.Object))
                    identities.Add(identity, addition.Object.Id);
            tail = new AppendSegment(tail, appended.ToDictionary(location => location.Object.Id), [.. appended], identities);
            appendedCount = checked(appendedCount + appended.Count);
            depth++;
        }

        Publish(target, new DocumentRevisionIndex(layers, previous._root, overrides ?? previous._overrides,
            overrides is null ? previous._overrideIdentities : BuildIdentityIndex(overrides.Values),
            tail, appendedCount, depth)
        { _structuralLayers = structural, _layerPositions = positions, _seenLayers = previous._seenLayers.Union(positions.Keys) });
    }

    private bool TryFindIdentity(IdentityKey identity, out ObjectId owner)
    {
        // An identity is taken only while its owner is still part of the document: a deleted
        // vector or composition releases its nested ids, so Undo can bring them back.
        if (_overrideIdentities.TryGetValue(identity, out owner) && OwnerExists(owner))
        {
            return true;
        }

        for (var segment = _appendTail; segment is not null; segment = segment.Parent)
        {
            if (segment.Identities.TryGetValue(identity, out owner)
                && !_overrides.ContainsKey(owner) && OwnerExists(owner))
            {
                return true;
            }
        }

        if (_rootIdentities.TryGetValue(identity, out owner)
            && !_overrides.ContainsKey(owner) && OwnerExists(owner))
        {
            return true;
        }

        owner = default;
        return false;
    }

    private bool OwnerExists(ObjectId owner) =>
        _structuralLayers.IsEmpty && _layerPositions is null || TryFind(owner, out _);

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
