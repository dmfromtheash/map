using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>The steps of the hex world generator, in the order they build on each other.</summary>
public enum HexGenerationStep
{
    /// <summary>Continents and seas: land grows where a noisy height field is high; shallows hug the coast, lakes fill inland basins.</summary>
    Land,

    /// <summary>Hills and mountain ranges on the land's high ground.</summary>
    Relief,

    /// <summary>Biomes from latitude, height and moisture: snow, taiga, forest, jungle, swamp, desert, plains.</summary>
    Climate,

    /// <summary>Rivers from the mountains downhill to the sea or a lake.</summary>
    Rivers,

    /// <summary>Cities, villages, castles, ports and mines where the land is good for them; fields around cities.</summary>
    Settlements,

    /// <summary>Roads joining the settlements by the cheapest ways (a spanning tree, Prim), bridged over rivers.</summary>
    Roads,
}

/// <summary>Settings of the hex world generator; one seed makes the whole world, each step re-uses it.</summary>
public sealed record HexGeneratorSettings
{
    public int Seed { get; init; } = 1;

    /// <summary>Share of the board that becomes land (0.1..0.9).</summary>
    public double LandShare { get; init; } = 0.45;

    /// <summary>0: one round continent; 1: many ragged islands.</summary>
    public double Fragmentation { get; init; } = 0.4;

    /// <summary>Share of the land that is hills and mountains (0..0.6).</summary>
    public double Relief { get; init; } = 0.22;

    /// <summary>-1 dry .. +1 wet.</summary>
    public double Moisture { get; init; }

    /// <summary>-1 cold .. +1 hot.</summary>
    public double Temperature { get; init; }

    /// <summary>Rivers per thousand land hexes.</summary>
    public double RiversPerThousand { get; init; } = 6;

    /// <summary>Settlements per thousand land hexes.</summary>
    public double SettlementsPerThousand { get; init; } = 8;
}

/// <summary>Which tile ids the generator writes; the starter set by default, an owner set later.</summary>
public sealed record HexGeneratorPalette
{
    public string Sea { get; init; } = "starter/sea";
    public string Shallows { get; init; } = "starter/shallows";
    public string Lake { get; init; } = "starter/lake";
    public string Grass { get; init; } = "starter/grass";
    public string Farmland { get; init; } = "starter/farmland";
    public string Forest { get; init; } = "starter/forest";
    public string Pines { get; init; } = "starter/pines";
    public string Jungle { get; init; } = "starter/jungle";
    public string Hills { get; init; } = "starter/hills";
    public string Mountains { get; init; } = "starter/mountains";
    public string Peaks { get; init; } = "starter/peaks";
    public string Desert { get; init; } = "starter/desert";
    public string Swamp { get; init; } = "starter/swamp";
    public string Snow { get; init; } = "starter/snow";
    public string City { get; init; } = "starter/city";
    public string Village { get; init; } = "starter/village";
    public string Castle { get; init; } = "starter/castle";
    public string Port { get; init; } = "starter/port";
    public string Mine { get; init; } = "starter/mine";

    /// <summary>Pictures per tile, for the random variant of every generated hex.</summary>
    public int Variants { get; init; } = 3;

    public bool IsWater(string? id) => id == Sea || id == Shallows || id == Lake;

    public bool IsHigh(string? id) => id == Hills || id == Mountains || id == Peaks;
}

/// <summary>
/// The hex world generator (Kohlhofer's hex map generator and the Worldographer 2025 generators,
/// re-done on Map's core): every step reads the board as it is now, so the user can paint between
/// steps and the next step builds on the edit; every step is one undoable edit of the layer; the
/// same seed always makes the same world. Heights and moisture are noise fields of the hex position,
/// so they never need to be stored. Arrays over the board keep a 1024 × 1024 board within seconds.
/// </summary>
public static class HexWorldGenerator
{
    /// <summary>Runs one step on a layer and returns the new layer (the input is unchanged).</summary>
    public static HexLayer Run(HexLayer layer, HexGenerationStep step, HexGeneratorSettings settings, HexGeneratorPalette? palette = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(settings);
        palette ??= new HexGeneratorPalette();
        cancellationToken.ThrowIfCancellationRequested();
        var world = new World(layer, settings, palette, cancellationToken);
        return step switch
        {
            HexGenerationStep.Land => world.Land(),
            HexGenerationStep.Relief => world.Relief(),
            HexGenerationStep.Climate => world.Climate(),
            HexGenerationStep.Rivers => world.Rivers(),
            HexGenerationStep.Settlements => world.Settlements(),
            _ => world.Roads(),
        };
    }

