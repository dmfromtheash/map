using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Map.App.Services;
using Map.App.Views;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Performance;

internal sealed record NativeStage15Report(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    string WindowMode,
    string ScreenshotPath,
    bool SyntheticDocumentTopologyReady,
    bool CompactCatalogReady,
    bool GhostVisibleAndTransient,
    bool PreviewCommitExactAndOneCommand,
    bool UndoRedoWorked,
    bool SelectedSliderWasOneCommand,
    bool SeriesAndScatterCancelWorked,
    bool PreferencesReopenedBoundedPreset,
    bool ObjectAndLayerShadowSwitched,
    bool ClipMaskLibraryForegroundAndStateWorked,
    bool SaveReopenAndExactExportMatched,
    bool ResourcesDisposed,
    bool GatePassed,
    IReadOnlyList<string> Limitations);

internal static class NativeStage15Smoke
{
    public static NativeStage15Report Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Stage 15 native smoke targets Windows.");

        var completion = new TaskCompletionSource<NativeStage15Report>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeStage15Application.Configure(options, completion);
                AppBuilder.Configure<NativeStage15Application>()
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
            Name = "Map Stage 15 native smoke",
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

public sealed class NativeStage15Application : Application
{
    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativeStage15Report>? _completion;

    internal static void Configure(
        PerformanceOptions options, TaskCompletionSource<NativeStage15Report> completion)
    {
        _options = options;
        _completion = completion;
    }

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
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || _options is null || _completion is null)
            throw new InvalidOperationException("Native Stage 15 smoke was not configured.");

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workspace = Path.Combine(
            Path.GetTempPath(), "map-stage15-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(workspace, "profile"));

        var catalog = CreateCatalog(workspace);
        var set = catalog.Query(new AssetQuery()).Sets.Single();
        var variant = catalog.FindSet(set.SetId)!.VariantIds
            .Select(catalog.FindVariant).First(item => item is not null)!;
        catalog.SetSetFavorite(set.SetId, true);
        catalog.TouchRecent(set.SetId, variant.Id, DateTimeOffset.UnixEpoch);
        var savePath = Path.Combine(workspace, "stage15-smoke.mapproj");
        var preferencesPath = Path.Combine(workspace, "profile", "editor-preferences.json");
        var preferences = new EditorPreferencesStore(preferencesPath);
        var editor = new EditorViewModel(
            Document(), null, false, new NativeStore(), new SmokeRecentMaps(),
            new SmokeFilePicker(savePath), new SmokePrompts(), new SmokeLog(),
            clock: () => new DateTimeOffset(2026, 9, 1, 15, 0, 0, TimeSpan.Zero),
            assetCatalog: catalog, availableFontFamilies: ["Arial"],
            localPreferences: preferences.Load(), saveLocalPreferences: preferences.Save);
        var view = new EditorView(
            editor, static () => Task.CompletedTask, static () => { }, static () => { });
        var window = new Window
        {
            Title = "Map Stage 15 native smoke",
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
                _completion.TrySetResult(await ExecuteAsync(
                    _options, workspace, savePath, preferences, catalog, editor, view));
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

    private static async Task<NativeStage15Report> ExecuteAsync(
        PerformanceOptions options,
        string workspace,
        string savePath,
        EditorPreferencesStore preferences,
        AssetCatalog catalog,
        EditorViewModel editor,
        EditorView view)
    {
        await SettleAsync();
        var topology = editor.Session.Document.Layers.OfType<ObjectLayer>().Count() == 2
            && editor.Session.Document.Layers.OfType<ObjectLayer>().Any(layer => layer.Mask is not null)
            && ObjectCount(editor) >= 3;

        var set = editor.LibrarySets.Single();
        editor.SelectAssetSet(set.SetId);
        await SettleAsync();

        var beforeForeground = editor.Session.UndoDepth;
        editor.CreateStampForegroundSurface();
        editor.EditStampForegroundSurface();
        editor.BrushSettings = editor.BrushSettings with
        {
            Color = "#FFFF5A36",
            Radius = 180,
            Spacing = 0.1,
        };
        var foregroundStarted = editor.BeginStroke(350, 235);
        editor.ContinueStroke(430, 255);
        editor.EndStroke();
        var foregroundPainted = foregroundStarted
            && editor.Session.UndoDepth == beforeForeground + 2
            && editor.Session.Document.StampForeground is { IsEmpty: false };
        editor.Undo();
        var foregroundUndo = editor.Session.Document.StampForeground is { IsEmpty: true };
        editor.Redo();
        var foregroundRedo = editor.Session.Document.StampForeground is { IsEmpty: false };

        editor.SelectAssetSet(set.SetId);
        editor.SetStampClipMaskEnabled(true);
        await SettleAsync();
        var documentBefore = editor.Session.Document;
        var revisionBefore = editor.Session.Revision;
        var undoBefore = editor.Session.UndoDepth;
        editor.SetStampScalePercent(170);
        editor.RotatePendingPlacement(1, largerStep: true); // same base rotation as the slider/W/S path
        editor.SetStampSublayer(5);
        editor.SetStampFlip(horizontal: true, vertical: false);
        editor.SetStampOpacityPercent(90);
        editor.SetStampHueDegrees(20);
        editor.SetStampSaturationPercent(130);
        editor.SetStampBrightnessPercent(120);
        editor.SetStampContrastPercent(110);
        editor.SetStampBlur(2);
        editor.SetStampBlend("Screen");
        editor.SetStampShadowStandard();
        editor.SetStampShadow(12, 16, 9, 60);
        editor.SetStampShadowColor("#FFFFC000");
        editor.SetStampShadowPlacement(StampShadowPlacement.Object);
        view.Canvas.MovePlacementPointerForSmoke(350, 235);
        await NextFrameAsync(view);
        var compact = view.StampCompactCurrentThumbnailVisibleForSmoke
            && view.StampCompactRecentCountForSmoke > 0
            && view.StampCompactFavoriteCountForSmoke > 0;
        var pending = editor.PendingPlacement!;
        var preview = pending.PreviewObjects.Single();
        var transient = pending.PointerVisible
            && editor.PlacementPreviewObjects.Count == 1
            && preview.Stamp is { ClipMaskEnabled: true, MaskAssetSha256: not null }
            && ReferenceEquals(documentBefore, editor.Session.Document)
            && revisionBefore == editor.Session.Revision
            && undoBefore == editor.Session.UndoDepth;

        var clipGroupVisible = view.ShowFutureClipMaskForSmoke();
        await SettleAsync();
        await NextFrameAsync(view);

        var screenshotPath = Path.ChangeExtension(options.OutputPath, ".ghost.png");
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
        using (var bitmap = new RenderTargetBitmap(new PixelSize(1_400, 900)))
        {
            bitmap.Render(view);
            using var stream = File.Create(screenshotPath);
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        }

        var committedOk = view.Canvas.CommitPlacementForSmoke();
        await SettleAsync();
        var committed = editor.Session.SelectedObject!;
        var exact = committedOk
            && NearlyEqual(committed.X, preview.X)
            && NearlyEqual(committed.Y, preview.Y)
            && NearlyEqual(committed.Width, preview.Width)
            && NearlyEqual(committed.Height, preview.Height)
            && NearlyEqual(committed.RotationDegrees, preview.RotationDegrees)
            && committed.Sublayer == preview.Sublayer
            && NearlyEqual(committed.ObjectOpacity, preview.ObjectOpacity / 0.55)
            && Equals(committed.Stamp, preview.Stamp)
            && editor.Session.UndoDepth == undoBefore + 1;
        var placedId = committed.Id;
        editor.Undo();
        var undoWorked = Objects(editor).All(item => item.Id != placedId);
        editor.Redo();
        var undoRedo = undoWorked && Objects(editor).Any(item => item.Id == placedId);

        editor.Session.SelectedObjectId = placedId;
        editor.ActiveTool = EditorTool.Select;
        var beforeSlider = editor.Session.UndoDepth;
        editor.SetStampHueDegrees(-50, commit: false);
        editor.SetStampHueDegrees(-30, commit: false);
        editor.SetStampHueDegrees(-20, commit: true);
        var oneSelectedCommand = editor.Session.UndoDepth == beforeSlider + 1;

        var beforeClipToggle = editor.Session.UndoDepth;
        editor.SetStampClipMaskEnabled(false);
        var selectedClipOff = editor.Session.UndoDepth == beforeClipToggle + 1
            && editor.Session.SelectedObject!.Stamp is { ClipMaskEnabled: false, MaskAssetSha256: not null };
        editor.SetStampClipMaskEnabled(true);
        var selectedClipOn = editor.Session.UndoDepth == beforeClipToggle + 2
            && editor.Session.SelectedObject!.Stamp is { ClipMaskEnabled: true, MaskAssetSha256: not null };
        var selectedBeforeMissing = editor.Session.SelectedObject!;
        editor.Session.Execute(new ReplaceObjectsCommand(
            [selectedBeforeMissing],
            [selectedBeforeMissing with
            {
                Stamp = selectedBeforeMissing.Stamp! with
                {
                    MaskAssetSha256 = new string('f', 64),
                    ClipMaskEnabled = true,
                },
            }],
            "native missing clip mask state"));
        var missingStateVisible = editor.StampClipMaskStatus == "Маска отсутствует или повреждена"
            && editor.Session.SelectedObject!.Stamp!.ClipMaskEnabled;
        editor.Undo();

        editor.ActiveTool = EditorTool.Stamp;
        editor.StampPlacementMode = StampPlacementMode.Series;
        editor.ArmStampPlacement();
        view.Canvas.MovePlacementPointerForSmoke(520, 350);
        var beforeSeries = ObjectCount(editor);
        var seriesCommit = view.Canvas.CommitPlacementForSmoke();
        var afterSeries = ObjectCount(editor);
        editor.StampPlacementMode = StampPlacementMode.Area;
        editor.StampRandomAngleStep = StampRandomAngleStep.Degrees45;
        editor.StampRandomFlip = true;
        editor.SetStampAreaRadius(130, persist: true);
        editor.SetStampDensity(70, persist: true);
        view.Canvas.MovePlacementPointerForSmoke(760, 510);
        var beforeCancel = ObjectCount(editor);
        var cancelled = view.Canvas.CancelPlacementWithSecondaryPointerForSmoke();
        var seriesScatter = seriesCommit && afterSeries == beforeSeries + 1
            && cancelled && ObjectCount(editor) == beforeCancel;

        var loadedPreferences = preferences.Load();
        var validPresetReopened = loadedPreferences.Stamp is
        {
            ScalePercent: 170,
            Sublayer: 5,
            RandomAngleStep: StampRandomAngleStep.Degrees45,
        } loaded
            && loaded.Style is { Blend: "Screen", ShadowColor: "#FFFFC000" };
        preferences.Save(new EditorLocalPreferences(Stamp: loadedPreferences.Stamp! with
        {
            ScalePercent = 9_999,
            Sublayer = 99,
            ObjectOpacity = -2,
            RandomAngleStep = (StampRandomAngleStep)17,
            Style = loadedPreferences.Stamp.Style! with
            {
                Blend = "unknown",
                ShadowColor = "bad",
                ShadowBlur = 999,
            },
        }));
        var bounded = preferences.Load().Stamp!;
        var preferencesReady = validPresetReopened
            && bounded.ScalePercent == 400
            && bounded.Sublayer == 10
            && bounded.ObjectOpacity == 0
            && bounded.RandomAngleStep == StampRandomAngleStep.Off
            && bounded.Style is
            {
                Blend: BlendModes.Normal,
                ShadowColor: AppearanceColor.OpaqueBlack,
                ShadowBlur: AppearanceLimits.MaxShadowBlur,
            };

        editor.Session.SelectedObjectId = placedId;
        editor.ActiveTool = EditorTool.Select;
        editor.SetStampShadowPlacement(StampShadowPlacement.Object);
        var objectPlacement = editor.Session.SelectedObject!.Stamp!.ShadowPlacement;
        editor.SetStampShadowPlacement(StampShadowPlacement.Layer);
        var layerPlacement = editor.Session.SelectedObject!.Stamp!.ShadowPlacement;
        var bothShadowModes = objectPlacement == StampShadowPlacement.Object
            && layerPlacement == StampShadowPlacement.Layer;

        var exporter = new ImageExportEngine();
        var exportPlan = exporter.Plan(editor.Session.Document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            640, 400, ExportOutputMode.SingleImage, false, false, 100));
        var beforeSaveExport = await exporter.RenderPreviewAsync(
            editor.Session.Document, exportPlan, editor.StampImages);
        var saved = await editor.SaveAsync();
        var reopened = MapProjectStore.Open(savePath).Document;
        var reopenedPlan = exporter.Plan(reopened, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            640, 400, ExportOutputMode.SingleImage, false, false, 100));
        using var reopenedSource = new CatalogStampImageSource(
            catalog, () => reopened, () => savePath);
        var afterSaveExport = await exporter.RenderPreviewAsync(reopened, reopenedPlan, reopenedSource);
        var reopenedStamp = reopened.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects).Single(item => item.Id == placedId);
        var saveExport = saved
            && Equals(reopenedStamp.Stamp, editor.Session.SelectedObject!.Stamp)
            && reopenedStamp.Stamp is { ClipMaskEnabled: true, MaskAssetSha256: not null }
            && reopened.StampForeground is { IsEmpty: false }
            && beforeSaveExport.PngBytes.SequenceEqual(afterSaveExport.PngBytes);

