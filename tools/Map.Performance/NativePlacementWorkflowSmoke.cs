using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.Performance;

internal sealed record PlacementSample(string Phase, int Batch, int Objects, double QueueMs,
    double HandlerMs, double InputToFrameMs, double SceneMs, double PrepareMs, double NamesMs,
    double CommitMs, double MetadataMs, int Backlog, int RealizedRows, long PrivateBytes);

internal sealed class PlacementWorkflowReport
{
    public string Observation { get; set; } = "Own native Avalonia window, ShowActivated=false. Input timestamp is taken on the producer thread before enqueue; complete visual-tree raster frame includes the committed snapshot. OS scan-out is not observed.";
    public List<PlacementSample> Samples { get; } = [];
    public List<double> HeartbeatGapsMs { get; } = [];
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string, double> CameraMs { get; } = [];
    public long PeakPrivateBytes { get; set; }
    public string? Failure { get; set; }
    public bool GatePassed { get; set; }
    public string Step { get; set; } = "starting";
}

internal static class NativePlacementWorkflowSmoke
{
    public static PlacementWorkflowReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<PlacementWorkflowReport>();
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
        var thread = new Thread(() =>
        {
            PlacementWorkflowApplication.Options = options;
            PlacementWorkflowApplication.Completion = completion;
            AppBuilder.Configure<PlacementWorkflowApplication>().UsePlatformDetect().WithInterFont()
                .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.GetAwaiter().GetResult();
    }
}

public sealed class PlacementWorkflowApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<PlacementWorkflowReport> Completion = null!;
    private readonly PlacementWorkflowReport _report = new();
    private readonly object _reportGate = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private string _workspace = "";
    private Window _window = null!;
    private AssetCatalog _catalog = null!;
    private EditorView _view = null!;
    private RenderTargetBitmap _frame = null!;
    private int _submitted;
    private int _finished;
    private ProjectStore _store = null!;
    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatus
    {
        public uint Length = 64, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);

