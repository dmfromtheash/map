using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>What the geography check found (R24).</summary>
public enum GeographyIssueKind
{
    /// <summary>A river climbs: the ground rises along its flow.</summary>
    RiverUphill,
    /// <summary>A lake with no river flowing out of it.</summary>
    LakeWithoutOutflow,
    /// <summary>A road runs straight into a cliff between two tiers.</summary>
    RoadIntoCliff,
    /// <summary>A town no road reaches.</summary>
    TownWithoutRoad,
}

/// <summary>One finding: what, where, which object, and what the user can do about it.</summary>
public sealed record GeographyIssue(GeographyIssueKind Kind, Guid ObjectId, string Message, string Fix, double X, double Y);

/// <summary>
/// The ground of a map as one height (R24): the tier of the highest visible land at a point
/// (<see cref="LandLayer.Level"/>) plus its relief (<see cref="LandLayer.ReliefTiles"/>) inside the
/// tier; the open sea is below every land.
/// </summary>
public sealed class TerrainProbe
{
    private readonly IReadOnlyList<LandLayer> _lands;

    public TerrainProbe(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _lands = [.. document.Layers.OfType<LandLayer>().Where(l => l is { Visible: true, Opacity: > 0 } && l.GeometryTiles.TileCount > 0)
            .OrderByDescending(l => l.Level)];
    }

    /// <summary>The land at a point (null over the sea), and the height there in tiers.</summary>
    public (LandLayer? Land, double Height) At(double x, double y)
    {
        foreach (var land in _lands)
            if (ReliefEditor.HeightAt(land.GeometryTiles, (int)Math.Floor(x), (int)Math.Floor(y)) >= 128)
                return (land, land.Level + (ReliefEditor.HeightAt(land.ReliefTiles, (int)Math.Floor(x), (int)Math.Floor(y)) / 255.0 * 0.9));
        return (null, -0.5);
    }
}

/// <summary>
/// Automatic routes and the geography check (R24): a road from A to B by the easiest way over the
/// land, a river from a point down to the sea, and a check that only points at what looks wrong -
/// a river uphill, a lake with no outflow, a road into a cliff, a town no road reaches - with a way
/// to fix it; an object marked «намеренно» (<c>GeographyIntentional</c>) is fantasy on purpose and
/// never reported.
/// </summary>
public static class Geography
{
    public const int Cell = 8;

