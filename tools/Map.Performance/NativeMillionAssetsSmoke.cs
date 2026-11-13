using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Platform;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class ZoomAssetsApplication
{
    // One case per process keeps the large document and GPU lifetime out of the next measurement.
    private async Task ExecuteMillion()
    {
        var root = System.Environment.GetEnvironmentVariable("MAP_DRIVE_LIBRARY_ROOTS")
            ?? throw new InvalidOperationException("Set MAP_DRIVE_LIBRARY_ROOTS to the owner's library.");
        _catalog.SetMounts(Directory.GetDirectories(root).Where(p => File.Exists(Path.Combine(p, "catalog.json"))).Order().ToArray());
        var records = _catalog.Records.OrderBy(r => r.Sha256, StringComparer.Ordinal).ToArray();
        if (records.Length == 0) throw new InvalidOperationException("No mounted images.");
        var kind = System.Environment.GetEnvironmentVariable("MAP_MILLION_CASE") ?? "giant";
        var count = Options.ObjectCount;
        var random = new Random(Options.Seed);
        random.Shuffle(records);
        var w = kind == "standard" ? 1920 : kind == "giant" ? 131072 : 32768;
        var h = kind == "standard" ? 1080 : w;
        var anchor = (X: w / 2d, Y: h / 2d);
        var objects = new SceneObject[count];
        for (var i = 0; i < count; i++)
        {
            var r = records[i % records.Length];
            var width = 64 * (0.5 + random.NextDouble() * 1.5);
            // Sparse districts on the giant; the ordinary map deliberately tests very dense overlap.
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Ассет " + i,
                random.NextDouble() * (w - width), random.NextDouble() * (h - width),
                width, width * r.Height / r.Width, random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new StampStyle(r.Sha256) };
        }
        var last = objects[^1];
        objects[^1] = new SceneObject(last.Id, last.Kind, last.Name, anchor.X - last.Width / 2,
            anchor.Y - last.Height / 2, last.Width, last.Height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = last.Stamp };
        var refs = records.Select(r => new MapAssetReference(r.Sha256, r.DisplayName, r.MediaType, r.Extension,
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner-drive", "user-owned", catalogId: r.Sha256)).ToArray();
        var layers = new List<MapLayer> { CurrentLook.Page(), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects) };
        if (kind == "hex") layers.Insert(1, new HexLayer(LayerId.New(), "1024 × 1024", true, false, 1,
            HexWorldGenerator.Run(new HexLayer(LayerId.New(), "Мир", true, false, 1, HexBoard.Create(HexOrientation.Flat, 16, 1024, 1024)),
                HexGenerationStep.Land, new HexGeneratorSettings { Seed = Options.Seed }).Board));
        var document = new MapDocument(DocumentId.New(), $"Astra · {kind} · {count:N0}", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(w, h, "px", CurrentLook.PageColor), layers).WithAssets(refs);
        if (kind == "light") document = document with { Lighting = new MapLighting { TimeOfDay = .7,
            Sources = [new MapLightSource(Guid.NewGuid(), anchor.X, anchor.Y, "#FFFFBB66", 500, 1, false)] } };
        _report.Observation = "Visible native editor on the selected test display; production canvas; frame = render-thread scene completion, not monitor scan-out. GPU flush is separately measured when MAP_MEASURE_GPU_FLUSH=1.";
        _report.Environment["mode"] = "ЖИВОЙ: visible native editor; real canvas GPU completion; not monitor scan-out";
        _report.Environment["library"] = root;
        _report.Environment["distinct-images"] = Math.Min(count, records.Length).ToString();
        _report.Environment["objects"] = count.ToString();
        _report.Environment["seed"] = Options.Seed.ToString();
        _report.Environment["case"] = kind;
        _report.Environment["profile"] = _workspace;
        Console.WriteLine($"million: {kind}, objects={count}, images={records.Length}, workspace={_workspace}");
        var scenario = new ZoomScenarioReport { Name = kind, CanvasWidth = w, CanvasHeight = h, Objects = count };
        _report.Scenarios.Add(scenario);
        void Checkpoint() => File.WriteAllText(Path.ChangeExtension(Options.OutputPath, ".progress.json"), JsonSerializer.Serialize(_report, new JsonSerializerOptions { WriteIndented = true }));
        Checkpoint();
        var open = Stopwatch.StartNew();
        OpenEditor(document);
        await Route(scenario, "open-first-frame", 1, _ => _view.Canvas.FitToCanvas());
        if (System.Environment.GetEnvironmentVariable("MAP_PERF_CAPTURE_PROGRESSIVE") == "1")
            await MillionCapture(kind + "-first-progressive");
        await Settled(scenario.Routes[^1]);
        if (System.Environment.GetEnvironmentVariable("MAP_PERF_DERIVED_ROOT") is not null)
        {
            if (_editor.StampImages is Map.Presentation.Services.CatalogStampImageSource prepared)
            {
                _report.Environment["derived-disk-hits"] = prepared.DerivedStatistics.DiskHits.ToString();
                _report.Environment["derived-builds"] = prepared.DerivedStatistics.Builds.ToString();
            }
        }
        scenario.OpenToExactTerrainMs = open.Elapsed.TotalMilliseconds;
        if (kind == "style") { _editor.ApplyStylePreset(MapStylePreset.WatercolorBigShapes); await Step(() => { }); }
        await MillionCapture(kind + "-fit"); Checkpoint();
        if (System.Environment.GetEnvironmentVariable("MAP_PERF_CAPTURE_REBUILT") == "1")
        {
            _view.Canvas.ClearDerivedRenderCachesForSmoke();
            await Step(() => { });
            await Settled(scenario.Routes[^1]);
            await MillionCapture(kind + "-fit-rebuilt"); Checkpoint();
            _view.Canvas.ClearDerivedRenderCachesForSmoke();
            Map.App.Controls.MapCanvasControl.PurgeGpuResourcesOnNextFrame();
            await Step(() => { });
            await Settled(scenario.Routes[^1]);
            await MillionCapture(kind + "-fit-purged"); Checkpoint();
        }
        if (System.Environment.GetEnvironmentVariable("MAP_MILLION_FIRST_ONLY") == "1")
        {
            _report.Environment["scope"] = "diagnostic first frame only; pan/zoom/edit routes NOT CHECKED";
            _report.Checks["first-frame-gpu"] = _view.Canvas.LastFrameHadGpuContext;
            await Finish(scenario); Checkpoint(); return;
        }
        var fit = _editor.Session.Viewport.Scale;
        foreach (var scale in new[] { .25, 1d, 4d })
        {
            await Route(scenario, "scale-" + scale, 1, _ => SetScale(anchor, scale));
            Checkpoint(); // Preserve the completed cold-zoom measurement even if the following burst times out.
            await BurstRoute(scenario, "pan-" + (scale * 100), 120, i => _view.Canvas.PanByScreenDeltaForSmoke(i < 60 ? -3 : 3, 0));
            await MillionCapture(kind + "-" + (scale * 100)); Checkpoint();
        }
        await Route(scenario, "fit", 1, _ => _view.Canvas.FitToCanvas());
        var notches = (int)Math.Ceiling(Math.Log(16 / fit) / Math.Log(1.15));
        await BurstRoute(scenario, "wheel-to-1600", notches, _ => ZoomAt(anchor, 1.15)); Checkpoint();
        await BurstRoute(scenario, "wheel-to-fit", notches, _ => ZoomAt(anchor, 1 / 1.15)); Checkpoint();
        if (System.Environment.GetEnvironmentVariable("MAP_PERF_LONG_ZOOM") == "1")
        {
            var duration = Stopwatch.StartNew();
            var cycles = 0;
            _report.Environment["long-zoom-start-working-set-mib"] = Memory().WorkingSet.ToString();
            // Same real wheel input and settled-frame gate as the short route. Per-route
            // snapshots retain resident bytes, pending requests, GC and process working set.
            while (duration.Elapsed < TimeSpan.FromMinutes(10))
            {
                cycles++;
                await BurstRoute(scenario, $"wheel-long-{cycles}-in", notches, _ => ZoomAt(anchor, 1.15));
                await BurstRoute(scenario, $"wheel-long-{cycles}-out", notches, _ => ZoomAt(anchor, 1 / 1.15));
                _report.Environment["long-zoom-elapsed-ms"] = duration.Elapsed.TotalMilliseconds.ToString("F1");
                Checkpoint();
            }
            _report.Environment["long-zoom-cycles"] = cycles.ToString();
            _report.Environment["long-zoom-end-working-set-mib"] = Memory().WorkingSet.ToString();
            _report.Checks["long-zoom-ten-minutes"] = duration.Elapsed >= TimeSpan.FromMinutes(10);
            await MillionCapture(kind + "-long-zoom");
        }
        await Route(scenario, "select", 1, _ => { SetScale(anchor, 1); var p = _editor.Session.Viewport.WorldToScreen(anchor.X, anchor.Y); _view.Canvas.ClickForSmoke(p.X, p.Y); });
        _report.Checks["selected"] = _editor.Session.SelectedObject is not null;
        var selected = _editor.Session.SelectedObject;
        await Route(scenario, "drag", 1, _ => { var p = _editor.Session.Viewport.WorldToScreen(anchor.X, anchor.Y); _view.Canvas.PressMoveReleaseForSmoke(p.X, p.Y, p.X + 32, p.Y + 24); });
        _report.Checks["dragged"] = selected is not null && _editor.Session.SelectedObject is { } moved && (moved.X != selected.X || moved.Y != selected.Y);
        await Route(scenario, "undo-drag", 1, _ => _editor.Undo());
        _report.Checks["undo-drag"] = selected is not null && _editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Objects).Any(o => o.Id == selected.Id && o.X == selected.X && o.Y == selected.Y);
        var set = _catalog.Sets.First(s => !s.Archived);
        _report.Checks["placement-armed"] = _editor.ChooseGalleryVariant(set.Id, null);
        await Route(scenario, "place", 1, _ => { var p = _editor.Session.Viewport.WorldToScreen(anchor.X + 100, anchor.Y); _view.Canvas.MovePlacementPointerForSmoke(p.X, p.Y); _view.Canvas.CommitPlacementForSmoke(); });
        _report.Checks["placed-one"] = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == count + 1;
        _editor.CancelPlacement();
        await Route(scenario, "undo-place", 1, _ => _editor.Undo());
        _report.Checks["undo-place"] = _editor.Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == count;
        Checkpoint();
        using var preview = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
        using var png = preview.Encode(SKEncodedImageFormat.Png, 80);
        var path = Path.Combine(_workspace, "million.mapproj");
        var io = Stopwatch.StartNew();
        await Task.Run(() => MapProjectStore.Save(_editor.Session.Document, path, new SaveMetadata("astra-fps", DateTimeOffset.UnixEpoch), png.ToArray()));
        _report.Environment["save-ms"] = io.Elapsed.TotalMilliseconds.ToString("F1");
        io.Restart();
        var reopened = await Task.Run(() => MapProjectStore.Open(path));
        _report.Environment["reopen-ms"] = io.Elapsed.TotalMilliseconds.ToString("F1");
        _report.Checks["reopened-count"] = reopened.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) == count;
        _report.Checks["gpu"] = scenario.Routes.All(r => r.Gpu);
        _report.Checks["fps-target"] = scenario.Routes.Where(r => r.Name != "open-first-frame").All(r =>
            r.FrameWorkMs.Count > 0 && r.FrameWorkP99 <= 16.7 && r.FrameWorkMax <= 33 && r.RefinementWorkMax <= 33
            && r.LatencyMax <= 33 && (r.FrameIntervalP99 is null || r.FrameIntervalP99 <= 16.7));
        await Finish(scenario); Checkpoint();
    }

    private async Task MillionCapture(string name)
    {
        var folder = System.Environment.GetEnvironmentVariable("MAP_PERF_SHOTS") ?? Path.GetDirectoryName(Options.OutputPath)!;
        Directory.CreateDirectory(folder);
        using var bitmap = await _view.Canvas.CaptureGpuFrameForSmokeAsync();
        _report.Checks["settled-capture/" + name] = !_view.Canvas.LastTerrainRefinementPending
            && !_view.Canvas.LastFrameFastTerrain && _view.Canvas.LastRenderStats.DetailTilesPending == 0;
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(folder, name + "-gpu.png");
        using (var file = File.Create(path)) png.SaveTo(file);
        _report.Frames.Add(path);
        // Window evidence includes the editor controls. The GPU capture above is the pixel source for comparisons.
        var hwnd = _window.TryGetPlatformHandle()!.Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("Window rectangle unavailable.");
        var width = rect.R - rect.L; var height = rect.B - rect.T;
        var header = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var dib = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        var previous = SelectObject(dc, dib);
        try
        {
            if (dib == IntPtr.Zero || !PrintWindow(hwnd, dc, 2)) throw new InvalidOperationException("PrintWindow failed.");
            using var pixmap = new SKPixmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), bits, width * 4);
            using var image = SKImage.FromPixels(pixmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            path = Path.Combine(folder, name + "-window.png");
            using var file = File.Create(path); data.SaveTo(file); _report.Frames.Add(path);
        }
        finally { SelectObject(dc, previous); DeleteObject(dib); DeleteDC(dc); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
