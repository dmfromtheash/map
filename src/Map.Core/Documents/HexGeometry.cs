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

    /// <summary>
    /// This hex turned around <paramref name="center"/> by <paramref name="steps"/> × 60° clockwise on
    /// screen (y down) - the same sense as <see cref="HexCell.Rotation"/>; negative steps turn back.
    /// </summary>
    public HexCoord RotatedAround(HexCoord center, int steps)
    {
        var d = this - center;
        int q = d.Q, r = d.R, s = d.S;
        for (var i = 0; i < ((steps % 6) + 6) % 6; i++)
        {
            // (q, r, s) → (−r, −s, −q): Directions[i] becomes Directions[i − 1], a clockwise turn.
            (q, r, s) = (-r, -s, -q);
        }

        return new HexCoord(center.Q + q, center.R + r);
    }

    /// <summary>This hex mirrored left-right on screen across the vertical line through <paramref name="center"/>.</summary>
    public HexCoord MirroredAround(HexCoord center, HexOrientation orientation)
    {
        var d = this - center;
        // Flat: x = 1.5q, so q → −q and r → r + q. Pointy: x = √3(q + r/2), so q → −q − r.
        return orientation == HexOrientation.Flat
            ? new HexCoord(center.Q - d.Q, center.R + d.R + d.Q)
            : new HexCoord(center.Q + d.S, center.R + d.R);
    }

    /// <summary>The direction (0..5) from this hex to a neighbour, or -1 when it is not a neighbour.</summary>
    public int DirectionTo(HexCoord neighbour)
    {
        var d = neighbour - this;
        for (var i = 0; i < 6; i++)
        {
            if (Directions[i] == d) return i;
        }

        return -1;
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

    /// <summary>The middle of the edge between a hex and its neighbour in <paramref name="direction"/>.</summary>
    public (double X, double Y) EdgeMiddle(HexCoord hex, int direction)
    {
        var (ax, ay) = Center(hex);
        var (bx, by) = Center(hex.Neighbor(direction));
        return ((ax + bx) / 2, (ay + by) / 2);
    }

    /// <summary>
    /// The screen angle (degrees, y down) from a hex centre to the neighbour in a direction. Used to
    /// turn a tile picture so that a side of its art faces a neighbour.
    /// </summary>
    public double DirectionAngle(int direction)
    {
        var (ax, ay) = Center(new HexCoord(0, 0));
        var (bx, by) = Center(new HexCoord(0, 0).Neighbor(direction));
        return Math.Atan2(by - ay, bx - ax) * 180 / Math.PI;
    }

    /// <summary>The canvas point of a hex corner.</summary>
    public (double X, double Y) Position(HexVertex vertex) => Corner(vertex.Hex, vertex.Corner);

    /// <summary>
    /// The one stored form of a corner: of the three hexes that meet there, the smallest by (q, r).
    /// Depends only on the orientation, so a moved or resized board keeps its corners.
    /// </summary>
    public HexVertex Canonical(HexVertex vertex)
    {
        var best = vertex with { Corner = ((vertex.Corner % 6) + 6) % 6 };
        foreach (var other in Around(best))
        {
            if (other.Hex.Q < best.Hex.Q || (other.Hex.Q == best.Hex.Q && other.Hex.R < best.Hex.R)) best = other;
        }

        return best;
    }

    /// <summary>The three corners joined to this one by an edge, in canonical form.</summary>
    public IReadOnlyList<HexVertex> VertexNeighbors(HexVertex vertex)
    {
        var self = Canonical(vertex);
        var result = new List<HexVertex>(3);
        foreach (var form in Around(self).Prepend(self))
        {
            foreach (var step in (ReadOnlySpan<int>)[1, 5])
            {
                var next = Canonical(new HexVertex(form.Hex, (form.Corner + step) % 6));
                if (next != self && !result.Contains(next)) result.Add(next);
            }
        }

        return result;
    }

    /// <summary>The corner of a hex nearest a canvas point.</summary>
    public HexVertex NearestVertex(double x, double y)
    {
        var hex = HexAt(x, y);
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < 6; i++)
        {
            var (cx, cy) = Corner(hex, i);
            var distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return Canonical(new HexVertex(hex, best));
    }

    /// <summary>The same corner as seen from the two other hexes that meet there.</summary>
    private IEnumerable<HexVertex> Around(HexVertex vertex)
    {
        // A unit layout of the same orientation: corners are compared in hex units, so the canvas
        // size and origin of this layout never change a corner's identity.
        var unit = Orientation == HexOrientation.Flat ? UnitFlat : UnitPointy;
        var (vx, vy) = unit.Corner(vertex.Hex, vertex.Corner);
        foreach (var neighbour in vertex.Hex.Neighbors())
        {
            for (var i = 0; i < 6; i++)
            {
                var (x, y) = unit.Corner(neighbour, i);
                if (Math.Abs(x - vx) < 1e-6 && Math.Abs(y - vy) < 1e-6)
                {
                    yield return new HexVertex(neighbour, i);
                }
            }
        }
    }

    private static readonly HexLayout UnitFlat = new(HexOrientation.Flat, 1, 0, 0);

    private static readonly HexLayout UnitPointy = new(HexOrientation.Pointy, 1, 0, 0);

    public HexOffset ToOffset(HexCoord hex) => Orientation == HexOrientation.Flat
        ? new HexOffset(hex.Q, hex.R + (hex.Q - (hex.Q & 1)) / 2)
        : new HexOffset(hex.Q + (hex.R - (hex.R & 1)) / 2, hex.R);

    public HexCoord FromOffset(HexOffset offset) => Orientation == HexOrientation.Flat
        ? new HexCoord(offset.Column, offset.Row - (offset.Column - (offset.Column & 1)) / 2)
        : new HexCoord(offset.Column - (offset.Row - (offset.Row & 1)) / 2, offset.Row);
}
