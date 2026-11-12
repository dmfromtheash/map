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
        _groundOffset = area.ContentMasks.Count > 0 && area.Pattern is PlacementAreaPattern.Ridge or PlacementAreaPattern.Peaks
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
        if (_area.Pattern == PlacementAreaPattern.Peaks) Massif(cellX, cellY, result);
        else if (_area.Pattern == PlacementAreaPattern.Ridge && _area.LayoutVersion >= 5) Range(cellX, cellY, result);
        else if (_area.Pattern == PlacementAreaPattern.Ridge) Ridge(cellX, cellY, result);
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

    // ------------------------------------------------------------------ layout 5 (G1, 2026-11-12)
    // Mountains in size classes, as ~1 800 reference maps place them: big peaks along a smooth spine, never on its
    // edge; mid peaks between and beside them; hills at the foot and the ends; a lower parallel range beyond a valley;
    // massifs of short ranges, each a dominant peak with satellites. The class of a peak follows its place in the
    // range, not chance. Density adds them in that order: spine, shoulders, parallel ranges, foothills.

    private const double Big = 1, Mid = .62, Hill = .34;

    /// <summary>A peak of class <paramref name="tier"/>; the size variation only jitters within the class.</summary>
    private void Put(int cellX, int cellY, List<PlacementAreaComposition.Sample> result,
        double x, double y, double tier, double activation, int role, int column, int row, uint salt)
    {
        var jitter = _area.SizeVariationPercent / 100 * .5 * (2 * Draw(column, row, 300 + salt) - 1);
        var scale = Math.Max(.2, tier * (1 + jitter));
        // The stored point is the image centre; the ground line stays where a full-size peak would stand.
        var point = new VectorPoint(Math.Round(x), Math.Round(y + (1 - scale) * _groundOffset));
        if (PlacementAreaGeometry.Cell(point.X) != cellX || PlacementAreaGeometry.Cell(point.Y) != cellY) return;
        // Bigger classes claim their place first: a hill never pushes a peak out of the range.
        var priority = (ulong)((role + .6 * (1 - Math.Min(1, tier)) + .4 * Draw(column, row, 400 + salt)) * (ulong.MaxValue / 8d));
        result.Add(new(true, scale, priority, Math.Clamp(activation, 1, 100), point));
    }

    private void Range(int cellX, int cellY, List<PlacementAreaComposition.Sample> result)
    {
        var origin = _area.CompositionOrigin;
        double w = _width, h = _height;
        var centerX = cellX * 100 + 50 - origin.X; var centerY = cellY * 100 + 50 - origin.Y;
        var projected = centerX * _cos + centerY * _sin;
        var step = w * .55;
        var reach = 72 + w * 2.0;
        var first = (int)Math.Floor((projected - reach) / step) - 1;
        var last = (int)Math.Ceiling((projected + reach) / step) + 1;
        void At(double u, double v, double tier, double activation, int role, int column, int row, uint salt) =>
            Put(cellX, cellY, result, origin.X + u * _cos - v * _sin, origin.Y + u * _sin + v * _cos, tier, activation, role, column, row, salt);

        for (var k = first; k <= last; k++)
        {
            // The spine: big peaks every other step in the middle, mid ones between, hills where the range ends.
            var u = (k + .2 * (Draw(k, 0, 101) - .5)) * step;
            var end = Math.Abs(u) / _halfLength;
            if (end > 1.12) continue;
            var central = Math.Clamp(1 - Math.Pow(end, 1.7), 0, 1);
            // The middle of the range is a continuous chain of big peaks, each overlapping the next; toward the ends mid
            // peaks, then hills. Now and then a mid peak breaks the chain, so the crest steps up and down.
            var tier = end > 1 ? Hill : central > .5 ? (Draw(k, 0, 102) < .82 ? Big : Mid) : central > .18 ? Mid : Hill;
            // Each peak shifts to the other side of the spine (the ridgeline zigzags behind them); a smaller peak comes
            // forward of its bigger neighbours, its tip against their feet — behind them it would be hidden.
            var side = tier == Big ? ((k & 1) == 0 ? -1 : 1) : 1;
            var v = Spine(u) + side * (tier == Big ? .1 + .12 * Draw(k, 0, 103) : .16 + .14 * Draw(k, 0, 103)) * h;
            At(u, v, tier, tier == Big ? 2 + 14 * Draw(k, 0, 104) : tier == Mid ? 10 + 22 * Draw(k, 0, 104) : 40 + 30 * Draw(k, 0, 104), 0, k, 0, 1);
            for (var s = -1; s <= 1; s += 2)
            {
                // Shoulders one class lower on both flanks; then a group of 1–4 hills at the foot.
                if (Draw(k, s, 105) < .1 || tier == Hill && Draw(k, s, 105) < .5) continue;
                // The front shoulder stands at the foot; the back one steps aside so its tip shows past the slope.
                var su = s > 0 ? u + (Draw(k, s, 106) - .5) * .7 * w
                    : u + (Draw(k, s, 106) < .5 ? -1 : 1) * (.42 + .2 * Draw(k, s, 117)) * w;
                var sv = v + s * (s > 0 ? .36 + .14 * Draw(k, s, 107) : .2 + .1 * Draw(k, s, 107)) * h * (tier == Big ? 1 : .75);
                var shoulder = tier == Big ? Mid : Hill;
                At(su, sv, shoulder, 22 + 26 * Draw(k, s, 108), 1, k, s, 2);
                // A big peak has a second, lower shoulder further out: the range is broad in its middle.
                if (tier == Big && Draw(k, s, 117) < .7)
                    At(su + (Draw(k, s, 118) - .5) * .5 * w, sv + s * (.3 + .1 * Draw(k, s, 119)) * h, Hill * 1.15,
                        38 + 20 * Draw(k, s, 120), 2, k, s * 2, 6);
                if (Draw(k, s, 109) < .2) continue;
                // «Go crazy on the smaller hills»: groups of 2–5 at the foot, of uneven sizes.
                var count = 2 + (int)(Draw(k, s, 110) * 3.99);
                var fu = su + (Draw(k, s, 111) - .5) * .8 * w;
                var fv = sv + s * (.28 + .22 * Draw(k, s, 112)) * h * (shoulder == Mid ? 1 : .75);
                for (var i = 0; i < count; i++)
                    At(fu + (i - (count - 1) / 2.0) * .32 * w + (Draw(k, s * (i + 3), 113) - .5) * .15 * w,
                        fv + s * (Draw(k, s * (i + 3), 114) - .5) * .3 * h, Hill * (.75 + .35 * Draw(k, s * (i + 3), 115)),
                        52 + 44 * Draw(k, s * (i + 3), 116), 3, k, s * (i + 3), 3);
            }
            // A lower parallel range on each side, beyond a valley, shifted along the axis (en échelon).
            for (var p = -1; p <= 1; p += 2)
            {
                var shift = p * _halfLength * (.18 + .12 * Draw(p, 0, 121));
                var half = _halfLength * (.5 + .15 * Draw(p, 0, 123));
                var pu = (k + .5 + .25 * (Draw(k, p * 9, 124) - .5)) * step;
                var pe = Math.Abs(pu - shift) / half;
                if (pe > 1) continue;
                var pc = 1 - Math.Pow(pe, 1.6);
                var ptier = pc > .45 && (k & 1) == 0 ? Mid : Hill;
                var pv = .7 * Spine(pu) + p * (1.45 + .25 * Draw(p, 0, 122)) * h + ((k & 1) == 0 ? .1 : -.1) * h;
                At(pu, pv, ptier, ptier == Mid ? 34 + 26 * Draw(k, p * 9, 125) : 60 + 30 * Draw(k, p * 9, 125), 2, k, p * 9, 4);
                if (ptier == Mid && Draw(k, p * 9, 126) > .5)
                    At(pu + (Draw(k, p * 9, 127) - .5) * .6 * w, pv + p * (.4 + .3 * Draw(k, p * 9, 128)) * h, Hill,
                        72 + 26 * Draw(k, p * 9, 129), 3, k, p * 9, 5);
            }
        }
    }

    private void Massif(int cellX, int cellY, List<PlacementAreaComposition.Sample> result)
    {
        var origin = _area.CompositionOrigin;
        double w = _width, h = _height;
        var gx = w * 2.5; var gy = h * 1.8;
        var lx = cellX * 100 + 50 - origin.X; var ly = cellY * 100 + 50 - origin.Y;
        var reach = 72 + w * 2.2;
        for (var row = (int)Math.Floor((ly - reach) / gy) - 1; row <= (int)Math.Ceiling((ly + reach) / gy) + 1; row++)
        for (var col = (int)Math.Floor((lx - reach) / gx) - 1; col <= (int)Math.Ceiling((lx + reach) / gx) + 1; col++)
        {
            // Some cluster sites stay empty: valleys and passes between the short ranges.
            if (Draw(col, row, 203) < .1) continue;
            var cx = (col + .5 * (row & 1) + .5 * (Draw(col, row, 201) - .5)) * gx;
            var cy = (row + .45 * (Draw(col, row, 202) - .5)) * gy;
            var strength = .35 + .65 * Draw(col, row, 205);
            var angle = (Draw(col, row, 204) - .5) * .9;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var count = 4 + (int)(Draw(col, row, 206) * 3.99);
            var salt = 0u;
            void At(double x, double y, double tier, double activation, int role) =>
                Put(cellX, cellY, result, origin.X + x, origin.Y + y, tier, activation, role, col, row, ++salt * 17);
            for (var i = 0; i < count; i++)
            {
                // A short range: the dominant peak in its middle, smaller ones toward its ends.
                var t = i - (count - 1) / 2.0;
                var along = t * .46 * w * (1 + .12 * Draw(col, row, 210 + (uint)i));
                var tier = Math.Abs(t) < .6 ? (strength > .55 ? Big : Mid) : Math.Abs(t) < 1.6 ? Mid : Math.Abs(t) < 2.6 ? Mid * .8 : Hill;
                // The dominant peak stands back; the smaller ones of its range come forward to its feet.
                var across = (Math.Abs(t) < .6 ? -.1 : .12 + .1 * Math.Abs(t)) * h + (Draw(col, row, 220 + (uint)i) - .5) * .1 * h;
                At(cx + along * cos - across * sin, cy + along * sin + across * cos, tier,
                    tier == Big ? 3 + 20 * (1 - strength) : tier == Mid ? 14 + 26 * (1 - strength) + 6 * Math.Abs(t) : 42 + 20 * Draw(col, row, 230 + (uint)i), Math.Abs(t) < .6 ? 0 : 1);
            }
            var satellites = 4 + (int)(Draw(col, row, 207) * 4.99);
            for (var j = 0; j < satellites; j++)
            {
                // Satellites around the range, flattened in depth as the 3/4 view sees the ground.
                // In front and to the sides: a satellite behind the range would only show its tip, if anything.
                var a = (-.15 + 1.3 * Draw(col, row, 240 + (uint)j)) * Math.PI;
                var r = (.4 + .5 * Draw(col, row, 250 + (uint)j)) * w;
                At(cx + Math.Cos(a) * r * 1.2, cy + Math.Sin(a) * r * .6 * h / w, r < .7 * w ? Mid * .8 : Hill * 1.1,
                    34 + 40 * Draw(col, row, 260 + (uint)j), 2);
            }
            var foothills = 3 + (int)(Draw(col, row, 208) * 4.99);
            for (var j = 0; j < foothills; j++)
            {
                var a = Draw(col, row, 270 + (uint)j) * Math.Tau;
                var r = (1.0 + .45 * Draw(col, row, 280 + (uint)j)) * w;
                At(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r * .6 * h / w, Hill * (.8 + .25 * Draw(col, row, 290 + (uint)j)),
                    66 + 32 * Draw(col, row, 295 + (uint)j), 3);
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
