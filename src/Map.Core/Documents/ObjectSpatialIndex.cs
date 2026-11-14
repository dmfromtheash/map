using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>
/// Persistent uniform grid. A changed snapshot updates only cells touched by its delta. A cell holds
/// its object indices as one sorted array (4 bytes per object), replaced as a whole when it changes.
/// </summary>
public sealed class ObjectSpatialIndex
{
    /// <summary>
    /// The same bounded-cell broad phase for evaluated features that are not SceneObjects.
    /// Callers cache it by immutable feature storage; predicates still use the exact geometry.
    /// </summary>
    internal sealed class BoundsGrid<T>
    {
        private readonly (T Item, WorldBounds Bounds)[] _items;
        private readonly Dictionary<(int X, int Y), List<int>> _cells = [];
        private readonly List<int> _oversized = [];

        internal BoundsGrid(IEnumerable<(T Item, WorldBounds Bounds)> items)
        {
            _items = [.. items];
            for (var i = 0; i < _items.Length; i++)
            {
                if (!Range(_items[i].Bounds, out var x0, out var x1, out var y0, out var y1))
                { _oversized.Add(i); continue; }
                for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
                {
                    if (!_cells.TryGetValue((x, y), out var cell)) _cells.Add((x, y), cell = []);
                    cell.Add(i);
                }
            }
        }

        internal IEnumerable<T> Query(WorldBounds bounds)
        {
            // Unusual/non-finite probes retain the old exact bounds predicate.
            if (!Range(bounds, out var x0, out var x1, out var y0, out var y1))
            {
                foreach (var entry in _items)
                    if (entry.Bounds.Intersects(bounds)) yield return entry.Item;
                yield break;
            }
            var seen = new HashSet<int>();
            for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
                if (_cells.TryGetValue((x, y), out var cell))
                    foreach (var i in cell)
                        if (seen.Add(i) && _items[i].Bounds.Intersects(bounds)) yield return _items[i].Item;
            foreach (var i in _oversized)
                if (_items[i].Bounds.Intersects(bounds)) yield return _items[i].Item;
        }

