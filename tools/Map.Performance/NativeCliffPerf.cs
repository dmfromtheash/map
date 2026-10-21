using System.Diagnostics;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.Performance;

/// <summary>
/// Cost of land tiers (cliffs), 2026-10-17: MAP_CLIFF_PERF=1 runs these routes in the paint-perf
/// window instead of the regional project. A 4096×3072 map: an island one level above the sea, two
/// plateaus a level above it and a pit cut into one of them. Routes: the first exact frame, a pan,
/// a zoom-in by wheel notches, a cliff slider dragged on a plateau and a land stroke over a plateau
/// edge. After every change the time until the exact terrain is on screen is recorded as
/// <c>exact/&lt;route&gt;</c> in the report environment.
/// </summary>
public sealed partial class PaintPerfApplication
{
    private async Task CliffRoutes()
    {
        var island = PerfLand("Остров", 1, (2048, 1560, 1750, 1250, 0.07, 7));
        var plateau = PerfLand("Плато", 2, (1500, 1200, 640, 420, 0.1, 9), (1560, 1150, 140, 90, 0.15, 5));
        var ridge = PerfLand("Гряда", 2, (2800, 1900, 520, 260, 0.12, 11));
        if (System.Environment.GetEnvironmentVariable("MAP_CLIFF_OFF") == "1")
        {
            island = island with { Level = 0 }; plateau = plateau with { Level = 0 }; ridge = ridge with { Level = 0 };
            _report.Environment["cliffs"] = "off (all levels 0)";
        }
        var sea = "#FF8FB0BA";
        var document = new MapDocument(DocumentId.New(), "Ярусы — замер", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 3072, "px", sea),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, sea), island, plateau, ridge]);
        if (System.Environment.GetEnvironmentVariable("MAP_CLIFF_LOOK") is { Length: > 0 } look)
            _report.Environment["look"] = look;
        OpenEditor(document);
        var canvas = _view.Canvas;
        var opened = Stopwatch.StartNew();
        await FirstFrame(document);
        _report.Environment["first-frame-ms"] = opened.Elapsed.TotalMilliseconds.ToString("0");
        var scale = EnvNumber("MAP_PAINT_SCALE", 0.6);
        SetScale((2048, 1500), scale);
        _report.Environment["exact/open"] = (await TimedRefined()).ToString("0");
        await Capture("cliff-open");

        await BurstRoute("cliff-pan", 60, i =>
        {
            canvas.PanByScreenDeltaForSmoke(-12, -4);
        });
        _report.Environment["exact/pan"] = (await TimedRefined()).ToString("0");

        await BurstRoute("cliff-zoom", 12, i =>
        {
            var v = _editor.Session.Viewport;
            canvas.ZoomAtForSmoke(v.ViewWidth / 2, v.ViewHeight / 2, 1.1);
        });
        _report.Environment["exact/zoom"] = (await TimedRefined()).ToString("0");
        await Capture("cliff-zoomed");

        SetScale((2048, 1500), scale); await TimedRefined();
        _editor.ActiveTool = EditorTool.Land; _editor.SetLandPaintMode(false);
        _editor.SelectLayer(plateau.Id); await Settle();
        if (System.Environment.GetEnvironmentVariable("MAP_CLIFF_NOPANEL") == "1") { _editor.ActiveTool = EditorTool.Select; await Settle(); }
        // The UI thread's own time per slider step, measured as the delay of an idle-priority probe.
        var probe = Stopwatch.StartNew();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        _report.Environment["ui-idle-before-slider-ms"] = probe.Elapsed.TotalMilliseconds.ToString("0.0");
        probe.Restart();
        _editor.SetActiveLandStyle(style => style with { Cliff = style.Cliff with { Height = 29 } });
        var handler = probe.Elapsed.TotalMilliseconds;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        _report.Environment["ui-one-style-step-ms"] = $"handler={handler:0.0} until-idle={probe.Elapsed.TotalMilliseconds:0.0}";
        await TimedRefined();
        await BurstRoute("cliff-slider", 30, i =>
            _editor.SetActiveLandStyle(style => style with { Cliff = style.Cliff with { Height = 30 + i } }));
        _report.Environment["exact/slider"] = (await TimedRefined()).ToString("0");

        _editor.BrushSettings = _editor.BrushSettings with { Radius = 40, Softness = .5, Spacing = .25 };
        await StrokeRoute("cliff-land-stroke", (1500, 1640), 60, 9, 4);
        _report.Environment["exact/stroke"] = (await TimedRefined()).ToString("0");
        await Capture("cliff-after-stroke");
        var (ws, pb) = Memory();
        _report.Environment["working-set-mib"] = ws.ToString(); _report.Environment["private-mib"] = pb.ToString();
        _report.Environment["cliff-fields"] = $"computed={Map.Rendering.LandCliffField.Computed} compute={Map.Rendering.LandCliffField.ComputeMilliseconds:0} ms draw={Map.Rendering.LandCliffField.DrawMilliseconds:0} ms";
        _report.Checks["gpu"] = _report.Routes.All(r => r.Gpu);
        _report.Checks["routes"] = _report.Routes.Count >= 4;
    }

    /// <summary>The longest scene frame seen while the last TimedRefined waited.</summary>
    private double LastRefinedMaxFrameMs;

    /// <summary>Time from now until a frame shows the exact (refined) terrain, polled at 15 ms.</summary>
    private async Task<double> TimedRefined()
    {
        var canvas = _view.Canvas;
        var watch = Stopwatch.StartNew();
        LastRefinedMaxFrameMs = 0;
        await Task.Delay(15);
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            canvas.InvalidateVisual();
            await Task.Delay(15);
            // The longest scene frame on the way to the exact picture: the settled frame after a zoom (R05).
            LastRefinedMaxFrameMs = Math.Max(LastRefinedMaxFrameMs, canvas.LastSceneRenderMilliseconds);
            if (!canvas.LastTerrainRefinementPending && !canvas.LastFrameFastTerrain && !canvas.LastDetailTilesPending) break;
        }
        var elapsed = watch.Elapsed.TotalMilliseconds;
        await Settle();
        return elapsed;
    }

    /// <summary>Land from wobbly ellipses; a negative radius cuts a pit out of the shape.</summary>
    private static LandLayer PerfLand(string name, int level, params (double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes)[] shapes)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty < 12; ty++)
            for (var tx = 0; tx < 16; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        double wx = tx * TileMap.TileSize + x, wy = ty * TileMap.TileSize + y;
                        var value = Shape(shapes[0], wx, wy);
                        for (var i = 1; i < shapes.Length; i++) value = Math.Min(value, 1 - Shape(shapes[i], wx, wy));
                        var b = (byte)Math.Clamp(value * 255, 0, 255);
                        bytes[y * TileMap.TileSize + x] = b; any |= b > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, LandEdgeStyle.SoftWatercolor()) { Level = level };

        static double Shape((double Cx, double Cy, double Rx, double Ry, double Wobble, int Lobes) s, double x, double y)
        {
            var angle = Math.Atan2(y - s.Cy, x - s.Cx);
            var wobble = 1 + s.Wobble * Math.Sin(angle * s.Lobes) + s.Wobble * .6 * Math.Sin(angle * (s.Lobes * 2 + 3) + 1.3);
            var distance = Math.Sqrt(Math.Pow((x - s.Cx) / s.Rx, 2) + Math.Pow((y - s.Cy) / s.Ry, 2)) / wobble;
            return Math.Clamp((1 - distance) * Math.Max(s.Rx, s.Ry) / 2, 0, 1);
        }
    }
}
