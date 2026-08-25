using Map.Spike.Core.Geometry;

namespace Map.Spike.Core.Documents;

/// <summary>
/// Spike-only fixture object. Deliberately a plain geometric primitive: the spike must not
/// depend on any third-party artwork (AGENTS.md clean-implementation boundary).
/// </summary>
public enum SpikeObjectKind
{
    Square = 0,
    Circle = 1,
    Triangle = 2,
}

public sealed class SpikeObject
{
    public required ObjectId Id { get; init; }
    public required SpikeObjectKind Kind { get; init; }
    public required Vec2 Center { get; init; }
    public required double Size { get; init; }

    /// <summary>Rotation in radians, applied around <see cref="Center"/>.</summary>
    public required double Rotation { get; init; }

    /// <summary>Non-premultiplied 0xAARRGGBB.</summary>
    public required uint ColorArgb { get; init; }

    /// <summary>Axis-aligned world bounds, conservative for rotated shapes.</summary>
    public Rect2 Bounds
    {
        get
        {
            // Rotating a square of side S around its center needs at most S*sqrt(2).
            var extent = Kind == SpikeObjectKind.Circle ? Size : Size * 1.4142135623730951;
            return Rect2.FromCenter(Center, extent, extent);
        }
    }

    /// <summary>Precise containment test in world space, used after the spatial broad phase.</summary>
    public bool ContainsPoint(Vec2 world)
    {
        var half = Size / 2;
        switch (Kind)
        {
            case SpikeObjectKind.Circle:
                return (world - Center).Length <= half;

            case SpikeObjectKind.Square:
                {
                    var local = Unrotate(world);
                    return Math.Abs(local.X) <= half && Math.Abs(local.Y) <= half;
                }

            case SpikeObjectKind.Triangle:
                {
                    // Isosceles triangle inscribed in the local box: apex up, base at +half.
                    var local = Unrotate(world);
                    if (local.Y < -half || local.Y > half) return false;
                    // Half-width grows linearly from 0 at the apex to `half` at the base.
                    var t = (local.Y + half) / (2 * half);
                    var allowed = half * t;
                    return Math.Abs(local.X) <= allowed;
                }

            default:
                return false;
        }
    }

    private Vec2 Unrotate(Vec2 world)
    {
        var d = world - Center;
        if (Rotation == 0) return d;
        var cos = Math.Cos(-Rotation);
        var sin = Math.Sin(-Rotation);
        return new Vec2((d.X * cos) - (d.Y * sin), (d.X * sin) + (d.Y * cos));
    }
}
