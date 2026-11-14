using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Input;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.Performance;

// Opt-in routes only. No product hooks or legacy measurement defaults are changed.
internal sealed record MillionMatrixOptions(string? Style, string[] Functions, int Steps, bool Reset)
{
    internal static readonly string[] Supported =
        ["navigation", "terrain", "water-road", "placement", "layout", "selection", "style-light", "hex", "export"];

    internal static MillionMatrixOptions Read()
    {
        var style = Environment.GetEnvironmentVariable("MAP_MILLION_STYLE");
        if (style is not null && style != "off" &&
            (!Enum.TryParse<MapStylePreset>(style, out var preset) || !Enum.IsDefined(preset)
             || preset.ToString() != style))
            throw new ArgumentException("MAP_MILLION_STYLE: use off or an exact MapStylePreset name.");
        var raw = Environment.GetEnvironmentVariable("MAP_MILLION_FUNCTIONS");
        var functions = raw is null ? [] : raw == "all" ? Supported
            : raw.Split(',', StringSplitOptions.TrimEntries);
        if (raw is not null && (functions.Length == 0 || functions.Any(f => !Supported.Contains(f))))
            throw new ArgumentException("MAP_MILLION_FUNCTIONS: " + string.Join(',', Supported) + ", or all.");
        var steps = 600;
        if (Environment.GetEnvironmentVariable("MAP_MILLION_STEPS") is { } value &&
            (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out steps) || steps is < 120 or > 6000))
            throw new ArgumentException("MAP_MILLION_STEPS must be 120..6000 (default 600).");
        var cache = Environment.GetEnvironmentVariable("MAP_MILLION_CACHE") ?? "warm";
        if (cache is not ("warm" or "reset-render"))
            throw new ArgumentException("MAP_MILLION_CACHE: warm or reset-render (disk/OS caches are not cleared).");
        if (functions.Length > 0 && Environment.GetEnvironmentVariable("MAP_MILLION_FIRST_ONLY") == "1")
            throw new ArgumentException("Matrix routes cannot be combined with MAP_MILLION_FIRST_ONLY.");
        return new(style, functions.Distinct().ToArray(), steps, cache == "reset-render");
    }
}

