using System.Diagnostics;
using System.Text.Json;
using Avalonia.Platform;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Performance;

public sealed partial class ZoomAssetsApplication
{
    private async Task ExecuteBlurReview(ZoomScenarioReport scenario, SceneObject[] objects)
    {
        var folder = Environment.GetEnvironmentVariable("MAP_PERF_BLUR_ROOT")
            ?? throw new ArgumentException("MAP_PERF_BLUR_ROOT is required.");
        Directory.CreateDirectory(folder);
        var canvas = _view.Canvas;
        if (_editor.StampImages is CatalogStampImageSource stampSource) stampSource.CaptureMipSelections = true;
        var w = scenario.CanvasWidth; var h = scenario.CanvasHeight;
        var prefix = $"{w}-{scenario.Objects}";
        var jsonPath = Path.Combine(folder, prefix + ".json");
        var captures = new List<Dictionary<string, object?>>();
        var centered = objects[^1];
        var centerRecord = _catalog.Find(centered.Stamp!.AssetSha256)!;
        _report.Environment["blur-centered-asset"] = JsonSerializer.Serialize(new
        {
            centerRecord.Sha256, centerRecord.DisplayName, centerRecord.Width, centerRecord.Height,
            source = _catalog.SourcePath(centerRecord), worldWidth = centered.Width, worldHeight = centered.Height,
            screenWidthAt1600 = centered.Width * 16, screenHeightAt1600 = centered.Height * 16,
        });
        // The fixture is uniformly random. Densest/sparsest means the extrema of this
        // fixed 32x32 centre histogram, not a claim that any whole viewport is empty.
        var cells = new int[1024];
        foreach (var obj in objects)
        {
            var x = Math.Clamp((int)((obj.X + obj.Width / 2) * 32 / w), 0, 31);
            var y = Math.Clamp((int)((obj.Y + obj.Height / 2) * 32 / h), 0, 31);
            cells[y * 32 + x]++;
        }
        var dense = Array.IndexOf(cells, cells.Max()); var sparse = Array.IndexOf(cells, cells.Min());
        (double X, double Y) Cell(int index) => ((index % 32 + .5) * w / 32, (index / 32 + .5) * h / 32);
        var center = (X: w / 2d, Y: h / 2d);
        var edge = (X: 0d, Y: h / 2d);
        await Step(canvas.FitToCanvas);
        var fit = _editor.Session.Viewport.Scale;
        var points = new[]
        {
            ("fit", fit, "center", center, false), ("25", .25, "edge", edge, false),
            ("100", 1d, "center", center, true), ("100", 1d, "sparse-cell", Cell(sparse), false),
            ("400", 4d, "center", center, true), ("400", 4d, "dense-cell", Cell(dense), false),
            ("1600", 16d, "center", center, true), ("1600", 16d, "sparse-cell", Cell(sparse), true),
        };
        _report.Environment["blur-review"] = "8 points, first completed frame after last pan input and 30 seconds later; four key points also at 5 seconds; 20 PNG per scene; no settled gate; PNG lossless zlib=0";
        _report.Environment["blur-regions"] = $"32x32 centre histogram; dense={dense}/{cells[dense]}, sparse={sparse}/{cells[sparse]}; sparse is not necessarily empty";
        for (var i = 0; i < points.Length; i++)
        {
            var (label, scale, region, anchor, atFive) = points[i];
            await Step(() => SetScale(anchor, scale));
            await Step(() => canvas.PanByScreenDeltaForSmoke(-1, 0));
            var sinceInput = Stopwatch.StartNew();
            await Step(() => { sinceInput.Restart(); canvas.PanByScreenDeltaForSmoke(1, 0); });
            var a = Capture("immediate");
            Dictionary<string, object?>? atFiveRow = null;
            if (atFive)
            {
                var untilFive = 5000 - sinceInput.Elapsed.TotalMilliseconds;
                if (untilFive > 0) await Task.Delay(TimeSpan.FromMilliseconds(untilFive));
                atFiveRow = Capture("after5s");
            }
            var remaining = 30000 - sinceInput.Elapsed.TotalMilliseconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining));
            var b = Capture("after30s");
            // Count only after both captures, so the diagnostic query cannot delay mip
            // preparation within the observation interval. Bounds include rotated art.
            var viewport = _editor.Session.Viewport;
            var topLeft = viewport.ScreenToWorld(0, 0);
            var bottomRight = viewport.ScreenToWorld(canvas.Bounds.Width, canvas.Bounds.Height);
            var bounds = new WorldBounds(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
            var layer = _editor.Session.Document.Layers.OfType<ObjectLayer>().Last();
            var visible = await Task.Run(() =>
            {
                var found = ObjectSpatialIndex.For(layer).Query(bounds);
                var assets = found.Where(obj => obj.Stamp is not null).GroupBy(obj => obj.Stamp!.AssetSha256).Select(group =>
                {
                    var record = _catalog.Find(group.Key);
                    return new { sha = group.Key, count = group.Count(), name = record?.DisplayName,
                        width = record?.Width, height = record?.Height, source = record is null ? null : _catalog.SourcePath(record),
                        projectedWidthMax = group.Max(obj => obj.Width) * scale,
                        projectedHeightMax = group.Max(obj => obj.Height) * scale };
                }).ToArray();
                return new { Count = found.Count, Assets = assets };
            });
            a["intersectingObjects"] = b["intersectingObjects"] = visible.Count;
            a["visibleAssets"] = b["visibleAssets"] = visible.Assets;
            if (atFiveRow is not null) { atFiveRow["intersectingObjects"] = visible.Count; atFiveRow["visibleAssets"] = visible.Assets; }
            Save();
            Console.WriteLine($"blur {prefix}/{i + 1}: {region}/{label}, intersects={visible.Count}, captured immediate/30s");

            Dictionary<string, object?> Capture(string phase)
            {
                var name = $"{prefix}-{i + 1:00}-{region}-{label}-{phase}.png";
                var row = new Dictionary<string, object?>
                {
                    ["file"] = name, ["region"] = region, ["zoomLabel"] = label,
                    ["scale"] = _editor.Session.Viewport.Scale, ["anchorX"] = anchor.X, ["anchorY"] = anchor.Y,
                    ["phase"] = phase, ["elapsedSinceInputMs"] = sinceInput.Elapsed.TotalMilliseconds,
                    ["utc"] = DateTimeOffset.UtcNow, ["qpc"] = Stopwatch.GetTimestamp(),
                    ["before"] = Snapshot(),
                };
                CaptureBlurWindow(Path.Combine(folder, name));
                row["after"] = Snapshot();
                row["captureEndedMs"] = sinceInput.Elapsed.TotalMilliseconds;
                captures.Add(row);
                _report.Frames.Add(Path.Combine(folder, name));
                Save();
                return row;
            }
        }
        _report.Checks["blur/20-window-captures"] = captures.Count == 20;
        _report.Checks["blur/gpu"] = canvas.LastFrameHadGpuContext;
        // Read-only source evidence for separating interpolation/LOD from the art itself.
        using (var source = SKBitmap.Decode(_catalog.SourcePath(centerRecord)))
        using (var png = source.Encode(SKEncodedImageFormat.Png, 100))
        using (var output = File.Create(Path.Combine(Path.GetDirectoryName(Options.OutputPath)!, "centered-source.png")))
            png.SaveTo(output);
        await Finish(scenario);

