using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Spatial;

/// <summary>
/// Uniform-grid broad phase over object bounds. Chosen over a tree because the spike scene is
/// evenly distributed; the interface is what matters for Stage 2, not this particular structure.
/// Values stored are indices into the owning layer's object list, so z-order is preserved.
/// </summary>
public sealed class UniformGridIndex
{
    private readonly Dictionary<(int X, int Y), List<int>> _cells = [];

    public UniformGridIndex(double cellSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        CellSize = cellSize;
    }

    public double CellSize { get; }
    public int CellCount => _cells.Count;
    public int EntryCount { get; private set; }

    public void Clear()
    {
        _cells.Clear();
        EntryCount = 0;
    }

    public void Insert(int index, Rect2 bounds)
    {
        foreach (var cell in CellsCovering(bounds))
        {
            if (!_cells.TryGetValue(cell, out var list))
            {
                list = new List<int>(4);
                _cells[cell] = list;
            }

            list.Add(index);
            EntryCount++;
        }
    }

    /// <summary>Distinct candidate indices whose cell overlaps <paramref name="area"/>, ascending.</summary>
    public List<int> Query(Rect2 area, ref SpatialQueryStats stats)
    {
        var seen = new HashSet<int>();
        var result = new List<int>();

        foreach (var cell in CellsCovering(area))
        {
            stats.CellsVisited++;
            if (!_cells.TryGetValue(cell, out var list)) continue;

            foreach (var index in list)
            {
                stats.CandidatesExamined++;
                if (seen.Add(index)) result.Add(index);
            }
        }

        result.Sort();
        return result;
    }

    /// <summary>Candidates in the single cell containing <paramref name="point"/>, plus neighbours
    /// reached because an object registered in a neighbouring cell can still overlap this point.</summary>
    public List<int> QueryPoint(Vec2 point, ref SpatialQueryStats stats) =>
        Query(new Rect2(point.X, point.Y, 0.0001, 0.0001), ref stats);

    private IEnumerable<(int X, int Y)> CellsCovering(Rect2 bounds)
    {
        var minX = (int)Math.Floor(bounds.X / CellSize);
        var minY = (int)Math.Floor(bounds.Y / CellSize);
        var maxX = (int)Math.Floor((bounds.X + Math.Max(bounds.Width, 0)) / CellSize);
        var maxY = (int)Math.Floor((bounds.Y + Math.Max(bounds.Height, 0)) / CellSize);

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                yield return (x, y);
            }
        }
    }
}
