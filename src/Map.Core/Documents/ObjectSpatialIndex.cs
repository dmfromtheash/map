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

    private ObjectSpatialIndex(ObjectLayer layer)
    {
        _objects = layer.Objects;

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

    /// <summary>The index of this layer snapshot, built once and reused while the snapshot lives.</summary>
    public static ObjectSpatialIndex For(ObjectLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return Cache.GetValue(layer, static l => new ObjectSpatialIndex(l));
    }

    public int ObjectCount => _objects.Count;

    public int CellCount => _cells.Count;

    public int OversizedCount => _oversized.Count;

    /// <summary>
    /// Objects whose padded bounds intersect the rectangle, in list order and without duplicates.
    /// The result is a candidate set: the caller still does exact work per object.
    /// </summary>
    public IReadOnlyList<SceneObject> Query(WorldBounds view)
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

        var result = new List<SceneObject>(hits.Count);
        foreach (var i in hits.Order())
        {
            if (SceneGeometry.BoundsOf(_objects[i]).Intersects(view))
            {
                result.Add(_objects[i]);
            }
        }

        return result;
    }
}
