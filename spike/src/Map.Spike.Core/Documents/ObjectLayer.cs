using Map.Spike.Core.Geometry;
using Map.Spike.Core.Spatial;

namespace Map.Spike.Core.Documents;

/// <summary>
/// Ordered object layer with a spatial broad phase. List order is z-order; later = drawn on top.
/// </summary>
public sealed class ObjectLayer(LayerId id, string name)
{
    private readonly List<SpikeObject> _objects = [];
    private UniformGridIndex? _index;

    public LayerId Id { get; } = id;
    public string Name { get; } = name;
    public bool Visible { get; set; } = true;

    public IReadOnlyList<SpikeObject> Objects => _objects;
    public int Count => _objects.Count;

    /// <summary>Null until <see cref="RebuildIndex"/> runs. Exposed so tests can assert it is used.</summary>
    public UniformGridIndex? Index => _index;

    public void Add(SpikeObject o) { _objects.Add(o); _index = null; }

    public void AddRange(IEnumerable<SpikeObject> items) { _objects.AddRange(items); _index = null; }

    public Rect2 ContentBounds()
    {
        var bounds = Rect2.Empty;
        foreach (var o in _objects) bounds = bounds.Union(o.Bounds);
        return bounds;
    }

    public void RebuildIndex(double? cellSize = null)
    {
        var size = cellSize ?? SuggestCellSize();
        var index = new UniformGridIndex(size);
        for (var i = 0; i < _objects.Count; i++) index.Insert(i, _objects[i].Bounds);
        _index = index;
    }

    public UniformGridIndex EnsureIndex()
    {
        if (_index is null) RebuildIndex();
        return _index!;
    }

    /// <summary>
    /// Target roughly a handful of objects per cell: too small explodes the entry count for
    /// large objects, too large degenerates back into a full scan.
    /// </summary>
    private double SuggestCellSize()
    {
        if (_objects.Count == 0) return 256;

        double sum = 0;
        foreach (var o in _objects) sum += o.Bounds.Width;
        var meanExtent = sum / _objects.Count;
        return Math.Max(16, meanExtent * 4);
    }

    /// <summary>Visible objects in <paramref name="area"/>, in draw order.</summary>
    public List<SpikeObject> QueryVisible(Rect2 area, ref SpatialQueryStats stats)
    {
        stats.SceneObjectCount = _objects.Count;
        var index = EnsureIndex();
        var candidates = index.Query(area, ref stats);

        var result = new List<SpikeObject>(candidates.Count);
        foreach (var i in candidates)
        {
            var o = _objects[i];
            stats.PreciseTests++;
            if (o.Bounds.Intersects(area)) result.Add(o);
        }

        return result;
    }

    /// <summary>Topmost object containing <paramref name="world"/>, or null.</summary>
    public SpikeObject? HitTest(Vec2 world, ref SpatialQueryStats stats)
    {
        stats.SceneObjectCount = _objects.Count;
        var index = EnsureIndex();
        var candidates = index.QueryPoint(world, ref stats);

        SpikeObject? best = null;
        foreach (var i in candidates)
        {
            var o = _objects[i];
            if (!o.Bounds.Contains(world)) continue;
            stats.PreciseTests++;
            if (o.ContainsPoint(world)) best = o; // ascending indices => last match is topmost
        }

        return best;
    }
}
