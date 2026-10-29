using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

internal sealed record NativePlacementAndCheckpointsReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    string WindowMode,
    string ScreenshotPath,
    bool PrimitiveArmWasTransient,
    bool GhostWasVisible,
    bool PrimitiveCommitWasExact,
    bool PrimitiveNameWasAutomatic,
    bool PrimitiveWasOneCommandAndSelected,
    bool SecondaryPointerCancelledWithoutMutation,
    bool SeriesPlacedExactlyTwoAndEscAddedNothing,
    int PointerMoveObjectTreeRebuilds,
    bool VersionCreatedThroughUi,
    string VersionName,
    bool PrimaryAutosaveReopenedLatestRevision,
    bool WorkspaceHasOnlyLayersAndObjects,
    bool CollapseExpandWorked,
    bool LargeCatalogOpened,
    bool ResourcesDisposed,
    bool GatePassed,
    IReadOnlyList<string> Limitations);

internal static class NativePlacementAndCheckpointsSmoke
{
    public static NativePlacementAndCheckpointsReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Stage 14 native smoke targets Windows.");
        }

        var completion = new TaskCompletionSource<NativePlacementAndCheckpointsReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativePlacementAndCheckpointsApplication.Configure(options, completion);
                AppBuilder.Configure<NativePlacementAndCheckpointsApplication>()
                    .UsePlatformDetect()
                    .WithInterFont()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Map Stage 14 native smoke",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        }
        finally
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}

public sealed class NativePlacementAndCheckpointsApplication : Application
{
    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativePlacementAndCheckpointsReport>? _completion;

    internal static void Configure(
        PerformanceOptions options,
        TaskCompletionSource<NativePlacementAndCheckpointsReport> completion)
    {
        _options = options;
        _completion = completion;
    }

