using System.Collections.Immutable;

namespace Map.Core.Documents;

/// <summary>
/// Persistent broad phase for placement rules. A prepared layer shares unchanged cells across
/// feature edits; buckets hold ids, so replacing one feature never copies a dense bucket.
/// Geometry and the exact water/road predicates remain the responsibility of the caller.
/// </summary>
internal sealed class FeatureSpatialIndex<T> where T : class
{
    private const double CellSize = 1024;
    private readonly record struct Entry(T Item, WorldBounds Bounds);
    private readonly ImmutableDictionary<Guid, Entry> _entries;
    private readonly ImmutableDictionary<(int X, int Y), ImmutableHashSet<Guid>> _cells;
    private readonly ImmutableHashSet<Guid> _oversized;
    private readonly Entry? _single;

    private FeatureSpatialIndex(ImmutableDictionary<Guid, Entry> entries,
        ImmutableDictionary<(int X, int Y), ImmutableHashSet<Guid>> cells, ImmutableHashSet<Guid> oversized)
    {
        _entries = entries; _cells = cells; _oversized = oversized;
        _single = entries.Count == 1 ? entries.First().Value : null;
    }

    internal static FeatureSpatialIndex<T> Build(IEnumerable<T> items, Func<T, Guid> id, Func<T, WorldBounds> bounds) =>
        new FeatureSpatialIndex<T>(ImmutableDictionary<Guid, Entry>.Empty,
            ImmutableDictionary<(int, int), ImmutableHashSet<Guid>>.Empty, ImmutableHashSet<Guid>.Empty)
        .Change([], items, id, bounds);

    internal FeatureSpatialIndex<T> Change(IEnumerable<Guid> removed, IEnumerable<T> added,
        Func<T, Guid> idOf, Func<T, WorldBounds> boundsOf)
    {
        var entries = _entries.ToBuilder();
        var cells = _cells.ToBuilder();
        var oversized = _oversized.ToBuilder();
        foreach (var id in removed)
            if (entries.TryGetValue(id, out var old))
            { ChangeCells(id, old.Bounds, false); entries.Remove(id); }
        foreach (var item in added)
        {
            var id = idOf(item);
            if (entries.TryGetValue(id, out var old)) ChangeCells(id, old.Bounds, false);
            var bounds = boundsOf(item);
            entries[id] = new(item, bounds);
            ChangeCells(id, bounds, true);
        }
        return new(entries.ToImmutable(), cells.ToImmutable(), oversized.ToImmutable());

        void ChangeCells(Guid id, WorldBounds bounds, bool add)
        {
            if (!Range(bounds, out var x0, out var x1, out var y0, out var y1))
            { if (add) oversized.Add(id); else oversized.Remove(id); return; }
            for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
            {
                var bucket = cells.GetValueOrDefault((x, y), ImmutableHashSet<Guid>.Empty);
                bucket = add ? bucket.Add(id) : bucket.Remove(id);
                if (bucket.IsEmpty) cells.Remove((x, y)); else cells[(x, y)] = bucket;
            }
        }
    }

    // Boolean probes need no candidate list or deduplication set. Visiting a feature twice
    // at a cell boundary is harmless for Any and keeps the common point probe allocation-free.
    internal bool Any<TState>(WorldBounds bounds, TState state, Func<T, TState, bool> predicate)
    {
        if (_entries.IsEmpty) return false;
        if (_single is { } single) return single.Bounds.Intersects(bounds) && predicate(single.Item, state);
        if (!Range(bounds, out var x0, out var x1, out var y0, out var y1))
        {
            foreach (var pair in _entries)
                if (pair.Value.Bounds.Intersects(bounds) && predicate(pair.Value.Item, state)) return true;
            return false;
        }
        for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
            if (_cells.TryGetValue((x, y), out var ids))
                foreach (var id in ids)
                    if (_entries[id].Bounds.Intersects(bounds) && predicate(_entries[id].Item, state)) return true;
        foreach (var id in _oversized)
            if (_entries[id].Bounds.Intersects(bounds) && predicate(_entries[id].Item, state)) return true;
        return false;
    }

    private static bool Range(WorldBounds b, out int x0, out int x1, out int y0, out int y1)
    {
        x0 = x1 = y0 = y1 = 0;
        var left = Math.Floor(b.Left / CellSize); var right = Math.Floor(b.Right / CellSize);
        var top = Math.Floor(b.Top / CellSize); var bottom = Math.Floor(b.Bottom / CellSize);
        if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom)
            || left < int.MinValue || top < int.MinValue || right >= int.MaxValue || bottom >= int.MaxValue
            || right < left || bottom < top || (right - left + 1) * (bottom - top + 1) > 64) return false;
        x0 = (int)left; x1 = (int)right; y0 = (int)top; y1 = (int)bottom;
        return true;
    }
}
