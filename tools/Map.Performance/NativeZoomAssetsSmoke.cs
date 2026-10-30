using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
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
/// Zoom, asset size and frame cost, 2026-09-09. The real editor window (off-screen, never
/// activated) opens finite scenes built from the owner's remaining library: the Standard preset
/// empty, with 1, 10 and 100 stamps, one moderately filled map with land material, rivers and
/// roads, and the Huge preset filled progressively. Every route drives the production viewport
/// through the canvas smoke hooks and records, per frame, the Skia scene pass time and the time
/// from the input to the finished frame. Frame times are the render thread finishing a frame of
/// the real canvas; OS scan-out is not observed, so an FPS here is an upper bound of what the
/// monitor shows and a lower bound of nothing.
/// </summary>
internal sealed class NativeZoomAssetsReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false, off-screen; production viewport/router hooks; frame = render-thread completion of the canvas scene pass, not monitor scan-out.";
    public Dictionary<string, string> Environment { get; } = [];
    public List<ZoomScenarioReport> Scenarios { get; } = [];
    public List<string> Frames { get; } = [];
    public Dictionary<string, bool> Checks { get; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count > 0 && Checks.Values.All(v => v);
}

internal sealed class ZoomScenarioReport
{
    public required string Name { get; init; }
    public required int CanvasWidth { get; init; }
    public required int CanvasHeight { get; init; }
    public required int Objects { get; init; }
    public string Detail { get; set; } = "";
    public List<ZoomRouteReport> Routes { get; } = [];
    public long WorkingSetMiBAfter { get; set; }
    public long PrivateMiBAfter { get; set; }
    /// <summary>From the start of opening until a frame shows the exact terrain (not the plain first frame).</summary>
    public double? OpenToExactTerrainMs { get; set; }
}

internal sealed class ZoomRouteReport
{
    public required string Name { get; init; }
    public int Steps { get; set; }
    public int FramesRendered { get; set; }
    public double WallMs { get; set; }
    /// <summary>Distinct canvas frames finished per second of wall time over the route.</summary>
    public double? Fps { get; set; }
    public double SceneP50 { get; set; }
    public double SceneP95 { get; set; }
    public double SceneP99 { get; set; }
    public double SceneMax { get; set; }
    public double LatencyP50 { get; set; }
    public double LatencyP95 { get; set; }
    public double LatencyP99 { get; set; }
    public double LatencyMax { get; set; }
    /// <summary>Frames whose input-to-frame time exceeded 50 ms.</summary>
    public int LongPauses { get; set; }
    public int PausesOver100 { get; set; }
    public double ScaleStart { get; set; }
    public double ScaleEnd { get; set; }
    public int ObjectsDrawnLast { get; set; }
    public int ObjectCandidatesLast { get; set; }
    public int DrawCallsLast { get; set; }
    public int AtlasBatchesLast { get; set; }
    public double TerrainMsLast { get; set; }
    public double ObjectsMsLast { get; set; }
    public double StampBatchMsLast { get; set; }
    public double StampSingleMsLast { get; set; }
    public int MipBlendsLast { get; set; }
    public double MipBlendMsLast { get; set; }
    public double GpuFlushMsLast { get; set; }
    public int TerrainReusedLast { get; set; }
    public double LandMsLast { get; set; }
    public double WaterMsLast { get; set; }
    /// <summary>Worst scene plus GPU completion time while waiting for the settled terrain.</summary>
    public double? SettleFrameMs { get; set; }
    public double? SettleDelayMs { get; set; }
    public bool? RefinementCompleted { get; set; }
    public double? RefinementBuildMs { get; set; }
    public List<string> SlowRefinementFrames { get; } = [];
    public double? FrameIntervalP95 { get; set; }
    public double? FrameIntervalMax { get; set; }
    public List<double> GpuFlushMs { get; } = [];
    public string TierLast { get; set; } = "";
    public bool Gpu { get; set; }
    public int ObjectCacheCount { get; set; }
    public long ObjectCacheMiB { get; set; }
    public long WorkingSetMiB { get; set; }
    public bool MemoryPressureLast { get; set; }
    public long SystemAvailableMiBLast { get; set; }
    public List<double> SceneMs { get; } = [];
    public List<double> LatencyMs { get; } = [];
}

internal static class NativeZoomAssetsSmoke
{
    public static NativeZoomAssetsReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeZoomAssetsReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ZoomAssetsApplication.Options = options; ZoomAssetsApplication.Completion = completion;
            try { AppBuilder.Configure<ZoomAssetsApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(25)).GetAwaiter().GetResult();
    }
}

