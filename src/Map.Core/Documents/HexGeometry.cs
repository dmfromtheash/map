namespace Map.Core.Documents;

/// <summary>Which way the hexes of a board point: flat top (columns) or pointy top (rows).</summary>
public enum HexOrientation
{
    /// <summary>A flat edge on top; the board is a run of vertical columns (odd columns shifted down).</summary>
    Flat,

    /// <summary>A corner on top; the board is a run of horizontal rows (odd rows shifted right).</summary>
    Pointy,
}

/// <summary>
/// One hex in axial coordinates (q, r). The third cube coordinate is s = −q − r. Axial
/// coordinates make every hex operation - neighbours, distance, rings, lines - plain arithmetic;
/// the board's rectangular extent and the grid numbering use <see cref="HexOffset"/> instead.
/// </summary>
public readonly record struct HexCoord(int Q, int R)
{
    /// <summary>The six neighbour steps, counter-clockwise from the east/south-east one.</summary>
    public static IReadOnlyList<HexCoord> Directions { get; } =
    [
        new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1),
    ];

    public int S => -Q - R;

    public static HexCoord operator +(HexCoord a, HexCoord b) => new(a.Q + b.Q, a.R + b.R);

    public static HexCoord operator -(HexCoord a, HexCoord b) => new(a.Q - b.Q, a.R - b.R);

    public HexCoord Neighbor(int direction) => this + Directions[((direction % 6) + 6) % 6];

    public IEnumerable<HexCoord> Neighbors()
    {
        foreach (var direction in Directions)
        {
            yield return this + direction;
        }
    }

    /// <summary>Steps between two hexes.</summary>
    public static int Distance(HexCoord a, HexCoord b)
    {
        var d = a - b;
        return (Math.Abs(d.Q) + Math.Abs(d.R) + Math.Abs(d.S)) / 2;
    }

    /// <summary>Every hex at most <paramref name="radius"/> steps away, the centre first.</summary>
    public static IEnumerable<HexCoord> Range(HexCoord center, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        yield return center;
        for (var ring = 1; ring <= radius; ring++)
        {
            foreach (var hex in Ring(center, ring))
            {
                yield return hex;
            }
        }
    }

    /// <summary>The hexes exactly <paramref name="radius"/> steps away (just the centre for 0).</summary>
    public static IEnumerable<HexCoord> Ring(HexCoord center, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        if (radius == 0)
        {
            yield return center;
            yield break;
        }

        var hex = new HexCoord(center.Q + Directions[4].Q * radius, center.R + Directions[4].R * radius);
        for (var side = 0; side < 6; side++)
        {
            for (var step = 0; step < radius; step++)
            {
                yield return hex;
                hex = hex.Neighbor(side);
            }
        }
    }

    /// <summary>The hexes a straight line from <paramref name="a"/> to <paramref name="b"/> crosses, both ends included.</summary>
    public static IEnumerable<HexCoord> Line(HexCoord a, HexCoord b)
    {
        var steps = Distance(a, b);
        if (steps == 0)
        {
            yield return a;
            yield break;
        }

        // A tiny nudge keeps a line that runs exactly along an edge on one consistent side.
        const double Nudge = 1e-6;
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            yield return RoundCube(
                a.Q + Nudge + (b.Q - a.Q) * t,
                a.R + Nudge + (b.R - a.R) * t,
                a.S - 2 * Nudge + (b.S - a.S) * t);
        }
    }

    /// <summary>The hex that contains a fractional cube position.</summary>
    public static HexCoord RoundCube(double q, double r, double s)
    {
        var rq = Math.Round(q);
        var rr = Math.Round(r);
        var rs = Math.Round(s);
        var dq = Math.Abs(rq - q);
        var dr = Math.Abs(rr - r);
        var ds = Math.Abs(rs - s);
        if (dq > dr && dq > ds)
        {
            rq = -rr - rs;
        }
        else if (dr > ds)
        {
            rr = -rq - rs;
        }

        return new HexCoord((int)rq, (int)rr);
    }

    public override string ToString() => $"{Q},{R}";
}

