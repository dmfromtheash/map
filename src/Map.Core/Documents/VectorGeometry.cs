using System.Collections.ObjectModel;

namespace Map.Core.Documents;

public enum VectorNodeKind { Corner, Curve }
public enum VectorCap { Butt, Round, Square }
public enum ShapePrimitiveKind { Rectangle, Ellipse, Polygon, FreeDraw }
public enum ShapeCombineMode { Add, Subtract }

/// <summary>Local coordinates are normalized to the owning object's unrotated bounds.</summary>
public sealed record VectorNode
{
    public VectorNode(
        VectorNodeId id, double x, double y, VectorNodeKind kind = VectorNodeKind.Corner,
        double controlInX = 0, double controlInY = 0,
        double controlOutX = 0, double controlOutY = 0)
    {
        if (!AllFinite(x, y, controlInX, controlInY, controlOutX, controlOutY))
        {
            throw new ArgumentException("Vector node coordinates must be finite.");
        }

        Id = id; X = x; Y = y; Kind = kind;
        ControlInX = controlInX; ControlInY = controlInY;
        ControlOutX = controlOutX; ControlOutY = controlOutY;
    }

    public VectorNodeId Id { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public VectorNodeKind Kind { get; init; }
    public double ControlInX { get; init; }
    public double ControlInY { get; init; }
    public double ControlOutX { get; init; }
    public double ControlOutY { get; init; }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);
}

public sealed record LinePortal
{
    public LinePortal(PortalId id, double position, double width)
    {
        if (!double.IsFinite(position) || position is < 0 or > 1 || !double.IsFinite(width) || width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Portal position is 0..1 and width is nonnegative.");
        }

        Id = id; Position = position; Width = width;
    }

    public PortalId Id { get; init; }
    public double Position { get; init; }
    public double Width { get; init; }
}

public sealed record VectorStrokeStyle
{
    public string Color { get; init; } = "#FF3D566E";
    public double Width { get; init; } = 8;
    public double Opacity { get; init; } = 1;
    public string Blend { get; init; } = "Normal";

    /// <summary>Gaussian blur sigma in canvas units applied to the whole stroke; 0 = none.</summary>
    public double Blur { get; init; }

    /// <summary>Solid, dotted or dashed rendering of a colour stroke.</summary>
    public StrokePattern Pattern { get; init; } = StrokePattern.Solid;

    public double Offset { get; init; }
    public VectorCap EndCap { get; init; } = VectorCap.Round;
    public VectorCap NodeCap { get; init; } = VectorCap.Round;
    public double ShadowOffsetX { get; init; } = 4;
    public double ShadowOffsetY { get; init; } = 4;
    public double ShadowBlur { get; init; } = 6;
    public double ShadowOpacity { get; init; }
    public string? AssetSha256 { get; init; }
    public double AssetSpacing { get; init; } = 1;
    public double Hue { get; init; }
    public double Saturation { get; init; }
}

public sealed record LineStyle
{
    public LineStyle(
        IReadOnlyList<VectorNode> nodes, VectorStrokeStyle stroke,
        IReadOnlyList<LinePortal>? portals = null, bool closed = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(stroke);
        if (nodes.Count < 2 || nodes.Select(n => n.Id).Distinct().Count() != nodes.Count)
        {
            throw new ArgumentException("A line has at least two uniquely identified nodes.", nameof(nodes));
        }

        portals ??= [];
        if (portals.Select(p => p.Id).Distinct().Count() != portals.Count)
        {
            throw new ArgumentException("Portal ids are unique.", nameof(portals));
        }

        Nodes = new ReadOnlyCollection<VectorNode>([.. nodes]);
        Stroke = stroke;
        Portals = new ReadOnlyCollection<LinePortal>([.. portals]);
        Closed = closed;
    }

    public IReadOnlyList<VectorNode> Nodes { get; init; }
    public VectorStrokeStyle Stroke { get; init; }
    public IReadOnlyList<LinePortal> Portals { get; init; }
    public bool Closed { get; init; }
}

