using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// Version 4 builds a stationary arrangement of primary, supporting and fringe positions.
/// Cells only index those positions. Neither the polygon nor density participates in their
/// construction, so a boundary edit cannot move or reshuffle surviving objects.
/// </summary>
internal sealed class PlacementAreaLayout
{
    private const int CachedCells = 128;
    private const int CachedPositions = 32_768;
    private int _positionCount;
    private readonly PlacementArea _area;
    private readonly double _width, _height, _stepX, _stepY, _phase, _cos, _sin, _halfLength, _groundOffset;
    private readonly Dictionary<long, PlacementAreaComposition.Sample[]> _cells = [];
    private readonly Queue<long> _recent = new();

    public PlacementAreaLayout(PlacementArea area)
    {
        _area = area;
        (_width, _height) = Dimensions(area);
        _groundOffset = area.ContentMasks.Count > 0 && area.Pattern == PlacementAreaPattern.Ridge
            ? area.Variants.Average(v => area.Request.BaseWidth * v.PixelHeight / v.PixelWidth
                * (area.ContentBounds.GetValueOrDefault(v.Sha256, StampContentBounds.Full).Bottom - .5)) : 0;
        var maxScale = PlacementAreaComposition.MaximumScale(area);
        var silhouette = area.ContentMasks.Count > 0;
        _stepX = _width * maxScale * (area.JoinEdges ? silhouette ? .60 : .72 : 1.10);
        _stepY = _height * maxScale * (area.JoinEdges ? silhouette ? .30 : .43 : 1.10);
        if (silhouette && area.JoinEdges && area.Pattern != PlacementAreaPattern.Ridge)
        {
            // Wider size variation must not open a rectangular hole around every small tree.
            // The silhouette test, rather than the largest possible image, decides local packing.
            _stepX = _width * .70; _stepY = _height * .35;
        }
        _phase = Draw(0, 0, 71) * Math.Tau;
        var angle = area.CompositionAngleDegrees * Math.PI / 180;
        _cos = Math.Cos(angle); _sin = Math.Sin(angle);
        _halfLength = Math.Max(_stepX * 2, area.CompositionLength * .49);
    }

    private static (double Width, double Height) Dimensions(PlacementArea area)
    {
        var width = area.Variants.Average(v => area.Request.BaseWidth
            * area.ContentBounds.GetValueOrDefault(v.Sha256, StampContentBounds.Full).Width);
        var height = area.Variants.Average(v => area.Request.BaseWidth * v.PixelHeight / v.PixelWidth
            * area.ContentBounds.GetValueOrDefault(v.Sha256, StampContentBounds.Full).Height);
        return (Math.Max(1, width), Math.Max(1, height));
    }

    // A conservative cell population bound, used for admission and sampled previews only.
    // Commands and neighbour checks enumerate the actual cell positions, not this upper bound.
    public static int Passes(PlacementArea area)
    {
        var (width, height) = Dimensions(area);
        return (int)Math.Clamp(Math.Ceiling(12 * (100 / width + 3) * (100 / height + 3)), 1, int.MaxValue);
    }

    private double Draw(int x, int y, uint salt) => PlacementAreaPlanner.Unit(PlacementAreaPlanner.Mix(
        PlacementAreaPlanner.Random(_area, new(x, y, 719)) ^ salt));

    public PlacementAreaComposition.Sample At(PlacementAreaSlot slot)
    {
        var positions = Cell(slot.X, slot.Y);
        return slot.Ordinal >= 0 && slot.Ordinal < positions.Count ? positions[slot.Ordinal]
            : new(false, 1, ulong.MaxValue, 100, new(slot.X * 100 + 50, slot.Y * 100 + 50));
    }

