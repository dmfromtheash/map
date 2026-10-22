using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Derived persistent order, warmed on load and carried by local edits; never serialized.</summary>
internal static class PlacementAreaDrawOrder
{
    internal readonly record struct Entry(double Y, double X, Guid Id) : IComparable<Entry>
    {
        public int CompareTo(Entry other)
        {
            var y = Y.CompareTo(other.Y); if (y != 0) return y;
            var x = X.CompareTo(other.X); return x != 0 ? x : Id.CompareTo(other.Id);
        }
    }
    private sealed record Index(ImmutableSortedSet<Entry> Entries);
    private static readonly ConditionalWeakTable<PlacementArea, Index> Cache = new();
    internal static Entry Key(PlacementArea area, SceneObject item)
    { var b = PlacementAreaSampler.VisibleBounds(area, item); return new(b.Bottom, (b.Left + b.Right) / 2, item.Id.Value); }
    internal static bool Uses(PlacementArea? area) => area is { LayoutVersion: >= 3, JoinEdges: true } or { DepthSort: true };
    internal static void Warm(PlacementArea area, IEnumerable<SceneObject> members)
    {
        if (Uses(area)) Cache.GetValue(area, _ => new(members.Select(o => Key(area, o)).ToImmutableSortedSet()));
    }
    internal static ImmutableSortedSet<Entry> For(PlacementArea area) => Cache.GetValue(area,
        a => new(a.Members.Select(m => Key(a, PlacementAreaPlanner.Item(a, m.Slot))).ToImmutableSortedSet())).Entries;
    internal static void Register(PlacementArea area, ImmutableSortedSet<Entry> entries) => Cache.GetValue(area, _ => new(entries));
    internal static void Detach(PlacementArea before, PlacementArea after, IEnumerable<SceneObject> removed)
    {
        if (Uses(before)) Register(after, For(before).Except(removed.Select(o => Key(before, o))));
    }

    internal static MapDocument Insert(MapDocument document, LayerId layerId, PlacementArea area,
        ImmutableSortedSet<Entry> surviving, IReadOnlyList<SceneObject> additions)
    {
        var layer = (ObjectLayer)document.FindLayer(layerId)!;
        var batches = new SortedDictionary<int, List<SceneObject>>();
        foreach (var item in additions.OrderBy(o => Key(area, o)))
        {
            var at = surviving.IndexOf(Key(area, item)); at = at < 0 ? ~at : at + 1;
            var position = at < surviving.Count ? layer.ObjectStorage.IndexOfId(new(surviving[at].Id))
                : surviving.Count > 0 ? layer.ObjectStorage.IndexOfId(new(surviving.Max.Id)) + 1 : layer.Objects.Count;
            if (position < 0) throw new InvalidOperationException("Не найден передний объект области.");
            if (!batches.TryGetValue(position, out var batch)) batches[position] = batch = [];
            batch.Add(item);
        }
        return DocumentEditor.InsertObjects(document, layerId, batches.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SceneObject>)kv.Value));
    }
}