        object Snapshot()
        {
            var stats = canvas.LastRenderStats;
            var diagnostic = new ZoomRouteReport { Name = "blur-snapshot" };
            CaptureRefinementState(diagnostic);
            return new
            {
                revision = canvas.CompletedSceneRevision, tier = canvas.LastLodTier.ToString(),
                fastTerrain = canvas.LastFrameFastTerrain, terrainPending = canvas.LastTerrainRefinementPending,
                gpu = canvas.LastFrameHadGpuContext, objectsDrawn = stats.ObjectsDrawn,
                exactObjects = stats.ExactObjects, overviewObjects = stats.OverviewObjects,
                cameraFramesReused = stats.CameraFramesReused, detailTiles = stats.ObjectTilesDrawn,
                sceneMs = canvas.LastSceneRenderMilliseconds,
                workMs = Map.App.Controls.MapCanvasControl.LastFrameWorkMilliseconds,
                counters = diagnostic.RefinementState, cache = diagnostic.ImageCacheAfterRefinement,
                mips = (_editor.StampImages as CatalogStampImageSource)?.ReadMipDiagnostics(),
                selections = (_editor.StampImages as CatalogStampImageSource)?.ReadMipSelections(),
                mipMeaning = "requested or pending keys; Resident = accepted in pixel cache, not proof of the level selected by a cached tile/camera draw",
            };
        }
        void Save() => File.WriteAllText(jsonPath, JsonSerializer.Serialize(captures, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void CaptureBlurWindow(string path)
    {
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
            using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.None, 0));
            using var file = File.Create(path); data.SaveTo(file);
        }
        finally { SelectObject(dc, previous); DeleteObject(dib); DeleteDC(dc); }
    }
}
