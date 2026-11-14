using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// Headless repro of the laptop regression: a pan at fit right after open, a pause, then wheel
/// notches in, all while the background worker prepares the plan, chunks and neighbour buckets.
/// The gesture is driven by this test (its own "moving" flag stands for the canvas clock), the
/// frames are paced to about 60 a second, and the CPU frame path is measured. Before = the
/// gesture budget off (settled adoption budget and prefetch during gestures), after = defaults.
/// Without MAP_GESTURE_BENCH it runs a small map and only asserts the bounds; with it (e.g.
/// MAP_GESTURE_BENCH=1000000,2000000) it prints the before/after numbers for those sizes.
/// </summary>
public sealed class OverviewPreparationGestureBenchmark(ITestOutputHelper output)
{
    private const int Width = 1280, Height = 720;
    private const double Extent = 131072;

    [Fact]
    public void Gesture_frames_adopt_a_bounded_number_of_pixels_and_prefetch_waits_for_the_settled_view()
    {
        var sizes = Environment.GetEnvironmentVariable("MAP_GESTURE_BENCH") is { Length: > 0 } list
            ? list.Split(',').Select(int.Parse).ToArray() : [60_000];
        output.WriteLine($"reference overview pixels sha256={ReferenceHash()}");
        if (sizes[0] > 100_000) Run(20_000, budget: true); // JIT and Skia start-up outside the measured runs
        foreach (var count in sizes)
        {
            var before = Run(count, budget: false);
            var after = Run(count, budget: true);
            Assert.True(after.Settled, "the gesture-budget run did not settle");
            Assert.True(before.Settled, "the reference run did not settle");
            // Per moving frame: the 1 Mpx gesture budget plus at most the one picture that crossed it
            // (a chunk raster is capped at 512 screen pixels plus its painted margin), and 8 pictures.
            Assert.All(after.GestureAdoptions, frame => Assert.True(
                frame.Count <= 8 && frame.Pixels <= 2L * 1024 * 1024,
                $"a gesture frame adopted {frame.Count} rasters, {frame.Pixels} px"));
            Assert.Equal(0, after.PrefetchDuringGesture);
        }
    }

    private sealed record Result(bool Settled, List<(int Count, long Pixels)> GestureAdoptions, long PrefetchDuringGesture);

