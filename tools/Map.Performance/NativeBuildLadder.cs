using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Input;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>One build phase of native-build-ladder: what the editor commands cost and what the map holds after it.</summary>
internal sealed class BuildPhaseReport
{
    public required string Name { get; init; }
    public int ObjectsBefore { get; set; }
    public int ObjectsAfter { get; set; }
    /// <summary>Objects the phase aimed for (cumulative); the land and water phases add none.</summary>
    public int TargetAfter { get; set; }
    public int Rivers { get; set; }
    public int Roads { get; set; }
    public int UndoDepth { get; set; }
    public double BuildSeconds { get; set; }
    public long WorkingSetMiB { get; set; }
    public long PrivateMiB { get; set; }
    public List<BuildEditStat> Edits { get; } = [];
    public List<string> Shots { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
}

/// <summary>
/// One kind of edit command in a phase. Work = synchronous UI-thread time of the command (for an area placed in the
/// background: until it landed); FrameLatency = from the start of the command to the first finished canvas frame after it.
/// </summary>
internal sealed class BuildEditStat
{
    public required string Name { get; init; }
    public int Count { get; set; }
    public int Refused { get; set; }
    public int ObjectsAdded { get; set; }
    public double WorkTotalMs { get; set; }
    public double WorkP50 { get; set; }
    public double WorkP95 { get; set; }
    public double WorkMax { get; set; }
    public double FrameLatencyP95 { get; set; }
    public double FrameLatencyMax { get; set; }
    public double FrameWorkP95 { get; set; }
    public List<double> WorkMs { get; } = [];
    public List<double> FrameLatencyMs { get; } = [];
    public List<double> FrameWorkMs { get; } = [];
}

/// <summary>
/// Owner 2026-11-16: «живой прогон поочерёдно заполняет разными объектами, строя то комнаты, то континенты, как обычно
/// люди делают карты, и проверяет, как идёт нагрузка; каждый прогон разный по визуалу». The real editor window builds a
/// map phase by phase through the commands a user triggers (canvas gestures and panel commands, every edit in Undo):
/// continents and sea, rivers and lakes, relief with mountains and forests in the four placement modes, roads, towns
/// with an interior of rooms and a corridor, areas with rules, labels and decorations, then more objects up to
/// --object-count. Layout, order inside a phase and the library sets come from one seed (--seed, MAP_BUILD_SEED or the
/// clock), written to the report. After every phase: the edit-command timings and the camera routes of the FPS grid.
/// MAP_BUILD_OPEN=&lt;saved map&gt; instead opens a map this profile saved, in a new process, and measures the open.
/// </summary>
public sealed partial class ZoomAssetsApplication
{
    private const int BuildRouteSteps = 240;
    private Random _rng = null!;
    private int _side;
    private LayerId _landId, _objectsId;
    private TerrainProbe? _probe;
    private BuildPhaseReport _buildPhase = null!;
    private readonly List<(double X, double Y, double Rx, double Ry)> _continents = [];
    private readonly List<(double X, double Y, int Continent)> _riverSources = [];
    private readonly List<(double X, double Y, int Continent, string Name)> _towns = [];
    private (double X, double Y) _interiorSite;
    private int _roomsLostByInspector;
    private bool _interiorPlanned;
    private string _shotsFolder = "";
    private string _shotPrefix = "";

    private static readonly string[] MountainSets = ["Горы", "Столовые горы", "Вулканы", "Скалы"];
    private static readonly string[] HillSets = ["Холмы", "Скалы", "Горы"];
    private static readonly string[] ForestSets = ["Деревья", "Ели", "Дерево", "Кусты"];
    private static readonly string[] NatureSets = ["Деревья", "Ели", "Кусты", "Трава", "Холмы", "Скалы", "Растения", "Цветы", "Дерево"];
    private static readonly string[] HouseSets = ["Дома", "Деревня", "Город", "Города", "Башня", "Башни", "Мельница"];
    private static readonly string[] LandmarkSets = ["Замок", "Крепость", "Цитадель", "Храм", "Город", "Башня"];
    private static readonly string[] FurnitureSets = ["Стулья", "Столы", "Диваны", "Кровати", "Кресла", "Шкафы", "Прямоугольный стол",
        "Круглый стол", "Растения в горшках", "Угловой стол", "Холодильники", "Плиты"];
    private static readonly string[] PreferredCatalogs = ["fantasy-world", "fantasy-regional", "parchment-world", "parchment-world-2", "modern-interiors-1"];