    public override void Initialize()
    {
        var production = new global::Map.App.App();
        production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources) Resources[resource.Key] = resource.Value;
        var styles = production.Styles.ToArray();
        production.Styles.Clear();
        Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _workspace = Path.Combine(Path.GetTempPath(), "map-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _catalog.ImportSet([
            OwnerMountainFixtures.Path(1),
            OwnerMountainFixtures.Path(2)],
            "Дерево", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        _window = new Window
        {
            Title = "Map — placement route measurement", Width = 1400, Height = 900,
            ShowActivated = false, ShowInTaskbar = false, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000),
        };
        _frame = new RenderTargetBitmap(new PixelSize(1400, 900));
        var watchdog = new System.Threading.Timer(_ =>
        {
            using var process = Process.GetCurrentProcess();
            var bytes = process.PrivateMemorySize64;
            var memory = new MemoryStatus();
            var reserveLow = GlobalMemoryStatusEx(memory) && memory.AvailablePhysical < 768UL * 1024 * 1024;
            lock (_reportGate)
            {
                _report.PeakPrivateBytes = Math.Max(_report.PeakPrivateBytes, bytes);
                var limit = Options.Profile == "native-placement-baseline" ? 120 : 360;
                if (_wall.Elapsed.TotalSeconds <= limit && bytes < 2560L * 1024 * 1024 && !reserveLow) return;
                _report.Failure = $"Own-process guard: {_wall.Elapsed.TotalSeconds:F1}s / {bytes / 1048576} MiB; limits {limit}s / 2560 MiB; free RAM {memory.AvailablePhysical / 1048576} MiB (reserve 768 MiB)";
                SaveProgress();
            }
            Environment.Exit(3);
        }, null, 500, 500);
        _window.Opened += async (_, _) =>
        {
            var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            var lastBeat = Stopwatch.GetTimestamp();
            heartbeat.Tick += (_, _) =>
            {
                var now = Stopwatch.GetTimestamp();
                lock (_reportGate) _report.HeartbeatGapsMs.Add(Stopwatch.GetElapsedTime(lastBeat, now).TotalMilliseconds);
                lastBeat = now;
            };
            heartbeat.Start();
            try
            {
                await RunAsync();
            }
            catch (Exception ex)
            {
                _report.Failure = ex.ToString();
            }
            finally
            {
                watchdog.Dispose();
                heartbeat.Stop();
                try { await _catalog.FlushMetadataAsync(); }
                catch (IOException ex) { _report.Failure ??= ex.ToString(); }
                SaveProgress();
                Completion.TrySetResult(_report);
                if (_view is not null) { _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches(); }
                _window.Content = null;
                _frame.Dispose();
                _window.Close();
                desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window;
        base.OnFrameworkInitializationCompleted();
    }

    private async Task RunAsync()
    {
        if (Options.Profile == "native-placement-overlap")
        {
            await AttachAsync(Document(8192));
            await OverlapAsync();
            _report.GatePassed = _report.Checks.Count > 0 && _report.Checks.Values.All(v => v);
            return;
        }
        if (Options.Profile == "native-placement-ux")
        {
            await AttachAsync(Document(8192));
            await BatchesAsync("ordinary-8192", 20);
            await CameraAsync("ordinary");
            await GesturesAndAutosaveAsync();
            await GalleryAsync();
            _report.GatePassed = _report.Checks.Values.All(v => v);
            return;
        }
        await AttachAsync(Document(131072));
        await BatchesAsync("maximum-cold-and-100k", 100);
        History("100k");
        await CameraAsync("100k");
        var source = _view.Editor.Session.Document;
        var layer = source.Layers.OfType<ObjectLayer>().Single();
        var prototype = layer.Objects[0];
        // Fixture preparation may release the preceding editor's history and native caches before
        // allocating a million-object document. The measured editor is attached normally below.
        _view.CloseAuxiliaryWindowsForSmoke();
        _window.Content = null;
        _view.Canvas.DisposeRenderCaches();
        _view = null!;
        await Task.Delay(50);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        Step("preparing million fixture");
        var fixture = await Task.Run(() =>
        {
            var objects = new SceneObject[1_000_000 - layer.Objects.Count];
            for (var i = 0; i < objects.Length; i++) objects[i] = MultiSelectionGeometry.Translate(
                [prototype], 128 + (i % 1000) * 130 - prototype.X,
                128 + (i / 1000) * 130 - prototype.Y)[0] with
            {
                Id = ObjectId.New(), Name = "Дерево " + (layer.Objects.Count + i),
            };
            return new PlaceAssetObjectsCommand(layer.Id, source.Assets, objects).Apply(source);
        });
        await AttachAsync(fixture);
        source = null!; layer = null!; prototype = null!; fixture = null!;
        await BatchesAsync("million", 20);
        History("million");
        await CameraAsync("million");
        await SaveAndReopenAsync("million");
        await AttachAsync(Document(8192));
        await BatchesAsync("ordinary-8192", 20);
        History("ordinary");
        await CameraAsync("ordinary");
        await GesturesAndAutosaveAsync();
        await GalleryAsync();
        var warm = _report.Samples.Where(s => s.Batch > 0).ToArray();
        _report.Checks["exact batches"] = _report.Samples.Count == 140;
        _report.GatePassed = _report.Checks.Values.All(v => v)
            && P95(warm.Where(s => s.Phase != "million").Select(s => s.InputToFrameMs)) <= 100
            && P95(warm.Where(s => s.Phase == "million").Select(s => s.InputToFrameMs)) <= 150
            && _report.Samples[0].InputToFrameMs <= 250
            && _report.CameraMs.Where(p => p.Key.EndsWith("warm-p95")).All(p => p.Value <= 33.3)
            && _report.HeartbeatGapsMs.DefaultIfEmpty().Max() <= 250;
    }

    private async Task AttachAsync(MapDocument document)
    {
        if (_view is not null)
        {
            _view.CloseAuxiliaryWindowsForSmoke();
            _window.Content = null;
            _view.Canvas.DisposeRenderCaches();
        }
        _store = new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery")));
        var editor = new EditorViewModel(document, null, false,
            _store, new Recent(),
            new Picker(Path.Combine(_workspace, "scenario.mapproj")), new Prompts(), new Log(),
            assetCatalog: _catalog, availableFontFamilies: ["Arial"]);
        _view = new EditorView(editor, () => Task.CompletedTask, () => { }, () => { });
        _window.Content = _view;
        await Task.Delay(100);
        _view.OpenObjects();
        editor.SelectAssetSet(editor.LibrarySets[0].SetId);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.StampAreaRadius = 1024;
        editor.StampDensity = 1000 * StampPlacement.DensityReferenceArea / (Math.PI * 1024 * 1024);
        editor.PlacementAutoSelect = PlacementAutoSelectPolicy.Off;
        editor.ArmStampPlacement();
        _view.Canvas.FitToCanvas();
        await Task.Delay(50);
        var ready = Stopwatch.StartNew();
        while (!ReferenceEquals(_view.Canvas.LastRenderedDocument, document))
        {
            if (ready.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Initial fixture frame did not finish.");
            await Task.Delay(20);
        }
        await Dispatcher.UIThread.InvokeAsync(() => { _view.UpdateLayout(); _frame.Render(_view); }, DispatcherPriority.Background);
        _report.CameraMs["fixture ready " + Count(document)] = ready.Elapsed.TotalMilliseconds;
        if (editor.PlannedStampCount != 1000) throw new InvalidOperationException("Expected exactly 1000 planned stamps.");
    }

    private bool ShowsObjects() => _view.Canvas.LastFrameObjectsDrawn > 0
        || _view.Canvas.LastRenderStats.LayerImagesKept > 0 || _view.Canvas.LastRenderStats.ObjectTilesDrawn > 0;

    private async Task BatchesAsync(string phase, int count)
    {
        var editor = _view.Editor;
        var startCount = Count(editor.Session.Document);
        var startDepth = editor.Session.UndoDepth;
        var completed = new TaskCompletionSource();
        // The producer never awaits an earlier click. Every intended timestamp precedes UI enqueue.
        _ = Task.Run(async () =>
        {
            var schedule = Stopwatch.StartNew();
            for (var batch = 0; batch < count; batch++)
            {
                var wait = batch * 200 - schedule.Elapsed.TotalMilliseconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
                var submittedAt = Stopwatch.GetTimestamp();
                var index = batch;
                Interlocked.Increment(ref _submitted);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        var began = Stopwatch.GetTimestamp();
                        // Alternate clustered clicks and four spatial boundaries, always in view.
                        var world = editor.Session.Document.Canvas.Width;
                        var center = index % 3 == 0 ? world * 0.5 : world * ((index % 4 + 1) / 5d);
                        var screen = editor.Session.Viewport.WorldToScreen(center, world * 0.5);
                        if (index % 7 == 6) editor.NextVariant();
                        var requiredCount = startCount + (index + 1) * 1000;
                        var nextFrame = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                        void OnFrame(object? sender, EventArgs args)
                        {
                            // A frame shows the objects when it drew them, drew their cached dense tiles,
                            // or drew their layer from its kept image (LayerImageCache: still and pan frames).
                            if (_view.Canvas.LastRenderedDocument is { } frameDocument && Count(frameDocument) >= requiredCount
                                && ShowsObjects())
                                nextFrame.TrySetResult(_view.Canvas.LastSceneRenderMilliseconds);
                        }
                        _view.Canvas.FrameRendered += OnFrame;
                        _view.Canvas.ClickForSmoke(screen.X, screen.Y);
                        var handled = Stopwatch.GetTimestamp();
                        var document = editor.Session.Document;
                        var timing = editor.LastPlacementTiming;
                        double sceneMs;
                        try { sceneMs = await nextFrame.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
                        finally { _view.Canvas.FrameRendered -= OnFrame; }
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _view.UpdateLayout();
                            _frame.Render(_view);
                            var ended = Stopwatch.GetTimestamp();
                            var rendered = _view.Canvas.LastRenderedDocument;
                            if (rendered is null || Count(rendered) < requiredCount
                                || Count(document) != requiredCount
                                || !ShowsObjects())
                                throw new InvalidOperationException($"Frame mismatch: same={ReferenceEquals(rendered, editor.Session.Document)}, objects={Count(document)}, expected={startCount + (index + 1) * 1000}, drawn={_view.Canvas.LastRenderStats.ObjectsDrawn}, canvas={_view.Canvas.Bounds}, visible={_view.Canvas.IsEffectivelyVisible}, status={editor.StatusDetail}.");
                            var sample = new PlacementSample(phase, index, Count(document),
                                Stopwatch.GetElapsedTime(submittedAt, began).TotalMilliseconds,
                                Stopwatch.GetElapsedTime(began, handled).TotalMilliseconds,
                                Stopwatch.GetElapsedTime(submittedAt, ended).TotalMilliseconds,
                                sceneMs, timing.Prepare, timing.Names,
                                timing.Commit, timing.Metadata, _submitted - Interlocked.Increment(ref _finished),
                                _view.ObjectListRealizedCount, Process.GetCurrentProcess().PrivateMemorySize64);
                            lock (_reportGate) { _report.Samples.Add(sample); SaveProgress(); }
                            Console.WriteLine($"{phase} {index + 1}/{count}: input {sample.InputToFrameMs:F1}ms; names {sample.NamesMs:F1}; scene {sample.SceneMs:F1}; backlog {sample.Backlog}");
                            if (index == count - 1) completed.TrySetResult();
                        }, DispatcherPriority.Background);
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                }, DispatcherPriority.Input);
            }
        });
        await completed.Task;
        Check(phase + " count/history", Count(editor.Session.Document) == startCount + count * 1000
            && editor.Session.UndoDepth == startDepth + count);
        Check(phase + " virtual rows", _view.ObjectListRealizedCount is > 0 and < 150);
        Check(phase + " unique ids/names", await Task.Run(() =>
        {
            var ids = new HashSet<ObjectId>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
                .All(o => ids.Add(o.Id) && names.Add(o.Name));
        }));
        SaveProgress();
    }

    private void History(string phase)
    {
        Step(phase + " history");
        var editor = _view.Editor;
        var count = Count(editor.Session.Document);
        var last = editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects.TakeLast(1000).ToArray();
        var timer = Stopwatch.StartNew();
        var undo = editor.Session.Undo();
        Step(phase + " undo complete");
        _report.CameraMs[phase + " undo"] = timer.Elapsed.TotalMilliseconds;
        var removed = Count(editor.Session.Document) == count - 1000;
        var redo = editor.Session.Redo();
        Step(phase + " redo complete");
        Check(phase + " undo/redo exact tail", undo && removed && redo
            && last.SequenceEqual(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects.TakeLast(1000)));
    }

    private async Task CameraAsync(string phase)
    {
        var viewport = _view.Editor.Session.Viewport;
        var center = _view.Editor.Session.Document.Canvas.Width / 2d;
        var fit = viewport.Scale;
        foreach (var (label, scale) in new[] { ("fit", fit), (".125", .125), (".2", .2), ("transition", .35), ("work", 1d) })
        {
            Step(phase + " camera " + label);
            var measurements = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var watch = Stopwatch.StartNew();
                viewport.Set(center - viewport.ViewWidth / scale / 2, center - viewport.ViewHeight / scale / 2, scale);
                _view.Canvas.PanByScreenDeltaForSmoke(i % 2 == 0 ? 12 : -12, 0);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _view.UpdateLayout(); _frame.Render(_view);
                }, DispatcherPriority.Background);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                Console.WriteLine($"camera {phase} {label} {i}: {elapsed:F1}ms; scene {_view.Canvas.LastSceneRenderMilliseconds:F1}; drawn {_view.Canvas.LastRenderStats.ObjectsDrawn}; occluded {_view.Canvas.LastRenderStats.StampOccludedObjects}; tiles {_view.Canvas.LastRenderStats.ObjectTilesDrawn}");
                if (i == 0) _report.CameraMs[phase + " " + label + " cold"] = elapsed;
                else measurements.Add(elapsed);
                Check(phase + " " + label + " rendered", ReferenceEquals(_view.Canvas.LastRenderedDocument, _view.Editor.Session.Document)
                    && _view.Canvas.LastFrameObjectsDrawn > 0);
                await Task.Delay(20);
            }
            _report.CameraMs[phase + " " + label + " warm-p95"] = P95(measurements);
        }
        _view.Canvas.FitToCanvas();
        SaveProgress();
    }

    private async Task SaveAndReopenAsync(string phase)
    {
        Step(phase + " save/reopen");
        var editor = _view.Editor;
        var snapshot = editor.Session.Document;
        var timer = Stopwatch.StartNew();
        Check(phase + " real save", await editor.SaveAsync());
        _report.CameraMs[phase + " save"] = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        Check(phase + " real reopen exact properties/order/assets", await Task.Run(() => SameScene(snapshot, _store.Open(editor.DocumentPath!).Document)));
        _report.CameraMs[phase + " reopen"] = timer.Elapsed.TotalMilliseconds;
        SaveProgress();
    }

    private async Task GesturesAndAutosaveAsync()
    {
        var editor = _view.Editor;
        editor.PrimaryAutosaveEnabled = true;
        editor.StampPlacementMode = StampPlacementMode.One;
        var before = Count(editor.Session.Document);
        _view.Canvas.PressMoveReleaseForSmoke(300, 300, 600, 300);
        Check("One held drag commits one and stays armed", Count(editor.Session.Document) == before + 1 && editor.HasPendingPlacement);
        editor.StampPlacementMode = StampPlacementMode.Series;
        editor.SetSeriesSpacingPercent(75, true);
        before = Count(editor.Session.Document);
        var depth = editor.Session.UndoDepth;
        _view.Canvas.PressForSmoke(300, 300); _view.Canvas.MoveForSmoke(600, 300);
        Check("Series pending is transient and defers autosave", editor.IsStampSeriesActive
            && Count(editor.Session.Document) == before && !await editor.TryAutosaveAsync());
        _frame.Render(_view);
        _view.Canvas.ReleaseForSmoke(600, 300);
        var series = Count(editor.Session.Document) - before;
        Check("Series distance gesture one command", series > 1 && editor.Session.UndoDepth == depth + 1 && !editor.HasTransientEditorAction);
        editor.Session.Undo();
        Check("Series one Undo", Count(editor.Session.Document) == before);
        editor.Session.Redo();
        foreach (var cancellation in new[] { "capture", "leave", "escape", "tool" })
        {
            editor.StampPlacementMode = StampPlacementMode.Series; editor.ArmStampPlacement();
            before = Count(editor.Session.Document);
            _view.Canvas.PressForSmoke(300, 300); _view.Canvas.MoveForSmoke(500, 300);
            if (cancellation == "capture") _view.Canvas.CancelGestureForSmoke();
            if (cancellation == "leave") _view.Canvas.PointerLeftForSmoke();
            if (cancellation == "escape") editor.CancelPlacement();
            if (cancellation == "tool") editor.ActiveTool = EditorTool.Select;
            _view.Canvas.ReleaseForSmoke(600, 300);
            Check("Series cancel " + cancellation, !editor.IsStampSeriesActive && Count(editor.Session.Document) == before);
        }
        editor.ArmStampPlacement();
        Check("armed real recovery autosave", await editor.TryAutosaveAsync());
        var snapshot = editor.Session.Document;
        Check("recovery exact committed snapshot", await Task.Run(() =>
        {
            var candidate = _store.FindRecoveryCandidates().Single(c => c.DocumentId == snapshot.Id.Value);
            return candidate.Kind == RecoveryCandidateKind.Autosave && SameScene(snapshot, _store.RestoreRecovery(candidate).Document);
        }));
        await SaveAndReopenAsync("ordinary");
        editor.StampPlacementMode = StampPlacementMode.One;
        _view.Canvas.ClickForSmoke(400, 350);
        Check("armed real primary autosave", await editor.TryPrimaryAutosaveAsync(ignoreDebounce: true));
        snapshot = editor.Session.Document;
        Check("primary exact committed snapshot", await Task.Run(() => SameScene(snapshot, _store.Open(editor.DocumentPath!).Document)));
    }

    /// <summary>«Без наложений» through the real panel toggle and real canvas clicks.</summary>
    private async Task OverlapAsync()
    {
        Step("no-overlap scatter");
        var editor = _view.Editor;
        editor.StampAreaRadius = 220;
        editor.StampDensity = 260;
        await Task.Delay(50);
        var toggle = _view.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "StampAvoidOverlapToggle");
        toggle.BringIntoView(); _view.UpdateLayout();
        Check("toggle visible in the scatter mode", toggle.IsEffectivelyVisible && !editor.StampAvoidOverlap);
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        Check("toggle switches the tool", editor.StampAvoidOverlap && toggle.IsChecked == true
            && editor.StampAreaSummary.Contains("без наложений"));

        var before = Count(editor.Session.Document);
        _view.Canvas.ClickForSmoke(500, 420); await Task.Delay(80);
        var first = Count(editor.Session.Document) - before;
        _view.Canvas.ClickForSmoke(600, 420); await Task.Delay(80);
        var stamps = editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .Where(o => o.Kind == SceneObjectKind.Stamp).ToArray();
        var boxes = stamps.Select(o => StampCollision.Box(o.X + o.Width / 2, o.Y + o.Height / 2, o.Width, o.Height,
            o.RotationDegrees, StampCollision.DefaultFraction)).ToArray();
        var overlaps = 0;
        for (var i = 0; i < boxes.Length; i++)
        for (var j = i + 1; j < boxes.Length; j++)
            if (StampCollision.Overlaps(boxes[i], boxes[j])) overlaps++;
        Console.WriteLine($"no-overlap: first click {first}, total {stamps.Length - before}, planned {editor.PlannedStampCount}, overlaps {overlaps}");
        Check("first click places fewer than planned", first > 1 && first < editor.PlannedStampCount);
        Check("second click adds only into free space", stamps.Length - before > first);
        Check("no collision boxes intersect", overlaps == 0);
        Check("two clicks are two undo steps", editor.Session.UndoDepth == 2);
        await Dispatcher.UIThread.InvokeAsync(() => { _view.UpdateLayout(); }, DispatcherPriority.Background);
        Capture(_view, "no-overlap");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        Check("toggle switches back off", !editor.StampAvoidOverlap);

        // «После размещения: выделять / не выделять» through the real segmented buttons.
        Step("select after placement");
        editor.StampPlacementMode = StampPlacementMode.One; await Task.Delay(50);
        Button Segment(string name)
        {
            var button = _view.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);
            button.BringIntoView(); _view.UpdateLayout();
            return button;
        }
        var dontSelect = Segment("PlacementAutoSelectOff");
        var select = Segment("PlacementAutoSelectAlways");
        Check("after-placement switch shows not selecting", dontSelect.IsEffectivelyVisible && !editor.SelectAfterPlacement
            && dontSelect.Classes.Contains("selected") != select.Classes.Contains("selected"));
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        var placedBefore = Count(editor.Session.Document);
        _view.Canvas.ClickForSmoke(800, 300); await Task.Delay(80);
        Check("select: placed asset is selected and the tool stays armed", editor.SelectAfterPlacement
            && Count(editor.Session.Document) == placedBefore + 1 && editor.Session.SelectedObjectId is not null && editor.HasPendingPlacement);
        dontSelect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50);
        _view.Canvas.ClickForSmoke(850, 340); await Task.Delay(80);
        var newest = editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Last();
        Check("dont select: placed asset is not selected", !editor.SelectAfterPlacement
            && Count(editor.Session.Document) == placedBefore + 2 && !editor.Session.SelectedObjectIds.Contains(newest.Id));
        Capture(_view, "select-after");
    }

    private async Task GalleryAsync()
    {
        Step("gallery");
        var editor = _view.Editor;
        var variants = _catalog.Variants.ToList(); var sets = _catalog.Sets.ToList(); var original = variants[0];
        for (var i = 0; i < 300; i++)
        {
            var variant = original with { Id = "galleryv" + i, Label = "Демонстрационный вариант " + i };
            variants.Add(variant);
            sets.Add(new AssetSet { Id = "gallerys" + i, Name = "Галерея " + i.ToString("D3"),
                CoverVariantId = variant.Id, VariantIds = [variant.Id], ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(i + 1) });
        }
        var longVariants = Enumerable.Range(0, 300).Select(i => original with { Id = "longv" + i, Label = "Длинный вариант " + i }).ToArray();
        variants.AddRange(longVariants);
        sets.Add(new AssetSet { Id = "longset", Name = "Длинный набор", CoverVariantId = "longv0",
            VariantIds = longVariants.Select(v => v.Id).ToArray(), ImportedUtc = DateTimeOffset.UnixEpoch.AddDays(1), Favorite = true });
        _catalog.ReplaceLibrary(new AssetLibraryDocument { Records = _catalog.Records.ToList(), Variants = variants, Sets = sets,
            Collections = _catalog.Collections.ToList(), Recent = _catalog.Recent.ToList() });
        editor.RefreshLibrary(); _view.RefreshLibraryPanel(); _view.OpenAssetCatalog();
        await Task.Delay(200);
        var window = _view.AssetCatalogWindowForSmoke!; window.UpdateLayout();
        Check("gallery finite realized cards", _view.GalleryRealizedCards is > 0 and < 100);
        var count = Count(editor.Session.Document); var recent = _catalog.Recent.ToArray();
        Click(window, "GalleryExpand_longset"); await Task.Delay(100);
        Check("variant count expands and stays open", window.IsVisible && Count(editor.Session.Document) == count);
        Capture(window, "gallery");
        Click(window, "GalleryVariant_longv1"); await Task.Delay(100);
        // One click puts the asset in the hand: the catalog closes and the ghost is armed on the
        // map (owner, 2026-10-24; it used to keep the catalog open like the reference editor).
        Check("variant click arms exact fixed ghost and closes the catalog", !window.IsVisible
            && editor.CurrentVariant?.Id == "longv1" && !editor.RandomVariantAfterPlacement
            && editor.PendingPlacement is { PointerVisible: true } && Count(editor.Session.Document) == count);
        _view.OpenAssetCatalog(); await Task.Delay(100);
        var chosenTile = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "GalleryVariant_longv1");
        chosenTile.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!)); await Task.Delay(100);
        Check("double click arms exact fixed ghost without click-through", !window.IsVisible
            && editor.CurrentVariant?.Id == "longv1"
            && editor.PendingPlacement is { PointerVisible: true } && Count(editor.Session.Document) == count);
        Check("selection is not recent usage", recent.SequenceEqual(_catalog.Recent));
        _view.OpenAssetCatalog(); await Task.Delay(100);
        Check("expanded state preserved", window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "GalleryVariant_longv1"));
        Click(window, "GalleryCover_longset");
        Check("cover restores remembered variant", editor.CurrentVariant?.Id == "longv1");
        _view.OpenAssetCatalog(); await Task.Delay(80);
        var search = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "AssetSearchBox");
        search.Text = "Галерея 29"; search.Text = "Галерея 299"; await Task.Delay(400);
        Check("search newest result", editor.LibrarySets.Count == 1 && editor.LibrarySets[0].Name == "Галерея 299");
        var tool = editor.PendingPlacement;
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        Check("Escape preserves previous tool", !window.IsVisible && ReferenceEquals(tool, editor.PendingPlacement));
        _view.OpenAssetCatalog(); await Task.Delay(100);
        Check("search preserved on reopen", search.Text == "Галерея 299" && editor.LibrarySets.Count == 1);
        // While the chosen set is being organised a click only chooses: the catalog stays.
        var organization = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "LibraryOrganizationDetails");
        organization.IsExpanded = true; await Task.Delay(50);
        Click(window, "GalleryCover_gallerys299");
        Check("organising keeps the catalog open", window.IsVisible && editor.SelectedSetId == "gallerys299");
        organization.IsExpanded = false;
        search.Text = ""; await Task.Delay(350);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "LibraryGrid");
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        scroll.Offset = new Vector(0, scroll.Extent.Height); await Task.Delay(150);
        Check("scrolled gallery bounded and nonempty", _view.GalleryRealizedCards is > 0 and < 100);
        var lastCard = list.GetVisualDescendants().OfType<Button>().First(b => b.Name?.StartsWith("GalleryCover_gallerys") == true);
        var expectedId = lastCard.Name!["GalleryCover_".Length..];
        lastCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("scrolled card action matches image identity", editor.SelectedSetId == expectedId && !window.IsVisible);
        Capture(_view, "editor");
        await _catalog.FlushMetadataAsync(); Check("catalog metadata flushed", _catalog.MetadataSaveError is null);
        SaveProgress();
    }

    private static void Click(Control root, string name) => root.GetVisualDescendants().OfType<Button>()
        .Single(b => b.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private void Capture(Control root, string suffix)
    {
        root.UpdateLayout();
        using var image = new RenderTargetBitmap(new PixelSize((int)root.Bounds.Width, (int)root.Bounds.Height));
        image.Render(root);
        image.Save(Path.ChangeExtension(Options.OutputPath, suffix + ".png"), new PngBitmapEncoderOptions());
    }
    private void Check(string key, bool value)
    {
        _report.Checks[key] = value;
        if (!value) Console.WriteLine("CHECK FAILED: " + key);
    }
    private void Step(string step) { _report.Step = step; Console.WriteLine(step); SaveProgress(); }
    private static bool SameScene(MapDocument source, MapDocument actual)
    {
        var equal = source.Id == actual.Id && source.Canvas == actual.Canvas
            && source.Layers.Select(l => l.Id).SequenceEqual(actual.Layers.Select(l => l.Id));
        if (!equal) Console.WriteLine("Round-trip document header/layers differ");
        var objects = source.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        var reopened = actual.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        if (!objects.SequenceEqual(reopened))
        {
            equal = false;
            var first = Enumerable.Range(0, Math.Min(objects.Length, reopened.Length)).FirstOrDefault(i => objects[i] != reopened[i], -1);
            Console.WriteLine($"Round-trip objects differ: {objects.Length}/{reopened.Length}; first {first}");
            if (first >= 0)
            {
                Console.WriteLine(JsonSerializer.Serialize(objects[first]));
                Console.WriteLine(JsonSerializer.Serialize(reopened[first]));
            }
        }
        // Persistence may canonicalize the asset table; scene object order above stays exact.
        if (JsonSerializer.Serialize(source.Assets.OrderBy(a => a.Sha256)) != JsonSerializer.Serialize(actual.Assets.OrderBy(a => a.Sha256)))
        { equal = false; Console.WriteLine("Round-trip asset contents/metadata differ"); }
        return equal;
    }
    private void SaveProgress()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Options.OutputPath)!);
        lock (_reportGate) File.WriteAllText(Options.OutputPath, JsonSerializer.Serialize(_report,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    private static double P95(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.PositiveInfinity : sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
    }
    private static int Count(MapDocument document) => document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
    private static MapDocument Document(int size) => new(DocumentId.New(), "Placement scenario", DateTimeOffset.UtcNow,
        new CanvasDefinition(size, size, "px", CurrentLook.PageColor),
        [CurrentLook.Page("Фон"),
         new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
    private sealed class Recent : IRecentMaps
    {
        public IReadOnlyList<RecentMapItem> Load() => [];
        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc, byte[]? thumbnailPng = null, string mapType = "Локальная карта") { }
        public void Remove(string path) { }
    }
    private sealed class Picker(string path) : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path);
    }
    private sealed class Prompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
    }
    private sealed class Log : IAppLog
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) => Console.WriteLine(message + " " + exception?.Message);
    }
}
