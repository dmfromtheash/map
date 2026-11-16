using System.Diagnostics;
using System.Reflection;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection("Memory pressure")]
public sealed class EdgePanTests
{
    // test-change: A cold 100% pan must perform no plan checks, pin walks or tile rasters on the frame.
    // A work bound is deterministic on CI; wall times below are CPU-relative diagnostics, not PC FPS.
    [Theory]
    [InlineData(250_000)]
    [InlineData(1_000_000)]
    public async Task New_ground_prepares_off_frame_and_settles_to_exact_owner_pixels(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-edgepan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Map.slnx"))) repo = repo.Parent;
            var records = Enumerable.Range(1, 6).Select(i => catalog.Import(Path.Combine(repo!.FullName,
                "tests", "fixtures", "assets", $"owner-mountain-{i:00}.png"), new(), DateTimeOffset.UnixEpoch)).ToArray();
            var random = new Random(17);
            var objects = Enumerable.Range(0, count).Select(i => new SceneObject(
                new ObjectId(new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)), SceneObjectKind.Stamp,
                "Mountain", random.NextDouble() * 8100, random.NextDouble() * 8100,
                32 + random.NextDouble() * 64, 32 + random.NextDouble() * 64,
                i % 3 * 15, "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new StampStyle(records[i % records.Length].Sha256), ObjectOpacity = .7 }).ToArray();
            var layer = new ObjectLayer(LayerId.New(), "Mountains", true, false, .8, objects);
            var document = new MapDocument(DocumentId.New(), "Edge pan", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"), layer])
                .WithAssets(records.Select(r => new MapAssetReference(r.Sha256, r.Sha256, "image/png", "png",
                    ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: r.Sha256)).ToArray());
            await using var images = new CatalogStampImageSource(catalog, () => document, () => null,
                derivedRoot: Path.Combine(root, "derived"), startWatcher: _ => { });
            // The Mac cannot run the Windows cross-process preparation lock. Supply exact owner
            // mip pixels in memory: this isolates the cold tile/plan path, including Pin, from disk.
            var samples = (AssetImageCache)typeof(CatalogStampImageSource).GetField("_samples",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(images)!;
            var masters = (AssetImageCache)typeof(CatalogStampImageSource).GetField("_cache",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(images)!;
            var colors = new Dictionary<string, SKColor>();
            foreach (var record in records)
            {
                using var original = SKBitmap.Decode(catalog.SourcePath(record));
                using var colorImage = SKImage.FromBitmap(original);
                colors.Add(record.Sha256, SceneRenderer.StampOverviewColor(colorImage));
                for (var step = 0; step <= AssetCatalog.DetailLadderDepth(record.Width, record.Height); step++)
                for (var level = 0; level <= 9; level++)
                {
                    var width = Math.Max(1, record.Width >> (step + level));
                    var height = Math.Max(1, record.Height >> (step + level));
                    using var mip = original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
                    mip.SetImmutable();
                    var key = record.Sha256 + (step == 0 ? "" : "@" + step) + "#" + level;
                    Assert.NotNull((Math.Max(width, height) <= 256 ? samples : masters).AdoptPixels(key, SKImage.FromBitmap(mip)));
                }
            }
            var source = new ObservedSource(images, colors);
            using var overview = new SceneRenderer.OverviewPreparation();
            overview.UpdateDocument(document, source, () => { });
            using var cache = new ObjectOverviewCache();
            using var surface = SKSurface.Create(new SKImageInfo(1536, 864))!;
            var frames = new List<string>();
            var beginMs = 0d; var endMs = 0d;
            (RenderStats Stats, double Ms) Draw(double x, bool moving)
            {
                var stats = new RenderStats();
                surface.Canvas.ResetMatrix();
                surface.Canvas.Translate((float)-x, -2048);
                if (moving) Map.Core.Resources.BackgroundWork.NoteInteraction();
                images.FrameMoving = moving;
                var started = Stopwatch.GetTimestamp();
                var scope = source.BeginFrame();
                beginMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                try
                {
                    cache.BeginFrame();
                    try
                    {
                        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                        {
                            Stamps = source, ObjectCache = cache, Stats = stats, ViewScale = 1,
                            LodTier = RenderLodTier.Overview, VisibleWorld = new(x, 2048, x + 1535, 2911),
                            FastTerrain = moving, CameraMoving = moving, OverviewPreparation = overview,
                            RequestRefinedFrame = () => { },
                        });
                    }
                    finally { cache.EndFrame(); }
                }
                finally { var ending = Stopwatch.GetTimestamp(); scope?.Dispose(); endMs = Stopwatch.GetElapsedTime(ending).TotalMilliseconds; }
                return (stats, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            double Settle(double x)
            {
                var clock = Stopwatch.StartNew();
                var last = "";
                while (clock.Elapsed.TotalSeconds < 45)
                {
                    var (s, ms) = Draw(x, false);
                    last = $"x={x} ms={ms:F1} pending={s.DetailTilesPending} unresolved={s.UnresolvedImages} requests={source.ImageRequestsPending} "
                        + $"started={s.DetailRastersStarted} adopted={s.DetailRastersAdopted} onFrame={s.DetailTilesBuiltOnFrame} "
                        + $"skip={s.DenseSkip} drawn={s.ObjectTilesDrawn} waiting={s.DetailRastersWaiting} cache={cache.Count} calls={source.FramePlanCalls} "
                        + $"plansReady={source.PlanReadies} pins={source.Pins} created={source.Created} added={source.Added} rev={source.SourceRevision} overview={overview.Statistics}";
                    var queuesEmpty = images.TryReadPerformanceQueues(out var queues) && queues == default;
                    if (s.DetailTilesPending == 0 && s.UnresolvedImages == 0 && !source.ImageRequestsPending && queuesEmpty)
                        return clock.Elapsed.TotalMilliseconds;
                    Thread.Sleep(5);
                }
                Assert.Fail("The view did not become sharp within 45 seconds: " + last);
                return 0;
            }
            Settle(0);
            source.FramePlanCalls = 0;
            var maximum = 0d; var builds = 0; var maxAdopted = 0; var frameTileMs = 0d; var maxPlanCalls = 0;
            for (var frame = 1; frame <= 52; frame++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var pause = GC.GetTotalPauseDuration();
                var planCalls = source.FramePlanCalls;
                var (s, ms) = Draw(frame * 128, true);
                maximum = Math.Max(maximum, ms); builds += s.DetailTilesBuiltOnFrame;
                maxAdopted = Math.Max(maxAdopted, s.DetailRastersAdopted);
                frameTileMs = Math.Max(frameTileMs, s.DetailPrepareMilliseconds + s.DetailRasterMilliseconds);
                var framePlans = source.FramePlanCalls - planCalls;
                maxPlanCalls = Math.Max(maxPlanCalls, framePlans);
                frames.Add($"edgepan objects={count} frame={frame} ms={ms:F3} ready={s.DetailReadyMilliseconds:F3} "
                    + $"loop={s.DetailLoopMilliseconds:F3} prepare={s.DetailPrepareMilliseconds:F3} raster={s.DetailRasterMilliseconds:F3} "
                    + $"overview={s.OverviewPlanMilliseconds + s.OverviewPaletteMilliseconds + s.OverviewVariantMilliseconds + s.OverviewRasterMilliseconds:F3} "
                    + $"built={s.DetailTilesBuiltOnFrame} adopted={s.DetailRastersAdopted} pending={s.DetailTilesPending} "
                    + $"begin={beginMs:F3} end={endMs:F3} objectsMs={s.ObjectsMilliseconds:F3} cold={s.DetailColdMilliseconds:F3} seed={s.DetailSeedMilliseconds:F3} "
                    + $"framePlanCalls={framePlans} "
                    + $"eligibility={Diagnostic(s, "DetailEligibilityMilliseconds"):F3} prelude={Diagnostic(s, "DetailPreludeMilliseconds"):F3} "
                    + $"queueDeferred={Diagnostic(s, "DetailQueueDeferrals")} jobBusy={Diagnostic(s, "DetailJobLocksBusy")} "
                    + $"allocated={GC.GetAllocatedBytesForCurrentThread() - allocated} gc={(GC.GetTotalPauseDuration() - pause).TotalMilliseconds:F3}");
                Thread.Sleep((int)Math.Max(1, 16 - ms));
            }
            var sharp = Settle(6656);
            using var actualImage = surface.Snapshot();
            using var actual = SKBitmap.FromImage(actualImage);
            // Same exact tiled evaluator, with no progressive work or overview fallback.
            using var referenceCache = new ObjectOverviewCache();
            surface.Canvas.ResetMatrix(); surface.Canvas.Translate(-6656, -2048);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                Stamps = source, ObjectCache = referenceCache, ViewScale = 1,
                VisibleWorld = new(6656, 2048, 8191, 2911),
            });
            using var expectedImage = surface.Snapshot();
            using var expected = SKBitmap.FromImage(expectedImage);
            var equal = actual.Bytes.AsSpan().SequenceEqual(expected.Bytes);
            var summary = $"EDGE objects={count} panMaxMs={maximum:F3} framePlanCalls={source.FramePlanCalls} "
                + $"maxFramePlanCalls={maxPlanCalls} onFrameTiles={builds} maxAdopted={maxAdopted} stopToSharpMs={sharp:F3} equal={equal} queuesEmpty=True "
                + $"sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(actual.Bytes))}";
            if (Environment.GetEnvironmentVariable("MAP_EDGEPAN_LOG") is { } log)
                File.AppendAllLines(log, frames.Append(summary));
            Assert.True(equal, summary);
            Assert.Equal(0, builds);
            Assert.Equal(0, frameTileMs);
            Assert.InRange(maxAdopted, 0, 1);
            Assert.True(source.FramePlanCalls == 0, summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            while (Map.Core.Resources.BackgroundWork.InteractionActive) Thread.Sleep(5);
        }
    }

    // The same harness also runs against the baseline, which predates these phase counters.
    private static double Diagnostic(RenderStats stats, string name) =>
        Convert.ToDouble(typeof(RenderStats).GetProperty(name)?.GetValue(stats) ?? 0);

    private sealed class ObservedSource(CatalogStampImageSource source, IReadOnlyDictionary<string, SKColor> colors) : IStampImageSource
    {
        private int _frameThread;
        public int FramePlanCalls, PlanReadies, Pins, Created, Added;
        public long SourceRevision => source.SourceRevision;
        public long MissingRevision => source.MissingRevision;
        public long? OverviewColorRevision => 0;
        public bool ImageRequestsPending => source.ImageRequestsPending;
        public bool LastImageRequestPending => source.LastImageRequestPending;
        public long UnresolvedRequestVersion => source.UnresolvedRequestVersion;
        public bool LastImageRequestCacheableMissing => source.LastImageRequestCacheableMissing;
        public bool IsMissingCacheable(string sha256) => source.IsMissingCacheable(sha256);
        public long PreparationBytes => source.PreparationBytes;
        public SKImage? TryGetImage(string sha256) => source.TryGetImage(sha256);
        public SKImage? TryGetImageForOcclusion(string sha256) => source.TryGetImageForOcclusion(sha256);
        public SKImage? TryGetImageForPixelSize(string sha256, double w, double h) => source.TryGetImageForPixelSize(sha256, w, h);
        public SKImage? TryGetReadyImageForPixelSize(string sha256, double w, double h) => source.TryGetReadyImageForPixelSize(sha256, w, h);
        public (int Width, int Height)? TryGetImageSize(string sha256) => source.TryGetImageSize(sha256);
        public SKColor? TryGetOverviewColor(string sha256) => colors.GetValueOrDefault(sha256);
        public bool PrepareOverview(IEnumerable<string> assets, Action ready) => true;
        public IStampImageLease Lease() => new ColorsLease(source.Lease(), colors);
        public IStampImageLease ExactLease(CancellationToken cancellation = default) => source.ExactLease(cancellation);
        public IDisposable? BeginFrame() { _frameThread = Environment.CurrentManagedThreadId; return source.BeginFrame(); }
        public IStampPreparationPlan? CreatePreparationPlan() { Created++; return source.CreatePreparationPlan() is { } plan ? new Plan(this, plan) : null; }
        private sealed class ColorsLease(IStampImageLease images, IReadOnlyDictionary<string, SKColor> colors) : IStampImageLease
        {
            public SKImage? TryGetImage(string sha256) => images.TryGetImage(sha256);
            public SKColor? TryGetOverviewColor(string sha256) => colors.GetValueOrDefault(sha256);
            public void Dispose() => images.Dispose();
        }
        private sealed class Plan(ObservedSource owner, IStampPreparationPlan plan) : IStampPreparationPlan
        {
            // Extra members compile against the baseline too; only the new renderer consumes them.
            public bool BackgroundPreparation => true;
            public StampImageRequestPriority? Priority { set => plan.Priority = value; }
            public IDisposable? KeepRequests() => plan.KeepRequests();
            public long Bytes => plan.Bytes;
            public long SourceRevision => plan.SourceRevision;
            public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps) { Interlocked.Increment(ref owner.Added); plan.Add(stamps); }
            public bool Prepare(Action ready) { Observe(); var result = plan.Prepare(ready); if (result) Interlocked.Increment(ref owner.PlanReadies); return result; }
            public IStampImageLease? Pin() { Observe(); var result = plan.Pin(); if (result is not null) Interlocked.Increment(ref owner.Pins); return result; }
            private void Observe() { if (Environment.CurrentManagedThreadId == owner._frameThread) owner.FramePlanCalls++; }
        }
    }
}
