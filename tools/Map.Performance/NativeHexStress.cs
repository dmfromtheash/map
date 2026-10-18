using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.Performance;

/// <summary>
/// Stress of the hex category, 2026-10-18: MAP_HEX_STRESS=1 runs these routes in the paint-perf
/// window instead of the regional project. For each board of MAP_HEX_BOARDS (default "128,512":
/// N × N hexes of 64 px, flat top) a map is generated with regions of every starter terrain and a
/// feature on about one hex in twelve. The routes: open (first frame), overview (fit), pan at the
/// overview, zoom in from the overview to close up and back, pan at 100 %, drags of the hex tool
/// with footprints 0, 3 and 8, an eraser drag, the grid with numbers, a fill of a second, empty
/// hex layer over the whole board and its undo, save and open of the project.
/// </summary>
public sealed partial class PaintPerfApplication
{
    private async Task HexStressRoutes()
    {
        var sizes = (System.Environment.GetEnvironmentVariable("MAP_HEX_BOARDS") ?? "128,512")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        EditorViewModel.CollectEditTimings = true;
        foreach (var size in sizes) await HexStressBoard(size);
    }

    private async Task HexStressBoard(int n)
    {
        var tag = $"hex{n}";
        var generate = Stopwatch.StartNew();
        var document = HexStressDocument(n);
        var board = document.Layers.OfType<HexLayer>().Single().Board;
        _report.Environment[tag + "/generate-ms"] = generate.Elapsed.TotalMilliseconds.ToString("0");
        _report.Environment[tag + "/hexes"] = board.CellCount.ToString();
        _report.Environment[tag + "/canvas"] = $"{document.Canvas.Width}×{document.Canvas.Height}";
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        _report.Environment[tag + "/managed-MiB-after-generate"] = (GC.GetTotalMemory(false) / (1024 * 1024)).ToString();

        OpenEditor(document);
        var canvas = _view.Canvas;
        var opened = Stopwatch.StartNew();
        await FirstFrame(document);
        _report.Environment[tag + "/first-frame-ms"] = opened.Elapsed.TotalMilliseconds.ToString("0");
        await Settle();
        _editor.ActiveTool = EditorTool.Hex; await Settle();

        // Overview: the whole board on screen (the flat-colour level of detail on large boards).
        await Route(tag + "/fit", 1, _ => canvas.FitToCanvas());
        _report.Environment[tag + "/fit-scale"] = _editor.Session.Viewport.Scale.ToString("0.####");
        await Capture(tag + "-overview");
        await BurstRoute(tag + "/pan-overview", 60, _ => canvas.PanByScreenDeltaForSmoke(-6, -3));
        canvas.FitToCanvas(); await Settle();

        // Zoom from the overview to close up through every level of detail, and back.
        var steps = (int)Math.Ceiling(Math.Log(1.5 / _editor.Session.Viewport.Scale) / Math.Log(1.12));
        await BurstRoute(tag + "/zoom-in", steps, _ =>
        {
            var v = _editor.Session.Viewport; canvas.ZoomAtForSmoke(v.ViewWidth / 2, v.ViewHeight / 2, 1.12);
        });
        await Capture(tag + "-close");
        await BurstRoute(tag + "/zoom-out", steps, _ =>
        {
            var v = _editor.Session.Viewport; canvas.ZoomAtForSmoke(v.ViewWidth / 2, v.ViewHeight / 2, 1 / 1.12);
        });

        // Middle distance: hexes of about 12 px, painted tiles from chunk images, grid on.
        var centre = (X: document.Canvas.Width / 2.0, Y: document.Canvas.Height / 2.0);
        SetScale(centre, 0.1); await Settle();
        await BurstRoute(tag + "/pan-12px", 60, _ => canvas.PanByScreenDeltaForSmoke(-12, -5));
        SetScale(centre, 1.0); await Settle();
        await BurstRoute(tag + "/pan-100", 60, _ => canvas.PanByScreenDeltaForSmoke(-12, -5));
        await Capture(tag + "-100");

        // Hex tool drags at 100 %.
        SetScale(centre, 0.5); await Settle();
        _editor.HexTileId = "starter/forest"; _editor.HexMode = HexToolMode.Place;
        foreach (var radius in new[] { 0, 3, 8 })
        {
            _editor.HexRadius = radius; await Settle();
            var depth = _editor.Session.UndoDepth;
            await StrokeRoute($"{tag}/drag-r{radius}", centre, 60, 9, 3);
            _report.Checks[$"{tag}/drag-r{radius} is one undo step"] = _editor.Session.UndoDepth == depth + 1;
        }

        _editor.HexMode = HexToolMode.Erase; _editor.HexRadius = 2; await Settle();
        await StrokeRoute(tag + "/erase-r2", (centre.X, centre.Y + 300), 60, 9, 4);
        _editor.HexMode = HexToolMode.Place; _editor.HexRadius = 0;

        // Hover only: the outline follows the pointer.
        _editor.HexRadius = 8; await Settle();
        await BurstRoute(tag + "/hover-r8", 60, i =>
        {
            var p = _editor.Session.Viewport.WorldToScreen(centre.X + i * 14, centre.Y);
            canvas.MoveForSmoke(p.X, p.Y);
        });
        _editor.HexRadius = 0;

        // The grid with numbers, at a distance where numbers show (≥ 44 px per hex) and where they do not.
        _editor.SetHexGrid(grid => grid with { Numbers = true }); await Settle();
        SetScale(centre, 0.5); await Settle();
        await BurstRoute(tag + "/pan-numbers-50", 60, _ => canvas.PanByScreenDeltaForSmoke(-12, -5));
        SetScale(centre, 0.08); await Settle();
        await BurstRoute(tag + "/pan-grid-8px", 60, _ => canvas.PanByScreenDeltaForSmoke(-12, -5));
        await Capture(tag + "-grid-8px");
        SetScale((0, 0), 0.25); await Settle(); await Capture(tag + "-grid-edge-25");
        _editor.SetHexGrid(grid => grid with { Numbers = false, Visible = false }); await Settle();
        await BurstRoute(tag + "/pan-nogrid-8px", 60, _ => canvas.PanByScreenDeltaForSmoke(-12, -5));
        SetScale(centre, 0.1); await Settle();
        await BurstRoute(tag + "/pan-nogrid-12px", 60, _ => canvas.PanByScreenDeltaForSmoke(-12, -5));
        _editor.SetHexGrid(grid => grid with { Visible = true }); await Settle();

        // Fill a second, empty hex layer over the whole board, then undo it.
        _editor.AddHexLayer(); await Settle();
        _editor.HexTileId = "starter/sea"; _editor.HexMode = HexToolMode.Fill; await Settle();
        canvas.FitToCanvas(); await Settle();
        var before = Memory();
        var fillAt = _editor.Session.Viewport.WorldToScreen(centre.X, centre.Y);
        await Route(tag + "/fill-new-layer", 1, _ => canvas.ClickForSmoke(fillAt.X, fillAt.Y));
        PrintEditTimings(tag + "/fill-new-layer");
        var filled = _editor.Session.Document.Layers.OfType<HexLayer>().Last().Board.CellCount;
        _report.Environment[tag + "/filled"] = filled.ToString();
        _report.Environment[tag + "/fill-ws-delta-MiB"] = (Memory().WorkingSet - before.WorkingSet).ToString();
        _report.Checks[tag + "/fill covered the board up to the fill limit"] = filled == Math.Min(n * n, HexEditing.MaxFillHexes);
        await BurstRoute(tag + "/pan-overview-two-layers", 60, _ => canvas.PanByScreenDeltaForSmoke(-6, -3));
        await Route(tag + "/undo-fill", 1, _ => _editor.Undo());
        _editor.Undo(); await Settle(); // the added layer

        // Save and open.
        var save = Stopwatch.StartNew();
        await _editor.SaveAsync(saveAs: true);
        _report.Environment[tag + "/save-ms"] = save.Elapsed.TotalMilliseconds.ToString("0");
        var path = Path.Combine(_workspace, "paint.mapproj");
        _report.Environment[tag + "/file-KiB"] = (new FileInfo(path).Length / 1024).ToString();
        // The stress map stays beside the report for checks in the real window.
        File.Copy(path, Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, tag + "-stress.mapproj"), true);
        var open = Stopwatch.StartNew();
        var reopened = MapProjectStore.Open(path).Document;
        _report.Environment[tag + "/open-ms"] = open.Elapsed.TotalMilliseconds.ToString("0");
        _report.Checks[tag + "/opens back hex for hex"] = reopened.Layers.OfType<HexLayer>().Single().Board.CellCount
            == _editor.Session.Document.Layers.OfType<HexLayer>().Single().Board.CellCount;
        _report.Environment[tag + "/ws-end-MiB"] = Memory().WorkingSet.ToString();
        _editor.Session.MarkSaved(_editor.Session.CaptureSaveCheckpoint());
        _view.CloseAuxiliaryWindowsForSmoke(); canvas.DisposeRenderCaches();
        _window.Content = null; await Settle();
    }

    /// <summary>N × N hexes: blobs of every terrain (value noise), a feature on about one hex in twelve.</summary>
    private static MapDocument HexStressDocument(int n)
    {
        var terrains = HexStarterTiles.All.Where(t => t.Target == HexPaintTarget.Terrain).ToArray();
        var features = HexStarterTiles.All.Where(t => t.Target == HexPaintTarget.Feature).ToArray();
        var document = HexMapFactory.Create($"Гексы {n}×{n} — стресс", DateTimeOffset.UnixEpoch, MapCategories.Hex.Id,
            HexOrientation.Flat, 64, n, n, "#FFE8DCC0");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var random = new Random(20261018 + n);
        var cells = new List<KeyValuePair<HexCoord, HexCell>>(n * n);
        for (var row = 0; row < n; row++)
            for (var column = 0; column < n; column++)
            {
                var noise = Math.Sin(column * 0.13 + Math.Sin(row * 0.07) * 3) + Math.Cos(row * 0.11 + Math.Sin(column * 0.05) * 2);
                var terrain = terrains[(int)((noise + 2) / 4 * terrains.Length) % terrains.Length];
                var cell = new HexCell(terrain.Id, random.Next(terrain.VariantCount));
                if (random.Next(12) == 0 && terrain.Id is not "starter/sea" and not "starter/shallows")
                {
                    var feature = features[random.Next(features.Length)];
                    cell = cell.WithFeature(feature.Id, random.Next(feature.VariantCount));
                }

                cells.Add(new(layer.Board.Layout.FromOffset(new HexOffset(column, row)), cell));
            }

        var board = HexBoard.FromCells(layer.Board.Layout, n, n, cells);
        return document.WithLayer(layer.Id, l => ((HexLayer)l) with { Board = board });
    }
}