    /// <summary>Every step in order.</summary>
    public static HexLayer RunAll(HexLayer layer, HexGeneratorSettings settings, HexGeneratorPalette? palette = null)
    {
        foreach (var step in Enum.GetValues<HexGenerationStep>()) layer = Run(layer, step, settings, palette);
        return layer;
    }

    /// <summary>A smooth noise in 0..1 of a plane point (value noise, quintic fade, several octaves).</summary>
    public static double Fractal(double x, double y, int seed, int octaves = 5, double persistence = 0.5)
    {
        var total = 0.0;
        var amplitude = 1.0;
        var norm = 0.0;
        for (var octave = 0; octave < octaves; octave++)
        {
            total += amplitude * Value(x, y, seed + octave * 1013);
            norm += amplitude;
            amplitude *= persistence;
            x *= 2.03;
            y *= 2.03;
        }

        return total / norm;
    }

    private static double Value(double x, double y, int seed)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        double Corner(int cx, int cy) => HexEditing.Noise(new HexCoord(cx, cy), seed) / (double)uint.MaxValue;
        var u = Fade(fx);
        var v = Fade(fy);
        var a = Corner(x0, y0) + (Corner(x0 + 1, y0) - Corner(x0, y0)) * u;
        var b = Corner(x0, y0 + 1) + (Corner(x0 + 1, y0 + 1) - Corner(x0, y0 + 1)) * u;
        return a + (b - a) * v;
    }

    private sealed class World
    {
        private readonly HexLayer _layer;
        private readonly HexBoard _board;
        private readonly HexGeneratorSettings _settings;
        private readonly HexGeneratorPalette _palette;
        private readonly int _columns;
        private readonly int _rows;
        private double[]? _height;
        private readonly CancellationToken _cancellation;

        public World(HexLayer layer, HexGeneratorSettings settings, HexGeneratorPalette palette, CancellationToken cancellation)
        {
            _cancellation = cancellation;
            _layer = layer;
            _board = layer.Board;
            _settings = settings;
            _palette = palette;
            _columns = _board.Columns;
            _rows = _board.Rows;
        }

        private int Index(HexOffset offset) => offset.Row * _columns + offset.Column;

        private HexCoord Hex(int index) => _board.Layout.FromOffset(new HexOffset(index % _columns, index / _columns));

        private int IndexOf(HexCoord hex) => Index(_board.Layout.ToOffset(hex));

        /// <summary>The hex position in hex units (orientation-neutral: the axial lattice).</summary>
        private static (double X, double Y) Plane(HexCoord hex) => (hex.Q + hex.R / 2.0, hex.R * 0.8660254037844386);

        /// <summary>The height field: fractal noise, lowered towards the board's rim so seas surround the land.</summary>
        private double[] Heights()
        {
            if (_height is not null) return _height;
            var height = new double[_columns * _rows];
            var scale = 0.9 + 3.2 * Math.Clamp(_settings.Fragmentation, 0, 1);
            var span = Math.Max(_columns, _rows);
            Parallel.For(0, _rows, new ParallelOptions { CancellationToken = _cancellation, MaxDegreeOfParallelism = 2 }, row =>
            {
                for (var column = 0; column < _columns; column++)
                {
                    var hex = _board.Layout.FromOffset(new HexOffset(column, row));
                    var (x, y) = Plane(hex);
                    var n = Fractal(x / span * scale * 4, y / span * scale * 4, _settings.Seed, 6, 0.52);
                    var dx = (column + 0.5) / _columns * 2 - 1;
                    var dy = (row + 0.5) / _rows * 2 - 1;
                    var rim = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    var falloff = Math.Pow(Math.Clamp(rim, 0, 1), 2.6) * (1.1 - 0.6 * _settings.Fragmentation);
                    height[row * _columns + column] = n - falloff;
                }
            });
            return _height = height;
        }

        private HexCell Cell(string terrain, HexCoord hex, HexCell? current, string? feature = null) =>
            HexCell.Of(terrain, (int)(HexEditing.Noise(hex, _settings.Seed ^ 0x2F1) % (uint)Math.Max(1, _palette.Variants)),
                feature ?? current?.Feature, feature is not null ? 1 : current?.FeatureVariant ?? 0,
                0, current?.Region ?? 0, current?.Revealed ?? false);

        private HexLayer With(Func<int, HexCoord, HexCell?, HexCell?> rewrite)
        {
            var changes = new List<KeyValuePair<HexCoord, HexCell?>>();
            for (var index = 0; index < _columns * _rows; index++)
            {
                if ((index & 255) == 0) _cancellation.ThrowIfCancellationRequested();
                var hex = Hex(index);
                var current = _board[hex];
                var after = rewrite(index, hex, current);
                if (!Equals(after, current)) changes.Add(new(hex, after));
            }

            return _layer with { Board = _board.With(changes) };
        }

        public HexLayer Land()
        {
            var height = Heights();
            var sorted = (double[])height.Clone();
            Array.Sort(sorted);
            var share = Math.Clamp(_settings.LandShare, 0.05, 0.95);
            var threshold = sorted[Math.Clamp((int)((1 - share) * sorted.Length), 0, sorted.Length - 1)];
            var land = new bool[height.Length];
            for (var i = 0; i < land.Length; i++) land[i] = height[i] >= threshold;

            // Water that does not reach the board's rim is a lake.
            var ocean = new bool[land.Length];
            var queue = new Queue<int>();
            for (var i = 0; i < land.Length; i++)
            {
                var column = i % _columns;
                var row = i / _columns;
                if (!land[i] && (column == 0 || row == 0 || column == _columns - 1 || row == _rows - 1))
                {
                    ocean[i] = true;
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                _cancellation.ThrowIfCancellationRequested();
                var hex = Hex(queue.Dequeue());
                foreach (var next in hex.Neighbors())
                {
                    if (!_board.Contains(next)) continue;
                    var j = IndexOf(next);
                    if (land[j] || ocean[j]) continue;
                    ocean[j] = true;
                    queue.Enqueue(j);
                }
            }

            // A new world: the rivers, roads and borders of the old one would run over its seas.
            return With((index, hex, current) =>
            {
                if (land[index]) return Cell(_palette.Grass, hex, null);
                if (!ocean[index]) return Cell(_palette.Lake, hex, null);
                var coast = hex.Neighbors().Any(n => _board.Contains(n) && land[IndexOf(n)]);
                return Cell(coast ? _palette.Shallows : _palette.Sea, hex, null);
            }) with { Paths = [] };
        }

        /// <summary>Rank of each land hex's height among the land, 0 (lowest) .. 1 (highest); -1 on water.</summary>
        private double[] LandRank()
        {
            var height = Heights();
            var land = new List<int>();
            for (var i = 0; i < height.Length; i++)
            {
                if ((i & 255) == 0) _cancellation.ThrowIfCancellationRequested();
                var cell = _board[Hex(i)];
                if (cell is not null && !_palette.IsWater(cell.Terrain)) land.Add(i);
            }

            var rank = new double[height.Length];
            Array.Fill(rank, -1);
            land.Sort((a, b) => height[a].CompareTo(height[b]));
            for (var k = 0; k < land.Count; k++) rank[land[k]] = land.Count == 1 ? 1 : (double)k / (land.Count - 1);
            return rank;
        }

        public HexLayer Relief()
        {
            var rank = LandRank();
            var relief = Math.Clamp(_settings.Relief, 0, 0.6);
            // Ridges: the mountains follow a second noise so they line up as ranges, not blobs.
            var span = Math.Max(_columns, _rows);
            return With((index, hex, current) =>
            {
                if (current is null || rank[index] < 0) return current;
                var (x, y) = Plane(hex);
                var ridge = 1 - Math.Abs(Fractal(x / span * 9, y / span * 9, _settings.Seed ^ 0x51ED, 4) * 2 - 1);
                var score = rank[index] * 0.65 + ridge * 0.35;
                if (score > 1 - relief * 0.08) return Cell(_palette.Peaks, hex, current);
                if (score > 1 - relief * 0.35) return Cell(_palette.Mountains, hex, current);
                if (score > 1 - relief) return Cell(_palette.Hills, hex, current);
                return _palette.IsHigh(current.Terrain) ? Cell(_palette.Grass, hex, current) : current;
            });
        }

        /// <summary>Hexes from each hex to the nearest water (breadth-first over the board), capped.</summary>
        private int[] WaterDistance(int cap = 12)
        {
            var distance = new int[_columns * _rows];
            Array.Fill(distance, cap);
            var queue = new Queue<int>();
            for (var i = 0; i < distance.Length; i++)
            {
                if (_palette.IsWater(_board[Hex(i)]?.Terrain))
                {
                    distance[i] = 0;
                    queue.Enqueue(i);
                }
            }

            while (queue.Count > 0)
            {
                _cancellation.ThrowIfCancellationRequested();
                var index = queue.Dequeue();
                if (distance[index] >= cap - 1) continue;
                foreach (var next in Hex(index).Neighbors())
                {
                    if (!_board.Contains(next)) continue;
                    var j = IndexOf(next);
                    if (distance[j] <= distance[index] + 1) continue;
                    distance[j] = distance[index] + 1;
                    queue.Enqueue(j);
                }
            }

            return distance;
        }

        public HexLayer Climate()
        {
            var rank = LandRank();
            var water = WaterDistance();
            var span = Math.Max(_columns, _rows);
            return With((index, hex, current) =>
            {
                if (current is null || rank[index] < 0) return current;
                var row = index / _columns;
                var (x, y) = Plane(hex);
                // Latitude: the middle row is the warm belt, the top and bottom rows the poles.
                var latitude = Math.Abs((row + 0.5) / _rows * 2 - 1);
                var temperature = 1 - latitude * 1.05 + 0.25 * _settings.Temperature
                    + (Fractal(x / span * 6, y / span * 6, _settings.Seed ^ 0x7E3, 3) - 0.5) * 0.3 - rank[index] * 0.25;
                var moisture = Fractal(x / span * 5, y / span * 5, _settings.Seed ^ 0x3C9, 4) * 0.75
                    + (1 - Math.Min(water[index], 8) / 8.0) * 0.35 + 0.25 * _settings.Moisture;
                if (_palette.IsHigh(current.Terrain))
                {
                    return temperature < 0.12 && current.Terrain != _palette.Peaks ? Cell(_palette.Snow, hex, current) : current;
                }

                string biome;
                if (temperature < 0.12) biome = _palette.Snow;
                else if (temperature < 0.32) biome = moisture > 0.45 ? _palette.Pines : _palette.Grass;
                else if (moisture > 0.86 && water[index] <= 2 && rank[index] < 0.35) biome = _palette.Swamp;
                else if (temperature > 0.74 && moisture > 0.72) biome = _palette.Jungle;
                else if (moisture > 0.62) biome = _palette.Forest;
                else if (temperature > 0.62 && moisture < 0.38) biome = _palette.Desert;
                else biome = _palette.Grass;
                return Cell(biome, hex, current);
            });
        }

        public HexLayer Rivers()
        {
            var height = Heights();
            var sources = new List<int>();
            var landCount = 0;
            for (var i = 0; i < height.Length; i++)
            {
                var terrain = _board[Hex(i)]?.Terrain;
                if (terrain is null || _palette.IsWater(terrain)) continue;
                landCount++;
                if (_palette.IsHigh(terrain)) sources.Add(i);
            }

            // A stable shuffle by noise picks the sources; rivers run downhill along the height field.
            sources.Sort((a, b) => HexEditing.Noise(Hex(a), _settings.Seed ^ 0x9A1).CompareTo(HexEditing.Noise(Hex(b), _settings.Seed ^ 0x9A1)));
            var wanted = (int)Math.Round(landCount / 1000.0 * _settings.RiversPerThousand);
            var used = new HashSet<HexCoord>(_layer.Paths.Where(p => p.Kind == HexPathKind.River).SelectMany(p => p.Hexes));
            var paths = _layer.Paths.ToBuilder();
            var lakes = new List<KeyValuePair<HexCoord, HexCell?>>();
            var made = 0;
            foreach (var source in sources)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (made >= wanted) break;
                var start = Hex(source);
                if (used.Contains(start) || start.Neighbors().Any(used.Contains)) continue;
                var run = new List<HexCoord> { start };
                var seen = new HashSet<HexCoord> { start };
                var ended = false;
                while (run.Count < 4 * (_columns + _rows))
                {
                    var here = run[^1];
                    HexCoord? best = null;
                    var bestHeight = double.MaxValue;
                    foreach (var next in here.Neighbors())
                    {
                        if (!_board.Contains(next) || seen.Contains(next)) continue;
                        var h = height[IndexOf(next)] + (HexEditing.Noise(next, _settings.Seed ^ 0x77) % 1000) / 1000.0 * 0.01;
                        if (h < bestHeight)
                        {
                            bestHeight = h;
                            best = next;
                        }
                    }

                    if (best is not { } step) break;
                    run.Add(step);
                    seen.Add(step);
                    var terrain = _board[step]?.Terrain;
                    if (_palette.IsWater(terrain) || used.Contains(step))
                    {
                        ended = true;
                        break;
                    }

                    if (bestHeight > height[IndexOf(here)] + 0.02)
                    {
                        // A basin with no way down: the river ends in a new lake.
                        lakes.Add(new(step, Cell(_palette.Lake, step, null)));
                        ended = true;
                        break;
                    }
                }

                if (!ended || run.Count < 5) continue;
                paths.Add(new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander, [.. run], [],
                    width: 1, name: "", seed: (int)(HexEditing.Noise(start, _settings.Seed) % 1000)));
                used.UnionWith(run);
                made++;
            }

            return _layer with { Paths = paths.ToImmutable(), Board = _board.With(lakes) };
        }

        public HexLayer Settlements()
        {
            var water = WaterDistance(6);
            var rivers = _layer.Paths.Where(p => p.Kind == HexPathKind.River).SelectMany(p => p.Hexes).ToHashSet();
            var scores = new List<(int Index, double Score)>();
            var landCount = 0;
            for (var i = 0; i < _columns * _rows; i++)
            {
                var hex = Hex(i);
                var cell = _board[hex];
                if (cell is null || _palette.IsWater(cell.Terrain)) continue;
                landCount++;
                var t = cell.Terrain;
                double score = t == _palette.Grass || t == _palette.Farmland ? 5
                    : t == _palette.Forest || t == _palette.Pines ? 2
                    : t == _palette.Hills ? 2
                    : t == _palette.Desert || t == _palette.Swamp || t == _palette.Jungle ? -1
                    : t == _palette.Snow ? -3 : -6;
                if (rivers.Contains(hex)) score += 4;
                if (water[i] == 1) score += 3;
                score += (HexEditing.Noise(hex, _settings.Seed ^ 0x5E7) % 1000) / 1000.0 * 3;
                scores.Add((i, score));
            }

            scores.Sort((a, b) => b.Score.CompareTo(a.Score));
            var wanted = Math.Max(1, (int)Math.Round(landCount / 1000.0 * _settings.SettlementsPerThousand));
            var cities = Math.Max(1, wanted / 6);
            var placedCount = 0;
            var cityCount = 0;
            // Spacing as blocked hexes: a city keeps 6 hexes free round it, every town 2.
            var nearAnyTown = new HashSet<HexCoord>();
            var nearCity = new HashSet<HexCoord>();
            var changes = new Dictionary<HexCoord, HexCell?>();
            foreach (var (index, score) in scores)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (placedCount >= wanted || score < 2) break;
                var hex = Hex(index);
                var city = cityCount < cities;
                if (city ? nearAnyTown.Contains(hex) || nearCity.Contains(hex) : nearAnyTown.Contains(hex) || nearCity.Contains(hex)) continue;
                var cell = _board[hex]!;
                var coastal = water[index] == 1;
                var feature = city ? (coastal ? _palette.Port : _palette.City)
                    : HexEditing.Noise(hex, _settings.Seed ^ 0xCA5) % 9 == 0 ? _palette.Castle : _palette.Village;
                changes[hex] = cell.WithFeature(feature, (int)(HexEditing.Noise(hex, 3) % 2));
                placedCount++;
                foreach (var near in HexCoord.Range(hex, city ? 6 : 2)) (city ? nearCity : nearAnyTown).Add(near);
                if (city) nearAnyTown.UnionWith(HexCoord.Range(hex, 2));
                if (!city) continue;
                cityCount++;
                // Fields round a city.
                foreach (var near in HexCoord.Range(hex, 2))
                {
                    if (near == hex || !_board.Contains(near) || changes.ContainsKey(near)) continue;
                    if (_board[near] is { } around && around.Terrain == _palette.Grass && around.Feature is null)
                    {
                        changes[near] = Cell(_palette.Farmland, near, around);
                    }
                }
            }

            // Mines on a few mountain hexes next to the land.
            foreach (var (index, _) in scores)
            {
                var hex = Hex(index);
                if (_board[hex] is not { } cell || cell.Terrain != _palette.Mountains || cell.Feature is not null) continue;
                if (HexEditing.Noise(hex, _settings.Seed ^ 0xA11) % 60 != 0) continue;
                changes[hex] = cell.WithFeature(_palette.Mine, 0);
            }

            return _layer with { Board = _board.With(changes) };
        }

        public HexLayer Roads()
        {
            var towns = _board.Cells
                .Where(pair => pair.Value.Feature is { } f && (f == _palette.City || f == _palette.Village || f == _palette.Castle || f == _palette.Port))
                .Select(pair => pair.Key).ToList();
            if (towns.Count < 2) return _layer;
            double? Cost(string terrain) =>
                terrain == _palette.Sea || terrain == _palette.Lake ? null
                : terrain == _palette.Shallows ? null
                : terrain == _palette.Peaks ? null
                : terrain == _palette.Mountains ? 6
                : terrain == _palette.Hills ? 2.5
                : terrain == _palette.Forest || terrain == _palette.Pines ? 2
                : terrain == _palette.Swamp || terrain == _palette.Jungle ? 3.5
                : terrain == _palette.Snow ? 3 : 1;
            var roadHexes = new HashSet<HexCoord>(_layer.Paths.Where(p => p.Kind == HexPathKind.Road).SelectMany(p => p.Hexes));
            double? Step(HexCoord _, HexCoord next)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (_board[next] is not { } cell) return null;
                // Following an existing road is cheap, so roads merge instead of running side by side.
                return roadHexes.Contains(next) ? 0.4 : Cost(cell.Terrain);
            }

            // Prim: grow a tree from the first town, always adding the town nearest to it (O(n²)).
            var count = towns.Count;
            var inTree = new bool[count];
            var best = new int[count];
            var parent = new int[count];
            Array.Fill(best, int.MaxValue);
            best[0] = 0;
            var paths = _layer.Paths.ToBuilder();
            for (var added = 0; added < count; added++)
            {
                _cancellation.ThrowIfCancellationRequested();
                var next = -1;
                for (var i = 0; i < count; i++)
                {
                    if (!inTree[i] && (next < 0 || best[i] < best[next])) next = i;
                }

                inTree[next] = true;
                for (var i = 0; i < count; i++)
                {
                    if (inTree[i]) continue;
                    var d = HexCoord.Distance(towns[next], towns[i]);
                    if (d < best[i])
                    {
                        best[i] = d;
                        parent[i] = next;
                    }
                }

                if (added == 0) continue;
                var (from, to, distance) = (towns[parent[next]], towns[next], best[next]);
                if (distance > 40) continue;
                var route = HexRouting.FindRoute(from, to, Step, 0.4, limit: 40_000);
                if (route is null || route.Count < 2) continue;
                paths.Add(new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [.. route], []));
                roadHexes.UnionWith(route);
            }

            return _layer with { Paths = paths.ToImmutable() };
        }
    }
}