public sealed record ShapeFillStyle
{
    public string Color { get; init; } = "#AA507A5B";
    public string? TextureAssetSha256 { get; init; }
    public double TextureScale { get; init; } = 1;
    public double TextureRotation { get; init; }
    public double TextureOffsetX { get; init; }
    public double TextureOffsetY { get; init; }
    public double Opacity { get; init; } = 1;
}

public sealed record ShapeRegion
{
    public ShapeRegion(
        ShapeRegionId id, ShapeCombineMode mode, ShapePrimitiveKind primitive,
        IReadOnlyList<VectorNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var minimum = primitive == ShapePrimitiveKind.Ellipse ? 2 : 3;
        if (nodes.Count < minimum || nodes.Select(n => n.Id).Distinct().Count() != nodes.Count
            || nodes.Any(n => !double.IsFinite(n.X) || !double.IsFinite(n.Y)))
        {
            throw new ArgumentException("A shape region has finite, uniquely identified nodes.", nameof(nodes));
        }

        Id = id; Mode = mode; Primitive = primitive;
        Nodes = new ReadOnlyCollection<VectorNode>([.. nodes]);
    }

    public ShapeRegionId Id { get; init; }
    public ShapeCombineMode Mode { get; init; }
    public ShapePrimitiveKind Primitive { get; init; }
    public IReadOnlyList<VectorNode> Nodes { get; init; }
}

public sealed record ShapeStyle
{
    public ShapeStyle(
        IReadOnlyList<ShapeRegion> regions, ShapeFillStyle fill, VectorStrokeStyle stroke,
        IReadOnlyList<LinePortal>? portals = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(stroke);
        if (regions.Count == 0 || regions[0].Mode != ShapeCombineMode.Add
            || regions.Select(r => r.Id).Distinct().Count() != regions.Count)
        {
            throw new ArgumentException("A shape starts with one add region and has unique region ids.", nameof(regions));
        }

        portals ??= [];
        Regions = new ReadOnlyCollection<ShapeRegion>([.. regions]);
        Fill = fill; Stroke = stroke;
        Portals = new ReadOnlyCollection<LinePortal>([.. portals]);
    }

    public IReadOnlyList<ShapeRegion> Regions { get; init; }
    public ShapeFillStyle Fill { get; init; }
    public VectorStrokeStyle Stroke { get; init; }
    public IReadOnlyList<LinePortal> Portals { get; init; }
}

public readonly record struct VectorPoint(double X, double Y);

/// <summary>Pure factories and geometry evaluation shared by editing, hit-test and tests.</summary>
public static class VectorGeometry
{
    public static SceneObject WithNewIds(SceneObject source)
    {
        var result = source with { Id = ObjectId.New() };
        if (source.Line is { } line)
        {
            result = result with
            {
                Line = new LineStyle(
                    [.. line.Nodes.Select(node => node with { Id = VectorNodeId.New() })],
                    line.Stroke,
                    [.. line.Portals.Select(portal => portal with { Id = PortalId.New() })],
                    line.Closed),
            };
        }
        if (source.Shape is { } shape)
        {
            result = result with
            {
                Shape = new ShapeStyle(
                    [.. shape.Regions.Select(region => new ShapeRegion(
                        ShapeRegionId.New(), region.Mode, region.Primitive,
                        [.. region.Nodes.Select(node => node with { Id = VectorNodeId.New() })]))],
                    shape.Fill, shape.Stroke,
                    [.. shape.Portals.Select(portal => portal with { Id = PortalId.New() })]),
            };
        }
        if (source.Composition is { } composition)
        {
            result = result with
            {
                Composition = composition with
                {
                    Id = CompositionId.New(),
                    Items = [.. composition.Items.Select(WithNewIds)],
                    Grids = [.. composition.Grids.Select(grid => grid with { Id = GridId.New() })],
                },
            };
        }
        return result;
    }

    public static SceneObject CreateLine(
        ObjectId id, string name, IReadOnlyList<VectorPoint> points, VectorStrokeStyle? stroke = null)
    {
        var (bounds, nodes) = Normalize(points, minimum: 2);
        return NewVectorObject(id, SceneObjectKind.Line, name, bounds) with
        {
            Line = new LineStyle(nodes, stroke ?? new VectorStrokeStyle()),
        };
    }

