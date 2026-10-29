using Map.Core.Documents;

namespace Map.Core.Editing;

public enum PortalSnapOutcome
{
    Snapped,
    AlreadySnapped,
    NotRepresentable,
}

public static class GridSnapService
{
    public static VectorPoint Snap(GridDefinition grid, VectorPoint world)
    {
        return GridGeometryContract.Snap(grid, world);
    }

    public static SceneObject SnapObject(SceneObject source, GridDefinition grid)
    {
        var centre = Snap(grid, new VectorPoint(source.X + source.Width / 2, source.Y + source.Height / 2));
        return Copy(source, centre.X - source.Width / 2, centre.Y - source.Height / 2);
    }

    public static SceneObject SnapNode(SceneObject source, VectorNodeId nodeId, GridDefinition grid)
    {
        var node = source.Line?.Nodes.FirstOrDefault(item => item.Id == nodeId)
            ?? source.Shape?.Regions.SelectMany(region => region.Nodes).FirstOrDefault(item => item.Id == nodeId)
            ?? throw new InvalidOperationException($"Node {nodeId} is absent.");
        var world = RotateOwner(source,
            new VectorPoint(source.X + node.X * source.Width, source.Y + node.Y * source.Height));
        var snapped = UnrotateOwner(source, Snap(grid, world));
        return VectorObjectEditor.MoveNode(source, nodeId,
            (snapped.X - source.X) / source.Width, (snapped.Y - source.Y) / source.Height);
    }

    public static SceneObject SnapPortal(SceneObject source, PortalId portalId, GridDefinition grid)
    {
        return SnapPortal(source, portalId, grid, out _);
    }

    public static SceneObject SnapPortal(
        SceneObject source, PortalId portalId, GridDefinition grid, out PortalSnapOutcome outcome)
    {
        var line = source.Line ?? throw new ArgumentException("Portal snapping currently requires a line.", nameof(source));
        var portal = line.Portals.FirstOrDefault(item => item.Id == portalId)
            ?? throw new InvalidOperationException($"Portal {portalId} is absent.");
        var points = VectorGeometry.Flatten(source, line.Nodes, line.Closed);
        var lengths = points.Zip(points.Skip(1)).Select(pair => Distance(pair.First, pair.Second)).ToList();
        var total = lengths.Sum();
        var point = RotateOwner(source, PointAt(points, lengths, portal.Position * total));
        var snapped = Snap(grid, point);
        var unrotatedSnapped = UnrotateOwner(source, snapped);
        var tolerance = ScaleAwareTolerance(points, point, snapped, unrotatedSnapped);

        if (Distance(point, snapped) <= tolerance)
        {
            outcome = PortalSnapOutcome.AlreadySnapped;
            return source;
        }

        if (!TryPositionAtPoint(points, lengths, total, unrotatedSnapped, tolerance, out var position))
        {
            outcome = PortalSnapOutcome.NotRepresentable;
            return source;
        }

        outcome = PortalSnapOutcome.Snapped;
        return VectorObjectEditor.MovePortal(source, portalId, position);
    }

