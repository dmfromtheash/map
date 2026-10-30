using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

internal sealed class GenerationControlReport
{
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public List<string> Frames { get; } = [];
    public double[] Kernel1024Ms { get; set; } = [];
    public double[] Controlled1024Ms { get; set; } = [];
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 16 && Checks.Values.All(v => v) && Frames.Count >= 3;
}

internal static class NativeGenerationControlSmoke
{
    public static GenerationControlReport Run(PerformanceOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        var completion = new TaskCompletionSource<GenerationControlReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            GenerationControlApplication.Options = options; GenerationControlApplication.Completion = completion;
            try { AppBuilder.Configure<GenerationControlApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
    }
}

public sealed class GenerationControlApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<GenerationControlReport> Completion = null!;
    private readonly GenerationControlReport _report = new();
    private string _workspace = "";
    private EditorViewModel _editor = null!;
    private EditorView _view = null!;
    private Window _window = null!;
    private AssetCatalog? _catalog;

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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        Environment.SetEnvironmentVariable("MAP_UI_TEST_RECOVERY_ROOT", Path.Combine(_workspace, "recovery"));
        Environment.SetEnvironmentVariable("MAP_UI_TEST_SHORTCUT_SETTINGS_PATH", Path.Combine(_workspace, "shortcuts.json"));
        var document = HexMapFactory.Create("R20", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 128, 128, "#FFFFFFFF");
        _editor = new EditorViewModel(document, null, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new Recent(), new Picker(Path.Combine(_workspace, "smoke.mapproj")), new Prompts(), new Log(),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { });
        _window = new Window { Width = 1440, Height = 1000, Content = _view, Title = "Map R20 smoke",
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000) };
        desktop.MainWindow = _window;
        _window.Opened += async (_, _) =>
        {
            _window.Position = new PixelPoint(-32000, -32000);
            try { await Execute(); }
            catch (Exception ex) { _report.Failure = ex.ToString(); }
            finally
            {
                try
                {
                    _editor.CancelGeneration(); await _editor.PendingGeneration;
                    _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches(); _window.Close();
                    if (_catalog is not null) await _catalog.DisposeAsync();
                    Directory.Delete(_workspace, recursive: true);
                    Check("isolated-workspace-deleted", !Directory.Exists(_workspace));
                }
                catch (Exception ex) { _report.Failure ??= ex.ToString(); }
                Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        base.OnFrameworkInitializationCompleted();
    }

    private HexLayer Layer => _editor.Session.Document.Layers.OfType<HexLayer>().Single();

    private async Task Execute()
    {
        await Settle(); _editor.ActiveTool = EditorTool.Hex; _view.Canvas.FitToCanvas(); await Settle();
        Check("own-window-offscreen-not-activated", !_window.ShowActivated && _window.Position.X < -10000 && _window.Position.Y < -10000);
        _editor.HexGeneratorSettings = new() { Seed = 9 };
        var original = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
        Press("HexGenStepLand"); Check("start-background", _editor.GenerationBusy);
        Press("GenerationCancelButton"); await _editor.PendingGeneration;
        Check("cancel-document-unchanged", ReferenceEquals(original, _editor.Session.Document) && _editor.Session.UndoDepth == depth);
        Check("cancel-no-preview", !_editor.GenerationHasPreview);

        Press("HexGenStepLand"); await _editor.PendingGeneration;
        Check("preview-does-not-write-document", _editor.GenerationHasPreview && ReferenceEquals(original, _editor.Session.Document) && _editor.Session.UndoDepth == depth);
        await Shot("preview");
        Press("GenerationApplyButton"); await Settle();
        Check("apply-one-command", _editor.Session.UndoDepth == depth + 1 && Layer.GenerationRecipe?.Settings.Seed == 9);
        Check("generated-reasons", Layer.Board.Cells.All(p => p.Value.GenerationReason is { Length: > 0 }));
        await Shot("applied");
        Press("UndoButton"); await Settle();
        Check("one-undo-restores-map", Layer.Board.CellCount == 0 && Layer.GenerationRecipe is null && _editor.Session.UndoDepth == depth);

        // Real protection button and the native canvas input router edit the map while Busy.
        Press("HexGenStepLand");
        Press("GenerationProtectBrushButton");
        var h = Layer.Board.Layout.FromOffset(new(60, 60));
        var center = Layer.Board.Layout.Center(h); var point = _editor.Session.Viewport.WorldToScreen(center.X, center.Y);
        _view.Canvas.ClickForSmoke(point.X, point.Y);
        await _editor.PendingGeneration;
        Check("protection-brush-real-canvas", Layer.GenerationProtection.Contains(h));
        Check("conflict-shown", _editor.GenerationConflict);
        var changed = _editor.Session.Document; var changedDepth = _editor.Session.UndoDepth;
        Press("GenerationApplyButton");
        Check("conflict-refuses-apply", ReferenceEquals(changed, _editor.Session.Document) && _editor.Session.UndoDepth == changedDepth
            && _editor.StatusDetail.Contains("Пересчитать", StringComparison.Ordinal));
        await Shot("conflict");
        Press("GenerationRecalculateButton"); await _editor.PendingGeneration;
        Check("recalculate-clears-conflict", _editor.GenerationHasPreview && !_editor.GenerationConflict);
        Press("GenerationApplyButton"); await Settle();
        Check("protected-empty-hex-unchanged", Layer.Board[h] is null && Layer.GenerationProtection.Contains(h));
        Check("recalculated-apply-one-command", _editor.Session.UndoDepth == changedDepth + 1);
        Press("SaveButton"); await Until(() => File.Exists(Path.Combine(_workspace, "smoke.mapproj")) && !_editor.Session.IsDirty, "save");
        var reopened = MapProjectStore.Open(Path.Combine(_workspace, "smoke.mapproj")).Document.Layers.OfType<HexLayer>().Single();
        Check("native-save-reopen-v31", reopened.GenerationRecipe?.Settings.Seed == 9 && reopened.GenerationProtection.Contains(h)
            && reopened.Board.Cells.All(p => p.Value.GenerationReason is not null));
        Press("UndoButton"); await Settle();
        Check("recalculated-undo-restores-protected-source", Layer.Board.CellCount == 0 && Layer.GenerationProtection.Contains(h));
        await Shot("undo");
        await AreaScenario();
        await Task.Run(Measure1024);
    }

    private async Task AreaScenario()
    {
        // One of the owner's mountains, embedded only in the disposable smoke document.
        var mountain = OwnerMountainFixtures.Embedded(1);
        var sha = mountain.Sha256;
        var layer = new ObjectLayer(LayerId.New(), "Области", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "R20 area", DateTimeOffset.UnixEpoch, new(768, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer], [mountain]);
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea { LayoutVersion = 4, Density = 70, Seed = 5,
            Nodes = [new(Guid.NewGuid(), 40, 40), new(Guid.NewGuid(), 720, 40), new(Guid.NewGuid(), 720, 720), new(Guid.NewGuid(), 40, 720)],
            Variants = [new(sha, 512, 512)], Style = new(sha), Request = new() { BaseWidth = 40 } });
        document = new PlacementAreaCommand(layer.Id, area.Id, area, "Fixture").Apply(document);
        _view.Canvas.DisposeRenderCaches(); _view.CloseAuxiliaryWindowsForSmoke();
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _editor = new EditorViewModel(document, null, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery-area"))),
            new Recent(), new Picker(Path.Combine(_workspace, "area.mapproj")), new Prompts(), new Log(),
            assetCatalog: _catalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        // Decode the fixture before UI and native rendering request the existing image cache concurrently.
        Check("area-fixture-image-decoded", _editor.StampImages?.TryGetImage(sha) is not null);
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
        _editor.ActiveTool = EditorTool.Stamp; _editor.SelectLayer(layer.Id); _editor.SelectEditableArea(area.Id);
        await Settle(); _view.Canvas.FitToCanvas(); await Settle();
        var stored = _editor.SelectedEditableArea!;
        var member = stored.Members.First(); var item = document.Layers.OfType<ObjectLayer>().Single().Objects.Single(o => o.Id == member.ObjectId);
        Press("GenerationProtectBrushButton");
        _editor.SetAreaDensityBrushRadius(20);
        var itemX = item.X + item.Width / 2; var itemY = item.Y + item.Height / 2;
        var point = _editor.Session.Viewport.WorldToScreen(itemX, itemY); _view.Canvas.ClickForSmoke(point.X, point.Y);
        Check("area-protection-real-brush", _editor.SelectedEditableArea!.GenerationProtection?.Contains(member.Slot) == true
            && _editor.SelectedEditableArea.GenerationProtectionMask?.Factor(itemX, itemY) < .99);
        Press("GenerationProtectBrushStopButton");
        _editor.GenerationProtectionErase = true; Press("GenerationProtectBrushButton");
        _view.Canvas.ClickForSmoke(point.X, point.Y); Press("GenerationProtectBrushStopButton");
        Check("area-unprotect-clears-space-and-object", _editor.SelectedEditableArea!.GenerationProtection?.Contains(member.Slot) != true
            && (_editor.SelectedEditableArea.GenerationProtectionMask?.Factor(itemX, itemY) ?? 1) >= .99);
        _editor.GenerationProtectionErase = false; Press("GenerationProtectBrushButton");
        _view.Canvas.ClickForSmoke(point.X, point.Y); Press("GenerationProtectBrushStopButton");
        var source = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
        Press("GenerationAreaStartButton"); Press("GenerationCancelButton"); await _editor.PendingGeneration;
        Check("area-cancel-unchanged", ReferenceEquals(source, _editor.Session.Document) && _editor.Session.UndoDepth == depth);
        Press("GenerationAreaStartButton"); await _editor.PendingGeneration;
        Check("area-preview-readonly", _editor.GenerationHasPreview && ReferenceEquals(source, _editor.Session.Document));
        await Shot("area-preview");
        Press("GenerationApplyButton"); await Settle();
        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects;
        Check("area-protected-object-exact", objects.Single(o => o.Id == item.Id) == item);
        Check("area-apply-recipe-reasons-one-undo", _editor.SelectedEditableArea!.GenerationRecipe is not null
            && objects.Where(o => o.Id != item.Id).Any(o => o.GenerationReason is not null) && _editor.Session.UndoDepth == depth + 1);
        Press("SaveButton"); await Until(() => File.Exists(Path.Combine(_workspace, "area.mapproj")) && !_editor.Session.IsDirty, "area save");
        var reopened = MapProjectStore.Open(Path.Combine(_workspace, "area.mapproj")).Document.Layers.OfType<ObjectLayer>().Single();
        Check("area-save-reopen", reopened.PlacementAreas.Single().GenerationProtection?.Contains(member.Slot) == true
            && reopened.PlacementAreas.Single().GenerationRecipe is not null && reopened.Objects.Any(o => o.GenerationReason is not null)
            && reopened.PlacementAreas.Single().GenerationProtectionMask?.Factor(itemX, itemY) < .99);
        Press("UndoButton"); await Settle();
        Check("area-one-undo-restores-source", _editor.Session.Document.Layers.SequenceEqual(source.Layers));
    }

    private void Measure1024()
    {
        // Alternating kernel/envelope pairs, identical snapshot + seed; no UI or I/O in the timer.
        // This measures R20 overhead, not a claim of speedup over another commit.
        var layer = new HexLayer(LayerId.New(), "1024", true, false, 1, HexBoard.Create(HexOrientation.Flat, 16, 1024, 1024));
        var recipe = new HexGenerationRecipe { Settings = new() { Seed = 9 }, Steps = [HexGenerationStep.Land] };
        var kernel = new List<double>(); var controlled = new List<double>();
        for (var pair = 0; pair < 3; pair++)
        {
            foreach (var envelope in pair % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                var watch = Stopwatch.StartNew();
                var after = envelope ? ControlledHexGeneration.Run(layer, recipe)
                    : HexWorldGenerator.Run(layer, HexGenerationStep.Land, recipe.Settings);
                watch.Stop(); (envelope ? controlled : kernel).Add(watch.Elapsed.TotalMilliseconds);
                Check("1024-" + pair + "-" + envelope, after.Board.CellCount == 1024 * 1024 && layer.Board.CellCount == 0);
                Console.WriteLine($"1024 land envelope={envelope}: {watch.Elapsed.TotalMilliseconds:0} ms");
            }
        }
        _report.Kernel1024Ms = [.. kernel]; _report.Controlled1024Ms = [.. controlled];
        _report.Notes["D5"] = "1024² Land; 3 alternating kernel vs R20-envelope pairs; same seed/snapshot, median in report; no cross-commit speed claim. Other steps tested at 32×24.";
    }

    private void Press(string name)
    {
        var target = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_view).OfType<Control>().FirstOrDefault(c => c.Name == name);
        if (target is not null)
        {
            foreach (var ancestor in Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(target).OfType<Control>().Reverse())
            {
                if (ancestor.Tag is string tag && tag.StartsWith("HexReveal:", StringComparison.Ordinal))
                    Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_view).OfType<Button>()
                        .FirstOrDefault(b => b.Name == tag["HexReveal:".Length..])?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (ancestor is Expander expander) expander.IsExpanded = true;
            }
            _view.UpdateLayout();
        }
        NativeEditorScenarioApplication.RevealTab(_view, name);
        var button = _view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible)
            ?? throw new InvalidOperationException("Control not visible: " + name);
        if (!button.IsEnabled) throw new InvalidOperationException("Control disabled: " + name + " " + _editor.StatusDetail);
        button.BringIntoView(); _view.UpdateLayout();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private async Task Shot(string name)
    {
        await Settle();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long revision = long.MaxValue;
        void Frame(object? sender, EventArgs args)
        {
            if (ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.RenderDocument)
                && _view.Canvas.CompletedSceneRevision >= revision) done.TrySetResult();
        }
        _view.Canvas.FrameRendered += Frame; revision = _view.Canvas.RequestFrameForSmoke();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { _view.Canvas.FrameRendered -= Frame; }
        using var bitmap = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
        File.WriteAllBytes(path, png.ToArray()); _report.Frames.Add(path);
        Check("frame-" + name + "-native", _view.Canvas.LastFrameHadGpuContext && _view.Canvas.LastRenderStats.MissingAssets == 0);
        using var panel = new RenderTargetBitmap(new PixelSize((int)_view.Bounds.Width, (int)_view.Bounds.Height));
        panel.Render(_view); var panelPath = Path.ChangeExtension(Options.OutputPath, "." + name + ".panel.png");
        panel.Save(panelPath, PngBitmapEncoderOptions.Default); _report.Frames.Add(panelPath);
    }

    private void Check(string key, bool value) { _report.Checks[key] = value; Console.WriteLine((value ? "ok " : "FAIL ") + key); }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(60); }
    private static async Task Until(Func<bool> condition, string label)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(label); await Task.Delay(60); } }
    private sealed class Picker(string path) : IFilePicker
    { public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path); public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path); }
    private sealed class Recent : IRecentMaps
    { public IReadOnlyList<RecentMapItem> Load() => []; public void Remove(string path) { }
        public void Touch(string path, Guid id, string title, DateTimeOffset date, byte[]? thumbnailPng = null, string mapType = "Локальная карта") { } }
    private sealed class Log : IAppLog
    { public void Info(string message) { } public void Error(string message, Exception? exception = null) => Console.WriteLine(message + exception?.Message); }
    private sealed class Prompts : IUserPrompts
    {
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string title) => Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string name) => Task.FromResult(false);
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string title, bool fileAlreadyMissing = false) => Task.FromResult(false);
    }
}