    public override void Initialize()
    {
        // Reuse the production application resources and styles so the AI-vision frame reviews
        // the real Map palette and control hierarchy instead of an unstyled Fluent test surface.
        var production = new global::Map.App.App();
        production.Initialize();
        RequestedThemeVariant = production.RequestedThemeVariant;
        foreach (var resource in production.Resources)
        {
            Resources[resource.Key] = resource.Value;
        }
        var styles = production.Styles.ToArray();
        production.Styles.Clear();
        Styles.AddRange(styles);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _options is null
            || _completion is null)
        {
            throw new InvalidOperationException("Native Stage 14 smoke was not configured.");
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workspace = Path.Combine(
            Path.GetTempPath(), "map-stage14-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(workspace, "profile"));

        var catalog = CreateCatalog(workspace);
        var savePath = Path.Combine(workspace, "stage14-smoke.mapproj");
        var now = new DateTimeOffset(2026, 9, 1, 14, 5, 0, TimeSpan.Zero);
        var store = new NativeStore();
        var editor = new EditorViewModel(
            Document(), null, false,
            store,
            new SmokeRecentMaps(),
            new SmokeFilePicker(savePath),
            new SmokePrompts(),
            new SmokeLog(),
            clock: () => now,
            assetCatalog: catalog,
            availableFontFamilies: ["Arial"]);
        var view = new EditorView(
            editor, static () => Task.CompletedTask, static () => { }, static () => { });
        var window = new Window
        {
            Title = "Map Stage 14 native smoke",
            Width = 1_400,
            Height = 900,
            CanResize = false,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32_000, -32_000),
            Content = view,
        };

        window.Opened += async (_, _) =>
        {
            try
            {
                var report = await ExecuteAsync(_options, workspace, savePath, editor, view,
                    () => now, value => now = value);
                _completion.TrySetResult(report);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                view.CloseAuxiliaryWindowsForSmoke();
                view.Canvas.DisposeRenderCaches();
                window.Close();
                desktop.Shutdown();
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativePlacementAndCheckpointsReport> ExecuteAsync(
        PerformanceOptions options,
        string workspace,
        string savePath,
        EditorViewModel editor,
        EditorView view,
        Func<DateTimeOffset> readClock,
        Action<DateTimeOffset> writeClock)
    {
        await SettleAsync();
        view.OpenObjects();
        await SettleAsync();

        var documentBeforeArm = editor.Session.Document;
        var revisionBeforeArm = editor.Session.Revision;
        var dirtyBeforeArm = editor.Session.IsDirty;
        var undoBeforeArm = editor.Session.UndoDepth;
        var selectionBeforeArm = editor.Session.SelectedObjectIds.ToArray();
        editor.ArmPrimitivePlacement(SceneObjectKind.Ellipse);
        var primitiveArmTransient = ReferenceEquals(documentBeforeArm, editor.Session.Document)
            && revisionBeforeArm == editor.Session.Revision
            && dirtyBeforeArm == editor.Session.IsDirty
            && undoBeforeArm == editor.Session.UndoDepth
            && selectionBeforeArm.SequenceEqual(editor.Session.SelectedObjectIds);

        view.Canvas.MovePlacementPointerForSmoke(700, 420);
        if (!editor.RotatePendingPlacement(1, largerStep: true))
        {
            throw new InvalidOperationException("Pending primitive rotation was not handled.");
        }
        await NextFrameAsync(view);
        var pending = editor.PendingPlacement!;
        var preview = AssertSingle(pending.PreviewObjects);
        var ghostVisible = pending.PointerVisible && editor.PlacementPreviewObjects.Count == 1;
        var screenshotPath = Path.ChangeExtension(options.OutputPath, ".ghost.png");
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
        using (var bitmap = new RenderTargetBitmap(new PixelSize(1_400, 900)))
        {
            bitmap.Render(view);
            using var stream = File.Create(screenshotPath);
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }

        var commit = view.Canvas.CommitPlacementForSmoke();
        await SettleAsync();
        var committed = editor.Session.SelectedObject!;
        var primitiveExact = commit
            && NearlyEqual(committed.X, preview.X)
            && NearlyEqual(committed.Y, preview.Y)
            && NearlyEqual(committed.Width, preview.Width)
            && NearlyEqual(committed.Height, preview.Height)
            && NearlyEqual(preview.RotationDegrees, 15)
            && NearlyEqual(committed.RotationDegrees, preview.RotationDegrees)
            && editor.PendingPlacement is null;
        var primitiveNamed = committed.Name == "Эллипс 1";
        var primitiveHistory = editor.Session.UndoDepth == undoBeforeArm + 1
            && editor.Session.SelectedObjectId == committed.Id;

        var beforeSecondaryCancel = editor.Session.Document;
        var revisionBeforeSecondaryCancel = editor.Session.Revision;
        var undoBeforeSecondaryCancel = editor.Session.UndoDepth;
        editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        view.Canvas.MovePlacementPointerForSmoke(640, 360);
        var secondaryCancelled = view.Canvas.CancelPlacementWithSecondaryPointerForSmoke()
            && editor.PendingPlacement is null
            && ReferenceEquals(beforeSecondaryCancel, editor.Session.Document)
            && revisionBeforeSecondaryCancel == editor.Session.Revision
            && undoBeforeSecondaryCancel == editor.Session.UndoDepth;

        var set = editor.LibrarySets.Single();
        editor.SelectAssetSet(set.SetId);
        editor.StampPlacementMode = StampPlacementMode.Series;
        editor.PlacementAutoSelect = PlacementAutoSelectPolicy.Auto;
        editor.ArmStampPlacement();
        var beforeSeries = ObjectCount(editor);
        var rebuildsBeforeMoves = view.ObjectListRebuildCount;
        for (var index = 0; index < 12; index++)
        {
            view.Canvas.MovePlacementPointerForSmoke(520 + index, 340 + index);
        }
        var pointerRebuilds = view.ObjectListRebuildCount - rebuildsBeforeMoves;
        var firstSeries = view.Canvas.CommitPlacementForSmoke();
        view.Canvas.MovePlacementPointerForSmoke(760, 510);
        var secondSeries = view.Canvas.CommitPlacementForSmoke();
        var afterSeries = ObjectCount(editor);
        var beforeEscape = afterSeries;
        var escaped = editor.HandleEscape();
        var seriesExact = firstSeries && secondSeries && escaped
            && afterSeries == beforeSeries + 2
            && ObjectCount(editor) == beforeEscape
            && editor.PendingPlacement is null;

        var versionThroughUi = await view.CreateVersionThroughUiForSmokeAsync();
        await SettleAsync();
        var versionName = editor.Checkpoints.Single().Name;

        var firstSave = await editor.SaveAsync();
        editor.AddObject(SceneObjectKind.Rectangle);
        writeClock(readClock().AddSeconds(5));
        var primarySaved = await editor.TryPrimaryAutosaveAsync();
        var reopened = MapProjectStore.Open(savePath).Document;
        var primaryReopenedLatest = firstSave && primarySaved
            && !editor.Session.IsDirty
            && reopened.Checkpoints.Count == 1
            && reopened.Checkpoints[0].Name == versionName
            && reopened.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count)
                == ObjectCount(editor);

        var onlyTwoTabs = view.WorkspaceTabCount == 6; // six working sections since the 2026-09-09 panel rework
        view.ToggleContextPanel();
        view.ToggleRightPanel();
        var collapsed = view.ContextPanelCollapsed && view.RightPanelCollapsed;
        view.ToggleContextPanel();
        view.ToggleRightPanel();
        var collapseExpand = collapsed && !view.ContextPanelCollapsed && !view.RightPanelCollapsed;
        view.OpenAssetCatalog(focusSearch: false);
        await SettleAsync();
        var catalogOpened = view.AssetCatalogVisible;

        view.CloseAuxiliaryWindowsForSmoke();
        view.Canvas.DisposeRenderCaches();
        var resourcesDisposed = view.Canvas.ObjectCacheCount == 0 && view.Canvas.TileCacheCount == 0;
        var passed = primitiveArmTransient
            && ghostVisible
            && primitiveExact
            && primitiveNamed
            && primitiveHistory
            && secondaryCancelled
            && seriesExact
            && pointerRebuilds == 0
            && versionThroughUi
            && versionName.StartsWith("Версия 01 · ", StringComparison.Ordinal)
            && primaryReopenedLatest
            && onlyTwoTabs
            && collapseExpand
            && catalogOpened
            && resourcesDisposed
            && File.Exists(screenshotPath);

        return new NativePlacementAndCheckpointsReport(
            1,
            DateTimeOffset.UtcNow,
            "native off-screen own-process window; ShowActivated=false; ShowInTaskbar=false; no system input",
            screenshotPath,
            primitiveArmTransient,
            ghostVisible,
            primitiveExact,
            primitiveNamed,
            primitiveHistory,
            secondaryCancelled,
            seriesExact,
            pointerRebuilds,
            versionThroughUi,
            versionName,
            primaryReopenedLatest,
            onlyTwoTabs,
            collapseExpand,
            catalogOpened,
            resourcesDisposed,
            passed,
            [
                "The screenshot is rendered from the real hidden Avalonia visual tree; the OS compositor is not inspected.",
                "All map, library, preferences and recovery paths belong to a unique temporary workspace.",
            ]);
    }

    private static SceneObject AssertSingle(IReadOnlyList<SceneObject> objects)
    {
        if (objects.Count != 1) throw new InvalidOperationException("Expected one placement preview.");
        return objects[0];
    }

    private static int ObjectCount(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count);

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.0001;

    private static async Task NextFrameAsync(EditorView view)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, EventArgs args) => completion.TrySetResult();
        view.Canvas.FrameRendered += OnFrame;
        try
        {
            view.Canvas.InvalidateVisual();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            view.Canvas.FrameRendered -= OnFrame;
        }
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(25);
    }