    public static SceneObject CreateShape(
        ObjectId id, string name, ShapePrimitiveKind kind, WorldBounds bounds,
        ShapeFillStyle? fill = null, VectorStrokeStyle? stroke = null)
    {
        var normalized = NormalizeBounds(bounds);
        IReadOnlyList<VectorNode> nodes = kind switch
        {
            ShapePrimitiveKind.Ellipse =>
            [new VectorNode(VectorNodeId.New(), 0, 0), new VectorNode(VectorNodeId.New(), 1, 1)],
            _ =>
            [
                new VectorNode(VectorNodeId.New(), 0, 0),
                new VectorNode(VectorNodeId.New(), 1, 0),
                new VectorNode(VectorNodeId.New(), 1, 1),
                new VectorNode(VectorNodeId.New(), 0, 1),
            ],
        };
        return NewVectorObject(id, SceneObjectKind.Shape, name, normalized) with
        {
            Shape = new ShapeStyle(
                [new ShapeRegion(ShapeRegionId.New(), ShapeCombineMode.Add, kind, nodes)],
                fill ?? new ShapeFillStyle(), stroke ?? new VectorStrokeStyle { Width = 3 }),
        };
    }

    public static SceneObject CreateFreeShape(
        ObjectId id, string name, IReadOnlyList<VectorPoint> points,
        ShapeFillStyle? fill = null, VectorStrokeStyle? stroke = null)
    {
        var (bounds, nodes) = Normalize(points, minimum: 3);
        return NewVectorObject(id, SceneObjectKind.Shape, name, bounds) with
        {
            Shape = new ShapeStyle(
                [new ShapeRegion(ShapeRegionId.New(), ShapeCombineMode.Add, ShapePrimitiveKind.FreeDraw, nodes)],
                fill ?? new ShapeFillStyle(), stroke ?? new VectorStrokeStyle { Width = 3 }),
        };
    }

    public static IReadOnlyList<VectorPoint> Flatten(SceneObject owner, IReadOnlyList<VectorNode> nodes, bool closed)
    {
        var result = new List<VectorPoint>();
        var segmentCount = closed ? nodes.Count : nodes.Count - 1;
        for (var index = 0; index < segmentCount; index++)
        {
            var a = nodes[index];
            var b = nodes[(index + 1) % nodes.Count];
            var curved = a.Kind == VectorNodeKind.Curve || b.Kind == VectorNodeKind.Curve;
            var steps = curved ? 16 : 1;
            for (var step = 0; step < steps; step++)
            {
                var t = step / (double)steps;
                result.Add(curved ? Cubic(owner, a, b, t) : Map(owner, a.X, a.Y));
            }
        }

        result.Add(Map(owner, nodes[closed ? 0 : ^1].X, nodes[closed ? 0 : ^1].Y));
        return result;
    }

    public static bool ContainsShape(SceneObject owner, double worldX, double worldY)
    {
        if (owner.Shape is not { } shape)
        {
            return false;
        }

        var local = ToLocal(owner, worldX, worldY);
        var included = false;
        foreach (var region in shape.Regions)
        {
            var contains = region.Primitive == ShapePrimitiveKind.Ellipse
                ? InEllipse(region.Nodes, local.X, local.Y)
                : InPolygon(region.Nodes, local.X, local.Y);
            included = region.Mode == ShapeCombineMode.Add ? included || contains : included && !contains;
        }

        return included;
    }

    public static bool ContainsLine(SceneObject owner, double worldX, double worldY)
    {
        if (owner.Line is not { } line)
        {
            return false;
        }

        var points = Flatten(owner, line.Nodes, line.Closed);
        var tolerance = Math.Max(3, line.Stroke.Width / 2);
        return points.Zip(points.Skip(1)).Any(pair => DistanceToSegment(worldX, worldY, pair.First, pair.Second) <= tolerance);
    }