        private static bool Range(WorldBounds b, out int x0, out int x1, out int y0, out int y1)
        {
            x0 = x1 = y0 = y1 = 0;
            var left = Math.Floor(b.Left / CellSize); var right = Math.Floor(b.Right / CellSize);
            var top = Math.Floor(b.Top / CellSize); var bottom = Math.Floor(b.Bottom / CellSize);
            if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom)
                || left < int.MinValue || right >= int.MaxValue || top < int.MinValue || bottom >= int.MaxValue
                || right < left || bottom < top || (right - left + 1) * (bottom - top + 1) > 64) return false;
            x0 = (int)left; x1 = (int)right; y0 = (int)top; y1 = (int)bottom;
            return true;
        }
    }

    public const double CellSize = 1024;
    private static readonly ConditionalWeakTable<ObjectLayer, ObjectSpatialIndex> Cache = [];
    private readonly IReadOnlyList<SceneObject> _objects;
    private readonly ImmutableDictionary<(int X, int Y), int[]> _cells;
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
                // Overrides hold only the difference from the basis: an object the basis does
                // not know leaves without a tombstone, and the very instance the basis holds
                // (Undo of a delete or a move) needs no override, so cycles do not accumulate.
                var basisObjects = _stableBasis._objects as SceneObjectList;
                foreach (var i in delta.Removed)
                {
                    var id = delta.Source.Objects[i].Id;
                    if (BasisPosition(basisObjects, id) != -1) overrides[id] = null;
                    else overrides.Remove(id);
                }
                foreach (var i in delta.Added)
                {
                    var item = _objects[i];
                    var at = BasisPosition(basisObjects, item.Id);
                    if (at >= 0 && ReferenceEquals(basisObjects![at], item)) overrides.Remove(item.Id);
                    else overrides[item.Id] = item;
                }
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
            // Indices arrive in increasing order, so every bucket is already sorted and unique.
            // Two passes (count, then fill arrays of the exact size): growing a list per cell and
            // copying it out allocated about three times the index on a million objects.
            var buckets = new Dictionary<(int X, int Y), (int[]? Items, int Count)>();
            var oversized = ImmutableHashSet.CreateBuilder<int>();
            for (var index = 0; index < _objects.Count; index++)
            {
                // A background build pauses here while the user pans or zooms (no-op elsewhere).
                if ((index & 0xFFFF) == 0xFFFF) Resources.BackgroundWork.YieldToInteraction();
                if (!TryCellRange(_objects[index], out var minX, out var maxX, out var minY, out var maxY))
                {
                    oversized.Add(index);
                    continue;
                }
                for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(buckets, (x, y), out _).Count++;
            }
            for (var index = 0; index < _objects.Count; index++)
            {
                if (!TryCellRange(_objects[index], out var minX, out var maxX, out var minY, out var maxY)) continue;
                for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
                {
                    ref var bucket = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(buckets, (x, y));
                    if (bucket.Items is null) { bucket.Items = new int[bucket.Count]; bucket.Count = 0; }
                    bucket.Items[bucket.Count++] = index;
                }
            }
            var cells = ImmutableDictionary.CreateBuilder<(int X, int Y), int[]>();
            foreach (var (key, bucket) in buckets) cells.Add(key, bucket.Items!);
            _cells = cells.ToImmutable();
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

    internal static bool IsPrepared(ObjectLayer layer) =>
        Cache.TryGetValue(layer, out _) || ByObjects.TryGetValue(layer.Objects, out _);

    /// <summary>Whether <see cref="For"/> returns at once (the index of these objects exists), without building it.</summary>
    public static bool IsReady(ObjectLayer layer) => IsPrepared(layer);
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
                if (basis._cells.TryGetValue((x, y), out var bucket) && (count += bucket.Length) >= threshold) return true;
        }
        else
        {
            foreach (var (cell, bucket) in basis._cells)
                if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY && (count += bucket.Length) >= threshold) return true;
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

    // Without identity slots on the basis list (never true after preparation) every id is treated
    // as unknown to the basis: a tombstone is kept and an override is written, as before.
    private static int BasisPosition(SceneObjectList? basis, ObjectId id) =>
        basis is { HasIdentityTable: true } ? basis.IndexOfId(id) : -2;

    private static void Change(ImmutableDictionary<(int X, int Y), int[]>.Builder cells,
        ImmutableHashSet<int>.Builder oversized, SceneObject item, int index, bool add)
    {
        if (!TryCellRange(item, out var minX, out var maxX, out var minY, out var maxY))
        {
            if (add) oversized.Add(index); else oversized.Remove(index);
            return;
        }
        for (var y = minY; y <= maxY; y++) for (var x = minX; x <= maxX; x++)
        {
            var bucket = cells.GetValueOrDefault((x, y)) ?? [];
            bucket = add ? WithIndex(bucket, index) : WithoutIndex(bucket, index);
            if (bucket.Length == 0) cells.Remove((x, y)); else cells[(x, y)] = bucket;
        }
    }

    /// <summary>Set semantics on a sorted array: a copy with the index added, or the same array if present.</summary>
    private static int[] WithIndex(int[] bucket, int index)
    {
        var at = Array.BinarySearch(bucket, index);
        if (at >= 0) return bucket;
        at = ~at;
        var result = new int[bucket.Length + 1];
        Array.Copy(bucket, 0, result, 0, at);
        result[at] = index;
        Array.Copy(bucket, at, result, at + 1, bucket.Length - at);
        return result;
    }

    /// <summary>Set semantics on a sorted array: a copy without the index, or the same array if absent.</summary>
    private static int[] WithoutIndex(int[] bucket, int index)
    {
        var at = Array.BinarySearch(bucket, index);
        if (at < 0) return bucket;
        var result = new int[bucket.Length - 1];
        Array.Copy(bucket, 0, result, 0, at);
        Array.Copy(bucket, at + 1, result, at, bucket.Length - at - 1);
        return result;
    }

    /// <summary>The cells an object covers, or false when it spans more than 64 cells (kept apart).</summary>
    private static bool TryCellRange(SceneObject item, out int minX, out int maxX, out int minY, out int maxY)
    {
        var bounds = SceneGeometry.BoundsOf(item);
        minX = (int)Math.Floor(bounds.Left / CellSize);
        maxX = (int)Math.Floor(bounds.Right / CellSize);
        minY = (int)Math.Floor(bounds.Top / CellSize);
        maxY = (int)Math.Floor(bounds.Bottom / CellSize);
        return ((long)maxX - minX + 1) * ((long)maxY - minY + 1) <= 64;
    }
}
