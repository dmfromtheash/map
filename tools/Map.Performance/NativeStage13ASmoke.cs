using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
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

internal sealed record NativeObjectTreeResult(
    int LogicalRows,
    int InitialRealizedRows,
    int MiddleRealizedRows,
    int EndRealizedRows,
    int MaxRealizedRows,
    int SearchResultRows,
    bool ScrollStayedVirtualized);

internal sealed record NativeDragResult(
    string ResponsivenessGate,
    IReadOnlyList<double> InputToRenderSamples,
    double InputToRenderP50,
    double InputToRenderP95,
    double InputToRenderMax,
    int PointerMoveTreeRebuilds,
    bool PreviewKeptSourceRevision,
    int PreviewProposedObjectCount,
    int IncrementalSpatialOverridesAfterCommit,
    int UndoDepthAfterCommit,
    bool SelectionWasExact,
    bool CommitWasExact,
    bool UndoWasExact,
    bool RedoWasExact);

internal sealed record NativeLodResult(
    IReadOnlyList<double> OverviewInputToRenderSamples,
    IReadOnlyList<double> OverviewSceneRenderSamples,
    double OverviewP50,
    double OverviewP95,
    double OverviewMax,
    double OverviewSceneRenderP95,
    string OverviewTier,
    string DragTier,
    string NormalIdleTier,
    int OverviewObjects,
    int OverviewChunks,
    int CacheItemsAtPlateau,
    long CacheBytesAtPlateau,
    bool SmallMarqueeWasExact,
    bool ExactRegionExportDecoded,
    int CacheItemsAfterDispose,
    long CacheBytesAfterDispose,
    long CacheDisposals,
    bool ResourcesWereBoundedAndDisposed);

internal sealed record NativeStage13AReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    PerformanceOptions Options,
    FixtureManifest Fixture,
    string WindowMode,
    string RendererBackend,
    NativeObjectTreeResult ObjectTree,
    NativeDragResult Drag,
    NativeLodResult? Lod,
    bool GatePassed,
    IReadOnlyList<string> Limitations);

internal static class NativeStage13ASmoke
{
    public static NativeStage13AReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The native Stage 13A smoke targets Map's Windows desktop runtime.");
        }

        var completion = new TaskCompletionSource<NativeStage13AReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeSmokeApplication.Configure(options, completion);
                AppBuilder.Configure<NativeSmokeApplication>()
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
            Name = "Map Stage 13A native smoke",
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

public sealed class NativeSmokeApplication : Application
{
    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativeStage13AReport>? _completion;

