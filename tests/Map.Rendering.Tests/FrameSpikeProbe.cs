using System.Diagnostics;
using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>CPU-only frame trace of the owner's wheel and edge-to-edge routes. No display pacing is included.</summary>
[Collection(RealTimeSettleCollection.Name)]
public sealed class FrameSpikeProbe
{
    private const int Width = 1280, Height = 720, Extent = 32768;

    [OptInFact("owner mountain frame-by-frame spike trace", "MAP_FRAME_SPIKES")]
    public async Task Trace_owner_routes()
    {
        var previous = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; // Map.App.Program.Main
        using var runtime = Environment.GetEnvironmentVariable("MAP_FRAME_RUNTIME") == "1" ? new FrameRuntimeEvents() : null;
        try
        {
            foreach (var count in Environment.GetEnvironmentVariable("MAP_FRAME_SPIKES")!.Split(',').Select(int.Parse))
                await Run(count);
        }
        finally { runtime?.Print(); System.Runtime.GCSettings.LatencyMode = previous; }
    }

    private sealed record Sample(string Route, int Frame, double Scale, double X, double Ms, double Begin,
        double Scene, double End, double Flush, double Objects, double Plan, double Palette, double Refinement,
        double Exact, double Ready, double Loop, double Cold, double Seed, double Prepare, double Raster,
        int Chunks, int Adoptions, long Pixels, int ExactLayer, int Direct, int Tiles, int Pending,
        long Allocated, long ProcessAllocated, int Gen0, int Gen1, int Gen2, double Pause, long GateMisses, long CanvasIo,
        double ChunkMs, double CollectMs, double PrepareMs, double DrawMs,
        CatalogStampImageSource.FrameTimingSnapshot Images, DateTime StartUtc, double CpuMs, long Contentions);

