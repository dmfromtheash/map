using System.Diagnostics;
using Map.Assets;
using Map.Core.Documents;
using Map.Persistence;
using Map.Presentation.ViewModels;

namespace Map.Performance;

/// <summary>
/// The test map of the owner's new art, 2026-10-18: MAP_NEW_ART_PERF=1 runs these routes in the
/// paint-perf window instead of the regional project. MAP_OWNER_ART_LIBRARY names a copy of the
/// owner's library (collection «Мои горы»: six 4096 px mountains with detail ladders); it is copied
/// again into the workspace, never used in place. The 4096×3072 map has four lands on the owner's
/// grounds (snow crust, sparkle dust, soft drifts, blue ice wash), two of them raised a tier with a
/// pit, and MAP_NEW_ART_OBJECTS (default 1500) of the owner's mountains spread over the land. The
/// routes: open, pan, zoom, a land stroke, a material stroke with each owner ground, a brush and
/// a mask stroke, a tier change up and down, a cliff slider and placement of a mountain. The map
/// itself is saved beside the report (test-map.mapproj) for live checks in the real window.
/// </summary>
public sealed partial class PaintPerfApplication
{
    private async Task NewArtRoutes()
    {
        var library = System.Environment.GetEnvironmentVariable("MAP_OWNER_ART_LIBRARY")
            ?? throw new InvalidOperationException("MAP_OWNER_ART_LIBRARY is not set");
        var root = Path.Combine(_workspace, "library");
        foreach (var directory in Directory.GetDirectories(library, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(library, root, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(root);
        foreach (var file in Directory.GetFiles(library, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(library, root, StringComparison.OrdinalIgnoreCase), true);
        _catalog = new AssetCatalog(root);
        var mountains = _catalog.Records.Where(r => r.Pack == "Мои горы").ToArray();
        if (mountains.Length == 0) throw new InvalidOperationException("The library copy has no «Мои горы».");
        _sets.Clear();
        // The copy holds only the owner's collection, so every live set is one of the mountains.
        _sets.AddRange(_catalog.Sets.Where(set => !set.Archived && set.VariantIds.Count > 0));
        _report.Environment["mountains"] = $"{mountains.Length} records, {_sets.Count} sets (catalog {_catalog.Sets.Count}, archived {_catalog.Sets.Count(s => s.Archived)}), largest {mountains.Max(m => Math.Max(m.Width, m.Height))} px";

        var count = (int)EnvNumber("MAP_NEW_ART_OBJECTS", 1500);
        var plain = PerfLand("Равнина", 1, (2048, 1536, 1850, 1350, 0.06, 7)) with
            { EdgeStyle = EditorViewModel.WithGroundTexture(LandEdgeStyle.SoftWatercolor(), MapFillTextures.OwnerSnow) };
        var plateau = PerfLand("Плато", 2, (1450, 1150, 620, 420, 0.1, 9), (1500, 1100, 130, 90, 0.15, 5)) with
            { EdgeStyle = EditorViewModel.WithGroundTexture(LandEdgeStyle.SoftWatercolor(), MapFillTextures.OwnerSnowSparkle) };
        var glacier = PerfLand("Ледник", 2, (2850, 1950, 560, 300, 0.12, 11)) with
            { EdgeStyle = EditorViewModel.WithGroundTexture(LandEdgeStyle.SoftWatercolor(), MapFillTextures.OwnerIceWash) };
        var drifts = PerfLand("Надувы", 1, (2900, 900, 420, 260, 0.1, 6)) with
            { EdgeStyle = EditorViewModel.WithGroundTexture(LandEdgeStyle.SoftWatercolor(), MapFillTextures.OwnerSnowSoft) };

        var random = new Random(20261018);
        var objects = new List<SceneObject>(count);
        while (objects.Count < count)
        {
            var record = mountains[random.Next(mountains.Length)];
            var w = 110 + random.NextDouble() * 320;
            var h = w * record.Height / Math.Max(1, record.Width);
            var x = 250 + random.NextDouble() * 3500; var y = 250 + random.NextDouble() * 2500;
            if (Coverage(plain, x + w / 2, y + h * .85) < 128) continue;
            objects.Add(new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName, x, y, w, h, 0,
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha256) });
        }
        // Back to front, the way a painted map stacks its mountains.
        objects.Sort((a, b) => (a.Y + a.Height).CompareTo(b.Y + b.Height));
        // R10: MAP_NEW_ART_BEAUTY=1 gives every mountain a ±5 % tint, a contact shadow and an underlay
        // blot, as an area with «Красота» does, to measure what they cost on the same map.
        if (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_BEAUTY") == "1")
            for (var i = 0; i < objects.Count; i++)
            {
                var shade = (byte)(242 + random.Next(14));
                objects[i] = objects[i] with { Stamp = objects[i].Stamp! with { Tint = $"#FF{shade:X2}{shade:X2}{(byte)(shade - 4):X2}",
                    GroundShadow = new StampGroundShadow(.34, .5, .95, .8, .12) } };
            }
        var sea = "#FF9DB7C2";
        var document = new MapDocument(DocumentId.New(), "Новые ассеты — замер", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 3072, "px", sea),
            [CurrentLook.Page("Море", sea), plain, drifts, plateau, glacier,
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [.. objects])]);
        _report.Environment["objects"] = objects.Count.ToString();
        _report.Environment["project"] = "generated: 4 lands on owner grounds, 2 tiers, owner mountains";

        EditorViewModel.CollectEditTimings = true;
        OpenEditor(document);
        var canvas = _view.Canvas;
        var opened = Stopwatch.StartNew();
        await FirstFrame(document);
        _report.Environment["first-frame-ms"] = opened.Elapsed.TotalMilliseconds.ToString("0");
        await Settle();
        try
        {
            var mapPath = Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "test-map.mapproj");
            var saveWatch = Stopwatch.StartNew();
            await _editor.SaveAsync(saveAs: true);
            _report.Environment["save-ms"] = saveWatch.Elapsed.TotalMilliseconds.ToString("0");
            var saved = Path.Combine(_workspace, "paint.mapproj");
            if (File.Exists(saved)) { File.Copy(saved, mapPath, true); _report.Environment["test-map"] = mapPath; }
        }
        catch (Exception ex) { _report.Environment["test-map"] = "save failed: " + ex.Message; }

        var scale = EnvNumber("MAP_PAINT_SCALE", 0.41);
        var centre = (X: 2048.0, Y: 1536.0);
        SetScale(centre, scale);
        _report.Environment["exact/open"] = (await TimedRefined()).ToString("0");
        await Capture("newart-open");
        if (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_RIBBON") == "1")
        {
            await RibbonShots();
            return;
        }
        if (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_STYLE") == "1")
        {
            await StyleRoutes(centre, scale);
            _report.Checks["gpu"] = _report.Routes.All(r => r.Gpu);
            if (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_STYLE_PRESETS") is not { Length: > 0 })
                _report.Checks["style-routes"] = _report.Routes.Count == 38;
            return;
        }
        if (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_LIGHT") == "1")
        {
            await LightingRoutes(centre, scale);
            _report.Checks["gpu"] = _report.Routes.All(r => r.Gpu);
            return;
        }

        var passes = int.TryParse(System.Environment.GetEnvironmentVariable("MAP_PAINT_PASSES"), out var p) ? Math.Max(1, p) : 1;
        for (var pass = 0; pass < passes; pass++)
        {
            var suffix = passes > 1 ? $"#{pass + 1}" : "";
            SetScale(centre, scale); await TimedRefined();

            if (Want("view")) {
            await BurstRoute("pan" + suffix, 60, _ => canvas.PanByScreenDeltaForSmoke(-10, -4));
            _report.Environment["exact/pan" + suffix] = (await TimedRefined()).ToString("0");
            _report.Environment["maxframe/pan" + suffix] = LastRefinedMaxFrameMs.ToString("0");
            await BurstRoute("zoom-in" + suffix, 12, _ =>
            {
                var v = _editor.Session.Viewport; canvas.ZoomAtForSmoke(v.ViewWidth / 2, v.ViewHeight / 2, 1.12);
            });
            _report.Environment["exact/zoom-in" + suffix] = (await TimedRefined()).ToString("0");
            _report.Environment["maxframe/zoom-in" + suffix] = LastRefinedMaxFrameMs.ToString("0");
            await BurstRoute("zoom-out" + suffix, 12, _ =>
            {
                var v = _editor.Session.Viewport; canvas.ZoomAtForSmoke(v.ViewWidth / 2, v.ViewHeight / 2, 1 / 1.12);
            });
            _report.Environment["exact/zoom-out" + suffix] = (await TimedRefined()).ToString("0");
            _report.Environment["maxframe/zoom-out" + suffix] = LastRefinedMaxFrameMs.ToString("0");
            SetScale(centre, scale); await TimedRefined(); }

            // Land: a shape stroke across the plain's edge into the sea.
            _editor.ActiveTool = EditorTool.Land; _editor.SetLandPaintMode(false);
            _editor.SelectLayer(plain.Id);
            // The land panel resizes the canvas: wait for the exact terrain of the new view, as
            // a user who looks before drawing does; the first stroke right after a tool change
            // rebuilds the whole view (no earlier image of that view exists).
            await TimedRefined();
            if (Want("land")) {
            _editor.BrushSettings = _editor.BrushSettings with { Radius = 40, Softness = .5, Spacing = .25 };
            await StrokeRoute("land-shape-stroke" + suffix, (260, 1500), 60, 9, 4);
            _report.Environment["exact/land-shape" + suffix] = (await TimedRefined()).ToString("0");
            _editor.Undo(); await TimedRefined(); }

            // Land material: each of the owner's grounds painted on the plain.
            if (Want("paint")) {
            _editor.SetLandPaintMode(true);
            foreach (var id in new[] { MapFillTextures.OwnerSnowSparkle, MapFillTextures.OwnerIceWash })
            {
                _report.Checks["material/" + id + suffix] = _editor.ChooseLandMaterial(id);
                _editor.BrushSettings = _editor.BrushSettings with { Radius = 64, Softness = .8, Spacing = .15 };
                await StrokeRoute("land-paint-" + id + suffix, (1900, 1900), 60, 9, 3);
                _report.Environment["exact/land-paint-" + id + suffix] = (await TimedRefined()).ToString("0");
                _editor.Undo(); await TimedRefined();
            }
            _editor.SetLandPaintMode(false); }

            // Tiers: the plateau down a level and back up, then a land stroke over a raised edge.
            if (Want("tier")) {
            _editor.SelectLayer(plateau.Id); await Settle();
            await Route("tier-down" + suffix, 1, _ => _editor.ShiftLandLevel(-1));
            _report.Environment["exact/tier-down" + suffix] = (await TimedRefined()).ToString("0");
            await Route("tier-up" + suffix, 1, _ => _editor.ShiftLandLevel(1));
            _report.Environment["exact/tier-up" + suffix] = (await TimedRefined()).ToString("0");
            await BurstRoute("cliff-slider" + suffix, 30, i =>
                _editor.SetActiveLandStyle(style => style with { Cliff = style.Cliff with { Height = 30 + i } }));
            PrintEditTimings("cliff-slider(last step)" + suffix);
            _report.Environment["exact/cliff-slider" + suffix] = (await TimedRefined()).ToString("0");
            _editor.BrushSettings = _editor.BrushSettings with { Radius = 40, Softness = .5, Spacing = .25 };
            await TimedRefined();
            await StrokeRoute("tier-land-stroke" + suffix, (1250, 1580), 60, 9, 4);
            _report.Environment["exact/tier-land-stroke" + suffix] = (await TimedRefined()).ToString("0");
            for (var u = 0; u < 3; u++) _editor.Undo();
            await TimedRefined(); }

            // A new land on top, the way the owner starts a new tier: «Новая земля» and a stroke.
            if (Want("newland")) {
            _editor.ChooseLandTexture(MapFillTextures.OwnerSnowSoft);
            _editor.AddLandLayer(); await Settle(); await TimedRefined();
            await StrokeRoute("new-land-stroke" + suffix, (1700, 1400), 60, 9, 5);
            _report.Environment["exact/new-land" + suffix] = (await TimedRefined()).ToString("0");
            await Route("new-land-tier-up" + suffix, 1, _ => _editor.ShiftLandLevel(1));
            _report.Environment["exact/new-land-tier-up" + suffix] = (await TimedRefined()).ToString("0");
            _editor.Undo(); _editor.Undo(); _editor.Undo(); await TimedRefined(); }

            // Brush and mask strokes over the mountains.
            if (Want("brush")) {
            _editor.AddBrushLayer(); await Settle();
            _editor.ActiveTool = EditorTool.Brush;
            _editor.BrushSettings = _editor.BrushSettings with { Radius = 32, Softness = .5, Spacing = .25, Color = "#FF6A3E1F", Texture = null };
            await StrokeRoute("brush-stroke" + suffix, centre, 60, 9, 5);
            _editor.Undo(); _editor.Undo(); await TimedRefined(); }
            if (Want("mask")) {
            var layerId = _editor.Session.Document.Layers.OfType<ObjectLayer>().First().Id;
            _editor.SelectLayer(layerId); _editor.ActiveTool = EditorTool.Mask; await Settle();
            if (_editor.ActiveLayer?.Mask is null) { _editor.ToggleActiveMask(); await Settle(); }
            await StrokeRoute("mask-stroke" + suffix, centre, 60, 9, 6);
            _editor.Undo(); if (_editor.ActiveLayer?.Mask is not null) _editor.Undo();
            _editor.ActiveTool = EditorTool.Select; await TimedRefined(); }

            // Placement of the owner's mountain: ghost, single clicks.
            if (_sets.Count > 0 && Want("place"))
            {
                _editor.RefreshLibrary(); _view.RefreshLibraryPanel(); await Settle();
                // A card is picked while it is on screen: the gallery has verified the visible cards' files
                // in the background by then (RefreshVisibleAvailabilityAsync), as in the real window.
                _editor.ReportRealizedLibraryRows(Enumerable.Range(0, _editor.LibraryRows.Count));
                await _editor.RefreshVisibleAvailabilityAsync(); await Settle();
                var armed = false;
                await Route("placement-activate" + suffix, 1, _ => armed = _editor.ChooseGalleryVariant(_sets[0].Id, null));
                _report.Checks["placement/armed" + suffix] = armed;
                if (armed)
                {
                    _editor.StampPlacementMode = StampPlacementMode.One;
                    await BurstRoute("placement-ghost" + suffix, 60, i =>
                    {
                        var pt = _editor.Session.Viewport.WorldToScreen(centre.X - 240 + i * 8, centre.Y + 100);
                        canvas.MovePlacementPointerForSmoke(pt.X, pt.Y);
                    });
                    var placed = 0;
                    await BurstRoute("placement-clicks" + suffix, 64, i =>
                    {
                        var pt = _editor.Session.Viewport.WorldToScreen(centre.X - 300 + i * 9, centre.Y + 60 + (i % 8) * 4);
                        canvas.MovePlacementPointerForSmoke(pt.X, pt.Y);
                        if (i % 8 == 4) { canvas.CommitPlacementForSmoke(); placed++; }
                    });
                    _report.Checks["placement/placed" + suffix] = placed == 8;
                    await Settle();
                    PrintEditTimings("placement-click(last)");
                    var (prep, names, commit, meta) = _editor.LastPlacementTiming;
                    Console.WriteLine($"    placement timing prepare={prep:0.0} names={names:0.0} commit={commit:0.0} metadata={meta:0.0}");
                    for (var i = 0; i < placed; i++) _editor.Undo();
                    _editor.CancelPlacement();
                }
                _editor.ActiveTool = EditorTool.Select; await TimedRefined();
            }
        }
        await Capture("newart-end");
        // Memory: the managed heap against the process, and Skia's own CPU caches. With
        // MAP_HOLD_AT_END=<seconds> the process waits so dotnet-gcdump / dotnet-counters can attach.
        var gc = GC.GetGCMemoryInfo();
        _report.Environment["mem/gc-heap-mib"] = (gc.HeapSizeBytes / (1024 * 1024)).ToString();
        _report.Environment["mem/gc-committed-mib"] = (gc.TotalCommittedBytes / (1024 * 1024)).ToString();
        _report.Environment["mem/gc-fragmented-mib"] = (gc.FragmentedBytes / (1024 * 1024)).ToString();
        _report.Environment["mem/skia-raster-cache-mib"] = (SkiaSharp.SKGraphics.GetResourceCacheTotalBytesUsed() / (1024 * 1024)).ToString();
        _report.Environment["mem/skia-font-cache-mib"] = (SkiaSharp.SKGraphics.GetFontCacheUsed() / (1024 * 1024)).ToString();
        _report.Environment["mem/object-cache-mib"] = (_view.Canvas.ObjectCacheBytes / (1024 * 1024)).ToString();
        if (System.Environment.GetEnvironmentVariable("MAP_MEM_SPLIT") == "1")
        {
            // What the render caches and stamp images hold: private bytes before and after they are
            // dropped and the heap is compacted. The rest is the editor, the document, the history,
            // Avalonia and the graphics driver.
            static long Private() { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64 / (1024 * 1024); }
            GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
            var before = Private();
            _report.Environment["mem/gpu-resource-cache-mib"] = (Map.App.Controls.MapCanvasControl.LastGpuResourceCacheBytes / (1024 * 1024)).ToString();
            Map.App.Controls.MapCanvasControl.PurgeGpuResourcesOnNextFrame();
            _view.Canvas.InvalidateVisual(); await Settle(); await Task.Delay(1000);
            var afterGpu = Private();
            _report.Environment["mem/private-after-gpu-purge-mib"] = afterGpu.ToString();
            Console.WriteLine($"MEM private {before} MiB -> {afterGpu} MiB after purging the GPU resource cache ({_report.Environment["mem/gpu-resource-cache-mib"]} MiB held)");
            _view.Canvas.DisposeRenderCaches();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers();
            await Task.Delay(1500);
            var after = Private();
            _report.Environment["mem/private-before-drop-mib"] = before.ToString();
            _report.Environment["mem/private-after-drop-mib"] = after.ToString();
            _report.Environment["mem/gc-heap-after-drop-mib"] = (GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024)).ToString();
            Console.WriteLine($"MEM private {before} MiB -> {after} MiB after dropping render caches and stamp images");
        }
        if (int.TryParse(System.Environment.GetEnvironmentVariable("MAP_HOLD_AT_END"), out var hold) && hold > 0)
        {
            Console.WriteLine($"HOLD pid={System.Environment.ProcessId} for {hold} s");
            await Task.Delay(TimeSpan.FromSeconds(hold));
        }
        var (ws, pb) = Memory();
        _report.Environment["working-set-mib"] = ws.ToString(); _report.Environment["private-mib"] = pb.ToString();
        _report.Environment["cliff-fields"] = $"computed={Map.Rendering.LandCliffField.Computed} compute={Map.Rendering.LandCliffField.ComputeMilliseconds:0} ms draw={Map.Rendering.LandCliffField.DrawMilliseconds:0} ms";
        _report.Checks["gpu"] = _report.Routes.All(r => r.Gpu);
        _report.Checks["routes"] = _report.Routes.Count >= (System.Environment.GetEnvironmentVariable("MAP_NEW_ART_ONLY") is { Length: > 0 } ? 1 : 10);
        _report.Checks["unchanged-after-undo"] = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == count;
    }

    private static bool Want(string group) =>
        System.Environment.GetEnvironmentVariable("MAP_NEW_ART_ONLY") is not { Length: > 0 } only || only.Split(',').Contains(group);

    private static int Coverage(LandLayer land, double x, double y)
    {
        var key = TileMap.KeyAt(x, y);
        if (land.GeometryTiles.TryGet(key) is not { } tile) return 0;
        var px = (int)(x - key.X * TileMap.TileSize); var py = (int)(y - key.Y * TileMap.TileSize);
        return tile[py * TileMap.TileSize + px];
    }
}
