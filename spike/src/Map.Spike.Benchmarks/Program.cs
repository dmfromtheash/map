using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Map.Spike.Benchmarks;
using Map.Spike.Core.Commands;
using Map.Spike.Core.Documents;
using Map.Spike.Core.Fixtures;
using Map.Spike.Core.Geometry;
using Map.Spike.Core.Spatial;
using Map.Spike.Export;
using Map.Spike.Persistence;
using Map.Spike.Persistence.Atomic;
using Map.Spike.Rendering;
using SkiaSharp;

if (args.Length > 0 && string.Equals(args[0], "crash-during-save", StringComparison.OrdinalIgnoreCase))
{
    return CrashDuringSave.Run(args);
}

const int ObjectCount = 10_000;
const int CanvasSize = 4096;
const int FrameWidth = 1920;
const int FrameHeight = 1080;
const int FrameWarmup = 30;
const int FrameSamples = 300;

var outputDirectory = ValueOf(args, "--out")
    ?? Path.Combine(AppContext.BaseDirectory, "benchmark-output");
Directory.CreateDirectory(outputDirectory);

var report = new List<string>();
void Emit(string line)
{
    Console.WriteLine(line);
    report.Add(line);
}

Emit("# Map spike benchmark");
Emit("");
Emit("## Machine and runtime");
Emit($"- utc: {DateTimeOffset.UtcNow:O}");
Emit($"- os: {RuntimeInformation.OSDescription}");
Emit($"- arch: {RuntimeInformation.OSArchitecture} / process {RuntimeInformation.ProcessArchitecture}");
Emit($"- runtime: {RuntimeInformation.FrameworkDescription}");
Emit($"- cpu-cores: {Environment.ProcessorCount}");
Emit($"- machine: {Environment.MachineName}");
Emit($"- configuration: {(IsDebug() ? "Debug" : "Release")}");
Emit($"- server-gc: {System.Runtime.GCSettings.IsServerGC}");
Emit("- renderer: CPU raster (SKSurface.Create, no GPU context) - see the app window report for the GPU path");
Emit("");

// ---------------------------------------------------------------- scene construction

Emit("## Scene construction");

var buildStopwatch = Stopwatch.StartNew();
var document = SpikeFixtures.CreateObjectScene(ObjectCount, CanvasSize);
buildStopwatch.Stop();

Emit($"- objects: {document.Objects.Count}");
Emit($"- canvas: {document.CanvasWidth}x{document.CanvasHeight}");
Emit($"- build+index: {buildStopwatch.Elapsed.TotalMilliseconds:F2} ms");
Emit($"- spatial index: cellSize={document.Objects.Index!.CellSize:F1} " +
     $"cells={document.Objects.Index.CellCount} entries={document.Objects.Index.EntryCount}");

var documentBaselineBytes = MeasureManagedBytes();
Emit($"- managed heap after scene load: {Mb(documentBaselineBytes)}");
Emit("");

// ---------------------------------------------------------------- hit test

Emit("## Hit test");

var hitRng = new DeterministicRandom(0xA11CE);
var probes = new Vec2[2_000];
for (var i = 0; i < probes.Length; i++)
{
    probes[i] = document.Objects.Objects[hitRng.NextInt(document.Objects.Count)].Center;
}

var candidateTotal = 0L;
var hitCount = 0;
var hitCalls = 0;
var hitIndex = 0;

// Counted over every call, warm-up included: reporting hits against the measured-iteration count
// alone would print more hits than probes.
var hitMeasure = Measure.Run("hit-test (point, 10k scene)", 200, probes.Length, _ =>
{
    var stats = default(SpatialQueryStats);
    var found = document.Objects.HitTest(probes[hitIndex % probes.Length], ref stats);
    hitIndex++;
    hitCalls++;
    candidateTotal += stats.CandidatesExamined;
    if (found is not null)
    {
        hitCount++;
    }
});

Emit($"- {hitMeasure.Format()}");
Emit($"- hits: {hitCount}/{hitCalls} calls (warm-up included)");
Emit($"- mean candidates examined: {candidateTotal / (double)hitCalls:F1} of {ObjectCount} objects " +
     $"({candidateTotal / (double)hitCalls / ObjectCount * 100:F3}% of the scene)");
Emit("");

// ---------------------------------------------------------------- geometry cache

Emit("## Object geometry cache");

var geometryStopwatch = Stopwatch.StartNew();
using var geometryCache = ObjectGeometryCache.Build(document.Objects);
geometryStopwatch.Stop();