    private static async Task Run(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-frame-spikes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Map.slnx"))) repo = repo.Parent;
            Assert.NotNull(repo);
            var records = Enumerable.Range(1, 6).Select(i => catalog.Import(Path.Combine(repo.FullName,
                "tests", "fixtures", "assets", $"owner-mountain-{i:00}.png"), new(), DateTimeOffset.UnixEpoch)).ToArray();
            var random = new Random(13013);
            var objects = new SceneObject[count];
            for (var i = 0; i < count; i++)
            {
                var width = i % 128 == 0 ? 700 + random.Next(600) : 32 + random.Next(96);
                objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
                    random.NextDouble() * (Extent - width), random.NextDouble() * (Extent - width),
                    width, width, random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new StampStyle(records[i % 6].Sha256) };
            }
            var document = new MapDocument(DocumentId.New(), "frame spikes", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(Extent, Extent, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
                 new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
                records.Select(r => new MapAssetReference(r.Sha256, r.Sha256, r.MediaType, r.Extension,
                    ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: r.Sha256)).ToArray());
            // Opening/indexing is outside the route, as in the native FPS grid after the ready barrier.
            DocumentDerivedIndexes.Prepare(document);
            await using var source = new CatalogStampImageSource(catalog, () => document, () => null,
                derivedRoot: Path.Combine(root, "derived"), startWatcher: static _ => { });
            source.MeasureTimings = true;
            using var cache = new ObjectOverviewCache();
            using var atlas = new ObjectOverviewCache();
            var moving = false;
            using var preparation = new SceneRenderer.OverviewPreparation(() => Volatile.Read(ref moving));
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
            var stats = new RenderStats();
            var samples = new List<Sample>(4000);
            preparation.UpdateDocument(document, source, static () => { });
            Sample Frame(string route, double cx, double scale, bool camera)
            {
                moving = camera;
                source.FrameMoving = camera;
                stats = new RenderStats();
                var halfW = Width / 2d / scale; var halfH = Height / 2d / scale;
                var options = new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, AtlasCache = atlas, Stats = stats, ViewScale = scale,
                    OverviewPreparation = preparation, CameraMoving = camera, FastTerrain = camera,
                    LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                    VisibleWorld = new(cx - halfW, Extent / 2 - halfH, cx + halfW, Extent / 2 + halfH),
                    RequestRefinedFrame = static () => { },
                };
                var a = GC.GetAllocatedBytesForCurrentThread(); var processA = GC.GetTotalAllocatedBytes(false);
                var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
                var pause = GC.GetTotalPauseDuration(); var misses = source.FrameGateMisses;
                var io = source.DerivedIoDiagnostics.ReadsOnCanvasThread;
                var contention = Monitor.LockContentionCount;
                var startUtc = DateTime.UtcNow;
                var cpu = ThreadCpuMilliseconds();
                var start = Stopwatch.GetTimestamp();
                var c = surface.Canvas;
                c.ResetMatrix(); c.Clear(SKColors.White);
                c.Scale((float)scale); c.Translate((float)-(cx - halfW), (float)-(Extent / 2 - halfH));
                var scope = source.BeginFrame(); cache.BeginFrame(); atlas.BeginFrame();
                var begin = Stopwatch.GetTimestamp();
                SceneRenderer.Render(c, document, options);
                var scene = Stopwatch.GetTimestamp();
                c.Flush();
                var flush = Stopwatch.GetTimestamp();
                atlas.EndFrame(); cache.EndFrame(); scope?.Dispose();
                var end = Stopwatch.GetTimestamp();
                double Ms(long x, long y) => Stopwatch.GetElapsedTime(x, y).TotalMilliseconds;
                var sample = new Sample(route, samples.Count, scale, cx, Ms(start, end), Ms(start, begin), Ms(begin, scene),
                    Ms(flush, end), Ms(scene, flush), stats.ObjectsMilliseconds, stats.OverviewPlanMilliseconds,
                    stats.OverviewPaletteMilliseconds, stats.OverviewRefinementMilliseconds, stats.OverviewExactMilliseconds,
                    stats.DetailReadyMilliseconds, stats.DetailLoopMilliseconds, stats.DetailColdMilliseconds,
                    stats.DetailSeedMilliseconds, stats.DetailPrepareMilliseconds, stats.DetailRasterMilliseconds,
                    stats.OverviewChunks, stats.OverviewAdoptions, stats.OverviewAdoptedPixels, stats.OverviewExactLayer,
                    stats.ExactObjects, stats.DetailTilesBuiltOnFrame, stats.DetailTilesPending,
                    GC.GetAllocatedBytesForCurrentThread() - a, GC.GetTotalAllocatedBytes(false) - processA,
                    GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
                    (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, source.FrameGateMisses - misses,
                    source.DerivedIoDiagnostics.ReadsOnCanvasThread - io,
                    stats.OverviewChunkMilliseconds, stats.OverviewExactCollectMilliseconds,
                    stats.OverviewExactPrepareMilliseconds, stats.OverviewExactDrawMilliseconds, source.FrameTimings,
                    startUtc, ThreadCpuMilliseconds() - cpu, Monitor.LockContentionCount - contention);
                samples.Add(sample);
                return sample;
            }
            void Settle(double cx, double scale)
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(90))
                {
                    Frame("settle", cx, scale, false);
                    if (stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && stats.ObjectsDrawn > 0
                        && preparation.Statistics.Idle) return;
                    Thread.Sleep(8);
                }
                foreach (var s in samples) Console.WriteLine(Format(count, s));
                Assert.Fail($"{count} did not settle: pending={stats.DetailTilesPending} unresolved={stats.UnresolvedImages}");
            }
            var fit = Height / (double)Extent;
            Settle(Extent / 2, fit);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                for (var direction = 0; direction < 2; direction++)
                {
                    for (var f = 0; f < 180; f++)
                    {
                        var t = f / 179d;
                        var scale = Math.Pow(fit, direction == 0 ? 1 - t : t);
                        var started = Stopwatch.GetTimestamp();
                        Frame(direction == 0 ? "wheel-in" : "wheel-out", Extent / 2, scale, true);
                        // Give the actual background workers a frame interval; CPU work excludes this sleep.
                        var rest = 16 - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        if (rest > 0) Thread.Sleep((int)Math.Ceiling(rest));
                    }
                    Settle(Extent / 2, direction == 0 ? 1 : fit);
                }
                Settle(Width / 2, 1);
                for (var f = 0; f < 240; f++)
                {
                    var started = Stopwatch.GetTimestamp();
                    Frame("pan", Width / 2d + (Extent - Width) * f / 239d, 1, true);
                    var rest = 16 - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (rest > 0) Thread.Sleep((int)Math.Ceiling(rest));
                }
                Settle(Extent - Width / 2, 1);
            }
            Console.WriteLine($"SPIKES count={count} tiered={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")} pixels={PixelHash(surface)}");
            Console.WriteLine("FRAME count route frame scale x ms begin scene end flush objects plan palette refinement exact ready loop cold seed prepare raster chunks adopts pixels layer exactCount frameTiles pending alloc processAlloc gen0 gen1 gen2 pause gateMiss canvasIo chunkMs collectMs prepareMs drawMs imageGateMs imageLookupMs mipAdoptMs mipBytes decodeCalls utc cpuMs processContentions pinMs");
            foreach (var s in samples) Console.WriteLine(Format(count, s));
            foreach (var group in samples.GroupBy(s => s.Route))
            {
                var sorted = group.OrderBy(s => s.Ms).ToArray();
                double P(double p) => sorted[(int)Math.Ceiling(p * sorted.Length) - 1].Ms;
                Console.WriteLine(FormattableString.Invariant($"DIST {count} {group.Key} n={sorted.Length} p50={P(.5):F3} p95={P(.95):F3} p99={P(.99):F3} max={sorted[^1].Ms:F3} slow1mean={sorted.TakeLast((int)Math.Ceiling(sorted.Length * .01)).Average(s => s.Ms):F3}"));
                foreach (var s in sorted.TakeLast((int)Math.Ceiling(sorted.Length * .01))) Console.WriteLine("WORST " + Format(count, s));
            }
            Assert.All(samples, s => Assert.Equal(0, s.CanvasIo));
            Assert.All(samples, s => Assert.Equal(0, s.Images.DecodeCalls));
            Assert.Null(preparation.Statistics.Error);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string PixelHash(SKSurface surface)
    {
        using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bitmap.Bytes));
    }

    private static string Format(int count, Sample s) => string.Join(' ', new object[]
    {
        "FRAME", count, s.Route, s.Frame, s.Scale, s.X, s.Ms, s.Begin, s.Scene, s.End, s.Flush, s.Objects, s.Plan,
        s.Palette, s.Refinement, s.Exact, s.Ready, s.Loop, s.Cold, s.Seed, s.Prepare, s.Raster, s.Chunks,
        s.Adoptions, s.Pixels, s.ExactLayer, s.Direct, s.Tiles, s.Pending, s.Allocated, s.ProcessAllocated,
        s.Gen0, s.Gen1, s.Gen2, s.Pause, s.GateMisses, s.CanvasIo,
        s.ChunkMs, s.CollectMs, s.PrepareMs, s.DrawMs, s.Images.GateWaitMs, s.Images.LookupMs,
        s.Images.MipAdoptMs, s.Images.MipAdoptBytes, s.Images.DecodeCalls,
        s.StartUtc.ToString("O"), s.CpuMs, s.Contentions, s.Images.PinMs,
    }.Select(v => v is double d ? d.ToString("F4", CultureInfo.InvariantCulture) : v.ToString()));

    // Darwin's per-thread CPU clock; other hosts still run the probe and report NaN for this optional column.
    private static double ThreadCpuMilliseconds()
    {
        if (!OperatingSystem.IsMacOS()) return double.NaN;
        return clock_gettime(16, out var time) == 0 ? time.Seconds * 1000d + time.Nanoseconds / 1e6 : double.NaN;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Timespec { public long Seconds; public long Nanoseconds; }
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int clock_gettime(int clock, out Timespec time);
}
