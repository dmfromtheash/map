using System.Diagnostics;
using System.Security.Cryptography;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>Native-million-assets distribution, paced quarter-octave wheel through the .03 tile handoff.</summary>
[Collection(RealTimeSettleCollection.Name)]
public sealed class WheelStepTests(ITestOutputHelper output)
{
    private const int Width = 1280, Height = 720;

    [Fact]
    public void Fit_to_one_hundred_percent_and_back_has_bounded_frame_work_and_identical_settled_pixels()
    {
        Run(6_000, 8192, warmup: true);
        Run(250_000, 32768, warmup: false);
    }

    [Fact]
    public void A_pan_at_fixed_scale_still_prepares_new_detail_while_the_camera_moves()
    {
        using var disk = new DenseDetailTilesTests.MipDisk();
        var sha = new string('a', 64);
        var records = new Dictionary<string, DenseDetailTilesTests.AssetShape>
            { [sha] = new(sha, 400, 400, 0) };
        var objects = Enumerable.Range(0, 6000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Гора", i % 100 * 80, i / 100 * 120, 64, 64, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Pan", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects)]);
        var source = new DenseDetailTilesTests.BoundedDerivedSource(records, disk, 128L << 20);
        using var tiles = new ObjectOverviewCache();
        using var preparation = new SceneRenderer.OverviewPreparation();
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;

        void Draw(bool moving, double left)
        {
            var canvas = surface.Canvas;
            canvas.ResetMatrix(); canvas.Clear(SKColors.White);
            canvas.Scale(.1f); canvas.Translate((float)-left, 0);
            using var images = source.BeginFrame(16.7);
            tiles.BeginFrame();
            try
            {
                SceneRenderer.Render(canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = tiles, OverviewPreparation = preparation,
                    ViewScale = .1, LodTier = RenderLodTier.Overview, CameraMoving = moving, FastTerrain = moving,
                    VisibleWorld = new(left, 0, left + Width / .1, Height / .1), RequestRefinedFrame = static () => { },
                });
            }
            finally { tiles.EndFrame(); }
        }

        Draw(false, 0);
        var before = source.PlanPrepares;
        var wait = Stopwatch.StartNew();
        do { Draw(true, 4096); Thread.Sleep(5); }
        while (source.PlanPrepares == before && wait.Elapsed < TimeSpan.FromSeconds(10));
        Assert.True(source.PlanPrepares > before, "a fixed-scale pan deferred all new detail until the camera stopped");
        Assert.Null(preparation.Statistics.Error);
    }

    private void Run(int count, int Extent, bool warmup)
    {
        using var disk = new DenseDetailTilesTests.MipDisk();
        var random = new Random(13013);
        var records = Enumerable.Range(0, 1500).Select(i =>
        {
            var side = 400 + random.Next(1600);
            return new DenseDetailTilesTests.AssetShape(Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(i))), side,
                (int)(side * (.6 + random.NextDouble() * .8)), i % DenseDetailTilesTests.MipDisk.Fixtures);
        }).ToDictionary(r => r.Sha, StringComparer.OrdinalIgnoreCase);
        var assets = records.Values.ToArray();
        var objects = new SceneObject[count];
        for (var i = 0; i < objects.Length; i++)
        {
            var asset = assets[i % assets.Length];
            var width = 64 * (.5 + random.NextDouble() * 1.5);
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора " + i,
                random.NextDouble() * (Extent - width), random.NextDouble() * (Extent - width),
                width, width * asset.Height / asset.Width, random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new StampStyle(asset.Sha) };
        }
        var document = new MapDocument(DocumentId.New(), "Wheel step", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Extent, Extent, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects)],
            assets.Select(a => new MapAssetReference(a.Sha, a.Sha, "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: a.Sha)).ToArray());
        var moving = false;
        using var tiles = new ObjectOverviewCache(1024, 256L << 20);
        using var atlas = new ObjectOverviewCache();
        using var preparation = new SceneRenderer.OverviewPreparation(() => Volatile.Read(ref moving));
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
        var source = new DenseDetailTilesTests.BoundedDerivedSource(records, disk, 512L << 20);
        source.ReserveNavigationCapacity(records.Count * 16);
        preparation.UpdateDocument(document, source, static () => { });
        var times = new List<double>();
        var coldLevelWork = new List<double>();
        var frameNumber = 0;
        var lastInterval = 16.7;
        var lastWork = 0.0;
        RenderStats last = new();

        bool Frame(double scale, bool gesture, bool measure = true)
        {
            Volatile.Write(ref moving, gesture);
            var canvas = surface.Canvas;
            canvas.ResetMatrix(); canvas.Clear(SKColors.White);
            var halfW = Width / 2 / scale; var halfH = Height / 2 / scale;
            canvas.Scale((float)scale); canvas.Translate((float)-(Extent / 2 - halfW), (float)-(Extent / 2 - halfH));
            var stats = new RenderStats();
            var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
            var pause = GC.GetTotalPauseDuration();
            var started = Stopwatch.GetTimestamp();
            var begin = 0.0; var render = 0.0; var end = 0.0;
            using (source.BeginFrame(lastInterval))
            {
                tiles.BeginFrame(); atlas.BeginFrame();
                begin = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var rendering = Stopwatch.GetTimestamp();
                try
                {
                    SceneRenderer.Render(canvas, document, new RenderOptions
                    {
                        Stamps = source, ObjectCache = tiles, AtlasCache = atlas, Stats = stats, ViewScale = scale,
                        OverviewPreparation = preparation, CameraMoving = gesture, FastTerrain = gesture,
                        LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                        VisibleWorld = new(Extent / 2 - halfW, Extent / 2 - halfH, Extent / 2 + halfW, Extent / 2 + halfH),
                        RequestRefinedFrame = static () => { },
                    });
                    canvas.Flush();
                    render = Stopwatch.GetElapsedTime(rendering).TotalMilliseconds;
                }
                finally
                {
                    var ending = Stopwatch.GetTimestamp();
                    tiles.EndFrame(); atlas.EndFrame();
                    end = Stopwatch.GetElapsedTime(ending).TotalMilliseconds;
                }
            }
            var work = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lastWork = work;
            if (measure) times.Add(work);
            if (measure && gesture) coldLevelWork.Add(stats.DetailReadyMilliseconds + stats.DetailSeedMilliseconds
                + stats.DetailPrepareMilliseconds + stats.DetailRasterMilliseconds);
            if (measure && (work > 16.7 || frameNumber % 30 == 0))
                output.WriteLine($"frame={frameNumber} scale={scale:F6} moving={gesture} work={work:F2} begin={begin:F2} render={render:F2} end={end:F2} "
                    + $"overview plan={stats.OverviewPlanMilliseconds:F2} variant={stats.OverviewVariantMilliseconds:F2} raster={stats.OverviewRasterMilliseconds:F2} exact={stats.OverviewExactMilliseconds:F2} objects={stats.ExactObjects} "
                    + $"detail ready={stats.DetailReadyMilliseconds:F2} loop={stats.DetailLoopMilliseconds:F2} cold={stats.DetailColdMilliseconds:F2} seed={stats.DetailSeedMilliseconds:F2} built={stats.DetailTilesBuiltOnFrame} adopted={stats.DetailRastersAdopted} pending={stats.DetailTilesPending} "
                    + $"gc={GC.CollectionCount(0)-gc0}/{GC.CollectionCount(1)-gc1}/{GC.CollectionCount(2)-gc2} pause={(GC.GetTotalPauseDuration()-pause).TotalMilliseconds:F2}");
            frameNumber++;
            Assert.Null(preparation.Statistics.Error);
            last = stats;
            var remaining = 16.7 - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining > 1) Thread.Sleep((int)remaining);
            lastInterval = Math.Max(16.7, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && !source.ImageRequestsPending;
        }

        double Settle(double scale, bool measure = true)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(90))
                if (Frame(scale, false, measure)) return timer.Elapsed.TotalMilliseconds;
            Assert.Fail($"did not settle at {scale}: pending={last.DetailTilesPending}");
            return 0;
        }

        byte[] Pixels()
        {
            using var snapshot = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(snapshot);
            return bitmap.Bytes;
        }

        byte[] Reference(double zoom)
        {
            using var referenceTiles = new ObjectOverviewCache(1024, 256L << 20);
            using var referenceAtlas = new ObjectOverviewCache();
            using var target = SKSurface.Create(new SKImageInfo(Width, Height))!;
            var canvas = target.Canvas;
            canvas.Clear(SKColors.White);
            var halfW = Width / 2 / zoom; var halfH = Height / 2 / zoom;
            canvas.Scale((float)zoom); canvas.Translate((float)-(Extent / 2 - halfW), (float)-(Extent / 2 - halfH));
            SceneRenderer.Render(canvas, document, new RenderOptions
            {
                Stamps = new DenseDetailTilesTests.BoundedDerivedSource(records, disk, 512L << 20) { Synchronous = true },
                ObjectCache = referenceTiles, AtlasCache = referenceAtlas, ViewScale = zoom,
                LodTier = zoom < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                VisibleWorld = new(Extent / 2 - halfW, Extent / 2 - halfH, Extent / 2 + halfW, Extent / 2 + halfH),
            });
            using var snapshot = target.Snapshot();
            using var bitmap = SKBitmap.FromImage(snapshot);
            return bitmap.Bytes;
        }

        var fit = Height * .9 / Extent;
        Settle(fit, measure: false);
        var fitPixels = Pixels();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var scale = fit;
        while (scale < 1)
        {
            scale = Math.Min(1, scale * Math.Pow(2, .25));
            for (var f = 0; f < 6; f++) Frame(scale, true);
        }
        var inSharp = Settle(1);
        var detailPixels = Pixels();
        // Calibrate against the same CPU stamp evaluator, on the same settled 100% view.
        // This keeps a CPU-only guard usable on different machines without an OS-specific skip.
        var control = new List<double>();
        for (var f = 0; f < 5; f++) { Frame(1, false, measure: false); control.Add(lastWork); }
        control.Sort();
        var frameBound = Math.Max(50, control[control.Count / 2] * 1.5 + 5);
        while (scale > fit)
        {
            scale = Math.Max(fit, scale / Math.Pow(2, .25));
            for (var f = 0; f < 6; f++) Frame(scale, true);
        }
        var outSharp = Settle(fit);
        var finalFit = Pixels();
        // Same document/source, both endpoints fully settled; compare exact bytes, not a channel tolerance.
        Assert.Equal(fitPixels, finalFit);
        Settle(1, measure: false);
        Assert.Equal(detailPixels, Pixels());
        Assert.Equal(Reference(fit), finalFit);
        Assert.Equal(Reference(1), detailPixels);
        // Stop just across the overview/detail handoff as well: the stand-in must eventually
        // become the exact detail tiles, including their fractional pixel sampling and bleed.
        if (!warmup)
        {
            const double handoff = .033258;
            var sharp = Settle(handoff, measure: false);
            Assert.True(last.ObjectTilesDrawn > 0, "the handoff stop did not exercise detail tiles");
            Assert.Equal(Reference(handoff), Pixels());
            output.WriteLine($"handoff stop sharp={sharp:F1}ms tiles={last.ObjectTilesDrawn}, pixels equal synchronous reference");
        }
        output.WriteLine($"pixels fit={Convert.ToHexString(SHA256.HashData(finalFit))} detail={Convert.ToHexString(SHA256.HashData(detailPixels))}");
        var sorted = times.Order().ToArray();
        output.WriteLine($"WHEEL objects={objects.Length} frames={times.Count} max={sorted[^1]:F2} p95={sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]:F2} over16.7={times.Count(t => t > 16.7)} sharpIn={inSharp:F1} sharpOut={outSharp:F1} ms control={control[control.Count / 2]:F2} bound={frameBound:F2} coldLevelMax={coldLevelWork.Max():F2}");
        // 719 visible stamps at the first wheel-out notch versus 580 at 100%: allow 1.5x
        // the calibrated evaluator cost plus 5 ms for scheduling/caches. The 50 ms CPU floor
        // covers first-use software sampling (20-40 ms on the shared Mac), independently of
        // its cheaper warm control. GC pauses remain included; cold sections are printed too.
        // This is a CPU regression guard, not a 60 FPS/GPU acceptance test.
        if (!warmup)
        {
            Assert.True(sorted[^1] < frameBound, $"wheel frame work {sorted[^1]:F2} ms exceeds calibrated {frameBound:F2} ms");
        }
    }
}
