using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Fonts.Inter;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// The rendering audit of 2026-09-08: frames captured INSIDE a gesture, not after it. The
/// pointer is pressed and moved through the production router, the canvas is asked to draw
/// while the button is still down, and only then is the frame read. The same is done with an
/// armed placement, a selected object and a drag in flight. Every land pixel far from the
/// gesture has to equal the idle frame; the finished-land cache has to be what the real window
/// drew from. The second half walks the sections of the right panel on a mixed map.
/// </summary>
internal sealed class NativeRenderAuditReport
{
    public string Observation { get; } = "Own native Avalonia window, ShowActivated=false, off-screen; the production canvas router in three halves (press, move, release) with frames read between them; no system input. Frames are Avalonia renders of the real canvas control, not OS scan-out.";
    public Dictionary<string, bool> Checks { get; } = [];
    public Dictionary<string, string> Notes { get; } = [];
    public Dictionary<string, double> Timings { get; } = [];
    public List<string> Frames { get; } = [];
    public string? SavedProject { get; set; }
    public string? Failure { get; set; }
    public bool GatePassed => Failure is null && Checks.Count >= 50 && Checks.Values.All(v => v);
}

internal static class NativeRenderAuditSmoke
{
    public static NativeRenderAuditReport Run(PerformanceOptions options)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource<NativeRenderAuditReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            RenderAuditApplication.Options = options; RenderAuditApplication.Completion = completion;
            try { AppBuilder.Configure<RenderAuditApplication>().UsePlatformDetect().WithInterFont()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromMinutes(4)).GetAwaiter().GetResult();
    }
}

public sealed class RenderAuditApplication : Application
{
    internal static PerformanceOptions Options = null!;
    internal static TaskCompletionSource<NativeRenderAuditReport> Completion = null!;
    private readonly NativeRenderAuditReport _report = new();
    private string _workspace = "";
    private Window _window = null!;
    private EditorView _view = null!;
    private EditorViewModel _editor = null!;
    private EditorShortcutSettings _shortcuts = null!;
    private AssetCatalog _catalog = null!;
    private LayerId _landId, _waterLayerId;

