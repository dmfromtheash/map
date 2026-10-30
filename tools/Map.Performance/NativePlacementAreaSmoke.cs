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
using Map.Core.Resources;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

internal sealed class NativePlacementAreaReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false; scoped control events and production canvas router. File pickers and map reopen use local adapters. No system input. Timing ends at a rendered native canvas frame, not OS scan-out.";
    public Dictionary<string, bool> Checks { get; } = [];
    public int FixtureObjects { get; set; }
    public bool MeasurementPerformed { get; set; }
    public bool MeasurementOnly { get; set; }
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
    public int? LegacyFillCount { get; set; }
    public int? CompactFillCount { get; set; }
    public int? NaturalFillCount { get; set; }
    public int? TreeFillCount { get; set; }
    public int? GroupFillCount { get; set; }
    public int JoinedTreePairs { get; set; }
    public double MaximumTreeOverlapFraction { get; set; }
    public string? GroupScreenshot { get; set; }
    public string? ClearingsScreenshot { get; set; }
    public string? DiagonalRidgeScreenshot { get; set; }
    public Dictionary<int, int> DensityCounts { get; } = [];
    public string? TreeScreenshot { get; set; }
    public string? SparseScreenshot { get; set; }
    public int? ManuallyEditedSelectionCount { get; set; }
    public string? Screenshot { get; set; }
    public string? PlacementDraftScreenshot { get; set; }
    public string? SavedProject { get; set; }
    public string? SaveOutcome { get; set; }
    public int? AvailableMemoryCeilingMiB { get; set; }
    public long? PreviewManagedAllocationBytes { get; set; }
    public long? PlacementManagedAllocationBytes { get; set; }
    public long? PlacementRetainedManagedBytes { get; set; }
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && (!MeasurementRequired || MeasurementPerformed)
        && Checks.Count >= (MeasurementRequired && !MeasurementOnly ? 20 : 7) && Checks.Values.All(v => v);
}

internal static class NativePlacementAreaSmoke
{
    public static NativePlacementAreaReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativePlacementAreaReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            NativePlacementAreaApplication.Options = options; NativePlacementAreaApplication.Completion = completion;
            try { AppBuilder.Configure<NativePlacementAreaApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
    }
}

