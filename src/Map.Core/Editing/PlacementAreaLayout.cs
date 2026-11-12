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

    // Measured on the references: heights about 1 : 0.52–0.62 : 0.29–0.34.
    private const double Big = 1, Mid = .56, Hill = .3;

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

    /// <summary>
    /// A range as reference maps draw it: a band of mountains along a smooth axis, packed so each nearer peak hides the
    /// foot of the one behind. A peak's class follows its place in the band — big on the crest in the middle of the
    /// length, mid on the flanks and toward the ends, hills at the band's edge and beyond it in loose groups. The seed
    /// chooses the range's character: the axis an arc, an S, a wave or a kinked line; the band broad or narrow; 0–3
    /// spurs branching at 30–60°; how many big peaks the crest carries; a lower parallel ridge beyond a valley or not.
    /// </summary>
    private void Range(int cellX, int cellY, List<PlacementAreaComposition.Sample> result) =>
        Range(cellX, cellY, result, _area.CompositionOrigin, _cos, _sin, _halfLength, 0, false);

    /// <param name="id">One range of a massif's knot (0 for a range area): its own character and random draws.</param>
    /// <param name="knot">Part of a massif: no parallel ridge, at most one spur — the other ranges of the knot are its neighbours.</param>
    private void Range(int cellX, int cellY, List<PlacementAreaComposition.Sample> result,
        VectorPoint origin, double axisCos, double axisSin, double length, int id, bool knot)
    {
        double w = _width, h = _height;
        double Pick(uint salt) => Draw(id, 0, 500 + salt);
        var seedPart = id * 977;
        var shape = (int)(Pick(1) * 4);                       // 0 arc, 1 S, 2 wave, 3 kink
        var bend = (Pick(2) < .5 ? -1 : 1) * (.08 + .12 * Pick(3)) * length;
        // Half-width of the band at its middle; a massif's arms are narrower ridges, or the knot becomes a blanket.
        var broad = h * (1.0 + .8 * Pick(4)) * (knot ? (id == 1 ? .8 : .6) : 1);
        var crest = .55 + .3 * Pick(5);                       // share of the middle that carries big peaks
        // 1–3 summits rise above the crest; 0–2 passes cut it, where the range drops to hills (a road's way across).
        var summits = Enumerable.Range(0, 1 + (int)(Pick(15) * 2.99)).Select(i => (Pick(60 + (uint)i) - .5) * 1.1 * length).ToArray();
        var passes = Enumerable.Range(0, Pick(16) < .45 ? 0 : Pick(17) < .7 ? 1 : 2).Select(i => (Pick(70 + (uint)i) - .5) * 1.4 * length)
            .Where(p => summits.All(s => Math.Abs(p - s) > w * 1.2)).ToArray();
        double Axis(double u)
        {
            var t = Math.Clamp(u / length, -1.2, 1.2);
            return shape switch
            {
                0 => bend * (1 - t * t),
                1 => bend * Math.Sin(Math.PI * t),
                2 => .6 * bend * Math.Sin(2.2 * Math.PI * t + Pick(6) * Math.Tau),
                _ => bend * (t < Pick(7) - .5 ? (t + 1) / (Pick(7) + .5) : (1 - t) / (1.5 - Pick(7))) - .4 * bend,
            };
        }
        double Width(double u)
        {
            var t = Math.Abs(u) / length;
            // Broad in the middle, tapering in steps to both ends; a slow wobble breaks its symmetry.
            return t >= 1.1 ? 0 : broad * Math.Pow(Math.Max(0, 1 - t * t / 1.21), .7) * (1 + .18 * Math.Sin(u / (w * 1.7) + Pick(8) * Math.Tau));
        }
        var centerX = cellX * 100 + 50 - origin.X; var centerY = cellY * 100 + 50 - origin.Y;
        var projected = centerX * axisCos + centerY * axisSin;
        void World(double u, double v, out double x, out double y) { x = origin.X + u * axisCos - v * axisSin; y = origin.Y + u * axisSin + v * axisCos; }

        Band(projected, 72 + w * 1.5, 0, Axis, Width, length, crest, true, seedPart, 0);
        // Spurs: shorter, narrower bands branching from the crest at 30–60°, mid peaks at the root, hills at the tip.
        var spurs = knot ? (int)(Pick(9) * 1.6) : (int)(Pick(9) * 3.4);
        for (var i = 0; i < spurs; i++)
        {
            var rootU = (Pick(20 + (uint)i) - .5) * 1.3 * length;
            var side = Pick(30 + (uint)i) < .5 ? -1 : 1;
            var angle = side * (30 + 30 * Pick(40 + (uint)i)) * Math.PI / 180 * (Pick(45 + (uint)i) < .5 ? 1 : -1);
            var spurLength = (.25 + .2 * Pick(50 + (uint)i)) * length;
            var spurWidth = broad * (.45 + .15 * Pick(55 + (uint)i));
            double sx = Math.Cos(angle), sy = Math.Sin(angle);
            var rootV = Axis(rootU);
            SpurBand(rootU, rootV, sx, sy, spurLength, spurWidth, 1 + i + id * 13);
        }
        // A lower parallel ridge beyond a valley, shifted along the axis (en échelon), in half the ranges.
        if (!knot && Pick(11) < .55)
        {
            var side = Pick(12) < .5 ? -1 : 1;
            var offset = side * (broad * 2.0 + h * .5);
            var shift = (Pick(13) - .5) * length * .8;
            var half = length * (.4 + .2 * Pick(14));
            Band(projected - shift, 72 + w * 1.5, 0, u => .5 * Axis(u + shift) + offset, u => .55 * Width(u * length / half), half, 0, false, seedPart + 4, shift);
        }

        // Rows of the band, back to front, every row offset by half a step; positions jitter so no row reads as a line.
        void Band(double around, double reach, double unused, Func<double, double> axis, Func<double, double> width, double half,
            double crestShare, bool main, int part, double shift)
        {
            var rowStep = h * .34;
            var rows = (int)Math.Ceiling(broad * 1.6 / rowStep) + 1;
            for (var r = -rows; r <= rows; r++)
            {
                var v0 = r * rowStep;
                var d0 = Math.Abs(v0) / broad;
                // The step follows the class this row mostly gets: big peaks need room, hills pack close.
                var step = w * (d0 < .3 ? .52 : d0 < .7 ? .42 : .26);
                var phase = Draw(r, part, 130);
                var first = (int)Math.Floor((around - reach) / step) - 1;
                var last = (int)Math.Ceiling((around + reach) / step) + 1;
                for (var k = first; k <= last; k++)
                {
                    var u = (k + phase + .35 * (Draw(k, r * 7 + part, 131) - .5)) * step;
                    var local = u;
                    var bw = width(local);
                    var v = v0 + (Draw(k, r * 7 + part, 132) - .5) * .22 * h;
                    var d = bw <= 0 ? 9 : Math.Abs(v) / bw;
                    var along = Math.Abs(local) / half;
                    var central = Math.Clamp(1 - Math.Pow(along, 1.6), 0, 1);
                    // Outside the band: only a loose fringe of hills in small groups.
                    if (d > 1)
                    {
                        if (d > 1.7 || Draw(k, r * 7 + part, 133) > .3 - .3 * (d - 1)) continue;
                        Place(u + shift, axis(local) + v, Hill * (.7 + .3 * Draw(k, r * 7 + part, 134)), 70 + 28 * Draw(k, r * 7 + part, 135), 3, k, r * 7 + part, 7);
                        continue;
                    }
                    double tier;
                    var summit = main ? summits.Min(s => Math.Abs(local - s)) : double.MaxValue;
                    var pass = main && passes.Length > 0 ? passes.Min(p => Math.Abs(local - p)) : double.MaxValue;
                    if (pass < w * .5) { if (d < .5 || Draw(k, r * 7 + part, 139) < .5) continue; tier = Hill * .9; }
                    else if (main && d < .3 && summit < w * .4) tier = Big * 1.15;
                    else if (main && d < .32 && central > 1 - crestShare && Draw(k, r * 7 + part, 136) < .85) tier = Big * (pass < w * 1.1 ? .8 : .94);
                    else if (d < .72 && central > .2) tier = Mid * (1 - .25 * Math.Max(0, d - .4));
                    else tier = Hill * (1.15 - .3 * d);
                    // A few flank peaks stay out: the slopes show between them, as on drawn ranges.
                    if (tier is >= .4 and < .9 && Draw(k, r * 7 + part, 129) < .18) continue;
                    // Thin the band toward its edge so the outline stays ragged, never a filled polygon.
                    if (d > .6 && Draw(k, r * 7 + part, 137) < (d - .6) * .9) continue;
                    var activation = tier >= .9 ? 2 + 18 * (1 - central) + 6 * Draw(k, r * 7 + part, 138)
                        : tier >= .4 ? 16 + 34 * d + 10 * (1 - central) + 8 * Draw(k, r * 7 + part, 138)
                        : 44 + 40 * d + 12 * Draw(k, r * 7 + part, 138);
                    Place(u + shift, axis(local) + v, tier, activation, tier >= .9 ? 0 : tier >= .4 ? 1 : 2, k, r * 7 + part, 8);
                }
            }
        }

        void SpurBand(double rootU, double rootV, double sx, double sy, double spurLength, double spurWidth, int part)
        {
            var rowStep = h * .34;
            var rows = (int)Math.Ceiling(spurWidth / rowStep);
            for (var r = -rows; r <= rows; r++)
            {
                var step = w * .3;
                var count = (int)Math.Ceiling(spurLength / step);
                for (var k = 1; k <= count; k++)
                {
                    var s = (k + .3 * (Draw(k, r * 7 + part * 31, 140) - .5)) * step;
                    var taper = 1 - s / spurLength;
                    var bw = spurWidth * Math.Sqrt(Math.Max(0, taper));
                    var q = r * rowStep + (Draw(k, r * 7 + part * 31, 141) - .5) * .2 * h;
                    if (bw <= 0 || Math.Abs(q) > bw) continue;
                    var d = Math.Abs(q) / bw;
                    if (d > .6 && Draw(k, r * 7 + part * 31, 142) < (d - .6)) continue;
                    var tier = taper > .55 && d < .4 ? Mid * (.8 + .2 * taper) : Hill * (.8 + .4 * taper);
                    Place(rootU + s * sx - q * sy, rootV + s * sy + q * sx, tier,
                        tier >= .4 ? 24 + 30 * (1 - taper) + 8 * d : 46 + 40 * (1 - taper) + 10 * d, tier >= .4 ? 1 : 2, k, r * 7 + part * 31, 9);
                }
            }
        }

        void Place(double u, double v, double tier, double activation, int role, int column, int row, uint salt)
        {
            World(u, v, out var x, out var y);
            Put(cellX, cellY, result, x, y, tier, activation, role, column, row, salt);
        }
    }

    /// <summary>
    /// A mountain massif as reference maps draw it: not scattered islands but a knot of ranges — a main range through the
    /// middle and 1–3 side ranges branching from it at 40–75°, each a full range (crest, flanks, hills, summits,
    /// passes) with its own character. Where they meet the massif is highest; between their arms lie valleys.
    /// </summary>
    private void Massif(int cellX, int cellY, List<PlacementAreaComposition.Sample> result)
    {
        var origin = _area.CompositionOrigin;
        var length = _halfLength;
        Range(cellX, cellY, result, origin, _cos, _sin, length * (.7 + .15 * Draw(9, 9, 600)), 1, true);
        var arms = 1 + (int)(Draw(9, 9, 601) * 2.99);
        for (var i = 0; i < arms; i++)
        {
            var at = (Draw(i, 9, 602) - .5) * 1.0 * length;
            var turn = (Draw(i, 9, 603) < .5 ? -1 : 1) * (40 + 35 * Draw(i, 9, 604)) * Math.PI / 180;
            var armLength = (.32 + .22 * Draw(i, 9, 605)) * length;
            double c = Math.Cos(Math.Atan2(_sin, _cos) + turn), s = Math.Sin(Math.Atan2(_sin, _cos) + turn);
            // The arm starts on the main range and runs outward: its own middle lies one arm-length from the root.
            var root = new VectorPoint(origin.X + at * _cos, origin.Y + at * _sin);
            var middle = new VectorPoint(root.X + c * armLength * .9, root.Y + s * armLength * .9);
            Range(cellX, cellY, result, middle, c, s, armLength, 2 + i, true);
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
