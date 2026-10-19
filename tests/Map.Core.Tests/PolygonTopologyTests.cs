using Map.Core.Documents;
using Map.Core.Geometry;

namespace Map.Core.Tests;

public sealed class PolygonTopologyTests
{
    private static VectorPoint[] Square(double x, double y, double size) =>
        [new(x, y), new(x + size, y), new(x + size, y + size), new(x, y + size)];

    private static double RingArea(IReadOnlyList<VectorPoint> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return Math.Abs(sum) / 2;
    }

    [Fact]
    public void Union_of_two_overlapping_squares_is_one_polygon_with_the_overlap_counted_once()
    {
        var result = PolygonTopology.Union([Square(0, 0, 2), Square(1, 1, 2)]);
        var polygon = Assert.Single(result);
        Assert.Empty(polygon.Holes);
        Assert.Equal(7, RingArea(polygon.Shell), 6);
        Assert.Equal(7, PolygonTopology.Area([Square(0, 0, 2), Square(1, 1, 2)]), 6);
    }

    [Fact]
    public void A_self_intersecting_bow_tie_is_repaired_into_two_triangles()
    {
        VectorPoint[] bowTie = [new(0, 0), new(2, 2), new(2, 0), new(0, 2)];
        var result = PolygonTopology.Union([bowTie]);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.Sum(p => RingArea(p.Shell)), 6);
    }

    [Fact]
    public void Difference_cuts_a_hole_and_keeps_the_shell()
    {
        var result = PolygonTopology.Difference([Square(0, 0, 4)], [Square(1, 1, 2)]);
        var polygon = Assert.Single(result);
        Assert.Equal(16, RingArea(polygon.Shell), 6);
        var hole = Assert.Single(polygon.Holes);
        Assert.Equal(4, RingArea(hole), 6);
    }

    [Fact]
    public void Degenerate_rings_are_ignored_rather_than_thrown()
    {
        VectorPoint[] line = [new(0, 0), new(1, 1), new(2, 2)];
        VectorPoint[] notFinite = [new(0, 0), new(double.NaN, 1), new(1, 1)];
        Assert.Empty(PolygonTopology.Union([line, notFinite, []]));
        Assert.Empty(PolygonTopology.Difference([line], [Square(0, 0, 1)]));
    }
}