public sealed class NativePlacementAreaApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativePlacementAreaReport> Completion = null!;
    private readonly NativePlacementAreaReport _report = new();
    private string _workspace = "";
    private AssetCatalog _catalog = null!;
    private string? _densitySetId, _treeSetId;
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
        var densityAsset = Environment.GetEnvironmentVariable("MAP_STAGE18_DENSITY_ASSET");
        _report.MeasurementOnly = Environment.GetEnvironmentVariable("MAP_STAGE18_LOCAL_MEASUREMENT_ONLY") == "1";
        _report.MeasurementRequired = Environment.GetEnvironmentVariable("MAP_STAGE18_UI_ONLY") != "1" && densityAsset is null;
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        if (densityAsset is not null)
            _densitySetId = _catalog.ImportSet([densityAsset], "Горы", new(), DateTimeOffset.UnixEpoch).Id;
        else if (_report.MeasurementRequired)
            _catalog.ImportSet([OwnerMountainFixtures.Path(1)],
                "Деревья", new(), DateTimeOffset.UnixEpoch);
        if (Environment.GetEnvironmentVariable("MAP_STAGE18_NATURAL_CHECK") == "1")
            _treeSetId = _catalog.ImportSet([OwnerMountainFixtures.Path(1)],
                "Деревья", new(), DateTimeOffset.UnixEpoch).Id;
        if (_densitySetId is not null) _catalog.RenameSet(_densitySetId, "Горы");
        if (_treeSetId is not null) _catalog.RenameSet(_treeSetId, "Деревья");
        _window = new Window { Width = 1440, Height = 900, Title = "Map — Stage 18", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        var trees = densityAsset is null || Environment.GetEnvironmentVariable("MAP_STAGE18_NATURAL_CHECK") == "1";
        var document = new MapDocument(DocumentId.New(), trees ? "Редактируемая область леса" : "Редактируемая область гор", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", CurrentLook.PageColor),
            [CurrentLook.Page("Фон"),
                new ObjectLayer(LayerId.New(), trees ? "Деревья" : "Горы", true, false, 1, [])]);
        if (!_report.MeasurementRequired && densityAsset is null)
            document = MapProjectStore.Open(Environment.GetEnvironmentVariable("MAP_STAGE18_UI_FIXTURE")
                ?? throw new InvalidOperationException("MAP_STAGE18_UI_FIXTURE must name a previously verified area map.")).Document;
        OpenEditor(document);
        _window.Opened += async (_, _) =>
        {
            try
            {
                if (_report.MeasurementOnly) await MeasureExistingMap();
                else if (densityAsset is not null && Environment.GetEnvironmentVariable("MAP_STAGE18_NATURAL_CHECK") == "1")
                    await ExecuteNaturalCorrection(densityAsset);
                else if (densityAsset is not null && Environment.GetEnvironmentVariable("MAP_STAGE18_SELECTION_CHECK") == "1")
                    await ExecuteSelectionCorrection(densityAsset);
                else if (densityAsset is not null) await ExecuteDensityCorrection(densityAsset);
                else if (_report.MeasurementRequired) await Execute(); else await ExecuteUiCorrection();
            }
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
        WorkingMemoryBudget? memory = null;
        if (Environment.GetEnvironmentVariable("MAP_STAGE18_SMALL_MEMORY") == "1")
        {
            _report.AvailableMemoryCeilingMiB = 1024;
            memory = new(() =>
            {
                var live = WorkingMemoryBudget.Shared.Snapshot;
                return live with { AvailableBytes = Math.Min(live.AvailableBytes, 1024 * WorkingMemoryBudget.MiB) };
            });
        }
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "editable-area.mapproj")), _prompts, new LocalLog(),
            assetCatalog: _catalog, exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "area-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false), memory: memory);
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }
    private async Task Execute()
    {
        await Settle(); _editor.ChooseGalleryVariant(_catalog.Sets[0].Id, null); await Settle();
        _editor.StampDensity = 3; _editor.SetStampScalePercent(45);
        await Click("StampModeFillArea");
        // A new area is a v4 «Массивы» fill with «Красота» on: density is a percentage, and a soft
        // edge refills the whole edge band when a vertex moves (Core: A_moved_vertex_with_a_soft_edge…).
        // The stable-interior promise below holds without the soft edge, so it is switched off first.
        _editor.SetEditableAreaSoftEdge(false);
        await Click("StartEditableAreaButton");
        var before = _editor.Session.Document;
        Point(100, 100); Point(750, 100); Point(750, 600); Point(480, 450); Point(100, 600); await Settle();
        var preview = _editor.PlacementPreviewObjects.ToArray();
        _report.Checks["draft-outside-document-history"] = ReferenceEquals(before, _editor.Session.Document)
            && _editor.Session.UndoDepth == 0 && preview.Length is > 0 and <= 64;
        await Click("FinishEditableAreaButton");
        Require(_editor.SelectedEditableArea is { Nodes.Length: 5 }, "Area controls did not create a concave polygon: " + _editor.StatusDetail);
        _report.Checks["new-area-has-spacing"] = _editor.SelectedEditableArea!.LayoutVersion == 4;
        _report.Checks["native-create-preview-equals-commit"] = preview.All(o => o == DocumentEditor.FindObject(_editor.Session.Document, o.Id));
        var created = _editor.Session.Document;
        Drag(750, 100, 820, 100); await Settle();
        _report.Checks["boundary-one-undo-stable-interior"] = _editor.Session.UndoDepth == 2
            && _editor.SelectedEditableArea!.Nodes[1].X == 820
            && _editor.SelectedEditableArea.Members.Where(m => DocumentEditor.FindObject(created, m.ObjectId) is not null)
                .All(m => ReferenceEquals(DocumentEditor.FindObject(created, m.ObjectId), DocumentEditor.FindObject(_editor.Session.Document, m.ObjectId)));
        await Number("EditableAreaDensityBox", "80");
        _report.Checks["density-control"] = _editor.SelectedEditableArea!.Density == 80;
        var area = _editor.SelectedEditableArea!;
        var ids = area.Members.Select(m => m.ObjectId).ToHashSet();
        var stamp = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Last(o => ids.Contains(o.Id));
        var member = area.Members.Single(m => m.ObjectId == stamp.Id);
        await Click("SelectAreaObjectButton"); Point(stamp.X + stamp.Width / 2, stamp.Y + stamp.Height / 2); await Settle();
        Require(_editor.Session.SelectedObjectId == stamp.Id, "Canvas did not select the owned tree");
        await Click("DetachAreaObjectButton");
        var detached = DocumentEditor.FindObject(_editor.Session.Document, stamp.Id)!;
        Drag(820, 100, 750, 100); Drag(750, 100, 820, 100); await Number("EditableAreaDensityBox", "70");
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
        await Number("EditableAreaDensityBox", "80");
        var scaleControl = Find<Slider>("FutureStampScaleSlider"); scaleControl.BringIntoView(); await Settle();
        Require(scaleControl.IsEffectivelyVisible && scaleControl.IsEnabled, "Saved area scale control is unavailable");
        await Number("FutureStampScaleSliderBox", "55"); await Click("ApplyEditableAreaSettingsButton");
        _report.Checks["reopen-edit-without-catalog"] = _editor.SelectedEditableArea!.Density == 80
            && _editor.SelectedEditableArea.ScalePercent == 55 && _catalog.Sets.Count == 0;
        await Number("FutureStampScaleSliderBox", "60");
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
        if (original.LayoutVersion < 4)
        {
            var old = _editor.Session.Document;
            await Click("NaturalEditableAreaButton");
            await _editor.PendingGeneration; _editor.ApplyGeneration(); await Settle();
            _report.Checks["legacy-area-upgrades-explicitly"] = _editor.SelectedEditableArea!.LayoutVersion == 4 && CountOverlaps() == 0;
            var packed = _editor.Session.Document; _editor.Undo(); var exact = ReferenceEquals(old, _editor.Session.Document); _editor.Redo();
            _report.Checks["spacing-upgrade-undo-redo"] = exact && ReferenceEquals(packed, _editor.Session.Document);
        }
        await Number("EditableAreaDensityBox", "5");
        var units = Find<TextBlock>("EditableAreaDensityUnits"); units.BringIntoView(); await Settle();
        _report.Checks["density-caption-wraps"] = units.IsEffectivelyVisible && units.Bounds.Width > 0
            && units.TextWrapping == Avalonia.Media.TextWrapping.Wrap
            && units.Text!.StartsWith("0–100%", StringComparison.Ordinal);
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
    private async Task ExecuteNaturalCorrection(string assetPath)
    {
        var treePath = OwnerMountainFixtures.Path(1);
        var treeHash = SHA256.HashData(File.ReadAllBytes(treePath));
        await Settle();
        _editor.ChooseGalleryVariant(_treeSetId!, null); await Settle();
        await Click("StampModeFillArea"); Find<Slider>("FutureStampScaleSlider").Value = 25; await Settle();
        await Click("StartEditableAreaButton"); await Number("EditableAreaDensityBox", "100");
        Point(90, 100); Point(950, 100); Point(950, 670); Point(590, 580); Point(330, 690); Point(90, 560);
        await Number("EditableAreaSeedBox", "18");
        var draft = _editor.DisplayedEditableArea!; var untouched = _editor.Session.Document;
        await Click("AnotherEditableAreaArrangementButton");
        _report.Checks["draft-another-pattern-keeps-document-and-history"] = _editor.DisplayedEditableArea!.Seed != draft.Seed
            && ReferenceEquals(untouched, _editor.Session.Document) && _editor.Session.UndoDepth == 0;
        await Number("EditableAreaSeedBox", "18"); var preview = _editor.PlacementPreviewObjects.ToArray();
        await _editor.PendingEditableAreaCount; await Settle();
        var predicted = _editor.PlannedEditableAreaCount;
        _report.Checks["draft-shows-exact-count-in-pinned-panel"] = predicted > 0
            && Find<TextBlock>("EditableAreaResult").Text!.Contains($"{predicted:N0}");
        await Click("FinishEditableAreaButton"); await Frame();
        var area = _editor.SelectedEditableArea!;
        _report.TreeFillCount = area.MemberCount;
        var trees = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToArray();
        for (var i = 0; i < trees.Length; i++)
            for (var j = i + 1; j < trees.Length; j++)
            {
                var overlap = PlacementAreaSampler.OverlapFraction(PlacementAreaSampler.VisibleBounds(area, trees[i]), PlacementAreaSampler.VisibleBounds(area, trees[j]));
                if (overlap > 0) _report.JoinedTreePairs++;
                _report.MaximumTreeOverlapFraction = Math.Max(_report.MaximumTreeOverlapFraction, overlap);
            }
        _report.Checks["forest-edges-join-with-protected-silhouettes"] = area.JoinEdges && area.ContentMasks.Count > 0
            && _report.JoinedTreePairs > 0 && CountOverlaps() == 0;
        _report.Checks["forest-draws-from-back-to-front"] = trees.Select(o => PlacementAreaSampler.VisibleBounds(area, o).Bottom)
            .SequenceEqual(trees.Select(o => PlacementAreaSampler.VisibleBounds(area, o).Bottom).Order());
        _report.Checks["tree-count-matches-placement"] = predicted == area.MemberCount;
        _report.Checks["trees-use-general-natural-composition"] = area.LayoutVersion == 4 && area.MemberCount > 10 && CountOverlaps() == 0;
        _report.Checks["tree-preview-is-exact"] = preview.Length > 0 && preview.All(o => o == DocumentEditor.FindObject(_editor.Session.Document, o.Id));
        _report.TreeScreenshot = Path.ChangeExtension(Options.OutputPath, ".trees.png"); SaveVisual(_report.TreeScreenshot);
        var treeDocument = _editor.Session.Document;
        Find<ComboBox>("EditableAreaPatternBox").SelectedIndex = 2; await Settle(); await Frame();
        _report.GroupFillCount = _editor.SelectedEditableArea!.MemberCount;
        _report.Checks["group-pattern-changes-real-arrangement"] = _editor.EditableAreaPattern == PlacementAreaPattern.Groups
            && _report.GroupFillCount > 0 && !ReferenceEquals(treeDocument, _editor.Session.Document) && CountOverlaps() == 0;
        _report.GroupScreenshot = Path.ChangeExtension(Options.OutputPath, ".groups.png"); SaveVisual(_report.GroupScreenshot);
        var groupsDocument = _editor.Session.Document;
        Find<ComboBox>("EditableAreaPatternBox").SelectedIndex = 3; await Settle(); await Frame();
        var clearing = _editor.SelectedEditableArea!;
        _report.Checks["clearings-pattern-leaves-an-open-centre"] = clearing.Pattern == PlacementAreaPattern.Clearings
            && clearing.MemberCount > 0 && CountOverlaps() == 0 && clearing.Members.All(m =>
            {
                var point = PlacementAreaPlanner.Anchor(clearing, m.Slot);
                return Math.Abs(point.X - clearing.CompositionOrigin.X) > clearing.Request.BaseWidth * .55
                    || Math.Abs(point.Y - clearing.CompositionOrigin.Y) > clearing.Request.BaseWidth * .55;
            });
        _report.ClearingsScreenshot = Path.ChangeExtension(Options.OutputPath, ".clearings.png"); SaveVisual(_report.ClearingsScreenshot);
        _editor.Undo();
        _report.Checks["clearings-pattern-undo-is-exact"] = ReferenceEquals(groupsDocument, _editor.Session.Document);
        _editor.Undo();
        _report.Checks["pattern-change-has-exact-undo"] = ReferenceEquals(treeDocument, _editor.Session.Document);
        _report.Checks["starter-tree-read-only"] = treeHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(treePath)));
        _view.Canvas.DisposeRenderCaches();
        OpenEditor(new MapDocument(DocumentId.New(), "Естественная расстановка гор", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", CurrentLook.PageColor),
            [CurrentLook.Page("Фон"),
                new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [])]));
        await ExecuteSelectionCorrection(assetPath);
        _view.Canvas.DisposeRenderCaches();
        OpenEditor(new MapDocument(DocumentId.New(), "Хребет по диагональной области", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", CurrentLook.PageColor),
            [CurrentLook.Page("Фон"),
                new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [])]));
        await Settle(); _editor.ChooseGalleryVariant(_densitySetId!, null); await Settle();
        await Click("StampModeFillArea"); Find<Slider>("FutureStampScaleSlider").Value = 40; await Settle();
        await Click("StartEditableAreaButton"); await Number("EditableAreaDensityBox", "100");
        Point(120, 570); Point(290, 680); Point(890, 190); Point(710, 75);
        Find<ComboBox>("EditableAreaPatternBox").SelectedIndex = 1; await Settle();
        await Number("EditableAreaSeedBox", "18"); await _editor.PendingEditableAreaCount;
        var diagonalCount = _editor.PlannedEditableAreaCount;
        await Click("FinishEditableAreaButton"); await Frame();
        _report.Checks["ridge-follows-a-diagonal-outline"] = _editor.SelectedEditableArea is { CompositionAngleDegrees: < -20 }
            && diagonalCount == _editor.SelectedEditableArea.MemberCount && CountOverlaps() == 0;
        _report.DiagonalRidgeScreenshot = Path.ChangeExtension(Options.OutputPath, ".diagonal.png"); SaveVisual(_report.DiagonalRidgeScreenshot);
    }

    private async Task ExecuteSelectionCorrection(string assetPath)
    {
        var sourceHash = SHA256.HashData(File.ReadAllBytes(assetPath));
        await Settle(); _editor.ChooseGalleryVariant(_densitySetId!, null); await Settle();
        await Click("StampModeFillArea");
        Find<Slider>("FutureStampScaleSlider").Value = 32; await Settle();
        await Click("StartEditableAreaButton");
        await Number("EditableAreaDensityBox", "100");
        Point(90, 110); Point(950, 110); Point(520, 650);
        Find<ComboBox>("EditableAreaPatternBox").SelectedIndex = 1; await Settle();
        await Number("EditableAreaSeedBox", "-184929685");
        var draft = _editor.DisplayedEditableArea!;
        var preview = _editor.PlacementPreviewObjects.ToArray();
        var legacy = draft with { LayoutVersion = 2, Density = 20 };
        var legacySampler = new PlacementAreaSampler(legacy);
        _report.LegacyFillCount = PlacementAreaGeometry.Coverage(legacy.Nodes).Sum(span =>
            Enumerable.Range(span.MinX, span.MaxX - span.MinX + 1).Sum(x => legacySampler.Slots(x, span.Y).Count()));
        await _editor.PendingEditableAreaCount; var predicted = _editor.PlannedEditableAreaCount;
        await Click("FinishEditableAreaButton"); await Frame();
        var area = _editor.SelectedEditableArea!;
        var layerId = DocumentEditor.FindLayerOf(_editor.Session.Document, area.Members.First().ObjectId)!.Id;
        _report.NaturalFillCount = area.MemberCount;
        _report.Checks["ridge-count-matches-placement"] = predicted == area.MemberCount && area.Pattern == PlacementAreaPattern.Ridge;
        _report.Checks["new-fill-uses-natural-groups"] = area.LayoutVersion == 4 && area.MemberCount > 3;
        _report.Checks["natural-preview-matches-placed-stamps"] = preview.Length > 0
            && preview.All(o => o == DocumentEditor.FindObject(_editor.Session.Document, o.Id));
        _report.OverlappingPairs = CountOverlaps();
        _report.Checks["natural-fill-has-no-forbidden-overlap"] = _report.OverlappingPairs == 0;
        _report.PlacementDraftScreenshot = Path.ChangeExtension(Options.OutputPath, ".packing.png"); SaveVisual(_report.PlacementDraftScreenshot);

        // Exercise the explicit upgrade control too; source files are never rewritten on open.
        _editor.Session.Execute(new PlacementAreaCommand(layerId, area.Id, area with { LayoutVersion = 2, Density = 20 }, "Legacy fixture"));
        _editor.SelectEditableArea(area.Id); await Settle();
        var beforeUpgrade = _editor.Session.Document;
        await Click("NaturalEditableAreaButton");
        await _editor.PendingGeneration; _editor.ApplyGeneration(); await Settle();
        var upgraded = _editor.Session.Document;
        _report.Checks["legacy-fill-upgrades-through-visible-button"] = _editor.SelectedEditableArea is { LayoutVersion: 4 }
            && _editor.SelectedEditableArea.MemberCount == _report.NaturalFillCount;
        _editor.Undo(); var undoUpgrade = ReferenceEquals(beforeUpgrade, _editor.Session.Document); _editor.Redo();
        _report.Checks["repack-undo-redo-exact"] = undoUpgrade && ReferenceEquals(upgraded, _editor.Session.Document);

        await Number("EditableAreaDensityBox", "0"); var previousDensity = _editor.Session.Document;
        var densityWorks = _editor.SelectedEditableArea!.MemberCount == 0; var stable = true;
        foreach (var value in new[] { 20, 50, 80, 100 })
        {
            var previousArea = _editor.SelectedEditableArea!;
            await Number("EditableAreaDensityBox", value.ToString()); await Frame();
            var current = _editor.SelectedEditableArea!; _report.DensityCounts[value] = current.MemberCount;
            densityWorks &= current.MemberCount > previousArea.MemberCount && CountOverlaps() == 0;
            stable &= current.CompositionOrigin == area.CompositionOrigin && previousArea.Members.All(m =>
                ReferenceEquals(DocumentEditor.FindObject(previousDensity, m.ObjectId), DocumentEditor.FindObject(_editor.Session.Document, m.ObjectId)));
            previousDensity = _editor.Session.Document;
            if (value == 20) { _report.SparseScreenshot = Path.ChangeExtension(Options.OutputPath, ".density20.png"); SaveVisual(_report.SparseScreenshot); }
        }
        _report.Checks["density-adds-objects-at-20-50-80-100-percent"] = densityWorks;
        _report.Checks["density-keeps-existing-positions-heights-and-origin"] = stable;
        var beforeAnother = _editor.Session.Document; var anotherDepth = _editor.Session.UndoDepth;
        var oldSeed = _editor.SelectedEditableArea!.Seed;
        await Click("AnotherEditableAreaArrangementButton"); await _editor.PendingGeneration; _editor.ApplyGeneration(); await Settle(); var another = _editor.Session.Document;
        _report.Checks["another-pattern-is-one-real-edit"] = _editor.SelectedEditableArea!.Seed != oldSeed && _editor.Session.UndoDepth == anotherDepth + 1;
        _editor.Undo(); var anotherUndo = ReferenceEquals(beforeAnother, _editor.Session.Document); _editor.Redo();
        _report.Checks["another-pattern-undo-redo-exact"] = anotherUndo && ReferenceEquals(another, _editor.Session.Document);
        _editor.Undo(); await Settle();
        var varied = _editor.Session.Document;
        await Number("EditableAreaSizeVariationBox", "0");
        _report.Checks["size-variation-can-be-disabled"] = _editor.SelectedEditableArea!.Members.All(m =>
            Math.Abs(DocumentEditor.FindObject(_editor.Session.Document, m.ObjectId)!.Width - area.Request.BaseWidth) < .00001);
        _editor.Undo(); _report.Checks["size-variation-undo-exact"] = ReferenceEquals(varied, _editor.Session.Document);
        await Settle();

        area = _editor.SelectedEditableArea!;
        var items = area.Members.Select(m => DocumentEditor.FindObject(_editor.Session.Document, m.ObjectId)!).ToArray();
        var bounds = MultiSelectionGeometry.CombinedBounds(items)!.Value;
        _editor.ActiveTool = EditorTool.Select; _editor.Session.ClearSelection(); await Settle();
        Drag(bounds.Left - 20, bounds.Top - 20, (bounds.Left + bounds.Right) / 2, bounds.Bottom + 20); await Settle();
        var selection = _editor.Session.SelectedObjects.ToArray();
        Require(selection.Length >= 2 && selection.Length < items.Length, "Marquee must select part of the fill");
        _report.ManuallyEditedSelectionCount = selection.Length;
        _report.Checks["canvas-marquee-selects-multiple-area-members"] = selection.All(o => PlacementAreaOwnership.Owner(_editor.Session.Document, o.Id)?.Id == area.Id);
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".selection.png"); SaveVisual(_report.Screenshot);
        var beforeMove = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
        var grab = selection[0]; var x = grab.X + grab.Width / 2; var y = grab.Y + grab.Height / 2;
        Point(x, y);
        _report.Checks["selection-click-does-not-detach-or-add-history"] = ReferenceEquals(beforeMove, _editor.Session.Document)
            && _editor.Session.UndoDepth == depth;
        Drag(x, y, x + 35, y + 20); await Frame();
        var moved = _editor.Session.Document;
        _report.Checks["whole-marquee-selection-drags-as-one-step"] = _editor.Session.UndoDepth == depth + 1
            && selection.All(o => DocumentEditor.FindObject(moved, o.Id) is { } actual
                && Math.Abs(actual.X - o.X - 35) <= .5 && Math.Abs(actual.Y - o.Y - 20) <= .5);
        _report.Checks["drag-detaches-only-selected-stamps"] = selection.All(o => PlacementAreaOwnership.Owner(moved, o.Id) is null)
            && ((ObjectLayer)moved.FindLayer(layerId)!).PlacementAreas.Single().MemberCount == area.MemberCount - selection.Length;
        _editor.Undo(); var undoMove = ReferenceEquals(beforeMove, _editor.Session.Document); _editor.Redo();
        _report.Checks["selection-move-undo-redo-exact"] = undoMove && ReferenceEquals(moved, _editor.Session.Document);
        _editor.Undo(); await Settle();

        var beforeDelete = _editor.Session.Document; depth = _editor.Session.UndoDepth;
        await Click("MultiDeleteButton"); await Frame();
        var deleted = _editor.Session.Document;
        var deletedArea = ((ObjectLayer)deleted.FindLayer(layerId)!).PlacementAreas.Single();
        var selectedIds = selection.Select(o => o.Id).ToHashSet();
        var deletedSlots = area.Members.Where(m => selectedIds.Contains(m.ObjectId)).Select(m => m.Slot).ToArray();
        _report.Checks["delete-button-removes-entire-selection-in-one-step"] = _editor.Session.UndoDepth == depth + 1
            && selection.All(o => DocumentEditor.FindObject(deleted, o.Id) is null) && _editor.SelectionCount == 0;
        _report.Checks["delete-keeps-unselected-objects-and-records-exclusions"] = deletedSlots.All(deletedArea.Exclusions.Contains)
            && deletedArea.Members.All(m => ReferenceEquals(DocumentEditor.FindObject(beforeDelete, m.ObjectId), DocumentEditor.FindObject(deleted, m.ObjectId)));
        _editor.Undo(); var undoDelete = ReferenceEquals(beforeDelete, _editor.Session.Document); _editor.Redo();
        _report.Checks["selection-delete-undo-redo-exact"] = undoDelete && ReferenceEquals(deleted, _editor.Session.Document);
        _editor.SelectEditableArea(area.Id); _editor.ActiveTool = EditorTool.Stamp; await Settle();
        await Number("EditableAreaDensityBox", "80");
        var final = _editor.Session.Document;
        _report.Checks["manual-deletions-survive-density-change"] = selection.All(o => DocumentEditor.FindObject(final, o.Id) is null);
        await Click("SaveButton"); await Until(() => _editor.SaveState != SaveState.Saving);
        _report.SaveOutcome = _editor.SaveState.ToString();
        _report.SavedProject = Path.Combine(_workspace, "editable-area.mapproj");
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var savedArea = ((ObjectLayer)opened.FindLayer(layerId)!).PlacementAreas.Single();
        _report.Checks["natural-fill-and-manual-exclusions-reopen"] = _editor.SaveState == SaveState.Saved
            && savedArea.LayoutVersion == 4 && savedArea.CompositionOrigin == area.CompositionOrigin
            && savedArea.SizeVariationPercent == area.SizeVariationPercent && deletedSlots.All(savedArea.Exclusions.Contains)
            && selection.All(o => DocumentEditor.FindObject(opened, o.Id) is null);
        _report.Checks["owner-asset-read-only"] = sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(assetPath)));
    }

    private async Task ExecuteDensityCorrection(string assetPath)
    {
        var sourceHash = SHA256.HashData(File.ReadAllBytes(assetPath));
        await Settle(); _editor.ChooseGalleryVariant(_catalog.Sets[0].Id, null); await Settle();
        await Click("StampModeFillArea");
        var scale = Find<Slider>("FutureStampScaleSlider"); scale.BringIntoView(); await Settle(); scale.Value = 129;
        _editor.SetStampRotationDegrees(359); _editor.SetStampSublayer(-1); _editor.SetStampFlip(horizontal: true);
        await Click("StartEditableAreaButton");
        await Number("EditableAreaDensityBox", "20");
        var original = _editor.Session.Document;
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var previewAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Point(100, 100); Point(900, 100); Point(500, 670); await Settle();
        await Number("EditableAreaSeedBox", "-184929685");
        _report.PreviewManagedAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - previewAllocatedBefore;
        var draft = _editor.DisplayedEditableArea!;
        _report.Checks["owner-mountain-asset-and-settings"] = draft.Variants.Single().Sha256 == Convert.ToHexString(sourceHash).ToLowerInvariant()
            && draft.ScalePercent == 129 && draft.RotationDegrees == 359 && draft.Sublayer == -1
            && draft.Style.FlipHorizontal && draft.Density == 20 && draft.Seed == -184929685;
        var place = Find<Button>("FinishEditableAreaButton");
        _report.Checks["density20-large-mountains-preview-and-place-ready"] = place.IsEnabled && _editor.PlacementPreviewObjects.Count > 0;
        _report.Checks["preview-memory-reservation-is-bounded"] = PlacementAreaSampler.EstimateBytes(draft,
            PlacementAreaGeometry.Coverage(draft.Nodes)) < 32 * 1024 * 1024;
        _report.Checks["preview-does-not-change-map-or-history"] = ReferenceEquals(original, _editor.Session.Document) && _editor.Session.UndoDepth == 0;
        Find<Control>("EditableAreaSummary").BringIntoView(); await Settle();
        _report.PlacementDraftScreenshot = Path.ChangeExtension(Options.OutputPath, ".draft.png"); SaveVisual(_report.PlacementDraftScreenshot);
        await Number("EditableAreaDensityBox", "40");
        var preview = _editor.PlacementPreviewObjects.ToArray();
        _report.Checks["density40-large-mountains-preview-and-place-ready"] = place.IsEnabled && preview.Length > 0;
        var placementAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        await Click("FinishEditableAreaButton");
        _report.PlacementManagedAllocationBytes = GC.GetAllocatedBytesForCurrentThread() - placementAllocatedBefore;
        _report.PlacementRetainedManagedBytes = GC.GetTotalMemory(forceFullCollection: true) - managedBefore;
        _report.Checks["mountains-placed-in-one-undo-step"] = _editor.SelectedEditableArea is { MemberCount: > 0, Density: 40 }
            && _editor.Session.UndoDepth == 1 && preview.All(o => o == DocumentEditor.FindObject(_editor.Session.Document, o.Id));
        var placed = _editor.Session.Document; _editor.Undo();
        var undone = ReferenceEquals(original, _editor.Session.Document); _editor.Redo();
        _report.Checks["mountains-undo-redo-exact"] = undone && ReferenceEquals(placed, _editor.Session.Document);
        _report.OverlappingPairs = CountOverlaps();
        _report.Checks["mountains-do-not-overlap"] = _report.OverlappingPairs == 0;
        await Click("SaveButton"); await Until(() => _editor.SaveState != SaveState.Saving);
        _report.SaveOutcome = _editor.SaveState.ToString();
        _report.Checks["mountains-save-succeeds"] = _editor.SaveState == SaveState.Saved && !_editor.Session.IsDirty;
        _report.SavedProject = Path.Combine(_workspace, "editable-area.mapproj");
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var saved = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.PlacementAreas).Single();
        _report.Checks["mountains-reopen-validates-same-layout"] = saved.Density == 40 && saved.MemberCount == _editor.SelectedEditableArea!.MemberCount
            && saved.Members.All(m => DocumentEditor.FindObject(opened, m.ObjectId) == DocumentEditor.FindObject(placed, m.ObjectId));
        Find<Control>("EditableAreaSummary").BringIntoView(); await Settle();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png"); SaveVisual(_report.Screenshot);
        _report.Checks["owner-asset-read-only"] = sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(assetPath)));
        if (_report.AvailableMemoryCeilingMiB.HasValue) await CheckPngCacheLifetime();
    }

    private async Task CheckPngCacheLifetime()
    {
        var path = Path.Combine(_workspace, "small-view.png");
        File.Copy(OwnerMountainFixtures.Path(1), path, overwrite: true);
        var original = SHA256.HashData(File.ReadAllBytes(path));
        var cache = Path.Combine(_workspace, "png-cache");
        var viewer = new PngMapWindow(cache) { ShowActivated = false, ShowInTaskbar = false, Position = _window.Position };
        string tile;
        try
        {
            viewer.Show(_window); await viewer.OpenImageAsync(path); await Settle();
            Require(viewer.Pyramid is not null, viewer.StatusText);
            await Until(() => viewer.ImageView.CachedPixelBytes > 0);
            tile = viewer.Pyramid!.CheckTile(0, 0, 0);
            _report.Checks["png-native-view-lease-protects-cache"] = PngViewCache.TrimUnused(cache, 0).InUse && File.Exists(tile);
        }
        finally { viewer.Close(); }
        await Until(() => { PngViewCache.TrimUnused(cache, 0); return !File.Exists(tile); });
        _report.Checks["png-native-close-releases-only-derived-cache"] = original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)));
    }
    private int CountOverlaps()
    {
        var pairs = 0;
        foreach (var layer in _editor.Session.Document.Layers.OfType<ObjectLayer>().Where(l => l.Visible))
        {
            var objects = layer.Objects.Where(o => o.Kind == SceneObjectKind.Stamp && o.Visible).ToArray();
            for (var i = 0; i < objects.Length; i++)
                for (var j = i + 1; j < objects.Length; j++)
                    if (objects[i].Sublayer == objects[j].Sublayer)
                    {
                        var owner = PlacementAreaOwnership.Owner(_editor.Session.Document, objects[i].Id);
                        var sameArea = owner is not null && PlacementAreaOwnership.Owner(_editor.Session.Document, objects[j].Id)?.Id == owner.Id;
                        if (sameArea ? PlacementAreaSampler.Conflicts(owner!, objects[i], objects[j])
                            : PlacementAreaSampler.Overlaps(Bounds(objects[i]), Bounds(objects[j]))) pairs++;
                    }
            WorldBounds Bounds(SceneObject item)
            {
                var area = layer.PlacementAreas.FirstOrDefault(a => a.ContentBounds.ContainsKey(item.Stamp!.AssetSha256));
                return area is null ? SceneGeometry.BoundsOf(item) : PlacementAreaSampler.VisibleBounds(area, item);
            }
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
        var measurementSet = _catalog.ImportSet([OwnerMountainFixtures.Path(1)], "Деревья", new(), DateTimeOffset.UnixEpoch).Id;
        OpenEditor(document); _editor.AddObjectLayer();
        _report.Checks["measurement-new-layer-only-in-copy"] = document.FindLayer(_editor.ActiveLayer!.Id) is null;
        _editor.ChooseGalleryVariant(measurementSet, null);
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
    private T Find<T>(string name) where T : Control
    {
        NativeEditorScenarioApplication.RevealTab(_view, name);
        if (_view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } found) return found;
        // The tool's own controls (FutureStamp…) live in the «Инструмент» half of the context area,
        // which a selected area hides behind «Объект» (2026-10-19): switch the way a user would.
        if (_view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ContextScopeTool") is { } scope)
        {
            scope.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); _view.UpdateLayout();
            NativeEditorScenarioApplication.RevealTab(_view, name);
        }
        if (name.StartsWith("FutureStamp", StringComparison.Ordinal)
            && _view.GetVisualDescendants().OfType<T>().All(c => c.Name != name))
        {
            // The tool page is the asset tool's; the rail has no button for it (the catalog arms
            // it), so the smoke activates it the way the catalog does, then its half of the area.
            _editor.ActiveTool = EditorTool.Stamp; _view.UpdateLayout();
            _view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "ContextScopeTool")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            _view.UpdateLayout();
            foreach (var folded in _view.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray()) folded.IsExpanded = true;
            _view.UpdateLayout();
        }
        return _view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new InvalidOperationException($"{name} is not in the panel");
    }
    private async Task Click(string name)
    { var button = Find<Button>(name); button.BringIntoView(); await Settle(); Require(button.IsEffectivelyVisible && button.IsEnabled, name + " is unavailable");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle(); }
    private async Task Number(string name, string value)
    { var box = Find<TextBox>(name); box.BringIntoView(); await Settle(); box.Text = value;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); await Settle(); }
    private void SaveVisual(string path)
    { using var bitmap = new RenderTargetBitmap(new PixelSize(1440, 900)); bitmap.Render(_view);
        using var file = File.Create(path); bitmap.Save(file, PngBitmapEncoderOptions.Default);
        if (Environment.GetEnvironmentVariable("MAP_STAGE18_NATURAL_CHECK") == "1")
        {
            using var canvas = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_view.Canvas.Bounds.Width), (int)Math.Ceiling(_view.Canvas.Bounds.Height)));
            canvas.Render(_view.Canvas);
            using var canvasFile = File.Create(Path.ChangeExtension(path, ".canvas.png"));
            canvas.Save(canvasFile, PngBitmapEncoderOptions.Default);
        }
    }
    private async Task Settle()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(50);
        // Slow area previews and recalculations finish off the UI thread; wait for them to land.
        if (_editor is { } editor)
            await Task.WhenAll(editor.PendingEditableAreaWork, editor.PendingEditableAreaPreview).WaitAsync(TimeSpan.FromSeconds(60));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }
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
    }
}
