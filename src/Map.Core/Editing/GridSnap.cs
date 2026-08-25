using Map.Core.Documents;

namespace Map.Core.Editing;

public static class GridSnapService
{
    public static VectorPoint Snap(GridDefinition grid, VectorPoint world)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var local = Rotate(world.X - grid.OffsetX, world.Y - grid.OffsetY, -grid.RotationDegrees);
        var size = grid.CellSize / Math.Max(1, grid.Subdivisions);
        var snapped = grid.Geometry switch
        {
            GridGeometry.HexFlat => SnapHex(local, size, pointy: false),
            GridGeometry.HexPointy => SnapHex(local, size, pointy: true),
            _ => new VectorPoint(Math.Round(local.X / size) * size, Math.Round(local.Y / size) * size),
        };
        var rotated = Rotate(snapped.X, snapped.Y, grid.RotationDegrees);
        return new VectorPoint(rotated.X + grid.OffsetX, rotated.Y + grid.OffsetY);
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
        var line = source.Line ?? throw new ArgumentException("Portal snapping currently requires a line.", nameof(source));
        var portal = line.Portals.FirstOrDefault(item => item.Id == portalId)
            ?? throw new InvalidOperationException($"Portal {portalId} is absent.");
        var points = VectorGeometry.Flatten(source, line.Nodes, line.Closed);
        var lengths = points.Zip(points.Skip(1)).Select(pair => Distance(pair.First, pair.Second)).ToList();
        var total = lengths.Sum();
        var point = PointAt(points, lengths, portal.Position * total);
        var snapped = UnrotateOwner(source, Snap(grid, RotateOwner(source, point)));
        var position = Project(points, lengths, snapped) / Math.Max(total, 0.001);
        return VectorObjectEditor.MovePortal(source, portalId, Math.Clamp(position, 0, 1));
    }

    private static VectorPoint SnapHex(VectorPoint point, double size, bool pointy)
    {
        var sqrt3 = Math.Sqrt(3);
        var q = pointy ? ((sqrt3 / 3 * point.X) - (point.Y / 3)) / size : (2.0 / 3 * point.X) / size;
        var r = pointy ? (2.0 / 3 * point.Y) / size : ((-point.X / 3) + (sqrt3 / 3 * point.Y)) / size;
        var (rq, rr) = CubeRound(q, r);
        return pointy
            ? new VectorPoint(size * sqrt3 * (rq + rr / 2), size * 1.5 * rr)
            : new VectorPoint(size * 1.5 * rq, size * sqrt3 * (rr + rq / 2));
    }

    private static (double Q, double R) CubeRound(double q, double r)
    {
        var x = q; var z = r; var y = -x - z;
        var rx = Math.Round(x); var ry = Math.Round(y); var rz = Math.Round(z);
        var dx = Math.Abs(rx - x); var dy = Math.Abs(ry - y); var dz = Math.Abs(rz - z);
        if (dx > dy && dx > dz) rx = -ry - rz;
        else if (dy > dz) ry = -rx - rz;
        else rz = -rx - ry;
        return (rx, rz);
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

    private static VectorPoint PointAt(IReadOnlyList<VectorPoint> points, IReadOnlyList<double> lengths, double distance)
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

    private static double Project(IReadOnlyList<VectorPoint> points, IReadOnlyList<double> lengths, VectorPoint target)
    {
        var bestDistance = double.MaxValue; var bestAlong = 0.0; var cursor = 0.0;
        for (var i = 0; i < lengths.Count; i++)
        {
            var a = points[i]; var b = points[i + 1];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length2 = dx * dx + dy * dy;
            var t = length2 == 0 ? 0 : Math.Clamp(((target.X - a.X) * dx + (target.Y - a.Y) * dy) / length2, 0, 1);
            var candidate = new VectorPoint(a.X + t * dx, a.Y + t * dy);
            var distance = Distance(candidate, target);
            if (distance < bestDistance) { bestDistance = distance; bestAlong = cursor + t * lengths[i]; }
            cursor += lengths[i];
        }
        return bestAlong;
    }

    private static double Distance(VectorPoint a, VectorPoint b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    private static SceneObject Copy(SceneObject source, double x, double y) => new(
        source.Id, source.Kind, source.Name, x, y, source.Width, source.Height, source.RotationDegrees,
        source.FillColor, source.StrokeColor, source.StrokeWidth, source.Visible, source.Locked)
    {
        Sublayer = source.Sublayer, GroupId = source.GroupId, Stamp = source.Stamp,
        Line = source.Line, Shape = source.Shape, Text = source.Text, ObjectOpacity = source.ObjectOpacity,
        Composition = source.Composition, PlaceholderReason = source.PlaceholderReason,
    };
}

public sealed class AddNoteCommand(MapNote note) : IEditCommand
{
    public string Name => "Add note";
    public MapDocument Apply(MapDocument document) => document.WithNotes([.. document.Notes, note]);
    public MapDocument Revert(MapDocument document) => document.WithNotes([.. document.Notes.Where(item => item.Id != note.Id)]);
}

public sealed class ReplaceNoteCommand(MapNote before, MapNote after) : IEditCommand
{
    public string Name => "Edit note";
    public MapDocument Apply(MapDocument document) => Write(document, after);
    public MapDocument Revert(MapDocument document) => Write(document, before);
    private MapDocument Write(MapDocument document, MapNote value) => document.WithNotes(
        [.. document.Notes.Select(item => item.Id == value.Id ? value : item)]);
}

public sealed class DeleteNoteCommand(MapDocument document, NoteId id) : IEditCommand
{
    private readonly int _index = document.Notes.ToList().FindIndex(note => note.Id == id);
    private readonly MapNote _note = document.Notes.First(note => note.Id == id);
    public string Name => "Delete note";
    public MapDocument Apply(MapDocument value) => value.WithNotes([.. value.Notes.Where(note => note.Id != id)]);
    public MapDocument Revert(MapDocument value)
    {
        var notes = value.Notes.ToList(); notes.Insert(Math.Min(_index, notes.Count), _note);
        return value.WithNotes(notes);
    }
}

/// <summary>Replaces one immutable grid-layer snapshot as one undoable edit.</summary>
public sealed class ReplaceGridLayerCommand(GridLayer before, GridLayer after) : IEditCommand
{
    public string Name => "Edit grid";
    public MapDocument Apply(MapDocument document) => document.WithLayer(before.Id, _ => after);
    public MapDocument Revert(MapDocument document) => document.WithLayer(before.Id, _ => before);
}
