namespace Map.Core.Documents;

/// <summary>Two local-space lattice vectors after subdivisions have been applied.</summary>
public readonly record struct GridBasis(VectorPoint U, VectorPoint V);

/// <summary>A finite world-space line primitive shared by viewport, preview and export.</summary>
public readonly record struct GridLineSegment(VectorPoint Start, VectorPoint End);

/// <summary>
/// The single mathematical contract for grid topology. Offset is removed before the inverse
/// rotation into local space; geometry and subdivisions are evaluated there; the common outer
/// rotation and offset are then applied on the way back to world space.
///
/// Isometric squares deliberately use the oblique basis
/// U=(cos(30°), sin(30°))*step and V=(cos(30°), -sin(30°))*step. Their two line families are
/// therefore approximately +30° and -30° and cannot collapse to Square + 45°.
/// </summary>
public static class GridGeometryContract
{
    public const int DefaultMaximumSegments = 4096;

    private const double Sqrt3 = 1.7320508075688772935;
    private const double Cos30 = 0.86602540378443864676;
    private const double Sin30 = 0.5;
    private const double ComparisonTolerance = 1e-12;

    public static GridBasis LocalBasis(GridDefinition grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var step = grid.CellSize / grid.Subdivisions;
        return grid.Geometry switch
        {
            GridGeometry.Square => new(new VectorPoint(step, 0), new VectorPoint(0, step)),
            GridGeometry.IsometricSquares => new(
                new VectorPoint(Cos30 * step, Sin30 * step),
                new VectorPoint(Cos30 * step, -Sin30 * step)),
            GridGeometry.HexFlat => new(
                new VectorPoint(1.5 * step, Sqrt3 / 2 * step),
                new VectorPoint(0, Sqrt3 * step)),
            GridGeometry.HexPointy => new(
                new VectorPoint(Sqrt3 * step, 0),
                new VectorPoint(Sqrt3 / 2 * step, 1.5 * step)),
            _ => throw new ArgumentOutOfRangeException(nameof(grid), "Unknown grid geometry."),
        };
    }

    /// <summary>A basis intersection transformed into world space. Used by deterministic fixtures.</summary>
    public static VectorPoint LatticePoint(GridDefinition grid, long u, long v)
    {
        var basis = LocalBasis(grid);
        return ToWorld(grid, Combine(basis, u, v));
    }

