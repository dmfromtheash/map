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

internal sealed class NativeStage18Report
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false; scoped control events and production canvas router. File pickers and map reopen use local adapters. No system input. Timing ends at a rendered native canvas frame, not OS scan-out.";
    public Dictionary<string, bool> Checks { get; } = [];
    public int FixtureObjects { get; set; }
    public bool MeasurementPerformed { get; set; }
    public bool MeasurementRequired { get; set; } = true;
    public int ChangedObjects { get; set; }
    public int VisitedCells { get; set; }
    public int ChangedCells { get; set; }
    public int SharedCellCount { get; set; }
    public long PrivateBeforeBytes { get; set; }
    public long PrivateAfterFrameBytes { get; set; }
    public double EditHandlerMs { get; set; }
    public double EditToFrameMs { get; set; }
    public long EditAllocatedBytes { get; set; }
    public long HistoryRetainedBytes { get; set; }
    public int HistoryDepth { get; set; }
    public double? MeasurementEditX { get; set; }
    public int? OverlappingPairs { get; set; }
    public string? Screenshot { get; set; }
    public string? PlacementDraftScreenshot { get; set; }
    public string? SavedProject { get; set; }
    public string? SaveOutcome { get; set; }
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && (!MeasurementRequired || MeasurementPerformed)
        && Checks.Count >= (MeasurementRequired ? 20 : 7) && Checks.Values.All(v => v);
}

internal static class NativeStage18Smoke
{
    public static NativeStage18Report Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeStage18Report>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Stage18Application.Options = options; Stage18Application.Completion = completion;
            try { AppBuilder.Configure<Stage18Application>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
    }
}

