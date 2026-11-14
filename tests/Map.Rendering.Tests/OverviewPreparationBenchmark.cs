using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

// Explicit, headless probe. No images or document creation are timed, and only this filter runs it.
public sealed class OverviewPreparationBenchmark(ITestOutputHelper output)
{
    [Theory]
    // background=false is the synchronous reference path (plan and variants inside the frame).
    [InlineData(100_000, true)]
    [InlineData(1_000_000, true)]
    [InlineData(100_000, false)]
    [InlineData(1_000_000, false)]
    public void Cold_bucket_and_warm_frames(int count, bool background)
    {
        var mode = background ? "background" : "sync";
        const double scale = 1d / 128;
        var assets = Enumerable.Range(0, 1000).Select(i => new MapAssetReference(i.ToString("x64"),
            "geometry probe", "image/png", "png", ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "probe", "owner", catalogId: "geometry")).ToArray();
        var objects = Enumerable.Range(0, count).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "probe", (i % 1000) * 130, (i / 1000) * 130, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(assets[i % 1000].Sha256) }).ToArray();
        var source = new Colors();
        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024))!;
        var cold = new List<double>(); var switches = new List<double>(); var warm = new List<double>();
        var plan = new List<double>(); var palette = new List<double>(); var variants = new List<double>();
        var raster = new List<double>(); var draw = new List<double>(); var settling = new List<double>();
        var backgroundPlan = new List<double>(); var backgroundPalette = new List<double>();
        var backgroundVariant = new List<double>(); var backgroundRaster = new List<double>();
        var coldReady = new List<double>(); var bucketReady = new List<double>();
        var slowFrames = 0;
        var measuring = false;
        // Exclude JIT/Skia startup from the measured five independent cold layer revisions.
        using (var startup = new ObjectOverviewCache()) Frame(Document(objects.Take(10).ToArray()), startup, scale);
        measuring = true;
        for (var sample = 0; sample < 5; sample++)
        {
            var document = Document(objects);
            using var cache = new ObjectOverviewCache();
            using var prep = background ? new SceneRenderer.OverviewPreparation() : null;
            var first = Frame(document, cache, scale, prep);
            cold.Add(first.Ms);
            plan.Add(first.Stats.OverviewPlanMilliseconds); palette.Add(first.Stats.OverviewPaletteMilliseconds);
            variants.Add(first.Stats.OverviewVariantMilliseconds); raster.Add(first.Stats.OverviewRasterMilliseconds);
            draw.Add(first.Ms - plan[^1] - palette[^1] - variants[^1] - raster[^1]);
            coldReady.Add(Settle(document, cache, prep, scale, first.Stats));
            var change = Frame(document, cache, scale / Math.Sqrt(2), prep);
            switches.Add(change.Ms);
            bucketReady.Add(Settle(document, cache, prep, scale / Math.Sqrt(2), change.Stats));
            for (var frame = 0; frame < 40; frame++) warm.Add(Frame(document, cache, scale / Math.Sqrt(2), prep).Ms);
            if (prep is null) continue;
            var work = prep.Statistics;
            Assert.Null(work.Error);
            backgroundPlan.Add(work.PlanMs); backgroundPalette.Add(work.PaletteMs);
            backgroundVariant.Add(work.VariantMs); backgroundRaster.Add(work.RasterMs);
        }
        Print("cold", cold); Print("bucket", switches); Print("warm", warm);
        Print("plan", plan); Print("palette", palette); Print("variant", variants); Print("raster", raster); Print("draw+other", draw);
        Print("refinement frames", settling);
        Print("background plan total", backgroundPlan); Print("background palette total", backgroundPalette);
        Print("background variants total", backgroundVariant); Print("background raster total", backgroundRaster);
        Print("cold ready wall", coldReady); Print("bucket ready wall", bucketReady);

        MapDocument Document(IReadOnlyList<SceneObject> items) => new(DocumentId.New(), "overview prep probe",
            DateTimeOffset.UnixEpoch, new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, items)], assets);

        (double Ms, RenderStats Stats) Frame(MapDocument document, ObjectOverviewCache cache, double zoom,
            SceneRenderer.OverviewPreparation? prep = null)
        {
            surface.Canvas.ResetMatrix(); surface.Canvas.Clear(SKColors.White); surface.Canvas.Scale((float)zoom);
            var stats = new RenderStats();
            var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
            var started = Stopwatch.GetTimestamp();
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    ObjectCache = cache, OverviewPreparation = prep, Stamps = source, Stats = stats, ViewScale = zoom,
                    LodTier = RenderLodTier.Overview, VisibleWorld = new(0, 0, 131071, 131071),
                    RequestRefinedFrame = static () => { },
                });
                surface.Canvas.Flush();
            }
            finally { cache.EndFrame(); }
            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (measuring && milliseconds > 16.667 && slowFrames++ < 15)
                output.WriteLine($"SLOW {count} {mode}: {milliseconds:F3}ms plan/request={stats.OverviewPlanMilliseconds:F3} palette={stats.OverviewPaletteMilliseconds:F3} variants={stats.OverviewVariantMilliseconds:F3} raster={stats.OverviewRasterMilliseconds:F3} adopt={stats.OverviewRefinementMilliseconds:F3} chunks={stats.OverviewChunks} GC={GC.CollectionCount(0)-gen0}/{GC.CollectionCount(1)-gen1}/{GC.CollectionCount(2)-gen2}");
            return (milliseconds, stats);
        }

        double Settle(MapDocument document, ObjectOverviewCache cache, SceneRenderer.OverviewPreparation? prep, double zoom, RenderStats stats)
        {
            var timeout = Stopwatch.StartNew();
            while (stats.DetailTilesPending > 0)
            {
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(60), "overview did not settle");
                Thread.Sleep(8);
                var frame = Frame(document, cache, zoom, prep); stats = frame.Stats; settling.Add(frame.Ms);
                Assert.Null(prep?.Statistics.Error);
            }
            return timeout.Elapsed.TotalMilliseconds;
        }

        void Print(string name, List<double> values)
        {
            if (values.Count == 0) return;
            values.Sort();
            var tail = values.TakeLast(Math.Max(1, (int)Math.Ceiling(values.Count * .01))).Average();
            output.WriteLine($"{count} {mode} {name}: n={values.Count} median={values[values.Count / 2]:F3}ms p99={values[(int)Math.Ceiling(values.Count * .99) - 1]:F3}ms max={values[^1]:F3}ms 1%low={1000 / tail:F1}fps");
        }
    }

    private sealed class Colors : IStampImageSource
    {
        public long? OverviewColorRevision => 0;
        public SKImage? TryGetImage(string sha256) => null;
        public SKColor? TryGetOverviewColor(string sha256) => SKColors.ForestGreen;
    }
}