    private async Task ExecuteBuildLadder()
    {
        var reopen = Environment.GetEnvironmentVariable("MAP_BUILD_OPEN");
        var seed = Options.SeedGiven ? Options.Seed
            : int.TryParse(Environment.GetEnvironmentVariable("MAP_BUILD_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var envSeed) ? envSeed
            : (int)(DateTime.UtcNow.Ticks % 1_000_000_000);
        _rng = new Random(seed);
        _report.BuildPhases = [];
        _report.Observation = "Visible native editor on the selected test display; map built through editor commands (canvas gestures and panel commands, Undo history); frame = render-thread scene completion, not monitor scan-out.";
        _report.Environment["mode"] = reopen is null ? "build" : "reopen";
        _report.Environment["seed"] = seed.ToString(CultureInfo.InvariantCulture);
        _report.Environment["target-objects"] = Options.ObjectCount.ToString(CultureInfo.InvariantCulture);
        _report.Environment["derived-root"] = Environment.GetEnvironmentVariable("MAP_PERF_DERIVED_ROOT") ?? "disabled";
        _report.Environment["os"] = Environment.OSVersion.VersionString;
        _report.Environment["cpu-threads"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
        _report.Environment["window-logical"] = $"{_window.Width}x{_window.Height}";
        _report.Environment["render-scaling"] = _window.RenderScaling.ToString("0.###", CultureInfo.InvariantCulture);
        _shotsFolder = Environment.GetEnvironmentVariable("MAP_PERF_SHOTS") ?? Path.GetDirectoryName(Options.OutputPath)!;
        _shotPrefix = Environment.GetEnvironmentVariable("MAP_BUILD_TAG") is { Length: > 0 } tag ? tag + "-" : "";
        Directory.CreateDirectory(_shotsFolder);
        MountBuildLibrary();
        Console.WriteLine($"build-ladder: mode={_report.Environment["mode"]} seed={seed} target={Options.ObjectCount} sets={_catalog.Sets.Count}");
        if (reopen is not null) { await ExecuteBuildReopen(reopen); return; }

        _side = int.TryParse(Environment.GetEnvironmentVariable("MAP_BUILD_SIDE"), out var side) && side >= 4096 ? side : SideFor(Options.ObjectCount);
        _report.Environment["canvas-side"] = _side.ToString(CultureInfo.InvariantCulture);
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, TileMap.Empty(1), CurrentLook.Land());
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        _landId = land.Id; _objectsId = objects.Id;
        var document = new MapDocument(DocumentId.New(), $"Лестница постройки · {seed}", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(_side, _side, "px", CurrentLook.PageColor), [CurrentLook.Page("Море"), land, objects]);
        OpenEditor(document);
        await NextBuildFrame();
        _view.Canvas.FitToCanvas();
        await NextBuildFrame();
        var total = Options.ObjectCount;
        var runClock = Stopwatch.StartNew();

        await BuildPhase("continents", 0, BuildContinents, () => (_continents[0].X, _continents[0].Y));
        await BuildPhase("rivers", 0, BuildWater, () => _riverSources.Count > 0 ? (_riverSources[0].X, _riverSources[0].Y) : (_continents[0].X, _continents[0].Y));
        await BuildPhase("relief", (int)(total * .35), target => BuildRelief(target), () => (_continents[^1].X, _continents[^1].Y));
        await BuildPhase("roads", (int)(total * .35), _ => BuildRoads(), () => (_towns[0].X, _towns[0].Y));
        await BuildPhase("towns", (int)(total * .5), target => BuildTowns(target), () => _interiorSite);
        await BuildPhase("regions", (int)(total * .6), target => BuildRegions(target), () => (_continents[0].X, _continents[0].Y));
        await BuildPhase("fill", total, target => Scatter(target, NatureSets.Concat(HouseSets).ToArray(), "fill/scatter"), () => RandomLandPoint());

        // History: the last command goes back and forward as one step.
        var count = CountObjects();
        _editor.Undo(); var undone = CountObjects();
        _editor.Redo();
        _report.Checks["undo-redo-last-command"] = undone <= count && CountObjects() == count;
        _report.Checks["reached-target"] = count >= total;
        _report.Checks["gpu"] = _report.Scenarios.All(s => s.Routes.All(r => r.Gpu));
        _report.Environment["build-minutes"] = runClock.Elapsed.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture);
        _report.Environment["objects-final"] = count.ToString(CultureInfo.InvariantCulture);

        // Atomic save as the editor does («Сохранить как»), into the run folder: the warm reopen opens that file.
        var saveClock = Stopwatch.StartNew();
        var saved = await _editor.SaveAsync(saveAs: true);
        _report.Environment["save-ms"] = saveClock.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        var savedPath = Path.Combine(_workspace, "zoom.mapproj");
        var target = Environment.GetEnvironmentVariable("MAP_BUILD_SAVE") ?? Path.ChangeExtension(Options.OutputPath, ".mapproj");
        if (saved && File.Exists(savedPath))
        {
            File.Move(savedPath, target, overwrite: true);
            _report.Environment["saved-map"] = target;
            _report.Environment["saved-map-mib"] = (new FileInfo(target).Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        }
        _report.Checks["saved"] = saved && File.Exists(target);
        PrintBuildTable();
        BuildCheckpoint();
    }

    /// <summary>Same canvas sides as Run-Ladder.ps1 for the larger counts; a smaller page below 50 000.</summary>
    private static int SideFor(int count) => count <= 50_000 ? 16384 : count <= 500_000 ? 32768 : count <= 1_000_000 ? 65536 : 131072;

    private void MountBuildLibrary()
    {
        var root = Environment.GetEnvironmentVariable("MAP_DRIVE_LIBRARY_ROOTS")
            ?? throw new InvalidOperationException("Set MAP_DRIVE_LIBRARY_ROOTS to the owner's library.");
        var all = Directory.GetDirectories(root).Where(p => File.Exists(Path.Combine(p, "catalog.json"))).Order().ToArray();
        string[] mounts;
        if (Environment.GetEnvironmentVariable("MAP_PERF_CATALOGS") is { Length: > 0 } names)
        {
            var wanted = names.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            mounts = all.Where(p => wanted.Contains(Path.GetFileName(p))).ToArray();
        }
        else
        {
            mounts = all.Where(p => PreferredCatalogs.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).ToArray();
            if (mounts.Length == 0) mounts = all;
        }
        if (mounts.Length == 0) throw new InvalidOperationException("No library catalog under " + root);
        _catalog.SetMounts(mounts);
        _report.Environment["library"] = root;
        _report.Environment["catalogs"] = string.Join(";", mounts.Select(Path.GetFileName));
        _report.Environment["library-sets"] = _catalog.Sets.Count(s => !s.Archived).ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ phases

    private async Task BuildPhase(string name, int targetAfter, Func<int, Task> build, Func<(double X, double Y)> focus)
    {
        Console.WriteLine($"=== phase {name}: objects {CountObjects()} -> target {targetAfter}");
        _buildPhase = new BuildPhaseReport { Name = name, ObjectsBefore = CountObjects(), TargetAfter = targetAfter };
        _report.BuildPhases!.Add(_buildPhase);
        var clock = Stopwatch.StartNew();
        await build(targetAfter);
        _buildPhase.BuildSeconds = clock.Elapsed.TotalSeconds;
        _editor.CancelPlacement(); _editor.DeselectObject(); _editor.ActiveTool = EditorTool.Select;
        var phase = _buildPhase;
        phase.ObjectsAfter = CountObjects();
        phase.Rivers = _editor.Rivers.Count; phase.Roads = _editor.Roads.Count;
        phase.UndoDepth = _editor.Session.UndoDepth;
        foreach (var stat in phase.Edits) CompleteEditStat(stat);
        Console.WriteLine($"    phase {name}: objects {phase.ObjectsBefore} -> {phase.ObjectsAfter}, edits {phase.Edits.Sum(e => e.Count)} in {phase.BuildSeconds:0.0} s, undo depth {phase.UndoDepth}");
        BuildCheckpoint();
        var scenario = new ZoomScenarioReport { Name = name, CanvasWidth = _side, CanvasHeight = _side, Objects = phase.ObjectsAfter };
        _report.Scenarios.Add(scenario);
        var (ws, pb) = Memory();
        phase.WorkingSetMiB = ws; phase.PrivateMiB = pb;
        if (Environment.GetEnvironmentVariable("MAP_BUILD_NO_ROUTES") == "1") { _view.Canvas.FitToCanvas(); await NextBuildFrame(); await BuildShot(name + "-fit"); }
        else await BuildCameraRoutes(scenario, focus(), name, deadline: null);
        await Finish(scenario);
        BuildCheckpoint();
    }

    private async Task BuildContinents(int _)
    {
        _probe = null;
        var count = 1 + _rng.Next(3);
        // Continents in their own cells of the page, so they stay apart and the sea reaches every coast.
        var cells = count == 1 ? new[] { (.5, .5) } : count == 2
            ? (_rng.Next(2) == 0 ? new[] { (.3, .5), (.72, .5) } : new[] { (.5, .3), (.5, .72) })
            : new[] { (.28, .3), (.72, .32), (.5, .74) };
        foreach (var (cx, cy) in cells)
        {
            var scale = count == 1 ? .26 : count == 2 ? .16 : .13;
            _continents.Add((_side * (cx + (_rng.NextDouble() - .5) * .06), _side * (cy + (_rng.NextDouble() - .5) * .06),
                _side * scale * (.75 + _rng.NextDouble() * .4), _side * scale * (.65 + _rng.NextDouble() * .4)));
        }
        _editor.SelectLayer(_landId);
        _editor.ActiveTool = EditorTool.Land; _editor.SetLandPaintMode(false);
        _view.Canvas.FitToCanvas();
        var defaults = NewMapDefaults.For(_editor.Session.Document.Canvas);
        foreach (var c in _continents)
        {
            // The body: a core ellipse and lobes reaching past it (the «Эллипс» land shape), capes as «Многоугольник».
            _editor.LandSubtract = false;
            var lobes = 3 + _rng.Next(4);
            for (var i = 0; i < lobes; i++)
            {
                var cape = i > 0 && _rng.Next(3) == 0;
                _editor.ActiveLandShape = cape ? LandShapeKind.Polygon : LandShapeKind.Ellipse;
                var rx = c.Rx * (i == 0 ? .8 : cape ? .25 + _rng.NextDouble() * .2 : .3 + _rng.NextDouble() * .4);
                var ry = c.Ry * (i == 0 ? .8 : cape ? .3 + _rng.NextDouble() * .3 : .3 + _rng.NextDouble() * .4);
                var angle = _rng.NextDouble() * Math.Tau;
                var reach = i == 0 ? 0 : .55 + _rng.NextDouble() * .4;
                var ox = Math.Cos(angle) * c.Rx * reach; var oy = Math.Sin(angle) * c.Ry * reach;
                await Cmd(cape ? "land-polygon-cape" : "land-ellipse", () => LandChanged(() => Drag(c.X + ox - rx, c.Y + oy - ry, c.X + ox + rx, c.Y + oy + ry)));
            }
            // The coast: rough freehand strokes along the shore, out (capes) and in (bays).
            _editor.ActiveLandShape = LandShapeKind.Circle;
            _editor.LandRoughness = defaults.LandRoughness * (.6 + _rng.NextDouble());
            _editor.BrushSettings = _editor.BrushSettings with { Radius = defaults.LandBrushRadius * (.7 + _rng.NextDouble() * .6), Softness = .5, Spacing = .25 };
            var strokes = 8 + _rng.Next(8);
            for (var i = 0; i < strokes; i++)
            {
                var subtract = i % 3 == 2;
                _editor.LandSubtract = subtract;
                var start = _rng.NextDouble() * Math.Tau; var arc = .3 + _rng.NextDouble() * .8;
                var wobble = .08 + _rng.NextDouble() * .15;
                var phase = _rng.NextDouble() * Math.Tau;
                var points = Enumerable.Range(0, 18).Select(k =>
                {
                    var a = start + arc * k / 17;
                    var r = (subtract ? .9 : 1.0) + Math.Sin(phase + k * .9) * wobble;
                    return (c.X + Math.Cos(a) * c.Rx * r, c.Y + Math.Sin(a) * c.Ry * r);
                }).ToArray();
                await Cmd(subtract ? "land-bay-stroke" : "land-coast-stroke", () => LandChanged(() => Stroke(points)));
            }
            _editor.LandSubtract = false;
        }
        // A few islands in the open sea: short freehand strokes.
        var islands = 2 + _rng.Next(5);
        for (var i = 0; i < islands; i++)
        {
            var p = RandomSeaPoint();
            var points = Enumerable.Range(0, 5).Select(k => (p.X + k * defaults.LandBrushRadius * .6, p.Y + (_rng.NextDouble() - .5) * defaults.LandBrushRadius)).ToArray();
            await Cmd("land-island-stroke", () => LandChanged(() => Stroke(points)));
        }
        _probe = new TerrainProbe(_editor.Session.Document);
        // Land material: a ground texture painted over part of each continent.
        var materials = MapFillTextures.Of(MapFillTextureKind.Ground).Where(m => MapTextureLibrary.Image(m.Id) is not null).ToArray();
        if (materials.Length > 0)
        {
            _editor.SetLandPaintMode(true);
            var material = materials[_rng.Next(materials.Length)];
            _buildPhase.Notes["land-material"] = material.Id;
            _editor.ChooseLandMaterial(material.Id);
            _editor.BrushSettings = _editor.BrushSettings with { Radius = defaults.LandBrushRadius, Softness = .8, Spacing = .15 };
            foreach (var c in _continents)
            {
                var points = Enumerable.Range(0, 10).Select(k => (c.X - c.Rx * .6 + c.Rx * 1.2 * k / 9, c.Y + Math.Sin(k * .7 + _rng.NextDouble()) * c.Ry * .3)).ToArray();
                await Cmd("land-material-stroke", () => LandChanged(() => Stroke(points), paint: true));
            }
            _editor.SetLandPaintMode(false);
        }
        var manners = Enum.GetValues<SeaManner>();
        var manner = manners[_rng.Next(manners.Length)];
        _buildPhase.Notes["sea"] = manner.ToString();
        await Cmd("sea-manner", () => { _editor.ApplySeaManner(manner); return _editor.Sea?.Manner == manner; });
        _editor.ActiveTool = EditorTool.Select;
        _editor.SelectLayer(_objectsId);
        _report.Checks["continents/land-drawn"] = (_editor.Session.Document.FindLayer(_landId) as LandLayer)?.GeometryTiles.TileCount > 0;
        _report.Checks["continents/land-material"] = materials.Length == 0 || (_editor.Session.Document.FindLayer(_landId) as LandLayer)?.PaintTiles.TileCount > 0;
    }

    private async Task BuildWater(int _)
    {
        _view.Canvas.FitToCanvas();
        _editor.SelectLayer(_objectsId);
        for (var ci = 0; ci < _continents.Count; ci++)
        {
            var wanted = 2 + _rng.Next(3);
            for (var tries = 0; tries < wanted * 6 && _riverSources.Count(s => s.Continent == ci) < wanted; tries++)
            {
                if (CoastInland(ci, 0.12 + _rng.NextDouble() * 0.10) is not { } source) continue;
                var before = _editor.Rivers.Count;
                var ok = await Cmd("river-to-sea", () =>
                {
                    _editor.CancelPlacement(); _editor.ActiveTool = EditorTool.Select;
                    if (_editor.GeographyPick != GeographyPick.None) _editor.ArmRiverToSea();
                    _editor.ArmRiverToSea();
                    Click(source.X, source.Y);
                    return _editor.Rivers.Count > before;
                });
                if (ok) _riverSources.Add((source.X, source.Y, ci));
                else Console.WriteLine($"    river probe {source.X:0},{source.Y:0}: land={IsLand(source.X, source.Y)} route={(Geography.RouteRiver(_editor.Session.Document, new VectorPoint(source.X, source.Y)) is { } r ? r.Count.ToString() : "none")} pick={_editor.GeographyPick} tool={_editor.ActiveTool}");
            }
            var lakes = 1 + _rng.Next(2);
            for (var i = 0; i < lakes; i++)
            {
                var centre = RandomLandPoint(ci, .55);
                var radius = _side * (.006 + _rng.NextDouble() * .01);
                var nodes = 7 + _rng.Next(4);
                var before = _editor.Rivers.Count;
                await Cmd("lake", () =>
                {
                    _editor.StartLake();
                    for (var k = 0; k < nodes; k++)
                    {
                        var a = Math.Tau * k / nodes; var r = radius * (.7 + _rng.NextDouble() * .5);
                        _editor.AddRiverNode(centre.X + Math.Cos(a) * r * 1.3, centre.Y + Math.Sin(a) * r);
                    }
                    return _editor.FinishRiver() && _editor.Rivers.Count > before;
                });
            }
        }
        _editor.ActiveTool = EditorTool.Select;
        _report.Checks["rivers/river-to-sea"] = _riverSources.Count > 0;
        _report.Checks["rivers/lakes"] = _editor.Rivers.Any(r => r.Channels.Any(c => c.Closed));
        PlanSettlements();
    }

    private async Task BuildRelief(int target)
    {
        _view.Canvas.FitToCanvas();
        var defaults = NewMapDefaults.For(_editor.Session.Document.Canvas);
        // Ridges: through the river heads of a continent, pulled inland - the rivers run from the mountains to the sea.
        var ridges = new List<(double X, double Y)[]>();
        for (var ci = 0; ci < _continents.Count; ci++)
        {
            var c = _continents[ci];
            var heads = _riverSources.Where(s => s.Continent == ci).ToArray();
            (double X, double Y) a, b;
            if (heads.Length >= 2) { a = (heads[0].X, heads[0].Y); b = (heads[1].X, heads[1].Y); }
            else { var t = _rng.NextDouble() * Math.Tau; a = (c.X + Math.Cos(t) * c.Rx * .5, c.Y + Math.Sin(t) * c.Ry * .5); b = (c.X - Math.Cos(t) * c.Rx * .5, c.Y - Math.Sin(t) * c.Ry * .5); }
            var ridge = Enumerable.Range(0, 6).Select(k =>
            {
                var t = k / 5.0; var x = a.X + (b.X - a.X) * t; var y = a.Y + (b.Y - a.Y) * t;
                var pull = .25 + Math.Sin(t * Math.PI) * .2;
                return (x + (c.X - x) * pull, y + (c.Y - y) * pull);
            }).ToArray();
            ridges.Add(ridge);
        }
        // Relief: the ground raised along each ridge with the land relief brush.
        _editor.SelectLayer(_landId); _editor.ActiveTool = EditorTool.Land; _editor.SetLandPaintMode(false);
        _editor.ActiveLandShape = LandShapeKind.Circle;
        _editor.SetLandReliefMode(true); _editor.SetLandReliefAction(ReliefAction.Raise);
        _editor.BrushSettings = _editor.BrushSettings with { Radius = defaults.LandBrushRadius, Softness = .7, Spacing = .25 };
        foreach (var ridge in ridges) await Cmd("relief-raise-stroke", () => { Stroke(ridge); return true; });
        _editor.SetLandReliefMode(false);
        _editor.ActiveTool = EditorTool.Select; _editor.SelectLayer(_objectsId);

        // «Серия»: a mountain range dragged along each ridge.
        foreach (var ridge in ridges)
        {
            if (!await Arm(MountainSets, StampPlacementMode.Series, 35 + _rng.Next(35))) break;
            _editor.SetSeriesSpacingPercent(55 + _rng.Next(35), false);
            await Cmd("mountains/series", () => Stroke(ridge));
        }
        // «Путь»: foothills along a path beside the ridge.
        foreach (var ridge in ridges)
        {
            if (!SelectSet(HillSets)) break;
            await Settle();
            var offset = _side * .02 * (_rng.Next(2) == 0 ? 1 : -1);
            _editor.StartEditablePath();
            SetFutureScale(18 + _rng.Next(18));
            _editor.RandomVariantAfterPlacement = true;
            await PrepareAssets();
            await Cmd("hills/path", () =>
            {
                foreach (var (x, y) in ridge) _editor.AddEditablePathNode(x + offset, y + offset * .6);
                return _editor.FinishEditablePath();
            });
        }
        // «Область»: forests drawn as areas.
        for (var ci = 0; ci < _continents.Count; ci++)
        {
            if (!await BeginArea(ForestSets)) break;
            var centre = RandomLandPoint(ci, .6);
            await AreaCmd("forest/area", centre, (int)(target * .12 / _continents.Count), rules: false);
        }
        // «Россыпь»: groves and copses clicked over the land until the phase holds its share.
        await Scatter(target, ForestSets.Concat(NatureSets).ToArray(), "nature/scatter");
        _report.Checks["relief/series"] = _buildPhase.Edits.Any(e => e.Name == "mountains/series" && e.ObjectsAdded > 0);
        _report.Checks["relief/path"] = _editor.EditablePaths.Count > 0;
        _report.Checks["relief/area"] = _buildPhase.Edits.Any(e => e.Name == "forest/area" && e.Count > e.Refused);
        _report.Checks["relief/scatter"] = _buildPhase.Edits.Any(e => e.Name == "nature/scatter" && e.ObjectsAdded > 0);
    }

    /// <summary>
    /// Where the towns and the interior will be (a plan, no command): decided once the land and water exist, so the
    /// forests and the fill keep off the building the way a person leaves room for it.
    /// </summary>
    private void PlanSettlements()
    {
        for (var ci = 0; ci < _continents.Count; ci++)
        {
            var count = 2 + _rng.Next(3);
            for (var i = 0; i < count; i++)
            {
                var p = RandomLandPoint(ci, .75);
                _towns.Add((p.X, p.Y, ci, PlaceName()));
            }
        }
        _interiorSite = FindInteriorSite(_towns[0].X, _towns[0].Y);
        _interiorPlanned = true;
    }

    private async Task BuildRoads()
    {
        _view.Canvas.FitToCanvas();
        foreach (var group in _towns.GroupBy(t => t.Continent))
        {
            var list = group.ToList();
            for (var i = 1; i < list.Count; i++)
            {
                var from = list[i - 1]; var to = list[i];
                var before = _editor.Roads.Count;
                await Cmd("road-auto", () =>
                {
                    _editor.CancelPlacement(); _editor.ActiveTool = EditorTool.Select;
                    if (_editor.GeographyPick != GeographyPick.None) _editor.ArmAutoRoad();
                    _editor.ArmAutoRoad();
                    Click(from.X, from.Y); Click(to.X, to.Y);
                    return _editor.Roads.Count > before;
                });
            }
        }
        // The road's name along it.
        foreach (var road in _editor.Roads.Take(2).ToArray())
            await Cmd("road-label-on-path", () => { _editor.SelectRoad(road.Id); return _editor.AddLabelAlongSelectedPath(TextPathTarget.Road); });
        _editor.DeselectObject();
        _report.Checks["roads/auto-road"] = _editor.Roads.Count > 0;
    }

    private async Task BuildTowns(int target)
    {
        _view.Canvas.FitToCanvas();
        var share = Math.Max(0, target - CountObjects());
        var per = Math.Max(10, share / Math.Max(1, _towns.Count));
        foreach (var town in _towns)
        {
            // The town: a landmark placed once, its houses scattered around it, its name.
            if (await Arm(LandmarkSets, StampPlacementMode.One, 10 + _rng.Next(10)))
                await Cmd("town/landmark", () => PlaceAt(town.X, town.Y));
            // Every town gets its houses, even when an earlier phase already passed this phase's share.
            var goal = Math.Max(CountObjects() + 12, Math.Min(target, CountObjects() + per));
            var radius = Math.Min(4096, _side * (.01 + _rng.NextDouble() * .015));
            while (CountObjects() < goal)
            {
                if (!await Arm(HouseSets, StampPlacementMode.Area, 3 + _rng.Next(4))) break;
                var n = Math.Min(goal - CountObjects(), ScatterPerClick());
                _editor.StampAreaRadius = radius;
                _editor.StampDensity = n * StampPlacement.DensityReferenceArea / (Math.PI * radius * radius);
                _editor.ArmStampPlacement();
                var a = _rng.NextDouble() * Math.Tau; var d = radius * _rng.NextDouble();
                if (!await Cmd("town/houses-scatter", () => PlaceAt(town.X + Math.Cos(a) * d, town.Y + Math.Sin(a) * d))) break;
            }
            _editor.CancelPlacement();
            await Label(town.X, town.Y - _side * .012, town.Name, LabelPreset.City, "town/label");
        }
        // An interior beside the first town: rooms along a corridor, doors, furniture - at 100 %.
        await BuildInterior();
        _report.Checks["towns/houses"] = _buildPhase.Edits.Any(e => e.Name == "town/houses-scatter" && e.ObjectsAdded > 0);
    }

    private async Task BuildInterior()
    {
        SetScale(_interiorSite, 1);
        await NextBuildFrame();
        _editor.ActiveTool = EditorTool.Select; _editor.SelectLayer(_objectsId);
        var rooms = new List<(double X, double Y, double W, double H)>();
        var corridorW = 900 + _rng.Next(300); var corridorH = 90 + _rng.Next(40);
        var cx = _interiorSite.X - corridorW / 2; var cy = _interiorSite.Y - corridorH / 2;
        rooms.Add((cx, cy, corridorW, corridorH));
        var x = cx;
        while (x < cx + corridorW - 200)
        {
            var w = 220 + _rng.Next(200); var h = 200 + _rng.Next(160);
            if (x + w > cx + corridorW) w = (int)(cx + corridorW - x);
            if (_rng.Next(3) != 0) rooms.Add((x, cy - h, w, h));
            if (_rng.Next(3) != 0) rooms.Add((x, cy + corridorH, w, h));
            x += w;
        }
        var made = 0;
        foreach (var (rx, ry, rw, rh) in rooms)
        {
            var ok = await Cmd(made == 0 ? "interior/corridor" : "interior/room", () =>
            {
                if (!_editor.CreateRoom()) return false;
                _editor.RefreshInspector();
                _editor.XText = ((int)rx).ToString(CultureInfo.InvariantCulture); _editor.YText = ((int)ry).ToString(CultureInfo.InvariantCulture);
                _editor.WidthText = ((int)rw).ToString(CultureInfo.InvariantCulture); _editor.HeightText = ((int)rh).ToString(CultureInfo.InvariantCulture);
                return _editor.CommitInspector();
            });
            if (!ok) continue;
            // Product defect seen 2026-11-16: the inspector's X/Y/W/H commit rebuilds the object without its Room
            // (EditorViewModel.Inspector WithGeometry), so the room turns back into a plain shape. Counted, then the
            // shape is made a room again with «Сделать комнатой», as a user would.
            if (_editor.Session.SelectedObject is { Room: null, Shape: not null })
            {
                _roomsLostByInspector++;
                await Cmd("interior/make-room", () => _editor.MakeSelectedRoom());
            }
            made++;
            await Cmd("interior/door", () =>
            {
                var done = _editor.AddRoomDoor();
                if (!done) Console.WriteLine($"    door: selected={_editor.Session.SelectedObjectIds.Count} room={_editor.Session.SelectedObject?.Room is not null} shape={_editor.Session.SelectedObject?.Shape?.Regions.Count} status={_editor.StatusDetail}");
                return done;
            });
        }
        _editor.DeselectObject();
        // Furniture: single placements inside the rooms.
        var furniture = 0;
        foreach (var (rx, ry, rw, rh) in rooms.Skip(1))
        {
            var pieces = 2 + _rng.Next(4);
            for (var i = 0; i < pieces; i++)
            {
                if (!await Arm(FurnitureSets, StampPlacementMode.One, 3 + _rng.Next(3))) break;
                if (await Cmd("interior/furniture", () => PlaceAt(rx + rw * (.2 + _rng.NextDouble() * .6), ry + rh * (.2 + _rng.NextDouble() * .6)))) furniture++;
            }
        }
        _editor.CancelPlacement(); _editor.ActiveTool = EditorTool.Select;
        _buildPhase.Notes["interior-rooms"] = made.ToString(CultureInfo.InvariantCulture);
        _buildPhase.Notes["interior-furniture"] = furniture.ToString(CultureInfo.InvariantCulture);
        _buildPhase.Notes["defect/inspector-resize-dropped-room"] = _roomsLostByInspector.ToString(CultureInfo.InvariantCulture);
        _report.Checks["towns/interior-rooms"] = made >= 3 && _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Count(o => o.Room is not null) >= 3;
        _report.Checks["towns/interior-doors"] = _buildPhase.Edits.Any(e => e.Name == "interior/door" && e.Count > e.Refused);
        _report.Checks["towns/furniture"] = furniture > 0;
        _view.Canvas.FitToCanvas();
    }

    private async Task BuildRegions(int target)
    {
        _view.Canvas.FitToCanvas();
        // Areas with rules: forests that keep off the water and the roads and stay on land, each named inside.
        var areas = 1 + _rng.Next(3);
        for (var i = 0; i < areas; i++)
        {
            if (!await BeginArea(ForestSets)) break;
            var ci = _rng.Next(_continents.Count);
            var centre = RandomLandPoint(ci, .5);
            if (!await AreaCmd("region/area-with-rules", centre, (int)(Options.ObjectCount * .06 / areas), rules: true)) continue;
            if (_editor.SelectedEditableArea is { } area)
                await Cmd("region/area-label", () => { _editor.SelectEditableArea(area.Id); return _editor.AddRegionLabel(); });
        }
        _editor.DeselectObject();
        // River names along the rivers.
        foreach (var river in _editor.Rivers.Where(r => r.Channels.Any(c => !c.Closed)).Take(3).ToArray())
            await Cmd("region/river-label-on-path", () => { _editor.SelectRiver(river.Id); return _editor.AddLabelAlongSelectedPath(TextPathTarget.River); });
        _editor.DeselectObject();
        // Country and sea names.
        foreach (var c in _continents) await Label(c.X, c.Y, PlaceName(), LabelPreset.Country, "region/country-label");
        var sea = RandomSeaPoint();
        await Label(sea.X, sea.Y, "Море " + PlaceName(), LabelPreset.Sea, "region/sea-label");
        // Decorations in the corners: a compass, a scale bar, a cartouche, a legend.
        var corners = new[] { (.1, .1), (.9, .9), (.5, .06), (.9, .12) };
        var kinds = new[] { DecorationKind.Compass, DecorationKind.ScaleBar, DecorationKind.Cartouche, DecorationKind.Legend };
        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            SetScale((_side * corners[i].Item1, _side * corners[i].Item2), _view.Canvas.Bounds.Width / (_side / 4.0));
            await Cmd("region/decoration", () => _editor.AddDecoration(kind));
        }
        _editor.DeselectObject(); _view.Canvas.FitToCanvas();
        // More of the land filled where the regions are.
        await Scatter(target, NatureSets, "region/scatter");
        var all = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        _report.Checks["regions/area-rules"] = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Any(a => a.Rules is { Any: true });
        _report.Checks["regions/region-label"] = all.Any(o => o.Text?.Region is not null);
        _report.Checks["regions/text-on-path"] = all.Any(o => o.Text?.Path is not null);
        _report.Checks["regions/decorations"] = all.Count(o => o.Decoration is not null) >= 2;
    }

    // ------------------------------------------------------------------ editor commands

    /// <summary>«Россыпь» clicks of random sets on land until the map holds <paramref name="target"/> objects.</summary>
    private async Task Scatter(int target, string[] sets, string name)
    {
        _view.Canvas.FitToCanvas();
        var refusals = 0;
        while (CountObjects() < target && refusals < 20)
        {
            if (!await Arm(sets, StampPlacementMode.Area, 3 + _rng.Next(6))) { refusals++; continue; }
            var n = Math.Min(target - CountObjects(), ScatterPerClick());
            var radius = Math.Min(4096, _side * (.012 + _rng.NextDouble() * .025));
            _editor.StampAreaRadius = radius;
            _editor.StampDensity = n * StampPlacement.DensityReferenceArea / (Math.PI * radius * radius);
            _editor.ArmStampPlacement();
            var p = RandomLandPoint();
            if (!await Cmd(name, () => PlaceAt(p.X, p.Y))) refusals++;
        }
        _editor.CancelPlacement();
        _buildPhase.Notes[name + "-refusals"] = refusals.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Objects one «Россыпь» click places: a few hundred clicks fill any rung of the ladder.</summary>
    private int ScatterPerClick() => Math.Clamp(Options.ObjectCount / 250, 40, 8000);

    /// <summary>Objects per square world unit an area placed so far (null before the first): sizes the next one.</summary>
    private double? _areaYield;

    /// <summary>
    /// One «Область» sized for about <paramref name="wanted"/> objects: the first is small, the next ones use what the
    /// earlier areas actually held per square unit (the count depends on the set's image sizes and the rules).
    /// </summary>
    private async Task<bool> AreaCmd(string name, (double X, double Y) centre, int wanted, bool rules)
    {
        var radius = _areaYield is { } yield && yield > 0
            ? Math.Sqrt(Math.Max(20, wanted) / yield / Math.PI) : _side * .012;
        radius = Math.Clamp(radius, _side * .006, _side * .08);
        var before = CountObjects();
        var ok = await Cmd(name, () => DrawArea(centre, radius, 7 + _rng.Next(4), 6, rules));
        var added = CountObjects() - before;
        if (ok && added > 0) _areaYield = added / (Math.PI * radius * radius * .85);
        return ok;
    }

    /// <summary>Draws an «Область» polygon around a point, optionally with the area rules, and places it.</summary>
    private bool DrawArea((double X, double Y) centre, double radius, int nodes, double density, bool rules)
    {
        for (var k = 0; k < nodes; k++)
        {
            var a = Math.Tau * k / nodes; var r = radius * (.65 + _rng.NextDouble() * .5);
            if (!_editor.AddEditableAreaNode(Math.Clamp(centre.X + Math.Cos(a) * r, 1, _side - 1), Math.Clamp(centre.Y + Math.Sin(a) * r, 1, _side - 1)) && k == 0)
                Console.WriteLine("    area node refused: " + _editor.StatusDetail);
        }
        _editor.SetEditableAreaDensity(Math.Clamp(density * 10, 0, 100));
        if (rules)
        {
            _editor.SetEditableAreaOnlyOnLand(true);
            _editor.SetEditableAreaAvoidWater(true);
            _editor.SetEditableAreaAvoidRoads(true);
        }
        return _editor.FinishEditableArea();
    }

    private bool SelectSet(string[] names)
    {
        var sets = _catalog.Sets.Where(s => !s.Archived && names.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (sets.Length == 0) sets = _catalog.Sets.Where(s => !s.Archived).ToArray();
        for (var tries = 0; tries < 6; tries++)
        {
            var set = sets[_rng.Next(sets.Length)];
            _editor.DeselectObject();
            if (_editor.ChooseGalleryVariant(set.Id, null, armPlacement: false))
            {
                _buildPhase.Notes["set/" + set.Name] = set.Detail ?? set.Id;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Chooses a set and arms placement in the given mode at the given scale, as the asset panel does, then waits until
    /// the chosen images are checked and read (the editor refuses a placement before that, «Содержимое ассета
    /// подготавливается»; a user waits the same time). The wait is its own edit row, asset-prepare.
    /// </summary>
    private async Task<bool> Arm(string[] names, StampPlacementMode mode, double scalePercent)
    {
        if (!SelectSet(names)) return false;
        // One layout pass between choosing a card and the next action, as with a pointer: switching sets twice before
        // the panel lays out crashed the editor (EditorView.BuildVariantChoice index out of range, 2026-11-16 run).
        await Settle();
        _editor.ActiveTool = EditorTool.Stamp;
        _editor.StampPlacementMode = mode;
        _editor.PlacementAutoSelect = PlacementAutoSelectPolicy.Off;
        if (!_editor.ArmStampPlacement()) return false;
        SetFutureScale(scalePercent);
        await PrepareAssets();
        return true;
    }

    /// <summary>A set chosen and «Область» started, its images prepared.</summary>
    private async Task<bool> BeginArea(string[] names)
    {
        if (!SelectSet(names)) return false;
        await Settle();
        _editor.StartEditableArea();
        SetFutureScale(4 + _rng.Next(5));
        // An area plans with every variant of the set: the panel's «случайный вариант» makes the preparation read them all.
        _editor.RandomVariantAfterPlacement = true;
        await PrepareAssets();
        return true;
    }

    private async Task PrepareAssets()
    {
        var stat = _buildPhase.Edits.FirstOrDefault(e => e.Name == "asset-prepare");
        if (stat is null) { stat = new BuildEditStat { Name = "asset-prepare" }; _buildPhase.Edits.Add(stat); }
        var start = Stopwatch.GetTimestamp();
        try { await _editor.PreparePlacementAssetsAsync().WaitAsync(TimeSpan.FromSeconds(180)); }
        catch (TimeoutException) { stat.Refused++; }
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        stat.Count++; stat.WorkMs.Add(ms); stat.FrameLatencyMs.Add(ms); stat.FrameWorkMs.Add(0);
    }

    private void SetFutureScale(double percent) => _editor.SetStampScalePercent(percent, commit: true);

    /// <summary>Pointer to the point, then the press that commits the armed placement.</summary>
    private bool PlaceAt(double x, double y)
    {
        var before = CountObjects();
        var p = _editor.Session.Viewport.WorldToScreen(x, y);
        _view.Canvas.MovePlacementPointerForSmoke(p.X, p.Y);
        _view.Canvas.CommitPlacementForSmoke();
        return CountObjects() > before;
    }

    private async Task Label(double x, double y, string text, LabelPreset preset, string name)
    {
        SetScale((x, y), _view.Canvas.Bounds.Width / (_side / 6.0));
        _editor.ActiveTool = EditorTool.Select; _editor.SelectLayer(_objectsId);
        await Cmd(name, () =>
        {
            _editor.AddText();
            if (_editor.Session.SelectedObject?.Text is null) return false;
            _editor.SetTextContent(text);
            _editor.ApplyLabelPreset(preset);
            // The preset's size is for a regional page; on a large page the user sets the size the map needs.
            var size = _side * (preset switch { LabelPreset.Country => .03, LabelPreset.Sea => .022, _ => .012 });
            _editor.SetTextFontSize(Math.Clamp(size, 16, 512));
            return true;
        });
        _editor.DeselectObject();
        _view.Canvas.FitToCanvas();
    }

    private bool Drag(double x0, double y0, double x1, double y1)
    {
        var a = _editor.Session.Viewport.WorldToScreen(x0, y0); var b = _editor.Session.Viewport.WorldToScreen(x1, y1);
        _view.Canvas.PressForSmoke(a.X, a.Y);
        _view.Canvas.MoveForSmoke((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        _view.Canvas.MoveForSmoke(b.X, b.Y);
        _view.Canvas.ReleaseForSmoke(b.X, b.Y);
        return true;
    }

    /// <summary>One press, the pointer through every point, one release - through the canvas router.</summary>
    private bool Stroke(IReadOnlyList<(double X, double Y)> world)
    {
        var before = CountObjects();
        var first = _editor.Session.Viewport.WorldToScreen(world[0].X, world[0].Y);
        _view.Canvas.PressForSmoke(first.X, first.Y);
        (double X, double Y) last = (first.X, first.Y);
        foreach (var point in world.Skip(1))
        {
            var p = _editor.Session.Viewport.WorldToScreen(point.X, point.Y);
            _view.Canvas.MoveForSmoke(p.X, p.Y); last = (p.X, p.Y);
        }
        _view.Canvas.ReleaseForSmoke(last.X, last.Y);
        return _editor.ActiveTool != EditorTool.Stamp || CountObjects() > before;
    }

    private void Click(double x, double y)
    {
        var p = _editor.Session.Viewport.WorldToScreen(x, y);
        _view.Canvas.ClickForSmoke(p.X, p.Y);
    }

    private bool LandChanged(Func<bool> gesture, bool paint = false)
    {
        var before = _editor.Session.Document.FindLayer(_landId) as LandLayer;
        gesture();
        var after = _editor.Session.Document.FindLayer(_landId) as LandLayer;
        return paint ? !ReferenceEquals(before?.PaintTiles, after?.PaintTiles) : !ReferenceEquals(before?.GeometryTiles, after?.GeometryTiles);
    }

    /// <summary>Runs one edit command, times its UI work (and a background area until it lands) and the next frame.</summary>
    private async Task<bool> Cmd(string name, Func<bool> action)
    {
        var stat = _buildPhase.Edits.FirstOrDefault(e => e.Name == name);
        if (stat is null) { stat = new BuildEditStat { Name = name }; _buildPhase.Edits.Add(stat); }
        var before = CountObjects();
        var start = Stopwatch.GetTimestamp();
        bool ok;
        try { ok = action(); }
        catch (Exception error) { ok = false; _buildPhase.Notes["error/" + name] = error.GetType().Name + ": " + error.Message; Console.WriteLine($"    {name} threw {error.Message}"); }
        var limit = Stopwatch.GetTimestamp() + 600 * Stopwatch.Frequency;
        while (_editor.IsEditableAreaBusy && Stopwatch.GetTimestamp() < limit) await Task.Delay(5);
        var work = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var (frameWork, _) = await NextBuildFrame();
        var latency = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        stat.Count++;
        if (!ok) { stat.Refused++; Console.WriteLine($"    {name} refused: {_editor.StatusDetail}"); }
        stat.ObjectsAdded += Math.Max(0, CountObjects() - before);
        stat.WorkMs.Add(work); stat.FrameLatencyMs.Add(latency); stat.FrameWorkMs.Add(frameWork);
        return ok;
    }

    private async Task<(double Work, double Scene)> NextBuildFrame()
    {
        var canvas = _view.Canvas;
        var completion = new TaskCompletionSource<(double, double)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = long.MaxValue;
        void OnFrame(object? s, EventArgs e)
        {
            if (canvas.CompletedSceneRevision >= Volatile.Read(ref requested))
                completion.TrySetResult((Map.App.Controls.MapCanvasControl.LastFrameWorkMilliseconds, canvas.LastSceneRenderMilliseconds));
        }
        canvas.FrameRendered += OnFrame;
        try
        {
            Volatile.Write(ref requested, canvas.RequestFrameForSmoke());
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(180));
        }
        finally { canvas.FrameRendered -= OnFrame; }
    }

    private static void CompleteEditStat(BuildEditStat stat)
    {
        var work = stat.WorkMs.Order().ToArray(); var latency = stat.FrameLatencyMs.Order().ToArray(); var frame = stat.FrameWorkMs.Order().ToArray();
        stat.WorkTotalMs = work.Sum(); stat.WorkP50 = Percentile(work, .5); stat.WorkP95 = Percentile(work, .95);
        stat.WorkMax = work.Length == 0 ? 0 : work[^1];
        stat.FrameLatencyP95 = Percentile(latency, .95); stat.FrameLatencyMax = latency.Length == 0 ? 0 : latency[^1];
        stat.FrameWorkP95 = Percentile(frame, .95);
    }

    // ------------------------------------------------------------------ camera routes and shots

    /// <summary>
    /// The FPS-grid routes after a phase: fit (time until sharp), pan at fit, wheel fit -> 100 % at the phase's place,
    /// pan at 100 %, wheel 100 -> 400 %, pan at 400 %, wheel back to fit, edge-to-edge pan at 100 %. Shots: fit and close-up.
    /// With a deadline (reopen) the routes stop when it passes.
    /// </summary>
    private async Task BuildCameraRoutes(ZoomScenarioReport scenario, (double X, double Y) focus, string name, Stopwatch? deadline)
    {
        var canvas = _view.Canvas;
        bool Over() => deadline is not null && deadline.Elapsed > TimeSpan.FromSeconds(60);
        _editor.CancelPlacement(); _editor.DeselectObject(); _editor.ActiveTool = EditorTool.Select;
        await Route(scenario, "fit", 1, _ => canvas.FitToCanvas());
        await Settled(scenario.Routes[^1]);
        await BuildShot(name + "-fit");
        var fit = _editor.Session.Viewport.Scale;
        if (Over()) return;
        await BurstRoute(scenario, "pan/fit", BuildRouteSteps, i => canvas.PanByScreenDeltaForSmoke(i < BuildRouteSteps / 2 ? -3 : 3, 0));
        if (Over()) return;
        await Wheel(1, "wheel/in-100");
        await BuildShot(name + "-100");
        if (Over()) return;
        await BurstRoute(scenario, "pan/100", BuildRouteSteps, i => canvas.PanByScreenDeltaForSmoke(i < BuildRouteSteps / 2 ? -3 : 3, 0));
        if (Over()) return;
        await Wheel(4, "wheel/in-400");
        if (name is "towns") await BuildShot(name + "-400");
        if (Over()) return;
        await BurstRoute(scenario, "pan/400", BuildRouteSteps, i => canvas.PanByScreenDeltaForSmoke(i < BuildRouteSteps / 2 ? -3 : 3, 0));
        if (Over()) return;
        await Wheel(fit, "wheel/out-fit");
        if (Over()) return;
        SetScale((canvas.Bounds.Width / 2, focus.Y), 1);
        await NextBuildFrame();
        var distance = Math.Max(0, scenario.CanvasWidth - canvas.Bounds.Width);
        await BurstRoute(scenario, "pan/edge-to-edge-100", BuildRouteSteps, _ => canvas.PanByScreenDeltaForSmoke(-distance / BuildRouteSteps, 0));
        canvas.FitToCanvas();
        await NextBuildFrame();

        async Task Wheel(double target, string route)
        {
            var from = _editor.Session.Viewport.Scale;
            var delta = Math.Log(target / from) / Math.Log(1.15) / BuildRouteSteps;
            await BurstRoute(scenario, route, BuildRouteSteps, _ =>
            {
                var p = _editor.Session.Viewport.WorldToScreen(focus.X, focus.Y);
                canvas.WheelForSmoke(p.X, p.Y, delta, KeyModifiers.None);
            });
        }
    }

    private async Task BuildShot(string name)
    {
        using var bitmap = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_shotsFolder, _shotPrefix + name + ".png");
        using (var file = File.Create(path)) png.SaveTo(file);
        _report.Frames.Add(path);
        if (_report.BuildPhases is { Count: > 0 } phases) phases[^1].Shots.Add(path);
    }

    // ------------------------------------------------------------------ reopen

    /// <summary>
    /// A saved map opened in a new process, as the dashboard opens it (inspect, then the editor): the time to the first
    /// frame and to a sharp whole-map fit, then the same routes for the first 60 s after the open started.
    /// </summary>
    private async Task ExecuteBuildReopen(string path)
    {
        var clock = Stopwatch.StartNew();
        var store = new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery")));
        var inspection = await Task.Run(() => store.Inspect(path));
        var document = inspection.Project?.Document ?? throw new InvalidOperationException("Saved map did not open: " + inspection.Message);
        _report.Environment["reopen-inspect-ms"] = clock.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        _side = document.Canvas.Width;
        _report.Environment["canvas-side"] = _side.ToString(CultureInfo.InvariantCulture);
        _buildPhase = new BuildPhaseReport { Name = "reopen", ObjectsBefore = 0 };
        _report.BuildPhases!.Add(_buildPhase);
        OpenEditor(document);
        var scenario = new ZoomScenarioReport { Name = "reopen", CanvasWidth = document.Canvas.Width, CanvasHeight = document.Canvas.Height, Objects = CountObjects() };
        _report.Scenarios.Add(scenario);
        await Route(scenario, "open-first-frame", 1, _ => _view.Canvas.FitToCanvas());
        _report.Environment["reopen-first-frame-ms"] = clock.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        await Settled(scenario.Routes[^1]);
        scenario.OpenToExactTerrainMs = clock.Elapsed.TotalMilliseconds;
        _buildPhase.ObjectsAfter = scenario.Objects;
        Console.WriteLine($"reopen: objects={scenario.Objects}, ready (sharp fit) after {scenario.OpenToExactTerrainMs / 1000:0.0} s");
        var focus = document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Where(o => o.Room is not null).Select(o => (o.X + o.Width / 2, o.Y + o.Height / 2)).FirstOrDefault();
        if (focus == default) focus = (_side / 2.0, _side / 2.0);
        await BuildCameraRoutes(scenario, focus, "reopen", clock);
        var (ws, pb) = Memory();
        _buildPhase.WorkingSetMiB = ws; _buildPhase.PrivateMiB = pb;
        _report.Checks["reopen/objects"] = scenario.Objects > 0;
        _report.Checks["reopen/gpu"] = scenario.Routes.All(r => r.Gpu);
        await Finish(scenario);
        BuildCheckpoint();
    }

    // ------------------------------------------------------------------ geometry of the plan

    private int CountObjects() => _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);

    private bool IsLand(double x, double y) => (_probe ??= new TerrainProbe(_editor.Session.Document)).At(x, y).Land is not null;

    private (double X, double Y) RandomLandPoint() => RandomLandPoint(_rng.Next(_continents.Count), .85);

    private (double X, double Y) RandomLandPoint(int continent, double within)
    {
        var c = _continents[continent];
        for (var i = 0; i < 200; i++)
        {
            var a = _rng.NextDouble() * Math.Tau; var r = Math.Sqrt(_rng.NextDouble()) * within;
            var x = c.X + Math.Cos(a) * c.Rx * r; var y = c.Y + Math.Sin(a) * c.Ry * r;
            // Leave the interior's ground free (its building plus the widest scatter around a click).
            if (_interiorPlanned && Math.Abs(x - _interiorSite.X) < 1400 + _side * .04 && Math.Abs(y - _interiorSite.Y) < 1000 + _side * .04) continue;
            if (x > 0 && y > 0 && x < _side && y < _side && IsLand(x, y)) return (x, y);
        }
        return (c.X, c.Y);
    }

    private (double X, double Y) RandomSeaPoint()
    {
        for (var i = 0; i < 400; i++)
        {
            var x = _side * (.04 + _rng.NextDouble() * .92); var y = _side * (.04 + _rng.NextDouble() * .92);
            var clear = _continents.All(c => Math.Pow((x - c.X) / (c.Rx * 1.5), 2) + Math.Pow((y - c.Y) / (c.Ry * 1.5), 2) > 1);
            if (clear) return (x, y);
        }
        return (_side * .05, _side * .05);
    }

    /// <summary>A point inland of the coast: walk out from the continent's centre to the sea, then back by a share of the side.</summary>
    private (double X, double Y)? CoastInland(int continent, double inland)
    {
        var c = _continents[continent];
        var a = _rng.NextDouble() * Math.Tau; var dx = Math.Cos(a); var dy = Math.Sin(a);
        var step = Math.Max(16, _side / 2048.0);
        double d = 0;
        while (d < _side && IsLand(c.X + dx * d, c.Y + dy * d)) d += step;
        if (d < step * 4) return null;
        // RouteRiver looks for the sea within 3000 units of the start.
        var back = Math.Min(Math.Min(2600, d * .5), Math.Max(step * 3, _side * inland * .5));
        var x = c.X + dx * (d - back); var y = c.Y + dy * (d - back);
        return IsLand(x, y) ? (x, y) : null;
    }

    private (double X, double Y) FindInteriorSite(double x, double y)
    {
        for (var i = 0; i < 60; i++)
        {
            var a = _rng.NextDouble() * Math.Tau; var d = Math.Max(1500, _side * .045) + _rng.NextDouble() * 2500;
            var px = x + Math.Cos(a) * d; var py = y + Math.Sin(a) * d;
            if (px > 1000 && py > 1000 && px < _side - 1000 && py < _side - 1000 && IsLand(px, py)
                && IsLand(px - 800, py - 600) && IsLand(px + 800, py - 600) && IsLand(px - 800, py + 600) && IsLand(px + 800, py + 600)) return (px, py);
        }
        return (x, y);
    }

    private static readonly string[] Syllables = ["ар", "бе", "вел", "го", "дар", "ен", "жи", "зор", "ил", "ка", "лу", "мир", "но", "ос", "пра", "ре", "ста", "ту", "ур", "хол", "че", "ям"];

    private string PlaceName()
    {
        var parts = 2 + _rng.Next(2);
        var name = string.Concat(Enumerable.Range(0, parts).Select(_ => Syllables[_rng.Next(Syllables.Length)]));
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    // ------------------------------------------------------------------ output

    private void BuildCheckpoint() => File.WriteAllText(Path.ChangeExtension(Options.OutputPath, ".progress.json"),
        JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));

    private void PrintBuildTable()
    {
        var text = new StringBuilder();
        text.AppendLine("phase      objects   edits  build s  edit p95 ms  worst edit           routes fps (min/avg)  sharp max s  WS MiB");
        foreach (var phase in _report.BuildPhases!)
        {
            var scenario = _report.Scenarios.FirstOrDefault(s => s.Name == phase.Name);
            var moving = scenario?.Routes.Where(r => r.Fps is not null && r.Name.Contains('/')).ToArray() ?? [];
            var worst = phase.Edits.OrderByDescending(e => e.WorkMax).FirstOrDefault();
            var p95 = phase.Edits.Count == 0 ? 0 : Percentile(phase.Edits.SelectMany(e => e.WorkMs).Order().ToArray(), .95);
            var sharp = scenario?.Routes.Where(r => r.SharpDelayMs is not null).Select(r => r.SharpDelayMs!.Value).DefaultIfEmpty(0).Max() ?? 0;
            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-10} {1,8} {2,6} {3,8:0.0} {4,12:0.0}  {5,-20} {6,8:0.0}/{7,-8:0.0} {8,11:0.0} {9,7}",
                phase.Name, phase.ObjectsAfter, phase.Edits.Sum(e => e.Count), phase.BuildSeconds, p95,
                worst is null ? "-" : $"{worst.Name} {worst.WorkMax:0}", moving.Length == 0 ? 0 : moving.Min(r => r.Fps!.Value),
                moving.Length == 0 ? 0 : moving.Average(r => r.Fps!.Value), sharp / 1000, phase.WorkingSetMiB));
        }
        Console.Write(text);
        _report.Environment["phase-table"] = text.ToString();
    }
}
