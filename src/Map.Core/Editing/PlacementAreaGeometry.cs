using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Scan-convert the polygon (or its symmetric difference) into conservative cell spans.</summary>
public static class PlacementAreaGeometry
{
    public const double CellSize = 100;
    public readonly record struct CellSpan(int Y, int MinX, int MaxX);
    private readonly record struct Edge(VectorPoint A, VectorPoint B);
    private const double Epsilon = 1e-7;

    public static void Validate(IReadOnlyList<PlacementPathNode> nodes)
    {
        if (nodes.Count < 3 || nodes.Any(n => Math.Abs(n.X) > CanvasDimensionPolicy.Maximum || Math.Abs(n.Y) > CanvasDimensionPolicy.Maximum))
            throw new ArgumentException("Для области нужны хотя бы три вершины в пределах карты.");
        var edges = Edges(nodes).ToArray();
        var area = 0d;
        for (var i = 0; i < edges.Length; i++)
        {
            var edge = edges[i];
            if (DistanceSquared(edge.A, edge.B) < Epsilon)
                throw new ArgumentException("Соседние вершины области должны различаться.");
            area += edge.A.X * edge.B.Y - edge.B.X * edge.A.Y;
            for (var j = i + 1; j < edges.Length; j++)
            {
                if (j == i + 1 || i == 0 && j == edges.Length - 1)
                {
                    var a = j == i + 1 ? edge.A : edge.B;
                    var b = j == i + 1 ? edges[j].B : edges[j].A;
                    var joint = j == i + 1 ? edge.B : edge.A;
                    if (Math.Abs(Cross(a, joint, b)) < Epsilon
                        && (a.X - joint.X) * (b.X - joint.X) + (a.Y - joint.Y) * (b.Y - joint.Y) > 0)
                        throw new ArgumentException("Рёбра области не должны накладываться друг на друга.");
                    continue;
                }
                if (Intersects(edge, edges[j])) throw new ArgumentException("Граница пересекает себя. Переместите или удалите вершину.");
            }
        }
        if (Math.Abs(area) < Epsilon) throw new ArgumentException("Область должна иметь ненулевую площадь.");
    }