Emit($"- build: {geometryStopwatch.Elapsed.TotalMilliseconds:F2} ms");
Emit($"- chunks: {geometryCache.ChunkCount}, triangles: {geometryCache.TriangleCount:N0}");
Emit($"- approximate vertex storage: {Mb(geometryCache.ApproximateVertexBytes)}");
Emit("");

// ---------------------------------------------------------------- brush stroke

Emit("## Brush stroke and undo");

var history = new CommandHistory();
var beforeStrokeBytes = MeasureManagedBytes();

var strokeStopwatch = Stopwatch.StartNew();
var strokeSession = new BrushStrokeSession(document.Brush, BrushSettings.Default);
for (var i = 0; i < 120; i++)
{
    strokeSession.AddPoint(new Vec2(1000 + (i * 8), 1000 + (i * 5)));
}

var strokeCommand = strokeSession.Complete()!;
history.Execute(document, strokeCommand);
strokeStopwatch.Stop();

var afterStrokeBytes = MeasureManagedBytes();

Emit($"- stroke of 120 points: {strokeStopwatch.Elapsed.TotalMilliseconds:F2} ms");
Emit($"- tiles touched: {strokeCommand.TouchedTiles.Count} of {document.Brush.TileCount} materialised");
Emit($"- document raster tiles: {Mb(document.Brush.TotalTileBytes + document.Decor.TotalTileBytes)}");
Emit($"- undo history (before+after images): {Mb(history.ApproximateMemoryBytes)}");
Emit($"- managed heap delta across the stroke: {Mb(afterStrokeBytes - beforeStrokeBytes)}");

var undoMeasure = Measure.Run("undo+redo of one stroke", 5, 50, _ =>
{
    history.Undo(document);
    history.Redo(document);
});

Emit($"- {undoMeasure.Format()}");
Emit("");

// ---------------------------------------------------------------- mask stroke

var maskSession = new MaskStrokeSession(document.Mask, BrushSettings.Default with { Radius = 60, Flow = 1.0 });
for (var i = 0; i < 40; i++)
{
    maskSession.AddPoint(new Vec2(1100 + (i * 10), 1050 + (i * 6)));
}

history.Execute(document, maskSession.Complete()!);

Emit("## Mask");
Emit($"- mask tiles: {document.Mask.TileCount} ({Mb(document.Mask.TotalTileBytes)})");
Emit($"- undo history after brush+mask: {Mb(history.ApproximateMemoryBytes)}");
Emit("");

// ---------------------------------------------------------------- frame times (CPU raster)

Emit("## Frame time, CPU raster surface");
Emit($"- surface: {FrameWidth}x{FrameHeight} Rgba8888/Premul");

using var tileCache = new TileImageCache();

