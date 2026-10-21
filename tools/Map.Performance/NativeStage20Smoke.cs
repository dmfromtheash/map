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
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

internal sealed class NativeStage20Report
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false; rail button, panel buttons/boxes and the production canvas router drive the road tool. File pickers and reopen use local adapters. No system input. Screenshots are Avalonia frames and export-path renders of the current document, not OS scan-out.";
    public Dictionary<string, bool> Checks { get; } = [];
    public int Branches { get; set; }
    public int Nodes { get; set; }
    public string? SharedNodeBefore { get; set; }
    public string? SharedNodeAfter { get; set; }
    public string? Screenshot { get; set; }
    public string? SavedProject { get; set; }
    public string? Export { get; set; }
    public string? Failure { get; set; }
    public string Scenario { get; set; } = "full";
    public string[] Required { get; set; } = [];
    public string[] Missing => [.. Required.Where(name => !Checks.ContainsKey(name))];
    public bool GatePassed => Failure is null && Missing.Length == 0
        && Required.Length > 0 && Required.All(name => Checks[name]) && Checks.Values.All(v => v);
}

internal static class NativeStage20Smoke
{
    public static NativeStage20Report Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeStage20Report>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Stage20Application.Options = options; Stage20Application.Completion = completion;
            try { AppBuilder.Configure<Stage20Application>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(4)).GetAwaiter().GetResult();
    }
}

