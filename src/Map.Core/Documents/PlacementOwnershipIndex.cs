using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>Revision-local ownership. Member ids point to owner ids so a metadata edit never
/// rewrites the owner's unchanged members. Nothing here is saved or retains a document.</summary>
internal sealed class PlacementOwnershipIndex
{
    private static readonly ConditionalWeakTable<ObjectLayer, PlacementOwnershipIndex> Cache = [];
    private readonly ImmutableDictionary<ObjectId, Guid> _members;
    private readonly ImmutableDictionary<Guid, object> _owners;

    private PlacementOwnershipIndex(ImmutableDictionary<ObjectId, Guid> members,
        ImmutableDictionary<Guid, object> owners)
    { _members = members; _owners = owners; }

    internal static PlacementOwnershipIndex For(ObjectLayer layer) => Cache.GetValue(layer, static source =>
    {
        var members = ImmutableDictionary.CreateBuilder<ObjectId, Guid>();
        var owners = ImmutableDictionary.CreateBuilder<Guid, object>();
        foreach (var area in source.PlacementAreas)
        {
            owners.Add(area.Id, area);
            foreach (var member in area.Members) members.Add(member.ObjectId, area.Id);
        }
        foreach (var path in source.PlacementPaths)
        {
            owners.Add(path.Id, path);
            foreach (var member in path.Members) members.Add(member.ObjectId, path.Id);
        }
        return new(members.ToImmutable(), owners.ToImmutable());
    });

    internal object? Owner(ObjectId id) => _members.TryGetValue(id, out var owner)
        ? _owners.GetValueOrDefault(owner) : null;

    internal static void Share(ObjectLayer source, ObjectLayer target)
    {
        if (source.PlacementAreas == target.PlacementAreas && source.PlacementPaths == target.PlacementPaths)
            Cache.GetValue(target, _ => For(source));
    }

    internal static void Register(ObjectLayer source, ObjectLayer target, Guid id, object? owner,
        IEnumerable<ObjectId> removed, IEnumerable<ObjectId> added)
    {
        var previous = For(source);
        var members = previous._members.ToBuilder();
        foreach (var member in removed) members.Remove(member);
        foreach (var member in added) members[member] = id;
        var owners = owner is null ? previous._owners.Remove(id) : previous._owners.SetItem(id, owner);
        Cache.AddOrUpdate(target, new(members.ToImmutable(), owners));
    }

    internal static void RegisterAreas(ObjectLayer source, ObjectLayer target,
        IEnumerable<PlacementArea> revised, IEnumerable<ObjectId> removed)
    {
        var previous = For(source);
        var members = previous._members.RemoveRange(removed);
        var owners = previous._owners.ToBuilder();
        foreach (var area in revised) owners[area.Id] = area;
        Cache.AddOrUpdate(target, new(members, owners.ToImmutable()));
    }

    internal static bool Allows<TOwner>(MapDocument before, MapDocument after) where TOwner : class
    {
        foreach (var source in before.Layers.OfType<ObjectLayer>())
        {
            if (typeof(TOwner) == typeof(PlacementArea) ? source.PlacementAreas.IsEmpty : source.PlacementPaths.IsEmpty)
                continue;
            if (after.FindLayer(source.Id) is not ObjectLayer target || ReferenceEquals(source.Objects, target.Objects))
                continue;
            var previous = For(source);
            var current = For(target);
            // Multi-step commands may pass through removal, insertion and metadata revisions.
            // Unknown/publicly constructed revisions keep the full-check semantics.
            var changed = ChangedIds(source, target);
            foreach (var id in changed ?? previous._members.Keys)
            {
                if (!previous._members.TryGetValue(id, out var ownerId)
                    || previous._owners[ownerId] is not TOwner owner
                    || !ReferenceEquals(owner, current._owners.GetValueOrDefault(ownerId))) continue;
                var position = target.ObjectStorage.IndexOfId(id);
                var oldPosition = source.ObjectStorage.IndexOfId(id);
                if (position < 0 || target.Objects[position] != (oldPosition < 0 ? null : source.Objects[oldPosition]))
                    return false;
            }
        }
        return true;
    }

    private static IEnumerable<ObjectId>? ChangedIds(ObjectLayer source, ObjectLayer target)
    {
        var ids = new HashSet<ObjectId>();
        while (!ReferenceEquals(source.Objects, target.Objects))
        {
            if (!ObjectLayerChanges.TryGet(target, out var change)) return null;
            foreach (var i in change.Removed) ids.Add(change.Source.Objects[i].Id);
            foreach (var i in change.Added) ids.Add(target.Objects[i].Id);
            target = change.Source;
        }
        return ids;
    }
}
