using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The steps of the continent generator (R23), each one preview and one undo step.</summary>
public enum ContinentStep
{
    /// <summary>Высоты: distance from the coast, noise and ridges (and the ridges painted by hand).</summary>
    Heights,
    /// <summary>Эрозия: slopes settle and the water cuts its valleys.</summary>
    Erosion,
    /// <summary>Климат: snow, tundra, forest, steppe, desert and marsh as washes over the ground.</summary>
    Climate,
    /// <summary>Реки: down the slopes to the sea, as ordinary editable rivers.</summary>
    Rivers,
    /// <summary>Поселения и дороги: named towns where the land is good, joined by the easiest ways.</summary>
    Settlements,
}

/// <summary>Settings of the continent generator; one seed makes the whole continent.</summary>
public sealed record ContinentSettings
{
    public int Seed { get; init; } = 1;
    /// <summary>How mountainous the land is, 0..1.</summary>
    public double Relief { get; init; } = 0.6;
    /// <summary>How much of the relief runs in ranges, 0..1.</summary>
    public double Ridges { get; init; } = 0.5;
    /// <summary>How strongly the slopes are worn, 0..1.</summary>
    public double Erosion { get; init; } = 0.5;
    /// <summary>-1 dry .. +1 wet.</summary>
    public double Moisture { get; init; }
    /// <summary>-1 cold .. +1 hot.</summary>
    public double Temperature { get; init; }
    /// <summary>Rivers per million square units of land.</summary>
    public double RiversPerMillion { get; init; } = 6;
    /// <summary>Towns per million square units of land.</summary>
    public double TownsPerMillion { get; init; } = 5;
}

/// <summary>
/// The continent generator (R23): from the land the user drew to a living continent, one step at
/// a time, every step reading the map as it is now. The logic is the hex world generator's
/// (<see cref="HexWorldGenerator"/>) on a grid over the land: heights from the distance to the
/// coast, noise and ridges; light erosion; climate from latitude, height and damp; rivers by flow
/// accumulation; towns where the land is good and roads by the cheapest way. Heights go into the
/// land's relief (R22), climate into its paint, rivers, roads and towns into an object layer as
/// ordinary editable objects marked with a generation reason, so a step run again replaces only
/// what it made before.
/// </summary>
public static class ContinentGenerator
{
    /// <summary>The grid cell, in world units: fine enough for a continent, coarse enough to be quick.</summary>
    public const int Cell = 8;
    public const string ReasonPrefix = "Материк: ";
    public const string RiverReason = ReasonPrefix + "река вниз по склону к морю.";
    public const string RoadReason = ReasonPrefix + "дорога между поселениями по самому лёгкому пути.";
    public const string TownReason = ReasonPrefix + "поселение у воды на ровной земле.";