public sealed class Stage20Application : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeStage20Report> Completion = null!;
    private readonly NativeStage20Report _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private EditorShortcutSettings _shortcuts = null!;
    private readonly LocalPrompts _prompts = new();

    private static readonly string[] FullRequired =
    [
        "draft-outside-document-history", "create-four-nodes", "branch-from-the-middle-shares-a-node",
        "branch-width-from-panel", "node-width-from-panel", "shared-node-drag-moves-both-branches",
        "undo-redo-exact", "fork-from-a-node", "closed-route-by-first-node-click",
        "road-under-higher-land", "road-on-land-of-its-level", "road-under-water-by-default",
        "road-over-water-after-order", "road-moves-to-another-layer-whole", "preset-changes-the-drawing",
        "own-branch-style-keeps-the-node", "save-open-keeps-network-and-order", "reopened-road-editable",
        "export-full-size-with-road", "far-zoom-keeps-road", "select-tool-picks-road-body",
        "delete-key-removes-road", "undo-restores-deleted-road", "delete-key-in-text-box-keeps-the-map",
        "road-in-object-list", "resources-disposed",
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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "stage20-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — Stage 20", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        _window.AddHandler(InputElement.KeyDownEvent,
            (object? _, KeyEventArgs args) =>
            { if (EditorKeyRouting.TryHandleKey(_window, _view, _shortcuts, args)) args.Handled = true; },
            RoutingStrategies.Bubble, handledEventsToo: true);
        OpenEditor(Settlement());
        _report.Required = FullRequired;
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

    /// <summary>
    /// A small settlement made with the existing means: grass land over the whole map, a plateau
    /// of a higher level in the lower right, a few house rectangles and an illustrated river.
    /// </summary>
    private static MapDocument Settlement()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        for (var x = 0; x < 4; x++) for (var y = 0; y < 3; y++) tiles = tiles.With(new TileKey(x, y), coverage);
        var grass = new LandLayer(LayerId.New(), "Луг · уровень 0", true, false, 1, tiles,
            new LandEdgeStyle { FillColor = "#FF9DB36F", OutlineWidth = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0, RippleCount = 0 });
        var plateau = new LandLayer(LayerId.New(), "Холм · уровень 1", true, false, 1, TileMap.Empty(1).With(new TileKey(3, 2), coverage),
            new LandEdgeStyle { FillColor = "#FF8AA35E", OutlineColor = "#FF4B5A31", OutlineWidth = 1.5, InnerShadowOpacity = 0.1, OuterShadowOpacity = 0.12, RippleCount = 0 }) { Level = 1 };
        var houses = new List<SceneObject>();
        var index = 0;
        foreach (var (x, y, w, h, angle) in new[] { (150, 330, 46, 34, 8.0), (380, 300, 52, 40, -6.0), (620, 360, 44, 44, 0.0), (330, 470, 40, 30, 14.0), (560, 560, 48, 36, -10.0) })
            houses.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Дом " + ++index, x, y, w, h, angle, "#FF8E6B4E", "#FF3A2A1B", 1.5, true, false));
        var river = new River
        {
            Name = "Река", Level = 0, Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(860, 10), new VectorPoint(840, 300), new VectorPoint(880, 560), new VectorPoint(850, 760)], 22)],
        };
        var objects = new ObjectLayer(LayerId.New(), "Поселение", true, false, 1, houses) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), "Дороги поселения", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FF9DB36F"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF9DB36F"), grass, plateau, objects]);
    }

    private void OpenEditor(MapDocument document, string? path = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "roads.mapproj")), _prompts, new LocalLog(),
            exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "roads-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    private async Task Execute()
    {
        await Settle();
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1024, _view.Canvas.Bounds.Height / 768));
        await Click("RoadToolButton");
        Require(_editor.ActiveTool == EditorTool.Road, "Rail button did not activate the road tool");

        // 1. The main road: an S-bend of four nodes from the panel and the real canvas router.
        await Number("NewRoadWidthBox", "22");
        await Click("StartRoadButton");
        var before = _editor.Session.Document;
        Point(60, 430); Point(300, 380); Point(520, 470); Point(960, 380);
        _report.Checks["draft-outside-document-history"] = ReferenceEquals(before, _editor.Session.Document) && _editor.Session.UndoDepth == 0 && _editor.HasRoadGesture;
        await Click("FinishRoadButton");
        Require(_editor.SelectedRoad is { Branches.Length: 1 }, "Native controls did not create the road: " + _editor.StatusDetail);
        var road = _editor.SelectedRoad!;
        var main = road.Branches[0];
        _report.Checks["create-four-nodes"] = main.Vertices.Length == 4 && _editor.Session.UndoDepth == 1;

        // 2. A path from the middle of the first stretch: the click inserts a real shared node.
        await Number("NewRoadWidthBox", "6");
        await Click("StartRoadButton");
        var geometry = RoadGeometry.Evaluate(_editor.Roads[0]).Branches[main.Id];
        var middle = RoadGeometry.PointAt(geometry, 0, 0.55);
        Point(middle.X, middle.Y); Point(230, 300); Point(210, 200);
        await Click("FinishRoadButton");
        Require(_editor.Roads.Count == 1 && _editor.Roads[0].Branches.Length == 2, "Path was not attached: " + _editor.StatusDetail);
        road = _editor.Roads[0];
        var path = road.Branches[1];
        var shared = path.Vertices[0].NodeId;
        _report.Checks["branch-from-the-middle-shares-a-node"] = road.IsShared(shared)
            && road.FindBranch(main.Id)!.Vertices.Length == 5 && road.FindBranch(main.Id)!.Vertices[1].NodeId == shared
            && path.Vertices[0].Width == 6;
        _report.SharedNodeBefore = $"{shared}:{road.FindNode(shared)!.X:0.#},{road.FindNode(shared)!.Y:0.#}";

        // 3. Widths from the panel: the whole path, then one vertex of the main road.
        _editor.SelectRoad(road.Id, path.Id); _editor.RoadLevel = RoadSelectionLevel.Branch; await Settle();
        await Number("RoadBranchWidthBox", "8");
        _report.Checks["branch-width-from-panel"] = _editor.Roads[0].FindBranch(path.Id)!.Vertices.All(v => v.Width == 8)
            && _editor.Roads[0].FindBranch(main.Id)!.Vertices.All(v => v.Width > 20);
        _editor.SelectRoad(road.Id, main.Id); _editor.RoadLevel = RoadSelectionLevel.Node; _editor.SelectRoadVertex(4); await Settle();
        await Number("RoadVertexWidthBox", "30");
        _report.Checks["node-width-from-panel"] = _editor.Roads[0].FindBranch(main.Id)!.Vertices[4].Width == 30;
        var widened = _editor.Session.Document;

        // 4. Drag the shared node on the canvas: the main road and the path follow together.
        _editor.SelectRoadVertex(1); await Settle();
        var node = _editor.Roads[0].FindNode(shared)!;
        var depth = _editor.Session.UndoDepth;
        Drag(node.X, node.Y, node.X + 30, node.Y - 50); await Frame();
        road = _editor.Roads[0];
        var movedNode = road.FindNode(shared)!;
        var set = RoadGeometry.Evaluate(road);
        _report.SharedNodeAfter = $"{shared}:{movedNode.X:0.#},{movedNode.Y:0.#}";
        _report.Checks["shared-node-drag-moves-both-branches"] = Math.Abs(movedNode.X - node.X - 30) < 1.5 && Math.Abs(movedNode.Y - node.Y + 50) < 1.5
            && _editor.Session.UndoDepth == depth + 1
            && Math.Abs(set.Branches[path.Id].Start.X - movedNode.X) < 1e-6
            && set.Branches[main.Id].Samples[set.Branches[main.Id].NodeSampleIndex[1]].X == movedNode.X;
        var moved = _editor.Session.Document;
        _editor.Undo(); var undone = ReferenceEquals(widened, _editor.Session.Document);
        _editor.Redo(); _report.Checks["undo-redo-exact"] = undone && ReferenceEquals(moved, _editor.Session.Document);

        // 5. An acute fork from the third node of the main road.
        await Number("NewRoadWidthBox", "14");
        await Click("StartRoadButton");
        var forkNode = _editor.Roads[0].FindNode(_editor.Roads[0].FindBranch(main.Id)!.Vertices[3].NodeId)!;
        Point(forkNode.X, forkNode.Y); Point(640, 640); Point(800, 700);
        await Click("FinishRoadButton");
        road = _editor.Roads[0];
        _report.Checks["fork-from-a-node"] = road.Branches.Length == 3 && road.Branches[2].Vertices[0].NodeId == forkNode.Id && road.Degree(forkNode.Id) == 3;
        _report.Branches = road.Branches.Length; _report.Nodes = road.Nodes.Length;

        // 6. A closed route around a block: the fourth click lands on the first node.
        await Number("NewRoadWidthBox", "9");
        await Click("StartRoadButton");
        Point(330, 90); Point(470, 80); Point(490, 220); Point(340, 240); Point(330, 90);
        var loop = _editor.Roads.FirstOrDefault(r => r.Id != road.Id);
        _report.Checks["closed-route-by-first-node-click"] = loop is { Branches.Length: 1 } && loop.Branches[0].Closed && loop.Branches[0].Vertices.Length == 4 && !_editor.IsCreatingRoad;
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".editor.png");
        SaveVisual(_report.Screenshot);
        SaveCanvas(Path.ChangeExtension(Options.OutputPath, ".canvas.png"));
        using (var plain = Bitmap()) Save(plain, "map-plain");
        using (var fork = Bitmap(new WorldBounds(forkNode.X - 160, forkNode.Y - 120, forkNode.X + 160, forkNode.Y + 120), 2.5)) Save(fork, "fork-plain-2x");
        using (var entry = Bitmap(new WorldBounds(movedNode.X - 120, movedNode.Y - 90, movedNode.X + 120, movedNode.Y + 90), 3)) Save(entry, "path-entry-plain-3x");

        // 7. Levels: under the plateau of level 1 the road disappears; at level 1 it lies on it.
        var plateauPoint = RoadGeometry.PointAt(RoadGeometry.Evaluate(road).Branches[road.Branches[2].Id], 1, 0.97); // on the fork, well inside the plateau tile (3,2)
        _editor.SelectRoadOnly(road.Id); _editor.RoadLevel = RoadSelectionLevel.Road;
        using (var covered = Bitmap()) _report.Checks["road-under-higher-land"] = IsLand(covered.GetPixel((int)plateauPoint.X, (int)plateauPoint.Y));
        _editor.SetSelectedRoadLevel(1);
        using (var lifted = Bitmap()) _report.Checks["road-on-land-of-its-level"] = IsRoad(lifted.GetPixel((int)plateauPoint.X, (int)plateauPoint.Y));
        _editor.Undo();

        // 8. Order with water: the river lies over the road by default; «Наверх» puts the road over it.
        var river = _editor.Rivers[0];
        var mainGeometry = RoadGeometry.Evaluate(_editor.Roads[0]).Branches[main.Id];
        var crossing = mainGeometry.Samples.MinBy(s => Math.Abs(s.X - 855))!;
        using (var underWater = Bitmap()) _report.Checks["road-under-water-by-default"] = IsWater(underWater.GetPixel((int)crossing.X, (int)crossing.Y));
        _editor.SelectRoadOnly(road.Id);
        Require(_editor.ReorderSelection(1, toExtreme: true), "Reorder refused: " + _editor.StatusDetail);
        using (var overWater = Bitmap()) _report.Checks["road-over-water-after-order"] = IsRoad(overWater.GetPixel((int)crossing.X, (int)crossing.Y));
        _editor.Undo();
        _ = river;

        // 9. Another layer: the whole network travels, the fork stays shared.
        _editor.AddObjectLayer(); await Settle();
        var second = _editor.Session.Document.Layers.OfType<ObjectLayer>().Last();
        _editor.SelectRoadOnly(road.Id);
        Require(_editor.MoveSelectionToLayer(second.Id), "Layer move refused: " + _editor.StatusDetail);
        var travelled = ((ObjectLayer)_editor.Session.Document.FindLayer(second.Id)!).Roads.SingleOrDefault(r => r.Id == road.Id);
        _report.Checks["road-moves-to-another-layer-whole"] = travelled is not null && travelled.Branches.Length == 3 && travelled.IsShared(shared)
            && ((ObjectLayer)_editor.Session.Document.FindLayer(second.Id)!).Roads.Length == 1;
        _editor.Undo(); _editor.Undo(); await Settle();
        Require(_editor.Roads.Any(r => r.Id == road.Id), "Undo of the layer move lost the road");

        // 10. Presets from the panel change the way of drawing; a branch may keep its own look.
        await Click("RoadToolButton");
        _editor.SelectRoad(road.Id, main.Id); _editor.RoadLevel = RoadSelectionLevel.Road; await Settle();
        await Click("RoadPresetPenRuts");
        var pen = _editor.Roads.Single(r => r.Id == road.Id);
        _report.Checks["preset-changes-the-drawing"] = pen.Style.Drawing == RoadDrawing.PenRuts && pen.Style.EdgeOpacity > 0.7;
        using (var penMap = Bitmap()) Save(penMap, "map-pen");
        using (var penFork = Bitmap(new WorldBounds(forkNode.X - 160, forkNode.Y - 120, forkNode.X + 160, forkNode.Y + 120), 2.5)) Save(penFork, "fork-pen-2x");
        _editor.Undo();
        _editor.SelectRoad(road.Id, path.Id); _editor.RoadLevel = RoadSelectionLevel.Branch; await Settle();
        _editor.SetSelectedBranchOwnStyle(true); await Settle(); // the toggle calls exactly this
        await Click("RoadPresetPainterly");
        var styled = _editor.Roads.Single(r => r.Id == road.Id);
        _report.Checks["own-branch-style-keeps-the-node"] = styled.FindBranch(path.Id)!.Style?.Drawing == RoadDrawing.Painterly
            && styled.Style.Drawing == RoadDrawing.Country && styled.IsShared(shared)
            && styled.FindBranch(main.Id)!.Vertices.SequenceEqual(road.FindBranch(main.Id)!.Vertices);
        using (var mixed = Bitmap(new WorldBounds(movedNode.X - 120, movedNode.Y - 90, movedNode.X + 120, movedNode.Y + 90), 3)) Save(mixed, "path-entry-two-looks-3x");
        await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".styled.png"));

        // 11. Save, reopen, edit again, export.
        await Click("SaveButton");
        await Until(() => File.Exists(Path.Combine(_workspace, "roads.mapproj")) && !_editor.Session.IsDirty);
        _report.SavedProject = Path.Combine(_workspace, "roads.mapproj");
        var opened = MapProjectStore.Open(_report.SavedProject).Document;
        var reopened = opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Single(r => r.Id == road.Id);
        _report.Checks["save-open-keeps-network-and-order"] = reopened.Branches.Length == 3 && reopened.IsShared(shared)
            && reopened.FindBranch(path.Id)!.Style?.Drawing == RoadDrawing.Painterly
            && reopened.FindBranch(main.Id)!.Vertices[4].Width == 30
            && opened.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Count() == 2
            && LayerSurfaceOrder.Resolve(opened.Layers.OfType<ObjectLayer>().First(l => l.Roads.Length > 0)).Last().Kind == SurfaceKind.Water;
        _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches(); OpenEditor(opened, _report.SavedProject);
        await Settle();
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1024, _view.Canvas.Bounds.Height / 768));
        _editor.SelectRoad(road.Id, path.Id); _editor.RoadLevel = RoadSelectionLevel.Branch; await Settle();
        await Number("RoadBranchEndWidthBox", "12");
        _report.Checks["reopened-road-editable"] = _editor.Roads.Single(r => r.Id == road.Id).FindBranch(path.Id)!.Vertices[^1].Width == 12;
        await Click("SaveButton"); await Until(() => !_editor.Session.IsDirty);

        await Click("ExportButton");
        await Until(() => _editor.CanStartExport);
        var exportWindow = ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Windows.Single(w => w.Name == "ExportWorkflowWindow");
        var exportButton = exportWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StartExportButton");
        exportButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => _editor.ExportResultPath is not null);
        _report.Export = Path.Combine(_workspace, "roads-export.png");
        using (var bitmap = SKBitmap.Decode(_report.Export))
        {
            var onRoad = RoadGeometry.PointAt(RoadGeometry.Evaluate(_editor.Roads.Single(r => r.Id == road.Id)).Branches[main.Id], 2, 0.5);
            _report.Checks["export-full-size-with-road"] = bitmap is { Width: 1024, Height: 768 } && IsRoad(bitmap.GetPixel((int)onRoad.X, (int)onRoad.Y));
        }
        _view.CloseAuxiliaryWindowsForSmoke();

        // 12. Far zoom keeps the road as a thread.
        _editor.Session.Viewport.Set(0, 0, .04); await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".far.png"));
        _report.Checks["far-zoom-keeps-road"] = _view.Canvas.LastFrameObjectsDrawn > 0;
        _editor.Session.Viewport.Set(0, 0, Math.Min(_view.Canvas.Bounds.Width / 1024, _view.Canvas.Bounds.Height / 768)); await Frame();

        // 13. The select tool picks the road by its body; Delete and Undo through the real key routing.
        _editor.ClearRoadSelection(); _editor.ClearWaterSelection();
        await Click("SelectToolButton");
        var body = RoadGeometry.Evaluate(_editor.Roads.Single(r => r.Id == road.Id)).Branches[main.Id];
        var sample = body.Samples[body.Samples.Length / 3];
        Point(sample.X - sample.TangentY * sample.HalfWidth * 0.7, sample.Y + sample.TangentX * sample.HalfWidth * 0.7); await Settle();
        _report.Checks["select-tool-picks-road-body"] = _editor.ActiveTool == EditorTool.Select && _editor.RoadSelectionCount == 1 && _editor.RoadSelectionIds[0] == road.Id;
        _report.Checks["road-in-object-list"] = _editor.ObjectTree.Any(row => row.Kind == ObjectTreeRowKind.Road && row.RoadId == road.Id && row.Selected);
        var roadsBefore = _editor.Roads.Count; var depthBefore = _editor.Session.UndoDepth;
        _view.Canvas.Focus(); await Settle();
        PressKey(_view.Canvas, Key.Delete); await Settle();
        _report.Checks["delete-key-removes-road"] = _editor.Roads.Count == roadsBefore - 1 && _editor.Roads.All(r => r.Id != road.Id) && _editor.Session.UndoDepth == depthBefore + 1;
        _editor.Undo(); await Settle();
        _report.Checks["undo-restores-deleted-road"] = _editor.Roads.Count == roadsBefore && _editor.Roads.Any(r => r.Id == road.Id);
        await Click("RoadToolButton");
        _editor.SelectRoad(road.Id); await Settle();
        var nameBox = Find<TextBox>("RoadNameBox");
        nameBox.BringIntoView(); nameBox.Focus(); await Settle();
        var beforeTyping = _editor.Session.Document;
        PressKey(nameBox, Key.Delete); await Settle();
        _report.Checks["delete-key-in-text-box-keeps-the-map"] = ReferenceEquals(beforeTyping, _editor.Session.Document) && _editor.Roads.Count == roadsBefore;
        _view.Canvas.Focus(); await Frame();
        SaveVisual(Path.ChangeExtension(Options.OutputPath, ".reopened.png"));
    }

    private static bool IsLand(SKColor c) => c.Green > c.Red + 10 && c.Green > c.Blue + 30;
    private static bool IsRoad(SKColor c) => c.Red >= c.Green - 6 && c.Red > 150 && c.Blue < c.Red;
    private static bool IsWater(SKColor c) => c.Blue > c.Red + 10 || (c.Green > c.Red + 20 && c.Blue > c.Red + 5);

    private SKBitmap Bitmap(WorldBounds? region = null, double scale = 1)
    {
        var view = region ?? new WorldBounds(0, 0, 1024, 768);
        var info = new SKImageInfo((int)((view.Right - view.Left) * scale), (int)((view.Bottom - view.Top) * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(surface.Canvas, _editor.Session.Document, new RenderOptions { IsExport = true, VisibleWorld = view, ViewScale = scale });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static void Save(SKBitmap bitmap, string suffix)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.ChangeExtension(Options.OutputPath, suffix + ".png"));
        data.SaveTo(file);
    }

    private void SaveCanvas(string path)
    {
        using var canvas = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_view.Canvas.Bounds.Width), (int)Math.Ceiling(_view.Canvas.Bounds.Height)));
        canvas.Render(_view.Canvas);
        using var file = File.Create(path);
        canvas.Save(file, PngBitmapEncoderOptions.Default);
    }

    private static void PressKey(InputElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });

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
        Stage19Application.RevealTab(_view, name);
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