    private static SceneObject NewVectorObject(ObjectId id, SceneObjectKind kind, string name, WorldBounds b) =>
        new(id, kind, name, b.Left, b.Top, Math.Max(0.001, b.Right - b.Left), Math.Max(0.001, b.Bottom - b.Top),
            0, "#00000000", "#FF3D566E", 0, true, false);

    private static (WorldBounds Bounds, IReadOnlyList<VectorNode> Nodes) Normalize(
        IReadOnlyList<VectorPoint> points, int minimum)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < minimum || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
        {
            throw new ArgumentException($"Vector geometry requires at least {minimum} finite points.", nameof(points));
        }

        var left = points.Min(p => p.X); var top = points.Min(p => p.Y);
        var width = Math.Max(0.001, points.Max(p => p.X) - left);
        var height = Math.Max(0.001, points.Max(p => p.Y) - top);
        var nodes = points.Select(p => new VectorNode(
            VectorNodeId.New(), (p.X - left) / width, (p.Y - top) / height)).ToList();
        return (new WorldBounds(left, top, left + width, top + height), nodes);
    }

    private static WorldBounds NormalizeBounds(WorldBounds b) => new(
        Math.Min(b.Left, b.Right), Math.Min(b.Top, b.Bottom),
        Math.Max(b.Left, b.Right), Math.Max(b.Top, b.Bottom));

    private static VectorPoint Map(SceneObject owner, double x, double y) =>
        new(owner.X + (x * owner.Width), owner.Y + (y * owner.Height));

    private static VectorPoint Cubic(SceneObject owner, VectorNode a, VectorNode b, double t)
    {
        var p0 = Map(owner, a.X, a.Y);
        var p1 = Map(owner, a.X + a.ControlOutX, a.Y + a.ControlOutY);
        var p2 = Map(owner, b.X + b.ControlInX, b.Y + b.ControlInY);
        var p3 = Map(owner, b.X, b.Y);
        var u = 1 - t;
        return new VectorPoint(
            (u * u * u * p0.X) + (3 * u * u * t * p1.X) + (3 * u * t * t * p2.X) + (t * t * t * p3.X),
            (u * u * u * p0.Y) + (3 * u * u * t * p1.Y) + (3 * u * t * t * p2.Y) + (t * t * t * p3.Y));
    }

    private static VectorPoint ToLocal(SceneObject owner, double x, double y)
    {
        var cx = owner.X + owner.Width / 2; var cy = owner.Y + owner.Height / 2;
        var radians = -owner.RotationDegrees * Math.PI / 180;
        var dx = x - cx; var dy = y - cy;
        var ux = (dx * Math.Cos(radians)) - (dy * Math.Sin(radians)) + owner.Width / 2;
        var uy = (dx * Math.Sin(radians)) + (dy * Math.Cos(radians)) + owner.Height / 2;
        return new VectorPoint(ux / owner.Width, uy / owner.Height);
    }

    private static bool InEllipse(IReadOnlyList<VectorNode> nodes, double x, double y)
    {
        var a = nodes[0]; var b = nodes[1];
        var cx = (a.X + b.X) / 2; var cy = (a.Y + b.Y) / 2;
        var rx = Math.Abs(b.X - a.X) / 2; var ry = Math.Abs(b.Y - a.Y) / 2;
        return rx > 0 && ry > 0 && Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2) <= 1;
    }

    private static bool InPolygon(IReadOnlyList<VectorNode> nodes, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = nodes.Count - 1; i < nodes.Count; j = i++)
        {
            var a = nodes[i]; var b = nodes[j];
            if ((a.Y > y) != (b.Y > y) && x < ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static double DistanceToSegment(double x, double y, VectorPoint a, VectorPoint b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var length2 = dx * dx + dy * dy;
        if (length2 == 0) return Math.Sqrt(Math.Pow(x - a.X, 2) + Math.Pow(y - a.Y, 2));
        var t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length2, 0, 1);
        return Math.Sqrt(Math.Pow(x - (a.X + t * dx), 2) + Math.Pow(y - (a.Y + t * dy), 2));
    }
}
