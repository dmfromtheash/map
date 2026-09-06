using System.Diagnostics;
using System.Security.Cryptography;
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

internal sealed class NativeStage17Report
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false; scoped control events and production canvas router. File pickers and map reopen use local adapters. No system input. Timing ends at a rendered native canvas frame, not OS scan-out.";
    public Dictionary<string, bool> Checks { get; } = [];
    public int FixtureObjects { get; set; }
    public bool MeasurementPerformed { get; set; }
    public int ChangedObjects { get; set; }
    public int RegeneratedSegments { get; set; }
    public double EditHandlerMs { get; set; }
    public double EditToFrameMs { get; set; }
    public long EditAllocatedBytes { get; set; }
    public long HistoryRetainedBytes { get; set; }
    public int HistoryDepth { get; set; }
    public string? Screenshot { get; set; }
    public string? SavedProject { get; set; }
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 10 && Checks.Values.All(v => v);
}

internal static class NativeStage17Smoke
{
    public static NativeStage17Report Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeStage17Report>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Stage17Application.Options = options; Stage17Application.Completion = completion;
            try { AppBuilder.Configure<Stage17Application>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
    }
}

public sealed class Stage17Application : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeStage17Report> Completion = null!;
    private readonly NativeStage17Report _report = new();
    private string _workspace = "";
    private AssetCatalog _catalog = null!;
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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "stage17-native");
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _catalog.ImportSet([Path.Combine(Environment.CurrentDirectory, "src/Map.Assets/StarterAssets/sample-tree.png")],
            "Деревья", new(), DateTimeOffset.UnixEpoch);
        _window = new Window { Width = 1440, Height = 900, Title = "Map — Stage 17", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        var document = new MapDocument(DocumentId.New(), "Редактируемая лесная тропа", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFF2EAD5"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFF2EAD5"),
                new ObjectLayer(LayerId.New(), "Деревья", true, false, 1, [])]);
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
                await _catalog.FlushMetadataAsync(); _window.Close(); Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }
    private void OpenEditor(MapDocument document, string? path = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "editable-path.mapproj")), _prompts, new LocalLog(),
            assetCatalog: _catalog, exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "path-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }
    private async Task Execute()
    {
        await Settle(); _editor.ChooseGalleryVariant(_catalog.Sets[0].Id, null); await Settle();
        if (Environment.GetEnvironmentVariable("MAP_STAGE17_PREVIEW_SMOKE") == "1")
        { await CheckLongPathPreview(); return; }
        await Click("StampModePath");
        await Click("StartEditablePathButton");
        var before = _editor.Session.Document;
        Point(150, 200); Point(450, 260); Point(780, 200);
        _report.Checks["draft-outside-document-history"] = ReferenceEquals(before, _editor.Session.Document) && _editor.Session.UndoDepth == 0;
        await Click("FinishEditablePathButton");
        Require(_editor.SelectedEditablePath is { Nodes.Length: 3 }, "Native controls did not create path: " + _editor.StatusDetail);
        _report.Checks["create-three-nodes"] = true;
        Drag(450, 260, 450, 320); await Settle();
        _report.Checks["node-drag-one-undo"] = _editor.SelectedEditablePath!.Nodes[1].Y == 320 && _editor.Session.UndoDepth == 2;
        await Number("EditablePathSpacingBox", "68");
        _report.Checks["spacing-control"] = _editor.SelectedEditablePath!.Spacing == 68;
        var path = _editor.SelectedEditablePath!; var member = path.Members[^1];
        var stamp = DocumentEditor.FindObject(_editor.Session.Document, member.ObjectId)!;
        await Click("SelectPathObjectButton");
        var pickX = stamp.X + stamp.Width / 2; var pickY = stamp.Y + stamp.Height / 2;
        Point(pickX, pickY); await Settle();
        Require(_editor.Session.SelectedObjectId == member.ObjectId, "Native pointer did not select the path stamp");
        await Click("DetachPathObjectButton");
        var detached = DocumentEditor.FindObject(_editor.Session.Document, member.ObjectId)!;
        await Number("EditablePathSeedBox", "70217");
        _report.Checks["detach-survives-recalculation"] = ReferenceEquals(detached,
            DocumentEditor.FindObject(_editor.Session.Document, member.ObjectId))
            && _editor.SelectedEditablePath!.Exclusions.Contains(member.Slot);
        var after = _editor.Session.Document; _editor.Undo(); _editor.Redo();
        _report.Checks["undo-redo-exact"] = ReferenceEquals(after, _editor.Session.Document);
        await Click("SaveButton");
        await Until(() => File.Exists(Path.Combine(_workspace, "editable-path.mapproj")) && !_editor.Session.IsDirty);
        _report.SavedProject = Path.Combine(_workspace, "editable-path.mapproj");
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var reopenedPath = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementPaths).Single();
        _report.Checks["save-open-links"] = reopenedPath.Exclusions.Contains(member.Slot) && reopenedPath.Seed == 70217
            && DocumentEditor.FindObject(opened, member.ObjectId) == detached;
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches(); OpenEditor(opened, _report.SavedProject);
        _editor.SelectEditablePath(reopenedPath.Id); await Settle();
        await Number("EditablePathSpacingBox", "72");
        _report.Checks["reopened-path-editable"] = _editor.SelectedEditablePath!.Spacing == 72;
        var scaleControl = Find<Slider>("FutureStampScaleSlider");
        scaleControl.BringIntoView(); await Settle();
        Require(scaleControl.IsEffectivelyVisible && scaleControl.IsEnabled, "Saved path scale is unavailable without a selected catalog set");
        scaleControl.Value = 110; await Settle();
        await Click("ApplyEditablePathSettingsButton");
        _report.Checks["reopened-path-appearance-controls"] = _editor.SelectedEditablePath!.ScalePercent == 110;
        Require(Find<Button>("StampModePath").IsEffectivelyVisible, "Saved path randomization controls are hidden");
        await Click("SaveButton"); await Until(() => !_editor.Session.IsDirty);
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png");
        Find<Control>("EditablePathSummary").BringIntoView(); await Settle(); SaveVisual(_report.Screenshot);
        await Click("ExportButton");
        await Until(() => _editor.CanStartExport);
        var exportWindow = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.Single(w => w.Name == "ExportWorkflowWindow");
        var exportButton = exportWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StartExportButton");
        exportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => _editor.ExportResultPath is not null);
        using (var bitmap = SKBitmap.Decode(Path.Combine(_workspace, "path-export.png")))
            _report.Checks["native-export-control-full-size"] = bitmap is { Width: 1024, Height: 768 };
        _view.CloseAuxiliaryWindowsForSmoke();
        _editor.ChooseGalleryVariant(_catalog.Sets[0].Id, null, armPlacement: false);
        var pathBeforeModeSwitch = _editor.SelectedEditablePath!;
        var countBeforeModeSwitch = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        await Click("StampModeOne");
        Point(pathBeforeModeSwitch.Nodes[0].X, pathBeforeModeSwitch.Nodes[0].Y); await Settle();
        _report.Checks["switch-to-one-releases-path-nodes"] = _editor.SelectedEditablePath is null
            && _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == countBeforeModeSwitch + 1;
        if (Environment.GetEnvironmentVariable("MAP_STAGE17_UI_ONLY") != "1") await MeasureExistingMap();
    }
    private async Task CheckLongPathPreview()
    {
        var scale = Find<Slider>("FutureStampScaleSlider");
        scale.BringIntoView(); await Settle(); scale.Value = 25; await Settle();
        await Click("StampModePath"); await Click("StartEditablePathButton");
        var before = _editor.Session.Document;
        for (var i = 0; i < 10; i++) { Point(i % 2 == 0 ? 100 : 900, 100 + i * 60); await Settle(); }
        var draft = _editor.DisplayedEditablePath!;
        var samples = _editor.PlacementPreviewObjects.ToArray();
        var total = PlacementPathPlanner.Count(draft);
        var segment = draft.Nodes.Length - 2;
        var lastId = PlacementPathPlanner.ObjectIdFor(draft.Id,
            new(draft.Nodes[segment].Id, PlacementPathPlanner.SegmentCount(draft, segment) - 1));
        _report.Checks["long-draft-ten-nodes"] = draft.Nodes.Length == 10;
        _report.Checks["preview-bounded"] = samples.Length == 64 && total > 64;
        _report.Checks["preview-reaches-new-end"] = samples.Any(o => o.Id == lastId);
        _report.Checks["draft-outside-document-history"] = ReferenceEquals(before, _editor.Session.Document) && _editor.Session.UndoDepth == 0;
        _report.Checks["draft-summary-reports-full-count"] = _editor.EditablePathSummary.Contains($"64 из {total}");
        Find<Control>("EditablePathSummary").BringIntoView(); await Settle();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".draft.png"));
        await Click("FinishEditablePathButton");
        _report.Checks["finish-materializes-entire-path"] = _editor.SelectedEditablePath?.Members.Length == total;
        _report.Checks["preview-uses-concrete-stamps"] = samples.All(o => o == DocumentEditor.FindObject(_editor.Session.Document, o.Id));
        var finished = _editor.Session.Document; _editor.Undo(); var undone = ReferenceEquals(before, _editor.Session.Document);
        _editor.Redo(); _report.Checks["long-path-undo-redo-exact"] = undone && ReferenceEquals(finished, _editor.Session.Document);
        for (var i = 0; i < 8; i++)
        {
            await Click("StartEditablePathButton"); Point(100, 80 + i * 75); Point(900, 80 + i * 75);
            await Click("FinishEditablePathButton");
        }
        _report.Checks["eight-further-paths-created"] = _editor.EditablePaths.Count == 9 && _editor.Session.UndoDepth == 9;
        _report.Checks["original-path-preserved"] = _editor.EditablePaths.Any(p => p.Id == draft.Id && p.Members.Length == total);
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png");
        SaveVisual(_report.Screenshot);
    }

    private async Task MeasureExistingMap()
    {
        var fixture = Environment.GetEnvironmentVariable("MAP_STAGE17_FIXTURE")
            ?? throw new InvalidOperationException("MAP_STAGE17_FIXTURE must name the existing ordinary map.");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(fixture));
        var document = await Task.Run(() => MapProjectStore.Open(fixture).Document);
        _report.FixtureObjects = document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        _report.MeasurementPerformed = true;
        Require(_report.FixtureObjects is >= 19000 and <= 21000, "Fixture is not the requested ordinary map");
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document);
        _editor.ChooseGalleryVariant(_catalog.Sets[0].Id, null); _editor.Session.Viewport.Set(0, 0, .35); await Settle();
        await Click("StampModePath");
        await Click("StartEditablePathButton"); Point(300, 500); Point(1000, 650); Point(1800, 500);
        await Click("FinishEditablePathButton"); await Frame();
        var before = _editor.Session.Document;
        var beforeIndex = DocumentRevisionIndex.For(before);
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var heap = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew(); Drag(1000, 650, 1000, 810); _report.EditHandlerMs = watch.Elapsed.TotalMilliseconds;
        _report.EditAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        await Frame(); _report.EditToFrameMs = watch.Elapsed.TotalMilliseconds;
        _report.HistoryRetainedBytes = GC.GetTotalMemory(forceFullCollection: true) - heap;
        _report.ChangedObjects = _editor.LastPathChangedObjects; _report.RegeneratedSegments = _editor.LastPathRegeneratedSegments;
        _report.HistoryDepth = _editor.Session.UndoDepth;
        _report.Checks["ordinary-map-unrelated-object-identities"] = document.Layers.OfType<ObjectLayer>()
            .SelectMany(l => l.Objects).All(o => ReferenceEquals(o, DocumentEditor.FindObject(_editor.Session.Document, o.Id)));
        var edited = _editor.Session.Document; _editor.Undo(); var exactUndo = ReferenceEquals(before, _editor.Session.Document);
        _editor.Redo(); _report.Checks["ordinary-map-history-exact"] = exactUndo && ReferenceEquals(edited, _editor.Session.Document);
        _report.Checks["ordinary-map-local-edit"] = _report.RegeneratedSegments == 2 && _report.ChangedObjects < 100
            && _report.EditHandlerMs < 250 && _report.EditToFrameMs < 1000;
        _report.Checks["fixture-read-only"] = sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture)));
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