    /// <summary>
    /// The cheapest way from A to B over the land (A*): the sea and a step between tiers stop it,
    /// slopes cost, a few cells off the shore are free. Null when there is no way within reach.
    /// </summary>
    public static IReadOnlyList<VectorPoint>? RouteRoad(MapDocument document, VectorPoint from, VectorPoint to, CancellationToken cancellation = default)
    {
        var probe = new TerrainProbe(document);
        var distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var margin = (distance * 0.6) + 200;
        var grid = new Grid(probe, Math.Min(from.X, to.X) - margin, Math.Min(from.Y, to.Y) - margin,
            Math.Max(from.X, to.X) + margin, Math.Max(from.Y, to.Y) + margin);
        var start = grid.IndexOf(from); var goal = grid.IndexOf(to);
        if (start < 0 || goal < 0 || !grid.IsLand(start) || !grid.IsLand(goal)) return null;
        var cost = new Dictionary<int, double> { [start] = 0 };
        var previous = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(start, 0);
        var expanded = 0;
        while (queue.TryDequeue(out var i, out _))
        {
            if (i == goal) break;
            if ((++expanded & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (expanded > 2_000_000) return null;
            foreach (var (j, diagonal) in grid.Neighbours(i))
            {
                // The sea, a step between tiers and the foot of a cliff (under its drawn wall) stop a road.
                if (!grid.IsLand(j) || grid.Level(j) != grid.Level(i) || grid.UnderCliff(j)) continue;
                var step = (diagonal ? 1.4142 : 1) * (1 + (Math.Abs(grid.Height(j) - grid.Height(i)) * 60) + (grid.NearShore(j) ? 3 : 0));
                var c = cost[i] + step;
                if (cost.TryGetValue(j, out var known) && known <= c) continue;
                cost[j] = c; previous[j] = i;
                queue.Enqueue(j, c + grid.Distance(j, goal));
            }
        }
        if (!previous.ContainsKey(goal)) return null;
        var path = new List<int> { goal };
        while (path[^1] != start) path.Add(previous[path[^1]]);
        path.Reverse();
        var points = path.Select(grid.Centre).ToList();
        points[0] = from; points[^1] = to;
        return RiverGeometry.SimplifyIndices(points, Cell * 0.9).Select(k => points[k]).ToList();
    }

    /// <summary>
    /// The way the water takes from a point down to the sea: the ground filled from the sea inwards
    /// (no hollow traps the water) and followed downhill. Null when the point is not on land or no
    /// sea is within reach.
    /// </summary>
    public static IReadOnlyList<VectorPoint>? RouteRiver(MapDocument document, VectorPoint from, CancellationToken cancellation = default)
    {
        var probe = new TerrainProbe(document);
        const double reach = 3000;
        var grid = new Grid(probe, from.X - reach, from.Y - reach, from.X + reach, from.Y + reach);
        var start = grid.IndexOf(from);
        if (start < 0 || !grid.IsLand(start)) return null;
        var downstream = new int[grid.Count];
        Array.Fill(downstream, -1);
        var done = new bool[grid.Count];
        var filled = new double[grid.Count];
        var queue = new PriorityQueue<int, double>();
        for (var i = 0; i < grid.Count; i++)
            if (!grid.IsLand(i)) { done[i] = true; filled[i] = grid.Height(i); queue.Enqueue(i, filled[i]); }
        var settled = 0;
        while (queue.TryDequeue(out var i, out _))
        {
            if ((++settled & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (i == start) break;
            foreach (var (j, _) in grid.Neighbours(i))
            {
                if (done[j]) continue;
                done[j] = true; downstream[j] = i;
                filled[j] = Math.Max(grid.Height(j), filled[i] + 1e-6);
                queue.Enqueue(j, filled[j]);
            }
        }
        if (downstream[start] < 0) return null;
        var run = new List<int>();
        for (var at = start; at >= 0 && run.Count < grid.Count; at = downstream[at])
        {
            run.Add(at);
            if (!grid.IsLand(at)) break;
        }
        if (run.Count < 3 || grid.IsLand(run[^1])) return null;
        var points = run.Select(grid.Centre).ToList();
        points[0] = from;
        return RiverGeometry.SimplifyIndices(points, Cell * 1.2).Select(k => points[k]).ToList();
    }

    /// <summary>Every finding of the map; objects marked «намеренно» are left out.</summary>
    public static ImmutableArray<GeographyIssue> Inspect(MapDocument document, CancellationToken cancellation = default)
    {
        var probe = new TerrainProbe(document);
        var issues = ImmutableArray.CreateBuilder<GeographyIssue>();
        var layers = document.Layers.OfType<ObjectLayer>().Where(l => l.Visible).ToList();
        foreach (var river in layers.SelectMany(l => l.Rivers))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!river.Visible || river.GeographyIntentional) continue;
            var set = RiverGeometry.Evaluate(river);
            foreach (var channel in river.Channels)
            {
                if (channel.Closed)
                {
                    if (!river.Channels.Any(c => c.Source?.ParentChannelId == channel.Id))
                    {
                        var centre = (channel.Nodes.Average(n => n.X), channel.Nodes.Average(n => n.Y));
                        issues.Add(new(GeographyIssueKind.LakeWithoutOutflow, river.Id, $"Озеро «{channel.Name}» реки «{river.Name}» без стока.",
                            "Проведите из озера реку к морю («Река отсюда в море») или отметьте «намеренно» (солёное или бессточное озеро).",
                            centre.Item1, centre.Item2));
                    }
                    continue;
                }
                // Along the flow, the ground may not rise by more than a third of a tier.
                var samples = set.Channels[channel.Id].Samples;
                var lowest = double.MaxValue; (double X, double Y)? worst = null; var rise = 0.0;
                for (var k = 0; k < samples.Length; k += Math.Max(1, samples.Length / 64))
                {
                    var (land, height) = probe.At(samples[k].X, samples[k].Y);
                    if (land is null) break;
                    lowest = Math.Min(lowest, height);
                    if (height - lowest > rise) { rise = height - lowest; worst = (samples[k].X, samples[k].Y); }
                }
                if (rise > 0.33 && worst is { } at)
                    issues.Add(new(GeographyIssueKind.RiverUphill, river.Id, $"Река «{river.Name}» течёт в гору.",
                        "Разверните течение («Развернуть течение»), проведите русло по низине или отметьте «намеренно».", at.X, at.Y));
            }
        }
        foreach (var road in layers.SelectMany(l => l.Roads))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!road.Visible || road.GeographyIntentional) continue;
            foreach (var branch in road.Branches)
            {
                var points = branch.Vertices.Select(v => road.FindNode(v.NodeId)).Where(n => n is not null).Select(n => (n!.X, n.Y)).ToList();
                (int Level, bool Land)? before = null;
                for (var k = 1; k < points.Count && before is not { Level: int.MinValue }; k++)
                {
                    var length = Math.Sqrt(Math.Pow(points[k].X - points[k - 1].X, 2) + Math.Pow(points[k].Y - points[k - 1].Y, 2));
                    var steps = Math.Max(1, (int)(length / 4));
                    for (var s = 0; s <= steps; s++)
                    {
                        var x = points[k - 1].X + ((points[k].X - points[k - 1].X) * s / steps);
                        var y = points[k - 1].Y + ((points[k].Y - points[k - 1].Y) * s / steps);
                        var (land, _) = probe.At(x, y);
                        var here = (land?.Level ?? 0, land is not null);
                        if (before is { } b && b.Land && here.Item2 && b.Level != here.Item1)
                        {
                            issues.Add(new(GeographyIssueKind.RoadIntoCliff, road.Id, $"Дорога «{road.Name}» упирается в обрыв между ярусами.",
                                "Проложите дорогу в обход («Проложить дорогу A→B»), сделайте спуск («Маска обрыва → Здесь пляж») или отметьте «намеренно».", x, y));
                            before = (int.MinValue, false);
                            break;
                        }
                        before = here;
                    }
                }
            }
        }
        var roadPoints = layers.SelectMany(l => l.Roads).Where(r => r.Visible).SelectMany(r => r.Nodes).Select(n => (n.X, n.Y)).ToList();
        foreach (var town in layers.SelectMany(l => l.Objects).Where(o => o.GenerationReason == ContinentGenerator.TownReason && !o.GeographyIntentional))
        {
            var (cx, cy) = (town.X + (town.Width / 2), town.Y + (town.Height / 2));
            var near = roadPoints.Any(p => Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) < 80);
            if (!near)
                issues.Add(new(GeographyIssueKind.TownWithoutRoad, town.Id.Value, $"К поселению «{town.Text?.Content ?? town.Name}» не ведёт дорога.",
                    "Проложите дорогу от соседнего поселения («Проложить дорогу A→B») или отметьте «намеренно» (уединённое место).", cx, cy));
        }
        return issues.ToImmutable();
    }

    /// <summary>A grid of cells over a rectangle of the map, reading the ground through a probe.</summary>
    private sealed class Grid
    {
        private readonly double _left, _top;
        private readonly int _columns, _rows;
        private readonly double[] _height;
        private readonly int[] _level;
        private readonly bool[] _land;
        private readonly bool[] _shore;
        private readonly bool[] _underCliff;

        public Grid(TerrainProbe probe, double left, double top, double right, double bottom)
        {
            // Never more than about two million cells: a very long way is read more coarsely.
            var cell = Math.Max(Cell, Math.Sqrt((right - left) * (bottom - top) / 2_000_000));
            CellSize = cell;
            _left = left; _top = top;
            _columns = Math.Max(1, (int)Math.Ceiling((right - left) / cell));
            _rows = Math.Max(1, (int)Math.Ceiling((bottom - top) / cell));
            _height = new double[_columns * _rows]; _level = new int[_columns * _rows]; _land = new bool[_columns * _rows];
            for (var i = 0; i < _height.Length; i++)
            {
                var c = Centre(i);
                var (land, height) = probe.At(c.X, c.Y);
                _land[i] = land is not null; _level[i] = land?.Level ?? int.MinValue; _height[i] = height;
            }
            _shore = new bool[_height.Length];
            for (var i = 0; i < _height.Length; i++)
                if (_land[i]) _shore[i] = Neighbours(i).Any(n => !_land[n.Index]);
            // A higher tier just north of a cell drops its wall over it (walls hang below southern edges).
            _underCliff = new bool[_height.Length];
            for (var i = 0; i < _height.Length; i++)
            {
                if (!_land[i]) continue;
                var c = Centre(i);
                foreach (var d in new[] { 10.0, 22, 34 })
                    if (probe.At(c.X, c.Y - d).Land is { } above && above.Level > _level[i]) { _underCliff[i] = true; break; }
            }
        }

        public double CellSize { get; }
        public int Count => _height.Length;
        public bool IsLand(int i) => _land[i];
        public int Level(int i) => _level[i];
        public double Height(int i) => _height[i];
        public bool NearShore(int i) => _shore[i];
        public bool UnderCliff(int i) => _underCliff[i];

        public int IndexOf(VectorPoint p)
        {
            var c = (int)Math.Floor((p.X - _left) / CellSize); var r = (int)Math.Floor((p.Y - _top) / CellSize);
            return c < 0 || r < 0 || c >= _columns || r >= _rows ? -1 : (r * _columns) + c;
        }

        public VectorPoint Centre(int i) => new(_left + (((i % _columns) + 0.5) * CellSize), _top + (((i / _columns) + 0.5) * CellSize));

        public double Distance(int a, int b) =>
            Math.Sqrt(Math.Pow((a % _columns) - (b % _columns), 2) + Math.Pow((a / _columns) - (b / _columns), 2));

        public IEnumerable<(int Index, bool Diagonal)> Neighbours(int i)
        {
            var c = i % _columns; var r = i / _columns;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nc = c + dx; var nr = r + dy;
                    if (nc < 0 || nr < 0 || nc >= _columns || nr >= _rows) continue;
                    yield return ((nr * _columns) + nc, dx != 0 && dy != 0);
                }
        }
    }
}