var frameInfo = new SKImageInfo(FrameWidth, FrameHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
using var frameSurface = SKSurface.Create(frameInfo)
    ?? throw new InvalidOperationException("Could not create the benchmark surface.");

var viewport = new Viewport();
viewport.Resize(FrameWidth, FrameHeight);

var settings = new RenderSettings
{
    IncludeOverlay = true,
    ShowMaskEditOverlay = false,
    MaskEnabled = true,
    BackgroundColor = new SKColor(24, 26, 30, 255),
};

var lastStats = default(RenderStats);

foreach (var (Name, Apply) in FrameScenarios(document))
{
    var frameIndex = 0;
    var measure = Measure.Run(Name, FrameWarmup, FrameSamples, _ =>
    {
        Apply(viewport, frameIndex++);
        var stats = default(RenderStats);
        SceneRenderer.Render(frameSurface.Canvas, document, viewport, settings, tileCache, geometryCache, ref stats);
        frameSurface.Canvas.Flush();
        lastStats = stats;
    });

    Emit($"- {measure.Format()}");
    Emit($"  - last frame: {lastStats}");
    Emit($"  - implied fps at median: {1000.0 / measure.Median:F1}, at p95: {1000.0 / measure.P95:F1}");
}

Emit($"- tile cache: entries={tileCache.EntryCount} hits={tileCache.Hits} misses={tileCache.Misses} " +
     $"bytes={Mb(tileCache.ApproximateBytes)}");
Emit("");

// ---------------------------------------------------------------- save / load

Emit("## Save, reopen, export");

var documentPath = Path.Combine(outputDirectory, "benchmark" + SpikeFormat.Extension);
if (File.Exists(documentPath))
{
    File.Delete(documentPath);
}

var store = new AtomicDocumentStore();
SaveResult? saveResult = null;

var saveMeasure = Measure.Run("atomic save (10k objects + raster)", 1, 5, _ =>
{
    saveResult = store.Save(document, documentPath);
});

Emit($"- {saveMeasure.Format()}");
Emit($"- file size: {Mb(saveResult!.BytesWritten)}");
Emit($"- saved revision: {saveResult.SavedRevision}, dirty after save: {document.IsDirty}");

SpikeDocument? reopened = null;
var loadMeasure = Measure.Run("reopen", 1, 5, _ =>
{
    reopened = AtomicDocumentStore.Load(documentPath);
});

Emit($"- {loadMeasure.Format()}");
Emit($"- reopened: {reopened!.Objects.Count} objects, revision {reopened.Revision}, dirty={reopened.IsDirty}");

var exportPath = Path.Combine(outputDirectory, "export-4096.png");
ExportResult? exportResult = null;

var exportMeasure = Measure.Run("offscreen export 4096x4096 transparent PNG", 1, 3, _ =>
{
    exportResult = OffscreenPngExporter.Export(document, exportPath, 4096, 4096);
});

Emit($"- {exportMeasure.Format()}");
Emit($"- export: {exportResult!.Width}x{exportResult.Height}, {Mb(exportResult.FileBytes)}, {exportResult.Stats}");
Emit("");

// ---------------------------------------------------------------- memory summary

using var process = Process.GetCurrentProcess();
process.Refresh();

Emit("## Memory");
Emit($"- managed heap after full run (GC.GetTotalMemory(true)): {Mb(MeasureManagedBytes())}");
Emit($"- total allocated over the run: {Mb(GC.GetTotalAllocatedBytes(precise: true))}");
Emit($"- process private bytes: {Mb(process.PrivateMemorySize64)}");
Emit($"- process working set: {Mb(process.WorkingSet64)}");
Emit("- accounted, by component:");
Emit($"  - document raster + mask tiles: {Mb(document.Brush.TotalTileBytes + document.Decor.TotalTileBytes + document.Mask.TotalTileBytes)}");
Emit($"  - render tile cache: {Mb(tileCache.ApproximateBytes)}");
Emit($"  - object geometry cache (approx, vertex buffers): {Mb(geometryCache.ApproximateVertexBytes)}");
Emit($"  - undo history: {Mb(history.ApproximateMemoryBytes)}");
Emit("");
Emit("Numbers above were produced by this run; nothing here is estimated.");

var reportPath = Path.Combine(outputDirectory, "benchmark-report.md");
File.WriteAllLines(reportPath, report);
Console.WriteLine();
Console.WriteLine($"Report written to {reportPath}");
return 0;

static IEnumerable<(string Name, Action<Viewport, int> Apply)> FrameScenarios(SpikeDocument document)
{
    var fitScale = Math.Min(
        FrameWidth / (double)document.CanvasWidth,
        FrameHeight / (double)document.CanvasHeight);

    yield return ("frame: whole canvas visible, static", (viewport, _) =>
        viewport.Set(new Vec2(0, 0), fitScale));

    yield return ("frame: whole canvas visible, panning", (viewport, i) =>
    {
        viewport.Set(new Vec2(0, 0), fitScale);
        viewport.PanByScreenDelta(Math.Sin(i * 0.05) * 40, Math.Cos(i * 0.05) * 40);
    }
    );

    yield return ("frame: 1:1 zoom, panning", (viewport, i) =>
        viewport.Set(new Vec2(1000 + (Math.Sin(i * 0.03) * 400), 1000 + (Math.Cos(i * 0.03) * 400)), 1.0));

    yield return ("frame: continuous zoom around cursor", (viewport, i) =>
    {
        viewport.Set(new Vec2(1500, 1500), 0.25);
        var steps = i % 60;
        for (var s = 0; s < steps; s++)
        {
            viewport.ZoomAt(new Vec2(FrameWidth / 2.0, FrameHeight / 2.0), 1.03);
        }
    }
    );
}

static long MeasureManagedBytes()
{
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    return GC.GetTotalMemory(forceFullCollection: true);
}

static string Mb(long bytes) =>
    string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:F2} MiB ({bytes:N0} B)");

static string? ValueOf(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static bool IsDebug()
{
#if DEBUG
    return true;
#else
    return false;
#endif
}