public sealed partial class ZoomAssetsApplication
{
    private MapDocument PrepareMillionMatrix(MapDocument document, MillionMatrixOptions options,
        string kind, (double X, double Y) anchor)
    {
        if (options.Style is { } style)
        {
            document = document with { Style = style == "off" ? null : MapStyle.For(Enum.Parse<MapStylePreset>(style)) };
            _report.Environment["requested-style"] = style;
        }
        if (options.Functions.Length == 0) return document;
        if (kind is not ("standard" or "giant" or "hex" or "light" or "style"))
            throw new ArgumentException("Matrix MAP_MILLION_CASE: standard, giant, hex, light, style.");
        if (options.Functions.Contains("hex") && kind != "hex")
            throw new ArgumentException("The hex route requires MAP_MILLION_CASE=hex.");

        // Shared immutable coverage bytes keep fixture construction bounded. This deliberately
        // exercises a large land extent, not 16 GiB of distinct painted coverage on the giant.
        var full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var changes = new List<KeyValuePair<TileKey, byte[]?>>();
        var columns = (int)Math.Ceiling(document.Canvas.Width / (double)TileMap.TileSize);
        var rows = (int)Math.Ceiling(document.Canvas.Height / (double)TileMap.TileSize);
        for (var x = 0; x < columns; x++)
            for (var y = 0; y < rows; y++)
                if (x < columns * .62 && (x + y) % 17 != 0)
                    changes.Add(new(new TileKey(x, y), full));
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1,
            TileMap.Empty(1).WithMany(changes), CurrentLook.Land());
        var river = new River { Name = "Река", Style = CurrentLook.River(), Channels =
            [RiverEditor.NewChannel([new(anchor.X - 600, anchor.Y - 350), new(anchor.X, anchor.Y - 100),
                new(anchor.X + 500, anchor.Y + 350)], 26)] };
        var road = RoadEditor.NewRoad([new(anchor.X - 500, anchor.Y + 200),
            new(anchor.X, anchor.Y + 100), new(anchor.X + 500, anchor.Y - 200)], 14, CurrentLook.Road(), "Тракт", seed: Options.Seed);
        var layers = document.Layers.Select(l => l is ObjectLayer o
            ? o with { Rivers = [river], Roads = [road] } : l).ToList();
        layers.Insert(1, land);
        _report.Environment["matrix-fixture"] = "62% land extent with every 17th tile omitted; shared coverage bytes; one river and road; original stamp count";
        _report.Environment["matrix-land-tiles"] = changes.Count.ToString(CultureInfo.InvariantCulture);
        return document with { Layers = layers };
    }

    private async Task ExecuteMillionMatrix(ZoomScenarioReport scenario, MillionMatrixOptions options,
        (double X, double Y) anchor)
    {
        var count = 0; var maxWork = 0d; var maxScene = 0d;
        var gate = new object();
        void OnFrame(object? sender, EventArgs args)
        {
            lock (gate)
            {
                count++;
                maxWork = Math.Max(maxWork, Map.App.Controls.MapCanvasControl.LastFrameWorkMilliseconds);
                maxScene = Math.Max(maxScene, _view.Canvas.LastSceneRenderMilliseconds);
            }
        }
        _view.Canvas.FrameRendered += OnFrame;
        try { await RunMillionMatrix(scenario, options, anchor); }
        finally
        {
            _view.Canvas.FrameRendered -= OnFrame;
            lock (gate)
            {
                _report.Environment["matrix-all-frames"] = count.ToString(CultureInfo.InvariantCulture);
                _report.Environment["matrix-all-frame-work-max-ms"] = maxWork.ToString("F3", CultureInfo.InvariantCulture);
                _report.Environment["matrix-all-scene-max-ms"] = maxScene.ToString("F3", CultureInfo.InvariantCulture);
                _report.Checks["matrix/all-frame-max"] = count > 0 && double.IsFinite(maxWork) && maxWork <= 33;
            }
        }
    }

    private async Task RunMillionMatrix(ZoomScenarioReport scenario, MillionMatrixOptions options,
        (double X, double Y) anchor)
    {
        var canvas = _view.Canvas;
        // Later placement/layout routes append managed members; selection must keep the
        // original independently editable fixture stamp, not the latest generated member.
        var selectionLayer = _editor.Session.Document.Layers.OfType<ObjectLayer>().Last();
        var selectionId = selectionLayer.Objects[^1].Id;
        _report.Environment["matrix-style"] = _editor.Session.Document.Style?.Preset.ToString() ?? "off";
        _report.Environment["matrix-functions"] = string.Join(',', options.Functions);
        _report.Environment["matrix-steps"] = options.Steps.ToString(CultureInfo.InvariantCulture);
        _report.Environment["matrix-cache"] = options.Reset ? "reset-render; image/disk/OS caches retained" : "warm after open/settle";
        _report.Environment["matrix-scope"] = "production canvas/router and public editor commands; not OS input or monitor scan-out; see MATRIX.md for remaining manual routes";
        _report.Checks["matrix/gpu-flush-enabled"] = Environment.GetEnvironmentVariable("MAP_MEASURE_GPU_FLUSH") == "1";
        await Event("open-first-frame", () => canvas.FitToCanvas());

        foreach (var function in options.Functions)
        {
            await Event(function + "/setup", () => { _editor.ActiveTool = EditorTool.Select; SetScale(anchor, 1); });
            if (options.Reset)
                await Event(function + "/reset-render", canvas.ClearDerivedRenderCachesForSmoke);
            // Keep setup/reset frames in the report and strict gate; do not hide cold spikes.
            switch (function)
            {
                case "navigation":
                    await Event("navigation/fit", canvas.FitToCanvas);
                    foreach (var scale in new[] { .25, 1d, 4d, 16d })
                    {
                        await Event("navigation/scale-" + scale.ToString(CultureInfo.InvariantCulture), () => SetScale(anchor, scale));
                        await BurstRoute(scenario, "navigation/pan-" + scale.ToString(CultureInfo.InvariantCulture), options.Steps,
                            i => canvas.PanByScreenDeltaForSmoke(i % 120 < 60 ? -7 : 7, i % 120 < 60 ? .5 : -.5));
                    }
                    await BurstRoute(scenario, "navigation/wheel", options.Steps, i =>
                    {
                        var p = _editor.Session.Viewport.WorldToScreen(anchor.X, anchor.Y);
                        canvas.WheelForSmoke(p.X, p.Y, i % 80 < 40 ? -1 : 1, KeyModifiers.None);
                    });
                    await Event("navigation/finish-fit", canvas.FitToCanvas);
                    break;
                case "terrain":
                    _editor.SelectLayer(_editor.Session.Document.Layers.OfType<LandLayer>().First().Id);
                    _editor.ActiveTool = EditorTool.Land;
                    _editor.BrushSettings = _editor.BrushSettings with { Eraser = true, Radius = 64 };
                    // The land shape tool has its own subtract mode; BrushSettings.Eraser
                    // controls paint, and adding to an already full land tile is a no-op.
                    _editor.LandSubtract = true;
                    await Stroke("terrain/land", anchor.X - 240, anchor.Y - 200);
                    _editor.LandSubtract = false;
                    _editor.BrushSettings = _editor.BrushSettings with { Eraser = false };
                    _editor.SetLandPaintMode(true);
                    _editor.SetLandPaintAction("Color");
                    await Stroke("terrain/material", anchor.X - 240, anchor.Y);
                    _editor.SetLandPaintMode(false);
                    _editor.SetLandCliffMaskMode(true);
                    await Stroke("terrain/cliff-mask", anchor.X - 240, anchor.Y);
                    _editor.SetLandCliffMaskMode(false);
                    _editor.SetLandReliefMode(true);
                    await Stroke("terrain/relief", anchor.X - 240, anchor.Y);
                    _editor.SetLandReliefMode(false);
                    await Event("terrain/add-mask", _editor.ToggleActiveMask);
                    _editor.ActiveTool = EditorTool.Mask;
                    await Stroke("terrain/layer-mask", anchor.X - 240, anchor.Y);
                    break;
                case "water-road":
                    _editor.SelectLayer(_editor.Session.Document.Layers.OfType<ObjectLayer>().First().Id);
                    _editor.StartRiver();
                    await Nodes("water-road/river", _editor.AddRiverNode, _editor.FinishRiver);
                    _editor.StartLake();
                    await Nodes("water-road/lake", _editor.AddRiverNode, _editor.FinishRiver);
                    _editor.StartRoad();
                    await Nodes("water-road/road", _editor.AddRoadNode, _editor.FinishRoad);
                    break;
                case "placement":
                    _editor.SelectLayer(_editor.Session.Document.Layers.OfType<ObjectLayer>().First().Id);
                    Require("placement/armed", _editor.ChooseGalleryVariant(_catalog.Sets.First(s => !s.Archived).Id, null));
                    _editor.StampPlacementMode = StampPlacementMode.One;
                    await BurstRoute(scenario, "placement/hover", options.Steps, i =>
                    {
                        var p = Point(i); canvas.MovePlacementPointerForSmoke(p.X, p.Y);
                    });
                    await Mutation("placement/one", () => Require("placement/committed", canvas.CommitPlacementForSmoke()));
                    _editor.CancelPlacement();
                    _editor.StampPlacementMode = StampPlacementMode.Series;
                    Require("placement/series-armed", _editor.ChooseGalleryVariant(_catalog.Sets.First(s => !s.Archived).Id, null));
                    await Stroke("placement/series", anchor.X - 240, anchor.Y);
                    _editor.CancelPlacement();
                    break;
                case "selection":
                    _editor.ActiveTool = EditorTool.Select;
                    _editor.SelectLayer(selectionLayer.Id);
                    var id = selectionId;
                    await Event("selection/select-id", () => _editor.SelectObject(id));
                    Require("selection/selected", _editor.Session.SelectedObjectId == id);
                    await Event("selection/drag-press", () => Require("selection/drag-started", _editor.BeginDrag(anchor.X, anchor.Y)));
                    await BurstRoute(scenario, "selection/drag-preview", options.Steps,
                        i => _editor.UpdateDragTo(anchor.X + 10 + i % 100, anchor.Y + 24));
                    await Mutation("selection/drag-commit", _editor.EndDrag);
                    await Mutation("selection/rotate", () => _editor.RotateSelectedObject(1));
                    await Mutation("selection/resize", () => _editor.ResizeSelectedObject(1));
                    await Mutation("selection/label", () => _editor.AddObject(SceneObjectKind.Text));
                    break;
                case "layout":
                    _editor.SelectLayer(_editor.Session.Document.Layers.OfType<ObjectLayer>().First().Id);
                    Require("layout/armed", _editor.ChooseGalleryVariant(_catalog.Sets.First(s => !s.Archived).Id, null));
                    _editor.StartEditablePath();
                    await Nodes("layout/path", _editor.AddEditablePathNode, _editor.FinishEditablePath,
                        () => _editor.PendingPathCalculation);
                    _editor.SetStampScalePercent(10);
                    _editor.StartEditableArea();
                    await Event("layout/area-node-1", () => Require("layout/area-node-1", _editor.AddEditableAreaNode(anchor.X - 300, anchor.Y - 250)));
                    await Event("layout/area-node-2", () => Require("layout/area-node-2", _editor.AddEditableAreaNode(anchor.X + 300, anchor.Y - 250)));
                    await Event("layout/area-node-3", () => Require("layout/area-node-3", _editor.AddEditableAreaNode(anchor.X, anchor.Y + 250)));
                    await BurstRoute(scenario, "layout/area-preview", options.Steps,
                        i => { var p = Point(i); canvas.MoveForSmoke(p.X, p.Y); });
                    await _editor.PendingEditableAreaPreview.WaitAsync(TimeSpan.FromSeconds(180));
                    await _editor.PendingEditableAreaCount.WaitAsync(TimeSpan.FromSeconds(180));
                    await Mutation("layout/area-finish", () => Require("layout/area-finished", _editor.FinishEditableArea()),
                        () => _editor.PendingEditableAreaWork);
                    Require("layout/area-populated", _editor.Session.Document.Layers.OfType<ObjectLayer>()
                        .SelectMany(layer => layer.PlacementAreas).Any(area => area.Members.Any()));
                    _editor.CancelPlacement();
                    break;
                case "style-light":
                    var initial = _editor.Style;
                    await BurstRoute(scenario, "style-light/style-preview", options.Steps, i =>
                        _editor.ChangeStyle("Сила стиля", s => s with { Enabled = true, Strength = .4 + .5 * (i % 100) / 100 }, false));
                    await Mutation("style-light/style-commit", () => _editor.ChangeStyle("Сила стиля", _ => initial with { Enabled = true, Strength = .65 }, true));
                    await Mutation("style-light/preset-switch", () => _editor.ApplyStylePreset(initial.Preset == MapStylePreset.OldMapBlaeu
                        ? MapStylePreset.WatercolorBigShapes : MapStylePreset.OldMapBlaeu));
                    await Event("style-light/restore-style", _editor.Undo);
                    await Mutation("style-light/lights", () => _editor.ChangeLighting("Свет", l => l with { Enabled = true, Sources =
                        [.. Enumerable.Range(0, 16).Select(i => new MapLightSource(Guid.NewGuid(), anchor.X + i * 20 - 160,
                            anchor.Y, "#FFFFBB66", 500, 1, true))] }, true));
                    await BurstRoute(scenario, "style-light/time-preview", options.Steps,
                        i => _editor.SetTimeOfDay(.6 + .3 * (i % 100) / 100, false));
                    await Mutation("style-light/time-commit", () => _editor.SetTimeOfDay(.75, true));
                    break;
                case "hex":
                    var hex = _editor.Session.Document.Layers.OfType<HexLayer>().First();
                    _editor.SelectLayer(hex.Id); _editor.ActiveTool = EditorTool.Hex; _editor.HexMode = HexToolMode.Erase;
                    var center = hex.Board.Layout.Center(hex.Board.Layout.HexAt(500, 500));
                    await Event("hex/center", () => SetScale((center.X, center.Y), 1));
                    await Stroke("hex/erase", center.X, center.Y);
                    break;
                case "export":
                    var watch = Stopwatch.StartNew();
                    await Event("export/open", _editor.BeginExport);
                    var previewTiming = PreviewTime();
                    await BurstRoute(scenario, "export/pan-during-preview", options.Steps,
                        i => canvas.PanByScreenDeltaForSmoke(i % 2 == 0 ? 3 : -3, 0));
                    _report.Environment["matrix-export-preview-ms"] = (await previewTiming).ToString("F1", CultureInfo.InvariantCulture);
                    Require("export/preview", _editor.ExportPreviewPng is { Length: > 0 } && !_editor.ExportPreviewBusy);
                    await Event("export/close", _editor.CloseExport);
                    break;
                    async Task<double> PreviewTime()
                    {
                        await _editor.WaitForExportPreviewAsync().WaitAsync(TimeSpan.FromSeconds(180));
                        return watch.Elapsed.TotalMilliseconds;
                    }
            }
            await MillionCapture("matrix-" + function);
            File.WriteAllText(Path.ChangeExtension(Options.OutputPath, ".progress.json"),
                JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));
        }
        _report.Checks["matrix/gpu"] = scenario.Routes.All(r => r.Gpu);
        _report.Checks["matrix/refinement"] = scenario.Routes.All(r => r.RefinementCompleted == true);
        _report.Checks["matrix/frame-gate"] = scenario.Routes.All(r =>
            r.FrameWorkMs.Count > 0 && double.IsFinite(r.FrameWorkP99) && r.FrameWorkP99 <= 16.7
            && r.FrameWorkMax <= 33 && r.RefinementWorkMax <= 33 && r.LatencyMax <= 33
            && (r.FrameIntervalP99 is null || r.FrameIntervalP99 <= 16.7)
            && (r.FrameIntervalMax is null || r.FrameIntervalMax <= 33)
            && (r.OnePercentLowFps is null || r.OnePercentLowFps >= 60));
        _report.Checks["matrix/sampling"] = scenario.Routes.Where(r => r.Steps == options.Steps)
            .All(r => r.FramesRendered >= 100 && r.FrameIntervalP99 is not null);
        await Finish(scenario);

        void Require(string name, bool passed)
        {
            _report.Checks["matrix/" + name] = passed;
            if (!passed) throw new InvalidOperationException("Matrix action failed: " + name + "; " + _editor.StatusDetail);
        }
        async Task Event(string name, Action action)
        {
            await Route(scenario, name, 1, _ => action());
            await Settled(scenario.Routes[^1]);
            var target = scenario.Routes[^1];
            var exact = Stopwatch.StartNew();
            while ((!canvas.FrameSettled || canvas.LastRenderStats.HexChunksPending > 0 || canvas.LastRenderStats.MissingAssets > 0)
                && exact.Elapsed < TimeSpan.FromSeconds(180)) await Task.Delay(10);
            target.SettleDelayMs = (target.SettleDelayMs ?? 0) + exact.Elapsed.TotalMilliseconds;
            target.RefinementCompleted = target.RefinementCompleted == true && canvas.FrameSettled
                && canvas.LastRenderStats.HexChunksPending == 0 && canvas.LastRenderStats.MissingAssets == 0;
        }
        async Task Mutation(string name, Action action, Func<Task>? background = null)
        {
            var before = _editor.Session.Document;
            await Event(name, action);
            if (background is not null)
            {
                var work = Stopwatch.StartNew();
                await background().WaitAsync(TimeSpan.FromSeconds(180));
                _report.Environment["matrix-background-ms/" + name] = work.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
                await Event(name + "/background-completed", () => { });
            }
            var after = _editor.Session.Document;
            Require(name + "/changed", !SameMatrixState(before, after));
            await Event(name + "/undo", _editor.Undo);
            Require(name + "/undo-state", SameMatrixState(before, _editor.Session.Document));
            await Event(name + "/redo", _editor.Redo);
            Require(name + "/redo-state", SameMatrixState(after, _editor.Session.Document));
        }
        (double X, double Y) Point(int i)
        {
            var p = _editor.Session.Viewport.WorldToScreen(anchor.X + i % 400 - 200, anchor.Y + 20 * Math.Sin(i * .03));
            return (p.X, p.Y);
        }
        async Task Stroke(string name, double x, double y)
        {
            var before = _editor.Session.Document;
            var p = _editor.Session.Viewport.WorldToScreen(x, y);
            await Event(name + "/press", () => canvas.PressForSmoke(p.X, p.Y));
            await BurstRoute(scenario, name + "/move", options.Steps,
                i => canvas.MoveForSmoke(p.X + i % 400, p.Y + 40 * Math.Sin(i * .03)));
            await Event(name + "/release", () => canvas.ReleaseForSmoke(p.X + 100, p.Y));
            var after = _editor.Session.Document;
            Require(name + "/changed", !SameMatrixState(before, after));
            await Event(name + "/undo", _editor.Undo);
            Require(name + "/undo-state", SameMatrixState(before, _editor.Session.Document));
            await Event(name + "/redo", _editor.Redo);
            Require(name + "/redo-state", SameMatrixState(after, _editor.Session.Document));
        }
        async Task Nodes(string name, Func<double, double, bool> add, Func<bool> finish, Func<Task>? background = null)
        {
            await Event(name + "/node-1", () => Require(name + "/node-1", add(anchor.X - 300, anchor.Y - 250)));
            await Event(name + "/node-2", () => Require(name + "/node-2", add(anchor.X, anchor.Y - 150)));
            await Event(name + "/node-3", () => Require(name + "/node-3", add(anchor.X + 300, anchor.Y - 250)));
            await BurstRoute(scenario, name + "/preview", options.Steps,
                i => { var p = Point(i); canvas.MoveForSmoke(p.X, p.Y); });
            await Mutation(name + "/finish", () => Require(name + "/finished", finish()), background);
        }
    }

    // Observable state touched by these routes, checked outside the timed input. Commands
    // reconstruct snapshots on Undo/Redo, so reference identity of MapDocument is not a test.
    private static bool SameMatrixState(MapDocument expected, MapDocument actual)
    {
        if (expected.Style != actual.Style || expected.Lighting != actual.Lighting
            || expected.Layers.Count != actual.Layers.Count
            || !expected.Assets.SequenceEqual(actual.Assets)) return false;
        for (var i = 0; i < expected.Layers.Count; i++)
        {
            var a = expected.Layers[i]; var b = actual.Layers[i];
            if (ReferenceEquals(a, b)) continue;
            if (a.Id != b.Id || a.Visible != b.Visible || a.Locked != b.Locked || a.Opacity != b.Opacity) return false;
            if ((a.Mask is null) != (b.Mask is null)) return false;
            if (a.Mask is { } am && b.Mask is { } bm && (am.Enabled != bm.Enabled || !SameTiles(am.Tiles, bm.Tiles))) return false;
            if (a is ObjectLayer ao && b is ObjectLayer bo)
            {
                if (!ao.Objects.SequenceEqual(bo.Objects) || !ao.Rivers.SequenceEqual(bo.Rivers)
                    || !ao.Roads.SequenceEqual(bo.Roads) || !ao.SurfaceOrder.SequenceEqual(bo.SurfaceOrder)
                    || !ao.PlacementPaths.SequenceEqual(bo.PlacementPaths) || !ao.PlacementAreas.SequenceEqual(bo.PlacementAreas)) return false;
            }
            else if (a is LandLayer al && b is LandLayer bl)
            {
                if (!SameTiles(al.GeometryTiles, bl.GeometryTiles) || !SameTiles(al.PaintTiles, bl.PaintTiles)
                    || !SameTiles(al.CliffWallTiles, bl.CliffWallTiles) || !SameTiles(al.CliffBeachTiles, bl.CliffBeachTiles)
                    || !SameTiles(al.ReliefTiles, bl.ReliefTiles)) return false;
            }
            else if (a is HexLayer ah && b is HexLayer bh)
            {
                if (ah.Board.CellCount != bh.Board.CellCount || !ah.Board.Cells.SequenceEqual(bh.Board.Cells)) return false;
            }
            else if (a != b) return false;
        }
        return true;
    }

    private static bool SameTiles(TileMap a, TileMap b) => ReferenceEquals(a, b)
        || a.TileCount == b.TileCount && a.BytesPerPixel == b.BytesPerPixel && a.Keys.All(key =>
            b.TryGet(key) is { } bytes && (ReferenceEquals(a.TryGet(key), bytes) || a.TryGet(key)!.AsSpan().SequenceEqual(bytes)));
}
