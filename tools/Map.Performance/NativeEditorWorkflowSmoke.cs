using Avalonia;
using Avalonia.Automation;
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
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Platform.Windows;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

internal sealed record NativeEditorWorkflowReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    string WindowMode,
    string DashboardScreenshotPath,
    string WideEditorScreenshotPath,
    string CompactEditorScreenshotPath,
    bool DashboardProjectFlowWorked,
    bool RasterAndSafeSvgImported,
    bool BrushMaterialPresetAndPreviewWorked,
    bool LandLayerMaskAndFilterWorked,
    bool VectorTextAndNoteWorked,
    bool CompositionPackageWorked,
    bool ConnectedEditingFlowWorked,
    bool SaveReopenAndExportWorked,
    bool SettingsRestartAndLocalHelpWorked,
    bool ResponsiveEditorSurfacesWorked,
    bool ResourcesDisposed,
    bool GatePassed,
    IReadOnlyList<string> Limitations);

internal static class NativeEditorWorkflowSmoke
{
    public static NativeEditorWorkflowReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Stage 16 native smoke targets Windows.");

        var completion = new TaskCompletionSource<NativeEditorWorkflowReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                NativeEditorWorkflowApplication.Configure(options, completion);
                AppBuilder.Configure<NativeEditorWorkflowApplication>()
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
            Name = "Map Stage 16 native smoke",
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

public sealed class NativeEditorWorkflowApplication : Application
{
    private static PerformanceOptions? _options;
    private static TaskCompletionSource<NativeEditorWorkflowReport>? _completion;

    internal static void Configure(
        PerformanceOptions options, TaskCompletionSource<NativeEditorWorkflowReport> completion)
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
            throw new InvalidOperationException("Native Stage 16 smoke was not configured.");

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workspace = Path.Combine(
            Path.GetTempPath(), "map-stage16-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(workspace, "profile"));

        var sourcePath = Path.Combine(workspace, "stage16-source.mapproj");
        var clonePath = Path.Combine(workspace, "stage16-clone.mapproj");
        var templatePath = Path.Combine(workspace, "stage16-template.mapproj");
        var document = Document();
        var preview = ThumbnailRenderer.RenderPng(document, 160, 100);
        MapProjectStore.Save(document, sourcePath,
            new SaveMetadata("stage16-smoke", DateTimeOffset.UnixEpoch), preview);
        var sourceBytes = File.ReadAllBytes(sourcePath);

        var recent = new SmokeRecentMaps();
        recent.Touch(sourcePath, document.Id.Value, document.Title, DateTimeOffset.UnixEpoch,
            preview, "Локальная карта");
        var picker = new QueueFilePicker(clonePath, templatePath);
        var dashboard = new DashboardViewModel(
            new NativeStore(), recent, new SmokeLog(), picker, new SmokePrompts(),
            clock: () => new DateTimeOffset(2026, 9, 1, 16, 0, 0, TimeSpan.Zero),
            appVersion: "stage16-smoke");
        var settingsOpened = false;
        var helpOpened = false;
        var dashboardView = new DashboardView(
            dashboard,
            static () => Task.CompletedTask,
            static () => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            cloneRequested: card => dashboard.CloneAsync(card),
            templateRequested: static () => Task.CompletedTask,
            settingsRequested: () => settingsOpened = true,
            helpRequested: () => helpOpened = true);

        var window = new Window
        {
            Title = "Map Stage 16 native smoke",
            Width = 960,
            Height = 640,
            CanResize = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32_000, -32_000),
            Content = dashboardView,
        };