    public IReadOnlyList<PlacementAreaComposition.Sample> Cell(int cellX, int cellY)
    {
        var key = new PlacementAreaSlot(cellX, cellY, 0).Cell;
        if (_cells.TryGetValue(key, out var cached)) return cached;
        var result = new List<PlacementAreaComposition.Sample>();
        if (_area.Pattern == PlacementAreaPattern.Ridge) Ridge(cellX, cellY, result);
        else Groves(cellX, cellY, result);
        // Stable local addresses are independent of density, polygon vertices and exclusions.
        var positions = result.OrderBy(p => p.Priority).ThenBy(p => p.Point!.Value.Y).ThenBy(p => p.Point!.Value.X).ToArray();
        while (_cells.Count > 0 && (_cells.Count >= CachedCells || _positionCount + positions.Length > CachedPositions))
        {
            var oldest = _recent.Dequeue(); _positionCount -= _cells[oldest].Length; _cells.Remove(oldest);
        }
        _positionCount += positions.Length;
        _recent.Enqueue(key); _cells.Add(key, positions);
        return positions;
    }

    private void Add(int cellX, int cellY, List<PlacementAreaComposition.Sample> result,
        double x, double y, double prominence, double activation, int role, int column, int row)
    {
        var scale = 1 + _area.SizeVariationPercent / 100 * Math.Clamp(prominence, -1, 1);
        // Mountain positions describe their ground line. Small foothills sit at the foot of a
        // larger peak instead of floating at the larger image's centre. The stored point is
        // still the actual image centre, as required by polygon membership and selection.
        var point = new VectorPoint(Math.Round(x), Math.Round(y + (1 - scale) * _groundOffset));
        if (PlacementAreaGeometry.Cell(point.X) != cellX || PlacementAreaGeometry.Cell(point.Y) != cellY) return;
        // Supporting objects yield to the composition's primary objects, wherever cell borders fall.
        var priority = (ulong)((role + Draw(column, row, 73)) * (ulong.MaxValue / 8d));
        result.Add(new(true, scale, priority, Math.Clamp(activation, 1, 100), point));
    }

    private void Groves(int cellX, int cellY, List<PlacementAreaComposition.Sample> result)
    {
        var origin = _area.CompositionOrigin;
        var left = cellX * 100 - origin.X; var top = cellY * 100 - origin.Y;
        // The smooth warp is bounded; only local lattice positions can land in this world cell.
        for (var row = (int)Math.Floor(top / _stepY) - 2; row <= (int)Math.Ceiling((top + 100) / _stepY) + 2; row++)
        for (var column = (int)Math.Floor(left / _stepX) - 2; column <= (int)Math.Ceiling((left + 100) / _stepX) + 2; column++)
        {
            var x = (column + .5 * (row & 1) + .55 * Math.Sin(row * .38 + _phase)
                + .26 * (Draw(column, row, 1) - .5)) * _stepX;
            var y = (row + .52 * Math.Sin(column * .41 - _phase)
                + .32 * (Draw(column, row, 2) - .5)) * _stepY;
            var strength = GroveStrength(x / _width, y / _height);
            if (strength <= .06 + .06 * Draw(column, row, 3)) continue;
            strength = Math.Clamp(strength, 0, 1);
            var prominence = 1.5 * strength - 1 + .5 * Draw(column, row, 4);
            var activation = 100 * (.90 * (1 - strength) + .10 * Draw(column, row, 5));
            Add(cellX, cellY, result, origin.X + x, origin.Y + y, prominence, activation,
                strength > .65 ? 0 : strength > .3 ? 1 : 2, column, row);
        }
    }

