namespace Map.Core.Editing;

/// <summary>
/// The no-overlap box of one placed stamp: a rectangle around the stamp's centre, a fraction of
/// its size, turned with the stamp. Two stamps "overlap" when these boxes intersect, so the
/// pictures themselves may still touch at the edges, which keeps a scatter dense but readable.
/// </summary>
public readonly record struct StampCollisionBox(
    double CenterX, double CenterY, double HalfWidth, double HalfHeight, double RotationDegrees)
{
    /// <summary>Half of the axis-aligned extent, used by the coarse grid.</summary>
    public double Reach => Math.Sqrt((HalfWidth * HalfWidth) + (HalfHeight * HalfHeight));
}

public static class StampCollision
{
    /// <summary>Box side as a share of the stamp side when the asset passport does not say otherwise.</summary>
    public const double DefaultFraction = 0.3;

    public const double MinFraction = 0.05;
    public const double MaxFraction = 1;

    public static double ClampFraction(double? fraction) =>
        fraction is { } value && double.IsFinite(value) ? Math.Clamp(value, MinFraction, MaxFraction) : DefaultFraction;

    /// <summary>The box of a planned stamp; <paramref name="extraRotation"/> is the tool rotation added after planning.</summary>
    public static StampCollisionBox For(StampPlacementItem item, double fraction, double extraRotation = 0) =>
        Box(item.X + (item.Width / 2), item.Y + (item.Height / 2), item.Width, item.Height,
            item.RotationDegrees + extraRotation, fraction);

    public static StampCollisionBox Box(
        double centerX, double centerY, double width, double height, double rotationDegrees, double fraction)
    {
        var f = ClampFraction(fraction);
        return new(centerX, centerY, Math.Abs(width) * f / 2, Math.Abs(height) * f / 2, rotationDegrees);
    }

    /// <summary>Separating-axis test of two turned rectangles; touching edges do not count.</summary>
    public static bool Overlaps(StampCollisionBox a, StampCollisionBox b)
    {
        var dx = b.CenterX - a.CenterX;
        var dy = b.CenterY - a.CenterY;
        var reach = a.Reach + b.Reach;
        if ((dx * dx) + (dy * dy) >= reach * reach) return false;
        Span<(double X, double Y)> axes =
        [
            Axis(a.RotationDegrees, 0), Axis(a.RotationDegrees, 90),
            Axis(b.RotationDegrees, 0), Axis(b.RotationDegrees, 90),
        ];
        foreach (var (x, y) in axes)
        {
            var distance = Math.Abs((dx * x) + (dy * y));
            if (distance >= Projection(a, x, y) + Projection(b, x, y) - 1e-9) return false;
        }
        return true;
    }

    private static (double X, double Y) Axis(double degrees, double offset)
    {
        var radians = (degrees + offset) * Math.PI / 180;
        return (Math.Cos(radians), Math.Sin(radians));
    }

    private static double Projection(StampCollisionBox box, double x, double y)
    {
        var (ux, uy) = Axis(box.RotationDegrees, 0);
        var (vx, vy) = Axis(box.RotationDegrees, 90);
        return (box.HalfWidth * Math.Abs((ux * x) + (uy * y))) + (box.HalfHeight * Math.Abs((vx * x) + (vy * y)));
    }
}

/// <summary>A coarse uniform grid of collision boxes so a dense gesture tests only its neighbours.</summary>
public sealed class StampCollisionGrid
{
    private readonly Dictionary<(long X, long Y), List<StampCollisionBox>> _cells = [];
    private readonly double _cell;

    public StampCollisionGrid(double cellSize) => _cell = Math.Max(1, cellSize);

    public int Count { get; private set; }

    public void Add(StampCollisionBox box)
    {
        var (x0, y0, x1, y1) = Range(box);
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        {
            if (!_cells.TryGetValue((x, y), out var list)) _cells[(x, y)] = list = [];
            list.Add(box);
        }
        Count++;
    }

    public bool Collides(StampCollisionBox box)
    {
        var (x0, y0, x1, y1) = Range(box);
        for (var x = x0; x <= x1; x++)
        for (var y = y0; y <= y1; y++)
        {
            if (!_cells.TryGetValue((x, y), out var list)) continue;
            foreach (var other in list)
                if (StampCollision.Overlaps(box, other)) return true;
        }
        return false;
    }

    private (long, long, long, long) Range(StampCollisionBox box)
    {
        var reach = box.Reach;
        return ((long)Math.Floor((box.CenterX - reach) / _cell), (long)Math.Floor((box.CenterY - reach) / _cell),
                (long)Math.Floor((box.CenterX + reach) / _cell), (long)Math.Floor((box.CenterY + reach) / _cell));
    }
}