    public static bool Contains(IReadOnlyList<PlacementPathNode> nodes, double x, double y)
    {
        var inside = false; var point = new VectorPoint(x, y);
        for (var i = 0; i < nodes.Count; i++)
        {
            var a = nodes[i]; var b = nodes[(i + 1) % nodes.Count];
            if (OnSegment(new(a.X, a.Y), new(b.X, b.Y), point)) return true;
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    public static IReadOnlyList<CellSpan> Coverage(IReadOnlyList<PlacementPathNode> nodes) => Rasterize(Edges(nodes).ToArray());

    public static IReadOnlyList<CellSpan> ChangedCoverage(IReadOnlyList<PlacementPathNode> before, IReadOnlyList<PlacementPathNode> after)
    {
        // Equal undirected edges cancel in even-odd fill. Moving a corner leaves a narrow
        // closed strip, not the union of the old/new polygon bounding boxes.
        var edges = new HashSet<Edge>();
        foreach (var edge in Edges(before).Concat(Edges(after)))
        {
            var canonical = edge.A.X < edge.B.X || edge.A.X == edge.B.X && edge.A.Y <= edge.B.Y
                ? edge : new Edge(edge.B, edge.A);
            if (!edges.Add(canonical)) edges.Remove(canonical);
        }
        return Rasterize(edges.ToArray());
    }

    /// <summary>The spans grown by <paramref name="cells"/> cells in every direction (R10 soft edge).</summary>
    public static IReadOnlyList<CellSpan> Dilate(IReadOnlyList<CellSpan> spans, int cells)
    {
        if (cells <= 0 || spans.Count == 0) return spans;
        var grown = new List<CellSpan>(spans.Count * (2 * cells + 1));
        foreach (var span in spans)
            for (var dy = -cells; dy <= cells; dy++)
                grown.Add(new(span.Y + dy, span.MinX - cells, span.MaxX + cells));
        return Merge(grown);
    }

    /// <summary>The parts of the spans whose cells touch the world rectangle.</summary>
    public static IReadOnlyList<CellSpan> Clip(IReadOnlyList<CellSpan> spans, WorldBounds bounds)
    {
        var left = Cell(bounds.Left); var right = Cell(bounds.Right);
        var top = Cell(bounds.Top); var bottom = Cell(bounds.Bottom);
        var result = new List<CellSpan>();
        foreach (var span in spans)
        {
            if (span.Y < top || span.Y > bottom) continue;
            var min = Math.Max(span.MinX, left); var max = Math.Min(span.MaxX, right);
            if (min <= max) result.Add(new(span.Y, min, max));
        }
        return result;
    }

    public static IReadOnlyList<CellSpan> Merge(IEnumerable<CellSpan> spans)
    {
        var result = new List<CellSpan>();
        foreach (var row in spans.GroupBy(s => s.Y).OrderBy(g => g.Key))
            foreach (var span in row.OrderBy(s => s.MinX))
            {
                if (result.Count > 0 && result[^1].Y == span.Y && span.MinX <= result[^1].MaxX + 1)
                    result[^1] = result[^1] with { MaxX = Math.Max(result[^1].MaxX, span.MaxX) };
                else result.Add(span);
            }
        return result;
    }

    private static IReadOnlyList<CellSpan> Rasterize(Edge[] edges)
    {
        if (edges.Length == 0) return [];
        var events = edges.SelectMany(e => new[] { e.A.Y, e.B.Y }).ToList();
        for (var i = 0; i < edges.Length; i++)
            for (var j = i + 1; j < edges.Length; j++)
                if (IntersectionY(edges[i], edges[j]) is { } y) events.Add(y);
        var low = (int)Math.Floor(events.Min() / CellSize);
        var high = (int)Math.Floor(events.Max() / CellSize);
        var rows = new List<CellSpan>();
        for (var row = low; row <= high; row++)
        {
            var bottom = row * CellSize; var top = bottom + CellSize;
            var cuts = events.Where(y => y > bottom && y < top).Append(bottom).Append(top).Distinct().Order().ToArray();
            for (var c = 0; c < cuts.Length - 1; c++)
            {
                var lo = cuts[c]; var hi = cuts[c + 1]; var middle = (lo + hi) / 2;
                var active = edges.Where(e => middle > Math.Min(e.A.Y, e.B.Y) && middle < Math.Max(e.A.Y, e.B.Y))
                    .OrderBy(e => XAt(e, middle)).ToArray();
                for (var i = 0; i + 1 < active.Length; i += 2)
                {
                    var min = Math.Min(XAt(active[i], lo), XAt(active[i], hi));
                    var max = Math.Max(XAt(active[i + 1], lo), XAt(active[i + 1], hi));
                    if (max - min > Epsilon) rows.Add(new(row, Cell(min), Cell(max)));
                }
            }
            // Include horizontal boundaries and vertices, including centres exactly on an edge.
            foreach (var horizontal in edges.Where(e => e.A.Y == e.B.Y && e.A.Y >= bottom && e.A.Y < top).GroupBy(e => e.A.Y))
            {
                var ends = horizontal.SelectMany(e => new[] { e.A.X, e.B.X }).Order().ToArray();
                for (var i = 0; i + 1 < ends.Length; i += 2)
                    if (ends[i + 1] - ends[i] > Epsilon) rows.Add(new(row, Cell(ends[i]), Cell(ends[i + 1])));
            }
        }
        return Merge(rows);
    }
    public static int Cell(double value) => (int)Math.Floor(value / CellSize);
    private static double XAt(Edge e, double y) => e.A.X + (y - e.A.Y) * (e.B.X - e.A.X) / (e.B.Y - e.A.Y);
    private static IEnumerable<Edge> Edges(IReadOnlyList<PlacementPathNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        { var a = nodes[i]; var b = nodes[(i + 1) % nodes.Count]; yield return new(new(a.X, a.Y), new(b.X, b.Y)); }
    }
    private static double Cross(VectorPoint a, VectorPoint b, VectorPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static double DistanceSquared(VectorPoint a, VectorPoint b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
    private static bool OnSegment(VectorPoint a, VectorPoint b, VectorPoint p) => Math.Abs(Cross(a, b, p)) <= Epsilon
        && p.X >= Math.Min(a.X, b.X) - Epsilon && p.X <= Math.Max(a.X, b.X) + Epsilon
        && p.Y >= Math.Min(a.Y, b.Y) - Epsilon && p.Y <= Math.Max(a.Y, b.Y) + Epsilon;
    private static bool Intersects(Edge a, Edge b) => OnSegment(a.A, a.B, b.A) || OnSegment(a.A, a.B, b.B)
        || OnSegment(b.A, b.B, a.A) || OnSegment(b.A, b.B, a.B)
        || Cross(a.A, a.B, b.A) * Cross(a.A, a.B, b.B) < 0 && Cross(b.A, b.B, a.A) * Cross(b.A, b.B, a.B) < 0;
    private static double? IntersectionY(Edge a, Edge b)
    {
        var dx = a.B.X - a.A.X; var dy = a.B.Y - a.A.Y;
        var ex = b.B.X - b.A.X; var ey = b.B.Y - b.A.Y;
        var denominator = dx * ey - dy * ex;
        if (Math.Abs(denominator) < Epsilon) return null;
        var t = ((b.A.X - a.A.X) * ey - (b.A.Y - a.A.Y) * ex) / denominator;
        var u = ((b.A.X - a.A.X) * dy - (b.A.Y - a.A.Y) * dx) / denominator;
        return t > 0 && t < 1 && u > 0 && u < 1 ? a.A.Y + t * dy : null;
    }
}
