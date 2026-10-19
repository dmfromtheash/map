using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class RobustPathOpsTests
{
    private static SKPath Rect(float x, float y, float size)
    {
        var path = new SKPath();
        path.AddRect(new SKRect(x, y, x + size, y + size));
        return path;
    }

    [Fact]
    public void Rings_flatten_lines_and_curves_into_closed_polygons()
    {
        using var path = new SKPath();
        path.AddRect(new SKRect(0, 0, 10, 10));
        path.AddCircle(30, 30, 5);
        var rings = RobustPathOps.Rings(path);
        Assert.Equal(2, rings.Count);
        Assert.Equal(4, rings[0].Count);
        Assert.True(rings[1].Count >= 16, "a circle of conics is sampled into many points");
        Assert.All(rings[1], p => Assert.InRange(Math.Sqrt((p.X - 30) * (p.X - 30) + (p.Y - 30) * (p.Y - 30)), 4.9, 5.1));
    }

    [Fact]
    public void TryUnion_resolves_overlapping_rectangles_into_one_winding_outline()
    {
        using var a = Rect(0, 0, 2);
        using var b = Rect(1, 1, 2);
        using var result = new SKPath();
        Assert.True(RobustPathOps.TryUnion(a, b, result));
        Assert.Equal(SKPathFillType.Winding, result.FillType);
        Assert.Equal(new SKRect(0, 0, 3, 3), result.Bounds);
        Assert.True(result.Contains(1.5f, 1.5f));
        Assert.True(result.Contains(0.5f, 0.5f));
        Assert.True(result.Contains(2.5f, 2.5f));
        Assert.False(result.Contains(2.5f, 0.5f));
    }

    [Fact]
    public void TryDifference_leaves_a_hole_that_the_path_no_longer_contains()
    {
        using var subject = Rect(0, 0, 4);
        using var clip = Rect(1, 1, 2);
        using var result = new SKPath();
        Assert.True(RobustPathOps.TryDifference(subject, clip, result));
        Assert.True(result.Contains(0.5f, 0.5f));
        Assert.False(result.Contains(2f, 2f));
    }

    [Fact]
    public void TryUnion_of_a_bow_tie_and_nothing_keeps_both_lobes()
    {
        using var bowTie = new SKPath();
        bowTie.MoveTo(0, 0); bowTie.LineTo(2, 2); bowTie.LineTo(2, 0); bowTie.LineTo(0, 2); bowTie.Close();
        using var empty = new SKPath();
        using var result = new SKPath();
        Assert.True(RobustPathOps.TryUnion(bowTie, empty, result));
        Assert.True(result.Contains(0.5f, 1f));
        Assert.True(result.Contains(1.5f, 1f));
        Assert.False(result.Contains(1f, 0.25f));
    }
}