        window.Opened += async (_, _) =>
        {
            try
            {
                _completion.TrySetResult(await ExecuteAsync(
                    _options, workspace, sourcePath, sourceBytes, clonePath, templatePath,
                    dashboard, dashboardView, picker, recent, window,
                    () => settingsOpened, () => helpOpened));
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                if (window.Content is EditorView editorView)
                {
                    editorView.CloseAuxiliaryWindowsForSmoke();
                    editorView.Canvas.DisposeRenderCaches();
                }
                window.Close();
                desktop.Shutdown();
                try { Directory.Delete(workspace, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<NativeEditorWorkflowReport> ExecuteAsync(
        PerformanceOptions options,
        string workspace,
        string sourcePath,
        byte[] sourceBytes,
        string clonePath,
        string templatePath,
        DashboardViewModel dashboard,
        DashboardView dashboardView,
        QueueFilePicker picker,
        SmokeRecentMaps recent,
        Window window,
        Func<bool> settingsOpened,
        Func<bool> helpOpened)
    {
        dashboard.Refresh();
        dashboard.SearchText = "Stage 16";
        dashboard.SortOrder = DashboardSortOrder.NameAscending;
        await SettleAsync();
        var sourceCard = dashboard.RecentCards.Single(card => card.Path == sourcePath);
        var clone = await dashboard.CloneAsync(sourceCard);
        var template = await dashboard.CloneAsync(sourceCard, asTemplate: true);
        var opened = clone is null ? null : await dashboard.TryOpenAsync(clone);
        await SettleAsync();

        Find<Button>(dashboardView, "AppSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Find<Button>(dashboardView, "LocalHelpButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // The library page (search, sort, every map) is the rail's "Карты" section since 2026-10-17.
        Find<Button>(dashboardView, "RailMapsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        var dashboardScreenshot = Path.ChangeExtension(options.OutputPath, ".dashboard.png");
        SaveVisual(dashboardView, dashboardScreenshot, 960, 640);
        var recentCard = Find<Button>(dashboardView, "RecentCard0");
        var newCard = Find<Button>(dashboardView, "NewMapButton");
        var dashboardNamed = Find<Button>(dashboardView, "NewMapButton").IsVisible
            && Find<Button>(dashboardView, "OpenFileButton").IsVisible
            && Find<TextBox>(dashboardView, "DashboardSearchBox").IsVisible
            && Find<Control>(dashboardView, "DashboardSortChoice").IsVisible
            && VisibleInside(recentCard, dashboardView)
            && VisibleInside(newCard, dashboardView);
        var dashboardWorked = clone == clonePath && template == templatePath && opened is not null
            && dashboardNamed
            && File.ReadAllBytes(sourcePath).SequenceEqual(sourceBytes)
            && MapProjectStore.Open(clonePath).Document.Id != MapProjectStore.Open(sourcePath).Document.Id
            && MapProjectStore.Open(templatePath).Document.Id != MapProjectStore.Open(sourcePath).Document.Id
            && recent.Load().Count >= 3;

        var catalog = new AssetCatalog(Path.Combine(workspace, "asset-catalog"));
        var rasterPath = Path.Combine(workspace, "owner-raster.png");
        var svgPath = Path.Combine(workspace, "owner-safe.svg");
        // The raster becomes a brush material below, so it is the owner's opaque snow texture, not a mountain.
        OwnerMountainFixtures.WriteTexture(rasterPath);
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='40' height='28' viewBox='0 0 40 28'>"
            + "<path d='M2 26 L20 2 L38 26 Z' fill='#779955' stroke='#223311' stroke-width='2'/></svg>";
        File.WriteAllText(svgPath, svg);
        var raster = catalog.Import(rasterPath, new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var safeSvg = catalog.Import(svgPath, new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var assetsWorked = raster.MediaType == "image/png" && safeSvg.MediaType == "image/png"
            && safeSvg.Width == 40 && safeSvg.Height == 28 && File.ReadAllText(svgPath) == svg;

        var editor = new EditorViewModel(
            opened!.Project.Document, clonePath, false,
            new NativeStore(), recent, picker, new SmokePrompts(), new SmokeLog(),
            clock: () => new DateTimeOffset(2026, 9, 1, 16, 5, 0, TimeSpan.Zero),
            appVersion: "stage16-smoke", assetCatalog: catalog,
            availableFontFamilies: ["Arial"]);
        var view = new EditorView(
            editor, static () => Task.CompletedTask, static () => { }, static () => { });
        window.Content = view;
        await SettleAsync();

        var wideScreenshot = Path.ChangeExtension(options.OutputPath, ".wide.png");
        var compactScreenshot = Path.ChangeExtension(options.OutputPath, ".compact.png");
        window.Width = 1_416; window.Height = 939;
        await SettleAsync();
        var wideResponsive = CheckEditorSurface(view);
        SaveVisual(view, wideScreenshot, 1_416, 939);
        window.Width = 960; window.Height = 640;
        await SettleAsync();
        var compactResponsive = CheckEditorSurface(view);
        SaveVisual(view, compactScreenshot, 960, 640);
        var responsive = wideResponsive && compactResponsive;

        var brushLayer = editor.Session.Document.Layers.OfType<BrushLayer>().Single();
        var landLayer = editor.Session.Document.Layers.OfType<LandLayer>().Single();
        var objectLayer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        var filterLayer = editor.Session.Document.Layers.OfType<FilterLayer>().Single();

        editor.SelectLayer(brushLayer.Id);
        editor.ActiveTool = EditorTool.Brush;
        editor.ApplyBrushPreset(BrushPresetKind.Ellipse);
        var material = editor.UseAssetAsBrushTexture(raster);
        editor.BrushSettings = editor.BrushSettings with
        {
            Radius = 22, Softness = .35, Opacity = .72,
            TextureScale = 1.8, TextureRotationDegrees = 27,
            TextureOffsetX = 4, TextureOffsetY = -3, TextureOpacity = .66,
            HueDegrees = 18, Saturation = .2, Brightness = .05, Contrast = .12,
        };
        var presetSaved = editor.SaveBrushPreset("Stage 16 material");
        var preset = editor.BrushPresets.Single(item => item.Name == "Stage 16 material");
        var presetApplied = editor.ApplyLocalBrushPreset(preset.Id);
        var beforeBrushDocument = editor.Session.Document;
        var beforeBrushUndo = editor.Session.UndoDepth;
        var brushStarted = editor.BeginStroke(100, 100, .55);
        editor.ContinueStroke(180, 135, .9);
        var brushTransient = editor.StrokePreview is { Coverage.Count: > 0 }
            && ReferenceEquals(beforeBrushDocument, editor.Session.Document);
        editor.EndStroke();
        var brushCommitted = ((BrushLayer)editor.Session.Document.FindLayer(brushLayer.Id)!)
            .Tiles.TileCount > 0 && editor.Session.UndoDepth == beforeBrushUndo + 1;
        var brushUndo = editor.Session.Undo();
        var brushRedo = editor.Session.Redo();
        var brushWorked = material && presetSaved && presetApplied && brushStarted
            && brushTransient && brushCommitted && brushUndo && brushRedo;
        if (!brushWorked)
            Console.WriteLine($"brush: material={material} preset={presetSaved}/{presetApplied} started={brushStarted} "
                + $"transient={brushTransient} committed={brushCommitted} undo={brushUndo} redo={brushRedo} status={editor.StatusDetail}");

        editor.SelectLayer(landLayer.Id);
        editor.ActiveTool = EditorTool.Land;
        var landShape = editor.ApplyLandShape(
            LandShapeKind.Ellipse, new WorldBounds(80, 70, 360, 260));
        var landStyle = editor.SetActiveLandStyle(style => style with
        {
            FillColor = "#FF779955", OutlineColor = "#FF223311", OutlineWidth = 5,
            InnerShadowOpacity = .2, OuterShadowOpacity = .35,
            ShadowOffsetX = 6, ShadowOffsetY = 8, ShadowBlur = 10,
            RippleColor = "#665577AA", RippleWidth = 3, RippleSpacing = 14, RippleCount = 4,
        });
        var landMove = editor.MoveActiveLandArea(
            new WorldBounds(80, 70, 360, 260), 16, 16, snapToGrid: false);

        editor.SelectLayer(brushLayer.Id);
        editor.ToggleActiveMask();
        editor.ActiveTool = EditorTool.Mask;
        var maskStarted = editor.BeginStroke(120, 100, .8);
        editor.ContinueStroke(170, 130, .8);
        editor.EndStroke();
        editor.CopyActiveMask();
        editor.SelectLayer(objectLayer.Id);
        var maskCopied = editor.PasteCopiedMask();
        editor.ToggleActiveMask();
        var opacity = editor.SetActiveLayerOpacity(64);
        var blend = editor.SetActiveLayerBlend("Overlay");
        var effects = editor.SetActiveObjectLayerEffects(new LayerEffects
        {
            ClippingEnabled = true, ShadowColor = "#FF283848",
            OuterShadowOpacity = .38, InnerShadowOpacity = .17,
            ShadowOffsetX = 7, ShadowOffsetY = -4, ShadowBlur = 9,
        });
        var sublayer = editor.SetActiveObjectSublayerState(7, visible: true, locked: true);
        editor.SelectLayer(filterLayer.Id);
        var filter = editor.SetActiveFilter(.14, -.18, .31, 24, -.12, .43, 2.4);
        editor.UseAssetAsActiveFilterTexture(safeSvg);
        var layersWorked = landShape && landStyle && landMove && maskStarted && maskCopied
            && ((ObjectLayer)editor.Session.Document.FindLayer(objectLayer.Id)!).Mask is { Enabled: false }
            && opacity && blend && effects && sublayer && filter;

        editor.SelectLayer(objectLayer.Id);
        editor.ActiveTool = EditorTool.Line;
        editor.ActiveLineDrawMode = LineDrawMode.Pen;
        var lineStarted = editor.BeginVectorDraw(90, 80);
        editor.ContinueVectorDraw(390, 210);
        var lineTransient = editor.PlacementPreviewObjects.Count == 1;
        editor.EndVectorDraw();
        var line = editor.Session.SelectedObject!;
        var nodeSelected = editor.SelectVectorNode(line.Line!.Nodes[1].Id);
        var nodeEdited = editor.SetSelectedVectorNode(
            .82, .68, VectorNodeKind.Curve, -.08, 0, .1, .03);
        editor.AddPortalToSelected(.42, 36);
        var portalEdited = editor.SetSelectedPortal(.47, 42);

        editor.ActiveTool = EditorTool.Shape;
        editor.ActiveShapeDrawMode = ShapeDrawMode.Rectangle;
        var firstShapeStarted = editor.BeginVectorDraw(310, 180);
        editor.ContinueVectorDraw(610, 430);
        editor.EndVectorDraw();
        var firstShape = editor.Session.SelectedObject!;
        editor.UseAssetAsSelectedShapeFill(raster);
        editor.SetShapeFillTextureScale(2.25);
        editor.SetShapeFillTextureRotation(31);
        editor.SetShapeFillTextureOffsetX(9);
        editor.SetShapeFillTextureOffsetY(-6);
        editor.ActiveShapeDrawMode = ShapeDrawMode.Ellipse;
        var secondShapeStarted = editor.BeginVectorDraw(470, 260);
        editor.ContinueVectorDraw(760, 500);
        editor.EndVectorDraw();
        var secondShape = editor.Session.SelectedObject!;
        editor.Session.SetSelection([firstShape.Id, secondShape.Id]);
        var booleanDocument = editor.Session.Document;
        var booleanPreview = editor.PreviewShapeBoolean(ShapeBooleanOperation.Add)
            && ReferenceEquals(booleanDocument, editor.Session.Document)
            && editor.PlacementPreviewObjects.Count == 1;
        var booleanCommit = editor.CommitShapeBooleanPreview();

        editor.ActiveTool = EditorTool.Text;
        editor.TextInheritFromSelection = false;
        var textStarted = editor.BeginTextPlacement(180, 500);
        editor.ContinueTextPlacement(560, 650);
        var textTransient = editor.PlacementPreviewObjects.Count == 1;
        editor.EndTextPlacement();
        var text = editor.Session.SelectedObject!;
        editor.AddNoteForSelection();
        var note = editor.Notes.Single();
        const string markdown = "## Первая карта\n- **Локальная** заметка\n[Справка](guide/first-map)";
        editor.EditNote(note.Id, "Stage 16 note", markdown);
        var noteSafe = NoteMarkdown.RenderPlain(markdown).Contains("Локальная", StringComparison.Ordinal)
            && editor.Notes.Single().AnchorKind == NoteAnchorKind.Object;
        var vectorWorked = lineStarted && lineTransient && nodeSelected && nodeEdited && portalEdited
            && firstShapeStarted && secondShapeStarted && booleanPreview && booleanCommit
            && textStarted && textTransient && text.Text is not null && noteSafe;

        var packagePath = Path.Combine(workspace, "stage16-composition.mapcomp");
        editor.Session.SetSelection([line.Id, text.Id]);
        var packageExport = editor.ExportSelectedCompositionPackage(packagePath);
        var beforePackageCount = ObjectCount(editor.Session.Document);
        var packageImport = editor.ImportCompositionPackage(packagePath);
        var packageAdded = ObjectCount(editor.Session.Document) == beforePackageCount + 1
            && editor.Session.SelectedObject?.Composition is not null;
        var packageUndo = editor.Session.Undo();
        var packageGone = ObjectCount(editor.Session.Document) == beforePackageCount;
        var packageRedo = editor.Session.Redo();
        var packageWorked = packageExport && packageImport && packageAdded
            && packageUndo && packageGone && packageRedo;

        var saved = await editor.SaveAsync();
        var reopened = MapProjectStore.Open(clonePath);
        var exporter = new ImageExportEngine();
        var exportPlan = exporter.Plan(editor.Session.Document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            512, 384, ExportOutputMode.SingleImage, false, false, 100));
        var beforeSavePreview = await exporter.RenderPreviewAsync(
            editor.Session.Document, exportPlan, editor.StampImages);
        var reopenedPlan = exporter.Plan(reopened.Document, new ExportRequest(
            ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            512, 384, ExportOutputMode.SingleImage, false, false, 100));
        var reopenedPreview = await exporter.RenderPreviewAsync(
            reopened.Document, reopenedPlan, editor.StampImages);
        var exportPath = Path.Combine(workspace, "stage16-result.png");
        var export = await exporter.ExportAsync(
            reopened.Document, reopenedPlan, exportPath, editor.StampImages);
        using var exportedBitmap = SKBitmap.Decode(exportPath);
        // The file is written in the oldest version that holds it: the sea of a new map needs v32.
        var saveExportWorked = saved
            && MapProjectStore.Inspect(clonePath).SourceSchema >= MapProjectFormat.SchemaVersion
            && beforeSavePreview.PngBytes.SequenceEqual(reopenedPreview.PngBytes)
            && export.FileCount == 1 && exportedBitmap is { Width: 512, Height: 384 };

        var settingsPath = Path.Combine(workspace, "profile", "settings.json");
        var settingsStore = new AppSettingsStore(settingsPath);
        var writtenSettings = settingsStore.Save(new AppSettings
        {
            Theme = LocalThemeMode.Light,
            UiScale = 1.25,
            AutosaveIntervalSeconds = 75,
            RecoveryItemBudget = 9,
            PerformanceMode = CanvasPerformanceMode.Quality,
            LibraryRoot = Path.Combine(workspace, "maps"),
            CacheRoot = Path.Combine(workspace, "cache"),
            DefaultExportFormat = "Png",
            DefaultExportScalePercent = 150,
            DefaultExportIncludeGrid = true,
        });
        var restartedSettings = new AppSettingsStore(settingsPath).Load();
        var settingsHelpWorked = settingsOpened() && helpOpened()
            && writtenSettings == restartedSettings
            && Find<Button>(dashboardView, "LocalHelpButton") is not null;

        view.CloseAuxiliaryWindowsForSmoke();
        view.Canvas.DisposeRenderCaches();
        var disposed = view.Canvas.ObjectCacheCount == 0 && view.Canvas.TileCacheCount == 0;
        var connected = brushWorked && layersWorked && vectorWorked && packageWorked;
        var passed = dashboardWorked && assetsWorked && connected && saveExportWorked
            && settingsHelpWorked && responsive && disposed
            && File.Exists(dashboardScreenshot) && File.Exists(wideScreenshot)
            && File.Exists(compactScreenshot);

        return new NativeEditorWorkflowReport(
            1,
            DateTimeOffset.UtcNow,
            "native off-screen own-process window; ShowActivated=false; ShowInTaskbar=false; no system input",
            dashboardScreenshot,
            wideScreenshot,
            compactScreenshot,
            dashboardWorked,
            assetsWorked,
            brushWorked,
            layersWorked,
            vectorWorked,
            packageWorked,
            connected,
            saveExportWorked,
            settingsHelpWorked,
            responsive,
            disposed,
            passed,
            [
                "The three PNGs are rendered from the real hidden Avalonia visual tree; the OS compositor is not inspected.",
                "All maps, assets, settings, packages, exports and recovery paths belong to one unique temporary workspace.",
            ]);
    }

    private static bool CheckEditorSurface(EditorView view)
    {
        var canvas = view.Canvas;
        var context = Find<Control>(view, "ContextPanel");
        // DEC-070 «Б»: the map panel is a tab of the right column beside «Свойства»; the tabs row stands for it.
        var side = Find<Control>(view, "RightColumnTabs");
        var select = Find<Button>(view, "SelectToolButton");
        var canvasReady = canvas.IsVisible && canvas.Bounds.Width >= 120 && canvas.Bounds.Height >= 180;
        var keyboardPath = !string.IsNullOrWhiteSpace(ToolTip.GetTip(select)?.ToString())
            && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(select));
        return canvasReady && context.IsVisible && context.Bounds.Width > 0
            && side.IsVisible && side.Bounds.Width > 0
            && view.WorkspaceTabCount == 6
            && select.IsVisible && select.Bounds.Width > 0 && keyboardPath;
    }

    private static bool VisibleInside(Control control, Control root)
    {
        var origin = control.TranslatePoint(default, root);
        return control.IsVisible && origin is { } point
            && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && point.X < root.Bounds.Width && point.Y < root.Bounds.Height
            && point.X + control.Bounds.Width > 0 && point.Y + control.Bounds.Height > 0;
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(item => item.Name == name)
        ?? throw new InvalidOperationException($"Native Stage 16 smoke did not find {name}.");

    private static void SaveVisual(Control control, string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bitmap.Render(control);
        using var stream = File.Create(path);
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static async Task SettleAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(25);
    }

    private static int ObjectCount(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Sum(layer => layer.Objects.Count);

    private static MapDocument Document()
    {
        var objectLayer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        return new MapDocument(
            DocumentId.New(), "Stage 16 smoke", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1_024, 768, "px", CurrentLook.PageColor),
            [
                CurrentLook.Page("Фон"),
                objectLayer,
                BrushLayer.CreateEmpty(LayerId.New(), "Кисть"),
                LandLayer.CreateEmpty(LayerId.New(), "Суша"),
                new FilterLayer(LayerId.New(), "Фильтр", true, false, 1, new FilterSettings()),
            ]);
    }

    private sealed class QueueFilePicker(params string[] savePaths) : IFilePicker
    {
        private readonly Queue<string> _savePaths = new(savePaths);
        public Task<string?> PickOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveAsync(string suggestedFileName) =>
            Task.FromResult<string?>(_savePaths.Count == 0 ? null : _savePaths.Dequeue());
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
        private readonly Dictionary<string, RecentMapItem> _items =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<RecentMapItem> Load() => _items.Values.ToList();

        public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc,
            byte[]? thumbnailPng = null, string mapType = "Локальная карта")
        {
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            _items[path] = new RecentMapItem(
                path, title, modifiedUtc, File.Exists(path), mapType, size, thumbnailPng);
        }

        public void Remove(string path) => _items.Remove(path);
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
        public OpenedProject RestoreRecovery(RecoveryCandidate candidate) =>
            throw new NotSupportedException();
        public void DiscardRecovery(RecoveryCandidate candidate) { }
    }
}
