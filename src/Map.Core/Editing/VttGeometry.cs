using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>One door or opening for a virtual tabletop: the two ends of the gap cut into a wall.</summary>
public sealed record VttPortal(VectorPoint Start, VectorPoint End)
{
    public VectorPoint Centre => new((Start.X + End.X) / 2, (Start.Y + End.Y) / 2);
}

/// <summary>Light-blocking walls of a map in world units: polylines already split at the openings.</summary>
public sealed record VttWalls(IReadOnlyList<IReadOnlyList<VectorPoint>> Walls, IReadOnlyList<VttPortal> Portals);

/// <summary>
/// Walls and openings for the Universal VTT (*.dd2vtt) export. A visible line or shape outline
/// whose stroke is marked «стена блокирует свет», and a visible road whose look is, becomes a
/// wall; a portal of such a line leaves a gap in the wall and is reported as an opening. The
/// same flattening as the renderer is used, so the walls lie under the drawn strokes.
/// </summary>
public static class VttGeometry
{
    public static VttWalls Collect(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var walls = new List<IReadOnlyList<VectorPoint>>();
        var portals = new List<VttPortal>();
        // Hex maps: border lines drawn along hex edges (walls, cliffs, frontiers) block sight.
        foreach (var hexLayer in document.Layers.OfType<HexLayer>())
        {
            if (!hexLayer.Visible) continue;
            foreach (var path in hexLayer.Paths)
            {
                if (path.Kind != HexPathKind.Border || path.Style != HexRouteStyle.Edges || path.Vertices.Length < 2) continue;
                walls.Add([.. path.Vertices.Select(vertex =>
                {
                    var (x, y) = hexLayer.Board.Layout.Position(vertex);
                    return new VectorPoint(x, y);
                })]);
            }
        }

        foreach (var layer in document.Layers.OfType<ObjectLayer>())
        {
            if (!layer.Visible) continue;
            foreach (var item in layer.Objects)
            {
                if (!item.Visible) continue;
                if (item.Line is { Stroke.BlocksLight: true } line)
                {
                    AddStroke(Rotated(item, VectorGeometry.Flatten(item, line.Nodes, line.Closed)), line.Portals, walls, portals);
                }
                else if (item.Shape is { Stroke.BlocksLight: true } shape)
                {
                    foreach (var region in shape.Regions)
                    {
                        if (region.Nodes.Count < 2) continue;
                        AddStroke(Rotated(item, VectorGeometry.RegionOutline(item, region)), shape.Portals, walls, portals);
                    }
                }
            }
            foreach (var road in layer.Roads)
            {
                if (!road.Visible) continue;
                foreach (var branch in RoadGeometry.Evaluate(road).Branches.Values)
                {
                    if (!branch.Style.BlocksLight || branch.Samples.Length < 2) continue;
                    walls.Add([.. branch.Samples.Select(sample => new VectorPoint(sample.X, sample.Y))]);
                }
            }
        }
        return new VttWalls(walls, portals);
    }

    /// <summary>The visible runs of a stroke between its portal gaps, and each gap as an opening.</summary>
    public static void AddStroke(
        IReadOnlyList<VectorPoint> points, IReadOnlyList<LinePortal> gaps,
        List<IReadOnlyList<VectorPoint>> walls, List<VttPortal> portals)
    {
        if (points.Count < 2) return;
        var lengths = new double[points.Count - 1];
        var total = 0.0;
        for (var i = 0; i < lengths.Length; i++) total += lengths[i] = Distance(points[i], points[i + 1]);
        if (total <= 0) return;
        var cuts = gaps
            .Select(gap => (Start: Math.Clamp(gap.Position * total - gap.Width / 2, 0, total),
                            End: Math.Clamp(gap.Position * total + gap.Width / 2, 0, total)))
            .Where(gap => gap.End > gap.Start)
            .OrderBy(gap => gap.Start)
            .ToList();
        foreach (var cut in cuts) portals.Add(new VttPortal(PointAt(points, lengths, cut.Start), PointAt(points, lengths, cut.End)));

        // Merge overlapping gaps, then walk the visible intervals between them.
        var merged = new List<(double Start, double End)>();
        foreach (var cut in cuts)
        {
            if (merged.Count > 0 && cut.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, cut.End));
            else merged.Add(cut);
        }
        var cursor = 0.0;
        foreach (var gap in merged.Append((Start: total, End: total)))
        {
            if (gap.Start - cursor > 1e-9) walls.Add(Slice(points, lengths, cursor, gap.Start));
            cursor = Math.Max(cursor, gap.End);
        }
    }

    private static IReadOnlyList<VectorPoint> Slice(IReadOnlyList<VectorPoint> points, double[] lengths, double from, double to)
    {
        var result = new List<VectorPoint> { PointAt(points, lengths, from) };
        var walked = 0.0;
        for (var i = 0; i < lengths.Length; i++)
        {
            walked += lengths[i];
            if (walked > from + 1e-9 && walked < to - 1e-9) result.Add(points[i + 1]);
        }
        result.Add(PointAt(points, lengths, to));
        return result;
    }

    private static VectorPoint PointAt(IReadOnlyList<VectorPoint> points, double[] lengths, double distance)
    {
        var walked = 0.0;
        for (var i = 0; i < lengths.Length; i++)
        {
            if (distance <= walked + lengths[i] || i == lengths.Length - 1)
            {
                var t = lengths[i] <= 0 ? 0 : Math.Clamp((distance - walked) / lengths[i], 0, 1);
                return new VectorPoint(points[i].X + ((points[i + 1].X - points[i].X) * t), points[i].Y + ((points[i + 1].Y - points[i].Y) * t));
            }
            walked += lengths[i];
        }
        return points[^1];
    }

    /// <summary>The flattened points turned with the object about its centre, as the renderer draws them.</summary>
    private static IReadOnlyList<VectorPoint> Rotated(SceneObject owner, IReadOnlyList<VectorPoint> points)
    {
        if (owner.RotationDegrees == 0) return points;
        var radians = owner.RotationDegrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        var cx = owner.X + (owner.Width / 2);
        var cy = owner.Y + (owner.Height / 2);
        return [.. points.Select(p => new VectorPoint(
            cx + ((p.X - cx) * cos) - ((p.Y - cy) * sin),
            cy + ((p.X - cx) * sin) + ((p.Y - cy) * cos)))];
    }

    private static double Distance(VectorPoint a, VectorPoint b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
}
