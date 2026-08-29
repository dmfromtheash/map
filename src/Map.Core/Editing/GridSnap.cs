using Map.Core.Documents;

namespace Map.Core.Editing;

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
        var line = source.Line ?? throw new ArgumentException("Portal snapping currently requires a line.", nameof(source));
        var portal = line.Portals.FirstOrDefault(item => item.Id == portalId)
            ?? throw new InvalidOperationException($"Portal {portalId} is absent.");
        var points = VectorGeometry.Flatten(source, line.Nodes, line.Closed);
        var lengths = points.Zip(points.Skip(1)).Select(pair => Distance(pair.First, pair.Second)).ToList();
        var total = lengths.Sum();
        var point = RotateOwner(source, PointAt(points, lengths, portal.Position * total));
        var snapped = Snap(grid, point);

        // A portal is constrained to its line. Moving only its scalar position generally cannot
        // land on a two-dimensional grid node, so the whole line is translated by the exact snap
        // delta while the portal and every vector id remain stable.
        return Copy(source, source.X + snapped.X - point.X, source.Y + snapped.Y - point.Y);
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

    private static double Distance(VectorPoint a, VectorPoint b) =>
        Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    private static SceneObject Copy(SceneObject source, double x, double y) => new(
        source.Id, source.Kind, source.Name, x, y, source.Width, source.Height, source.RotationDegrees,
        source.FillColor, source.StrokeColor, source.StrokeWidth, source.Visible, source.Locked)
    {
        Sublayer = source.Sublayer,
        GroupId = source.GroupId,
        GroupName = source.GroupName,
        Stamp = source.Stamp,
        Line = source.Line,
        Shape = source.Shape,
        Text = source.Text,
        ObjectOpacity = source.ObjectOpacity,
        Composition = source.Composition,
        PlaceholderReason = source.PlaceholderReason,
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
