using Map.Core.Documents;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;

namespace Map.Core.Geometry;

/// <summary>One simple polygon as rings: the outer shell and its holes, without a repeated closing point.</summary>
public sealed record PolygonRings(IReadOnlyList<VectorPoint> Shell, IReadOnlyList<IReadOnlyList<VectorPoint>> Holes);

/// <summary>
/// Robust set operations on polygons (DEC-051) through NetTopologySuite. Every input ring is
/// repaired first (self-intersections split, degenerate parts dropped), so a union of outlines
/// that Skia's path ops refuse still resolves to clean, non-overlapping polygons instead of two
/// outlines glued into one path. Pure managed code: Core keeps no Skia or UI dependency.
/// </summary>
public static class PolygonTopology
{
    private static readonly GeometryFactory Factory =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory();

    /// <summary>The union of all rings as non-overlapping polygons; empty when nothing has area.</summary>
    public static IReadOnlyList<PolygonRings> Union(IEnumerable<IReadOnlyList<VectorPoint>> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var polygons = ToPolygons(rings);
        return polygons.Count == 0 ? [] : ToRings(UnionOf(polygons));
    }

    /// <summary>The subject rings minus the clip rings, as non-overlapping polygons.</summary>
    public static IReadOnlyList<PolygonRings> Difference(
        IEnumerable<IReadOnlyList<VectorPoint>> subject, IEnumerable<IReadOnlyList<VectorPoint>> clip)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(clip);
        var left = ToPolygons(subject);
        if (left.Count == 0) return [];
        var right = ToPolygons(clip);
        var a = UnionOf(left);
        if (right.Count == 0) return ToRings(a);
        var b = UnionOf(right);
        return ToRings(OverlayNGRobust.Overlay(a, b, SpatialFunction.Difference));
    }

    /// <summary>The area covered by the rings after repair and union (overlaps counted once).</summary>
    public static double Area(IEnumerable<IReadOnlyList<VectorPoint>> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);
        var polygons = ToPolygons(rings);
        return polygons.Count == 0 ? 0 : UnionOf(polygons).Area;
    }

    private static NetTopologySuite.Geometries.Geometry UnionOf(List<Polygon> polygons) =>
        polygons.Count == 1 ? polygons[0] : OverlayNGRobust.Union(polygons.Cast<NetTopologySuite.Geometries.Geometry>());

    private static List<Polygon> ToPolygons(IEnumerable<IReadOnlyList<VectorPoint>> rings)
    {
        var polygons = new List<Polygon>();
        foreach (var ring in rings)
        {
            if (ring is null || ring.Count < 3) continue;
            var coordinates = new Coordinate[ring.Count + 1];
            var finite = true;
            for (var i = 0; i < ring.Count; i++)
            {
                var point = ring[i];
                if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) { finite = false; break; }
                coordinates[i] = new Coordinate(point.X, point.Y);
            }

            if (!finite) continue;
            coordinates[ring.Count] = new Coordinate(ring[0].X, ring[0].Y);
            NetTopologySuite.Geometries.Geometry fixedPolygon;
            try
            {
                fixedPolygon = GeometryFixer.Fix(Factory.CreatePolygon(Factory.CreateLinearRing(coordinates)));
            }
            catch (ArgumentException)
            {
                continue;
            }

            Collect(fixedPolygon, polygons);
        }

        return polygons;
    }

    private static void Collect(NetTopologySuite.Geometries.Geometry geometry, List<Polygon> into)
    {
        for (var i = 0; i < geometry.NumGeometries; i++)
        {
            var part = geometry.GetGeometryN(i);
            if (part is Polygon polygon)
            {
                if (!polygon.IsEmpty && polygon.Area > 0) into.Add(polygon);
            }
            else if (part.NumGeometries > 1 || part is GeometryCollection)
            {
                Collect(part, into);
            }
        }
    }

    private static List<PolygonRings> ToRings(NetTopologySuite.Geometries.Geometry geometry)
    {
        var polygons = new List<Polygon>();
        Collect(geometry, polygons);
        var result = new List<PolygonRings>(polygons.Count);
        foreach (var polygon in polygons)
        {
            var shell = Points(polygon.ExteriorRing);
            if (shell.Count < 3) continue;
            var holes = new List<IReadOnlyList<VectorPoint>>(polygon.NumInteriorRings);
            for (var i = 0; i < polygon.NumInteriorRings; i++)
            {
                var hole = Points(polygon.GetInteriorRingN(i));
                if (hole.Count >= 3) holes.Add(hole);
            }

            result.Add(new PolygonRings(shell, holes));
        }

        return result;
    }

    private static List<VectorPoint> Points(LineString ring)
    {
        var coordinates = ring.Coordinates;
        var count = coordinates.Length;
        if (count > 1 && coordinates[0].Equals2D(coordinates[count - 1])) count--;
        var points = new List<VectorPoint>(count);
        for (var i = 0; i < count; i++) points.Add(new VectorPoint(coordinates[i].X, coordinates[i].Y));
        return points;
    }
}
