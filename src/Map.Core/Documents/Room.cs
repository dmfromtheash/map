namespace Map.Core.Documents;

/// <summary>
/// «Комната» for dungeon maps: a closed shape drawn as three layers - the floor (the shape fill:
/// a colour or a material), the wall (the shape stroke: a colour or a repeated asset, which can
/// block light for virtual tabletops) and the caps (an asset at every corner, turned along the
/// wall). Doors are the shape's portals. This record only adds what an ordinary shape lacks;
/// a shape without it is drawn exactly as before.
/// </summary>
public sealed record RoomStyle
{
    public const double MaxCornerRadius = 512;
    public const double MaxCapScale = 8;

    /// <summary>Sharp corners are rounded with this radius in map units (0 keeps them sharp).</summary>
    public double CornerRadius { get; init; } = 16;

    /// <summary>The cap picture placed on each corner; null - no caps.</summary>
    public string? CapAssetSha256 { get; init; }

    /// <summary>Cap width as a multiple of the wall thickness.</summary>
    public double CapScale { get; init; } = 2.5;

    /// <summary>Extra turn of every cap, degrees, on top of the wall direction at the corner.</summary>
    public double CapAngleDegrees { get; init; }

    /// <summary>Caps cast the wall's shadow.</summary>
    public bool CapShadow { get; init; } = true;

    public bool IsValid =>
        double.IsFinite(CornerRadius) && CornerRadius is >= 0 and <= MaxCornerRadius
        && double.IsFinite(CapScale) && CapScale is > 0 and <= MaxCapScale
        && double.IsFinite(CapAngleDegrees) && CapAngleDegrees is >= -360 and <= 360;
}

/// <summary>One cap on the wall: where its centre sits and which way the wall runs there.</summary>
public readonly record struct RoomCap(VectorPoint Centre, double AngleDegrees);

/// <summary>Pure room geometry shared by rendering, export and tests.</summary>
public static class RoomGeometry
{
    private const int ArcSteps = 8;

    /// <summary>True when this region's corners can be rounded: a polygon of straight corners.</summary>
    public static bool CanRound(SceneObject owner, ShapeRegion region) =>
        owner.Room is { CornerRadius: > 0 }
        && region.Primitive != ShapePrimitiveKind.Ellipse
        && region.Nodes.Count >= 3
        && region.Nodes.All(node => node.Kind == VectorNodeKind.Corner);

    /// <summary>The corner vertices of a region in world units (before the object's rotation).</summary>
    public static IReadOnlyList<VectorPoint> Corners(SceneObject owner, ShapeRegion region) =>
        [.. region.Nodes.Select(node => new VectorPoint(owner.X + (node.X * owner.Width), owner.Y + (node.Y * owner.Height)))];

    /// <summary>
    /// The closed outline with each corner replaced by a circular arc tangent to both sides; the
    /// radius shrinks where a side is too short for it. The first point is repeated at the end.
    /// </summary>
    public static IReadOnlyList<VectorPoint> RoundedOutline(IReadOnlyList<VectorPoint> corners, double radius)
    {
        var result = new List<VectorPoint>(corners.Count * (ArcSteps + 1) + 1);
        for (var i = 0; i < corners.Count; i++)
        {
            foreach (var point in CornerArc(corners, i, radius)) result.Add(point);
        }
        result.Add(result[0]);
        return result;
    }

    /// <summary>One cap per corner, at the middle of the rounded corner, turned along the wall.</summary>
    public static IReadOnlyList<RoomCap> Caps(SceneObject owner, ShapeRegion region)
    {
        if (owner.Room is not { } room || region.Nodes.Count < 3) return [];
        var corners = Corners(owner, region);
        var radius = CanRound(owner, region) ? room.CornerRadius : 0;
        var caps = new List<RoomCap>(corners.Count);
        for (var i = 0; i < corners.Count; i++)
        {
            var previous = corners[(i - 1 + corners.Count) % corners.Count];
            var next = corners[(i + 1) % corners.Count];
            // The wall direction at the corner: halfway between the incoming and outgoing sides.
            var inAngle = Math.Atan2(corners[i].Y - previous.Y, corners[i].X - previous.X);
            var outAngle = Math.Atan2(next.Y - corners[i].Y, next.X - corners[i].X);
            var turn = Math.Atan2(Math.Sin(outAngle - inAngle), Math.Cos(outAngle - inAngle));
            var direction = (inAngle + (turn / 2)) * 180 / Math.PI;
            var arc = CornerArc(corners, i, radius);
            caps.Add(new RoomCap(arc[arc.Count / 2], direction + room.CapAngleDegrees));
        }
        return caps;
    }

    private static IReadOnlyList<VectorPoint> CornerArc(IReadOnlyList<VectorPoint> corners, int index, double radius)
    {
        var corner = corners[index];
        var previous = corners[(index - 1 + corners.Count) % corners.Count];
        var next = corners[(index + 1) % corners.Count];
        var (ax, ay, aLength) = Unit(previous.X - corner.X, previous.Y - corner.Y);
        var (bx, by, bLength) = Unit(next.X - corner.X, next.Y - corner.Y);
        var cos = Math.Clamp((ax * bx) + (ay * by), -1, 1);
        var angle = Math.Acos(cos);
        if (radius <= 0 || aLength <= 0 || bLength <= 0 || angle < 1e-3 || Math.PI - angle < 1e-3) return [corner];
        // Spikes sharper than 45° get proportionally less rounding, so a narrow corner keeps its point.
        radius *= Math.Min(1, angle / (Math.PI / 4));

        // Distance from the corner to the tangent points, limited to half of each side.
        var tangent = radius / Math.Tan(angle / 2);
        var limit = Math.Min(aLength, bLength) / 2;
        if (tangent > limit) { tangent = limit; radius = tangent * Math.Tan(angle / 2); }
        var start = new VectorPoint(corner.X + (ax * tangent), corner.Y + (ay * tangent));
        var end = new VectorPoint(corner.X + (bx * tangent), corner.Y + (by * tangent));
        var (cx, cy, _) = Unit(ax + bx, ay + by);
        var centreDistance = radius / Math.Sin(angle / 2);
        var centre = new VectorPoint(corner.X + (cx * centreDistance), corner.Y + (cy * centreDistance));
        var from = Math.Atan2(start.Y - centre.Y, start.X - centre.X);
        var to = Math.Atan2(end.Y - centre.Y, end.X - centre.X);
        var sweep = Math.Atan2(Math.Sin(to - from), Math.Cos(to - from));
        var arc = new List<VectorPoint>(ArcSteps + 1);
        for (var step = 0; step <= ArcSteps; step++)
        {
            var a = from + (sweep * step / ArcSteps);
            arc.Add(new VectorPoint(centre.X + (Math.Cos(a) * radius), centre.Y + (Math.Sin(a) * radius)));
        }
        return arc;
    }

    private static (double X, double Y, double Length) Unit(double x, double y)
    {
        var length = Math.Sqrt((x * x) + (y * y));
        return length <= 0 ? (0, 0, 0) : (x / length, y / length, length);
    }
}