public sealed class Stage18Application : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeStage18Report> Completion = null!;
    private readonly NativeStage18Report _report = new();
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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "stage18-native");
        _report.MeasurementRequired = Environment.GetEnvironmentVariable("MAP_STAGE18_UI_ONLY") != "1";
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        if (_report.MeasurementRequired)
            _catalog.ImportSet([Path.Combine(Environment.CurrentDirectory, "src/Map.Assets/StarterAssets/sample-tree.png")],
                "Деревья", new(), DateTimeOffset.UnixEpoch);
        _window = new Window { Width = 1440, Height = 900, Title = "Map — Stage 18", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        var document = new MapDocument(DocumentId.New(), "Редактируемая область леса", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFF2EAD5"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFF2EAD5"),
                new ObjectLayer(LayerId.New(), "Деревья", true, false, 1, [])]);
        if (!_report.MeasurementRequired)
            document = MapProjectStore.Open(Environment.GetEnvironmentVariable("MAP_STAGE18_UI_FIXTURE")
                ?? throw new InvalidOperationException("MAP_STAGE18_UI_FIXTURE must name a previously verified area map.")).Document;
        OpenEditor(document);
        _window.Opened += async (_, _) =>
        {
            try { if (_report.MeasurementRequired) await Execute(); else await ExecuteUiCorrection(); }
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
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "editable-area.mapproj")), _prompts, new LocalLog(),
            assetCatalog: _catalog, exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "area-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }
    private async Task Execute()
    {
        await Settle(); _editor.ChooseGalleryVariant(_catalog.Sets[0].Id, null); await Settle();
        _editor.StampDensity = 3; _editor.SetStampScalePercent(45);
        await Click("StampModeFillArea"); await Click("StartEditableAreaButton");
        var before = _editor.Session.Document;
        Point(100, 100); Point(750, 100); Point(750, 600); Point(480, 450); Point(100, 600);
        var preview = _editor.PlacementPreviewObjects.ToArray();
        _report.Checks["draft-outside-document-history"] = ReferenceEquals(before, _editor.Session.Document)
            && _editor.Session.UndoDepth == 0 && preview.Length is > 0 and <= 64;
        await Click("FinishEditableAreaButton");
        Require(_editor.SelectedEditableArea is { Nodes.Length: 5 }, "Area controls did not create a concave polygon: " + _editor.StatusDetail);
        _report.Checks["new-area-has-spacing"] = _editor.SelectedEditableArea!.LayoutVersion == 1;
        _report.Checks["native-create-preview-equals-commit"] = preview.All(o => o == DocumentEditor.FindObject(_editor.Session.Document, o.Id));
        var created = _editor.Session.Document;
        Drag(750, 100, 820, 100); await Settle();
        _report.Checks["boundary-one-undo-stable-interior"] = _editor.Session.UndoDepth == 2
            && _editor.SelectedEditableArea!.Nodes[1].X == 820
            && _editor.SelectedEditableArea.Members.Where(m => DocumentEditor.FindObject(created, m.ObjectId) is not null)
                .All(m => ReferenceEquals(DocumentEditor.FindObject(created, m.ObjectId), DocumentEditor.FindObject(_editor.Session.Document, m.ObjectId)));
        await Number("EditableAreaDensityBox", "4");
        _report.Checks["density-control"] = _editor.SelectedEditableArea!.Density == 4;
        var area = _editor.SelectedEditableArea!;
        var ids = area.Members.Select(m => m.ObjectId).ToHashSet();
        var stamp = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Last(o => ids.Contains(o.Id));
        var member = area.Members.Single(m => m.ObjectId == stamp.Id);
        await Click("SelectAreaObjectButton"); Point(stamp.X + stamp.Width / 2, stamp.Y + stamp.Height / 2); await Settle();
        Require(_editor.Session.SelectedObjectId == stamp.Id, "Canvas did not select the owned tree");
        await Click("DetachAreaObjectButton");
        var detached = DocumentEditor.FindObject(_editor.Session.Document, stamp.Id)!;
        Drag(820, 100, 750, 100); Drag(750, 100, 820, 100); await Number("EditableAreaDensityBox", "3");
        _report.Checks["manual-exclusion-survives-boundary-and-density"] = ReferenceEquals(detached, DocumentEditor.FindObject(_editor.Session.Document, stamp.Id))
            && _editor.SelectedEditableArea!.Exclusions.Contains(member.Slot);
        var after = _editor.Session.Document; _editor.Undo(); _editor.Redo();
        _report.Checks["undo-redo-exact"] = ReferenceEquals(after, _editor.Session.Document);
        await Click("InsertEditableAreaNodeButton"); Point(100, 350); await Settle();
        _report.Checks["closing-edge-insert"] = _editor.SelectedEditableArea!.Nodes.Length == 6;
        await Click("DeleteEditableAreaNodeButton");
        _report.Checks["remove-node"] = _editor.SelectedEditableArea!.Nodes.Length == 5;
        await Click("StampCurrentAssetButton"); Point(200, 200);
        Require(_editor.IsCreatingEditableArea, "Current card did not start an area: " + _editor.ActiveTool + "/" + _editor.StampPlacementMode + "/" + _editor.StatusDetail);
        _editor.HandleEscape(); await Settle();
        _report.Checks["current-card-new-draft-cancel"] = !_editor.HasTransientEditorAction && _editor.EditableAreas.Count == 1;
        // The picker is a real ComboBox. Reopen and the initial catalogue choice are local adapters.
        var picker = Find<ComboBox>("EditableAreaPicker"); picker.SelectedIndex = 0; await Settle();
        Require(_editor.SelectedEditableArea is not null, "Area picker failed");
        await Number("EditableAreaSeedBox", "18018");
        await Click("SaveButton"); await Until(() => !_editor.Session.IsDirty);
        _report.SavedProject = Path.Combine(_workspace, "editable-area.mapproj");
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var savedArea = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
        _report.Checks["save-open-frozen-links-exclusion"] = savedArea.Seed == 18018 && savedArea.Exclusions.Contains(member.Slot)
            && DocumentEditor.FindObject(opened, member.ObjectId) == detached;
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
        await _catalog.FlushMetadataAsync();
        _catalog = new AssetCatalog(Path.Combine(_workspace, "empty-catalog-after-reopen"));
        OpenEditor(opened, _report.SavedProject); _editor.SelectEditableArea(savedArea.Id); await Settle();
        await Number("EditableAreaDensityBox", "4");
        var scaleControl = Find<Slider>("FutureStampScaleSlider"); scaleControl.BringIntoView(); await Settle();
        Require(scaleControl.IsEffectivelyVisible && scaleControl.IsEnabled, "Saved area scale control is unavailable");
        scaleControl.Value = 55; await Settle(); await Click("ApplyEditableAreaSettingsButton");
        _report.Checks["reopen-edit-without-catalog"] = _editor.SelectedEditableArea!.Density == 4
            && _editor.SelectedEditableArea.ScalePercent == 55 && _catalog.Sets.Count == 0;
        scaleControl.Value = 60; await Settle();
        await Click("StampCurrentAssetButton"); Point(120, 100); Point(330, 100); Point(330, 300);
        await Click("FinishEditableAreaButton");
        _report.Checks["second-area-from-frozen-card"] = _editor.EditableAreas.Count == 2
            && _editor.SelectedEditableArea!.ScalePercent == 60
            && _editor.EditableAreas.Single(a => a.Id == savedArea.Id).ScalePercent == 55;
        var secondId = _editor.SelectedEditableArea!.Id;
        var beforeBake = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        await Click("BakeEditableAreaButton");
        _report.Checks["bake-keeps-materialized-objects"] = _editor.EditableAreas.Count == 1
            && beforeBake.All(o => ReferenceEquals(o, DocumentEditor.FindObject(_editor.Session.Document, o.Id)));
        _editor.Undo(); _editor.SelectEditableArea(secondId); await Settle(); await Click("DeleteEditableAreaButton");
        _report.Checks["delete-protects-other-area-and-detached"] = _editor.EditableAreas.Count == 1
            && DocumentEditor.FindObject(_editor.Session.Document, stamp.Id) == detached;
        _editor.SelectEditableArea(savedArea.Id); await Settle();
        _report.OverlappingPairs = CountOverlaps();
        _report.Checks["visible-trees-do-not-overlap-after-all-edits"] = _report.OverlappingPairs == 0;
        await Click("SaveButton"); await Until(() => !_editor.Session.IsDirty);
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png");
        Find<Control>("EditableAreaSummary").BringIntoView(); await Settle(); SaveVisual(_report.Screenshot);
        await Click("ExportButton"); await Until(() => _editor.CanStartExport);
        var exportWindow = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.Single(w => w.Name == "ExportWorkflowWindow");
        var exportButton = exportWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StartExportButton");
        exportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => _editor.ExportResultPath is not null);
        using (var bitmap = SKBitmap.Decode(Path.Combine(_workspace, "area-export.png")))
            _report.Checks["native-full-size-export"] = bitmap is { Width: 1024, Height: 768 };
        _view.CloseAuxiliaryWindowsForSmoke();
        if (_report.MeasurementRequired) await MeasureExistingMap();
    }
    private async Task ExecuteUiCorrection()
    {
        var fixture = Environment.GetEnvironmentVariable("MAP_STAGE18_UI_FIXTURE")!;
        var sourceHash = SHA256.HashData(File.ReadAllBytes(fixture));
        var original = _editor.EditableAreas.Single();
        _editor.SelectEditableArea(original.Id); await Settle();
        if (original.LayoutVersion == 0)
        {
            var old = _editor.Session.Document;
            await Click("RemoveEditableAreaOverlapsButton");
            _report.Checks["legacy-area-upgrades-without-moving-survivors"] = _editor.SelectedEditableArea!.LayoutVersion == 1
                && _editor.SelectedEditableArea.Members.All(m => ReferenceEquals(DocumentEditor.FindObject(old, m.ObjectId),
                    DocumentEditor.FindObject(_editor.Session.Document, m.ObjectId)));
            var packed = _editor.Session.Document; _editor.Undo(); _editor.Redo();
            _report.Checks["spacing-upgrade-undo-redo"] = ReferenceEquals(packed, _editor.Session.Document);
        }
        await Number("EditableAreaDensityBox", "5");
        var units = Find<TextBlock>("EditableAreaDensityUnits"); units.BringIntoView(); await Settle();
        _report.Checks["density-caption-wraps"] = units.IsEffectivelyVisible && units.Bounds.Width > 0
            && units.TextWrapping == Avalonia.Media.TextWrapping.Wrap
            && units.Text!.StartsWith("До указанного числа объектов", StringComparison.Ordinal);
        _report.Checks["density-changes-without-catalog"] = _editor.SelectedEditableArea!.Density == 5 && _catalog.Sets.Count == 0;
        var scale = Find<Slider>("FutureStampScaleSlider"); scale.BringIntoView(); await Settle(); scale.Value = 65;
        await Click("StampCurrentAssetButton");
        var place = Find<Button>("FinishEditableAreaButton");
        var hint = Find<TextBlock>("EditableAreaPlacementHint");
        var draftStart = _editor.Session.Document; var draftDepth = _editor.Session.UndoDepth;
        _report.Checks["place-button-visible-before-outline"] = place.IsEffectivelyVisible && !place.IsEnabled
            && Avalonia.Automation.AutomationProperties.GetName(place) == "Разместить" && hint.Text!.Contains("3 вершины");
        Point(50, 50); Point(950, 50); await Settle();
        _report.Checks["place-button-waits-for-three-vertices"] = !place.IsEnabled && hint.Text!.Contains("2 из 3");
        Point(950, 720); Point(500, 520); Point(50, 720); await Settle();
        var preview = _editor.PlacementPreviewObjects.ToArray();
        _report.Checks["ready-outline-remains-preview-until-place"] = place.IsEnabled
            && ReferenceEquals(draftStart, _editor.Session.Document) && draftDepth == _editor.Session.UndoDepth && preview.Length > 0;
        var placePosition = place.TranslatePoint(default, _view);
        scale.BringIntoView(); await Settle();
        _report.Checks["place-button-stays-onscreen-when-settings-scroll"] = place.IsEffectivelyVisible
            && placePosition is { Y: > 0 } position && position.Y + place.Bounds.Height <= _view.Bounds.Height
            && place.TranslatePoint(default, _view) == placePosition && !place.GetVisualAncestors().OfType<ScrollViewer>().Any();
        Find<Control>("EditableAreaSummary").BringIntoView(); await Settle();
        _report.PlacementDraftScreenshot = Path.ChangeExtension(Options.OutputPath, ".draft.png"); SaveVisual(_report.PlacementDraftScreenshot);
        await Click("FinishEditableAreaButton");
        _report.Checks["new-area-uses-current-controls-and-frozen-images"] = _editor.EditableAreas.Count == 2
            && _editor.SelectedEditableArea!.ScalePercent == 65
            && _editor.EditableAreas.Single(a => a.Id == original.Id).ScalePercent == original.ScalePercent;
        _report.Checks["place-click-creates-visible-objects-in-one-undo-step"] = _editor.SelectedEditableArea is { MemberCount: > 0 }
            && _editor.Session.UndoDepth == draftDepth + 1 && !place.IsEnabled && hint.Text!.StartsWith("Размещено объектов:", StringComparison.Ordinal)
            && preview.All(o => o == DocumentEditor.FindObject(_editor.Session.Document, o.Id));
        var placed = _editor.Session.Document; _editor.Undo();
        var restored = ReferenceEquals(draftStart, _editor.Session.Document); _editor.Redo();
        _report.Checks["place-click-undo-redo-exact"] = restored && ReferenceEquals(placed, _editor.Session.Document);
        var saved = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
        await Click("StampCurrentAssetButton"); Point(200, 200); _editor.HandleEscape(); await Settle();
        _report.Checks["current-card-cancel-preserves-history"] = ReferenceEquals(saved, _editor.Session.Document)
            && depth == _editor.Session.UndoDepth && !_editor.HasTransientEditorAction;
        var picker = Find<ComboBox>("EditableAreaPicker"); picker.SelectedIndex = 0; await Settle();
        _report.Checks["area-picker-after-card-cancel"] = _editor.SelectedEditableArea!.Id == original.Id;
        Find<Control>("EditableAreaSummary").BringIntoView(); await Settle();
        _report.OverlappingPairs = CountOverlaps();
        _report.Checks["visible-trees-do-not-overlap"] = _report.OverlappingPairs == 0;
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png"); SaveVisual(_report.Screenshot);
        await Click("SaveButton"); await Until(() => _editor.SaveState != SaveState.Saving);
        _report.SaveOutcome = _editor.SaveState.ToString();
        if (_editor.SaveState == SaveState.SaveFailed)
            _report.Checks["save-resource-refusal-keeps-editor-open"] = _editor.StatusDetail.Contains("памят", StringComparison.OrdinalIgnoreCase)
                && ReferenceEquals(saved, _editor.Session.Document) && _editor.Session.IsDirty && depth == _editor.Session.UndoDepth;
        else _report.Checks["save-ui-completes"] = _editor.SaveState == SaveState.Saved && !_editor.Session.IsDirty;
        _report.Checks["fixture-read-only"] = sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture)));
    }
    private int CountOverlaps()
    {
        var pairs = 0;
        foreach (var layer in _editor.Session.Document.Layers.OfType<ObjectLayer>().Where(l => l.Visible))
        {
            var objects = layer.Objects.Where(o => o.Kind == SceneObjectKind.Stamp && o.Visible).ToArray();
            for (var i = 0; i < objects.Length; i++)
                for (var j = i + 1; j < objects.Length; j++)
                    if (objects[i].Sublayer == objects[j].Sublayer && PlacementAreaSampler.Overlaps(
                        SceneGeometry.BoundsOf(objects[i]), SceneGeometry.BoundsOf(objects[j]))) pairs++;
        }
        return pairs;
    }
    private async Task MeasureExistingMap()
    {
        var fixture = Environment.GetEnvironmentVariable("MAP_STAGE18_FIXTURE")
            ?? throw new InvalidOperationException("MAP_STAGE18_FIXTURE must name the existing ordinary map.");
        var sourceHash = SHA256.HashData(File.ReadAllBytes(fixture));
        var copy = Path.Combine(_workspace, "ordinary-map-copy.mapproj"); File.Copy(fixture, copy, overwrite: false);
        var document = await Task.Run(() => MapProjectStore.Open(copy).Document);
        _report.FixtureObjects = document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count);
        _report.MeasurementPerformed = true;
        Require(_report.FixtureObjects is >= 19000 and <= 21000, "Fixture is not the requested ordinary map");
        _view.Canvas.DisposeRenderCaches();
        _catalog.ImportSet([Path.Combine(Environment.CurrentDirectory, "src/Map.Assets/StarterAssets/sample-tree.png")], "Деревья", new(), DateTimeOffset.UnixEpoch);
        OpenEditor(document); _editor.AddObjectLayer();
        _report.Checks["measurement-new-layer-only-in-copy"] = document.FindLayer(_editor.ActiveLayer!.Id) is null;
        _editor.ChooseGalleryVariant(_catalog.Sets[0].Id, null);
        _editor.StampDensity = 3; _editor.SetStampScalePercent(55); _editor.Session.Viewport.Set(0, 0, .35); await Settle();
        await Click("StampModeFillArea"); await Click("StartEditableAreaButton");
        Point(300, 300); Point(1800, 300); Point(1800, 1600); Point(300, 1600);
        await Click("FinishEditableAreaButton"); await Frame();
        var before = _editor.Session.Document; var beforeArea = _editor.SelectedEditableArea!;
        var beforeIndex = DocumentRevisionIndex.For(before);
        // Choose one nonempty local edge edit before timing; no trial edits or repeated timing.
        var editX = new[] { 1750d, 1700, 1650, 1600, 1500 }.First(x =>
        {
            var nodes = beforeArea.Nodes.SetItem(1, beforeArea.Nodes[1] with { X = x });
            return beforeArea.Members.Any(m =>
            { var p = PlacementAreaPlanner.Anchor(beforeArea, m.Slot); return !PlacementAreaGeometry.Contains(nodes, p.X, p.Y); });
        });
        _report.MeasurementEditX = editX;
        using var process = Process.GetCurrentProcess(); process.Refresh(); _report.PrivateBeforeBytes = process.PrivateMemorySize64;
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var heap = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew(); Drag(1800, 300, editX, 300); _report.EditHandlerMs = watch.Elapsed.TotalMilliseconds;
        _report.EditAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        await Frame(); _report.EditToFrameMs = watch.Elapsed.TotalMilliseconds;
        process.Refresh(); _report.PrivateAfterFrameBytes = process.PrivateMemorySize64;
        _report.HistoryRetainedBytes = GC.GetTotalMemory(forceFullCollection: true) - heap;
        _report.ChangedObjects = _editor.LastAreaChangedObjects; _report.VisitedCells = _editor.LastAreaVisitedCells;
        _report.ChangedCells = _editor.LastAreaChangedCells; _report.HistoryDepth = _editor.Session.UndoDepth;
        _report.Checks["ordinary-map-unrelated-identities"] = document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects)
            .All(o => ReferenceEquals(o, DocumentEditor.FindObject(_editor.Session.Document, o.Id)));
        var newArea = _editor.SelectedEditableArea!;
        _report.SharedCellCount = beforeArea.Cells.Count(pair => newArea.Cells.TryGetValue(pair.Key, out var members) && members.Equals(pair.Value));
        _report.Checks["persistent-unchanged-cell-storage"] = _report.SharedCellCount > beforeArea.Cells.Count / 2;
        var edited = _editor.Session.Document; _editor.Undo(); var exactUndo = ReferenceEquals(before, _editor.Session.Document);
        _editor.Redo(); _report.Checks["ordinary-map-history-exact"] = exactUndo && ReferenceEquals(edited, _editor.Session.Document);
        _report.Checks["ordinary-map-local-edit"] = _report.VisitedCells < 80 && _report.ChangedObjects is > 0 and < 100;
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
