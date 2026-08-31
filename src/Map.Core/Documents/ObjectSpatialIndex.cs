using System.Runtime.CompilerServices;

namespace Map.Core.Documents;

/// <summary>
/// A uniform grid over one object layer. Derived state, never part of the document: it is keyed by
/// the immutable layer instance, so an edit that produces a new layer snapshot gets a new index
/// while untouched layers keep theirs (docs/05 derived-cache rule).
///
/// It exists so a 65 536-unit map holding tens of thousands of objects renders from the handful
/// that intersect the viewport instead of walking the whole list every frame.
/// </summary>
public sealed class ObjectSpatialIndex
{
    /// <summary>Grid cell edge in world units. 1024 = four tiles.</summary>
    public const double CellSize = 1024.0;

    private static readonly ConditionalWeakTable<ObjectLayer, ObjectSpatialIndex> Cache = [];

    private readonly Dictionary<(int X, int Y), List<int>> _cells = [];
    private readonly List<int> _oversized = [];
    private readonly IReadOnlyList<SceneObject> _objects;
    private readonly ObjectSpatialIndex _root;
    private readonly IReadOnlyDictionary<int, SceneObject> _overrides;

    private ObjectSpatialIndex(ObjectLayer layer)
    {
        _objects = layer.Objects;
        _root = this;
        _overrides = new Dictionary<int, SceneObject>();

        for (var i = 0; i < _objects.Count; i++)
        {
            var bounds = SceneGeometry.BoundsOf(_objects[i]);
            var minX = (int)Math.Floor(bounds.Left / CellSize);
            var maxX = (int)Math.Floor(bounds.Right / CellSize);
            var minY = (int)Math.Floor(bounds.Top / CellSize);
            var maxY = (int)Math.Floor(bounds.Bottom / CellSize);

            // An object spanning a huge number of cells would bloat the grid; keep those apart and
            // always test them. In practice that is the rare full-map backdrop shape.
            if ((long)(maxX - minX + 1) * (maxY - minY + 1) > 64)
            {
                _oversized.Add(i);
                continue;
            }

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    if (!_cells.TryGetValue((x, y), out var bucket))
                    {
                        bucket = [];
                        _cells[(x, y)] = bucket;
                    }

                    bucket.Add(i);
                }
            }
        }
    }

    private ObjectSpatialIndex(
        ObjectSpatialIndex root,
        ObjectLayer layer,
        IReadOnlyDictionary<int, SceneObject> overrides)
    {
        _root = root;
        _objects = layer.Objects;
        _overrides = overrides;
        _cells = root._cells;
        _oversized = root._oversized;
    }

    /// <summary>The index of this layer snapshot, built once and reused while the snapshot lives.</summary>
    public static ObjectSpatialIndex For(ObjectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return Cache.GetValue(layer, static l => new ObjectSpatialIndex(l));
    }

    /// <summary>
    /// Shares an already-warm root grid with a replacement-only layer revision. Old/new bounds
    /// are reconciled at query time for the bounded changed-position overlay.
    /// </summary>
    internal static void RegisterReplacements(
        ObjectLayer source,
        ObjectLayer target,
        IReadOnlyDictionary<int, SceneObject> replacements)
    {
        if (source.Objects.Count != target.Objects.Count
            || !Cache.TryGetValue(source, out var sourceIndex))
        {
            return;
        }

        var combined = new Dictionary<int, SceneObject>(sourceIndex._overrides);
        foreach (var (position, replacement) in replacements)
        {
            combined[position] = replacement;
        }

        // A broad overlay is no longer an incremental edit; let that revision build a fresh root
        // when first queried instead of making every later query walk an unbounded delta.
        if (combined.Count > 1_024)
        {
            return;
        }

        Cache.Add(target, new ObjectSpatialIndex(sourceIndex._root, target, combined));
    }

    public int ObjectCount => _objects.Count;

    public int CellCount => _cells.Count;

    public int OversizedCount => _oversized.Count;

    public int IncrementalOverrideCount => _overrides.Count;

    /// <summary>
    /// Objects whose padded bounds contain the point, in source-list order. A point touches only
    /// one regular cell, so the common path neither scans the layer nor allocates a de-dup set.
    /// Oversized entries are merged by source index to preserve the same stable order as
    /// <see cref="Query"/>.
    /// </summary>
    public IReadOnlyList<SceneObject> QueryPoint(double worldX, double worldY)
    {
        var indices = PointCandidateIndices(worldX, worldY);
        var result = new List<SceneObject>(indices.Count);
        foreach (var index in indices)
        {
            result.Add(_objects[index]);
        }

        return result;
    }

    /// <summary>
    /// Objects whose padded bounds intersect the rectangle, in list order and without duplicates.
    /// The result is a candidate set: the caller still does exact work per object.
    /// </summary>
    public IReadOnlyList<SceneObject> Query(WorldBounds view)
    {
        var indices = QueryCandidateIndices(view);
        var result = new List<SceneObject>(indices.Count);
        foreach (var index in indices)
        {
            result.Add(_objects[index]);
        }

        return result;
    }

    private IReadOnlyList<int> QueryCandidateIndices(WorldBounds view)
    {
        var indices = _root.BaseQueryCandidateIndices(view);
        if (_overrides.Count == 0)
        {
            return indices;
        }

        var hits = indices.ToHashSet();
        foreach (var (index, replacement) in _overrides)
        {
            hits.Remove(index);
            if (SceneGeometry.BoundsOf(replacement).Intersects(view))
            {
                hits.Add(index);
            }
        }

        return [.. hits.Order()];
    }

    private IReadOnlyList<int> BaseQueryCandidateIndices(WorldBounds view)
    {
        var minX = (int)Math.Floor(view.Left / CellSize);
        var maxX = (int)Math.Floor(view.Right / CellSize);
        var minY = (int)Math.Floor(view.Top / CellSize);
        var maxY = (int)Math.Floor(view.Bottom / CellSize);

        var hits = new HashSet<int>(_oversized);
        var cellsInView = ((long)(maxX - minX) + 1) * ((long)(maxY - minY) + 1);

        if (cellsInView <= _cells.Count)
        {
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    if (_cells.TryGetValue((x, y), out var bucket))
                    {
                        hits.UnionWith(bucket);
                    }
                }
            }
        }
        else
        {
            // Zoomed out past the populated area: walking the populated cells is cheaper.
            foreach (var (cell, bucket) in _cells)
            {
                if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY)
                {
                    hits.UnionWith(bucket);
                }
            }
        }

        var result = new List<int>(hits.Count);
        foreach (var i in hits.Order())
        {
            if (SceneGeometry.BoundsOf(_objects[i]).Intersects(view))
            {
                result.Add(i);
            }
        }

        return result;
    }

    private IReadOnlyList<int> PointCandidateIndices(double worldX, double worldY)
    {
        var indices = _root.BasePointCandidateIndices(worldX, worldY);
        if (_overrides.Count == 0)
        {
            return indices;
        }

        var hits = indices.ToHashSet();
        foreach (var (index, replacement) in _overrides)
        {
            hits.Remove(index);
            var bounds = SceneGeometry.BoundsOf(replacement);
            if (worldX >= bounds.Left && worldX <= bounds.Right
                && worldY >= bounds.Top && worldY <= bounds.Bottom)
            {
                hits.Add(index);
            }
        }

        return [.. hits.Order()];
    }

    private IReadOnlyList<int> BasePointCandidateIndices(double worldX, double worldY)
    {
        var cell = (
            (int)Math.Floor(worldX / CellSize),
            (int)Math.Floor(worldY / CellSize));
        _cells.TryGetValue(cell, out var bucket);
        if (_oversized.Count == 0)
        {
            return PointMatches(bucket, worldX, worldY);
        }
        if (bucket is null || bucket.Count == 0)
        {
            return PointMatches(_oversized, worldX, worldY);
        }

        var merged = new List<int>(bucket.Count + _oversized.Count);
        var regularIndex = 0;
        var oversizedIndex = 0;
        while (regularIndex < bucket.Count || oversizedIndex < _oversized.Count)
        {
            if (oversizedIndex >= _oversized.Count
                || regularIndex < bucket.Count && bucket[regularIndex] < _oversized[oversizedIndex])
            {
                merged.Add(bucket[regularIndex++]);
            }
            else
            {
                merged.Add(_oversized[oversizedIndex++]);
            }
        }

        return PointMatches(merged, worldX, worldY);
    }

    private IReadOnlyList<int> PointMatches(
        IReadOnlyList<int>? indices, double worldX, double worldY)
    {
        if (indices is null || indices.Count == 0)
        {
            return Array.Empty<int>();
        }

        var result = new List<int>(indices.Count);
        foreach (var sourceIndex in indices)
        {
            var bounds = SceneGeometry.BoundsOf(_objects[sourceIndex]);
            if (worldX >= bounds.Left && worldX <= bounds.Right
                && worldY >= bounds.Top && worldY <= bounds.Bottom)
            {
                result.Add(sourceIndex);
            }
        }

        return result;
    }
}