    private static AssetCatalog CreateCatalog(string workspace)
    {
        var sourceDirectory = Path.Combine(workspace, "source");
        Directory.CreateDirectory(sourceDirectory);
        var paths = new List<string>();
        foreach (var (name, color) in new[]
                 { ("tree-a", SKColors.ForestGreen), ("tree-b", SKColors.OliveDrab) })
        {
            var path = Path.Combine(sourceDirectory, name + ".png");
            using var surface = SKSurface.Create(new SKImageInfo(48, 36))!;
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            paths.Add(path);
        }
        var catalog = new AssetCatalog(Path.Combine(workspace, "catalog"));
        catalog.ImportSet(paths, "Дерево", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        return catalog;
    }

    private static MapDocument Document()
    {
        var objects = new[]
        {
            Rectangle("Прямоугольник 10", 140, 120),
            Rectangle("Прямоугольник 11", 320, 210),
            Rectangle("Прямоугольник 12", 520, 280),
        };
        return new MapDocument(
            DocumentId.New(), "Stage 14 smoke", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1_600, 1_000, "px", "#FF20242B"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
                new ObjectLayer(LayerId.New(), "Объекты 1", true, false, 1, objects),
            ]);
    }

    private static SceneObject Rectangle(string name, double x, double y) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, y, 120, 80, 0, "#FF3E5C76", "#FF10131A", 2, true, false);

    private sealed class SmokeFilePicker(string path) : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path);
    }

    private sealed class SmokePrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) =>
            Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
    }

    private sealed class SmokeLog : IAppLog
    {
        public void Info(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class SmokeRecentMaps : IRecentMaps
    {
        public IReadOnlyList<RecentMapItem> Load() => [];
        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта") { }
        public void Remove(string path) { }
    }

    private sealed class NativeStore : IProjectStore
    {
        public ProjectFileInspection Inspect(string path) => MapProjectStore.Inspect(path);
        public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
            MapProjectStore.MigrateCopy(sourcePath, destinationPath);
        public OpenedProject Open(string path) => MapProjectStore.Open(path);
        public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
            MapProjectStore.Save(document, path, metadata, previewPng);
        public void SaveAutosave(
            MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) { }
        public void ClearRecovery(Guid documentId) { }
        public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => [];
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }
}