    private double GroveStrength(double x, double y)
    {
        var mass = _area.Pattern == PlacementAreaPattern.Masses;
        var clearings = _area.Pattern == PlacementAreaPattern.Clearings;
        var stepX = mass || clearings ? 9.0 : 6.2; var stepY = mass || clearings ? 7.0 : 5.0;
        var cellX = (int)Math.Floor(x / stepX + .5); var cellY = (int)Math.Floor(y / stepY + .5);
        var strength = 0d;
        for (var cy = cellY - 1; cy <= cellY + 1; cy++)
        for (var cx = cellX - 1; cx <= cellX + 1; cx++)
        {
            var dx = x - cx * stepX - (cx == 0 && cy == 0 ? 0 : (Draw(cx, cy, 11) - .5) * 2.0);
            var dy = y - cy * stepY - (cx == 0 && cy == 0 ? 0 : (Draw(cx, cy, 12) - .5) * 1.8);
            var angle = (Draw(cx, cy, 13) - .5) * 1.5;
            var u = dx * Math.Cos(angle) + dy * Math.Sin(angle);
            var v = -dx * Math.Sin(angle) + dy * Math.Cos(angle);
            v += .6 * Math.Sin(u * .62 + Draw(cx, cy, 14) * Math.Tau);
            var a = mass || clearings ? 5.0 + Draw(cx, cy, 15) : 2.5 + Draw(cx, cy, 15) * .7;
            var b = mass || clearings ? 3.5 + Draw(cx, cy, 16) : 1.6 + Draw(cx, cy, 16) * .5;
            b *= 1 + .22 * Math.Sin(u * 1.25 + Draw(cx, cy, 17) * Math.Tau);
            var radius = Math.Sqrt(u * u / (a * a) + v * v / (b * b));
            var local = 1 - radius * radius;
            if (clearings)
            {
                // A courtyard/clearing with two curved openings. Density fills its surrounding
                // groups, never the reserved open centre or the routes through it.
                var opening = Math.Abs(v + .32 * Math.Sin(u * .6 + _phase));
                local = Math.Min(1 - Math.Abs(radius - .70) / .26, (opening - .55) * 1.4);
            }
            strength = Math.Max(strength, local);
        }
        return strength;
    }

    private double Spine(double u) => _height * (.65 * Math.Sin(u / (_width * 2.6) + _phase)
        + .30 * Math.Sin(u / (_width * 1.3) - _phase));

    private void Ridge(int cellX, int cellY, List<PlacementAreaComposition.Sample> result)
    {
        if (_area.ContentMasks.Count > 0) { ShapedRidge(cellX, cellY, result); return; }
        var origin = _area.CompositionOrigin;
        var centerX = cellX * 100 + 50 - origin.X; var centerY = cellY * 100 + 50 - origin.Y;
        var projected = centerX * _cos + centerY * _sin;
        // Five staggered ranks: the connected backbone, its shoulders and small fringe objects.
        var first = (int)Math.Floor((projected - 72) / _stepX) - 2;
        var last = (int)Math.Ceiling((projected + 72) / _stepX) + 2;
        for (var row = -2; row <= 2; row++)
        for (var column = first; column <= last; column++)
        {
            var rank = Math.Abs(row);
            var u = (column + (rank == 0 ? 0 : .5) + (rank == 0 ? .12 : .35)
                * (Draw(column, row, 21) - .5)) * _stepX;
            var end = Math.Abs(u) / _halfLength;
            if (end > 1 || rank == 2 && Draw(column, row, 22) < .25 + .15 * Math.Sin(column * .9 + _phase)) continue;
            var prominence = rank == 0 ? .45 + .50 * Math.Cos(column * .93 + _phase)
                : rank == 1 ? -.20 + .25 * Math.Cos(column * .75 + _phase) : -.85 + .15 * Draw(column, row, 23);
            prominence -= Math.Max(0, end - .65) * 1.3;
            var spacing = _height * (PlacementAreaComposition.MaximumScale(_area)) * (_area.JoinEdges ? .42 : 1.05);
            var v = Spine(u) + Math.Sign(row) * (rank == 2 ? 1.7 : rank) * spacing * (1 - .18 * end)
                + (rank == 0 ? .08 : .22) * _height * (Draw(column, row, 24) - .5);
            var activation = rank == 0 ? 4 + 18 * Draw(column, row, 25)
                : rank == 1 ? 25 + 35 * Draw(column, row, 25) : 62 + 36 * Draw(column, row, 25);
            Add(cellX, cellY, result, origin.X + u * _cos - v * _sin,
                origin.Y + u * _sin + v * _cos, prominence, activation, rank, column, row);
        }
        // Short, tapering arms grow out of the same backbone, rather than becoming distant islands.
        for (var branch = -1; branch <= 1; branch += 2)
        {
            var root = _halfLength * branch * .3;
            var length = Math.Min(4, _halfLength / _stepX * .6);
            for (var i = 1; i <= Math.Ceiling(length); i++)
            {
                var t = i / length;
                var u = root + i * _stepX * (.45 + Draw(branch, 0, 31) * .2);
                var v = Spine(root) + branch * _height * (i * .57 + .17 * Math.Sin(i));
                Add(cellX, cellY, result, origin.X + u * _cos - v * _sin,
                    origin.Y + u * _sin + v * _cos, -.25 - .65 * t,
                    30 + 50 * t, 3, i, branch * 7);
            }
        }
    }