    private static VectorPoint Rotate(double x, double y, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new VectorPoint(x * Math.Cos(radians) - y * Math.Sin(radians),
            x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    private static VectorPoint RotateOwner(SceneObject owner, VectorPoint point)
    {
        var cx = owner.X + owner.Width / 2; var cy = owner.Y + owner.Height / 2;
        var rotated = Rotate(point.X - cx, point.Y - cy, owner.RotationDegrees);
        return new VectorPoint(cx + rotated.X, cy + rotated.Y);
    }

    private static VectorPoint UnrotateOwner(SceneObject owner, VectorPoint point)
    {
        var cx = owner.X + owner.Width / 2; var cy = owner.Y + owner.Height / 2;
        var rotated = Rotate(point.X - cx, point.Y - cy, -owner.RotationDegrees);
        return new VectorPoint(cx + rotated.X, cy + rotated.Y);
    }

    private static VectorPoint PointAt(IReadOnlyList<VectorPoint> points, List<double> lengths, double distance)
    {
        var cursor = 0.0;
        for (var i = 0; i < lengths.Count; i++)
        {
            if (distance <= cursor + lengths[i])
            {
                var t = (distance - cursor) / Math.Max(lengths[i], 0.001);
                return new VectorPoint(points[i].X + (points[i + 1].X - points[i].X) * t,
                    points[i].Y + (points[i + 1].Y - points[i].Y) * t);
            }
            cursor += lengths[i];
        }
        return points[^1];
    }

    private static bool TryPositionAtPoint(
        IReadOnlyList<VectorPoint> points,
        List<double> lengths,
        double total,
        VectorPoint target,
        double tolerance,
        out double position)
    {
        position = 0;
        if (total <= tolerance)
        {
            return false;
        }

        var cursor = 0.0;
        var bestDistance = double.PositiveInfinity;
        var bestArcDistance = 0.0;
        for (var index = 0; index < lengths.Count; index++)
        {
            var length = lengths[index];
            if (length <= tolerance)
            {
                cursor += length;
                continue;
            }

            var start = points[index];
            var end = points[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var amount = Math.Clamp(
                ((target.X - start.X) * dx + (target.Y - start.Y) * dy) / (length * length),
                0,
                1);
            var projected = new VectorPoint(start.X + dx * amount, start.Y + dy * amount);
            var distance = Distance(projected, target);
            var arcDistance = cursor + length * amount;
            if (distance + tolerance < bestDistance
                || Math.Abs(distance - bestDistance) <= tolerance && arcDistance < bestArcDistance)
            {
                bestDistance = distance;
                bestArcDistance = arcDistance;
            }

            cursor += length;
        }

        if (bestDistance > tolerance)
        {
            return false;
        }

        position = Math.Clamp(bestArcDistance / total, 0, 1);
        return true;
    }

    private static double ScaleAwareTolerance(
        IReadOnlyList<VectorPoint> points,
        params VectorPoint[] additionalPoints)
    {
        var scale = points.Concat(additionalPoints)
            .SelectMany(point => new[] { Math.Abs(point.X), Math.Abs(point.Y) })
            .Append(1)
            .Max();
        var next = Math.BitIncrement(scale);
        var ulp = double.IsFinite(next) ? next - scale : scale - Math.BitDecrement(scale);
        return ulp <= double.MaxValue / 128
            ? Math.Max(1e-9, ulp * 128)
            : double.MaxValue;
    }

    private static double Distance(VectorPoint a, VectorPoint b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    private static SceneObject Copy(SceneObject source, double x, double y) => new SceneObject(
        source.Id, source.Kind, source.Name, x, y, source.Width, source.Height, source.RotationDegrees,
        source.FillColor, source.StrokeColor, source.StrokeWidth, source.Visible, source.Locked)
    {
        Sublayer = source.Sublayer,
        GroupId = source.GroupId,
        GroupName = source.GroupName,
        Stamp = source.Stamp,
        Line = source.Line,
        Shape = source.Shape,
        FillGradient = source.FillGradient,
        Text = source.Text,
        ObjectOpacity = source.ObjectOpacity,
        Composition = source.Composition,
        Decoration = source.Decoration,
        PlaceholderReason = source.PlaceholderReason,
        OpaquePayload = source.OpaquePayload,
    }.KeepingMeaningOf(source);
}

/// <summary>Replaces one immutable grid-layer snapshot as one undoable edit.</summary>
public sealed class ReplaceGridLayerCommand(GridLayer before, GridLayer after) : IEditCommand
{
    public string Name => "Edit grid";
    public MapDocument Apply(MapDocument document) => document.WithLayer(before.Id, _ => after);
    public MapDocument Revert(MapDocument document) => document.WithLayer(before.Id, _ => before);
}