public sealed class ZoomAssetsApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeZoomAssetsReport> Completion = null!;
    private readonly NativeZoomAssetsReport _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private AssetCatalog _catalog = null!;
    private readonly List<(AssetRecord Record, AssetSet Set, byte[] Bytes)> _library = [];
    private readonly Stopwatch _runClock = Stopwatch.StartNew();

    /// <summary>Time budget of the whole run; the dense large map stops filling when it is spent.</summary>
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(18);

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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "zoom-assets-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
        System.Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        System.Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — zoom/assets performance", ShowActivated = false,
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

    // ------------------------------------------------------------------ library

    /// <summary>
    /// The owner's active library, read from its catalog.json without opening it for writing:
    /// every record still listed there is copied into the isolated catalog. The rejected ridge is
    /// not listed any more and is therefore never imported.
    /// </summary>
    private void ImportLibrary()
    {
        var root = System.Environment.GetEnvironmentVariable("MAP_ZOOM_ASSETS_LIBRARY")
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Map", "Assets");
        var catalogPath = Path.Combine(root, "catalog.json");
        if (!File.Exists(catalogPath)) throw new InvalidOperationException("The owner's library catalog was not found: " + catalogPath);
        using var json = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        var sources = new List<(string Sha, string Name, string Path)>();
        foreach (var record in json.RootElement.GetProperty("records").EnumerateArray())
        {
            var sha = record.GetProperty("sha256").GetString()!;
            var relative = record.GetProperty("storedRelativePath").GetString();
            if (relative is null) continue;
            var path = Path.Combine(root, relative);
            if (File.Exists(path)) sources.Add((sha, record.GetProperty("displayName").GetString() ?? sha[..8], path));
        }
        if (sources.Count == 0) throw new InvalidOperationException("The owner's library lists no stored originals.");
        foreach (var (sha, name, path) in sources.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var set = _catalog.ImportSet([path], name, new AssetImportOptions { Pack = "Замер" }, DateTimeOffset.UnixEpoch);
            var record = _catalog.Find(sha) ?? throw new InvalidOperationException("Imported asset not found: " + name);
            _library.Add((record, set, File.ReadAllBytes(path)));
        }
        _report.Environment["library-assets"] = _library.Count.ToString();
        _report.Environment["library-pixels"] = string.Join(", ", _library.Select(a => $"{a.Record.DisplayName} {a.Record.Width}x{a.Record.Height}"));
    }

    private MapAssetReference Reference(int index)
    {
        var (record, _, bytes) = _library[index % _library.Count];
        return new MapAssetReference(record.Sha256, record.DisplayName, record.MediaType, record.Extension,
            ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "zoom-assets-smoke", "owner-generated-study", embeddedContent: bytes);
    }

    private SceneObject Stamp(int assetIndex, double x, double y, double width, string name, double rotation = 0)
    {
        var record = _library[assetIndex % _library.Count].Record;
        var height = width * record.Height / record.Width;
        return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, name, x, y, width, height, rotation,
            "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(record.Sha256) { ReferenceWidth = width, ReferenceHeight = height } };
    }

    // ------------------------------------------------------------------ scenes

    /// <summary>The Standard preset of the real wizard, exactly as "Обычная" creates it.</summary>
    private static (int W, int H) Standard => (1920, 1080);
    private static (int W, int H) Huge => (16384, 16384);

    /// <summary>Default placement width of the editor (PlaceStamp): min(256, width / 4).</summary>
    private static double DefaultWidth(int canvasWidth) => Math.Min(256.0, canvasWidth / 4.0);

    private MapDocument StandardScene(int count, bool sameAsset, out (double X, double Y) anchor)
    {
        var (w, h) = Standard;
        var width = DefaultWidth(w);
        var objects = new List<SceneObject>();
        var assets = new Dictionary<string, MapAssetReference>(StringComparer.OrdinalIgnoreCase);
        var random = new Random(20260909);
        anchor = (w / 2.0, h / 2.0);
        for (var i = 0; i < count; i++)
        {
            var asset = sameAsset ? 0 : i;
            double x, y, size;
            if (i == 0) { size = width; x = anchor.X - size / 2; y = anchor.Y - size * 0.3; }
            else
            {
                // 1..99: default size, 50 %, 25 % and 10 % of the default, like a map with a
                // few large landmarks and many reduced details.
                size = width * (i % 4) switch { 0 => 1, 1 => .5, 2 => .25, _ => .1 };
                x = random.NextDouble() * (w - size); y = random.NextDouble() * (h - size);
            }
            objects.Add(Stamp(asset, x, y, size, $"Ассет {i + 1}", i == 0 ? 0 : random.Next(-20, 21)));
            var reference = Reference(asset);
            assets.TryAdd(reference.Sha256, reference);
        }
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects);
        return new MapDocument(DocumentId.New(), $"Обычная · {count} ассетов", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(w, h, "px", "#FF1E2430"),
            [new BaseColorLayer(LayerId.New(), "Цвет фона", true, false, 1, "#FF1E2430"), layer])
            .WithAssets([.. assets.Values]);
    }

    /// <summary>
    /// A moderately filled Standard map: sea, one textured land mass over about 60 % of the canvas,
    /// two rivers, a lake, two roads and 40 stamps of mixed sizes. A labelled test profile, not a
    /// copy of any map of the owner.
    /// </summary>
    private MapDocument ModerateScene(out (double X, double Y) anchor)
    {
        var (w, h) = Standard;
        var full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        for (var x = 1; x < 7; x++) for (var y = 0; y < 4; y++) tiles = tiles.With(new TileKey(x, y), full);
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, CurrentLook.Land());
        var river = new River
        {
            Name = "Река", Style = CurrentLook.River(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(300, 40), new VectorPoint(620, 300), new VectorPoint(900, 520), new VectorPoint(1300, 1040)], 26)],
        };
        var creek = new River
        {
            Name = "Приток", Style = CurrentLook.River(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(1500, 200), new VectorPoint(1200, 420), new VectorPoint(930, 560)], 14)],
        };
        var lake = new River
        {
            Name = "Озеро", Style = CurrentLook.River(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(1400, 700), new VectorPoint(1560, 680), new VectorPoint(1620, 800), new VectorPoint(1500, 880), new VectorPoint(1380, 820)], 20) with { Closed = true }],
        };
        var road = RoadEditor.NewRoad([new(340, 900), new(700, 760), new(1100, 700), new(1500, 560)], 14, RoadStyle.Country(), "Тракт", seed: 7);
        var path = RoadEditor.NewRoad([new(500, 300), new(760, 420), new(1040, 380)], 9, RoadStyle.Country(), "Тропа", seed: 11);
        var random = new Random(1919);
        var width = DefaultWidth(w);
        anchor = (960, 540);
        var objects = new List<SceneObject> { Stamp(0, anchor.X - width / 2, anchor.Y - width * .3, width, "Гора у реки") };
        var assets = new Dictionary<string, MapAssetReference>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < 40; i++)
        {
            var size = width * (i % 4) switch { 0 => 1, 1 => .6, 2 => .35, _ => .15 };
            objects.Add(Stamp(i, 280 + random.NextDouble() * (w - 560 - size), random.NextDouble() * (h - size), size, $"Ассет {i + 1}", random.Next(-15, 16)));
        }
        foreach (var o in objects) { var r = Reference(_library.FindIndex(a => a.Record.Sha256 == o.Stamp!.AssetSha256)); assets.TryAdd(r.Sha256, r); }
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects) { Rivers = [river, creek, lake], Roads = [road, path] };
        return new MapDocument(DocumentId.New(), "Умеренная · земля, реки, дороги, 40 ассетов", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(w, h, "px", "#FF6E9FBF"),
            [CurrentLook.Page("Море", "#FF6E9FBF"), land, layer]).WithAssets([.. assets.Values]);
    }

    /// <summary>
    /// The Max preset (131072²) with land: <paramref name="patches"/> square land masses of
    /// 16×16 tiles (4096 units) in the land of a new map, one river across the central mass, and
    /// <paramref name="count"/> stamps clustered near the anchor and spread over the map.
    /// </summary>
    private MapDocument MaxLandScene(int count, int patches, out (double X, double Y) anchor)
    {
        const int w = 131072, h = 131072;
        var full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var changes = new List<KeyValuePair<TileKey, byte[]?>>();
        var random = new Random(131072);
        var centreTile = w / 2 / TileMap.TileSize;
        for (var patch = 0; patch < patches; patch++)
        {
            var originX = patch == 0 ? centreTile - 8 : random.Next(0, w / TileMap.TileSize - 16);
            var originY = patch == 0 ? centreTile - 8 : random.Next(0, h / TileMap.TileSize - 16);
            for (var x = 0; x < 16; x++) for (var y = 0; y < 16; y++)
                changes.Add(new(new TileKey(originX + x, originY + y), full));
        }
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, TileMap.Empty(1).WithMany(changes), CurrentLook.Land());
        anchor = (w / 2.0, h / 2.0);
        var river = new River
        {
            Name = "Река", Style = CurrentLook.River(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(anchor.X - 2000, anchor.Y - 1900), new VectorPoint(anchor.X - 400, anchor.Y - 300),
                new VectorPoint(anchor.X + 600, anchor.Y + 500), new VectorPoint(anchor.X + 2100, anchor.Y + 2100)], 40)],
        };
        var objects = HugeObjects(count, w, h, anchor, random);
        var assets = _library.Select((_, i) => Reference(i)).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), $"Максимальная · земля {patches}×4096² · {count} ассетов", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(w, h, "px", "#FF6E9FBF"),
            [CurrentLook.Page("Море", "#FF6E9FBF"), land, layer]).WithAssets(assets);
    }

    private List<SceneObject> HugeObjects(int count, int w, int h, (double X, double Y) anchor, Random random)
    {
        var width = DefaultWidth(w);
        var objects = new List<SceneObject>(count) { Stamp(0, anchor.X - width / 2, anchor.Y - width * .3, width, "Центр") };
        var clustered = Math.Min(count - 1, Math.Max(0, count / 5));
        for (var i = 1; i < count; i++)
        {
            var size = width * (i % 4) switch { 0 => 1, 1 => .5, 2 => .25, _ => .1 };
            double x, y;
            if (i <= clustered)
            {
                // A busy district around the anchor: one screen at 100 % holds a fifth of the map's objects.
                x = anchor.X - 600 + random.NextDouble() * 1200; y = anchor.Y - 450 + random.NextDouble() * 900;
            }
            else { x = random.NextDouble() * (w - size); y = random.NextDouble() * (h - size); }
            objects.Add(Stamp(i, x, y, size, $"Ассет {i + 1}", random.Next(-20, 21)));
        }
        return objects;
    }

    /// <summary>The Huge preset with <paramref name="count"/> stamps: a dense cluster around the anchor and the rest spread.</summary>
    private MapDocument HugeScene(int count, out (double X, double Y) anchor)
    {
        var (w, h) = Huge;
        var width = DefaultWidth(w);
        var random = new Random(16384 + count);
        anchor = (w / 2.0, h / 2.0);
        var objects = new List<SceneObject>(count) { Stamp(0, anchor.X - width / 2, anchor.Y - width * .3, width, "Центр") };
        var clustered = Math.Min(count - 1, Math.Max(0, count / 5));
        for (var i = 1; i < count; i++)
        {
            var size = width * (i % 4) switch { 0 => 1, 1 => .5, 2 => .25, _ => .1 };
            double x, y;
            if (i <= clustered)
            {
                // One screen of a busy district around the anchor at 100 %: ~1000×800 world units.
                x = anchor.X - 600 + random.NextDouble() * 1200; y = anchor.Y - 450 + random.NextDouble() * 900;
            }
            else { x = random.NextDouble() * (w - size); y = random.NextDouble() * (h - size); }
            objects.Add(Stamp(i, x, y, size, $"Ассет {i + 1}", random.Next(-20, 21)));
        }
        var assets = _library.Select((_, i) => Reference(i)).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects);
        return new MapDocument(DocumentId.New(), $"Очень крупная · {count} ассетов", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(w, h, "px", "#FF1E2430"),
            [new BaseColorLayer(LayerId.New(), "Цвет фона", true, false, 1, "#FF1E2430"), layer]).WithAssets(assets);
    }

    private void OpenEditor(MapDocument document)
    {
        if (_view is not null) { _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches(); _window.Content = null; }
        _editor = new EditorViewModel(document, null, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "zoom.mapproj")), new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog,
            exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "zoom-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    // ------------------------------------------------------------------ run

    private async Task Execute()
    {
        _report.Environment["os"] = System.Environment.OSVersion.VersionString;
        _report.Environment["cpu-threads"] = System.Environment.ProcessorCount.ToString();
        _report.Environment["window-logical"] = $"{_window.Width}x{_window.Height}";
        _report.Environment["render-scaling"] = _window.RenderScaling.ToString("0.###");
        _report.Environment["window-physical"] = $"{Math.Round(_window.Width * _window.RenderScaling)}x{Math.Round(_window.Height * _window.RenderScaling)}";
        _report.Environment["max-scale"] = Viewport.MaxScale.ToString("0.###");
        _report.Environment["min-scale"] = Viewport.MinScale.ToString("0.###");
        foreach (var assembly in new[] { "Map.App", "Map.Rendering", "Map.Presentation", "Map.Assets" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, assembly + ".dll");
            if (File.Exists(path))
                _report.Environment["build/" + assembly] = File.GetLastWriteTimeUtc(path).ToString("u") + " sha256=" +
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..16];
        }
        if (System.Environment.GetEnvironmentVariable("MAP_REGIONAL_PROJECT") is { Length: > 0 } regionalProject)
        {
            var pack = RegionalArtPack.Import(_catalog);
            foreach (var item in pack.Manifest.Assets)
            {
                var record = pack.Records[item.Id];
                _library.Add((record,pack.Sets[item.Group],_catalog.ReadVerified(record).Content!));
            }
            var document = MapProjectStore.Open(regionalProject).Document;
            _report.Environment["project"] = Path.GetFullPath(regionalProject);
            var scenario = await Scenario("regional",document,(1280,1140),true,"regional");
            _report.Checks["regional/1000-to-3000-stamps"] = document.Layers.OfType<ObjectLayer>().Sum(l=>l.Objects.Count(o=>o.Stamp is not null)) is >=1000 and <=3000;
            _report.Checks["regional/gpu"] = scenario.Routes.All(r=>r.Gpu);
            _report.Checks["regional/refinement"] = scenario.Routes.All(r=>r.RefinementCompleted is not false);
            _editor.Undo(); // undo only the probe's placed stamp before inspecting the saved map
            _editor.CancelPlacement();_editor.DeselectObject();
            foreach(var scale in new[]{1d,4d,8d,16d})
            { SetScale((1280,1140),scale); await Settle(); await Capture("regional-crop-"+(scale*100)); }
            return;
        }
        ImportLibrary();
        var scenes = System.Environment.GetEnvironmentVariable("MAP_ZOOM_ASSETS_SCENES") ?? "all";
        bool Wanted(string key) => scenes == "all" || scenes.Split(',').Contains(key);
        foreach (var pair in _report.Environment) Console.WriteLine($"env {pair.Key} = {pair.Value}");

        // 1. Standard preset: empty, 1, 10, 100 assets (different assets), 100 of the same asset.
        foreach (var (count, same) in new[] { (0, false), (1, false), (10, false), (100, false), (100, true) })
        {
            if (!Wanted("standard") && !Wanted($"standard-{count}{(same ? "-same" : "")}")) continue;
            var document = StandardScene(count, same, out var anchor);
            var scenario = await Scenario($"standard-{count}{(same ? "-same" : "")}", document, anchor, full: count > 0,
                captureKey: count == 100 && !same ? "standard-100" : null);
            if (count == 100 && !same) await FixedScaleProbes(scenario, anchor);
        }

        // 2. Moderate map with terrain.
        if (Wanted("moderate"))
        {
            var document = ModerateScene(out var anchor);
            await Scenario("moderate", document, anchor, full: true, captureKey: "moderate");
        }

        // 3. Huge preset, filled progressively; stop on the run budget or on memory.
        foreach (var count in new[] { 100, 1_000, 5_000, 20_000, 100_000 })
        {
            if (!Wanted("huge") && !Wanted($"huge-{count}")) continue;
            if (_runClock.Elapsed > RunBudget) { _report.Environment["huge-stopped"] = $"run budget reached before {count}"; break; }
            var document = HugeScene(count, out var anchor);
            var scenario = await Scenario($"huge-{count}", document, anchor, full: count <= 1_000 || count >= 20_000,
                captureKey: count >= 5_000 ? $"huge-{count}" : null);
            if (scenario.WorkingSetMiBAfter > 6_000) { _report.Environment["huge-stopped"] = $"working set {scenario.WorkingSetMiBAfter} MiB at {count}"; break; }
        }

        // 4. The Max preset with land masses, a river and 20 000 stamps.
        if (Wanted("max-land") || scenes == "all")
        {
            if (_runClock.Elapsed > RunBudget) _report.Environment["max-land-stopped"] = "run budget reached";
            else
            {
                var document = MaxLandScene(20_000, 24, out var anchor);
                await Scenario("max-land-20000", document, anchor, full: true, captureKey: "max-land-20000");
            }
        }

        // The gate: the profile ran every scene with a GPU context; the pass/fail of the FPS goal
        // is judged from the numbers in the report, not hidden behind one boolean.
        _report.Checks["all-scenarios-rendered-with-gpu"] = _report.Scenarios.All(s => s.Routes.All(r => r.Gpu));
        _report.Checks["all-requested-refinements-completed"] = _report.Scenarios.All(s => s.Routes.All(r => r.RefinementCompleted is not false));
        _report.Checks["standard-and-moderate-completed"] = scenes != "all" || _report.Scenarios.Count(s => s.Name.StartsWith("standard") || s.Name == "moderate") == 6;
        _report.Environment["run-minutes"] = _runClock.Elapsed.TotalMinutes.ToString("0.0");
    }

    /// <summary>One scene, the shared camera routes, memory after.</summary>
    private async Task<ZoomScenarioReport> Scenario(string name, MapDocument document, (double X, double Y) anchor, bool full, string? captureKey)
    {
        Console.WriteLine($"--- {name}");
        var opening = Stopwatch.StartNew();
        var sinceOpen = Stopwatch.StartNew();
        OpenEditor(document);
        var scenario = new ZoomScenarioReport
        {
            Name = name, CanvasWidth = document.Canvas.Width, CanvasHeight = document.Canvas.Height,
            Objects = document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count),
        };
        _report.Scenarios.Add(scenario);
        var canvas = _view.Canvas;

        var firstFrame = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFirstFrame(object? sender, EventArgs args)
        {
            if (canvas.LastRenderedDocument?.Id == document.Id && canvas.LastFrameHadGpuContext)
                firstFrame.TrySetResult(canvas.LastSceneRenderMilliseconds);
        }
        canvas.FrameRendered += OnFirstFrame;
        try
        {
            var firstScene = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(30));
            opening.Stop();
            var open = new ZoomRouteReport { Name = "open-first-frame", Steps = 1, FramesRendered = 1,
                WallMs = opening.Elapsed.TotalMilliseconds, ScaleEnd = _editor.Session.Viewport.Scale };
            open.SceneMs.Add(firstScene); open.LatencyMs.Add(open.WallMs);
            Complete(scenario, open);
            // The first frame may draw the plain terrain while the exact one is prepared in the
            // background: keep that frame, then the time until the exact terrain is on screen.
            if (captureKey is not null && canvas.LastTerrainRefinementPending) await Capture(captureKey + "-open-first");
            await Settled(open);
            scenario.OpenToExactTerrainMs = sinceOpen.Elapsed.TotalMilliseconds;
            Console.WriteLine($"    open to exact terrain: {scenario.OpenToExactTerrainMs:0.0} ms (first frame {open.WallMs:0.0} ms)");
            if (captureKey is not null) await Capture(captureKey + "-open-exact");
        }
        finally { canvas.FrameRendered -= OnFirstFrame; }
        await Settle();

        // The opening frame was measured above. A subsequent fit already has warmed caches.
        await Route(scenario, "fit-warm", 1, _ => canvas.FitToCanvas());
        var fitScale = _editor.Session.Viewport.Scale;
        if (captureKey is not null) await Capture(captureKey + "-fit");
        // One exact frame for a view nothing was built for yet: the full cost of the terrain
        // and of the visible objects at a new place, without any kept image.
        await Route(scenario, "exact-fresh-view", 1, _ => SetScale(anchor, 0.77));

        // Zoom in on the anchor stamp, one wheel notch per frame, to the maximum scale: cold, then out, then warm.
        await ZoomRoute(scenario, "zoom-in-cold", anchor, 1.15, Viewport.MaxScale);
        if (captureKey is not null) await Capture(captureKey + "-max-zoom");
        await ZoomRoute(scenario, "zoom-out", anchor, 1 / 1.15, fitScale);
        await ZoomRoute(scenario, "zoom-in-warm", anchor, 1.15, Viewport.MaxScale);
        if (!full) { await Finish(scenario); return scenario; }

        // A burst of wheel notches at input cadence, frames counted while it runs.
        await ZoomRoute(scenario, "zoom-out", anchor, 1 / 1.15, fitScale, record: false);
        await BurstRoute(scenario, "zoom-burst", 24, i => ZoomAt(anchor, i < 12 ? 1.15 : 1 / 1.15));

        // At 100 %: pan across the anchor at input cadence, then leave the area and come back.
        SetScale(anchor, 1);
        await BurstRoute(scenario, "pan-burst-100", 60, i => canvas.PanByScreenDeltaForSmoke(i < 30 ? -14 : 14, i < 30 ? -6 : 6));
        await Route(scenario, "leave-and-return", 20, i => canvas.PanByScreenDeltaForSmoke(i < 10 ? -400 : 400, 0));

        // At 300 %, where the reduced stamps are still small on screen: the same pan burst.
        SetScale(anchor, 3);
        await BurstRoute(scenario, "pan-burst-300", 60, i => canvas.PanByScreenDeltaForSmoke(i < 30 ? -14 : 14, i < 30 ? -6 : 6));

        // Selection and placement at 100 %.
        SetScale(anchor, 1);
        await Route(scenario, "select-click", 1, _ => { var p = _editor.Session.Viewport.WorldToScreen(anchor.X, anchor.Y); canvas.ClickForSmoke(p.X, p.Y); });
        _report.Checks[name + "/selected-anchor"] = _editor.Session.SelectedObject is not null || _editor.SelectedEditableArea is not null;
        var set = _library[1 % _library.Count].Set;
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel(); await Settle();
        var armed = false;
        await Route(scenario, "placement-activate", 1, _ => armed = _editor.ChooseGalleryVariant(set.Id, null));
        if (armed)
        {
            await BurstRoute(scenario, "placement-ghost", 60, i =>
            {
                var p = _editor.Session.Viewport.WorldToScreen(anchor.X - 200 + i * 7, anchor.Y + 120);
                canvas.MovePlacementPointerForSmoke(p.X, p.Y);
            });
            var before = scenario.Objects;
            await Route(scenario, "placement-commit", 1, _ => canvas.CommitPlacementForSmoke());
            _report.Checks[name + "/placed-one"] = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == before + 1;
            _editor.CancelPlacement();
        }
        else _report.Checks[name + "/placement-armed"] = false;
        _editor.ActiveTool = EditorTool.Select;
        await Settle();

        // The same two actions a second time, after the panels involved have been shown once:
        // what a user pays for every later selection and every later return to placement.
        // Appended after the original routes so the earlier numbers stay comparable.
        _editor.DeselectObject(); await Settle();
        await Route(scenario, "select-click-repeat", 1, _ => { var p = _editor.Session.Viewport.WorldToScreen(anchor.X, anchor.Y); canvas.ClickForSmoke(p.X, p.Y); });
        _report.Checks[name + "/selected-anchor-repeat"] = _editor.Session.SelectedObject is not null || _editor.SelectedEditableArea is not null;
        _editor.DeselectObject(); await Settle();
        var rearmed = false;
        await Route(scenario, "placement-activate-repeat", 1, _ => rearmed = _editor.ChooseGalleryVariant(set.Id, null));
        _report.Checks[name + "/placement-rearmed"] = rearmed;
        _editor.CancelPlacement();
        _editor.ActiveTool = EditorTool.Select;
        await Finish(scenario);
        return scenario;
    }

    /// <summary>
    /// Two probes that tell the sampling path apart: the anchor stamp drawn at exactly half its
    /// source pixels (a whole mip level) and at 1/2.9 of them (a fractional level between two mips).
    /// Same objects, same view otherwise; ten one-pixel pans each.
    /// </summary>
    private async Task FixedScaleProbes(ZoomScenarioReport scenario, (double X, double Y) anchor)
    {
        var record = _library[0].Record;
        var width = DefaultWidth(Standard.W);
        foreach (var (label, divisor) in new[] { ("scale-pow2", 2.0), ("scale-fractional", 2.9) })
        {
            var scale = record.Width / divisor / width;
            SetScale(anchor, scale);
            await Route(scenario, label, 10, i => _view.Canvas.PanByScreenDeltaForSmoke(i % 2 == 0 ? 1 : -1, 0));
        }
    }

    private async Task Finish(ZoomScenarioReport scenario)
    {
        var (ws, pb) = Memory();
        scenario.WorkingSetMiBAfter = ws; scenario.PrivateMiBAfter = pb;
        scenario.Detail = $"objects={scenario.Objects}; assets={_editor.Session.Document.Assets.Count}";
        await Settle();
    }

    // ------------------------------------------------------------------ routes

    private void ZoomAt((double X, double Y) anchor, double factor)
    {
        var p = _editor.Session.Viewport.WorldToScreen(anchor.X, anchor.Y);
        _view.Canvas.ZoomAtForSmoke(p.X, p.Y, factor);
    }

    /// <summary>The anchor at the centre of the view at the given scale, through the same viewport.</summary>
    private void SetScale((double X, double Y) anchor, double scale)
    {
        var viewport = _editor.Session.Viewport;
        viewport.Set(anchor.X - viewport.ViewWidth / scale / 2, anchor.Y - viewport.ViewHeight / scale / 2, scale);
        _view.Canvas.InvalidateVisual();
    }

    private async Task ZoomRoute(ZoomScenarioReport scenario, string name, (double X, double Y) anchor, double factor, double limit, bool record = true)
    {
        var steps = 0;
        var target = record ? new ZoomRouteReport { Name = name } : null;
        var watch = Stopwatch.StartNew();
        var start = _editor.Session.Viewport.Scale;
        while (steps < 80)
        {
            var scale = _editor.Session.Viewport.Scale;
            if (factor > 1 ? scale >= limit - 1e-9 : scale <= limit + 1e-9) break;
            var (scene, latency) = await Step(() => ZoomAt(anchor, factor));
            steps++;
            if (target is not null) { target.SceneMs.Add(scene); target.LatencyMs.Add(latency); target.GpuFlushMs.Add(Map.App.Controls.MapCanvasControl.LastFrameGpuFlushMilliseconds); }
        }
        watch.Stop();
        if (target is null) return;
        target.Steps = steps; target.WallMs = watch.Elapsed.TotalMilliseconds;
        target.FramesRendered = steps; // one revision-matched frame per sequential input
        target.ScaleStart = start; target.ScaleEnd = _editor.Session.Viewport.Scale;
        Complete(scenario, target);
        await Settled(target);
    }

    /// <summary>
    /// Observes frames after camera movement until the requested terrain is fully refined.
    /// Reports the worst scene plus GPU completion time, separately from background latency.
    /// </summary>
    private async Task Settled(ZoomRouteReport target)
    {
        var canvas = _view.Canvas;
        if (!canvas.LastFrameFastTerrain && !canvas.LastTerrainRefinementPending)
        { target.RefinementCompleted = true; return; }
        var before = canvas.CompletedSceneRevision;
        var exactFrames = new List<double>();
        var completed = false;
        void OnFrame(object? sender, EventArgs args)
        {
            if (canvas.CompletedSceneRevision > before && !canvas.LastFrameFastTerrain)
                lock (exactFrames)
                {
                    var totalFrame = canvas.LastSceneRenderMilliseconds + Map.App.Controls.MapCanvasControl.LastFrameGpuFlushMilliseconds;
                    exactFrames.Add(totalFrame);
                    if (totalFrame > 25 && target.SlowRefinementFrames.Count < 32)
                    {
                        var detail = $"scene={canvas.LastSceneRenderMilliseconds:0.0} gpu={Map.App.Controls.MapCanvasControl.LastFrameGpuFlushMilliseconds:0.0} terrain={canvas.LastRenderStats.TerrainMilliseconds:0.0}";
                        target.SlowRefinementFrames.Add(detail);
                        Console.WriteLine("    slow refinement: " + detail);
                    }
                    completed |= !canvas.LastTerrainRefinementPending;
                }
        }
        canvas.FrameRendered += OnFrame;
        var watch = Stopwatch.StartNew();
        try
        {
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(10);
                lock (exactFrames) { if (completed) break; }
            }
        }
        finally { canvas.FrameRendered -= OnFrame; }
        lock (exactFrames)
            if (exactFrames.Count > 0) { target.SettleFrameMs = exactFrames.Max(); target.SettleDelayMs = watch.Elapsed.TotalMilliseconds; }
        target.RefinementCompleted = completed;
        target.RefinementBuildMs = canvas.LastRenderStats.TerrainRefinementBuildMilliseconds;
        Console.WriteLine($"    settled exact frame: worst scene+GPU={target.SettleFrameMs:0.0} ms, delay={target.SettleDelayMs:0.0} ms, background={target.RefinementBuildMs:0.0} ms, complete={target.RefinementCompleted}");
    }

    private async Task Route(ZoomScenarioReport scenario, string name, int steps, Action<int> step)
    {
        TraceMark(name);
        var target = new ZoomRouteReport { Name = name, ScaleStart = _editor.Session.Viewport.Scale };
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
        Complete(scenario, target);
    }

    /// <summary>
    /// Inputs at a fixed 16 ms cadence whether or not the previous frame is done, like a wheel or a
    /// pointer; frames are counted while the burst runs and the last input is timed to the frame
    /// that follows it. Scene times are those of the frames that did get drawn.
    /// </summary>
    private async Task BurstRoute(ZoomScenarioReport scenario, string name, int steps, Action<int> step)
    {
        var target = new ZoomRouteReport { Name = name, ScaleStart = _editor.Session.Viewport.Scale };
        var canvas = _view.Canvas;
        var sceneTimes = new List<double>();
        var frameStamps = new List<(long Stamp, long Revision)>();
        var lastRevision = canvas.CompletedSceneRevision;
        void OnFrame(object? s, EventArgs e)
        {
            lock (sceneTimes)
            {
                var revision = canvas.CompletedSceneRevision;
                if (revision <= lastRevision) return; // compositor replays are not fresh input frames
                lastRevision = revision;
                sceneTimes.Add(canvas.LastSceneRenderMilliseconds);
                frameStamps.Add((Stopwatch.GetTimestamp(), revision));
            }
        }
        canvas.FrameRendered += OnFrame;
        var watch = Stopwatch.StartNew();
        var inputStamps = new (long Stamp, long Revision)[steps];
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Submit independently of the UI queue, so a busy UI does not stop the measurement
            // clock and make its own stall disappear from the reported input latency.
            await Task.Run(async () =>
            {
                var schedule = Stopwatch.StartNew();
                for (var i = 0; i < steps; i++)
                {
                    // Absolute deadlines avoid accumulating the Windows timer's rounding on
                    // every delay (which otherwise turns a nominal 60 Hz input into 35 Hz).
                    var remaining = i * 16.0 - schedule.Elapsed.TotalMilliseconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
                    var index = i;
                    var stamp = Stopwatch.GetTimestamp();
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            step(index);
                            inputStamps[index] = (stamp, canvas.RequestFrameForSmoke());
                            if (index == steps - 1) submitted.TrySetResult();
                        }
                        catch (Exception error) { submitted.TrySetException(error); }
                    }, DispatcherPriority.Input);
                }
            });
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var lastInput = inputStamps[^1].Revision;
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                lock (sceneTimes) { if (frameStamps.Any(f => f.Revision >= lastInput)) break; }
                await Task.Delay(2);
            }
        }
        finally { canvas.FrameRendered -= OnFrame; }
        watch.Stop();
        List<(long Stamp, long Revision)> frames;
        lock (sceneTimes) { frames = [.. frameStamps]; target.SceneMs.AddRange(sceneTimes); }
        // An older frame that merely finished later cannot satisfy this input's revision.
        foreach (var input in inputStamps)
        {
            var frame = frames.FirstOrDefault(f => f.Revision >= input.Revision);
            if (input.Revision == 0 || frame.Stamp == 0) throw new TimeoutException("Input had no matching canvas frame.");
            target.LatencyMs.Add((frame.Stamp - input.Stamp) * 1000.0 / Stopwatch.Frequency);
        }
        var intervals = frames.Skip(1).Select((frame, index) =>
            (frame.Stamp - frames[index].Stamp) * 1000.0 / Stopwatch.Frequency).Order().ToArray();
        if (intervals.Length > 0) { target.FrameIntervalP95 = Percentile(intervals, .95); target.FrameIntervalMax = intervals[^1]; }
        target.Steps = steps;
        target.FramesRendered = frames.Count;
        target.WallMs = frames.Count > 0 ? (frames[^1].Stamp - inputStamps[0].Stamp) * 1000.0 / Stopwatch.Frequency : watch.Elapsed.TotalMilliseconds;
        target.ScaleEnd = _editor.Session.Viewport.Scale;
        Complete(scenario, target, burst: true);
        await Settled(target);
    }

    /// <summary>
    /// With MAP_TRACE_MARKS set, prints the route start in milliseconds since process start, so a
    /// runtime EventPipe sample trace of the same run can be cut to the route's window.
    /// </summary>
    private static void TraceMark(string name)
    {
        if (System.Environment.GetEnvironmentVariable("MAP_TRACE_MARKS") is not { Length: > 0 }) return;
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"mark {name} at {(DateTime.Now - process.StartTime).TotalMilliseconds:0} ms");
    }

    private void Complete(ZoomScenarioReport scenario, ZoomRouteReport target, bool burst = false)
    {
        var scene = target.SceneMs.Order().ToArray();
        var latency = target.LatencyMs.Order().ToArray();
        target.SceneP50 = Percentile(scene, .5); target.SceneP95 = Percentile(scene, .95); target.SceneP99 = Percentile(scene, .99);
        target.SceneMax = scene.Length == 0 ? double.NaN : scene[^1];
        target.LatencyP50 = Percentile(latency, .5); target.LatencyP95 = Percentile(latency, .95); target.LatencyP99 = Percentile(latency, .99);
        target.LatencyMax = latency.Length == 0 ? double.NaN : latency[^1];
        target.LongPauses = target.LatencyMs.Count(l => l > 50);
        target.PausesOver100 = target.LatencyMs.Count(l => l > 100);
        target.Fps = target.WallMs > 0 && target.FramesRendered > (burst ? 0 : 1)
            ? target.FramesRendered * 1000.0 / target.WallMs : null;
        var canvas = _view.Canvas;
        var stats = canvas.LastRenderStats;
        target.ObjectsDrawnLast = canvas.LastFrameObjectsDrawn; target.ObjectCandidatesLast = stats.ObjectCandidates;
        target.DrawCallsLast = stats.DrawCalls; target.AtlasBatchesLast = stats.StampAtlasBatches;
        target.TerrainMsLast = stats.TerrainMilliseconds; target.ObjectsMsLast = stats.ObjectsMilliseconds;
        target.StampBatchMsLast = stats.StampBatchMilliseconds; target.StampSingleMsLast = stats.StampSingleMilliseconds;
        target.MipBlendsLast = stats.StampMipBlends; target.MipBlendMsLast = stats.StampMipBlendMilliseconds;
        target.GpuFlushMsLast = Map.App.Controls.MapCanvasControl.LastFrameGpuFlushMilliseconds;
        target.TerrainReusedLast = stats.TerrainReused;
        target.LandMsLast = stats.LandMilliseconds; target.WaterMsLast = stats.WaterMilliseconds;
        target.TierLast = canvas.LastLodTier.ToString(); target.Gpu = canvas.LastFrameHadGpuContext;
        target.ObjectCacheCount = canvas.ObjectCacheCount; target.ObjectCacheMiB = canvas.ObjectCacheBytes / (1024 * 1024);
        target.WorkingSetMiB = Memory().WorkingSet;
        target.MemoryPressureLast = Map.Core.Resources.WorkingMemoryBudget.Shared.IsUnderPressure;
        target.SystemAvailableMiBLast = Map.Core.Resources.WorkingMemoryBudget.Shared.Snapshot.AvailableBytes / (1024 * 1024);
        scenario.Routes.Add(target);
        Console.WriteLine($"{scenario.Name}/{target.Name}: steps={target.Steps} frames={target.FramesRendered} fps={target.Fps:0.0} " +
            $"scene P50/P95/P99/max={target.SceneP50:0.0}/{target.SceneP95:0.0}/{target.SceneP99:0.0}/{target.SceneMax:0.0} " +
            $"latency P50/P95/max={target.LatencyP50:0.0}/{target.LatencyP95:0.0}/{target.LatencyMax:0.0} pauses={target.LongPauses} " +
            $"scale={target.ScaleStart:0.###}->{target.ScaleEnd:0.###} drawn={target.ObjectsDrawnLast} tier={target.TierLast} gpu={target.Gpu} ws={target.WorkingSetMiB}MiB " +
            $"last: terrain={target.TerrainMsLast:0.0} (land={target.LandMsLast:0.0} water={target.WaterMsLast:0.0} rivers=[{string.Join('/', stats.RiverPassMilliseconds.Select(p => p.ToString("0")))}]) objects={target.ObjectsMsLast:0.0} batch={target.StampBatchMsLast:0.0}/{target.AtlasBatchesLast} single={target.StampSingleMsLast:0.0} mipblend={target.MipBlendMsLast:0.0}/{target.MipBlendsLast} calls={target.DrawCallsLast} gpuflush={target.GpuFlushMsLast:0.0} terrainReused={target.TerrainReusedLast} tilesReused={stats.TilesReused} tilePrep={stats.DetailPrepareMilliseconds:0} tileRaster={stats.DetailRasterMilliseconds:0}");
    }

    /// <summary>One input, then the first frame finished after it; scene time of that frame and wall time from the input.</summary>
    private async Task<(double Scene, double Latency)> Step(Action input)
    {
        var canvas = _view.Canvas;
        var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = long.MaxValue;
        void OnFrame(object? s, EventArgs e)
        {
            if (canvas.CompletedSceneRevision >= System.Threading.Volatile.Read(ref requested))
                completion.TrySetResult(canvas.LastSceneRenderMilliseconds);
        }
        canvas.FrameRendered += OnFrame;
        try
        {
            var watch = Stopwatch.StartNew();
            input();
            System.Threading.Volatile.Write(ref requested, canvas.RequestFrameForSmoke());
            var scene = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            watch.Stop();
            return (scene, watch.Elapsed.TotalMilliseconds);
        }
        finally { canvas.FrameRendered -= OnFrame; }
    }

    private async Task Capture(string name)
    {
        var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
        using var bitmap = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path); data.SaveTo(file);
        _report.Frames.Add(path);
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

    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(50); }

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
    }
}
