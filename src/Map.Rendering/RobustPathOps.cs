using System;
using System.Collections.Generic;
using System.Threading;
using Map.Core.Documents;
using Map.Core.Geometry;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The fallback behind <see cref="SKPath.Op"/> (DEC-051): when Skia refuses a union or a
/// difference (degenerate or self-intersecting operand), the same paths are resolved through
/// <see cref="PolygonTopology"/> and rebuilt as one even-odd path. Curves are flattened on the
/// way in; the callers only hand over polygonal outlines, so nothing visible is lost.
/// </summary>
public static class RobustPathOps
{
    private const int CurveSteps = 8;
    // Diagnostics: MAP_ROBUST_PATHOPS=0 turns the fallback off to compare with plain Skia behaviour.
    private static readonly bool Disabled = Environment.GetEnvironmentVariable("MAP_ROBUST_PATHOPS") == "0";
    private static long _fallbacks;

    /// <summary>How many times Skia's path op failed and the topology fallback ran (diagnostics).</summary>
    public static long Fallbacks => Interlocked.Read(ref _fallbacks);

    public static bool TryUnion(SKPath a, SKPath b, SKPath result)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(result);
        if (Disabled) return false;
        var rings = Rings(a);
        var orientation = Orientation(rings);
        rings.AddRange(Rings(b));
        return Build(PolygonTopology.Union(rings), result, orientation);
    }

    public static bool TryDifference(SKPath subject, SKPath clip, SKPath result)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(result);
        if (Disabled) return false;
        var rings = Rings(subject);
        return Build(PolygonTopology.Difference(rings, Rings(clip)), result, Orientation(rings));
    }

    /// <summary>
    /// The result keeps the winding of the first operand: shells turn the same way as the
    /// caller's own outlines and holes the opposite way, so the caller may go on adding its
    /// outlines to a nonzero-winding path (as the river and road bodies do) without the new
    /// contours cancelling against the old ones.
    /// </summary>
    private static bool Build(IReadOnlyList<PolygonRings> polygons, SKPath result, int orientation)
    {
        Interlocked.Increment(ref _fallbacks);
        result.Reset();
        result.FillType = SKPathFillType.Winding;
        foreach (var polygon in polygons)
        {
            Add(result, polygon.Shell, orientation);
            foreach (var hole in polygon.Holes) Add(result, hole, -orientation);
        }

        return !result.IsEmpty;
    }

    /// <summary>+1 or -1: the sign of the signed area of the first ring that has any area.</summary>
    private static int Orientation(List<IReadOnlyList<VectorPoint>> rings)
    {
        foreach (var ring in rings)
        {
            var area = SignedArea(ring);
            if (Math.Abs(area) > 1e-9) return area > 0 ? 1 : -1;
        }

        return 1;
    }

    private static double SignedArea(IReadOnlyList<VectorPoint> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return sum / 2;
    }

    private static void Add(SKPath path, IReadOnlyList<VectorPoint> ring, int orientation)
    {
        if (ring.Count < 3) return;
        var forward = SignedArea(ring) * orientation >= 0;
        var first = forward ? ring[0] : ring[^1];
        path.MoveTo((float)first.X, (float)first.Y);
        if (forward)
        {
            for (var i = 1; i < ring.Count; i++) path.LineTo((float)ring[i].X, (float)ring[i].Y);
        }
        else
        {
            for (var i = ring.Count - 2; i >= 0; i--) path.LineTo((float)ring[i].X, (float)ring[i].Y);
        }

        path.Close();
    }

    /// <summary>Every closed contour of the path as a polygon; curves are sampled in a few steps.</summary>
    public static List<IReadOnlyList<VectorPoint>> Rings(SKPath path)
    {
        var rings = new List<IReadOnlyList<VectorPoint>>();
        var current = new List<VectorPoint>();
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Flush();
                    current.Add(new VectorPoint(points[0].X, points[0].Y));
                    break;
                case SKPathVerb.Line:
                    current.Add(new VectorPoint(points[1].X, points[1].Y));
                    break;
                case SKPathVerb.Quad:
                    for (var s = 1; s <= CurveSteps; s++)
                    {
                        var t = s / (double)CurveSteps; var u = 1 - t;
                        current.Add(new VectorPoint(
                            u * u * points[0].X + 2 * u * t * points[1].X + t * t * points[2].X,
                            u * u * points[0].Y + 2 * u * t * points[1].Y + t * t * points[2].Y));
                    }

                    break;
                case SKPathVerb.Conic:
                    var w = iterator.ConicWeight();
                    for (var s = 1; s <= CurveSteps; s++)
                    {
                        var t = s / (double)CurveSteps; var u = 1 - t;
                        var denominator = u * u + 2 * w * u * t + t * t;
                        current.Add(new VectorPoint(
                            (u * u * points[0].X + 2 * w * u * t * points[1].X + t * t * points[2].X) / denominator,
                            (u * u * points[0].Y + 2 * w * u * t * points[1].Y + t * t * points[2].Y) / denominator));
                    }

                    break;
                case SKPathVerb.Cubic:
                    for (var s = 1; s <= CurveSteps; s++)
                    {
                        var t = s / (double)CurveSteps; var u = 1 - t;
                        current.Add(new VectorPoint(
                            u * u * u * points[0].X + 3 * u * u * t * points[1].X + 3 * u * t * t * points[2].X + t * t * t * points[3].X,
                            u * u * u * points[0].Y + 3 * u * u * t * points[1].Y + 3 * u * t * t * points[2].Y + t * t * t * points[3].Y));
                    }

                    break;
                case SKPathVerb.Close:
                    Flush();
                    break;
            }
        }

        Flush();
        return rings;

        void Flush()
        {
            if (current.Count > 1 && current[0] == current[^1]) current.RemoveAt(current.Count - 1);
            if (current.Count >= 3) rings.Add(current.ToArray());
            current = [];
        }
    }
}
