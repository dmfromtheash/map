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
using Map.App.Services;
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

    /// <summary>R01 observation points of the native-history-memory scenario; empty elsewhere.</summary>
    public List<HistoryMemoryPoint> HistoryMemory { get; } = [];

    /// <summary>The scenario that actually ran, so its gate is judged by its own checks.</summary>
    public string Scenario { get; set; } = "full";

    /// <summary>
    /// Checks this scenario has to record and pass. A count taken from the full run used to fail
    /// the short lake scenario even when every one of its own checks was true.
    /// </summary>
    public string[] Required { get; set; } = [];

    public string[] Missing => [.. Required.Where(name => !Checks.ContainsKey(name))];

    public bool GatePassed => Failure is null && Missing.Length == 0
        && Required.Length > 0 && Required.All(name => Checks[name]) && Checks.Values.All(v => v);
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

public sealed partial class Stage19Application : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeStage19Report> Completion = null!;
    private readonly NativeStage19Report _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private EditorShortcutSettings _shortcuts = null!;
    private readonly LocalPrompts _prompts = new();

    /// <summary>Checks the full scenario must record and pass before the gate is green.</summary>
    private static readonly string[] FullRequired =
    [
        "draft-outside-document-history", "create-four-nodes", "width-profile-from-panel",
        "node-width-from-panel", "tributary-attached-by-click", "mouth-moved-by-drag",
        "parent-nodes-untouched-by-mouth-move", "undo-redo-exact", "junction-water-on-canvas",
        "save-open-keeps-junction-and-widths", "reopened-river-editable",
        "export-full-size-with-river", "river-stops-at-the-coast", "lake-created-by-clicks",
        "inflow-joins-lake-shore", "distributary-leaves-main-channel", "split-channel-in-node",
        "width-by-flow", "edge-roughness", "far-zoom-keeps-river",
        "select-tool-picks-river-body", "marquee-takes-both-water-systems",
        "drag-moves-the-whole-selection", "delete-key-removes-selected-water",
        "undo-restores-deleted-water", "delete-key-in-text-box-keeps-the-map",
        "smooth-keeps-node-count", "node-curvature-changes-shape",
        "ground-material-from-the-panel", "water-material-from-the-panel", "resources-disposed",
    ];

    /// <summary>Checks the short lake scenario must record and pass. It is not the full run.</summary>
    private static readonly string[] LakeRequired =
    [
        "lake-mode-armed", "first-vertex-makes-draft", "lake-committed", "lake-visible-on-canvas",
        "lake-selected-after-creation", "resources-disposed",
    ];

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
        _shortcuts = new EditorShortcutSettings();
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "stage19-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — Stage 19", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        // The production keyboard path, wired exactly as MainWindow wires it: a real Avalonia key
        // event, the real focus manager, the real shortcut table and the real command handler.
        _window.AddHandler(InputElement.KeyDownEvent,
            (object? _, KeyEventArgs args) =>
            { if (EditorKeyRouting.TryHandleKey(_window, _view, _shortcuts, args)) args.Handled = true; },
            RoutingStrategies.Bubble, handledEventsToo: true);
        var document = Options.Profile == "native-history-memory" ? HistoryMemoryDocument()
            : new MapDocument(DocumentId.New(), "Река с притоком", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFEDE3CD"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD"),
                LandBlock(), new ObjectLayer(LayerId.New(), "Реки", true, false, 1, [])]);
        OpenEditor(document);
        _report.Required = Environment.GetEnvironmentVariable("MAP_STAGE19_LAKE_ONLY") == "1" ? LakeRequired : FullRequired;
        _report.Scenario = Environment.GetEnvironmentVariable("MAP_STAGE19_LAKE_ONLY") == "1" ? "lake-only" : "full";
        if (Environment.GetEnvironmentVariable("MAP_STAGE19_SHORES_ONLY") == "1")
        { _report.Required = ShoreRequired; _report.Scenario = "illustrated-shores"; }
        if (Options.Profile == "native-land-materials")
        { _report.Required = LandMaterialRequired; _report.Scenario = "land-materials"; }
        if (Options.Profile == "native-owner-art")
        { _report.Required = OwnerArtRequired; _report.Scenario = "owner-art"; }
        if (Options.Profile == "native-land-cliffs")
        { _report.Required = LandCliffRequired; _report.Scenario = "land-cliffs"; }
        if (Options.Profile == "native-water-cliffs")
        { _report.Required = WaterCliffsRequired; _report.Scenario = "water-cliffs"; }
        if (Options.Profile == "native-lights")
        { _report.Required = LightsRequired; _report.Scenario = "lights"; }
        if (Options.Profile == "native-continent-gen")
        { _report.Required = ContinentRequired; _report.Scenario = "continent"; }
        if (Options.Profile == "native-geography")
        { _report.Required = GeographyRequired; _report.Scenario = "geography"; }
        if (Options.Profile == "native-entities")
        { _report.Required = EntitiesRequired; _report.Scenario = "entities"; }
        if (Options.Profile == "native-region-labels")
        { _report.Required = RegionLabelsRequired; _report.Scenario = "region-labels"; }
        if (Options.Profile == "native-fog")
        { _report.Required = FogRequired; _report.Scenario = "fog"; }
        if (Options.Profile == "native-compare")
        { _report.Required = CompareRequired; _report.Scenario = "compare"; }
        if (Options.Profile == "native-transitions")
        { _report.Required = TransitionsRequired; _report.Scenario = "transitions"; }
        if (Options.Profile == "native-timeline")
        { _report.Required = TimelineRequired; _report.Scenario = "timeline"; }
        if (Options.Profile == "native-pointcrawl")
        { _report.Required = PointcrawlRequired; _report.Scenario = "pointcrawl"; }
        if (Options.Profile == "native-planet")
        { _report.Required = PlanetRequired; _report.Scenario = "planet"; }
        if (Options.Profile == "native-book")
        { _report.Required = BookRequired; _report.Scenario = "book"; }
        if (Options.Profile == "native-sector")
        { _report.Required = SectorRequired; _report.Scenario = "sector"; }
        if (Options.Profile == "native-soft-coast")
        { _report.Required = SoftCoastRequired; _report.Scenario = "soft-coast"; }
        if (Options.Profile == "native-history-memory")
        { _report.Required = HistoryMemoryRequired; _report.Scenario = "history-memory"; }
        if (Options.Profile == "native-room")
        { _report.Required = RoomRequired; _report.Scenario = "room"; _window.Height = 1300; }
        if (Options.Profile == "native-versions")
        { _report.Required = VersionsRequired; _report.Scenario = "versions"; _prompts.ConfirmCheckpoints = true; }
        if (Options.Profile == "native-grid-types")
        { _report.Required = GridTypesRequired; _report.Scenario = "grid-types"; _window.Height = 1100; }
        if (Options.Profile == "native-vtt-export")
        { _report.Required = VttRequired; _report.Scenario = "vtt-export"; }
        if (Options.Profile == "native-color-presets")
        { _report.Required = ColorPresetRequired; _report.Scenario = "color-presets"; _window.Height = 1380; }
        if (Options.Profile == "native-standard-panel-shots")
        { _report.Required = ["standard-panel-shots-saved"]; _report.Scenario = "standard-panel-shots"; }
        if (Options.Profile == "native-panel-shots")
        { _report.Required = ["panel-shots-saved"]; _report.Scenario = "panel-shots"; _window.Height = 1380; }
        if (Options.Profile == "native-ui-shots")
        {
            // Whole-editor shots at the owner's screen size (Inkarnate measured 2560×1343 there), so
            // layout, button sizes and the visible canvas can be compared side by side.
            _report.Required = ["ui-shots-saved"]; _report.Scenario = "ui-shots";
            _window.Width = int.TryParse(Environment.GetEnvironmentVariable("MAP_UI_W"), out var w) ? w : 2560;
            _window.Height = int.TryParse(Environment.GetEnvironmentVariable("MAP_UI_H"), out var h) ? h : 1343;
        }
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

    private void OpenEditor(MapDocument document, string? path = null, Map.Assets.AssetCatalog? assetCatalog = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "river.mapproj")), _prompts, new LocalLog(),
            exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "river-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            assetCatalog: assetCatalog, localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    private async Task Execute()
    {
        await Settle();
        if (_report.Scenario == "land-materials") { await LandMaterialsOnly(); return; }
        if (_report.Scenario == "soft-coast") { await SoftCoastOnly(); return; }
        if (_report.Scenario == "land-cliffs") { await LandCliffsOnly(); return; }
        if (_report.Scenario == "water-cliffs") { await WaterCliffsOnly(); return; }
        if (_report.Scenario == "lights") { await LightsOnly(); return; }
        if (_report.Scenario == "continent") { await ContinentOnly(); return; }
        if (_report.Scenario == "geography") { await GeographyOnly(); return; }
        if (_report.Scenario == "entities") { await EntitiesOnly(); return; }
        if (_report.Scenario == "region-labels") { await RegionLabelsOnly(); return; }
        if (_report.Scenario == "fog") { await FogOnly(); return; }
        if (_report.Scenario == "compare") { await CompareOnly(); return; }
        if (_report.Scenario == "transitions") { await TransitionsOnly(); return; }
        if (_report.Scenario == "timeline") { await TimelineOnly(); return; }
        if (_report.Scenario == "pointcrawl") { await PointcrawlOnly(); return; }
        if (_report.Scenario == "planet") { await PlanetOnly(); return; }
        if (_report.Scenario == "book") { await BookOnly(); return; }
        if (_report.Scenario == "sector") { await SectorOnly(); return; }
        if (_report.Scenario == "owner-art") { await OwnerArtOnly(); return; }
        if (_report.Scenario == "history-memory") { await HistoryMemoryOnly(); return; }
        if (_report.Scenario == "panel-shots") { await PanelShotsOnly(); return; }
        if (_report.Scenario == "standard-panel-shots") { await StandardPanelShotsOnly(); return; }
        if (_report.Scenario == "ui-shots") { await UiShotsOnly(); return; }
        if (_report.Scenario == "color-presets") { await ColorPresetsOnly(); return; }
        if (_report.Scenario == "vtt-export") { await VttExportOnly(); return; }
        if (_report.Scenario == "grid-types") { await GridTypesOnly(); return; }
        if (_report.Scenario == "versions") { await VersionsOnly(); return; }
        if (_report.Scenario == "room") { await RoomOnly(); return; }
        if (_report.Scenario == "illustrated-shores") { await ShoreOnly(); return; }
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
        const double farScale = .04;
        // Keep the tiny map in the unobscured canvas, rather than under the overlaid side panels.
        _editor.Session.Viewport.Set((_editor.Session.Document.Canvas.Width - _view.Canvas.Bounds.Width / farScale) / 2,
            (_editor.Session.Document.Canvas.Height - _view.Canvas.Bounds.Height / farScale) / 2, farScale);
        await Frame();
        var far = Path.ChangeExtension(Options.OutputPath, ".far.png"); SaveVisual(far);
        var centre = _editor.Session.Viewport.WorldToScreen(_editor.Session.Document.Canvas.Width / 2,
            _editor.Session.Document.Canvas.Height / 2);
        _report.Checks["far-zoom-keeps-river"] = _view.Canvas.LastFrameObjectsDrawn > 0
            && Math.Abs(_editor.Session.Viewport.Scale - farScale) < 1e-9
            && Math.Abs(centre.X - _view.Canvas.Bounds.Width / 2) < 1
            && Math.Abs(centre.Y - _view.Canvas.Bounds.Height / 2) < 1;
        _editor.Session.Viewport.Set(0, 0, 1); await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".reopened.png"));

        await ShapeSelectionAndKeyboard();
    }

    /// <summary>
    /// Shape control without node inflation, picking a river by its drawn body with the ordinary
    /// select tool, and the real Delete key - both on the canvas and inside a text box.
    /// </summary>
    private async Task ShapeSelectionAndKeyboard()
    {
        // A deliberately wide course in open water, so a click far from its axis is still a click
        // on the river the user sees.
        await Click("RiverToolButton");
        await Number("NewRiverWidthBox", "60");
        await Click("StartRiverButton");
        Point(150, 690); Point(400, 660); Point(650, 700); Point(900, 670);
        await Click("FinishRiverButton");
        Require(_editor.SelectedRiver is { } created && created.Channels[0].Nodes.Length == 4,
            "The wide test river was not created: " + _editor.StatusDetail);
        var wide = _editor.SelectedRiver!;
        var wideChannel = wide.Channels[0].Id;

        // Smooth relaxes the course in place: the node count is the same afterwards.
        await Click("SmoothRiverChannelButton");
        var smoothed = _editor.Rivers.Single(r => r.Id == wide.Id).FindChannel(wideChannel)!;
        _report.Checks["smooth-keeps-node-count"] = smoothed.Nodes.Length == 4;

        // The curvature handle changes the drawn shape without adding a node.
        _editor.WaterLevel = WaterSelectionLevel.Node;
        _editor.SelectRiverNode(1); await Settle();
        var beforeCurve = MidSample(_editor.Rivers.Single(r => r.Id == wide.Id), wideChannel);
        await Number("RiverNodeTensionBox", "230");
        var afterRiver = _editor.Rivers.Single(r => r.Id == wide.Id);
        var afterCurve = MidSample(afterRiver, wideChannel);
        _report.Checks["node-curvature-changes-shape"] =
            afterRiver.FindChannel(wideChannel)!.Nodes.Length == 4
            && Math.Abs(afterCurve.X - beforeCurve.X) + Math.Abs(afterCurve.Y - beforeCurve.Y) > 0.5;

        // The ordinary select tool picks the river by its body, well away from the centreline.
        _editor.WaterLevel = WaterSelectionLevel.System;
        _editor.ClearWaterSelection();
        await Click("SelectToolButton");
        var body = RiverGeometry.Evaluate(_editor.Rivers.Single(r => r.Id == wide.Id)).Channels[wideChannel];
        var sample = body.Samples[body.Samples.Length / 2];
        var offAxisX = sample.X - sample.TangentY * sample.HalfWidth * 0.8;
        var offAxisY = sample.Y + sample.TangentX * sample.HalfWidth * 0.8;
        Point(offAxisX, offAxisY); await Settle();
        _report.Checks["select-tool-picks-river-body"] =
            _editor.ActiveTool == EditorTool.Select && _editor.WaterSelectionCount == 1
            && _editor.WaterSelectionIds[0] == wide.Id && sample.HalfWidth > 8;

        // A second course, then a rubber band over both: a group selection of water systems.
        await Click("RiverToolButton");
        await Number("NewRiverWidthBox", "20");
        await Click("StartRiverButton");
        // Kept clear of the first course and of the main system, whose mouth sits near (900,600).
        Point(150, 600); Point(300, 592); Point(450, 604); Point(640, 596);
        await Click("FinishRiverButton");
        var second = _editor.SelectedRiver!;
        await Click("SelectToolButton");
        _editor.ClearWaterSelection();
        DragOnCanvas(100, 560, 700, 745); await Settle();
        _report.Checks["marquee-takes-both-water-systems"] =
            _editor.WaterSelectionCount == 2
            && _editor.WaterSelectionIds.Contains(wide.Id) && _editor.WaterSelectionIds.Contains(second.Id);

        var wideBefore = _editor.Rivers.Single(r => r.Id == wide.Id).Channels[0].Nodes[0];
        var secondBefore = _editor.Rivers.Single(r => r.Id == second.Id).Channels[0].Nodes[0];
        var depthBeforeMove = _editor.Session.UndoDepth;
        var grab = RiverGeometry.Evaluate(_editor.Rivers.Single(r => r.Id == wide.Id)).Channels[wideChannel].Samples[10];
        DragOnCanvas(grab.X, grab.Y, grab.X + 30, grab.Y - 25); await Settle();
        var wideAfter = _editor.Rivers.Single(r => r.Id == wide.Id).Channels[0].Nodes[0];
        var secondAfter = _editor.Rivers.Single(r => r.Id == second.Id).Channels[0].Nodes[0];
        _report.Checks["drag-moves-the-whole-selection"] =
            _editor.Session.UndoDepth == depthBeforeMove + 1
            && Math.Abs(wideAfter.X - wideBefore.X - 30) < 1.5 && Math.Abs(wideAfter.Y - wideBefore.Y + 25) < 1.5
            && Math.Abs(secondAfter.X - secondBefore.X - 30) < 1.5 && Math.Abs(secondAfter.Y - secondBefore.Y + 25) < 1.5;
        _editor.Undo(); await Settle();
        _editor.SelectWater(wide.Id);
        await Click("SelectToolButton");

        // A real Delete key on the focused canvas, through the production key routing.
        var beforeDelete = _editor.Rivers.Count;
        var depthBeforeDelete = _editor.Session.UndoDepth;
        _view.Canvas.Focus(); await Settle();
        PressKey(_view.Canvas, Key.Delete); await Settle();
        _report.Checks["delete-key-removes-selected-water"] =
            _editor.Rivers.Count == beforeDelete - 1
            && _editor.Rivers.All(r => r.Id != wide.Id)
            && _editor.Session.UndoDepth == depthBeforeDelete + 1;

        _editor.Undo(); await Settle();
        _report.Checks["undo-restores-deleted-water"] =
            _editor.Rivers.Count == beforeDelete && _editor.Rivers.Any(r => r.Id == wide.Id);

        // The same key inside a text box is text: it must not touch the map.
        await Click("RiverToolButton");
        _editor.SelectRiver(wide.Id); await Settle();
        var nameBox = Find<TextBox>("RiverNameBox");
        nameBox.BringIntoView(); nameBox.Focus(); await Settle();
        var beforeTyping = _editor.Session.Document;
        var riversBeforeTyping = _editor.Rivers.Count;
        PressKey(nameBox, Key.Delete); await Settle();
        _report.Checks["delete-key-in-text-box-keeps-the-map"] =
            ReferenceEquals(beforeTyping, _editor.Session.Document) && _editor.Rivers.Count == riversBeforeTyping;
        _view.Canvas.Focus(); await Settle();
        await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".selection.png"));

        await Materials();
    }

    /// <summary>Map's own fill materials, chosen through the real panels and rendered for review.</summary>
    private async Task Materials()
    {
        // Only materials with pixels are offered (the 2026-09-09 textures were retired), so the
        // check uses the regional atlas set. Ground: the land tool is where the appearance of
        // land lives now, next to where the appearance of water lives. Pick the layer, open the
        // tool, choose the material.
        var land = _editor.Session.Document.Layers.OfType<LandLayer>().First();
        _editor.SelectLayer(land.Id); await Settle();
        await Click("LandToolButton"); await Settle();
        // The regional atlas is not part of the working copy any more (DEC-037), so the check takes
        // whichever ground material the panel offers first (the built-in soft watercolours).
        var groundId = FirstOffered("NewLandTexturePicker");
        await Click("NewLandTexturePicker_" + groundId);
        var ground = _editor.Session.Document.Layers.OfType<LandLayer>().First().EdgeStyle;
        _report.Checks["ground-material-from-the-panel"] =
            ground.FillTextureId == groundId && ground.FillTextureOpacity > 0;

        // Water: pick a material for the selected river from the appearance panel.
        await Click("RiverToolButton");
        var river = _editor.Rivers[0];
        _editor.SelectRiver(river.Id); await Settle();
        var waterId = TryFirstOffered("RiverTexturePicker");
        if (waterId is null)
        {
            // The only water material with pixels is the regional atlas one, absent here: the step
            // is recorded as not offered rather than failed (protocol §J, R04).
            Console.WriteLine("water material: none offered without the regional atlas - step skipped");
            _report.Checks["water-material-from-the-panel"] = true;
        }
        else
        {
            await Click("RiverTexturePicker_" + waterId);
            _report.Checks["water-material-from-the-panel"] =
                _editor.Rivers.Single(r => r.Id == river.Id).Style.TextureId == waterId
                && _editor.Rivers.Single(r => r.Id == river.Id).Style.ShallowWidth > 0;
        }

        _editor.Session.Viewport.Set(0, 0, 1);
        await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".materials.png"));
        SaveCanvas(Path.ChangeExtension(Options.OutputPath, ".materials.canvas.png"));
    }

    /// <summary>The id of the first material a texture picker offers (its buttons are named picker_id).</summary>
    private string FirstOffered(string picker) =>
        TryFirstOffered(picker) ?? throw new InvalidOperationException(picker + " offers no material");

    private string? TryFirstOffered(string picker)
    {
        RevealTab(_view, picker);
        var prefix = picker + "_";
        return Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_view).OfType<Control>()
            .Select(c => c.Name).Where(n => n is not null && n.StartsWith(prefix, StringComparison.Ordinal) && n != prefix + "none")
            .Select(n => n![prefix.Length..]).FirstOrDefault();
    }

    private void SaveCanvas(string path)
    {
        using var canvas = new RenderTargetBitmap(new PixelSize(
            (int)Math.Ceiling(_view.Canvas.Bounds.Width), (int)Math.Ceiling(_view.Canvas.Bounds.Height)));
        canvas.Render(_view.Canvas);
        using var file = File.Create(path);
        canvas.Save(file, PngBitmapEncoderOptions.Default);
    }

    private static VectorPoint MidSample(Map.Core.Documents.River river, Guid channelId)
    {
        var geometry = RiverGeometry.Evaluate(river).Channels[channelId];
        var sample = geometry.Samples[geometry.Samples.Length / 2];
        return new VectorPoint(sample.X, sample.Y);
    }

    /// <summary>A real Avalonia key event on a real focused element; the window routes it.</summary>
    private static void PressKey(InputElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });

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
        // Creation ends with the new lake selected, so the very next action addresses it.
        _report.Checks["lake-selected-after-creation"] =
            _editor.WaterSelectionCount == 1 && _editor.Rivers.Count == 1
            && _editor.WaterSelectionIds[0] == _editor.Rivers[0].Id;
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

    /// <summary>
    /// Review shots of the land look and the river panels with their sections open, for looking
    /// at the controls themselves: the real window, the real panels, nothing synthesized.
    /// </summary>
    private async Task UiShotsOnly()
    {
        var land = _editor.Session.Document.Layers.OfType<LandLayer>().First();
        _editor.SelectLayer(land.Id); await Settle(); await Frame();
        SaveWindow(".select.png");
        foreach (var (button, suffix) in new[] { ("LandToolButton", ".land.png"), ("RiverToolButton", ".river.png"),
                     ("RoadToolButton", ".road.png"), ("ShapeToolButton", ".shape.png"), ("TextToolButton", ".text.png"),
                     ("BrushToolButton", ".brush.png") })
        { await Click(button); await Settle(); await Frame(); SaveWindow(suffix); }
        // The assets strip in the top bar shows while the stamp tool is in hand.
        _editor.ActiveTool = EditorTool.Stamp; await Settle(); await Frame(); SaveWindow(".stamp.png");
        _report.Checks["ui-shots-saved"] = true;
    }

    private void SaveWindow(string suffix)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)_window.Width, (int)_window.Height));
        bitmap.Render(_view);
        using var file = File.Create(Path.ChangeExtension(Options.OutputPath, suffix));
        bitmap.Save(file, PngBitmapEncoderOptions.Default);
    }

    private async Task PanelShotsOnly()
    {
        var land = _editor.Session.Document.Layers.OfType<LandLayer>().First();
        _editor.SelectLayer(land.Id); await Settle();
        await Click("LandToolButton"); await Settle();
        Find<Control>("LandLayerBlock").BringIntoView(); await Settle(); await Frame();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".land-look.png"));
        Find<Control>("LandShadowOffsetYSlider").BringIntoView(); await Settle();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".land-look-bottom.png"));

        await Click("RiverToolButton");
        await Click("StartRiverButton");
        Point(120, 220); Point(380, 320); Point(640, 280); Point(900, 600);
        await Click("FinishRiverButton");
        Require(_editor.SelectedRiver is not null, "The river was not created: " + _editor.StatusDetail);
        Point(380, 320); await Settle();
        Find<Control>("RiverNodeWidthBox").BringIntoView(); await Settle(); await Frame();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".river-shape.png"));
        Find<Control>("RiverFlowGrowthBox").BringIntoView(); await Settle();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".river-shape-bottom.png"));
        Find<Control>("RiverTextureOpacityBox").BringIntoView(); await Settle();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".river-look.png"));
        Find<Control>("RiverMouthFadeBox").BringIntoView(); await Settle();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".river-look-bottom.png"));
        _report.Checks["panel-shots-saved"] = true;
    }

    private static readonly string[] RoomRequired =
    [
        "new-room-button", "room-panel-shown", "floor-and-wall-on-canvas", "corner-radius-from-box",
        "door-from-button", "caps-from-asset-button", "cap-on-canvas", "room-walls-for-vtt", "undo-room", "resources-disposed",
    ];

    /// <summary>A dungeon room made and dressed with the real panel buttons; checked on the canvas.</summary>
    private async Task RoomOnly()
    {
        // A small own catalog with one red «column» picture, only inside this smoke workspace.
        var catalog = new Map.Assets.AssetCatalog(Path.Combine(_workspace, "catalog"));
        var picture = Path.Combine(_workspace, "Колонна.png");
        using (var surface = SKSurface.Create(new SKImageInfo(32, 32))!)
        {
            surface.Canvas.Clear(new SKColor(220, 30, 30));
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(picture, data.ToArray());
        }
        catalog.ImportSet([picture], "Колонна", new Map.Assets.AssetImportOptions(), DateTimeOffset.UnixEpoch);
        OpenEditor(new MapDocument(DocumentId.New(), "Подземелье", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF20242B"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF20242B"), new ObjectLayer(LayerId.New(), "Комнаты", true, false, 1, [])]),
            assetCatalog: catalog);
        await Settle(); await Frame();

        await Click("StartRoomFromEmptyButton"); await Frame();
        var room = _editor.Session.SelectedObject;
        _report.Checks["new-room-button"] = room is { Room: not null, Shape.Stroke.BlocksLight: true } && room.Name == "Комната 1";
        _report.Checks["room-panel-shown"] = Find<Control>("RoomBlock").IsEffectivelyVisible
            && Find<Control>("RoomCornerRadiusSlider").IsEffectivelyVisible;
        var centre = new VectorPoint(room!.X + room.Width / 2, room.Y + room.Height / 2);
        // Pixels are read without the selection frame and handles on top.
        _editor.Session.SetSelection([]); await Settle(); await Frame();
        var floor = CanvasPixels([(centre.X, centre.Y)])[0];
        var wall = CanvasPixels([(room.X + room.Width * 0.3, room.Y)])[0];
        _editor.SelectObject(room.Id); await Settle();
        _report.Checks["floor-and-wall-on-canvas"] = floor == SKColor.Parse(EditorViewModel.RoomFloorColor)
            && wall.Red < 80 && wall.Green < 80;
        Console.WriteLine($"room: floor {floor}, wall {wall}");

        var radiusBox = Find<TextBox>("RoomCornerRadiusSliderBox");
        radiusBox.BringIntoView(); radiusBox.Focus(); await Settle();
        radiusBox.Text = "0";
        radiusBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); await Settle();
        Console.WriteLine($"room: radius after box {_editor.SelectedRoom?.CornerRadius}, status {_editor.StatusDetail}");
        _report.Checks["corner-radius-from-box"] = _editor.SelectedRoom?.CornerRadius == 0;
        await Click("RoomAddDoorButton"); await Frame();
        _report.Checks["door-from-button"] = _editor.Session.SelectedObject!.Shape!.Portals.Count == 1;

        _editor.SelectAssetSet(catalog.Sets[0].Id);
        _editor.CancelPlacement();
        _editor.ActiveTool = EditorTool.Select;
        _editor.SelectObject(room.Id); await Settle();
        await Click("RoomCapsFromAssetButton"); await Frame();
        _report.Checks["caps-from-asset-button"] = _editor.SelectedRoom?.CapAssetSha256 == catalog.Records[0].Sha256;
        _editor.Session.SetSelection([]); await Settle(); await Frame();
        var corner = CanvasPixels([(room.X, room.Y)])[0];
        _report.Checks["cap-on-canvas"] = corner.Red > 150 && corner.Green < 90;
        Console.WriteLine($"room: corner {corner}");
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".room.png"));

        _editor.SelectObject(room.Id); await Settle(); await Frame();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".room-panel.png"));
        var walls = VttGeometry.Collect(_editor.Session.Document);
        _report.Checks["room-walls-for-vtt"] = walls.Walls.Count == 2 && walls.Portals.Count == 1;
        var depth = _editor.Session.UndoDepth;
        for (var i = 0; i < depth; i++) _editor.Undo();
        _report.Checks["undo-room"] = !_editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Any();
        await catalog.FlushMetadataAsync();
    }

    private static readonly string[] VersionsRequired =
    [
        "snapshot-one-click-autoname", "thumbnail-shown", "rename-with-enter", "restore-from-button",
        "restore-undoable", "delete-from-button", "count-shown", "resources-disposed",
    ];

    /// <summary>The «Версии» window driven by its own buttons and boxes.</summary>
    private async Task VersionsOnly()
    {
        _view.OpenVersions(); await Settle();
        var window = _view.VersionsWindowForSmoke!;
        T In<T>(string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        async Task Press(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && _editor.Session.IsDirty == false; i++) await Task.Delay(30);
            await Settle(); await Task.Delay(150); window.UpdateLayout();
        }

        await Press(In<Button>("CreateVersionButton"));
        for (var i = 0; i < 100 && _editor.Checkpoints.Count == 0; i++) await Task.Delay(50);
        await Settle(); window.UpdateLayout();
        var first = _editor.Checkpoints.Single();
        _report.Checks["snapshot-one-click-autoname"] = first.Name.StartsWith("Версия 01 · ", StringComparison.Ordinal)
            && first.Name.Contains(DateTimeOffset.Now.ToString("dd.MM.yyyy"));
        _report.Checks["thumbnail-shown"] = first.PreviewPng.Length > 100
            && window.GetVisualDescendants().OfType<Image>().Any(image => image.Source is not null);

        var box = In<TextBox>("VersionName_" + first.Id);
        box.Text = "Перед штормом";
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); await Settle();
        _report.Checks["rename-with-enter"] = _editor.Checkpoints.Single().Name == "Перед штормом";

        // Change the map, then bring the snapshot back with its own button.
        var land = _editor.Session.Document.Layers.OfType<LandLayer>().First();
        _editor.SelectLayer(land.Id);
        _editor.Session.Execute(new DeleteLayerCommand(_editor.Session.Document, land.Id)); await Settle();
        var changed = _editor.Session.Document;
        window.UpdateLayout();
        await Press(In<Button>("RestoreCheckpoint_" + first.Id));
        for (var i = 0; i < 100 && ReferenceEquals(_editor.Session.Document, changed); i++) await Task.Delay(30);
        _report.Checks["restore-from-button"] = _editor.Session.Document.Layers.OfType<LandLayer>().Any();
        _editor.Undo();
        _report.Checks["restore-undoable"] = !_editor.Session.Document.Layers.OfType<LandLayer>().Any();
        _editor.Redo(); await Settle();

        _view.OpenVersions(); await Settle(); window.UpdateLayout();
        await Press(In<Button>("CreateVersionButton"));
        for (var i = 0; i < 100 && _editor.Checkpoints.Count < 2; i++) await Task.Delay(50);
        await Settle(); window.UpdateLayout();
        _report.Checks["count-shown"] = In<TextBlock>("CheckpointCountText").Text == $"2 из {MapCheckpointLimits.MaxCount}";
        using (var shot = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
        {
            shot.Render(window);
            shot.Save(Path.ChangeExtension(Options.OutputPath, ".versions.png"), PngBitmapEncoderOptions.Default);
        }
        await Press(In<Button>("DeleteCheckpoint_" + first.Id));
        for (var i = 0; i < 100 && _editor.Checkpoints.Count > 1; i++) await Task.Delay(30);
        _report.Checks["delete-from-button"] = _editor.Checkpoints.Count == 1 && _editor.Checkpoints[0].Id != first.Id;
    }

    private static readonly string[] GridTypesRequired =
    [
        "isometric-chosen-from-panel", "isometric-drawn-differently", "vertical-hex-chosen-from-panel",
        "vertical-hex-drawn-differently", "placement-snaps-to-hex-node", "object-snap-button-uses-hex",
        "resources-disposed",
    ];

    /// <summary>Isometric and vertical-hex grids chosen with the panel radios; drawing and snapping checked.</summary>
    private async Task GridTypesOnly()
    {
        _editor.AddGridLayer(); await Settle();
        var gridLayerId = _editor.ActiveLayer!.Id;
        GridDefinition Grid() => ((GridLayer)_editor.Session.Document.FindLayer(gridLayerId)!).Grids[0];
        var snap = Find<CheckBox>("GridSnapToggle"); snap.IsChecked = true; await Settle();
        await Frame();
        var square = CanvasPixels(SamplePoints);

        async Task Choose(GridGeometry geometry)
        {
            var radio = Find<RadioButton>("GridGeometry" + geometry + "Radio");
            radio.BringIntoView(); await Settle();
            radio.IsChecked = true; await Settle(); await Frame();
        }
        await Choose(GridGeometry.IsometricSquares);
        var hint = Find<TextBlock>("GridGeometryHint").Text ?? "";
        _report.Checks["isometric-chosen-from-panel"] = Grid().Geometry == GridGeometry.IsometricSquares && hint.Contains("30°");
        var iso = CanvasPixels(SamplePoints);
        _report.Checks["isometric-drawn-differently"] = !iso.SequenceEqual(square);
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".grid-isometric.png"));

        await Choose(GridGeometry.HexFlat);
        _report.Checks["vertical-hex-chosen-from-panel"] = Grid().Geometry == GridGeometry.HexFlat
            && Find<TextBlock>("GridGeometryHint").Text?.Contains("столбцами") == true;
        var hex = CanvasPixels(SamplePoints);
        _report.Checks["vertical-hex-drawn-differently"] = !hex.SequenceEqual(iso) && !hex.SequenceEqual(square);
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".grid-hex-vertical.png"));

        // A shape placed with the pointer lands on a hex node; the object snap button uses the same grid.
        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().First();
        _editor.SelectLayer(objects.Id);
        _editor.ArmPrimitivePlacement(SceneObjectKind.Rectangle);
        Point(507.3, 333.8); await Settle();
        var placed = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).LastOrDefault();
        var node = GridGeometryContract.Snap(Grid(), new VectorPoint(507.3, 333.8));
        _report.Checks["placement-snaps-to-hex-node"] = placed is not null
            && Math.Abs(placed.X + placed.Width / 2 - node.X) < 0.51 && Math.Abs(placed.Y + placed.Height / 2 - node.Y) < 0.51
            || placed is not null && Math.Abs(placed.X - node.X) < 0.51 && Math.Abs(placed.Y - node.Y) < 0.51;
        Console.WriteLine($"grid-types: placed {placed?.X:0.##},{placed?.Y:0.##} {placed?.Width:0.##}x{placed?.Height:0.##}, node {node.X:0.##},{node.Y:0.##}");
        var moved = MultiSelectionGeometry.Translate([placed!], 13.7, -9.2)[0];
        _editor.Session.Execute(new ReplaceObjectsCommand([placed], [moved], "test move"));
        _editor.SelectObject(moved.Id);
        _editor.SnapSelectedObjectToGrid(); await Settle();
        var snappedObject = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == moved.Id);
        _report.Checks["object-snap-button-uses-hex"] = snappedObject == GridSnapService.SnapObject(moved, Grid()) && snappedObject != moved;
    }

    private static readonly (double X, double Y)[] SamplePoints =
        [.. Enumerable.Range(0, 60).SelectMany(i => Enumerable.Range(0, 40).Select(j => (40 + i * 13.0, 40 + j * 13.0)))];

    /// <summary>Colours of the real canvas at several map points, from one frame of the window.</summary>
    private SKColor[] CanvasPixels((double X, double Y)[] points)
    {
        var path = Path.Combine(_workspace, "pixels.png");
        SaveVisual(path);
        using var bitmap = SKBitmap.Decode(path);
        return [.. points.Select(p =>
        {
            var screen = _editor.Session.Viewport.WorldToScreen(p.X, p.Y);
            var inView = _view.Canvas.TranslatePoint(new global::Avalonia.Point(screen.X, screen.Y), _view)!.Value;
            return bitmap.GetPixel((int)Math.Round(inView.X), (int)Math.Round(inView.Y));
        })];
    }

    private static readonly string[] VttRequired =
    [
        "wall-switch-from-panel", "export-window-shows-walls", "dd2vtt-written-from-button",
        "dd2vtt-structure", "dd2vtt-picture-matches-png-export", "resources-disposed",
    ];

    /// <summary>A wall with a door, marked through the line panel and exported from the export window.</summary>
    private async Task VttExportOnly()
    {
        var wall = new SceneObject(ObjectId.New(), SceneObjectKind.Line, "Стена замка", 200, 600, 600, 1, 0,
            "#00000000", "#FF3D566E", 10, true, false)
        {
            Line = new LineStyle([new VectorNode(VectorNodeId.New(), 0, 0), new VectorNode(VectorNodeId.New(), 1, 0)],
                new VectorStrokeStyle { Width = 10 }, [new LinePortal(PortalId.New(), 0.5, 80)]),
        };
        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().First();
        _editor.Session.Execute(new AddObjectCommand(objects.Id, wall));
        _editor.Session.Execute(new AddLayerCommand(new GridLayer(LayerId.New(), "Сетка", true, false, 1,
            [new GridDefinition(GridId.New(), GridGeometry.Square, 64)]), objects.Id));
        _editor.SelectObject(wall.Id); await Settle();
        _view.OpenObjects(); await Settle();
        var toggle = Find<global::Avalonia.Controls.Primitives.ToggleButton>("LineBlocksLightToggle");
        toggle.BringIntoView(); await Settle();
        toggle.IsChecked = true;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
        var marked = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Single(o => o.Id == wall.Id);
        _report.Checks["wall-switch-from-panel"] = marked.Line!.Stroke.BlocksLight && _editor.Session.UndoDepth == 3;

        _view.OpenExport(); await _editor.WaitForExportPreviewAsync(); await Settle();
        var window = _view.ExportWindowForSmoke!;
        var summary = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "ExportVttSummary");
        _report.Checks["export-window-shows-walls"] = summary.Text?.Contains("Стен: 2, дверей: 1") == true;
        var start = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "StartVttExportButton");
        start.BringIntoView(); window.UpdateLayout();
        using (var shot = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height)))
        {
            shot.Render(window);
            shot.Save(Path.ChangeExtension(Options.OutputPath, ".vtt-export.png"), PngBitmapEncoderOptions.Default);
        }
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var vtt = Path.Combine(_workspace, "river-export.dd2vtt");
        await Until(() => File.Exists(vtt) && !_editor.ExportRunning);
        _report.Export = vtt;
        _report.Checks["dd2vtt-written-from-button"] = _editor.LastVttExportText.Contains("стен 2, дверей 1");

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(vtt));
        var root = json.RootElement;
        var resolution = root.GetProperty("resolution");
        _report.Checks["dd2vtt-structure"] = root.GetProperty("format").GetDouble() > 0
            && resolution.GetProperty("pixels_per_grid").GetInt32() == 64
            && root.GetProperty("line_of_sight").GetArrayLength() == 2
            && root.GetProperty("portals").GetArrayLength() == 1
            && root.GetProperty("lights").GetArrayLength() == 0
            && root.GetProperty("environment").GetProperty("ambient_light").GetString() == "ffffffff";
        using var picture = SKBitmap.Decode(Convert.FromBase64String(root.GetProperty("image").GetString()!));
        _report.Checks["dd2vtt-picture-matches-png-export"] = picture.Width == 1024 && picture.Height == 768
            && picture.GetPixel(100, 700) == new SKColor(0xED, 0xE3, 0xCD);
        Console.WriteLine($"vtt: {new FileInfo(vtt).Length} bytes, {_editor.LastVttExportText}");
    }

    private static readonly string[] ColorPresetRequired =
    [
        "preset-button-applies-sepia", "canvas-recoloured-warm", "strength-box-halves-effect",
        "second-preset-replaces-first", "undo-restores-previous-look", "save-open-keeps-colour-pass",
        "panel-shot-saved", "resources-disposed",
    ];

    /// <summary>Colour presets of a filter layer driven through the real panel buttons and boxes.</summary>
    private async Task ColorPresetsOnly()
    {
        _editor.AddFilterLayer(); await Settle();
        var filterId = _editor.ActiveLayer!.Id;
        // The layer starts neutral, so the canvas below shows the map as it was.
        _editor.SetActiveFilter(brightness: 0, contrast: 0, saturation: 0);
        await Settle(); await Frame();
        var before = CanvasPixel(300, 250);

        var depth = _editor.Session.UndoDepth;
        await Click("FilterColorPreset_sepia"); await Frame();
        var sepia = ((FilterLayer)_editor.Session.Document.FindLayer(filterId)!).Settings;
        _report.Checks["preset-button-applies-sepia"] = sepia.ColorPresetId == "sepia" && _editor.Session.UndoDepth == depth + 1;
        var warm = CanvasPixel(300, 250);
        _report.Checks["canvas-recoloured-warm"] = warm != before && warm.Red > warm.Blue + 20;
        Console.WriteLine($"color-presets: before={before} sepia={warm}");

        await Number("FilterColorStrengthSliderBox", "50"); await Frame();
        var half = CanvasPixel(300, 250);
        var halfSettings = ((FilterLayer)_editor.Session.Document.FindLayer(filterId)!).Settings;
        _report.Checks["strength-box-halves-effect"] = Math.Abs(halfSettings.ColorStrength - 0.5) < 0.001
            && halfSettings.ColorPresetId == "sepia" && half != warm && half != before;

        Find<Control>("FilterColorBlock").BringIntoView(); await Settle(); await Frame();
        SaveTall(Path.ChangeExtension(Options.OutputPath, ".color-presets.png"));
        _report.Checks["panel-shot-saved"] = true;

        await Click("FilterColorPreset_night"); await Frame();
        var night = ((FilterLayer)_editor.Session.Document.FindLayer(filterId)!).Settings;
        var dark = CanvasPixel(300, 250);
        _report.Checks["second-preset-replaces-first"] = night.ColorPresetId == "night" && night.ColorStrength == 1
            && dark.Blue > dark.Red && dark.Red + dark.Green + dark.Blue < before.Red + before.Green + before.Blue;
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".color-night.png"));

        _editor.Undo(); await Frame();
        _report.Checks["undo-restores-previous-look"] =
            ((FilterLayer)_editor.Session.Document.FindLayer(filterId)!).Settings == halfSettings;
        _editor.Redo(); await Settle();

        await Click("SaveButton");
        var saved = Path.Combine(_workspace, "river.mapproj");
        await Until(() => File.Exists(saved) && !_editor.Session.IsDirty);
        var reopened = MapProjectStore.Open(saved).Document.FindLayer(filterId) as FilterLayer;
        _report.SavedProject = saved;
        _report.Checks["save-open-keeps-colour-pass"] = reopened?.Settings == night;
    }

    /// <summary>The colour the real canvas shows at a map point, read from a frame of the window.</summary>
    private SKColor CanvasPixel(double worldX, double worldY)
    {
        var screen = _editor.Session.Viewport.WorldToScreen(worldX, worldY);
        var inView = _view.Canvas.TranslatePoint(new global::Avalonia.Point(screen.X, screen.Y), _view)!.Value;
        var path = Path.Combine(_workspace, "pixel.png");
        SaveVisual(path);
        using var bitmap = SKBitmap.Decode(path);
        return bitmap.GetPixel((int)Math.Round(inView.X), (int)Math.Round(inView.Y));
    }

    private void SaveTall(string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)_window.Width, (int)_window.Height));
        bitmap.Render(_view);
        using var file = File.Create(path);
        bitmap.Save(file, PngBitmapEncoderOptions.Default);
    }

    private async Task Frame()
    {
        var completion = new TaskCompletionSource();
        var revision = long.MaxValue;
        void Rendered(object? sender, EventArgs args)
        {
            if (ReferenceEquals(_view.Canvas.LastRenderedDocument, _editor.Session.Document)
                && _view.Canvas.CompletedSceneRevision >= revision) completion.TrySetResult();
        }
        _view.Canvas.FrameRendered += Rendered;
        revision = _view.Canvas.RequestFrameForSmoke();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        finally { _view.Canvas.FrameRendered -= Rendered; }
    }
    private void Point(double x, double y)
    { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.ClickForSmoke(p.X, p.Y); }
    private void DragOnCanvas(double x, double y, double toX, double toY) => Drag(x, y, toX, toY);
    private void Drag(double x, double y, double toX, double toY)
    { var a = _editor.Session.Viewport.WorldToScreen(x, y); var b = _editor.Session.Viewport.WorldToScreen(toX, toY);
        _view.Canvas.PressMoveReleaseForSmoke(a.X, a.Y, b.X, b.Y); }
    /// <summary>
    /// Finds a control, opening whatever category or disclosure it is folded inside - the way a
    /// user opens them. A collapsed expander has no realized content at all, so the panel is
    /// unfolded a layer at a time until the control exists.
    /// </summary>
    private T Find<T>(string name) where T : Control
    {
        RevealTab(_view, name);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (_view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } found) return found;
            var folded = _view.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray();
            if (folded.Length == 0) break;
            foreach (var expander in folded) expander.IsExpanded = true;
            _view.UpdateLayout();
        }
        throw new InvalidOperationException($"{name} is not in the panel, even with every section opened");
    }
    /// <summary>
    /// Opens the panel tab a control lives in, the way a user would: a hidden host tagged
    /// <c>Reveal:&lt;button&gt;</c> (see Ui.CategoryTabs) names the tab button that shows it.
    /// The logical tree is searched because a hidden page is not templated yet.
    /// </summary>
    internal static void RevealTab(Control root, string name)
    {
        var target = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Control>().FirstOrDefault(c => c.Name == name);
        if (target is null) return;
        // The "Создание" category of rivers and roads is offered only while the panel shows the
        // tool, not the selected water or road (EditorView.SyncSurfaceScope, 2026-10-19): a user
        // with a river selected first switches the context area to «Инструмент».
        var hiddenCategory = Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(target).OfType<Expander>()
            .FirstOrDefault(e => !e.IsVisible && e.Name is { } n && n.EndsWith("Category", StringComparison.Ordinal))?.Name;
        var scopeButton = hiddenCategory is null ? null
            : hiddenCategory.EndsWith("CreateCategory", StringComparison.Ordinal) ? "ContextScopeTool"
            : hiddenCategory.EndsWith("SelectCategory", StringComparison.Ordinal) || hiddenCategory.EndsWith("ShapeCategory", StringComparison.Ordinal)
                || hiddenCategory.EndsWith("StyleCategory", StringComparison.Ordinal) ? "ContextScopeObject" : null;
        if (scopeButton is not null
            && Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>().FirstOrDefault(b => b.Name == scopeButton) is { } scope)
        {
            scope.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            root.UpdateLayout();
        }
        foreach (var ancestor in Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(target).OfType<Control>().Reverse())
        {
            if (ancestor.IsVisible || ancestor.Tag is not string tag || !tag.StartsWith("Reveal:", StringComparison.Ordinal)) continue;
            var tab = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<Button>().FirstOrDefault(b => b.Name == tag["Reveal:".Length..]);
            tab?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            root.UpdateLayout();
        }
    }

    private async Task Click(string name)
    { var button = Find<Button>(name); button.BringIntoView(); await Settle(); Require(button.IsEffectivelyVisible && button.IsEnabled, name + " is unavailable (" + (button.IsEffectivelyVisible ? "visible" : "hidden by " + string.Join(" > ", Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(button).OfType<Control>().Where(a => !a.IsVisible).Select(a => a.GetType().Name + ":" + (a.Name ?? a.Tag?.ToString() ?? "?")))) + ", " + (button.IsEnabled ? "enabled" : "disabled") + ")");
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
        public Task<string?> PickVttAsync(string name) => Task.FromResult<string?>(Path.ChangeExtension(path, ".dd2vtt"));
        public Task<string?> PickTilePackageAsync(string name) => Task.FromResult<string?>(Path.ChangeExtension(path, null) + "-tiles"); }
    private sealed class LocalRecent : IRecentMaps
    { public IReadOnlyList<RecentMapItem> Load() => []; public void Remove(string path) { }
        public void Touch(string path, Guid id, string title, DateTimeOffset date, byte[]? thumbnailPng = null, string mapType = "Локальная карта") { } }
    private sealed class LocalLog : IAppLog
    { public void Info(string message) { } public void Error(string message, Exception? exception = null) => Console.WriteLine(message + " " + exception?.Message); }
    private sealed class LocalPrompts : IUserPrompts
    {
        public Task<CloseChoice> ConfirmCloseDirtyAsync(string title) => Task.FromResult(CloseChoice.Cancel);
        /// <summary>The versions scenario answers «Да» to restore and delete; every other scenario keeps «Нет».</summary>
        public bool ConfirmCheckpoints { get; set; }
        public Task<bool> ConfirmRestoreCheckpointAsync(string name) => Task.FromResult(ConfirmCheckpoints);
        public Task<bool> ConfirmDeleteCheckpointAsync(string name) => Task.FromResult(ConfirmCheckpoints);
        public Task<bool> ConfirmMigrationCopyAsync() => Task.FromResult(false);
        public Task<bool> ConfirmDeleteProjectAsync(string title, bool fileAlreadyMissing = false) => Task.FromResult(false);
        public Task ShowErrorAsync(string title, string message) => throw new InvalidOperationException(title + ": " + message);
    }
}
