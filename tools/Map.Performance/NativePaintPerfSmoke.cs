using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Threading;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// Painting and placement cost on a real map, 2026-10-17. The production editor window (off-screen,
/// never activated) opens a copy of the regional project named by <c>MAP_REGIONAL_PROJECT</c> and
/// drives, through the production canvas router at a 16 ms input cadence: a freehand land stroke
/// (coast shape), a land material stroke, a brush-layer stroke, a mask stroke, the ghost of an
/// armed asset and a series of single placements. For each route it records the Skia scene pass
/// of every frame, the time from each input to the frame that shows it, the frames per second
/// over the route and the commit (release) latency. The frame boundary is the render thread
/// finishing the canvas scene pass, not monitor scan-out.
/// </summary>
internal sealed class NativePaintPerfReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false, off-screen; production router hooks at 16 ms cadence; frame = render-thread completion of the canvas scene pass.";
    public Dictionary<string, string> Environment { get; } = [];
    public List<ZoomRouteReport> Routes { get; } = [];
    public Dictionary<string, bool> Checks { get; } = [];
    public List<string> Frames { get; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count > 0 && Checks.Values.All(v => v);
}

internal static class NativePaintPerfSmoke
{
    public static NativePaintPerfReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; // as Map.App.Program sets it
        var completion = new TaskCompletionSource<NativePaintPerfReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            PaintPerfApplication.Options = options; PaintPerfApplication.Completion = completion;
            // Under RenderDoc the WinUI compositor rejects the wrapped D3D11 device: MAP_RENDERDOC_ROUTE
            // switches the window to a redirection surface (the scene's Skia work is the same).
            var builder = AppBuilder.Configure<PaintPerfApplication>().UsePlatformDetect();
            if (System.Environment.GetEnvironmentVariable("MAP_RENDERDOC_ROUTE") is { Length: > 0 })
                builder = builder.With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] });
            try { builder.WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(20)).GetAwaiter().GetResult();
    }
}