    /// <summary>Returns the nearest topology node with a stable local X/Y tie-break.</summary>
    public static VectorPoint Snap(GridDefinition grid, VectorPoint world)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!double.IsFinite(world.X) || !double.IsFinite(world.Y))
            throw new ArgumentOutOfRangeException(nameof(world));

        var local = ToLocal(grid, world);
        var snapped = grid.Geometry is GridGeometry.HexFlat or GridGeometry.HexPointy
            ? SnapHexVertex(grid, local)
            : SnapBasis(grid, local);
        return ToWorld(grid, snapped);
    }

    /// <summary>
    /// Produces bounded world-space primitives. A deterministic LOD stride limits work without a
    /// full-canvas bitmap or a loop proportional to the whole logical canvas.
    /// </summary>
    public static IReadOnlyList<GridLineSegment> VisibleSegments(
        GridDefinition grid, WorldBounds worldView, int maximumSegments = DefaultMaximumSegments)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegments);
        ValidateView(worldView);

        var localCorners = Corners(worldView).Select(point => ToLocal(grid, point)).ToArray();
        return grid.Geometry is GridGeometry.HexFlat or GridGeometry.HexPointy
            ? HexSegments(grid, localCorners, maximumSegments)
            : BasisSegments(grid, localCorners, maximumSegments);
    }

    private static VectorPoint SnapBasis(GridDefinition grid, VectorPoint local)
    {
        var basis = LocalBasis(grid);
        var coordinate = Coordinates(basis, local);
        var floorU = Math.Floor(coordinate.X);
        var floorV = Math.Floor(coordinate.Y);
        var best = default(VectorPoint);
        var bestDistance = double.PositiveInfinity;

        // A small oracle-sized neighbourhood is intentional. The isometric basis is skewed, so
        // independent component rounding is not a correct nearest-node algorithm.
        for (var du = -2; du <= 3; du++)
            for (var dv = -2; dv <= 3; dv++)
            {
                var candidate = Combine(basis, floorU + du, floorV + dv);
                if (IsBetter(local, candidate, best, bestDistance, out var distance))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

        return best;
    }

    private static VectorPoint SnapHexVertex(GridDefinition grid, VectorPoint local)
    {
        var step = grid.CellSize / grid.Subdivisions;
        var axial = HexAxial(grid.Geometry, step, local);
        var floorQ = Math.Floor(axial.X);
        var floorR = Math.Floor(axial.Y);
        var best = default(VectorPoint);
        var bestDistance = double.PositiveInfinity;

        for (var dq = -2; dq <= 3; dq++)
            for (var dr = -2; dr <= 3; dr++)
                for (var vertex = 0; vertex < 6; vertex++)
                {
                    var candidate = HexVertex(grid.Geometry, step, floorQ + dq, floorR + dr, vertex);
                    if (IsBetter(local, candidate, best, bestDistance, out var distance))
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }

        return best;
    }

    private static List<GridLineSegment> BasisSegments(
        GridDefinition grid, IReadOnlyList<VectorPoint> localCorners, int maximumSegments)
    {
        var basis = LocalBasis(grid);
        var coordinates = localCorners.Select(point => Coordinates(basis, point)).ToArray();
        var minU = coordinates.Min(point => point.X);
        var maxU = coordinates.Max(point => point.X);
        var minV = coordinates.Min(point => point.Y);
        var maxV = coordinates.Max(point => point.Y);
        var lineCount = Math.Max(1, Math.Ceiling(maxU - minU) + Math.Ceiling(maxV - minV) + 6);
        var stride = Math.Max(1, Math.Ceiling(lineCount / maximumSegments));
        var segments = new List<GridLineSegment>(Math.Min(maximumSegments, 256));

        var startV = Math.Floor((minV - 1) / stride) * stride;
        for (var v = startV; v <= maxV + 1 && segments.Count < maximumSegments; v += stride)
        {
            AddWorldSegment(grid, segments,
                Combine(basis, minU - 2, v), Combine(basis, maxU + 2, v));
            if (v + stride == v) break;
        }

        var startU = Math.Floor((minU - 1) / stride) * stride;
        for (var u = startU; u <= maxU + 1 && segments.Count < maximumSegments; u += stride)
        {
            AddWorldSegment(grid, segments,
                Combine(basis, u, minV - 2), Combine(basis, u, maxV + 2));
            if (u + stride == u) break;
        }

        return segments;
    }

    private static List<GridLineSegment> HexSegments(
        GridDefinition grid, IReadOnlyList<VectorPoint> localCorners, int maximumSegments)
    {
        var step = grid.CellSize / grid.Subdivisions;
        var axial = localCorners.Select(point => HexAxial(grid.Geometry, step, point)).ToArray();
        var minQ = axial.Min(point => point.X) - 2;
        var maxQ = axial.Max(point => point.X) + 2;
        var minR = axial.Min(point => point.Y) - 2;
        var maxR = axial.Max(point => point.Y) + 2;
        var cellBudget = Math.Max(1, maximumSegments / 6);
        var candidateCells = Math.Max(1, (maxQ - minQ + 1) * (maxR - minR + 1));
        var stride = Math.Max(1, Math.Ceiling(Math.Sqrt(candidateCells / cellBudget)));
        var startQ = Math.Floor(minQ / stride) * stride;
        var startR = Math.Floor(minR / stride) * stride;
        var segments = new List<GridLineSegment>(Math.Min(maximumSegments, 256));

        for (var q = startQ; q <= maxQ; q += stride)
        {
            for (var r = startR; r <= maxR; r += stride)
            {
                if (segments.Count + 6 > maximumSegments) return segments;
                for (var vertex = 0; vertex < 6; vertex++)
                {
                    AddWorldSegment(grid, segments,
                        HexVertex(grid.Geometry, step, q, r, vertex),
                        HexVertex(grid.Geometry, step, q, r, (vertex + 1) % 6));
                }
                if (r + stride == r) break;
            }
            if (q + stride == q) break;
        }

        return segments;
    }

    private static VectorPoint HexAxial(GridGeometry geometry, double step, VectorPoint point) => geometry switch
    {
        GridGeometry.HexFlat => new VectorPoint(
            (2.0 / 3 * point.X) / step,
            ((-point.X / 3) + (Sqrt3 / 3 * point.Y)) / step),
        GridGeometry.HexPointy => new VectorPoint(
            ((Sqrt3 / 3 * point.X) - (point.Y / 3)) / step,
            (2.0 / 3 * point.Y) / step),
        _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
    };

    private static VectorPoint HexVertex(
        GridGeometry geometry, double step, double q, double r, int vertex)
    {
        var centre = geometry switch
        {
            GridGeometry.HexFlat => new VectorPoint(1.5 * step * q, Sqrt3 * step * (r + q / 2)),
            GridGeometry.HexPointy => new VectorPoint(Sqrt3 * step * (q + r / 2), 1.5 * step * r),
            _ => throw new ArgumentOutOfRangeException(nameof(geometry)),
        };
        var angle = (geometry == GridGeometry.HexPointy ? -90 : 0) + vertex * 60;
        var radians = angle * Math.PI / 180;
        return new VectorPoint(centre.X + Math.Cos(radians) * step, centre.Y + Math.Sin(radians) * step);
    }

    private static VectorPoint Coordinates(GridBasis basis, VectorPoint point)
    {
        var determinant = basis.U.X * basis.V.Y - basis.U.Y * basis.V.X;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < double.Epsilon)
            throw new ArgumentOutOfRangeException(nameof(basis));
        return new VectorPoint(
            (point.X * basis.V.Y - point.Y * basis.V.X) / determinant,
            (basis.U.X * point.Y - basis.U.Y * point.X) / determinant);
    }

    private static VectorPoint Combine(GridBasis basis, double u, double v) => new(
        basis.U.X * u + basis.V.X * v,
        basis.U.Y * u + basis.V.Y * v);

    private static VectorPoint ToLocal(GridDefinition grid, VectorPoint world) =>
        Rotate(world.X - grid.OffsetX, world.Y - grid.OffsetY, -grid.RotationDegrees);

    private static VectorPoint ToWorld(GridDefinition grid, VectorPoint local)
    {
        var rotated = Rotate(local.X, local.Y, grid.RotationDegrees);
        return new VectorPoint(rotated.X + grid.OffsetX, rotated.Y + grid.OffsetY);
    }

    private static VectorPoint Rotate(double x, double y, double degrees)
    {
        var normalized = Math.IEEERemainder(degrees, 360);
        var radians = normalized * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new VectorPoint(x * cosine - y * sine, x * sine + y * cosine);
    }

    private static bool IsBetter(
        VectorPoint target, VectorPoint candidate, VectorPoint current,
        double currentDistance, out double candidateDistance)
    {
        var dx = candidate.X - target.X;
        var dy = candidate.Y - target.Y;
        candidateDistance = dx * dx + dy * dy;
        if (double.IsPositiveInfinity(currentDistance)) return true;
        var tolerance = Math.Max(1, currentDistance) * ComparisonTolerance;
        return candidateDistance < currentDistance - tolerance
            || Math.Abs(candidateDistance - currentDistance) <= tolerance
            && (candidate.X < current.X - ComparisonTolerance
                || Math.Abs(candidate.X - current.X) <= ComparisonTolerance && candidate.Y < current.Y);
    }

    private static IEnumerable<VectorPoint> Corners(WorldBounds bounds)
    {
        yield return new VectorPoint(bounds.Left, bounds.Top);
        yield return new VectorPoint(bounds.Right, bounds.Top);
        yield return new VectorPoint(bounds.Right, bounds.Bottom);
        yield return new VectorPoint(bounds.Left, bounds.Bottom);
    }

    private static void AddWorldSegment(
        GridDefinition grid, List<GridLineSegment> segments, VectorPoint localStart, VectorPoint localEnd) =>
        segments.Add(new GridLineSegment(ToWorld(grid, localStart), ToWorld(grid, localEnd)));

    private static void ValidateView(WorldBounds view)
    {
        if (!double.IsFinite(view.Left) || !double.IsFinite(view.Top)
            || !double.IsFinite(view.Right) || !double.IsFinite(view.Bottom)
            || view.Right < view.Left || view.Bottom < view.Top)
        {
            throw new ArgumentOutOfRangeException(nameof(view));
        }
    }
}