    /// <summary>What the real window's last frame reported, read before any diagnostic capture can overwrite it.</summary>
    private (bool Fast, long Hits, long Misses, bool Gpu, double Ms, RenderLodTier Tier, long TerrainHits, long TerrainMisses) _frame;

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
        _workspace = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "render-audit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
        Environment.SetEnvironmentVariable("MAP_UI_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("MAP_UI_TEST_PROFILE_ROOT", Path.Combine(_workspace, "profile"));
        _catalog = new AssetCatalog(Path.Combine(_workspace, "catalog"));
        _window = new Window { Width = 1440, Height = 900, Title = "Map — render audit", ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
        _window.AddHandler(InputElement.KeyDownEvent,
            (object? _, KeyEventArgs args) =>
            { if (EditorKeyRouting.TryHandleKey(_window, _view, _shortcuts, args)) args.Handled = true; },
            RoutingStrategies.Bubble, handledEventsToo: true);
        OpenEditor(Scene());
        _window.Opened += async (_, _) =>
        {
            try { await Execute(); }
            catch (Exception ex)
            {
                _report.Failure = ex.ToString();
                SaveVisual("failure");
            }
            finally
            {
                _view.CloseAuxiliaryWindowsForSmoke(); _view.Canvas.DisposeRenderCaches();
                _report.Checks["resources-disposed"] = _view.Canvas.ObjectCacheCount == 0 && _view.Canvas.TileCacheCount == 0;
                await _catalog.FlushMetadataAsync();
                _window.Close(); Completion.TrySetResult(_report); desktop.Shutdown();
            }
        };
        desktop.MainWindow = _window; base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Textured green land over the upper-left 768×512 with a painted patch, one river leaving
    /// it into the sea, a lake, and an object layer that will hold stamps and a road.
    /// </summary>
    private MapDocument Scene()
    {
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        for (var x = 0; x < 3; x++) for (var y = 0; y < 2; y++) tiles = tiles.With(new TileKey(x, y), coverage);
        var paint = new byte[TileMap.TileSize * TileMap.TileSize * 4];
        for (var y = 60; y < 200; y++) for (var x = 40; x < 220; x++)
        { var o = (y * TileMap.TileSize + x) * 4; paint[o] = 184; paint[o + 1] = 120; paint[o + 2] = 70; paint[o + 3] = 255; }
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1, tiles,
            new LandEdgeStyle { FillColor = "#FF7FA35A", FillTextureId = "owner-snow-drifts", FillTextureOpacity = 0.7, FillTextureScale = 256 })
        { PaintTiles = TileMap.Empty(4).With(new TileKey(1, 0), paint) };
        _landId = land.Id;
        var river = new River
        {
            Name = "Река", Style = RiverStyle.Illustrated() with { TextureId = "water-still", TextureOpacity = 0.5 },
            Channels = [RiverEditor.NewChannel([new VectorPoint(120, 60), new VectorPoint(360, 260), new VectorPoint(640, 420), new VectorPoint(900, 680)], 26)],
        };
        var lake = new River
        {
            Name = "Озеро", Style = RiverStyle.Illustrated(),
            Channels = [RiverEditor.NewChannel([new VectorPoint(520, 90), new VectorPoint(640, 70), new VectorPoint(690, 150), new VectorPoint(600, 210), new VectorPoint(500, 170)], 20) with { Closed = true }],
        };
        var road = RoadEditor.NewRoad([new(60, 560), new(380, 600), new(700, 540)], 14, RoadStyle.Country(),
            "Старый тракт от переправы до дальних холмов за лесом", seed: 7);
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river, lake], Roads = [road] };
        _waterLayerId = objects.Id;
        return new MapDocument(DocumentId.New(), "Аудит отображения", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FF6E9FBF"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF6E9FBF"), land, objects]);
    }

    private void OpenEditor(MapDocument document, string? path = null)
    {
        _editor = new EditorViewModel(document, path, false, new ProjectStore(new RecoveryJournal(Path.Combine(_workspace, "recovery"))),
            new LocalRecent(), new LocalPicker(Path.Combine(_workspace, "audit.mapproj")), new LocalPrompts(), new LocalLog(),
            assetCatalog: _catalog,
            exportDestinationPicker: new ExportPicker(Path.Combine(_workspace, "audit-export.png")),
            imageExporter: new ImageExportEngine(new ExportEngineSettings { TileEdge = 256 }),
            localPreferences: new EditorLocalPreferences(PrimaryAutosaveEnabled: false));
        _view = new EditorView(_editor, () => Task.CompletedTask, () => { }, () => { }); _window.Content = _view;
    }

    // Land far from every gesture of the run: the frame there must never change.
    private static readonly (double X, double Y)[] LandProbes = [(90, 430), (60, 300), (120, 480), (740, 100), (200, 330)];

    private async Task Execute()
    {
        await Settle();
        await Warm("idle");
        var idle = await Capture("idle");
        Check("idle-frame-has-gpu-context", _frame.Gpu);
        Check("idle-frame-is-exact", !_frame.Fast && _frame.Tier == RenderLodTier.Exact);
        var stoneOnLand = PixelAt(idle, 90, 430);
        var plainGreen = new SKColor(0x7F, 0xA3, 0x5A);
        Check("idle-land-carries-material", Distance(stoneOnLand, plainGreen) > 6);

        // 1. Land shape stroke: press, move while held, frame, release.
        _editor.ActiveTool = EditorTool.Land; _editor.SetLandPaintMode(false);
        _editor.ActiveLandShape = LandShapeKind.Circle;
        _editor.BrushSettings = _editor.BrushSettings with { Radius = 40, Softness = 0.3 };
        await Warm("land-tool");
        Press(600, 490); Move(620, 550); await Frame();
        var firstHeld = await Capture("land-stroke-first-held");
        Check("land-stroke-first-held-shows-new-ground", PixelAt(firstHeld, 620, 550) != new SKColor(0x6E, 0x9F, 0xBF));
        Move(690, 580);
        await Frame();
        // A panel may still be settling its size after the tool switch; the held state is judged
        // once the view has stopped changing (at most two more frames).
        for (var extra = 0; extra < 2 && _frame.Hits == 0; extra++) await Frame();
        var held = await Capture("land-stroke-held");
        Check("land-stroke-held-is-not-fast-terrain", !_frame.Fast);
        Check("land-stroke-held-drew-from-finished-land", _frame.Hits > 0);
        // A frame of a live stroke draws the kept idle terrain (or builds it once) and lays the
        // stroke over it; it never rebuilds every coast per frame and never stores the stroke.
        Check("land-stroke-gpu-frame-reuses-whole-terrain-cache", _frame.Gpu && _frame.TerrainHits + _frame.TerrainMisses >= 1);
        Note("stroke-held-scene-ms", _frame.Ms.ToString("0.0") + $" hits={_frame.Hits} misses={_frame.Misses} reason={_view.Canvas.LastStyledLandMiss}");
        Check("land-stroke-held-keeps-material-around", SameLand(idle, held, "land-stroke-held"));
        var newGround = PixelAt(held, 660, 568);
        Check("land-stroke-held-new-ground-has-material", newGround != new SKColor(0x6E, 0x9F, 0xBF) && Distance(newGround, plainGreen) > 6);
        Check("land-stroke-continues-after-first-frame", PixelAt(held, 690, 580) != PixelAt(firstHeld, 690, 580));
        Release(690, 580); await Frame();
        var released = await Capture("land-stroke-released");
        Check("land-stroke-released-keeps-material-around", SameLand(idle, released, "land-stroke-released"));
        var landAfter = (LandLayer)_editor.Session.Document.FindLayer(_landId)!;
        Check("land-stroke-added-ground", TerrainOrder.CoverageAt(landAfter, 660, 568) > 127 && _editor.Session.UndoDepth == 1);
        var grownGround = PixelAt(released, 660, 568);
        Check("land-stroke-released-new-ground-has-material", Distance(grownGround, plainGreen) > 6 && grownGround != new SKColor(0x6E, 0x9F, 0xBF));
        Check("land-shape-preview-keeps-committed-material-colour", Distance(newGround, grownGround) <= 3);
        Note("land-shape-preview-vs-commit-colour-diff", Distance(newGround, grownGround).ToString());

        // A cancelled stroke leaves nothing: neither pixels nor history.
        Press(300, 600); Move(340, 620); await Frame();
        _view.Canvas.CancelGestureForSmoke(); await Frame();
        var cancelled = await Capture("land-stroke-cancelled");
        Check("cancelled-stroke-leaves-no-trace", SameLand(released, cancelled, "cancelled") && _editor.Session.UndoDepth == 1
            && PixelAt(cancelled, 320, 610) == PixelAt(released, 320, 610));

        // 2. Material brush: the held preview and the committed stroke agree.
        _editor.SetLandPaintMode(true); _editor.ChooseLandMaterial("sand");
        _editor.BrushSettings = _editor.BrushSettings with { Radius = 36, Softness = 0.6, Opacity = 1 };
        await Warm("paint-tool");
        Press(300, 380); Move(420, 400); await Frame();
        var paintHeld = await Capture("paint-held");
        // The live paint frame composes the land in full instead of drawing the kept image; the
        // 8-bit intermediate of that image rounds by at most one or two levels.
        Check("paint-held-keeps-material-around", SameLand(released, paintHeld, "paint-held", tolerance: 2));
        Release(420, 400); await Frame();
        var paintDone = await Capture("paint-released");
        Check("paint-preview-matches-commit", Similar(paintHeld, paintDone, 300, 370, 420, 410, tolerance: 6, out var paintDifference));
        Note("paint-preview-vs-commit-max-diff", paintDifference.ToString());
        Check("paint-changed-the-land", PixelAt(paintDone, 360, 390) != PixelAt(released, 360, 390));
        var baseline = paintDone;

        // 3. Objects: arm a stamp, carry the ghost, place with the tool still armed, select, drag.
        string Fixture(int number) => Path.Combine(Environment.CurrentDirectory, "tests", "fixtures", "assets", $"diagnostic-stamp-{number:00}.png");
        var set = _catalog.ImportSet([Fixture(4)], "Образец", new AssetImportOptions { Pack = "Аудит" }, DateTimeOffset.UnixEpoch);
        var second = _catalog.ImportSet([Fixture(5)], "Образец 5", new AssetImportOptions { Pack = "Аудит" }, DateTimeOffset.UnixEpoch);
        _editor.RefreshLibrary(); _view.RefreshLibraryPanel(); await Settle();
        Require(_editor.ChooseGalleryVariant(set.Id, null), "The diagnostic set could not be armed: " + _editor.StatusDetail);
        _editor.SetStampScalePercent(120, commit: true);
        await Warm("stamp-tool");
        MoveScreen(400, 300); await Frame();
        var armed = await Capture("placement-armed");
        Check("placement-armed-is-not-fast-terrain", !_frame.Fast && _editor.HasPendingPlacement);
        Check("placement-armed-keeps-material", SameLand(baseline, armed, "placement-armed"));
        Click(400, 300); await Frame();
        var placed = await Capture("placed-tool-armed");
        var objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToList();
        Check("placed-with-tool-still-armed", objects.Count == 1 && _editor.HasPendingPlacement);
        Check("placed-keeps-material", SameLand(baseline, placed, "placed"));
        _editor.ChooseGalleryVariant(second.Id, null); await Settle();
        Click(560, 340); await Frame();
        objects = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToList();
        Require(objects.Count == 2, "Second stamp was not placed");
        _editor.HandleEscape(); await Settle();
        Check("escape-disarms-placement", !_editor.HasPendingPlacement);

        _editor.ActiveTool = EditorTool.Select; await Warm("select-tool");
        var first = objects[0]; var next = objects[1];
        static (double X, double Y) Centre(SceneObject o) => (o.X + o.Width / 2, o.Y + o.Height / 2);
        var (firstX, firstY) = Centre(first); var (nextX, nextY) = Centre(next);
        Click(firstX, firstY); await Frame();
        var selected = await Capture("object-selected");
        Check("single-selection-keeps-material", _editor.SelectionCount == 1 && SameLand(baseline, selected, "selected"));
        Click(nextX, nextY, additive: true); await Frame();
        var multi = await Capture("objects-multi-selected");
        Check("multi-selection-keeps-material", _editor.SelectionCount == 2 && SameLand(baseline, multi, "multi"));
        Press(nextX, nextY); Move(nextX + 30, nextY + 20); await Frame();
        var dragging = await Capture("objects-drag-held");
        Check("drag-held-is-not-fast-terrain", !_frame.Fast && _editor.DragPreview is not null);
        Check("drag-held-keeps-material", SameLand(baseline, dragging, "drag-held"));
        Release(nextX + 30, nextY + 20); await Frame();
        var dragged = await Capture("objects-drag-released");
        var moved = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToList();
        Note("drag-positions", string.Join("; ", moved.Select(o => $"{o.Name}: {o.X:0},{o.Y:0}")));
        Check("drag-released-keeps-material-and-moved", SameLand(baseline, dragged, "drag-released")
            && moved.All(o => Math.Abs(o.X - (o.Id == first.Id ? first.X : next.X) - 30) < 1));
        _editor.Session.ClearSelection(); await Frame();
        var deselected = await Capture("objects-deselected");
        Check("deselecting-changes-nothing-on-land", SameLand(baseline, deselected, "deselected"));

        await PanelAndSwatchChecks(baseline);

        // 4. Water gestures stay fast on purpose, and the frame after them is exact again.
        _editor.ActiveTool = EditorTool.River; await Settle();
        var waterLayer = (ObjectLayer)_editor.Session.Document.FindLayer(_waterLayerId)!;
        var river = waterLayer.Rivers[0];
        _editor.SelectRiver(river.Id, river.Channels[0].Id); await Warm("river-tool");
        var node = river.Channels[0].Nodes[1];
        Press(node.X, node.Y); Move(node.X + 10, node.Y + 24); await Frame();
        Check("river-node-drag-uses-fast-terrain", _frame.Fast);
        Release(node.X + 10, node.Y + 24); await Frame();
        Check("river-node-drag-ends-exact", !_frame.Fast
            && _editor.Rivers[0].Channels[0].Nodes[1].Y > node.Y + 10);
        _editor.Undo(); _editor.ClearWaterSelection(); await Frame();
        var undone = await Capture("river-undo");
        Check("river-undo-restores-cut", _editor.Rivers[0].Channels[0].Nodes[1].Y == node.Y && SameLand(baseline, undone, "river-undo"));

        // Where the frame time goes: the same view without water, then without land.
        var waterIds = _editor.Rivers.Select(r => r.Id).ToList();
        foreach (var id in waterIds) { _editor.SelectWater(id); _editor.SetSelectedWaterVisible(false); }
        _editor.ClearWaterSelection(); await Warm("no-water");
        foreach (var _ in waterIds) _editor.Undo();
        _editor.ToggleLayerVisibility(_landId); await Warm("no-land");
        _editor.ToggleLayerVisibility(_landId); await Warm("all-back");

        await _catalog.FlushMetadataAsync();
        Require(await _editor.SaveAsync(), "The audit map did not save");
        _report.SavedProject = Path.Combine(_workspace, "audit.mapproj");
        SaveVisual("editor");

        // Save → reopen → the same picture: land coverage and painting, rivers, roads and stamps
        // come back as saved, and the reopened map renders to the same pixels as the edited one.
        var reopened = MapProjectStore.Open(_report.SavedProject).Document;
        var current = _editor.Session.Document;
        var savedLand = (LandLayer)reopened.FindLayer(_landId)!; var liveLand = (LandLayer)current.FindLayer(_landId)!;
        static bool SameTiles(TileMap a, TileMap b) => a.TileCount == b.TileCount
            && a.Keys.All(k => b.TryGet(k) is { } bytes && a.TryGet(k)!.AsSpan().SequenceEqual(bytes));
        var savedObjects = (ObjectLayer)reopened.FindLayer(_waterLayerId)!; var liveObjects = (ObjectLayer)current.FindLayer(_waterLayerId)!;
        Check("reopen-keeps-land-and-painting", SameTiles(savedLand.GeometryTiles, liveLand.GeometryTiles)
            && SameTiles(savedLand.PaintTiles, liveLand.PaintTiles) && savedLand.EdgeStyle == liveLand.EdgeStyle);
        Check("reopen-keeps-water-roads-and-stamps", savedObjects.Rivers.Length == liveObjects.Rivers.Length
            && savedObjects.Rivers.Zip(liveObjects.Rivers).All(p => p.First.Id == p.Second.Id && p.First.Style == p.Second.Style
                && p.First.Channels.Length == p.Second.Channels.Length
                && p.First.Channels.Zip(p.Second.Channels).All(c => c.First.Nodes.SequenceEqual(c.Second.Nodes)))
            && savedObjects.Roads.Length == liveObjects.Roads.Length
            && savedObjects.Roads.Zip(liveObjects.Roads).All(p => p.First.Nodes.SequenceEqual(p.Second.Nodes) && p.First.Branches.Length == p.Second.Branches.Length)
            && savedObjects.Objects.Count == liveObjects.Objects.Count
            && savedObjects.Objects.Zip(liveObjects.Objects).All(p => p.First.Id == p.Second.Id && Math.Abs(p.First.X - p.Second.X) < 1e-6 && Math.Abs(p.First.Y - p.Second.Y) < 1e-6));
        static SKBitmap Export(MapDocument document)
        {
            var info = new SKImageInfo(1024, 768);
            using var surface = SKSurface.Create(info)!;
            SceneRenderer.Render(surface.Canvas, document, new Map.Rendering.RenderOptions { IsExport = true, ViewScale = 1 });
            var bitmap = new SKBitmap(info); surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0); return bitmap;
        }
        using (var liveFrame = Export(current))
        using (var savedFrame = Export(reopened))
        {
            var worst = 0;
            for (var y = 0; y < 768; y += 3) for (var x = 0; x < 1024; x += 3)
                worst = Math.Max(worst, Distance(liveFrame.GetPixel(x, y), savedFrame.GetPixel(x, y)));
            Note("reopen-export-max-diff", worst.ToString());
            Check("reopened-map-renders-the-same", worst == 0);
            using var image = SKImage.FromBitmap(savedFrame); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.ChangeExtension(Options.OutputPath, ".export-reopened.png")); data.SaveTo(file);
            _report.Frames.Add(Path.ChangeExtension(Options.OutputPath, ".export-reopened.png"));
        }
    }

    // ------------------------------------------------------------------ panel and swatches

    /// <summary>Finds a control, unfolding collapsed categories the way a user opens them until it exists.</summary>
    private T Find<T>(string name) where T : Control
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (_view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) is { } found) return found;
            var folded = _view.GetVisualDescendants().OfType<Expander>().Where(e => !e.IsExpanded).ToArray();
            if (folded.Length == 0) break;
            foreach (var expander in folded) expander.IsExpanded = true;
            _view.UpdateLayout();
        }
        throw new InvalidOperationException(name + " is not in the window, even with every section opened");
    }

    private T? TryFind<T>(string name) where T : Control =>
        _view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    private async Task Press(string name)
    {
        var button = Find<Button>(name);
        Require(button.IsEffectivelyVisible && button.IsEnabled, name + " is unavailable");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
    }

    private static SKColor AverageOf(Border swatch)
    {
        if (swatch.Background is not ImageBrush { Source: Bitmap bitmap }) return SKColors.Transparent;
        using var stream = new MemoryStream();
        bitmap.Save(stream); stream.Position = 0;
        using var decoded = SKBitmap.Decode(stream);
        long r = 0, g = 0, b = 0, n = 0;
        for (var y = decoded.Height / 4; y < decoded.Height * 3 / 4; y += 4)
            for (var x = decoded.Width / 4; x < decoded.Width * 3 / 4; x += 4)
            { var p = decoded.GetPixel(x, y); r += p.Red; g += p.Green; b += p.Blue; n++; }
        return n == 0 ? SKColors.Transparent : new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static Border SwatchOf(Button button) => button.Content as Border ?? throw new InvalidOperationException("The swatch button has no swatch");

    /// <summary>
    /// The right panel walked section by section on the mixed map, and the material swatches
    /// read back as pixels: green land makes a grey stone show green; the brush sample stays raw.
    /// </summary>
    private async Task PanelAndSwatchChecks(Shot baseline)
    {
        _editor.ActiveTool = EditorTool.Select; _editor.Session.ClearSelection(); _editor.ClearWaterSelection();
        await Settle();
        Check("panel-has-six-sections", Enum.GetValues<PanelSection>().All(s => TryFind<ToggleButton>("PanelSection" + s) is not null));

        var rivers = _editor.Rivers; var roads = _editor.Roads;
        var before = _editor.Session.Document; var depth = _editor.Session.UndoDepth;
        var kinds = new Dictionary<PanelSection, Func<ObjectTreeRow, bool>>
        {
            [PanelSection.Land] = r => r.Kind == ObjectTreeRowKind.Land,
            [PanelSection.Water] = r => r.Kind == ObjectTreeRowKind.Water,
            [PanelSection.Assets] = r => r.Object?.Kind == SceneObjectKind.Stamp,
            [PanelSection.Roads] = r => r.Kind == ObjectTreeRowKind.Road,
            [PanelSection.Other] = r => r.Object is { } o && o.Kind != SceneObjectKind.Stamp,
        };
        var expectedCounts = new Dictionary<PanelSection, int>
        { [PanelSection.Land] = 1, [PanelSection.Water] = rivers.Count, [PanelSection.Assets] = 2, [PanelSection.Roads] = roads.Count, [PanelSection.Other] = 0 };
        foreach (var section in Enum.GetValues<PanelSection>())
        {
            await Press("PanelSection" + section); await Frame();
            var rows = _editor.ObjectTree;
            var realized = section == PanelSection.Layers
                ? _view.GetVisualDescendants().OfType<ToggleButton>().Count(b => b.Name?.StartsWith("Layer_", StringComparison.Ordinal) == true)
                : _view.GetVisualDescendants().OfType<ListBox>().Count(l => l.Name == "ObjectList" && l.IsVisible);
            var ok = section == PanelSection.Layers
                ? realized == _editor.Layers.Count
                : rows.Count == expectedCounts[section] && rows.All(kinds[section]) && (rows.Count == 0 || realized == 1)
                    && (rows.Count > 0 || TryFind<Control>("NoObjectsText") is { IsVisible: true });
            Check("section-" + section.ToString().ToLowerInvariant() + "-lists-only-its-kind", ok);
            Check("section-" + section.ToString().ToLowerInvariant() + "-footer-present",
                section == PanelSection.Layers ? TryFind<Control>("LayerNameBox") is { IsEffectivelyVisible: true }
                : section == PanelSection.Other ? TryFind<Control>("ObjectCompositions") is { IsEffectivelyVisible: true }
                : TryFind<Control>(section + "SectionFooter") is { IsEffectivelyVisible: true });
            SaveVisual("panel-" + section.ToString().ToLowerInvariant());
        }
        var afterSwitching = await Capture("after-section-switches");
        Check("switching-sections-changes-nothing", ReferenceEquals(before, _editor.Session.Document) && depth == _editor.Session.UndoDepth
            && SameLand(baseline, afterSwitching, "sections") && afterSwitching.Bitmap.Width == baseline.Bitmap.Width);

        // Row → canvas, canvas → section.
        await Press("PanelSectionWater"); await Settle();
        var lake = rivers.First(r => r.Channels[0].Closed);
        await Press("WaterRow_" + lake.Id.ToString("D"));
        Check("water-row-selects-on-canvas", _editor.WaterSelectionIds.SequenceEqual([lake.Id]) && _editor.WaterSelectionFrame is not null
            && Find<TextBlock>("WaterSectionSummary").Text!.Contains(lake.Name));
        var stamps = _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).ToList();
        Click(stamps[0].X + stamps[0].Width / 2, stamps[0].Y + stamps[0].Height / 2); await Settle();
        Check("canvas-stamp-opens-assets-section", _view.CurrentPanelSection == PanelSection.Assets && _editor.SelectionCount == 1);
        _view.SetObjectSearchText("no-match-assets"); await Settle();
        var riverBody = RiverGeometry.Evaluate(rivers.First(r => !r.Channels[0].Closed)).Channels.Values.First().Samples;
        var mid = riverBody[riverBody.Length / 8];
        Click(mid.X, mid.Y); await Settle();
        Check("canvas-river-opens-water-section", _view.CurrentPanelSection == PanelSection.Water && _editor.WaterSelectionCount == 1);
        Check("canvas-selection-drops-foreign-search", _editor.ObjectSearchText.Length == 0
            && string.IsNullOrEmpty(Find<TextBox>("ObjectSearchBox").Text)
            && _editor.ObjectTree.Any(row => row.WaterId == _editor.WaterSelectionIds.Single()));
        _editor.SelectLandAt(60, 300); await Settle();
        Check("canvas-land-opens-land-section", _view.CurrentPanelSection == PanelSection.Land && _editor.SelectedLand is not null);
        _editor.SelectRoadOnly(roads[0].Id); await Settle();
        Check("road-selection-opens-roads-section", _view.CurrentPanelSection == PanelSection.Roads
            && Find<TextBlock>("RoadSectionSummary").Text!.Contains(roads[0].Name[..12]));

        // Search stays inside its section and is dropped on a switch; Ctrl+A stays inside too.
        await Press("PanelSectionWater"); await Settle();
        _view.SetObjectSearchText("Озеро"); await Settle();
        Check("search-filters-the-section", _editor.ObjectTree.Count == 1 && _editor.ObjectTree[0].WaterId == lake.Id);
        await Press("PanelSectionRoads"); await Settle();
        Check("switch-drops-foreign-search", _editor.ObjectSearchText.Length == 0 && _editor.ObjectTree.Count == roads.Count);
        await Press("PanelSectionWater"); await Settle();
        await Press("SelectAllButton"); await Settle();
        Check("select-all-takes-water-only", _editor.WaterSelectionCount == rivers.Count && _editor.SelectionCount == 0 && _editor.RoadSelectionCount == 0);
        await Press("PanelSectionAssets"); await Settle();
        await Press("SelectAllButton"); await Settle();
        Check("select-all-takes-assets-only", _editor.SelectionCount == 2 && _editor.WaterSelectionCount == 0 && _editor.RoadSelectionCount == 0);
        _editor.Session.ClearSelection(); await Settle();

        // An empty result must replace the old selection too; Delete may not touch a hidden asset.
        foreach (var section in new[] { PanelSection.Water, PanelSection.Roads, PanelSection.Land })
        {
            _editor.SelectObject(stamps[0].Id); await Settle();
            Check("empty-select-all-starts-with-asset-selected/" + section, _editor.Session.SelectedObjectIds.Contains(stamps[0].Id));
            await Press("PanelSection" + section);
            _view.SetObjectSearchText("no-match-xyz"); await Settle();
            var unchanged = _editor.Session.Document;
            var undoDepth = _editor.Session.UndoDepth;
            await Press("SelectAllButton"); await Settle();
            Check("empty-select-all-clears-selection/" + section, _editor.SelectionCount == 0
                && _editor.WaterSelectionCount == 0 && _editor.RoadSelectionCount == 0 && _editor.SelectedLand is null);
            _view.Canvas.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
                Source = _view.Canvas, Key = Key.Delete });
            await Settle();
            Check("delete-after-empty-select-all-preserves-map/" + section,
                ReferenceEquals(unchanged, _editor.Session.Document) && _editor.Session.UndoDepth == undoDepth);
        }

        // A row switch hides one water system, never the layer or the other water.
        await Press("PanelSectionWater"); await Settle();
        await Press("WaterRowVisible_" + lake.Id.ToString("D")); await Settle();
        var owner = (ObjectLayer)_editor.Session.Document.FindLayer(_waterLayerId)!;
        Check("row-switch-hides-one-water-only", owner.Visible && owner.Rivers.Single(r => r.Id == lake.Id).Visible == false
            && owner.Rivers.Where(r => r.Id != lake.Id).All(r => r.Visible));
        _editor.Undo(); await Settle();
        await Press("PanelSectionLayers"); await Settle();
        var layerSwitch = Find<Button>("LayerRowVisibility_" + _waterLayerId);
        Check("layer-switch-names-its-scope", AutomationProperties.GetName(layerSwitch)?.Contains("целиком") == true);

        // The empty state says what to do.
        await Press("PanelSectionRoads"); await Settle();
        await Press("RoadRow_" + roads[0].Id.ToString("D")); await Settle();
        await Press("RoadSectionDelete"); await Settle();
        var empty = TryFind<Control>("NoObjectsText");
        Check("empty-section-explains", _editor.Roads.Count == 0 && empty is { IsVisible: true }
            && empty.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("нет дорог") == true));
        _editor.Undo(); await Settle();
        Check("undo-restores-the-road", _editor.Roads.Count == 1);
        SaveVisual("panel-roads-empty-undone");

        // Swatches: the shared land material in the land's colour, the brush sample raw.
        _editor.SelectLayer(_landId); _editor.ActiveTool = EditorTool.Land; _editor.SetLandPaintMode(false); await Settle();
        // The expected swatch colour is the renderer's own formula: base colour, then the
        // material at its strength. The material's mean tone is read from the very tile.
        var stoneTile = MapTextureLibrary.Image("owner-snow-drifts")!;
        using var stoneBitmap = SKBitmap.FromImage(stoneTile);
        long sr = 0, sg = 0, sb = 0, sn = 0;
        for (var y = 0; y < stoneBitmap.Height; y += 8) for (var x = 0; x < stoneBitmap.Width; x += 8)
        { var p = stoneBitmap.GetPixel(x, y); sr += p.Red; sg += p.Green; sb += p.Blue; sn++; }
        var stoneMean = new SKColor((byte)(sr / sn), (byte)(sg / sn), (byte)(sb / sn));
        static SKColor Mix(SKColor baseColor, SKColor material, double opacity) => new(
            (byte)Math.Round(baseColor.Red + (material.Red - baseColor.Red) * opacity),
            (byte)Math.Round(baseColor.Green + (material.Green - baseColor.Green) * opacity),
            (byte)Math.Round(baseColor.Blue + (material.Blue - baseColor.Blue) * opacity));
        static bool Near(SKColor actual, SKColor expected, int tolerance) => Distance(actual, expected) <= tolerance;
        var green = new SKColor(0x7F, 0xA3, 0x5A); var red = new SKColor(0xB0, 0x30, 0x30);
        var stone = SwatchOf(Find<Button>("LandTexturePicker_owner-snow-drifts"));
        var greenStone = AverageOf(stone);
        Note("swatch-green-stone", $"{greenStone} expected {Mix(green, stoneMean, 0.7)} raw {stoneMean}");
        Check("swatch-shows-green-land-with-stone", Near(greenStone, Mix(green, stoneMean, 0.7), 14) && Distance(greenStone, Mix(green, stoneMean, 0.7)) < Distance(greenStone, stoneMean));
        _editor.SetActiveLandStyle(s => s with { FillColor = "#FFB03030" }); await Settle(); await Settle();
        var redStone = AverageOf(SwatchOf(Find<Button>("LandTexturePicker_owner-snow-drifts")));
        Note("swatch-red-stone", $"{redStone} expected {Mix(red, stoneMean, 0.7)}");
        Check("swatch-follows-land-colour", Near(redStone, Mix(red, stoneMean, 0.7), 14));
        _editor.SetActiveLandStyle(s => s with { FillTextureOpacity = 0.0 }); await Settle(); await Settle();
        var flat = AverageOf(SwatchOf(Find<Button>("LandTexturePicker_owner-snow-drifts")));
        Check("swatch-follows-material-strength", Near(flat, red, 4));
        _editor.Undo(); _editor.Undo(); await Settle(); await Settle();
        var back = AverageOf(SwatchOf(Find<Button>("LandTexturePicker_owner-snow-drifts")));
        Note("swatch-after-undo", back.ToString());
        Check("swatch-follows-undo", Near(back, Mix(green, stoneMean, 0.7), 14));
        _editor.SetLandPaintMode(true); _editor.ChooseLandMaterial("owner-snow-drifts"); await Settle();
        var raw = AverageOf(Find<Border>("LandMaterialPreview"));
        Note("brush-raw-stone", $"{raw} tile {stoneMean}");
        Check("brush-sample-shows-raw-material", Near(raw, stoneMean, 14));
        SaveVisual("swatches-brush");
        _editor.SetLandPaintMode(false); await Settle();
        await Press("LandTexturePickerCatalogButton");
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var watch = Stopwatch.StartNew();
        while (!desktop.Windows.Any(w => w.Name == "MaterialCatalogWindow") && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        var catalog = desktop.Windows.Single(w => w.Name == "MaterialCatalogWindow"); await Settle();
        var card = catalog.GetVisualDescendants().OfType<Button>().First(b => b.Name == "MaterialCatalog_owner-snow-drifts");
        var catalogStone = AverageOf(card.GetVisualDescendants().OfType<Border>().First(b => b.Background is ImageBrush));
        Note("catalog-stone", catalogStone.ToString());
        Check("catalog-swatch-uses-land-colour", Near(catalogStone, Mix(green, stoneMean, 0.7), 14));
        using (var capture = new RenderTargetBitmap(new PixelSize(900, 650)))
        { capture.Render(catalog); using var file = File.Create(Path.ChangeExtension(Options.OutputPath, ".swatches-catalog.png")); capture.Save(file, PngBitmapEncoderOptions.Default); }
        catalog.Close(); await Settle();
        _editor.ActiveTool = EditorTool.Select; await Settle();
    }

    // ------------------------------------------------------------------ gestures

    private void Press(double x, double y) { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.PressForSmoke(p.X, p.Y); }
    private void Move(double x, double y) { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.MoveForSmoke(p.X, p.Y); }
    private void MoveScreen(double x, double y) { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.MovePlacementPointerForSmoke(p.X, p.Y); }
    private void Release(double x, double y) { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.ReleaseForSmoke(p.X, p.Y); }
    private void Click(double x, double y, bool additive = false) { var p = _editor.Session.Viewport.WorldToScreen(x, y); _view.Canvas.ClickForSmoke(p.X, p.Y, additive); }

    // ------------------------------------------------------------------ frames

    /// <summary>Waits for the real window to draw the current state; the flags read afterwards belong to that frame.</summary>
    private async Task Frame()
    {
        // Two frames: the first may have been in flight before this call; the second was
        // requested after it finished, so its flags describe the state set up just before.
        for (var pass = 0; pass < 2; pass++)
        {
            // A panel that changed size since the last frame has panned the view by half the
            // difference; every frame of the run is drawn through the same whole-pixel view.
            _editor.Session.Viewport.Set(0, 0, 1);
            var before = _view.Canvas.FramesRendered;
            _view.Canvas.InvalidateVisual();
            var watch = Stopwatch.StartNew();
            while (_view.Canvas.FramesRendered == before)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("The canvas did not render a frame");
                await Task.Delay(10);
            }
        }
        _frame = (_view.Canvas.LastFrameFastTerrain, _view.Canvas.LastFrameStyledLandHits, _view.Canvas.LastFrameStyledLandMisses,
            _view.Canvas.LastFrameHadGpuContext, _view.Canvas.LastSceneRenderMilliseconds, _view.Canvas.LastLodTier,
            _view.Canvas.LastFrameTerrainHits, _view.Canvas.LastFrameTerrainMisses);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
    }

    /// <summary>The fixed view of every capture, drawn twice so the caches for it are warm.</summary>
    private async Task Warm(string label)
    {
        await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        await Frame();
        Note("scene-ms-first/" + label, _frame.Ms.ToString("0.0"));
        await Frame();
        Note("scene-ms-warm/" + label, _frame.Ms.ToString("0.0") + (_frame.Gpu ? " gpu" : " cpu") + $" hits={_frame.Hits} misses={_frame.Misses}");
    }

    /// <summary>A frame of the canvas together with the viewport it was drawn through, so world points can be read back.</summary>
    private sealed record Shot(SKBitmap Bitmap, double OffsetX, double OffsetY, double Scale)
    {
        public (int X, int Y) Screen(double worldX, double worldY) =>
            ((int)Math.Round((worldX - OffsetX) * Scale), (int)Math.Round((worldY - OffsetY) * Scale));
        public SKColor At(double worldX, double worldY)
        {
            var (x, y) = Screen(worldX, worldY);
            if (x < 0 || y < 0 || x >= Bitmap.Width || y >= Bitmap.Height)
                throw new InvalidOperationException($"Probe ({worldX},{worldY}) is outside the captured canvas ({Bitmap.Width}x{Bitmap.Height}).");
            return Bitmap.GetPixel(x, y);
        }
    }

    private async Task<Shot> Capture(string name)
    {
        // A panel that just changed size pans the view by half the difference: every capture is
        // read through the same whole-pixel view, so frames can be compared pixel by pixel.
        var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
        var viewport = _editor.Session.Viewport;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            _view.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            viewport.Set(0, 0, 1);
            using (var canvas = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_view.Canvas.Bounds.Width), (int)Math.Ceiling(_view.Canvas.Bounds.Height))))
            {
                canvas.Render(_view.Canvas);
                using var file = File.Create(path);
                canvas.Save(file, PngBitmapEncoderOptions.Default);
            }
            // A layout pass inside the render pans the view when the canvas changed size; the
            // frame is only accepted when it was drawn through the whole-pixel view.
            if (viewport.OffsetX == 0 && viewport.OffsetY == 0 && viewport.Scale == 1) break;
        }
        _report.Frames.Add(path);
        return new Shot(SKBitmap.Decode(path), viewport.OffsetX, viewport.OffsetY, viewport.Scale);
    }

    private static SKColor PixelAt(Shot frame, double worldX, double worldY) => frame.At(worldX, worldY);

    /// <summary>Every probe block of land far from the gesture is pixel-identical between the two frames.</summary>
    private bool SameLand(Shot expected, Shot actual, string label, int tolerance = 0)
    {
        var worst = 0;
        var detail = new List<string>();
        foreach (var (x, y) in LandProbes)
        {
            var a0 = expected.Screen(x, y); var b0 = actual.Screen(x, y);
            var probeWorst = 0;
            for (var dy = -10; dy <= 10; dy++)
                for (var dx = -10; dx <= 10; dx++)
                {
                    var a = expected.Bitmap.GetPixel(a0.X + dx, a0.Y + dy);
                    var b = actual.Bitmap.GetPixel(b0.X + dx, b0.Y + dy);
                    probeWorst = Math.Max(probeWorst, Distance(a, b));
                }
            detail.Add($"({x},{y})={probeWorst}@{a0.X},{a0.Y}/{b0.X},{b0.Y} {expected.Bitmap.GetPixel(a0.X, a0.Y)}/{actual.Bitmap.GetPixel(b0.X, b0.Y)}");
            worst = Math.Max(worst, probeWorst);
        }
        Note("land-probe-max-diff/" + label, worst + " " + string.Join(" ", detail));
        return worst <= tolerance;
    }

    private static bool Similar(Shot a, Shot b, double left, double top, double right, double bottom, int tolerance, out int worst)
    {
        worst = 0;
        for (var y = top; y < bottom; y += 1 / a.Scale)
            for (var x = left; x < right; x += 1 / a.Scale)
                worst = Math.Max(worst, Distance(a.At(x, y), b.At(x, y)));
        return worst <= tolerance;
    }

    private static int Distance(SKColor a, SKColor b) =>
        Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));

    private void SaveVisual(string name)
    {
        var path = Path.ChangeExtension(Options.OutputPath, "." + name + ".png");
        using var bitmap = new RenderTargetBitmap(new PixelSize(1440, 900)); bitmap.Render(_view);
        using var file = File.Create(path); bitmap.Save(file, PngBitmapEncoderOptions.Default);
        _report.Frames.Add(path);
    }

    private void Check(string key, bool value)
    {
        _report.Checks[key] = value;
        Console.WriteLine((value ? "ok      " : "FAILED  ") + key);
    }

    private void Note(string key, string value) => _report.Notes[key] = value;

    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render); await Task.Delay(50); }
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
