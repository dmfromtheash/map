using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

internal sealed class NativeStage19Report
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false; rail button, panel buttons/boxes and the production canvas router drive the river tool. File pickers and reopen use local adapters. No system input. The screenshot is an Avalonia frame, not OS scan-out.";
    public Dictionary<string, bool> Checks { get; } = [];
    public int MainNodes { get; set; }
    public int Channels { get; set; }
    public double[] MainWidths { get; set; } = [];
    public string? JunctionBefore { get; set; }
    public string? JunctionAfter { get; set; }
    public string? Screenshot { get; set; }
    public string? SavedProject { get; set; }
    public string? Export { get; set; }
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 10 && Checks.Values.All(v => v);
}

internal static class NativeStage19Smoke
{
    public static NativeStage19Report Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeStage19Report>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Stage19Application.Options = options; Stage19Application.Completion = completion;
            try { AppBuilder.Configure<Stage19Application>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
    }
}

public sealed class Stage19Application : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeStage19Report> Completion = null!;
    private readonly NativeStage19Report _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private readonly LocalPrompts _prompts = new();

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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "stage19-native");
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — Stage 19", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        var document = new MapDocument(DocumentId.New(), "Река с притоком", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFEDE3CD"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD"),
                LandBlock(), new ObjectLayer(LayerId.New(), "Реки", true, false, 1, [])]);
        OpenEditor(document);
        _window.Opened += async (_, _) =>
        {
            try { await Execute(); }
            catch (Exception ex)
            {
                _report.Failure = ex.ToString();
                _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".failure.png");
                SaveVisual(_report.Screenshot);
            }
            finally
            {
                _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
                _report.Checks["resources-disposed"] = _view.Canvas.ObjectCacheCount == 0 && _view.Canvas.TileCacheCount == 0;
                _window.Close(); Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A land mass covering the upper-left 768×512 of the map, so a river can leave it into the sea below.</summary>
    private static LandLayer LandBlock()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        for (var x = 0; x < 3; x++) for (var y = 0; y < 2; y++) tiles = tiles.With(new TileKey(x, y), coverage);
        return new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles, new LandEdgeStyle());
    }

    private void OpenEditor(MapDocument document, string? path = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "river.mapproj")), _prompts, new LocalLog(),
            exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "river-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    private async Task Execute()
    {
        await Settle();
        await Click("RiverToolButton");
        if (Environment.GetEnvironmentVariable("MAP_STAGE19_LAKE_ONLY") == "1") { await LakeOnly(); return; }
        Require(_editor.ActiveTool == EditorTool.River, "Rail button did not activate the river tool");
        await Number("NewRiverWidthBox", "12");
        await Click("StartRiverButton");
        var before = _editor.Session.Document;
        Point(120, 220); Point(380, 320); Point(640, 280); Point(900, 600);
        _report.Checks["draft-outside-document-history"] = ReferenceEquals(before, _editor.Session.Document) && _editor.Session.UndoDepth == 0;
        await Click("FinishRiverButton");
        Require(_editor.SelectedRiver is { Channels.Length: 1 }, "Native controls did not create the river: " + _editor.StatusDetail);
        var main = _editor.SelectedRiver!.Channels[0];
        _report.MainNodes = main.Nodes.Length;
        _report.Checks["create-four-nodes"] = main.Nodes.Length == 4 && _editor.Session.UndoDepth == 1;

        // Two different widths along one channel, from the panel, without touching a contour.
        await Number("RiverSourceWidthBox", "6");
        await Number("RiverMouthWidthBox", "34");
        var profiled = _editor.SelectedRiver!.Channels[0];
        _report.MainWidths = [.. profiled.Nodes.Select(n => n.Width)];
        _report.Checks["width-profile-from-panel"] = profiled.Nodes[0].Width == 6 && profiled.Nodes[^1].Width == 34
            && profiled.Nodes[1].Width < profiled.Nodes[2].Width;
        Point(900, 600); await Settle();
        await Number("RiverNodeWidthBox", "40");
        _report.Checks["node-width-from-panel"] = _editor.SelectedRiver!.Channels[0].Nodes[^1].Width == 40;

        // A tributary: two clicks, then a click on the main channel attaches it there.
        await Click("StartRiverButton");
        Point(520, 90); Point(600, 190);
        var geometry = RiverGeometry.Evaluate(_editor.Rivers[0]).Channels[main.Id];
        var target = RiverGeometry.PointAt(geometry, 1, 0.55);
        Point(target.X, target.Y); await Settle();
        Require(_editor.Rivers.Count == 1 && _editor.Rivers[0].Channels.Length == 2, "Tributary was not attached: " + _editor.StatusDetail);
        var tributary = _editor.Rivers[0].Channels[1];
        _report.Channels = 2;
        _report.JunctionBefore = $"{tributary.Mouth!.ParentNodeId}:{tributary.Mouth.Fraction:0.###}";
        _report.Checks["tributary-attached-by-click"] = tributary.Mouth.ParentChannelId == main.Id && tributary.Mouth.ParentNodeId == main.Nodes[1].Id;
        await Frame();
        var attached = _editor.Session.Document;

        // Move the mouth diamond along the parent onto its next segment.
        var river = _editor.Rivers[0];
        var mouth = RiverGeometry.Evaluate(river).Channels[tributary.Id].End;
        var spot = RiverGeometry.PointAt(RiverGeometry.Evaluate(river).Channels[main.Id], 2, 0.4);
        Drag(mouth.X, mouth.Y, spot.X, spot.Y); await Frame();
        var movedMouth = _editor.Rivers[0].FindChannel(tributary.Id)!.Mouth!;
        _report.JunctionAfter = $"{movedMouth.ParentNodeId}:{movedMouth.Fraction:0.###}";
        _report.Checks["mouth-moved-by-drag"] = movedMouth.ParentNodeId == main.Nodes[2].Id && _editor.Session.UndoDepth == 6;
        _report.Checks["parent-nodes-untouched-by-mouth-move"] = _editor.Rivers[0].FindChannel(main.Id)!.Nodes
            .Select(n => (n.Id, n.X, n.Y)).SequenceEqual(river.FindChannel(main.Id)!.Nodes.Select(n => (n.Id, n.X, n.Y)));
        var moved = _editor.Session.Document;
        _editor.Undo(); var undone = ReferenceEquals(attached, _editor.Session.Document);
        _editor.Redo(); _report.Checks["undo-redo-exact"] = undone && ReferenceEquals(moved, _editor.Session.Document);

        // The junction pixels on the real canvas are water, not background, on both sides of the mouth.
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png");
        SaveVisual(_report.Screenshot);
        _report.Checks["junction-water-on-canvas"] = JunctionIsWater(_report.Screenshot);

        await Click("SaveButton");
        await Until(() => File.Exists(Path.Combine(_workspace, "river.mapproj")) && !_editor.Session.IsDirty);
        _report.SavedProject = Path.Combine(_workspace, "river.mapproj");
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var reopened = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).Single();
        _report.Checks["save-open-keeps-junction-and-widths"] = reopened.Channels.Length == 2
            && reopened.FindChannel(tributary.Id)!.Mouth == movedMouth
            && reopened.FindChannel(main.Id)!.Nodes[^1].Width == 40;
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches(); OpenEditor(opened, _report.SavedProject);
        await Settle();
        _editor.SelectRiver(reopened.Id, tributary.Id); await Settle();
        await Number("RiverMouthWidthBox", "16");
        _report.Checks["reopened-river-editable"] = _editor.Rivers[0].FindChannel(tributary.Id)!.Nodes[^1].Width == 16;
        await Click("SaveButton"); await Until(() => !_editor.Session.IsDirty);

        await Click("ExportButton");
        await Until(() => _editor.CanStartExport);
        var exportWindow = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.Single(w => w.Name == "ExportWorkflowWindow");
        var exportButton = exportWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StartExportButton");
        exportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => _editor.ExportResultPath is not null);
        _report.Export = Path.Combine(_workspace, "river-export.png");
        using (var bitmap = SKBitmap.Decode(_report.Export))
        {
            var onLand = RiverGeometry.PointAt(RiverGeometry.Evaluate(_editor.Rivers[0]).Channels[main.Id], 1, 0.5);
            var pixel = bitmap.GetPixel((int)onLand.X, (int)onLand.Y);
            var sea = bitmap.GetPixel(1000, 740);
            // The main channel ends at (900,600), below the land block: clipped to land, its end is sea.
            var beyondCoast = bitmap.GetPixel(900, 600);
            _report.Checks["export-full-size-with-river"] = bitmap is { Width: 1024, Height: 768 } && pixel != sea && pixel.Blue > pixel.Red;
            _report.Checks["river-stops-at-the-coast"] = beyondCoast == sea;
        }
        _view.CloseAuxiliaryWindowsForSmoke();

        // A lake from clicks, a tributary that ends on its shore, a distributary that leaves the main
        // channel, a split in the middle and the flow-based widths: the whole water system in one window.
        await Click("StartLakeButton");
        Point(150, 380); Point(250, 350); Point(310, 420); Point(240, 490); Point(140, 470);
        await Click("FinishRiverButton");
        Require(_editor.Rivers.Any(r => r.Channels.Any(c => c.Closed)), "Lake was not created: " + _editor.StatusDetail);
        _report.Checks["lake-created-by-clicks"] = _editor.Rivers.Single(r => r.Channels.Any(c => c.Closed)).Channels.Single().Nodes.Length == 5;
        await Click("StartRiverButton");
        Point(40, 300); Point(90, 340);
        var lakeRiver = _editor.Rivers.Single(r => r.Channels.Any(c => c.Closed));
        var lakeGeometry = RiverGeometry.Evaluate(lakeRiver).Channels[lakeRiver.Channels[0].Id];
        var shore = RiverGeometry.PointAt(lakeGeometry, 4, 0.5);
        Point(shore.X, shore.Y); await Settle();
        var lakeSystem = _editor.Rivers.Single(r => r.Channels.Any(c => c.Closed));
        _report.Checks["inflow-joins-lake-shore"] = lakeSystem.Channels.Length == 2 && lakeSystem.Channels[1].Mouth?.ParentChannelId == lakeSystem.Channels[0].Id;
        await Click("StartRiverButton");
        var mainRiver = _editor.Rivers.Single(r => r.Id == river.Id);
        var branchFrom = RiverGeometry.PointAt(RiverGeometry.Evaluate(mainRiver).Channels[main.Id], 1, 0.5);
        Point(branchFrom.X, branchFrom.Y); Point(branchFrom.X + 50, branchFrom.Y + 110); Point(branchFrom.X + 40, branchFrom.Y + 190);
        await Click("FinishRiverButton");
        var withBranch = _editor.Rivers.Single(r => r.Id == river.Id);
        _report.Checks["distributary-leaves-main-channel"] = withBranch.Channels.Length == 3
            && withBranch.Channels[2].Source?.ParentChannelId == main.Id && withBranch.Channels[2].Mouth is null;
        _editor.SelectRiver(river.Id, main.Id); _editor.SelectRiverNode(2); await Settle();
        await Click("SplitRiverChannelButton");
        _report.Checks["split-channel-in-node"] = _editor.Rivers.Single(r => r.Id == river.Id).Channels.Length == 4
            && _editor.Rivers.Single(r => r.Id == river.Id).FindChannel(main.Id)!.Mouth is not null;
        _editor.SelectRiver(river.Id, main.Id); await Settle();
        await Number("RiverFlowSourceWidthBox", "5"); await Number("RiverFlowGrowthBox", "10");
        await Click("ApplyRiverFlowWidthButton");
        _report.Checks["width-by-flow"] = _editor.Rivers.Single(r => r.Id == river.Id).FindChannel(main.Id)!.Nodes[0].Width == 5;
        await Number("RiverEdgeRoughnessBox", "35");
        _report.Checks["edge-roughness"] = Math.Abs(_editor.Rivers.Single(r => r.Id == river.Id).Style.EdgeRoughness - 0.35) < 1e-9;
        await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".system.png"));
        await Click("SaveButton"); await Until(() => !_editor.Session.IsDirty);

        // Far zoom keeps the river as a visible thread.
        _editor.Session.Viewport.Set(0, 0, .04); await Frame();
        var far = Path.ChangeExtension(Options.OutputPath, ".far.png"); SaveVisual(far);
        _report.Checks["far-zoom-keeps-river"] = _view.Canvas.LastFrameObjectsDrawn > 0;
        _editor.Session.Viewport.Set(0, 0, 1); await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".reopened.png"));
    }

    /// <summary>Reproduces the owner's report: the lake button as the very first action.</summary>
    private async Task LakeOnly()
    {
        await Click("StartLakeButton");
        _report.Checks["lake-mode-armed"] = _editor.IsCreatingLake;
        Point(200, 200); await Settle();
        _report.Checks["first-vertex-makes-draft"] = _editor.DisplayedRiver is not null;
        Point(400, 180); Point(460, 320); Point(320, 420); Point(180, 360); await Frame();
        _report.MainNodes = _editor.DisplayedRiver?.Channels[0].Nodes.Length ?? 0;
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".lake-draft.png"));
        await Click("FinishRiverButton");
        _report.Checks["lake-committed"] = _editor.Rivers.Count == 1 && _editor.Rivers[0].Channels[0].Closed;
        _report.Channels = _editor.Rivers.Count;
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".lake.png");
        SaveVisual(_report.Screenshot);
        // Is the lake actually painted on the canvas?
        var canvasPath = Path.ChangeExtension(_report.Screenshot, ".canvas.png");
        using (var canvas = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_view.Canvas.Bounds.Width), (int)Math.Ceiling(_view.Canvas.Bounds.Height))))
        { canvas.Render(_view.Canvas); using var file = File.Create(canvasPath); canvas.Save(file, PngBitmapEncoderOptions.Default); }
        using (var bitmap = SKBitmap.Decode(canvasPath))
        {
            var viewport = _editor.Session.Viewport;
            var middle = viewport.WorldToScreen(320, 290);
            var pixel = bitmap.GetPixel((int)middle.X, (int)middle.Y);
            _report.JunctionAfter = $"lake pixel {pixel}";
            _report.Checks["lake-visible-on-canvas"] = pixel.Blue > pixel.Red;
        }
    }

    private bool JunctionIsWater(string screenshotPath)
    {
        var river = _editor.Rivers[0]; var set = RiverGeometry.Evaluate(river);
        var tributary = river.Channels[1];
        var end = set.Channels[tributary.Id].End;
        var canvasPath = Path.ChangeExtension(screenshotPath, ".canvas.png");
        using var canvas = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_view.Canvas.Bounds.Width), (int)Math.Ceiling(_view.Canvas.Bounds.Height)));
        canvas.Render(_view.Canvas);
        using (var file = File.Create(canvasPath)) canvas.Save(file, PngBitmapEncoderOptions.Default);
        using var bitmap = SKBitmap.Decode(canvasPath);
        var viewport = _editor.Session.Viewport;
        // Sample beside the centreline (the editor draws a thin guide and a mouth diamond on it):
        // a quarter width into the water, upstream of the mouth on the tributary and downstream on the parent.
        var parent = set.Channels[river.Channels[0].Id];
        var nearest = parent.Samples.MinBy(p => Math.Abs(p.X - end.X) + Math.Abs(p.Y - end.Y));
        var downstream = parent.Samples.FirstOrDefault(s => s.Distance > nearest.Distance + 12, parent.Samples[^1]);
        var upstream = set.Channels[tributary.Id].Samples[^8];
        SKColor At(RiverSample s)
        {
            var p = viewport.WorldToScreen(s.X - s.TangentY * s.HalfWidth * 0.5, s.Y + s.TangentX * s.HalfWidth * 0.5);
            return bitmap.GetPixel((int)p.X, (int)p.Y);
        }
        var page = bitmap.GetPixel((int)viewport.WorldToScreen(30, 700).X, (int)viewport.WorldToScreen(30, 700).Y);
        static bool Water(SKColor c, SKColor page) => c != page && c.Blue > c.Red;
        return Water(At(downstream), page) && Water(At(upstream), page);
    }

    private async Task Frame()
    {
        var completion = new TaskCompletionSource();
        void Rendered(object? sender, EventArgs args)
        { if (ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document)) completion.TrySetResult(); }
        _view.Canvas.FrameRendered += Rendered; _view.Canvas.InvalidateVisual();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { _view.Canvas.FrameRendered -= Rendered; }
    }
    private void Point(double x, double y)
    { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.ClickForSmoke(p.X, p.Y); }
    private void Drag(double x, double y, double toX, double toY)
    { var a = _editor.Session.Viewport.WorldToScreen(x, y); var b = _editor.Session.Viewport.WorldToScreen(toX, toY);
        _view.Canvas.PressMoveReleaseForSmoke(a.X, a.Y, b.X, b.Y); }
    private T Find<T>(string name) where T : Control => _view.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
    private async Task Click(string name)
    { var button = Find<Button>(name); button.BringIntoView(); await Settle(); Require(button.IsEffectivelyVisible && button.IsEnabled, name + " is unavailable");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(); }
    private async Task Number(string name, string value)
    { var box = Find<TextBox>(name); box.BringIntoView(); await Settle(); box.Text = value;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); await Settle(); }
    private void SaveVisual(string path)
    { using var bitmap = new RenderTargetBitmap(new PixelSize(1440, 900)); bitmap.Render(_view);
        using var file = File.Create(path); bitmap.Save(file, PngBitmapEncoderOptions.Default); }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(50); }
    private static async Task Until(Func<bool> condition)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException(); await Task.Delay(50); } }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
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