/// <summary>
/// A hex as column and row of the board's rectangle: odd-q for flat boards, odd-r for pointy
/// ones. This is what the extent, the chunks and the printed grid numbers ("0101") are written in.
/// </summary>
public readonly record struct HexOffset(int Column, int Row)
{
    public override string ToString() => $"{Column}:{Row}";
}

/// <summary>
/// Where the hexes of a board sit on the canvas: orientation, size and the centre of hex (0, 0).
/// Size is the circumradius - centre to corner - in canvas units.
/// </summary>
public sealed record HexLayout
{
    public static readonly double Sqrt3 = Math.Sqrt(3);

    public HexLayout(HexOrientation orientation, double size, double originX, double originY)
    {
        if (!double.IsFinite(size) || size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Hex size must be a positive finite number.");
        }

        if (!double.IsFinite(originX) || !double.IsFinite(originY))
        {
            throw new ArgumentException("Hex origin must be finite.");
        }

        Orientation = orientation;
        Size = size;
        OriginX = originX;
        OriginY = originY;
    }

    public HexOrientation Orientation { get; }

    public double Size { get; }

    public double OriginX { get; }

    public double OriginY { get; }

    /// <summary>Width of one hex's bounding box.</summary>
    public double HexWidth => Orientation == HexOrientation.Flat ? 2 * Size : Sqrt3 * Size;

    /// <summary>Height of one hex's bounding box.</summary>
    public double HexHeight => Orientation == HexOrientation.Flat ? Sqrt3 * Size : 2 * Size;

    public (double X, double Y) Center(HexCoord hex) => Orientation == HexOrientation.Flat
        ? (OriginX + Size * 1.5 * hex.Q, OriginY + Size * Sqrt3 * (hex.R + hex.Q / 2.0))
        : (OriginX + Size * Sqrt3 * (hex.Q + hex.R / 2.0), OriginY + Size * 1.5 * hex.R);

    /// <summary>The hex under a canvas point.</summary>
    public HexCoord HexAt(double x, double y)
    {
        var px = (x - OriginX) / Size;
        var py = (y - OriginY) / Size;
        double q, r;
        if (Orientation == HexOrientation.Flat)
        {
            q = 2.0 / 3 * px;
            r = -1.0 / 3 * px + Sqrt3 / 3 * py;
        }
        else
        {
            q = Sqrt3 / 3 * px - 1.0 / 3 * py;
            r = 2.0 / 3 * py;
        }

        return HexCoord.RoundCube(q, r, -q - r);
    }

    /// <summary>Corner <paramref name="index"/> (0..5) of a hex, clockwise on screen.</summary>
    public (double X, double Y) Corner(HexCoord hex, int index)
    {
        var (cx, cy) = Center(hex);
        var degrees = 60 * index - (Orientation == HexOrientation.Pointy ? 30 : 0);
        var radians = Math.PI / 180 * degrees;
        return (cx + Size * Math.Cos(radians), cy + Size * Math.Sin(radians));
    }

    /// <summary>The bounding box of one hex.</summary>
    public WorldBounds Bounds(HexCoord hex)
    {
        var (cx, cy) = Center(hex);
        return new WorldBounds(cx - HexWidth / 2, cy - HexHeight / 2, cx + HexWidth / 2, cy + HexHeight / 2);
    }

    public HexOffset ToOffset(HexCoord hex) => Orientation == HexOrientation.Flat
        ? new HexOffset(hex.Q, hex.R + (hex.Q - (hex.Q & 1)) / 2)
        : new HexOffset(hex.Q + (hex.R - (hex.R & 1)) / 2, hex.R);

    public HexCoord FromOffset(HexOffset offset) => Orientation == HexOrientation.Flat
        ? new HexCoord(offset.Column, offset.Row - (offset.Column - (offset.Column & 1)) / 2)
        : new HexCoord(offset.Column - (offset.Row - (offset.Row & 1)) / 2, offset.Row);
}
