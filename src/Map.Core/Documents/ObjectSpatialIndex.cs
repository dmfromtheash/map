using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>Persistent uniform grid. A changed snapshot updates only cells touched by its delta.</summary>
public sealed class ObjectSpatialIndex
{
    public const double CellSize = 1024;
    private static readonly ConditionalWeakTable<ObjectLayer, ObjectSpatialIndex> Cache = [];
    private readonly IReadOnlyList<SceneObject> _objects;
    private readonly ImmutableDictionary<(int X, int Y), ImmutableHashSet<int>> _cells;
    private readonly ImmutableHashSet<int> _oversized;
    private readonly ObjectSpatialIndex? _stableBasis;
    private readonly ImmutableDictionary<ObjectId, SceneObject?> _structuralOverrides = ImmutableDictionary<ObjectId, SceneObject?>.Empty;
    public int IncrementalOverrideCount { get; }

    private ObjectSpatialIndex(ObjectLayer layer)
    {
        _objects = layer.Objects;
        if (ObjectLayerChanges.TryGet(layer, out var delta))
        {
            var previous = For(delta.Source);
            if (delta.OrderCompacted || previous._stableBasis is not null)
            {
                _stableBasis = previous._stableBasis ?? previous;
                var overrides = previous._structuralOverrides.ToBuilder();
                foreach (var i in delta.Removed) overrides[delta.Source.Objects[i].Id] = null;
                foreach (var i in delta.Added) overrides[_objects[i].Id] = _objects[i];
                _structuralOverrides = overrides.ToImmutable();
                _cells = _stableBasis._cells; _oversized = _stableBasis._oversized;
                IncrementalOverrideCount = delta.Added.Count + delta.Removed.Count;
                return;
            }
            var cells = previous._cells.ToBuilder();
            var oversized = previous._oversized.ToBuilder();
            foreach (var index in delta.Removed) Change(cells, oversized, delta.Source.Objects[index], index, false);
            foreach (var index in delta.Added) Change(cells, oversized, _objects[index], index, true);
            _cells = cells.ToImmutable();
            _oversized = oversized.ToImmutable();
            IncrementalOverrideCount = delta.Added.Count;
        }
        else
        {
            var buckets = new Dictionary<(int X, int Y), HashSet<int>>();
            var oversized = ImmutableHashSet.CreateBuilder<int>();
            for (var index = 0; index < _objects.Count; index++)
            {
                var cells = CellsOf(_objects[index]);
                if (cells is null) { oversized.Add(index); continue; }
                foreach (var cell in cells)
                {
                    if (!buckets.TryGetValue(cell, out var bucket)) buckets[cell] = bucket = [];
                    bucket.Add(index);
                }
            }
            _cells = buckets.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableHashSet());
            _oversized = oversized.ToImmutable();
        }
    }

    /// <summary>
    /// The index of a layer's objects. It depends on the object list alone: a new record of the
    /// layer with the very same list (a mask, opacity, name or lock changed) shares the index of the
    /// record before it instead of indexing every object again (50 000 objects per mask stroke).
    /// </summary>
    public static ObjectSpatialIndex For(ObjectLayer layer)
    {
        if (Cache.TryGetValue(layer, out var known)) return known;
        if (ByObjects.TryGetValue(layer.Objects, out var shared))
        {
            Cache.AddOrUpdate(layer, shared);
            return shared;
        }
        var index = Cache.GetValue(layer, static l => new(l));
        ByObjects.AddOrUpdate(layer.Objects, index);
        return index;
    }

    private static readonly ConditionalWeakTable<object, ObjectSpatialIndex> ByObjects = [];
    // All three edit kinds now use the same lazy ObjectLayerChanges handoff.
    internal static void RegisterReplacements(ObjectLayer source, ObjectLayer target,
        IReadOnlyDictionary<int, SceneObject> replacements) { }
    public int ObjectCount => _objects.Count;
    public int CellCount => _cells.Count;
    public int OversizedCount => _oversized.Count;

    public IReadOnlyList<SceneObject> QueryPoint(double worldX, double worldY)
    {
        if (_stableBasis is not null) return StructuralQuery(new(worldX, worldY, worldX, worldY), point: true);
        var hits = _oversized.ToHashSet();
        if (_cells.TryGetValue(((int)Math.Floor(worldX / CellSize), (int)Math.Floor(worldY / CellSize)), out var cell))
            hits.UnionWith(cell);
        return hits.Order().Select(i => _objects[i])
            .Where(o => { var b = SceneGeometry.BoundsOf(o); return b.Left <= worldX && b.Right >= worldX
                && b.Top <= worldY && b.Bottom >= worldY; }).ToArray();
    }

    /// <summary>
    /// Whether at least <paramref name="threshold"/> objects may lie in the view, from the sizes
    /// of the cells it touches alone: no sorting, no bounds test, no list. An object that spans
    /// several cells counts once per cell, so this can say yes where the exact query would say
    /// no by a little; the renderer uses it only to choose between two correct ways of drawing a
    /// screen, and the exact query of a dense screen was itself several milliseconds per frame.
    /// </summary>
    public bool MayContainAtLeast(WorldBounds view, int threshold)
    {
        if (threshold <= 0) return true;
        var basis = _stableBasis ?? this;
        var count = basis._oversized.Count + _structuralOverrides.Count;
        if (count >= threshold) return true;
        var minX = (int)Math.Floor(view.Left / CellSize);
        var maxX = (int)Math.Floor(view.Right / CellSize);
        var minY = (int)Math.Floor(view.Top / CellSize);
        var maxY = (int)Math.Floor(view.Bottom / CellSize);
        if (((long)maxX - minX + 1) * ((long)maxY - minY + 1) <= basis._cells.Count)
        {
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                if (basis._cells.TryGetValue((x, y), out var bucket) && (count += bucket.Count) >= threshold) return true;
        }
        else
        {
            foreach (var (cell, bucket) in basis._cells)
                if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY && (count += bucket.Count) >= threshold) return true;
        }
        return false;
    }

    public IReadOnlyList<SceneObject> Query(WorldBounds view)
    {
        if (_stableBasis is not null) return StructuralQuery(view, point: false);
        var minX = (int)Math.Floor(view.Left / CellSize);
        var maxX = (int)Math.Floor(view.Right / CellSize);
        var minY = (int)Math.Floor(view.Top / CellSize);
        var maxY = (int)Math.Floor(view.Bottom / CellSize);
        var hits = _oversized.ToHashSet();
        if (((long)maxX - minX + 1) * ((long)maxY - minY + 1) <= _cells.Count)
        {
            for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                if (_cells.TryGetValue((x, y), out var bucket)) hits.UnionWith(bucket);
        }
        else
        {
            foreach (var (cell, bucket) in _cells)
                if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY) hits.UnionWith(bucket);
        }
        return hits.Order().Select(i => _objects[i]).Where(o => SceneGeometry.BoundsOf(o).Intersects(view)).ToArray();
    }

    private IReadOnlyList<SceneObject> StructuralQuery(WorldBounds view, bool point)
    {
        var basis = point ? _stableBasis!.QueryPoint(view.Left, view.Top) : _stableBasis!.Query(view);
        var result = basis.Where(o => !_structuralOverrides.ContainsKey(o.Id)).ToList();
        foreach (var item in _structuralOverrides.Values.OfType<SceneObject>())
        {
            var b = SceneGeometry.BoundsOf(item);
            if (point ? b.Left <= view.Left && b.Right >= view.Right && b.Top <= view.Top && b.Bottom >= view.Bottom
                : b.Intersects(view)) result.Add(item);
        }
        var storage = (SceneObjectList)_objects;
        return result.OrderBy(o => storage.IndexOfId(o.Id)).ToArray();
    }

    private static void Change(ImmutableDictionary<(int X, int Y), ImmutableHashSet<int>>.Builder cells,
        ImmutableHashSet<int>.Builder oversized, SceneObject item, int index, bool add)
    {
        var keys = CellsOf(item);
        if (keys is null) { if (add) oversized.Add(index); else oversized.Remove(index); return; }
        foreach (var key in keys)
        {
            var bucket = cells.GetValueOrDefault(key) ?? ImmutableHashSet<int>.Empty;
            bucket = add ? bucket.Add(index) : bucket.Remove(index);
            if (bucket.IsEmpty) cells.Remove(key); else cells[key] = bucket;
        }
    }

    private static IReadOnlyList<(int X, int Y)>? CellsOf(SceneObject item)
    {
        var bounds = SceneGeometry.BoundsOf(item);
        var minX = (int)Math.Floor(bounds.Left / CellSize);
        var maxX = (int)Math.Floor(bounds.Right / CellSize);
        var minY = (int)Math.Floor(bounds.Top / CellSize);
        var maxY = (int)Math.Floor(bounds.Bottom / CellSize);
        if (((long)maxX - minX + 1) * ((long)maxY - minY + 1) > 64) return null;
        var result = new List<(int, int)>();
        for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++) result.Add((x, y));
        return result;
    }
}