    private Result Run(int count, bool budget)
    {
        var mode = budget ? "after" : "before";
        var assets = Enumerable.Range(0, 1000).Select(i => new MapAssetReference(i.ToString("x64"),
            "geometry probe", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "probe", "owner", catalogId: "geometry")).ToArray();
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        var step = (Extent - 1024) / side;
        var objects = new SceneObject[count];
        for (var i = 0; i < count; i++)
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора " + (i + 1),
                512 + (i % side) * step, 512 + (i / side) * step, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(assets[i % 1000].Sha256) };
        var layer = new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects);
        var document = new MapDocument(DocumentId.New(), "gesture probe", DateTimeOffset.UnixEpoch,
            new CanvasDefinition((int)Extent, (int)Extent, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"), layer], assets);

        // The derived lookups of open, measured on their own (allocation per job).
        if (Environment.GetEnvironmentVariable("MAP_GESTURE_BENCH") is { Length: > 0 })
        {
            var lookups = new SceneObject[count];
            Array.Copy(objects, lookups, count);
            var lookupDocument = document with { Layers = [document.Layers[0], layer with { Objects = lookups }] };
            var lookupLayer = (ObjectLayer)lookupDocument.Layers[1];
            string Measure(Action action)
            {
                var a = GC.GetAllocatedBytesForCurrentThread(); var t = Stopwatch.GetTimestamp();
                action();
                return $"{(GC.GetAllocatedBytesForCurrentThread() - a) / 1048576.0:F1}MB/{Stopwatch.GetElapsedTime(t).TotalMilliseconds:F0}ms";
            }
            var revision = Measure(() => DocumentRevisionIndex.For(lookupDocument));
            var spatial = Measure(() => ObjectSpatialIndex.For(lookupLayer));
            var rest = Measure(() => DocumentDerivedIndexes.Prepare(lookupDocument));
            output.WriteLine($"{count} {mode} lookups: revision+slots={revision} spatial={spatial} bound+names={rest}");
        }

        var moving = false;
        using var prep = new SceneRenderer.OverviewPreparation(() => Volatile.Read(ref moving)) { YieldDuringGesture = budget };
        using var cache = new ObjectOverviewCache { GestureAdoptedPixelsPerFrame = budget ? 1L * 1024 * 1024 : 0 };
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
        var source = new Colors();
        var gestureWork = new List<double>(); var settleWork = new List<double>(); var intervals = new List<double>();
        var adoptions = new List<(int Count, long Pixels)>();
        long prefetchDuringGesture = 0, deferred = 0;
        var gen0 = 0; var gen1 = 0; var gen2 = 0; var pause = TimeSpan.Zero;
        var last = Stopwatch.GetTimestamp();
        var slow = 0; var frames = 0;

        RenderStats Frame(double cx, double cy, double scale, bool camera)
        {
            Volatile.Write(ref moving, camera);
            var canvas = surface.Canvas;
            canvas.ResetMatrix(); canvas.Clear(SKColors.White);
            var halfW = Width / 2 / scale; var halfH = Height / 2 / scale;
            canvas.Scale((float)scale); canvas.Translate((float)-(cx - halfW), (float)-(cy - halfH));
            var stats = new RenderStats();
            var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
            var paused = GC.GetTotalPauseDuration();
            var prefetched = prep.Statistics.Prefetched;
            var started = Stopwatch.GetTimestamp();
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(canvas, document, new RenderOptions
                {
                    ObjectCache = cache, OverviewPreparation = prep, Stamps = source, Stats = stats, ViewScale = scale,
                    LodTier = RenderLodTier.Overview, CameraMoving = camera,
                    VisibleWorld = new(cx - halfW, cy - halfH, cx + halfW, cy + halfH),
                    RequestRefinedFrame = static () => { },
                });
                canvas.Flush();
            }
            finally { cache.EndFrame(); }
            var work = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            // Pace to about 60 frames a second; the interval is what a 1 % low is made of.
            // (Sleep(1) follows the 15.6 ms system tick here, so the rest of the interval yields instead.)
            while (Stopwatch.GetElapsedTime(last).TotalMilliseconds < 16.6) Thread.Sleep(0);
            var now = Stopwatch.GetTimestamp();
            if (work > 50 && slow++ < 12)
                output.WriteLine($"SLOW {count} {mode} frame {frames} moving={camera}: {work:F1}ms plan={stats.OverviewPlanMilliseconds:F1} adopt={stats.OverviewRefinementMilliseconds:F1} chunks={stats.OverviewChunks} adopted={stats.OverviewAdoptions} GC={GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2} pause={(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:F1}ms");
            frames++;
            if (camera)
            {
                gestureWork.Add(work); intervals.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
                adoptions.Add((stats.OverviewAdoptions, stats.OverviewAdoptedPixels));
                deferred += stats.OverviewAdoptionsDeferred;
                prefetchDuringGesture += prep.Statistics.Prefetched - prefetched;
                gen0 += GC.CollectionCount(0) - g0; gen1 += GC.CollectionCount(1) - g1; gen2 += GC.CollectionCount(2) - g2;
                pause += GC.GetTotalPauseDuration() - paused;
            }
            else settleWork.Add(work);
            last = now;
            Assert.Null(prep.Statistics.Error);
            return stats;
        }

        bool Settle(double cx, double cy, double scale)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(120))
                if (Frame(cx, cy, scale, false).DetailTilesPending == 0) return true;
            return false;
        }

        // Open, then pan at fit straight away (the plan is still being built).
        prep.UpdateDocument(document, source, static () => { });
        var fit = Width / Extent;
        var centre = Extent / 2;
        for (var frame = 0; frame < 180; frame++)
            Frame(centre + Math.Sin(frame / 30.0) * Extent * .2, centre, fit, true);
        var settled = Settle(centre, centre, fit);
        // Wheel notches in at the centre: one notch (a quarter of an octave) every six frames.
        var scale = fit;
        for (var notch = 0; notch < 20; notch++)
        {
            scale *= Math.Pow(2, .25);
            for (var frame = 0; frame < 6; frame++) Frame(centre, centre, scale, true);
        }
        settled &= Settle(centre, centre, scale);
        // Pan at the zoomed-in view (larger chunk rasters), then the final settle.
        for (var frame = 0; frame < 120; frame++)
            Frame(centre + frame * 4 / scale, centre, scale, true);
        settled &= Settle(centre + 480 / scale, centre, scale);

        var gesture = prep.GestureStatistics;
        var bytes = prep.AllocatedBytes;
        Print($"{count} {mode} gesture work", gestureWork);
        Print($"{count} {mode} gesture interval", intervals);
        Print($"{count} {mode} settle work", settleWork);
        var pixels = adoptions.Select(a => (double)a.Pixels).ToList();
        pixels.Sort();
        output.WriteLine($"{count} {mode} adopted px per gesture frame: max={pixels[^1]:F0} p99={pixels[(int)Math.Ceiling(pixels.Count * .99) - 1]:F0} total={pixels.Sum():F0} maxRasters={adoptions.Max(a => a.Count)} deferredChunks={deferred}");
        output.WriteLine($"{count} {mode} background during gesture: started={gesture.StartedDuringGesture} prefetchDeferred={gesture.PrefetchDeferred} planWaits={gesture.PlanWaits} prefetchDone={prefetchDuringGesture}");
        output.WriteLine($"{count} {mode} GC during gesture frames: gen0={gen0} gen1={gen1} gen2={gen2} pause={pause.TotalMilliseconds:F1}ms");
        output.WriteLine($"{count} {mode} worker MB: plan={bytes.Plan / 1048576.0:F1} palette={bytes.Palette / 1048576.0:F1} variants={bytes.Variants / 1048576.0:F1} rasters={bytes.Rasters / 1048576.0:F1} prefetch={bytes.Prefetch / 1048576.0:F1}");
        return new(settled, adoptions, prefetchDuringGesture);
    }

    /// <summary>The synchronous overview of a fixed mixed-colour map: the same before and after any change of the dot builder.</summary>
    private static string ReferenceHash()
    {
        var assets = Enumerable.Range(0, 1000).Select(i => new MapAssetReference(i.ToString("x64"),
            "geometry probe", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "probe", "owner", catalogId: "geometry")).ToArray();
        var objects = Enumerable.Range(0, 40_000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "probe", 300 + (i % 200) * 650 + (i * 7919 % 97), 300 + (i / 200) * 650, 16 + i % 5, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(assets[i * 31 % 1000].Sha256) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "reference", DateTimeOffset.UnixEpoch,
            new CanvasDefinition((int)Extent, (int)Extent, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects)], assets);
        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        using var cache = new ObjectOverviewCache();
        var digest = new List<string>();
        foreach (var scale in new[] { 1024 / Extent, 4096 / Extent })
        {
            surface.Canvas.ResetMatrix(); surface.Canvas.Clear(SKColors.White); surface.Canvas.Scale((float)scale);
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    ObjectCache = cache, Stamps = new Colors(), ViewScale = scale, LodTier = RenderLodTier.Overview,
                    VisibleWorld = new(0, 0, 1024 / scale, 1024 / scale),
                });
            }
            finally { cache.EndFrame(); }
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            digest.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bitmap.Bytes))[..16]);
        }
        return string.Join("/", digest);
    }

    private void Print(string name, List<double> values)
    {
        if (values.Count == 0) return;
        var sorted = values.Order().ToList();
        var tail = sorted.TakeLast(Math.Max(1, (int)Math.Ceiling(sorted.Count * .01))).Average();
        output.WriteLine($"{name}: n={sorted.Count} median={sorted[sorted.Count / 2]:F2}ms p99={sorted[(int)Math.Ceiling(sorted.Count * .99) - 1]:F2}ms max={sorted[^1]:F2}ms 1%low={1000 / tail:F1}fps");
    }

    private sealed class Colors : IStampImageSource
    {
        public long? OverviewColorRevision => 0;
        public SKImage? TryGetImage(string sha256) => null;
        // A colour per asset, as with the owner's mixed library: neighbouring dots rarely share a run.
        public SKColor? TryGetOverviewColor(string sha256)
        {
            var i = int.Parse(sha256.AsSpan(56), System.Globalization.NumberStyles.HexNumber);
            return new SKColor((byte)(40 + i * 37 % 180), (byte)(60 + i * 53 % 160), (byte)(30 + i * 91 % 150));
        }
    }
}