    internal static void Configure(
        PerformanceOptions options,
        TaskCompletionSource<NativeStage13AReport> completion)
    {
        _options = options;
        _completion = completion;
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _options is null
            || _completion is null)
        {
            throw new InvalidOperationException("Native Stage 13A smoke was not configured.");
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var fixture = _options.Profile switch
        {
            "native-stage13d" => Stage13Fixtures.Mixed50KLarge(_options.Seed),
            "native-stage13c" => Stage13Fixtures.Mixed50K(_options.Seed),
            _ => Stage13Fixtures.Simple(_options.ObjectCount, _options.Seed),
        };
        var editor = new EditorViewModel(
            fixture.Document,
            documentPath: null,
            startDirty: false,
            new SmokeProjectStore(),
            new SmokeRecentMaps(),
            new SmokeFilePicker(),
            new SmokePrompts(),
            new SmokeLog(),
            availableFontFamilies: ["Arial"]);
        var view = new EditorView(editor, static () => Task.CompletedTask, static () => { }, static () => { });
        var window = new Window
        {
            Title = "Map Stage 13A native smoke",
            Width = 1_280,
            Height = 800,
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
                var report = await ExecuteAsync(_options, fixture, editor, view);
                _completion.TrySetResult(report);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                window.Close();
                desktop.Shutdown();
            }
        };
        desktop.MainWindow = window;

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativeStage13AReport> ExecuteAsync(
        PerformanceOptions options,
        Stage13Fixture fixture,
        EditorViewModel editor,
        EditorView view)
    {
        view.OpenObjects();
        await SettleAsync();

        var logicalRows = view.ObjectListLogicalCount;
        var realized = new List<int> { view.ObjectListRealizedCount };
        var initialRealized = realized[^1];

        view.ScrollObjectsTo(logicalRows / 2);
        await SettleAsync();
        realized.Add(view.ObjectListRealizedCount);
        var middleRealized = realized[^1];

        view.ScrollObjectsTo(logicalRows - 1);
        await SettleAsync();
        realized.Add(view.ObjectListRealizedCount);
        var endRealized = realized[^1];

        view.SetObjectSearchText(fixture.PointTarget.Name);
        await SettleAsync();
        var searchRows = view.ObjectListLogicalCount;

        view.SetObjectSearchText("");
        await SettleAsync();
        realized.Add(view.ObjectListRealizedCount);

        NativeLodResult? lod = null;
        var isStageC = options.Profile is "native-stage13c" or "native-stage13d";
        var overviewSamples = new List<double>();
        var overviewRenderSamples = new List<double>();
        string overviewTier = "not-run";
        var overviewObjects = 0;
        var overviewChunks = 0;
        var cacheItemsAtPlateau = 0;
        var cacheBytesAtPlateau = 0L;
        if (isStageC)
        {
            _ = await MeasureNextFrameAsync(view, view.Canvas.FitToCanvas);
            for (var index = 0; index < options.WarmupIterations; index++)
            {
                _ = await MeasureNextFrameAsync(
                    view,
                    () => view.Canvas.PanByScreenDeltaForSmoke(index % 2 == 0 ? 1 : -1, 0));
            }
            for (var index = 0; index < options.MeasuredIterations; index++)
            {
                overviewSamples.Add(await MeasureNextFrameAsync(
                    view,
                    () => view.Canvas.PanByScreenDeltaForSmoke(index % 2 == 0 ? 1 : -1, 0)));
                overviewRenderSamples.Add(view.Canvas.LastSceneRenderMilliseconds);
            }
            overviewTier = view.Canvas.LastLodTier.ToString();
            overviewObjects = view.Canvas.LastRenderStats.OverviewObjects;
            overviewChunks = view.Canvas.LastRenderStats.OverviewChunks;
            cacheItemsAtPlateau = view.Canvas.ObjectCacheCount;
            cacheBytesAtPlateau = view.Canvas.ObjectCacheBytes;
        }

        // Use an ungrouped object so one pointer press has the exact single-object semantics the
        // drag assertion checks. Position the viewport around it; the earlier attached-view fit
        // deliberately showed the whole 65K canvas and belongs to the later Stage 13C overview.
        var target = fixture.ObjectLayer.Objects[fixture.Manifest.GroupedObjectCount];
        var startX = target.X + (target.Width / 2);
        var startY = target.Y + (target.Height / 2);
        editor.Session.Viewport.Set(target.X - 100, target.Y - 100, 1);
        var sourceRevision = editor.Session.Document;
        var gesture = editor.BeginCanvasInteraction(startX, startY, viewScale: 1);
        var selectedExactly = gesture == CanvasGesture.Drag
            && editor.Session.SelectedObjectId == target.Id
            && editor.SelectionCount == 1;
        var rebuildsBeforeMoves = view.ObjectListRebuildCount;

        for (var index = 0; index < options.WarmupIterations; index++)
        {
            _ = await MeasureNextFrameAsync(
                view,
                () => editor.UpdateDragTo(startX + 1 + index, startY + 1 + index));
        }

        var samples = new List<double>(options.MeasuredIterations);
        for (var index = 0; index < options.MeasuredIterations; index++)
        {
            var nextX = startX + 4 + index;
            var nextY = startY + 3 + index;
            samples.Add(await MeasureNextFrameAsync(
                view,
                () => editor.UpdateDragTo(nextX, nextY)));
        }
        var dragTier = view.Canvas.LastLodTier.ToString();

        var pointerMoveRebuilds = view.ObjectListRebuildCount - rebuildsBeforeMoves;
        var previewKeptSourceRevision = ReferenceEquals(sourceRevision, editor.Session.Document);
        var previewProposedCount = editor.DragPreview?.Proposed.Count ?? 0;
        var finalDeltaX = 4 + options.MeasuredIterations - 1;
        var finalDeltaY = 3 + options.MeasuredIterations - 1;
        editor.EndDrag();
        await SettleAsync();
        var afterCommit = DocumentEditor.FindObject(editor.Session.Document, target.Id)!;
        var committedLayer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        var incrementalSpatialOverrides = ObjectSpatialIndex.For(committedLayer).IncrementalOverrideCount;
        var commitExact = editor.Session.UndoDepth == 1
            && NearlyEqual(afterCommit.X, target.X + finalDeltaX)
            && NearlyEqual(afterCommit.Y, target.Y + finalDeltaY);

        editor.Undo();
        await SettleAsync();
        var afterUndo = DocumentEditor.FindObject(editor.Session.Document, target.Id)!;
        var undoExact = editor.Session.UndoDepth == 0
            && editor.Session.RedoDepth == 1
            && NearlyEqual(afterUndo.X, target.X)
            && NearlyEqual(afterUndo.Y, target.Y);

        editor.Redo();
        await SettleAsync();
        var afterRedo = DocumentEditor.FindObject(editor.Session.Document, target.Id)!;
        var redoExact = editor.Session.UndoDepth == 1
            && editor.Session.RedoDepth == 0
            && NearlyEqual(afterRedo.X, target.X + finalDeltaX)
            && NearlyEqual(afterRedo.Y, target.Y + finalDeltaY);

        var normalIdleTier = view.Canvas.LastLodTier.ToString();
        var marqueeExact = true;
        var exactRegionExportDecoded = true;
        var cacheItemsAfterDispose = view.Canvas.ObjectCacheCount;
        var cacheBytesAfterDispose = view.Canvas.ObjectCacheBytes;
        var cacheDisposals = view.Canvas.ObjectCacheDisposals;
        var resourcesDisposed = true;
        if (isStageC)
        {
            editor.BeginMarquee(startX - 20, startY - 20, additive: false);
            _ = await MeasureNextFrameAsync(
                view,
                () => editor.UpdateMarquee(startX + 20, startY + 20));
            editor.EndMarquee();
            await SettleAsync();
            marqueeExact = editor.SelectionCount == 1
                && editor.Session.SelectedObjectId == target.Id;

            var exporter = new ImageExportEngine();
            var region = new CanvasRegion(
                Math.Max(0, (int)Math.Floor(target.X) - 32),
                Math.Max(0, (int)Math.Floor(target.Y) - 32),
                128,
                128);
            var plan = exporter.Plan(editor.Session.Document, new ExportRequest(
                ExportImageFormat.Png,
                ExportAreaMode.Region,
                region,
                128,
                128,
                ExportOutputMode.SingleImage,
                TransparentBackground: false,
                IncludeGrid: true));
            var preview = await exporter.RenderPreviewAsync(
                editor.Session.Document, plan, stamps: null);
            using var decoded = SKBitmap.Decode(preview.PngBytes);
            exactRegionExportDecoded = decoded is { Width: 128, Height: 128 };

            view.Canvas.DisposeRenderCaches();
            cacheItemsAfterDispose = view.Canvas.ObjectCacheCount;
            cacheBytesAfterDispose = view.Canvas.ObjectCacheBytes;
            cacheDisposals = view.Canvas.ObjectCacheDisposals;
            resourcesDisposed = cacheItemsAtPlateau is > 0 and <= 1_024
                && cacheBytesAtPlateau is > 0 and <= 64L * 1024 * 1024
                && cacheItemsAfterDispose == 0
                && cacheBytesAfterDispose == 0
                && cacheDisposals >= cacheItemsAtPlateau;

            var overviewOrdered = overviewSamples.Order().ToArray();
            var overviewRenderOrdered = overviewRenderSamples.Order().ToArray();
            lod = new NativeLodResult(
                overviewSamples,
                overviewRenderSamples,
                Percentile(overviewOrdered, 0.50),
                Percentile(overviewOrdered, 0.95),
                overviewOrdered[^1],
                Percentile(overviewRenderOrdered, 0.95),
                overviewTier,
                dragTier,
                normalIdleTier,
                overviewObjects,
                overviewChunks,
                cacheItemsAtPlateau,
                cacheBytesAtPlateau,
                marqueeExact,
                exactRegionExportDecoded,
                cacheItemsAfterDispose,
                cacheBytesAfterDispose,
                cacheDisposals,
                resourcesDisposed);
        }

        var ordered = samples.Order().ToArray();
        var p50 = Percentile(ordered, 0.50);
        var p95 = Percentile(ordered, 0.95);
        var maximum = ordered[^1];
        var responsivenessLimit = options.Profile == "native-stage13a" ? 150.0 : 33.3;
        var maxRealized = realized.Max();
        var objectTree = new NativeObjectTreeResult(
            logicalRows,
            initialRealized,
            middleRealized,
            endRealized,
            maxRealized,
            searchRows,
            maxRealized is > 0 and < 500);
        var drag = new NativeDragResult(
            $"off-screen native interaction-start to Skia-pass P95 <= {responsivenessLimit:F1} ms",
            samples,
            p50,
            p95,
            maximum,
            pointerMoveRebuilds,
            previewKeptSourceRevision,
            previewProposedCount,
            incrementalSpatialOverrides,
            editor.Session.UndoDepth,
            selectedExactly,
            commitExact,
            undoExact,
            redoExact);
        var expectedLogicalRows = fixture.Manifest.ObjectCount
            - fixture.Manifest.GroupedObjectCount
            + fixture.Manifest.GroupCount;
        var passed = logicalRows == expectedLogicalRows
            && objectTree.ScrollStayedVirtualized
            && searchRows == 1
            && pointerMoveRebuilds == 0
            && previewKeptSourceRevision
            && previewProposedCount == 1
            && incrementalSpatialOverrides == 1
            && p95 <= responsivenessLimit
            && selectedExactly
            && commitExact
            && undoExact
            && redoExact
            && (!isStageC || lod is not null
                && lod.OverviewTier == nameof(Map.Rendering.RenderLodTier.Overview)
                && lod.DragTier == nameof(Map.Rendering.RenderLodTier.Interactive)
                && lod.NormalIdleTier == nameof(Map.Rendering.RenderLodTier.Exact)
                && lod.OverviewObjects == fixture.Manifest.ObjectCount
                && lod.OverviewP95 <= 33.3
                && lod.OverviewMax <= 100
                && lod.SmallMarqueeWasExact
                && lod.ExactRegionExportDecoded
                && lod.ResourcesWereBoundedAndDisposed);

        return new NativeStage13AReport(
            SchemaVersion: 1,
            RecordedUtc: DateTimeOffset.UtcNow,
            Options: options,
            Fixture: fixture.Manifest,
            WindowMode: "native off-screen window; ShowActivated=false; ShowInTaskbar=false; no system input",
            RendererBackend: "Avalonia native desktop / Skia",
            ObjectTree: objectTree,
            Drag: drag,
            Lod: lod,
            GatePassed: passed,
            Limitations:
            [
                "The smoke measures own-process interaction-start to completion of the Avalonia Skia scene pass; OS compositor presentation is not observable from the app hook.",
                "The window is real but off-screen and never activated, so the smoke cannot disturb another foreground application.",
            ]);
    }

    private static async Task<double> MeasureNextFrameAsync(EditorView view, Action interaction)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, EventArgs args) => completion.TrySetResult();
        view.Canvas.FrameRendered += OnFrame;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            interaction();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
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

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var index = (int)Math.Ceiling(ordered.Count * percentile) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.0001;

    private sealed class SmokeFilePicker : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    }

    private sealed class SmokePrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) =>
            Task.FromResult(CloseChoice.Cancel);
        public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
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

    private sealed class SmokeProjectStore : IProjectStore
    {
        public ProjectFileInspection Inspect(string path) => MapProjectStore.Inspect(path);

        public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
            MapProjectStore.MigrateCopy(sourcePath, destinationPath);

        public OpenedProject Open(string path) => throw new NotSupportedException();
        public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
            throw new NotSupportedException();
        public void SaveAutosave(
            MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) =>
            throw new NotSupportedException();
        public void ClearRecovery(Guid documentId) { }
        public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => [];
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }
}
