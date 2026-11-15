using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class FrameBudgetRenderingTests
{
    [Fact]
    public void Dense_tiles_with_shared_budget_settle_to_the_same_pixels()
    {
        var result = DenseDetailTilesTests.Run(24_000, 300, 4096, 120, 128L << 20, budget: new FrameBudget());
        Assert.True(result.Settled, result.ToString());
        Assert.True(result.PixelsEqual, result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // test-change: late frames defer; refinement resumes when a subsequent frame has time.
    public void Overview_build_and_adoption_resume_after_late_frames_without_changing_pixels(bool background)
    {
        using var source = new OverviewPreparationTests.Mountains();
        var document = OverviewPreparationTests.Document(Enumerable.Range(0, 180)
            .Select(i => OverviewPreparationTests.Stamp(i, painted: true)).ToArray());
        using var old = new ObjectOverviewCache();
        var reference = Draw(old, null, null).Pixels;
        using var cache = new ObjectOverviewCache();
        using var preparation = background ? new SceneRenderer.OverviewPreparation() : null;
        preparation?.UpdateDocument(document, source, source.Ready);
        var budget = new FrameBudget();
        var timer = Stopwatch.StartNew();
        var frame = 0;
        while (true)
        {
            budget.Begin(16.7, frame++ < 3 ? 30 : 0);
            var result = Draw(cache, preparation, budget);
            if (frame <= 3)
            {
                Assert.Equal(0, budget.SpentMilliseconds(FrameWorkKind.Overview));
                Assert.True(result.Stats.DetailTilesPending > 0);
            }
            Assert.True(timer.Elapsed.TotalSeconds < 30, "Overview refinement stalled in late frames.");
            if (result.Stats.DetailTilesPending == 0)
            {
                Assert.Equal(reference, result.Pixels);
                break;
            }
            Thread.Sleep(2);
        }

        (byte[] Pixels, RenderStats Stats) Draw(ObjectOverviewCache tiles, SceneRenderer.OverviewPreparation? prep, FrameBudget? frameBudget)
        {
            using var surface = SKSurface.Create(new SKImageInfo(512, 512))!;
            surface.Canvas.Scale(1f / 64);
            var stats = new RenderStats();
            tiles.BeginFrame(frameBudget);
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = tiles, Stats = stats, FrameBudget = frameBudget,
                    OverviewPreparation = prep, ViewScale = 1d / 64, LodTier = RenderLodTier.Overview,
                    VisibleWorld = new(0, 0, 32768, 32768), RequestRefinedFrame = frameBudget is null ? null : source.Ready,
                });
            }
            finally { tiles.EndFrame(); }
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            return (bitmap.Bytes, stats);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Hex_chunks_resume_after_late_frames_and_settle_to_the_old_pixels(bool background, bool ghost)
    {
        var document = HexLayerRenderingTests.Sample(HexOrientation.Pointy, 40, 32);
        const double scale = .12;
        using var old = new HexChunkCache();
        using var shared = new HexChunkCache { BuildInBackground = background };
        var budget = new FrameBudget();
        var reference = Settle(old, null);
        Assert.Equal(reference, Settle(shared, budget));

        byte[] Settle(HexChunkCache cache, FrameBudget? frameBudget)
        {
            using var surface = SKSurface.Create(new SKImageInfo(700, 500))!;
            var stats = new RenderStats();
            var timer = Stopwatch.StartNew();
            var frame = 0;
            do
            {
                frameBudget?.Begin(16.7, frame++ < 3 ? 30 : 0);
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Save();
                surface.Canvas.Scale((float)scale);
                var requested = false;
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    HexChunks = cache, ViewScale = scale, Stats = stats, FrameBudget = frameBudget,
                    HexLevel = ghost ? 1 : null, HexGhostLevel = ghost ? 0 : null,
                    RequestRefinedFrame = () => requested = true,
                });
                surface.Canvas.Restore();
                if (frameBudget is not null && frame <= 3)
                {
                    Assert.Equal(0, frameBudget.SpentMilliseconds(FrameWorkKind.Hex));
                    Assert.True(requested);
                }
                Assert.True(timer.Elapsed.TotalSeconds < 30, $"Hex refinement stalled; pending={stats.HexChunksPending}");
                if (!requested && !cache.Animating(cache.AnimationNow)) break;
                Thread.Sleep(2);
            } while (true);
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            return bitmap.Bytes;
        }
    }

    [OptInProbe("MAP_FRAME_BUDGET_BENCH_LOG", "FrameBudgetRenderingTests.Dense_tiles_with_shared_budget_settle_to_the_same_pixels")]
    [Trait("Category", "Benchmark")]
    public void Benchmark_sum_of_dense_refinement_clients()
    {
        var log = Environment.GetEnvironmentVariable("MAP_FRAME_BUDGET_BENCH_LOG");
        Assert.False(string.IsNullOrEmpty(log));
        // Same deterministic dense view and owner fixtures as DenseDetailTilesTests. The source
        // models bounded mip completion; workers are not charged to the frame-thread CPU sum.
        // Warm both paths before collecting cold-view refinement frames (including frame zero).
        Run(null);
        Run(new FrameBudget());
        // A-B-B-A reduces sensitivity to compilation, fixture cache warmth and drift on the shared Mac.
        var before = Run(null);
        var after = Run(new FrameBudget());
        after.AddRange(Run(new FrameBudget()));
        before.AddRange(Run(null));
        Write("before-null", before);
        Write("after-shared", after);

        List<DenseDetailTilesTests.RefinementSample> Run(FrameBudget? budget)
        {
            var samples = new List<DenseDetailTilesTests.RefinementSample>();
            var result = DenseDetailTilesTests.Run(60_000, 600, 4096, 180, 128L << 20,
                budget: budget, refinementSample: samples.Add);
            Assert.True(result.Settled, result.ToString());
            Assert.True(result.PixelsEqual, result.ToString());
            return samples;
        }

        void Write(string name, List<DenseDetailTilesTests.RefinementSample> samples)
        {
            samples.Sort((a, b) => a.Total.CompareTo(b.Total));
            var p50 = samples[(int)Math.Ceiling(samples.Count * .5) - 1].Total;
            var p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1].Total;
            var worst = samples[^1];
            File.AppendAllText(log,
                FormattableString.Invariant($"{name}: order=A-B-B-A objects=60000 assets=600 frames={samples.Count} refinement-sum p50={p50:F3} p95={p95:F3} max={worst.Total:F3} ms pixelsEqual=True worst[mips={worst.Mips:F3} tiles={worst.Tiles:F3} ready={worst.Ready:F3} overview={worst.Overview:F3} trims={worst.Trims:F3}]\n"));
        }
    }
}
