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

    /// <summary>
    /// States (R21): each capital (city or port) grows its land outwards by the cost of the way
    /// (<see cref="HexRouting"/> costs), and every state becomes a named region.
    /// </summary>
    States,
}

/// <summary>The kind of world the generator makes (R21).</summary>
public enum HexWorldMode
{
    /// <summary>Continents and seas.</summary>
    Normal = 0,
    /// <summary>Many small islands in a wide sea.</summary>
    Archipelago = 1,
    /// <summary>A hot, dry world: deserts, few rivers, oases at the water.</summary>
    Desert = 2,
    /// <summary>Подземье: caverns in solid rock, underground lakes and rivers, no sky and no seasons.</summary>
    Underdark = 3,
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

    /// <summary>
    /// How rivers are found (R21): 0 - each source runs downhill alone, as every recipe before R21;
    /// 1 - the flow of every hex is gathered downhill and the largest streams become rivers that
    /// join into one another.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int RiverModel { get; init; }

    public const int DownhillRivers = 0, FlowRivers = 1;

    /// <summary>
    /// The span of latitudes from the middle row to the top and bottom rows, 0..1: 1 puts the poles
    /// on both edges (every recipe before R21), less leaves the map without poles.
    /// </summary>
    public double LatitudeSpan { get; init; } = 1;

    /// <summary>Moves the whole map towards the poles (+) or the equator (−), −1..1.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public double LatitudeShift { get; init; }

    /// <summary>Land may reach the edges of the board: no sea is pressed in from the rim.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool OpenEdges { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public HexWorldMode Mode { get; init; }

