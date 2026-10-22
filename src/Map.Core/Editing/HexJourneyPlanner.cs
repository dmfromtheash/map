using Map.Core.Documents;

namespace Map.Core.Editing;

public sealed record JourneyDay(int Day, HexCoord Camp, double Miles, string Description);
public sealed record JourneyRoute(HexRoute Route, IReadOnlyList<JourneyDay> Days, int Crossings, string Fatigue);
public sealed record TravelBand(int Day, IReadOnlyList<IReadOnlyList<VectorPoint>> Contours);
public sealed record TravelReach(IReadOnlyDictionary<HexCoord, double> Costs, IReadOnlyList<TravelBand> Bands, bool Limited);

/// <summary>Профильный A*, альтернативы со штрафами и ограниченный Дейкстра. Никаких изменений документа.</summary>
public sealed class HexJourneyPlanner
{
    private readonly HexLayer _layer;
    private readonly Func<string, double?> _terrainCost;
    private readonly Func<string, bool> _water;
    private readonly HashSet<(HexCoord, HexCoord)> _roads = [];
    private readonly HashSet<(HexCoord, HexCoord)> _rivers = [];
    private readonly HashSet<HexCoord> _riverCells = [];
    private readonly HashSet<(HexCoord, HexCoord)> _blocked = [];
    public HexJourneyPlanner(HexLayer layer, Func<string, double?> terrainCost, Func<string, bool> water)
    {
        _layer = layer; _terrainCost = terrainCost; _water = water;
        foreach (var p in layer.Paths)
        {
            if (p.Kind == HexPathKind.River) _riverCells.UnionWith(p.Hexes);
            for (var i = 1; i < p.Hexes.Length; i++)
            {
                var set = p.BlocksMove ? _blocked : p.Kind is HexPathKind.Road or HexPathKind.Trail ? _roads : p.Kind == HexPathKind.River ? _rivers : null;
                set?.Add((p.Hexes[i - 1], p.Hexes[i])); set?.Add((p.Hexes[i], p.Hexes[i - 1]));
            }
            if (!p.BlocksMove || p.Style != HexRouteStyle.Edges) continue;
            // Пересечь ребро нельзя; канонические вершины позволяют учесть обе ориентации сетки.
            var edges = new HashSet<(HexVertex, HexVertex)>();
            for (var i = 1; i < p.Vertices.Length; i++)
            { edges.Add((p.Vertices[i - 1], p.Vertices[i])); edges.Add((p.Vertices[i], p.Vertices[i - 1])); }
            foreach (var v in p.Vertices)
            foreach (var h in v.Hex.Neighbors().Append(v.Hex))
            for (var d = 0; d < 6; d++)
            {
                var n = h.Neighbor(d); var common = Enumerable.Range(0, 6).Select(c => layer.Board.Layout.Canonical(new HexVertex(h, c)))
                    .Intersect(Enumerable.Range(0, 6).Select(c => layer.Board.Layout.Canonical(new HexVertex(n, c)))).ToArray();
                if (common.Length == 2 && edges.Contains((common[0], common[1]))) _blocked.Add((h, n));
            }
        }
    }
    private double MilesPerHex => _layer.Travel.DistancePerHex / (_layer.Travel.Unit == "км" ? 1.609344 : 1);
    public bool IsRoad(HexCoord a, HexCoord b) => _roads.Contains((a, b));
    public double? StepCost(HexCoord from, HexCoord to)
    {
        if (!_layer.Board.Contains(from) || !_layer.Board.Contains(to) || HexCoord.Distance(from, to) != 1 || _blocked.Contains((from, to))) return null;
        var profile = _layer.Journey.Profile;
        var id = _layer.Board[to]?.Terrain ?? "";
        double cost;
        if (profile.Mode == TravelMode.Boat)
        {
            if (!_rivers.Contains((from, to)) && !(_water(id) && _water(_layer.Board[from]?.Terrain ?? ""))) return null;
            cost = profile.Costs.GetValueOrDefault(id, 1);
        }
        else
        {
            var terrain = profile.Costs.TryGetValue(id, out var custom) ? custom : _terrainCost(id);
            if (terrain is null) return null;
            cost = terrain.Value;
            // Дорога улучшает местность ровно на ступень: 3 → 2, 2 → 1, 1 → 1.
            if (IsRoad(from, to)) cost = Math.Max(1, cost - 1);
            else
            {
                if (profile.Mode == TravelMode.Cart) cost *= 3;
                if (_riverCells.Contains(to)) cost += .5;
            }
        }
        if (!double.IsFinite(cost) || cost <= 0) return null;
        return cost * MilesPerHex / profile.MilesPerDay * CampaignCalendar.Factor(_layer.CampaignDay, id);
    }
    private static (HexCoord, HexCoord) Edge(HexCoord a, HexCoord b) => a.Q < b.Q || a.Q == b.Q && a.R < b.R ? (a, b) : (b, a);
    private static HashSet<(HexCoord, HexCoord)> Edges(IReadOnlyList<HexCoord> path) => [.. Enumerable.Range(1, path.Count - 1).Select(i => Edge(path[i - 1], path[i]))];
    public static double Overlap(HexRoute a, HexRoute b)
    {
        var x = Edges(a.Hexes); var y = Edges(b.Hexes);
        return Math.Min(x.Count, y.Count) == 0 ? 1 : x.Intersect(y).Count() / (double)Math.Min(x.Count, y.Count);
    }
    public IReadOnlyList<JourneyRoute> Routes(HexCoord from, HexCoord to)
    {
        if (!_layer.Board.Contains(from) || !_layer.Board.Contains(to)) return [];
        // Нижняя оценка учитывает редактируемую таблицу и не завышает даже самый быстрый профиль.
        var lower = Math.Min(1, _layer.Journey.Profile.Costs.Values.DefaultIfEmpty(1).Min()) * MilesPerHex / _layer.Journey.Profile.MilesPerDay;
        var first = HexRouting.FindRoute(from, to, StepCost, lower, 100_000);
        if (first is null) return [];
        HexRoute Measure(IReadOnlyList<HexCoord> path) => new(path, path.Count - 1, (path.Count - 1) * MilesPerHex, HexRouting.CostOf(path, StepCost), "миль");
        var routes = new List<HexRoute> { Measure(first) };
        var penalties = Edges(first).ToDictionary(e => e, _ => 1.0);
        for (var attempt = 0; attempt < 12 && routes.Count < 3; attempt++)
        {
            double? Penalized(HexCoord a, HexCoord b) => StepCost(a, b) is { } c ? c * (1 + penalties.GetValueOrDefault(Edge(a, b))) : null;
            var path = HexRouting.FindRoute(from, to, Penalized, lower, 100_000);
            if (path is null) break;
            var candidate = Measure(path);
            if (candidate.Days <= routes[0].Days * 1.3 + 1e-9 && candidate.Distance <= routes[0].Distance * 1.3 + 1e-9
                && routes.All(r => Overlap(r, candidate) < .6)) routes.Add(candidate);
            foreach (var e in Edges(path)) penalties[e] = penalties.GetValueOrDefault(e) + 1.5;
        }
        return routes.Select(r => new JourneyRoute(r, Navigate(r), CountCrossings(r.Hexes),
            r.Days >= 5 ? "высокая" : r.Days >= 2 || CampaignCalendar.Season(_layer.CampaignDay) == CampaignSeason.Winter ? "средняя" : "низкая")).ToArray();
    }
    private int CountCrossings(IReadOnlyList<HexCoord> path) => Enumerable.Range(1, path.Count - 1)
        .Count(i => _riverCells.Contains(path[i]) && !_riverCells.Contains(path[i - 1]));
    private string? Place(HexCoord h)
    {
        var point = _layer.Board[h]?.PointsOfInterest.FirstOrDefault(p => p.Name.Length != 0);
        return point?.Name ?? (_layer.Notes.TryGetValue(h, out var n) && n.Title.Length != 0 ? n.Title : null);
    }
    private IReadOnlyList<JourneyDay> Navigate(HexRoute route)
    {
        var path = route.Hexes; var result = new List<JourneyDay>(); var start = 0;
        while (start < path.Count - 1)
        {
            var end = start; var spent = 0.0; var candidates = new List<(int Index, double Cost)>();
            while (end + 1 < path.Count)
            {
                var next = spent + StepCost(path[end], path[end + 1])!.Value;
                if (next > 1.1 && end > start) break;
                spent = next; end++; candidates.Add((end, spent));
                if (spent > 1.1) break; // Один очень медленный гекс не теряется и не зацикливает навигатор.
            }
            var camp = candidates.Where(c => c.Cost <= 1).LastOrDefault();
            if (camp.Index <= start) camp = candidates[0];
            if (end == path.Count - 1 && spent <= 1.1) camp = (end, spent);
            else
            {
                var near = candidates.Where(c => Math.Abs(c.Cost - 1) <= .1 && Place(path[c.Index]) is not null)
                    .OrderBy(c => Math.Abs(c.Cost - 1)).FirstOrDefault();
                if (near.Index > start) camp = near;
            }
            var segment = path.Skip(start).Take(camp.Index - start + 1).ToArray();
            var miles = (camp.Index - start) * MilesPerHex; var h = path[camp.Index]; var day = result.Count + 1;
            var features = new List<string>();
            if (Enumerable.Range(1, segment.Length - 1).Any(i => IsRoad(segment[i - 1], segment[i]))) features.Add("дорога");
            if (CountCrossings(segment) > 0) features.Add("мост или брод");
            features.AddRange(segment.Select(p => _layer.Board[p]?.Terrain).Where(t => t is not null).Distinct().Select(t => TerrainName(t!)));
            var regions = segment.Select(p => _layer.Board[p]?.Region ?? 0).Distinct().Where(r => r != 0).ToArray();
            if (regions.Length > 1) features.Add("граница регионов");
            features.Add(camp.Index == path.Count - 1 ? "прибытие" : "ночёвка" + (Place(h) is { } place ? " у «" + place + "»" : " в пути"));
            var date = (int)Math.Min(int.MaxValue, (long)_layer.CampaignDay + day - 1);
            var text = $"День {day}: {miles:0.#} миль · {camp.Cost:0.#} дн.\n{string.Join(" → ", features)}\n{CampaignCalendar.Weather(_layer, h, date)} · {CampaignCalendar.Encounter(_layer, h, date)}";
            result.Add(new(day, h, miles, text)); start = camp.Index;
        }
        return result;
    }
    public static string TerrainName(string id) => id.Split('/').Last() switch
    {
        "grass" => "равнина", "forest" => "лес", "hills" => "холмы", "mountains" => "горы", "swamp" => "болото",
        "desert" => "пустыня", "water" or "sea" or "ocean" => "вода", "snow" => "снег", "tundra" => "тундра", _ => "местность",
    };
    public TravelReach Reach(HexCoord from)
    {
        var costs = new Dictionary<HexCoord, double>();
        if (!_layer.Board.Contains(from)) return new(costs, [], false);
        costs[from] = 0; var queue = new PriorityQueue<HexCoord, double>(); queue.Enqueue(from, 0); var limited = false;
        while (queue.TryDequeue(out var h, out var cost))
        {
            if (cost > costs[h]) continue;
            if (costs.Count >= 100_000) { limited = true; break; }
            foreach (var n in h.Neighbors())
            {
                if (StepCost(h, n) is not { } step) continue;
                var total = cost + step;
                if (total > 3 || costs.TryGetValue(n, out var known) && known <= total) continue;
                costs[n] = total; queue.Enqueue(n, total);
            }
        }
        var bands = Enumerable.Range(1, 3).Select(day => new TravelBand(day,
            Contours(costs.Where(p => p.Value <= day && (day == 1 || p.Value > day - 1)).Select(p => p.Key).ToHashSet()))).ToArray();
        return new(costs, bands, limited);
    }
    // Точный контур гекс-поля: только внешние рёбра; кольца и отверстия не накладывают прозрачность.
    private IReadOnlyList<IReadOnlyList<VectorPoint>> Contours(HashSet<HexCoord> hexes)
    {
        var layout = _layer.Board.Layout;
        (long, long) Key(VectorPoint p) => ((long)Math.Round(p.X * 100000), (long)Math.Round(p.Y * 100000));
        var edges = new Dictionary<((long, long), (long, long)), (VectorPoint A, VectorPoint B)>();
        foreach (var h in hexes)
        for (var c = 0; c < 6; c++)
        {
            var a = layout.Corner(h, c); var b = layout.Corner(h, (c + 1) % 6);
            var pa = new VectorPoint(a.X, a.Y); var pb = new VectorPoint(b.X, b.Y); var ka = Key(pa); var kb = Key(pb);
            if (!edges.Remove((kb, ka))) edges[(ka, kb)] = (pa, pb);
        }
        var outgoing = edges.GroupBy(e => e.Key.Item1).ToDictionary(g => g.Key, g => g.Select(e => e.Key).ToList());
        var contours = new List<IReadOnlyList<VectorPoint>>();
        while (edges.Count > 0)
        {
            var first = edges.First().Key; var edge = first; var points = new List<VectorPoint>();
            while (edges.Remove(edge, out var segment))
            {
                points.Add(segment.A); if (edge.Item2 == first.Item1) break;
                if (!outgoing.TryGetValue(edge.Item2, out var next)) break;
                var found = next.FindIndex(edges.ContainsKey); if (found < 0) break; edge = next[found];
            }
            if (points.Count >= 3) contours.Add(points);
        }
        return contours;
    }
}
