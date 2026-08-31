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
    private static readonly ConditionalWeakTable<MapDocument, DocumentRevisionIndex> Cache = [];

    private readonly MapDocument _document;
    private readonly DocumentRevisionIndex _root;
    private readonly IReadOnlyDictionary<ObjectId, int> _rootPositions;
    private readonly IReadOnlyList<DocumentObjectLocation> _rootLocations;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<ObjectId>> _rootGroups;
    private readonly IReadOnlyDictionary<IdentityKey, ObjectId> _rootIdentities;
    private readonly Dictionary<ObjectId, DocumentObjectLocation> _overrides;

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
    }

    private DocumentRevisionIndex(
        MapDocument document,
        DocumentRevisionIndex root,
        Dictionary<ObjectId, DocumentObjectLocation> overrides)
    {
        _document = document;
        _root = root;
        _rootPositions = root._rootPositions;
        _rootLocations = root._rootLocations;
        _rootGroups = root._rootGroups;
        _rootIdentities = root._rootIdentities;
        _overrides = overrides;
    }

    public static DocumentRevisionIndex For(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Cache.GetValue(document, static snapshot => new DocumentRevisionIndex(snapshot));
    }

    public int ObjectCount => _rootPositions.Count;

    public int OverlayObjectCount => _overrides.Count;

    public bool TryFind(ObjectId id, out DocumentObjectLocation location)
    {
        if (_overrides.TryGetValue(id, out location))
        {
            return true;
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

        if (overrides.Count > MaxOverlayObjects)
        {
            return;
        }

        Cache.Add(target, new DocumentRevisionIndex(target, sourceIndex._root, overrides));
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

    private readonly record struct IdentityKey(IdentityKind Kind, Guid Value);
}