    /// <summary>True when every number is finite and every choice known (a recipe read from a file).</summary>
    public bool IsValid => double.IsFinite(LandShare) && double.IsFinite(Fragmentation) && double.IsFinite(Relief)
        && double.IsFinite(Moisture) && double.IsFinite(Temperature) && double.IsFinite(RiversPerThousand)
        && double.IsFinite(SettlementsPerThousand) && double.IsFinite(LatitudeSpan) && double.IsFinite(LatitudeShift)
        && RiverModel is DownhillRivers or FlowRivers && Enum.IsDefined(Mode);
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
    /// <summary>
    /// Runs one step on a layer and returns the new layer (the input is unchanged). The board is
    /// worked through in chunks of rows; <paramref name="progress"/> hears 0..1 as they finish.
    /// </summary>
    public static HexLayer Run(HexLayer layer, HexGenerationStep step, HexGeneratorSettings settings, HexGeneratorPalette? palette = null,
        CancellationToken cancellationToken = default, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(settings);
        palette ??= new HexGeneratorPalette();
        cancellationToken.ThrowIfCancellationRequested();
        var world = new World(layer, settings, palette, cancellationToken, progress);
        var result = step switch
        {
            HexGenerationStep.Land => world.Land(),
            HexGenerationStep.Relief => world.Relief(),
            HexGenerationStep.Climate => world.Climate(),
            HexGenerationStep.Rivers => world.Rivers(),
            HexGenerationStep.Settlements => world.Settlements(),
            HexGenerationStep.States => world.States(),
            _ => world.Roads(),
        };
        progress?.Invoke(1);
        return result;
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
        private readonly Action<double>? _progress;
        private bool Underdark => _settings.Mode == HexWorldMode.Underdark;

        public World(HexLayer layer, HexGeneratorSettings settings, HexGeneratorPalette palette, CancellationToken cancellation,
            Action<double>? progress = null)
        {
            _cancellation = cancellation;
            _progress = progress;
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
            // An archipelago is the most broken coast there is.
            var fragmentation = _settings.Mode == HexWorldMode.Archipelago ? Math.Max(0.85, _settings.Fragmentation) : _settings.Fragmentation;
            // Islands are small: the noise of an archipelago runs at twice the frequency.
            var scale = (0.9 + 3.2 * Math.Clamp(fragmentation, 0, 1)) * (_settings.Mode == HexWorldMode.Archipelago ? 2.2 : 1);
            var rimFalloff = _settings.OpenEdges ? 0 : 1.1 - 0.6 * fragmentation;
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
                    var falloff = Math.Pow(Math.Clamp(rim, 0, 1), 2.6) * rimFalloff;
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
            var total = _columns * _rows;
            for (var index = 0; index < total; index++)
            {
                if ((index & 255) == 0) _cancellation.ThrowIfCancellationRequested();
                if ((index & 16383) == 0) _progress?.Invoke(index / (double)total);
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
            if (_settings.Mode == HexWorldMode.Archipelago) share = Math.Min(share, 0.32);
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

            if (Underdark)
            {
                // Caverns in rock: the high ground is open cave, the rest solid rock; the deepest
                // hollows of the rock hold underground lakes.
                var lakeLevel = sorted[Math.Clamp((int)(0.06 * sorted.Length), 0, sorted.Length - 1)];
                return With((index, hex, current) => land[index] ? Cell(_palette.Grass, hex, null)
                    : height[index] <= lakeLevel ? Cell(_palette.Lake, hex, null) : Cell(_palette.Mountains, hex, null)) with { Paths = [] };
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
            // Under the earth the rock is the relief; the cave floors stay as they are.
            if (Underdark) return _layer;
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
                // Latitude: the middle row is the warm belt, the top and bottom rows the poles; a
                // narrower span leaves the map without poles, a shift moves it north or south.
                var latitude = Math.Clamp(Math.Abs(((row + 0.5) / _rows * 2 - 1) * Math.Clamp(_settings.LatitudeSpan, 0, 1))
                    + Math.Clamp(_settings.LatitudeShift, -1, 1), 0, 1);
                var hot = _settings.Mode == HexWorldMode.Desert ? 0.5 : 0;
                var dry = _settings.Mode == HexWorldMode.Desert ? 0.9 : 0;
                var temperature = 1 - latitude * 1.05 + 0.25 * _settings.Temperature + hot
                    + (Fractal(x / span * 6, y / span * 6, _settings.Seed ^ 0x7E3, 3) - 0.5) * 0.3 - rank[index] * 0.25;
                var moisture = Fractal(x / span * 5, y / span * 5, _settings.Seed ^ 0x3C9, 4) * 0.75
                    + (1 - Math.Min(water[index], 8) / 8.0) * 0.35 + 0.25 * _settings.Moisture - dry;
                if (Underdark)
                {
                    // No sky and no seasons: the cave floor goes by damp alone - fungal bogs at the
                    // water, mushroom woods in the damp, dust where it is dry.
                    if (_palette.IsHigh(current.Terrain)) return current;
                    var cave = moisture > 0.86 && water[index] <= 2 ? _palette.Swamp
                        : moisture > 0.6 ? _palette.Forest
                        : moisture < 0.3 ? _palette.Desert : _palette.Grass;
                    return Cell(cave, hex, current);
                }
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
            if (_settings.RiverModel == HexGeneratorSettings.FlowRivers) return FlowRivers();
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
            var wanted = (int)Math.Round(landCount / 1000.0 * RiversPerThousand());
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

        /// <summary>Rivers per thousand: a desert has few.</summary>
        private double RiversPerThousand() => _settings.RiversPerThousand * (_settings.Mode == HexWorldMode.Desert ? 0.4 : 1);

        /// <summary>A hex the water can run over: land, and under the earth only the cave floor.</summary>
        private bool Flows(string? terrain) => terrain is not null && !_palette.IsWater(terrain) && !(Underdark && _palette.IsHigh(terrain));

        /// <summary>
        /// Rivers by flow accumulation (R21): every land hex drains to its lowest neighbour on the
        /// height field filled from the water inwards (priority flood), so no basin traps the
        /// water; the flow of every hex - rain, more where the climate is wet - is summed downhill,
        /// and the heads of the largest streams start rivers. A river ends where it reaches water
        /// or another river: tributaries join the main stream instead of running beside it.
        /// </summary>
        private HexLayer FlowRivers()
        {
            var height = Heights();
            var count = _columns * _rows;
            var downstream = new int[count];
            Array.Fill(downstream, -1);
            var filled = new double[count];
            var done = new bool[count];
            var order = new List<int>(count);
            var queue = new PriorityQueue<int, double>();
            var landCount = 0;
            for (var i = 0; i < count; i++)
            {
                var terrain = _board[Hex(i)]?.Terrain;
                if (Flows(terrain)) { landCount++; continue; }
                // Water, rock and empty hexes are where land drains to; rock never takes water in.
                done[i] = true;
                if (terrain is not null && _palette.IsWater(terrain)) { filled[i] = height[i]; queue.Enqueue(i, height[i]); }
            }
            // Land at the open edge of the board drains off it.
            for (var i = 0; i < count; i++)
            {
                var column = i % _columns; var row = i / _columns;
                if (!done[i] && (column == 0 || row == 0 || column == _columns - 1 || row == _rows - 1) && _settings.OpenEdges)
                { done[i] = true; filled[i] = height[i]; order.Add(i); queue.Enqueue(i, height[i]); }
            }
            while (queue.TryDequeue(out var here, out _))
            {
                if ((order.Count & 4095) == 0) { _cancellation.ThrowIfCancellationRequested(); _progress?.Invoke(0.6 * order.Count / Math.Max(1, landCount)); }
                foreach (var next in Hex(here).Neighbors())
                {
                    if (!_board.Contains(next)) continue;
                    var j = IndexOf(next);
                    if (done[j]) continue;
                    done[j] = true;
                    downstream[j] = here;
                    filled[j] = Math.Max(height[j], filled[here] + 1e-6);
                    order.Add(j);
                    queue.Enqueue(j, filled[j]);
                }
            }

            // Upstream hexes come later in the flood: summing in reverse carries every hex's water down.
            var span = Math.Max(_columns, _rows);
            var flow = new double[count];
            for (var k = order.Count - 1; k >= 0; k--)
            {
                var i = order[k];
                var (x, y) = Plane(Hex(i));
                flow[i] += 0.6 + Fractal(x / span * 5, y / span * 5, _settings.Seed ^ 0x3C9, 3) * 0.8;
                if (downstream[i] >= 0 && Flows(_board[Hex(downstream[i])]?.Terrain)) flow[downstream[i]] += flow[i];
            }

            // The threshold that gives about the wanted number of river heads.
            var wanted = Math.Max(0, (int)Math.Round(landCount / 1000.0 * RiversPerThousand()));
            if (wanted == 0 || order.Count == 0) return _layer;
            int Heads(double threshold)
            {
                var heads = 0;
                foreach (var i in order)
                    if (flow[i] >= threshold && !HasStreamAbove(i, threshold)) heads++;
                return heads;
            }
            bool HasStreamAbove(int i, double threshold)
            {
                foreach (var n in Hex(i).Neighbors())
                {
                    if (!_board.Contains(n)) continue;
                    var j = IndexOf(n);
                    if (downstream[j] == i && flow[j] >= threshold) return true;
                }
                return false;
            }
            double low = 2, high = Math.Max(3, order.Max(i => flow[i]));
            for (var step = 0; step < 24; step++)
            {
                _cancellation.ThrowIfCancellationRequested();
                var middle = (low + high) / 2;
                if (Heads(middle) > wanted) low = middle; else high = middle;
            }
            var threshold = high;
            _progress?.Invoke(0.85);

            var used = new HashSet<HexCoord>(_layer.Paths.Where(p => p.Kind == HexPathKind.River).SelectMany(p => p.Hexes));
            // The longest streams first: they claim the trunk, the others join it as tributaries.
            var heads = order.Where(i => flow[i] >= threshold && !HasStreamAbove(i, threshold))
                .OrderByDescending(Length).ThenBy(i => i).ToList();
            int Length(int i)
            {
                var n = 0;
                for (var at = i; at >= 0 && n < count; at = downstream[at])
                {
                    n++;
                    if (!Flows(_board[Hex(at)]?.Terrain)) break;
                }
                return n;
            }
            var paths = _layer.Paths.ToBuilder();
            foreach (var head in heads)
            {
                _cancellation.ThrowIfCancellationRequested();
                var run = new List<HexCoord>();
                var at = head;
                var ended = false;
                while (at >= 0 && run.Count < 4 * (_columns + _rows))
                {
                    var hex = Hex(at);
                    run.Add(hex);
                    if (used.Contains(hex) || !Flows(_board[hex]?.Terrain)) { ended = true; break; }
                    at = downstream[at];
                }
                if (!ended && at < 0 && _settings.OpenEdges) ended = true;
                if (!ended || run.Count < 4) continue;
                paths.Add(new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander, [.. run], [],
                    width: flow[head] > threshold * 4 ? 2 : 1, name: "", seed: (int)(HexEditing.Noise(run[0], _settings.Seed) % 1000)));
                used.UnionWith(run.Take(run.Count - 1));
            }
            return _layer with { Paths = paths.ToImmutable() };
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
                // Under the earth every rock face is a seam to dig: mines are three times as common.
                if (HexEditing.Noise(hex, _settings.Seed ^ 0xA11) % (Underdark ? 20u : 60u) != 0) continue;
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

        /// <summary>
        /// States (R21): every city and port is a capital; each grows its land outwards by the
        /// cheapest way (the costs of the roads, peaks and water stop it), so borders follow
        /// ridges, rivers and forests. Every state becomes a named region with its own tint. Hexes
        /// that already belong to a region keep it.
        /// </summary>
        public HexLayer States()
        {
            var capitals = _board.Cells
                .Where(pair => pair.Value.Feature is { } f && (f == _palette.City || f == _palette.Port))
                .Select(pair => pair.Key).OrderBy(h => h.R).ThenBy(h => h.Q).ToList();
            if (capitals.Count == 0) return _layer;
            double? Cost(string terrain) =>
                _palette.IsWater(terrain) || terrain == _palette.Peaks || (Underdark && _palette.IsHigh(terrain)) ? null
                : terrain == _palette.Mountains ? 6
                : terrain == _palette.Hills ? 2.5
                : terrain == _palette.Forest || terrain == _palette.Pines ? 2
                : terrain == _palette.Swamp || terrain == _palette.Jungle ? 3.5
                : terrain == _palette.Snow || terrain == _palette.Desert ? 3 : 1;
            var rivers = _layer.Paths.Where(p => p.Kind == HexPathKind.River).SelectMany(p => p.Hexes).ToHashSet();
            var count = _columns * _rows;
            var owner = new int[count];
            var cost = new double[count];
            Array.Fill(cost, double.MaxValue);
            var queue = new PriorityQueue<int, double>();
            for (var k = 0; k < capitals.Count; k++)
            {
                var i = IndexOf(capitals[k]);
                owner[i] = k + 1; cost[i] = 0; queue.Enqueue(i, 0);
            }
            var settled = 0;
            while (queue.TryDequeue(out var here, out var at))
            {
                if (at > cost[here]) continue;
                if ((++settled & 4095) == 0) { _cancellation.ThrowIfCancellationRequested(); _progress?.Invoke(0.9 * settled / count); }
                foreach (var next in Hex(here).Neighbors())
                {
                    if (!_board.Contains(next) || _board[next] is not { } cell || Cost(cell.Terrain) is not { } step) continue;
                    // Crossing a river is a frontier of its own.
                    var price = at + step + (rivers.Contains(next) && !rivers.Contains(Hex(here)) ? 2 : 0);
                    var j = IndexOf(next);
                    if (price >= cost[j]) continue;
                    cost[j] = price; owner[j] = owner[here]; queue.Enqueue(j, price);
                }
            }

            var regions = _layer.Regions.ToList();
            var firstId = regions.Count == 0 ? 1 : regions.Max(r => r.Id) + 1;
            var names = new HashSet<string>(regions.Select(r => r.Name));
            var idOf = new int[capitals.Count + 1];
            for (var k = 0; k < capitals.Count && firstId + k <= HexCell.MaxRegion; k++)
            {
                var id = firstId + k;
                idOf[k + 1] = id;
                var name = StateName(k, names);
                names.Add(name);
                regions.Add(new HexRegion(id, name, StateColors[k % StateColors.Length]));
            }
            var used = new HashSet<int>();
            var layer = With((index, hex, current) =>
            {
                if (current is null || current.Region != 0 || owner[index] == 0 || idOf[owner[index]] == 0) return current;
                used.Add(idOf[owner[index]]);
                return current.WithRegion(idOf[owner[index]]);
            });
            return layer with { Regions = [.. regions.Where(r => r.Id < firstId || used.Contains(r.Id))] };
        }

        /// <summary>Muted tints in the owner's palette, light enough to read the land through.</summary>
        private static readonly string[] StateColors =
        [
            "#66B5654A", "#665B7FA3", "#668C9A5B", "#66A3785B", "#667A6A9E", "#665E9A8C", "#66B39A4A", "#66996677",
            "#66617A52", "#66A35B6E", "#664F7090", "#66A88A62",
        ];

        private static readonly string[] Starts =
            ["Ар", "Бел", "Вер", "Гор", "Дал", "Зар", "Кар", "Лис", "Мир", "Нор", "Ост", "Рав", "Сол", "Тар", "Ул", "Хол", "Яр", "Вал", "Кре", "Сев"];
        private static readonly string[] Middles = ["", "", "ан", "ес", "ов", "ил", "ар", "ен", "им"];
        private static readonly string[] Ends = ["ия", "ань", "ор", "ем", "ск", "ава", "ония", "ель", "ин", "град", "ея", "аль"];

        /// <summary>A pronounceable name for the k-th state of this seed, unlike any name already used.</summary>
        private string StateName(int k, HashSet<string> taken)
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var noise = HexEditing.Noise(new HexCoord(k * 31 + attempt, k * 7 - attempt), _settings.Seed ^ 0x57A7E);
                var name = Starts[noise % (uint)Starts.Length] + Middles[(noise / 37) % (uint)Middles.Length] + Ends[(noise / 1013) % (uint)Ends.Length];
                if (!taken.Contains(name)) return name;
            }
            return $"Государство {k + 1}";
        }
    }
}