public sealed partial class PaintPerfApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativePaintPerfReport> Completion = null!;
    private readonly NativePaintPerfReport _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private AssetCatalog _catalog = null!;
    private readonly List<AssetSet> _sets = [];

    public override void Initialize()
    {
        var production = new global::Map.App.App(); production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources) Resources[resource.Key] = resource.Value;
        var styles = production.Styles.ToArray(); production.Styles.Clear(); Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "paint-perf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
        System.Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        System.Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _window = new Window { Width = EnvNumber("MAP_PAINT_W", 1440), Height = EnvNumber("MAP_PAINT_H", 900), Title = "Map — paint performance", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        _window.Opened += async (_, _) =>
        {
            try { await Execute(); }
            catch (Exception ex) { _report.Failure = ex.ToString(); Console.WriteLine(ex); }
            finally
            {
                try { _view?.CloseAuxiliaryWindowsForSmoke(); _view?.Canvas.DisposeRenderCaches(); } catch { }
                await _catalog.FlushMetadataAsync();
                _window.Close(); Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    private void OpenEditor(MapDocument document)
    {
        _editor = new EditorViewModel(document, null, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "paint.mapproj")), new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog,
            exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "paint-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    private async Task Execute()
    {
        _report.Environment["os"] = System.Environment.OSVersion.VersionString;
        _report.Environment["cpu-threads"] = System.Environment.ProcessorCount.ToString();
        _report.Environment["render-scaling"] = _window.RenderScaling.ToString("0.###");
        foreach (var assembly in new[] { "Map.App", "Map.Rendering", "Map.Presentation", "Map.Core" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, assembly + ".dll");
            if (File.Exists(path))
                _report.Environment["build/" + assembly] = File.GetLastWriteTimeUtc(path).ToString("u") + " sha256=" +
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..16];
        }
        if (System.Environment.GetEnvironmentVariable("MAP_CLIFF_PERF") == "1") { await CliffRoutes(); return; }
        if (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_PERF") == "1") { await NewArtRoutes(); return; }
        var projectPath = System.Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT");
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            throw new InvalidOperationException("MAP_REGIONAL_PROJECT must name an existing .mapproj");
        var pack = RegionalArtPack.Import(_catalog);
        _sets.AddRange(pack.Sets.Values.DistinctBy(s => s.Id));
        var document = MapProjectStore.Open(projectPath).Document;
        _report.Environment["project"] = Path.GetFullPath(projectPath);
        _report.Environment["objects"] = document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count).ToString();
        var land = document.Layers.OfType<LandLayer>().FirstOrDefault(l => l.GeometryTiles.TileCount > 0)
            ?? throw new InvalidOperationException("The project has no land layer with coverage.");
        var passes = int.TryParse(System.Environment.GetEnvironmentVariable("MAP_PAINT_PASSES"), out var p) ? Math.Max(1, p) : 1;

        EditorViewModel.CollectEditTimings = true;
        OpenEditor(document);
        var canvas = _view.Canvas;
        await FirstFrame(document);
        await Settle();
        canvas.FitToCanvas(); await Settle();

        // The view: 100 % around the centre of the land's coverage, so strokes hit real terrain.
        var centre = CoverageCentre(land);
        SetScale(centre, EnvNumber("MAP_PAINT_SCALE", 1.0)); await Settle(); await WaitRefined();
        _report.Environment["view-centre"] = $"{centre.X:0},{centre.Y:0}";
        var seaStart = SeaPointNear(land, centre);
        _report.Environment["sea-start"] = seaStart is { } s ? $"{s.X:0},{s.Y:0}" : "none";

        for (var pass = 0; pass < passes; pass++)
        {
            var suffix = passes > 1 ? $"#{pass + 1}" : "";
            // 1. Freehand land stroke: the coast shape tool with the default roughness.
            _editor.ActiveTool = EditorTool.Land; _editor.SetLandPaintMode(false);
            _editor.SelectLayer(land.Id);
            _editor.BrushSettings = _editor.BrushSettings with { Radius = 40, Softness = .5, Spacing = .25 };
            var start = seaStart ?? centre;
            await StrokeRoute("land-shape-stroke" + suffix, start, 60, 9, 4);
            _report.Checks["land-shape/committed" + suffix] = _editor.Session.Document.FindLayer(land.Id) is LandLayer after && !ReferenceEquals(after.GeometryTiles, land.GeometryTiles);
            await Capture("land-shape-after" + suffix);
            _editor.Undo(); await Settle(); await WaitRefined();

            // 2. Land material stroke on the land.
            _editor.SetLandPaintMode(true);
            var material = MapFillTextures.Of(MapFillTextureKind.Ground).FirstOrDefault(m => MapTextureLibrary.Image(m.Id) is not null);
            if (material is not null) _editor.ChooseLandMaterial(material.Id);
            _editor.BrushSettings = _editor.BrushSettings with { Radius = 64, Softness = .8, Spacing = .15 };
            await StrokeRoute("land-paint-stroke" + suffix, centre, 60, 9, 3);
            _report.Checks["land-paint/committed" + suffix] = _editor.Session.Document.FindLayer(land.Id) is LandLayer painted && painted.PaintTiles.TileCount > 0;
            await Capture("land-paint-after" + suffix);
            _editor.Undo(); _editor.SetLandPaintMode(false); await Settle(); await WaitRefined();

            // 3. Brush layer stroke.
            _editor.AddBrushLayer(); await Settle();
            _editor.ActiveTool = EditorTool.Brush;
            _editor.BrushSettings = _editor.BrushSettings with { Radius = 32, Softness = .5, Spacing = .25, Color = "#FF6A3E1F", Texture = null };
            await StrokeRoute("brush-stroke" + suffix, centre, 60, 9, 5);
            _editor.Undo(); _editor.Undo(); await Settle();

            // 4. Mask stroke on the object layer.
            var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().OrderByDescending(l => l.Objects.Count).First();
            _editor.SelectLayer(objects.Id); _editor.ActiveTool = EditorTool.Mask; await Settle();
            if (_editor.ActiveLayer?.Mask is null) { _editor.ToggleActiveMask(); await Settle(); }
            await StrokeRoute("mask-stroke" + suffix, centre, 60, 9, 6);
            _editor.Undo(); await Settle();
            _editor.ActiveTool = EditorTool.Select; await Settle();

            // 5. Armed asset: ghost moves, then twenty single placements.
            var set = _sets[0];
            _editor.RefreshLibrary(); _view.RefreshLibraryPanel(); await Settle();
            var armed = false;
            await Route("placement-activate" + suffix, 1, _ => armed = _editor.ChooseGalleryVariant(set.Id, null));
            _report.Checks["placement/armed" + suffix] = armed;
            if (armed)
            {
                _editor.StampPlacementMode = StampPlacementMode.One;
                await BurstRoute("placement-ghost" + suffix, 60, i =>
                {
                    var pt = _editor.Session.Viewport.WorldToScreen(centre.X - 240 + i * 8, centre.Y + 100);
                    canvas.MovePlacementPointerForSmoke(pt.X, pt.Y);
                });
                var before = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
                await Route("placement-commit-x20" + suffix, 20, i =>
                {
                    var pt = _editor.Session.Viewport.WorldToScreen(centre.X - 300 + (i % 10) * 60, centre.Y - 120 + (i / 10) * 140);
                    canvas.MovePlacementPointerForSmoke(pt.X, pt.Y);
                    canvas.CommitPlacementForSmoke();
                });
                var placed = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) - before;
                _report.Checks["placement/placed-20" + suffix] = placed == 20;
                await Settle();
                PrintEditTimings("placement-commit(last)");
                var (prep, names, commit, meta) = _editor.LastPlacementTiming;
                Console.WriteLine($"    placement timing prepare={prep:0.0} names={names:0.0} commit={commit:0.0} metadata={meta:0.0}");
                await Capture("placement-after" + suffix);
                // Ghost over the freshly placed objects, the way a user keeps placing in one spot.
                await BurstRoute("placement-ghost-dense" + suffix, 60, i =>
                {
                    var pt = _editor.Session.Viewport.WorldToScreen(centre.X - 300 + i * 10, centre.Y - 60);
                    canvas.MovePlacementPointerForSmoke(pt.X, pt.Y);
                });
                // The way a user places: the ghost follows the pointer every frame and a click lands
                // every eighth move (~7 clicks a second); a frame lost to a click shows up here.
                await BurstRoute("placement-clicks" + suffix, 64, i =>
                {
                    var pt = _editor.Session.Viewport.WorldToScreen(centre.X - 300 + i * 9, centre.Y + 60 + (i % 8) * 4);
                    canvas.MovePlacementPointerForSmoke(pt.X, pt.Y);
                    if (i % 8 == 4) { canvas.CommitPlacementForSmoke(); placed++; }
                });
                await Settle();
                for (var i = 0; i < placed; i++) _editor.Undo();
                _editor.CancelPlacement();
            }
            _editor.ActiveTool = EditorTool.Select; await Settle();
        }
        var (ws, pb) = Memory();
        _report.Environment["working-set-mib"] = ws.ToString(); _report.Environment["private-mib"] = pb.ToString();
        _report.Checks["gpu"] = _report.Routes.All(r => r.Gpu);
    }

    // ------------------------------------------------------------------ geometry helpers

    private static (double X, double Y) CoverageCentre(LandLayer land)
    {
        double sx = 0, sy = 0; var n = 0;
        foreach (var key in land.GeometryTiles.Keys)
        {
            if (land.GeometryTiles.TryGet(key) is not { } tile) continue;
            var filled = 0; foreach (var b in tile) if (b > 127) filled++;
            if (filled < tile.Length / 2) continue;
            sx += (key.X + .5) * TileMap.TileSize; sy += (key.Y + .5) * TileMap.TileSize; n++;
        }
        return n == 0 ? (0, 0) : (sx / n, sy / n);
    }

    /// <summary>A point in the sea inside the view at 100 %: the first tile with no coverage, scanning outward from the centre.</summary>
    private (double X, double Y)? SeaPointNear(LandLayer land, (double X, double Y) centre)
    {
        var viewport = _editor.Session.Viewport;
        var halfW = viewport.ViewWidth / 2 - 80; var halfH = viewport.ViewHeight / 2 - 80;
        for (var ring = 1; ring < 8; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    var x = centre.X + dx * 96; var y = centre.Y + dy * 96;
                    if (Math.Abs(x - centre.X) > halfW || Math.Abs(y - centre.Y) > halfH) continue;
                    var key = TileMap.KeyAt(x, y);
                    var tile = land.GeometryTiles.TryGet(key);
                    if (tile is null) return (x, y);
                    var px = (int)(x - key.X * TileMap.TileSize); var py = (int)(y - key.Y * TileMap.TileSize);
                    if (tile[py * TileMap.TileSize + px] == 0) return (x, y);
                }
        return null;
    }

    // ------------------------------------------------------------------ routes

    private async Task FirstFrame(MapDocument document)
    {
        var canvas = _view.Canvas;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? s, EventArgs e) { if (canvas.LastRenderedDocument?.Id == document.Id && canvas.LastFrameHadGpuContext) first.TrySetResult(); }
        canvas.FrameRendered += OnFrame;
        try { await first.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { canvas.FrameRendered -= OnFrame; }
    }

    /// <summary>Window size and zoom of the routes: MAP_PAINT_W/H/SCALE reproduce a large maximised window over the whole map.</summary>
    private static double EnvNumber(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void SetScale((double X, double Y) anchor, double scale)
    {
        var viewport = _editor.Session.Viewport;
        viewport.Set(anchor.X - viewport.ViewWidth / scale / 2, anchor.Y - viewport.ViewHeight / scale / 2, scale);
        _view.Canvas.InvalidateVisual();
    }

    /// <summary>Waits until no terrain refinement is pending (bounded), so a route starts from an exact idle frame.</summary>
    private async Task WaitRefined()
    {
        var canvas = _view.Canvas;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            canvas.InvalidateVisual();
            await Task.Delay(40);
            if (!canvas.LastTerrainRefinementPending && !canvas.LastFrameFastTerrain) break;
        }
        await Settle();
    }

    /// <summary>
    /// One press, <paramref name="moves"/> pointer moves at 16 ms cadence along a direction, one
    /// release: the moves are a burst route; the release is timed as its own one-step route.
    /// </summary>
    private async Task StrokeRoute(string name, (double X, double Y) start, int moves, double stepPx, int direction)
    {
        var canvas = _view.Canvas;
        var (dx, dy) = direction switch { 3 => (stepPx, stepPx * .35), 4 => (stepPx, -stepPx * .5), 5 => (stepPx * .7, stepPx * .7), _ => (stepPx, 0) };
        var p0 = _editor.Session.Viewport.WorldToScreen(start.X, start.Y);
        // Keep the whole stroke on screen: start left of the anchor when it would run off the right edge.
        var width = _editor.Session.Viewport.ViewWidth;
        var x0 = p0.X; if (x0 + moves * dx > width - 40) x0 = Math.Max(40, width - 40 - moves * dx);
        var y0 = p0.Y; var height = _editor.Session.Viewport.ViewHeight;
        if (y0 + moves * dy > height - 40) y0 = Math.Max(40, height - 40 - moves * dy);
        if (y0 + moves * dy < 40) y0 = Math.Min(height - 40, 40 - moves * dy);
        await Route(name + "/press", 1, _ => canvas.PressForSmoke(x0, y0));
        await BurstRoute(name, moves, i => canvas.MoveForSmoke(x0 + (i + 1) * dx, y0 + (i + 1) * dy));
        await Route(name + "/release", 1, _ => canvas.ReleaseForSmoke(x0 + moves * dx, y0 + moves * dy));
        await Settle();
        PrintEditTimings(name + "/release");
    }

    /// <summary>The UI-thread steps of the last edit, largest first, so a slow release is attributed to its cause.</summary>
    private void PrintEditTimings(string label)
    {
        var timings = _editor.LastEditTimings;
        if (timings.Count == 0) return;
        var total = timings.Sum(t => t.Ms);
        var top = timings.Where(t => t.Ms >= 0.3).OrderByDescending(t => t.Ms).Take(24)
            .Select(t => $"{t.Step}={t.Ms:0.0}");
        Console.WriteLine($"    {label} ui-steps total={total:0.0} ms: {string.Join(", ", top)}");
        var stats = _view.Canvas.LastRenderStats;
        Console.WriteLine($"    {label} rivers=[{string.Join('/', stats.RiverPassMilliseconds.Select(v => v.ToString("0")))}] road={stats.RoadMilliseconds:0} terrainRefinementsPending={stats.TerrainRefinementsPending}");
    }

    private async Task Route(string name, int steps, Action<int> step)
    {
        var target = new ZoomRouteReport { Name = name, ScaleStart = _editor.Session.Viewport.Scale };
        MarkJit();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < steps; i++)
        {
            var index = i;
            var (scene, latency) = await Step(() => step(index));
            target.SceneMs.Add(scene); target.LatencyMs.Add(latency);
        }
        watch.Stop();
        target.Steps = steps; target.WallMs = watch.Elapsed.TotalMilliseconds;
        target.FramesRendered = steps;
        target.ScaleEnd = _editor.Session.Viewport.Scale;
        Complete(target);
    }

    private async Task BurstRoute(string name, int steps, Action<int> step)
    {
        var target = new ZoomRouteReport { Name = name, ScaleStart = _editor.Session.Viewport.Scale };
        MarkJit();
        var canvas = _view.Canvas;
        var gcBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var pauseBefore = GC.GetTotalPauseDuration();
        var sceneTimes = new List<double>();
        var frameStamps = new List<(long Stamp, long Revision)>();
        var lastRevision = canvas.CompletedSceneRevision;
        void OnFrame(object? s, EventArgs e)
        {
            lock (sceneTimes)
            {
                var revision = canvas.CompletedSceneRevision;
                if (revision <= lastRevision) return;
                lastRevision = revision;
                sceneTimes.Add(canvas.LastSceneRenderMilliseconds);
                frameStamps.Add((Stopwatch.GetTimestamp(), revision));
            }
        }
        canvas.FrameRendered += OnFrame;
        var watch = Stopwatch.StartNew();
        var inputStamps = new (long Stamp, long Revision)[steps];
        var handlerMs = new double[steps];
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Task.Run(async () =>
            {
                var schedule = Stopwatch.StartNew();
                for (var i = 0; i < steps; i++)
                {
                    var remaining = i * 16.0 - schedule.Elapsed.TotalMilliseconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
                    var index = i;
                    var stamp = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            var started = Stopwatch.GetTimestamp();
                            step(index);
                            RenderDocCapture.OnStep(name, index);
                            handlerMs[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            inputStamps[index] = (stamp, canvas.RequestFrameForSmoke());
                            if (index == steps - 1) submitted.TrySetResult();
                        }
                        catch (Exception error) { submitted.TrySetException(error); }
                    }, DispatcherPriority.Input);
                }
            });
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var lastInput = inputStamps[^1].Revision;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                lock (sceneTimes) { if (frameStamps.Any(f => f.Revision >= lastInput)) break; }
                await Task.Delay(2);
            }
        }
        finally { canvas.FrameRendered -= OnFrame; }
        watch.Stop();
        List<(long Stamp, long Revision)> frames;
        lock (sceneTimes) { frames = [.. frameStamps]; target.SceneMs.AddRange(sceneTimes); }
        foreach (var input in inputStamps)
        {
            var frame = frames.FirstOrDefault(f => f.Revision >= input.Revision);
            if (input.Revision == 0 || frame.Stamp == 0) throw new TimeoutException("Input had no matching canvas frame.");
            target.LatencyMs.Add((frame.Stamp - input.Stamp) * 1000.0 / Stopwatch.Frequency);
        }
        var intervals = frames.Skip(1).Select((frame, index) => (frame.Stamp - frames[index].Stamp) * 1000.0 / Stopwatch.Frequency).Order().ToArray();
        if (intervals.Length > 0) { target.FrameIntervalP95 = Percentile(intervals, .95); target.FrameIntervalMax = intervals[^1]; }
        var handlers = handlerMs.Order().ToArray();
        target.GpuFlushMs.AddRange([Percentile(handlers, .5), Percentile(handlers, .95), handlers[^1]]); // handler P50/P95/max (UI-thread cost of the input itself)
        target.Steps = steps;
        target.FramesRendered = frames.Count;
        target.WallMs = frames.Count > 0 ? (frames[^1].Stamp - inputStamps[0].Stamp) * 1000.0 / Stopwatch.Frequency : watch.Elapsed.TotalMilliseconds;
        target.ScaleEnd = _editor.Session.Viewport.Scale;
        target.SlowRefinementFrames.Add($"gc gen0/1/2 during route: {GC.CollectionCount(0) - gcBefore.Item1}/{GC.CollectionCount(1) - gcBefore.Item2}/{GC.CollectionCount(2) - gcBefore.Item3}; gc pause during route {(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:0} ms");
        Complete(target, burst: true);
        Console.WriteLine("    " + target.SlowRefinementFrames[^1]);
    }

    private TimeSpan _jitTime;
    private long _jitMethods;

    private void MarkJit()
    {
        _jitTime = System.Runtime.JitInfo.GetCompilationTime();
        _jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount();
    }

    private void Complete(ZoomRouteReport target, bool burst = false)
    {
        var scene = target.SceneMs.Order().ToArray();
        var latency = target.LatencyMs.Order().ToArray();
        target.SceneP50 = Percentile(scene, .5); target.SceneP95 = Percentile(scene, .95); target.SceneP99 = Percentile(scene, .99);
        target.SceneMax = scene.Length == 0 ? double.NaN : scene[^1];
        target.LatencyP50 = Percentile(latency, .5); target.LatencyP95 = Percentile(latency, .95); target.LatencyP99 = Percentile(latency, .99);
        target.LatencyMax = latency.Length == 0 ? double.NaN : latency[^1];
        target.LongPauses = target.LatencyMs.Count(l => l > 50);
        target.PausesOver100 = target.LatencyMs.Count(l => l > 100);
        target.Fps = target.WallMs > 0 && target.FramesRendered > (burst ? 0 : 1) ? target.FramesRendered * 1000.0 / target.WallMs : null;
        var canvas = _view.Canvas;
        var stats = canvas.LastRenderStats;
        target.ObjectsDrawnLast = canvas.LastFrameObjectsDrawn; target.ObjectCandidatesLast = stats.ObjectCandidates;
        target.DrawCallsLast = stats.DrawCalls; target.AtlasBatchesLast = stats.StampAtlasBatches;
        target.TerrainMsLast = stats.TerrainMilliseconds; target.ObjectsMsLast = stats.ObjectsMilliseconds;
        target.StampBatchMsLast = stats.StampBatchMilliseconds; target.StampSingleMsLast = stats.StampSingleMilliseconds;
        target.TerrainReusedLast = stats.TerrainReused;
        target.LandMsLast = stats.LandMilliseconds; target.WaterMsLast = stats.WaterMilliseconds;
        target.TierLast = canvas.LastLodTier.ToString(); target.Gpu = canvas.LastFrameHadGpuContext;
        target.ObjectCacheCount = canvas.ObjectCacheCount; target.ObjectCacheMiB = canvas.ObjectCacheBytes / (1024 * 1024);
        target.WorkingSetMiB = Memory().WorkingSet;
        target.MemoryPressureLast = Map.Core.Resources.WorkingMemoryBudget.Shared.IsUnderPressure;
        target.SystemAvailableMiBLast = Map.Core.Resources.WorkingMemoryBudget.Shared.Snapshot.AvailableBytes / (1024 * 1024);
        _report.Routes.Add(target);
        // Cold-start attribution: JIT time and methods compiled, all threads, during the route.
        var jitMs = (System.Runtime.JitInfo.GetCompilationTime() - _jitTime).TotalMilliseconds;
        var jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount() - _jitMethods;
        if (jitMethods > 0) target.SlowRefinementFrames.Add($"jit {jitMs:0} ms / {jitMethods} methods");
        if (jitMs >= 5) Console.WriteLine($"    {target.Name} jit={jitMs:0} ms methods={jitMethods}");
        var handler = target.GpuFlushMs.Count == 3 ? $" handler P50/P95/max={target.GpuFlushMs[0]:0.0}/{target.GpuFlushMs[1]:0.0}/{target.GpuFlushMs[2]:0.0}" : "";
        if (System.Environment.GetEnvironmentVariable("MAP_MEASURE_GPU_FLUSH") == "1") handler += $" gpuFlushLast={Map.App.Controls.MapCanvasControl.LastFrameGpuFlushMilliseconds:0.0}";
        Console.WriteLine($"{target.Name}: steps={target.Steps} frames={target.FramesRendered} fps={target.Fps:0.0} " +
            $"scene P50/P95/max={target.SceneP50:0.0}/{target.SceneP95:0.0}/{target.SceneMax:0.0} " +
            $"latency P50/P95/max={target.LatencyP50:0.0}/{target.LatencyP95:0.0}/{target.LatencyMax:0.0} pauses>50={target.LongPauses}{handler} " +
            $"ws={target.WorkingSetMiB}MiB last: terrain={target.TerrainMsLast:0.0} (land={target.LandMsLast:0.0} water={target.WaterMsLast:0.0}) objects={target.ObjectsMsLast:0.0} drawn={target.ObjectsDrawnLast} tier={target.TierLast} terrainReused={target.TerrainReusedLast}");
    }

    private async Task<(double Scene, double Latency)> Step(Action input)
    {
        var canvas = _view.Canvas;
        var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = long.MaxValue;
        void OnFrame(object? s, EventArgs e)
        {
            if (canvas.CompletedSceneRevision >= Volatile.Read(ref requested))
                completion.TrySetResult(canvas.LastSceneRenderMilliseconds);
        }
        canvas.FrameRendered += OnFrame;
        try
        {
            var watch = Stopwatch.StartNew();
            input();
            Volatile.Write(ref requested, canvas.RequestFrameForSmoke());
            var scene = await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
            watch.Stop();
            return (scene, watch.Elapsed.TotalMilliseconds);
        }
        finally { canvas.FrameRendered -= OnFrame; }
    }

    private async Task Capture(string name)
    {
        try
        {
            var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
            using var bitmap = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path); data.SaveTo(file);
            _report.Frames.Add(path);
        }
        catch (Exception ex) { Console.WriteLine("capture failed: " + ex.Message); }
    }

    private static (long WorkingSet, long Private) Memory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return (process.WorkingSet64 / (1024 * 1024), process.PrivateMemorySize64 / (1024 * 1024));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0) return double.NaN;
        var index = (int)Math.Ceiling(ordered.Count * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(60); }

    private sealed class LocalPicker(string path) : IFilePicker
    { public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path); public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path); }
    private sealed class ExportPicker(string path) : IExportDestinationPicker
    { public Task<string?> PickImageAsync(string name, ExportImageFormat format) => Task.FromResult<string?>(path);
        public Task<string?> PickTilePackageAsync(string name) => Task.FromResult<string?>(Path.ChangeExtension(path, null) + "-tiles"); }
    private sealed class LocalRecent : IRecentMaps
    { public IReadOnlyList<RecentMapItem> Load() => []; public void Remove(string path) { }
        public void Touch(string path, Guid id, string title, DateTimeOffset date, byte[]? thumbnailPng = null, string mapType = "Локальная карта") { } }
    private sealed class LocalLog : IAppLog
    { public void Info(string message) { } public void Error(string message, Exception? exception = null) => Console.WriteLine(message + " " + exception?.Message); }
    private sealed class LocalPrompts : IUserPrompts
    {
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string title) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string title, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => throw new InvalidOperationException(title + ": " + message);
    }
}