    public static MapDocument Run(MapDocument document, LayerId landId, ContinentStep step, ContinentSettings settings,
        CancellationToken cancellation = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);
        if (document.FindLayer(landId) is not LandLayer land || land.GeometryTiles.TileCount == 0)
            throw new InvalidOperationException("Сначала нарисуйте сушу: генератор строит материк из неё.");
        var grid = new Grid(document, land, cancellation, progress);
        var result = step switch
        {
            ContinentStep.Heights => grid.Heights(settings),
            ContinentStep.Erosion => grid.Erosion(settings),
            ContinentStep.Climate => grid.Climate(settings),
            ContinentStep.Rivers => grid.Rivers(settings),
            _ => grid.Settlements(settings),
        };
        progress?.Invoke(1);
        return result;
    }

    private sealed class Grid
    {
        private readonly MapDocument _document;
        private readonly LandLayer _land;
        private readonly CancellationToken _cancellation;
        private readonly Action<double>? _progress;
        private readonly int _left, _top, _columns, _rows;
        private readonly bool[] _isLand;
        private readonly int _landCount;

        public Grid(MapDocument document, LandLayer land, CancellationToken cancellation, Action<double>? progress)
        {
            _document = document; _land = land; _cancellation = cancellation; _progress = progress;
            var keys = land.GeometryTiles.Keys.ToList();
            var size = TileMap.TileSize;
            _left = keys.Min(k => k.X) * size; _top = keys.Min(k => k.Y) * size;
            var right = (keys.Max(k => k.X) + 1) * size; var bottom = (keys.Max(k => k.Y) + 1) * size;
            // One cell of sea all round, so the coast is always seen from the water.
            _left -= Cell; _top -= Cell; right += Cell; bottom += Cell;
            _columns = (right - _left) / Cell; _rows = (bottom - _top) / Cell;
            _isLand = new bool[_columns * _rows];
            for (var r = 0; r < _rows; r++)
                for (var c = 0; c < _columns; c++)
                {
                    var (x, y) = Centre(c, r);
                    if (ReliefEditor.HeightAt(land.GeometryTiles, (int)x, (int)y) >= 128) { _isLand[(r * _columns) + c] = true; _landCount++; }
                }
        }

        private (double X, double Y) Centre(int column, int row) => (_left + ((column + 0.5) * Cell), _top + ((row + 0.5) * Cell));

        private IEnumerable<int> Neighbours(int i)
        {
            var c = i % _columns; var r = i / _columns;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nc = c + dx; var nr = r + dy;
                    if (nc < 0 || nr < 0 || nc >= _columns || nr >= _rows) continue;
                    yield return (nr * _columns) + nc;
                }
        }

        private void Tick(int i, double share = 1, double start = 0)
        {
            if ((i & 4095) != 0) return;
            _cancellation.ThrowIfCancellationRequested();
            _progress?.Invoke(start + (share * i / Math.Max(1, _columns * _rows)));
        }

        // ------------------------------------------------------------------ heights

        /// <summary>The land's heights as they are now (0..1), read at the middle of each cell.</summary>
        private double[] ReadHeights()
        {
            var h = new double[_columns * _rows];
            for (var i = 0; i < h.Length; i++)
            {
                if (!_isLand[i]) continue;
                var (x, y) = Centre(i % _columns, i / _columns);
                h[i] = ReliefEditor.HeightAt(_land.ReliefTiles, (int)x, (int)y) / 255.0;
            }
            return h;
        }

        /// <summary>Cells from each land cell to the nearest sea cell (8-neighbour chamfer).</summary>
        private double[] CoastDistance()
        {
            var d = new double[_columns * _rows];
            Array.Fill(d, double.MaxValue);
            var queue = new PriorityQueue<int, double>();
            for (var i = 0; i < d.Length; i++) if (!_isLand[i]) { d[i] = 0; queue.Enqueue(i, 0); }
            while (queue.TryDequeue(out var i, out var at))
            {
                if (at > d[i]) continue;
                foreach (var j in Neighbours(i))
                {
                    var step = (i % _columns != j % _columns) && (i / _columns != j / _columns) ? 1.4142 : 1;
                    if (at + step >= d[j]) continue;
                    d[j] = at + step; queue.Enqueue(j, d[j]);
                }
            }
            return d;
        }

        public MapDocument Heights(ContinentSettings s)
        {
            var coast = CoastDistance();
            var drawn = ReadHeights();
            var deepest = Math.Max(1, coast.Where((_, i) => _isLand[i]).DefaultIfEmpty(1).Max());
            var span = Math.Max(_columns, _rows);
            var h = new double[coast.Length];
            for (var i = 0; i < h.Length; i++)
            {
                Tick(i, 0.6);
                if (!_isLand[i]) continue;
                var (x, y) = (i % _columns, i / _columns);
                var inland = Math.Pow(Math.Clamp(coast[i] / (deepest * 0.6), 0, 1), 0.8);
                var noise = HexWorldGenerator.Fractal(x / (double)span * 6, y / (double)span * 6, s.Seed, 5, 0.52);
                var ridge = 1 - Math.Abs((HexWorldGenerator.Fractal(x / (double)span * 9, y / (double)span * 9, s.Seed ^ 0x51ED, 4) * 2) - 1);
                var ridges = Math.Clamp(s.Ridges, 0, 1);
                h[i] = (inland * 0.5) + (noise * 0.3) + (Math.Pow(ridge, 3) * ridges * 0.6) + (drawn[i] * 0.6);
            }
            Normalize(h, Math.Clamp(s.Relief, 0, 1));
            return WriteHeights(h);
        }

        private void Normalize(double[] h, double scale)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (var i = 0; i < h.Length; i++) if (_isLand[i]) { low = Math.Min(low, h[i]); high = Math.Max(high, h[i]); }
            if (high <= low) return;
            for (var i = 0; i < h.Length; i++) h[i] = _isLand[i] ? (h[i] - low) / (high - low) * scale : 0;
        }

        /// <summary>The grid heights into the land's relief tiles, bilinear between cell middles.</summary>
        private MapDocument WriteHeights(double[] h)
        {
            var size = TileMap.TileSize;
            var changes = new Dictionary<TileKey, byte[]?>();
            foreach (var key in _land.ReliefTiles.Keys) changes[key] = null;
            var keys = _land.GeometryTiles.Keys.ToList();
            for (var k = 0; k < keys.Count; k++)
            {
                _cancellation.ThrowIfCancellationRequested();
                _progress?.Invoke(0.6 + (0.4 * k / keys.Count));
                var key = keys[k];
                var cover = _land.GeometryTiles.TryGet(key)!;
                var pixels = new byte[size * size];
                var any = false;
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        if (cover[(y * size) + x] == 0) continue;
                        var gx = (((key.X * size) + x - _left) / (double)Cell) - 0.5;
                        var gy = (((key.Y * size) + y - _top) / (double)Cell) - 0.5;
                        var v = Sample(h, gx, gy);
                        var b = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
                        pixels[(y * size) + x] = b; any |= b != 0;
                    }
                changes[key] = any ? pixels : null;
            }
            var land = _land with { ReliefTiles = _land.ReliefTiles.WithMany(changes) };
            return _document.WithLayer(_land.Id, _ => land);
        }

        private double Sample(double[] field, double gx, double gy)
        {
            var c0 = (int)Math.Floor(gx); var r0 = (int)Math.Floor(gy);
            var fx = gx - c0; var fy = gy - r0;
            double At(int c, int r) => field[(Math.Clamp(r, 0, _rows - 1) * _columns) + Math.Clamp(c, 0, _columns - 1)];
            var top = At(c0, r0) + ((At(c0 + 1, r0) - At(c0, r0)) * fx);
            var bottom = At(c0, r0 + 1) + ((At(c0 + 1, r0 + 1) - At(c0, r0 + 1)) * fx);
            return top + ((bottom - top) * fy);
        }

        // ------------------------------------------------------------------ erosion

        public MapDocument Erosion(ContinentSettings s)
        {
            var h = ReadHeights();
            var strength = Math.Clamp(s.Erosion, 0, 1);
            // Thermal: what stands steeper than the rest angle slides to the lower neighbour.
            var rounds = (int)Math.Round(4 + (16 * strength));
            const double talus = 0.012;
            for (var round = 0; round < rounds; round++)
            {
                _cancellation.ThrowIfCancellationRequested();
                _progress?.Invoke(0.5 * round / rounds);
                var next = (double[])h.Clone();
                for (var i = 0; i < h.Length; i++)
                {
                    if (!_isLand[i]) continue;
                    var lowest = -1; var drop = 0.0;
                    foreach (var j in Neighbours(i))
                        if (h[i] - h[j] > drop) { drop = h[i] - h[j]; lowest = j; }
                    if (lowest < 0 || drop <= talus) continue;
                    var moved = (drop - talus) * 0.25;
                    next[i] -= moved;
                    if (_isLand[lowest]) next[lowest] += moved;
                }
                h = next;
            }
            // Hydraulic, lightly: the water that gathers cuts its valley in proportion to its flow.
            var (downstream, order) = Drainage(h);
            var flow = Accumulate(downstream, order, s.Seed);
            for (var k = 0; k < order.Count; k++)
            {
                var i = order[k];
                if (downstream[i] < 0) continue;
                var slope = Math.Max(0, h[i] - h[downstream[i]]);
                h[i] = Math.Max(0, h[i] - (strength * 0.004 * Math.Sqrt(flow[i]) * (0.5 + (slope * 20))));
            }
            _progress?.Invoke(0.6);
            return WriteHeights(h);
        }

        /// <summary>Priority flood from the sea: every land cell drains to a neighbour; no basin traps the water.</summary>
        private (int[] Downstream, List<int> Order) Drainage(double[] h)
        {
            var downstream = new int[h.Length];
            Array.Fill(downstream, -1);
            var done = new bool[h.Length];
            var filled = new double[h.Length];
            var order = new List<int>(_landCount);
            var queue = new PriorityQueue<int, double>();
            for (var i = 0; i < h.Length; i++) if (!_isLand[i]) { done[i] = true; queue.Enqueue(i, 0); }
            while (queue.TryDequeue(out var i, out _))
                foreach (var j in Neighbours(i))
                {
                    if (done[j]) continue;
                    done[j] = true; downstream[j] = i;
                    filled[j] = Math.Max(h[j], filled[i] + 1e-6);
                    order.Add(j); queue.Enqueue(j, filled[j]);
                }
            return (downstream, order);
        }

        private double[] Accumulate(int[] downstream, List<int> order, int seed)
        {
            var flow = new double[downstream.Length];
            var span = Math.Max(_columns, _rows);
            for (var k = order.Count - 1; k >= 0; k--)
            {
                var i = order[k];
                flow[i] += 0.6 + (HexWorldGenerator.Fractal((i % _columns) / (double)span * 5, (i / _columns) / (double)span * 5, seed ^ 0x3C9, 3) * 0.8);
                if (downstream[i] >= 0 && _isLand[downstream[i]]) flow[downstream[i]] += flow[i];
            }
            return flow;
        }

        // ------------------------------------------------------------------ climate

        public MapDocument Climate(ContinentSettings s)
        {
            var h = ReadHeights();
            var coast = CoastDistance();
            var span = Math.Max(_columns, _rows);
            var canvasHeight = Math.Max(1, _document.Canvas.Height);
            // The wash of each cell (premultiplied RGBA, 0..1), blended between cell middles.
            var r = new double[h.Length]; var g = new double[h.Length]; var b = new double[h.Length]; var a = new double[h.Length];
            for (var i = 0; i < h.Length; i++)
            {
                Tick(i, 0.4);
                if (!_isLand[i]) continue;
                var (x, y) = (i % _columns, i / _columns);
                var (_, wy) = Centre(x, y);
                var latitude = Math.Abs((wy / canvasHeight * 2) - 1) * 0.55;
                // The climate bands wander: a broad noise bends every border so none runs along a row.
                var temperature = 1 - latitude - (h[i] * 0.55) + (0.25 * s.Temperature)
                    + ((HexWorldGenerator.Fractal(x / (double)span * 4, y / (double)span * 4, s.Seed ^ 0x7E3, 4) - 0.5) * 0.6);
                var moisture = (HexWorldGenerator.Fractal(x / (double)span * 5, y / (double)span * 5, s.Seed ^ 0x3C9, 4) * 0.75)
                    + ((1 - Math.Min(coast[i], 12) / 12.0) * 0.35) + (0.25 * s.Moisture);
                var (colour, alpha) =
                    temperature < 0.12 || h[i] > 0.85 ? ("#EEF2F4", 0.55)
                    : temperature < 0.3 ? ("#B4B49A", 0.32)
                    : moisture > 0.86 && coast[i] <= 3 && h[i] < 0.3 ? ("#66745A", 0.34)
                    : temperature > 0.62 && moisture < 0.38 ? ("#D8BF8A", 0.45)
                    : moisture > 0.6 ? ("#5E7A4A", 0.36)
                    : ("#9AA36E", 0.12);
                var (cr, cg, cb) = (Convert.ToInt32(colour[1..3], 16) / 255.0, Convert.ToInt32(colour[3..5], 16) / 255.0, Convert.ToInt32(colour[5..7], 16) / 255.0);
                r[i] = cr * alpha; g[i] = cg * alpha; b[i] = cb * alpha; a[i] = alpha;
            }
            // Washes, not patches: the colours of the cells run into one another over a few cells.
            foreach (var field in new[] { r, g, b, a }) Soften(field, 3);
            var size = TileMap.TileSize;
            var changes = new Dictionary<TileKey, byte[]?>();
            foreach (var key in _land.PaintTiles.Keys) changes[key] = null;
            var keys = _land.GeometryTiles.Keys.ToList();
            for (var k = 0; k < keys.Count; k++)
            {
                _cancellation.ThrowIfCancellationRequested();
                _progress?.Invoke(0.4 + (0.6 * k / keys.Count));
                var key = keys[k];
                var cover = _land.GeometryTiles.TryGet(key)!;
                var pixels = new byte[size * size * 4];
                var any = false;
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        if (cover[(y * size) + x] == 0) continue;
                        var gx = (((key.X * size) + x - _left) / (double)Cell) - 0.5;
                        var gy = (((key.Y * size) + y - _top) / (double)Cell) - 0.5;
                        var al = Sample(a, gx, gy);
                        if (al <= 0.004) continue;
                        var o = ((y * size) + x) * 4;
                        pixels[o] = (byte)Math.Clamp(Math.Round(Sample(r, gx, gy) * 255), 0, 255);
                        pixels[o + 1] = (byte)Math.Clamp(Math.Round(Sample(g, gx, gy) * 255), 0, 255);
                        pixels[o + 2] = (byte)Math.Clamp(Math.Round(Sample(b, gx, gy) * 255), 0, 255);
                        pixels[o + 3] = (byte)Math.Clamp(Math.Round(al * 255), 0, 255);
                        any = true;
                    }
                changes[key] = any ? pixels : null;
            }
            var land = _land with { PaintTiles = _land.PaintTiles.WithMany(changes) };
            return _document.WithLayer(_land.Id, _ => land);
        }

        /// <summary>A few passes of a 5×5 box blur over the land cells; the sea stays empty.</summary>
        private void Soften(double[] field, int passes)
        {
            for (var pass = 0; pass < passes; pass++)
            {
                _cancellation.ThrowIfCancellationRequested();
                var next = new double[field.Length];
                for (var i = 0; i < field.Length; i++)
                {
                    if (!_isLand[i]) continue;
                    var (c, r) = (i % _columns, i / _columns);
                    double total = 0; var count = 0;
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dx = -2; dx <= 2; dx++)
                        {
                            var nc = c + dx; var nr = r + dy;
                            if (nc < 0 || nr < 0 || nc >= _columns || nr >= _rows) continue;
                            var j = (nr * _columns) + nc;
                            if (!_isLand[j]) continue;
                            total += field[j]; count++;
                        }
                    next[i] = count == 0 ? field[i] : total / count;
                }
                Array.Copy(next, field, field.Length);
            }
        }

        // ------------------------------------------------------------------ rivers

        public MapDocument Rivers(ContinentSettings s)
        {
            var h = ReadHeights();
            var (downstream, order) = Drainage(h);
            var flow = Accumulate(downstream, order, s.Seed);
            _progress?.Invoke(0.3);
            var area = _landCount * (double)Cell * Cell;
            var wanted = Math.Max(0, (int)Math.Round(area / 1e6 * s.RiversPerMillion));
            var (objects, layers) = TargetLayer();
            var rivers = objects.Rivers.Where(r => r.GenerationReason?.StartsWith(ReasonPrefix, StringComparison.Ordinal) != true).ToList();
            if (wanted > 0 && order.Count > 0)
            {
                bool HasStreamAbove(int i, double t) => Neighbours(i).Any(j => downstream[j] == i && flow[j] >= t);
                int Heads(double t) => order.Count(i => flow[i] >= t && !HasStreamAbove(i, t));
                double low = 2, high = Math.Max(3, order.Max(i => flow[i]));
                for (var step = 0; step < 22; step++)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    var middle = (low + high) / 2;
                    if (Heads(middle) > wanted) low = middle; else high = middle;
                }
                var threshold = high;
                var used = new HashSet<int>();
                var heads = order.Where(i => flow[i] >= threshold && !HasStreamAbove(i, threshold)).OrderByDescending(i => Length(i)).ToList();
                int Length(int i) { var n = 0; for (var at = i; at >= 0 && _isLand[at] && n < order.Count; at = downstream[at]) n++; return n; }
                _progress?.Invoke(0.6);
                foreach (var head in heads)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    var run = new List<int>();
                    for (var at = head; at >= 0; at = downstream[at])
                    {
                        run.Add(at);
                        if (!_isLand[at] || used.Contains(at)) break;
                    }
                    if (run.Count < 6) continue;
                    used.UnionWith(run.Take(run.Count - 1));
                    var points = run.Select(i => Centre(i % _columns, i / _columns)).Select(p => new VectorPoint(p.X, p.Y)).ToList();
                    var kept = RiverGeometry.SimplifyIndices(points, Cell * 1.2);
                    if (kept.Count < 3) continue;
                    var nodes = kept.Select(k => new RiverNode(Guid.NewGuid(), points[k].X, points[k].Y,
                        Math.Round(Math.Clamp(2 + (Math.Sqrt(flow[run[Math.Min(k, run.Count - 1)]]) * 0.45), 2, 18), 1))).ToImmutableArray();
                    var channel = new RiverChannel { Name = "Русло", Nodes = nodes };
                    rivers.Add(new River
                    {
                        Name = EntityNameGenerator.PlaceName(_document, LabelPreset.River, s.Seed + rivers.Count * 7919),
                        Channels = [channel], Style = RiverStyle.Natural(), Level = _land.Level, GenerationReason = RiverReason,
                    });
                }
            }
            var target = objects with { Rivers = [.. rivers] };
            return _document.WithLayers([.. layers.Select(l => l.Id == target.Id ? target : l)]);
        }

        /// <summary>The object layer the generated objects go to: the first visible, unlocked one, or a new layer.</summary>
        private (ObjectLayer Layer, IReadOnlyList<MapLayer> Layers) TargetLayer()
        {
            if (_document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l is { Visible: true, Locked: false }) is { } existing)
                return (existing, _document.Layers);
            var added = new ObjectLayer(LayerId.New(), "Материк", true, false, 1, []);
            return (added, [.. _document.Layers, added]);
        }

        // ------------------------------------------------------------------ settlements and roads

        public MapDocument Settlements(ContinentSettings s)
        {
            var h = ReadHeights();
            var coast = CoastDistance();
            var (objects, layers) = TargetLayer();
            var riverCells = new HashSet<int>();
            foreach (var river in objects.Rivers)
                foreach (var channel in river.Channels)
                    foreach (var node in channel.Nodes)
                    {
                        var c = (int)((node.X - _left) / Cell); var r = (int)((node.Y - _top) / Cell);
                        if (c >= 0 && r >= 0 && c < _columns && r < _rows) riverCells.Add((r * _columns) + c);
                    }
            double Slope(int i) => Neighbours(i).Max(j => Math.Abs(h[i] - h[j]));
            var scores = new List<(int Cell, double Score)>();
            for (var i = 0; i < h.Length; i++)
            {
                Tick(i, 0.3);
                if (!_isLand[i] || coast[i] < 1.5) continue;
                var near = Neighbours(i).Any(riverCells.Contains) || riverCells.Contains(i);
                var score = 3 - (h[i] * 4) - (Slope(i) * 60) + (near ? 3 : 0) + (coast[i] <= 3 ? 2 : 0)
                    + (HexEditing.Noise(new HexCoord(i % _columns, i / _columns), s.Seed ^ 0x5E7) % 1000 / 1000.0 * 2);
                if (score > 0.5) scores.Add((i, score));
            }
            scores.Sort((x, y) => y.Score.CompareTo(x.Score));
            var area = _landCount * (double)Cell * Cell;
            var wanted = Math.Max(1, (int)Math.Round(area / 1e6 * s.TownsPerMillion));
            var spacing = Math.Sqrt(area / wanted) * 0.55 / Cell;
            var towns = new List<int>();
            foreach (var (i, _) in scores)
            {
                if (towns.Count >= wanted) break;
                var (c, r) = (i % _columns, i / _columns);
                if (towns.Any(t => Math.Sqrt(Math.Pow((t % _columns) - c, 2) + Math.Pow((t / _columns) - r, 2)) < spacing)) continue;
                towns.Add(i);
            }
            _progress?.Invoke(0.4);

            var keptObjects = objects.Objects.Where(o => o.GenerationReason?.StartsWith(ReasonPrefix, StringComparison.Ordinal) != true).ToList();
            var names = new HashSet<string>(keptObjects.Where(o => o.Text is not null).Select(o => o.Text!.Content));
            for (var k = 0; k < towns.Count; k++)
            {
                var kind = k < Math.Max(1, towns.Count / 4) ? LabelPreset.City : LabelPreset.Village;
                var name = "";
                for (var attempt = 0; attempt < 32; attempt++)
                {
                    name = EntityNameGenerator.PlaceName(_document, kind, s.Seed + (k * 104729) + (attempt * 31));
                    if (names.Add(name)) break;
                }
                var style = LabelPresets.Apply(new TextStyle { Content = name, Alignment = TextAlignment.Center }, kind);
                var width = (name.Length * style.FontSize * 0.62) + 24; var height = style.FontSize * 1.7;
                var (x, y) = Centre(towns[k] % _columns, towns[k] / _columns);
                keptObjects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Text, name, x - (width / 2), y - (height / 2), width, height, 0,
                    "#FF20242B", "#FFFFFFFF", 0, true, false) { Text = style, GenerationReason = TownReason });
            }
            _progress?.Invoke(0.55);

            // Roads: a spanning tree of the towns (Prim by distance), each link the cheapest way over the land.
            var roads = objects.Roads.Where(r => r.GenerationReason?.StartsWith(ReasonPrefix, StringComparison.Ordinal) != true).ToList();
            if (towns.Count >= 2)
            {
                var inTree = new bool[towns.Count]; var best = new double[towns.Count]; var parent = new int[towns.Count];
                Array.Fill(best, double.MaxValue); best[0] = 0;
                double Distance(int a, int b) => Math.Sqrt(Math.Pow((towns[a] % _columns) - (towns[b] % _columns), 2) + Math.Pow((towns[a] / _columns) - (towns[b] / _columns), 2));
                for (var added = 0; added < towns.Count; added++)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    _progress?.Invoke(0.55 + (0.45 * added / towns.Count));
                    var next = -1;
                    for (var i = 0; i < towns.Count; i++) if (!inTree[i] && (next < 0 || best[i] < best[next])) next = i;
                    inTree[next] = true;
                    for (var i = 0; i < towns.Count; i++)
                        if (!inTree[i] && Distance(next, i) < best[i]) { best[i] = Distance(next, i); parent[i] = next; }
                    if (added == 0) continue;
                    if (Route(towns[parent[next]], towns[next], h, coast, riverCells) is not { Count: >= 2 } route) continue;
                    var points = route.Select(i => Centre(i % _columns, i / _columns)).Select(p => new VectorPoint(p.X, p.Y)).ToList();
                    var kept = RiverGeometry.SimplifyIndices(points, Cell * 0.9).Select(i => points[i]).ToList();
                    if (kept.Count < 2) continue;
                    roads.Add(RoadEditor.NewRoad(kept, 3, RoadStyle.Painted(), "Дорога", s.Seed + roads.Count) with
                    { Level = _land.Level, GenerationReason = RoadReason });
                }
            }
            var target = objects with { Objects = [.. keptObjects], Roads = [.. roads] };
            return _document.WithLayers([.. layers.Select(l => l.Id == target.Id ? target : l)]);
        }

        /// <summary>The cheapest way between two cells over the land: slopes cost, rivers cost a ford, the sea stops.</summary>
        private List<int>? Route(int from, int to, double[] h, double[] coast, HashSet<int> rivers)
        {
            var cost = new Dictionary<int, double> { [from] = 0 };
            var previous = new Dictionary<int, int>();
            var queue = new PriorityQueue<int, double>();
            queue.Enqueue(from, 0);
            var (tc, tr) = (to % _columns, to / _columns);
            var expanded = 0;
            while (queue.TryDequeue(out var i, out _))
            {
                if (i == to) break;
                if (++expanded > 400_000) return null;
                foreach (var j in Neighbours(i))
                {
                    if (!_isLand[j]) continue;
                    var diagonal = (i % _columns != j % _columns) && (i / _columns != j / _columns);
                    // Slopes cost, a few cells' clearance from the shore costs nothing, the shore itself is avoided.
                    var step = (diagonal ? 1.4142 : 1) * (1 + (Math.Abs(h[j] - h[i]) * 80) + (coast[j] < 3 ? 4 : 0))
                        + (rivers.Contains(j) && !rivers.Contains(i) ? 6 : 0);
                    var c = cost[i] + step;
                    if (cost.TryGetValue(j, out var known) && known <= c) continue;
                    cost[j] = c; previous[j] = i;
                    var heuristic = Math.Sqrt(Math.Pow((j % _columns) - tc, 2) + Math.Pow((j / _columns) - tr, 2));
                    queue.Enqueue(j, c + heuristic);
                }
            }
            if (!previous.ContainsKey(to)) return null;
            var path = new List<int> { to };
            while (path[^1] != from) path.Add(previous[path[^1]]);
            path.Reverse();
            return path;
        }
    }
}