    private void ShapedRidge(int cellX, int cellY, List<PlacementAreaComposition.Sample> result)
    {
        var origin = _area.CompositionOrigin;
        var centerX = cellX * 100 + 50 - origin.X; var centerY = cellY * 100 + 50 - origin.Y;
        var projected = centerX * _cos + centerY * _sin;
        var reach = 72 + Math.Abs(_sin * _groundOffset) * _area.SizeVariationPercent / 100;
        var first = (int)Math.Floor((projected - reach) / _stepX) - 2;
        var last = (int)Math.Ceiling((projected + reach) / _stepX) + 2;
        for (var column = first; column <= last; column++)
        {
            var u = (column + .18 * (Draw(column, 0, 81) - .5)) * _stepX;
            var end = Math.Abs(u) / _halfLength;
            if (end > 1) continue;
            var taper = Math.Max(0, end - .64) * 1.8;
            // A backbone of unequal dominant peaks; neighbours are local fans, not parallel ranks.
            var prominence = .25 + .65 * Draw(column / 2, 0, 82) + .1 * Math.Cos(column * 1.8 + _phase) - taper;
            Place(u, Spine(u), prominence, 3 + 17 * Draw(column, 0, 83), 0, column, 0);
            for (var side = -1; side <= 1; side += 2)
            {
                var shoulderU = u + _stepX * (.35 + .28 * Draw(column, side, 84));
                var shoulderV = Spine(shoulderU) + side * _height * (.26 + .14 * Draw(column, side, 85));
                Place(shoulderU, shoulderV, -.35 + .40 * Draw(column, side, 86) - taper,
                    24 + 34 * Draw(column, side, 87), 1, column, side);
                // Foothills belong to a particular shoulder. Some shoulders have a fan of two,
                // some have none, producing alternating broad and narrow sections of the range.
                if (Draw(column, side, 88) < .22) continue;
                var count = Draw(column, side, 89) > .55 ? 2 : 1;
                for (var i = 0; i < count; i++)
                {
                    var fringeU = shoulderU + _stepX * (i * .38 - .12 + .2 * Draw(column, side * (i + 3), 90));
                    var fringeV = shoulderV + side * _height * (.24 + .14 * Draw(column, side * (i + 3), 91));
                    Place(fringeU, fringeV, -.85 - .15 * Draw(column, side * (i + 3), 92),
                        60 + 39 * Draw(column, side * (i + 3), 93), 2, column, side * (i + 3));
                }
            }
        }
        for (var branch = -1; branch <= 1; branch += 2)
        {
            var root = _halfLength * branch * (.2 + .15 * Draw(branch, 0, 94));
            var length = Math.Min(5, _halfLength / _stepX * .70);
            var distance = 0d; var previousScale = 1d;
            for (var i = 1; i <= Math.Ceiling(length); i++)
            {
                var size = Math.Clamp(.40 - 1.15 * i / length, -1, 1);
                var scale = 1 + _area.SizeVariationPercent / 100 * size;
                distance += .46 * (previousScale + scale) / 2; previousScale = scale;
                var u = root + distance * _stepX;
                var v = Spine(root) + branch * _height * (distance + .08 * Math.Sin(i));
                Place(u, v, size, 25 + 40 * i / length, 1, i, branch * 8);
            }
        }
        void Place(double u, double v, double size, double activation, int role, int column, int row) =>
            Add(cellX, cellY, result, origin.X + u * _cos - v * _sin,
                origin.Y + u * _sin + v * _cos, size, activation, role, column, row);
    }
}