        var clipMaskWorked = foregroundPainted && foregroundUndo && foregroundRedo
            && selectedClipOff && selectedClipOn && missingStateVisible
            && clipGroupVisible
            && catalog.FindVariant(catalog.FindSet(set.SetId)!.VariantIds[0])!.MaskSha256 is not null;

        view.CloseAuxiliaryWindowsForSmoke();
        view.Canvas.DisposeRenderCaches();
        var resourcesDisposed = view.Canvas.ObjectCacheCount == 0 && view.Canvas.TileCacheCount == 0;
        var passed = topology && compact && transient && exact && undoRedo && oneSelectedCommand
            && seriesScatter && preferencesReady && bothShadowModes && clipMaskWorked && saveExport
            && resourcesDisposed && File.Exists(screenshotPath);
        return new NativeStage15Report(
            1, DateTimeOffset.UtcNow,
            "native off-screen own-process window; ShowActivated=false; ShowInTaskbar=false; no system input",
            screenshotPath, topology, compact, transient, exact, undoRedo, oneSelectedCommand,
            seriesScatter, preferencesReady, bothShadowModes, clipMaskWorked,
            saveExport, resourcesDisposed, passed,
            [
                "The screenshot comes from the real hidden Avalonia visual tree; the OS compositor is not inspected.",
                "Clip masks and the shared foreground use synthetic local images created inside the smoke workspace.",
                "All map, asset, preferences and export paths belong to one unique temporary workspace.",
            ]);
    }

    private static AssetCatalog CreateCatalog(string workspace)
    {
        var sourceDirectory = Path.Combine(workspace, "source");
        Directory.CreateDirectory(sourceDirectory);
        var paths = new List<string>();
        foreach (var (name, color, width, height) in new[]
                 { ("tree-a", SKColors.Lime, 64, 48), ("tree-b", SKColors.HotPink, 48, 64) })
        {
            var path = Path.Combine(sourceDirectory, name + ".png");
            using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
            surface.Canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = color, IsAntialias = true };
            surface.Canvas.DrawOval(new SKRect(4, 4, width - 4, height - 4), paint);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            paths.Add(path);
        }
        var catalog = new AssetCatalog(Path.Combine(workspace, "catalog"));
        var set = catalog.ImportSet(paths, "Дерево", new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        for (var index = 0; index < set.VariantIds.Count; index++)
        {
            var variant = catalog.FindVariant(set.VariantIds[index])!;
            var visual = catalog.Find(variant.Sha256)!;
            var maskPath = Path.Combine(sourceDirectory, $"tree-mask-{index}.png");
            using var maskSurface = SKSurface.Create(new SKImageInfo(visual.Width, visual.Height))!;
            maskSurface.Canvas.Clear(SKColors.Black);
            using (var maskPaint = new SKPaint { Color = SKColors.White, IsAntialias = true })
            {
                maskSurface.Canvas.DrawOval(
                    new SKRect(8, 8, visual.Width - 8, visual.Height - 8), maskPaint);
            }
            using var maskImage = maskSurface.Snapshot();
            using var maskData = maskImage.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(maskPath, maskData.ToArray());
            catalog.AssignVariantMask(
                variant.Id, maskPath, DateTimeOffset.UnixEpoch.AddSeconds(index + 1));
        }
        return catalog;
    }

    private static MapDocument Document()
    {
        var first = new ObjectLayer(
            LayerId.New(), "Фоновые объекты", true, false, 1,
            [Rectangle("Дом", 160, 130), Rectangle("Камень", 420, 260)], LayerMask.CreateEmpty());
        var second = new ObjectLayer(
            LayerId.New(), "Штампы", true, false, 0.9,
            [Rectangle("Ориентир", 700, 360)]);
        return new MapDocument(
            DocumentId.New(), "Stage 15 smoke", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1_600, 1_000, "px", "#FF20242B"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"),
                first,
                second,
            ]);
    }

    private static SceneObject Rectangle(string name, double x, double y) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, y, 130, 90, 0, "#FF3E5C76", "#FF10131A", 2, true, false);

    private static IReadOnlyList<SceneObject> Objects(EditorViewModel editor) =>
        editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects).ToArray();
    private static int ObjectCount(EditorViewModel editor) => Objects(editor).Count;
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

    private sealed class SmokeFilePicker(string path) : IFilePicker
    {
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickSaveAsync(string suggestedFileName) => Task.FromResult<string?>(path);
    }

    private sealed class SmokePrompts : IUserPrompts
    {
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string projectTitle) => Task.FromResult(false);
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) => Task.FromResult(CloseChoice.Cancel);
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

    private sealed class NativeStore : IProjectStore
    {
        public ProjectFileInspection Inspect(string path) => MapProjectStore.Inspect(path);
        public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
            MapProjectStore.MigrateCopy(sourcePath, destinationPath);
        public OpenedProject Open(string path) => MapProjectStore.Open(path);
        public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
            MapProjectStore.Save(document, path, metadata, previewPng);
        public void SaveAutosave(MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) { }
        public void ClearRecovery(Guid documentId) { }
        public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => [];
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }
}
