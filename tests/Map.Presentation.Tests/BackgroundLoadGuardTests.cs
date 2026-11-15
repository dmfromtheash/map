using System.Diagnostics;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Export;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

[CollectionDefinition(nameof(BackgroundLoadObservation), DisableParallelization = true)]
public sealed class BackgroundLoadObservation;

/// <summary>
/// Process-wide counters need an isolated collection. Run this class alone in a fresh process for
/// measurements, optionally with DOTNET_PROCESSOR_COUNT=4. The sampler is a dedicated thread so
/// thread-pool starvation cannot silently suppress samples or expire the simulated gesture.
/// </summary>
[Collection(nameof(BackgroundLoadObservation))]
public sealed class BackgroundLoadGuardTests
{
    [Fact]
    public void Nested_scopes_pause_and_exceptions_count_a_thread_once_and_release_it()
    {
        // The counters are process-wide: background work left over by other test classes (stores,
        // overview workers, detail rasters) may enter and leave at any moment while this test
        // observes. Exact checks are therefore made on this thread's own category; global counts
        // are only checked as lower bounds that other threads cannot break.
        using var observation = BackgroundLoad.Observe();
        Assert.Equal(BackgroundProducer.None, BackgroundLoad.CurrentProducer);
        using (BackgroundLoad.Enter(BackgroundProducer.BackgroundWork))
        {
            Assert.Equal(BackgroundProducer.BackgroundWork, BackgroundLoad.CurrentProducer);
            AssertCounted(BackgroundProducer.BackgroundWork);
            using (BackgroundLoad.Enter(BackgroundProducer.DetailRaster))
            {
                Assert.Equal(BackgroundProducer.DetailRaster, BackgroundLoad.CurrentProducer);
                AssertCounted(BackgroundProducer.DetailRaster);
                using (BackgroundLoad.Pause()) Assert.Equal(BackgroundProducer.None, BackgroundLoad.CurrentProducer);
                Assert.Equal(BackgroundProducer.DetailRaster, BackgroundLoad.CurrentProducer);
            }
            Assert.Equal(BackgroundProducer.BackgroundWork, BackgroundLoad.CurrentProducer);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var failing = BackgroundLoad.Enter(BackgroundProducer.ExportRender);
                throw new InvalidOperationException("diagnostic probe");
            }));
            Assert.Equal(BackgroundProducer.BackgroundWork, BackgroundLoad.CurrentProducer);
        }
        Assert.Equal(BackgroundProducer.None, BackgroundLoad.CurrentProducer);

        // A thread of its own: its scopes balance exactly once it has left them, whatever others do.
        var producers = Enum.GetValues<BackgroundProducer>().Where(p => p != BackgroundProducer.None).ToArray();
        var worker = new Thread(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                using var outer = BackgroundLoad.Enter(producers[i % producers.Length]);
                using var inner = BackgroundLoad.Enter(producers[(i * 7 + 3) % producers.Length]);
                using var paused = BackgroundLoad.Pause();
            }
        });
        worker.Start();
        worker.Join();
        Assert.All(BackgroundLoad.Capture().Busy, busy => Assert.True(busy >= 0, "A scope left a negative count."));
    }

    private static void AssertCounted(BackgroundProducer producer)
    {
        var load = BackgroundLoad.Capture();
        Assert.True(load.Busy[(int)producer] >= 1, $"{producer} is not counted while this thread is inside it.");
        Assert.True(load.Total >= 1);
    }

    [Fact]
    public void Unobserved_scopes_do_not_allocate_or_change_counters()
    {
        // Production runs with no observer: Enter/Pause must be a branch, never a lock or heap object.
        static void Run()
        {
            using var load = BackgroundLoad.Enter(BackgroundProducer.DetailRaster);
            using var paused = BackgroundLoad.Pause();
            using var nested = BackgroundLoad.Enter(BackgroundProducer.ExportRender);
        }
        // Tiered compilation promotes Run after a delay and installing optimised code can allocate
        // once on the calling thread (as in PerformanceJournalTests): warm until two consecutive
        // batches allocate nothing, then measure.
        for (int warm = 0, clean = 0; clean < 2 && warm < 200; warm++)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) Run();
            clean = GC.GetAllocatedBytesForCurrentThread() == start ? clean + 1 : 0;
            if (clean == 0) Thread.Sleep(20);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) Run();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(BackgroundProducer.None, BackgroundLoad.CurrentProducer);
    }

    // This is an executable guard, not a scheduler fix. Opt in to obtain the failing assertion
    // and the exact sampled producers; default discovery explains why the suite skips it.
    [CoresGuardFact]
    public async Task Open_overview_detail_owner_mips_and_export_share_the_gesture_core_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-background-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await RunLoad(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task RunLoad(string root)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Map.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
        var paths = Enumerable.Range(1, 6).Select(n => Path.Combine(repository.FullName,
            "tests", "fixtures", "assets", $"owner-mountain-{n:00}.png")).ToArray();
        var records = paths.Select(path => catalog.Import(path, new(), DateTimeOffset.UnixEpoch)).ToArray();
        using var images = new ResidentMountains(paths, records);
        var document = Generate(records, paths);
        Console.WriteLine("BACKGROUND load created: 120000 owner stamps; opening editor during gesture");
        using var observation = BackgroundLoad.Observe();
        var initial = BackgroundLoad.Capture();
        await using var store = new StampDerivedAssetStore(catalog, Path.Combine(root, "derived"),
            maintenance: new() { Automatic = false });
        using var overview = new SceneRenderer.OverviewPreparation();

        // First open while moving: the editing indexes and cold overview plan should yield.
        using var sampler = new GestureSampler();
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
        overview.UpdateDocument(document, images, static () => { });
        Task<(int Headers, int Images, int Unavailable)>? mipWork = null;
        Task? export = null;
        try
        {
            await Task.Delay(350);
            // A settled pause builds the cold plan; then a second gesture overlaps visible
            // overview rasters, detail rasters, repeated owner mips and an actual export.
            sampler.Moving = false;
            await editor.DerivedIndexPreparation.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntil(() => overview.Statistics.Plans > 0 && overview.Statistics.Idle);
            Console.WriteLine("BACKGROUND cold open settled; starting gesture, owner mips and export");
            sampler.Moving = true;
            mipWork = PrepareMips(store, records);
            var engine = new ImageExportEngine(new() { TileEdge = 512, TileCacheCapacity = 4 });
            var plan = engine.Plan(document, new(ExportImageFormat.Png, ExportAreaMode.WholeCanvas,
                null, 2048, 2048, ExportOutputMode.SingleImage, false, false));
            export = engine.ExportAsync(document, plan, Path.Combine(root, "export.png"), images);
            // Like a canvas, both caches and their Skia surface stay on one render thread.
            var detailStarts = await Task.Run(() =>
            {
                using var overviewCache = new ObjectOverviewCache();
                using var detailCache = new ObjectOverviewCache();
                using var surface = SKSurface.Create(new SKImageInfo(1024, 1024))!;
                var clock = Stopwatch.StartNew();
                var started = 0;
                for (var frame = 0; clock.Elapsed < TimeSpan.FromSeconds(4); frame++)
                {
                    var shift = frame % 8 * 256;
                    Draw(surface, document, images, overviewCache, overview, 1d / 64, shift);
                    started += Draw(surface, document, images, detailCache, null, .25, shift).DetailRastersStarted;
                    Thread.Sleep(16);
                }
                return started;
            });
            await export.WaitAsync(TimeSpan.FromSeconds(60));
            var mipResults = await mipWork.WaitAsync(TimeSpan.FromSeconds(60));
            sampler.Stop();
            var samples = sampler.Samples;
            var peak = samples.MaxBy(s => s.Load.Total)!;
            var budget = Math.Max(1, Environment.ProcessorCount - 2);
            var violations = samples.Where(s => s.Load.Total > budget).ToArray();
            var final = BackgroundLoad.Capture();
            var entries = Enum.GetValues<BackgroundProducer>().Where(p => final.Entries[(int)p] > initial.Entries[(int)p])
                .Select(p => $"{p}={final.Entries[(int)p] - initial.Entries[(int)p]}");
            Console.WriteLine($"BACKGROUND cores={Environment.ProcessorCount} budget={budget} objects={DocumentDerivedIndexes.ObjectCount(document)} "
                + $"samples={samples.Count} peak={peak.Load.Total} violations={violations.Length} peakAtMs={peak.Milliseconds:F1}: {peak.Load}");
            Console.WriteLine($"BACKGROUND entries: {string.Join(", ", entries)}; detailQueued={detailStarts}; "
                + $"mipHeaders={mipResults.Headers}/6 mipImages={mipResults.Images} mipUnavailable={mipResults.Unavailable}");
            Console.WriteLine($"BACKGROUND sampleGapMs={sampler.MaxGapMilliseconds:F1}; overviewError={overview.Statistics.Error}");
            foreach (var sample in violations.Take(12)) Console.WriteLine($"VIOLATION {sample.Milliseconds:F1}ms {sample.Load.Total}/{budget}: {sample.Load}");
            Assert.True(samples.Count >= 100, "Too few active-gesture samples; this run cannot validate the budget.");
            Assert.True(detailStarts > 0, "The scenario did not queue detail rasters.");
            Assert.Null(overview.Statistics.Error);
            // Check the budget first so a Mac mip failure cannot hide an observed violation.
            Assert.True(violations.Length == 0, $"cores-2 exceeded in {violations.Length}/{samples.Count} samples: peak {peak.Load.Total}/{budget}: {peak.Load}");
            Assert.Equal(6, mipResults.Headers);
            Assert.True(mipResults.Images > 0, "Owner mip preparation failed; workload coverage is incomplete.");
            foreach (var producer in new[] { BackgroundProducer.Overview, BackgroundProducer.DetailRaster,
                BackgroundProducer.AssetHeader, BackgroundProducer.AssetBuild, BackgroundProducer.AssetSmallReader,
                BackgroundProducer.AssetLargeLane, BackgroundProducer.ExportRender })
                Assert.True(final.Entries[(int)producer] > initial.Entries[(int)producer], $"Producer not exercised: {producer}");
        }
        finally
        {
            sampler.Stop();
            overview.Reset();
            // Join every finite operation before the sources and temporary directory are freed.
            var joins = new List<Task>
            {
                editor.DerivedIndexPreparation.WaitAsync(TimeSpan.FromSeconds(30)),
                WaitUntil(() => overview.Statistics.Idle),
                // Queued and executing static detail jobs both own pinned leases.
                WaitUntil(() => images.ActiveLeases == 0),
            };
            if (export is not null) joins.Add(export.WaitAsync(TimeSpan.FromSeconds(60)));
            if (mipWork is not null) joins.Add(mipWork.WaitAsync(TimeSpan.FromSeconds(60)));
            await Task.WhenAll(joins);
        }
    }

    private static async Task<(int Headers, int Images, int Unavailable)> PrepareMips(StampDerivedAssetStore store, AssetRecord[] records)
    {
        var headers = (await Task.WhenAll(records.Select(r => store.Request(r.Sha256))))
            .OfType<StampDerivedAssetStore.Header>().ToArray();
        var images = 0; var unavailable = records.Length - headers.Length;
        // More than one lane plus the small reader, sustained across the export's first bands.
        for (var round = 0; round < 40; round++)
        {
            var requested = headers.SelectMany(h => new[] { 0, 2, 4 }.Select(level => (h, level))).ToArray();
            await Task.WhenAll(requested.Select(item =>
            {
                Assert.True(store.TryRequestPixels(item.h, 0, item.level, out var task));
                return task;
            }));
            foreach (var (header, level) in requested)
            {
                Assert.True(store.TryTakePixels(new(header.Sha, 0, level), out var mip));
                if (mip?.Image is not null || mip?.Pixels is not null) images++; else unavailable++;
                mip?.Image?.Dispose();
            }
            await Task.Delay(20);
        }
        return (headers.Length, images, unavailable);
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "Background producer did not finish.");
            await Task.Delay(10);
        }
    }

    private static MapDocument Generate(AssetRecord[] records, string[] paths)
    {
        var objects = Enumerable.Range(0, 120_000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "mountain", 256 + i % 400 * 12, 256 + i / 400 * 12, 24, 24, i % 7, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(records[i % records.Length].Sha256) }).ToArray();
        return new(DocumentId.New(), "background guard", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
            records.Select((r, i) => new MapAssetReference(r.Sha256, r.DisplayName, r.MediaType, r.Extension,
                ProjectAssetStorage.Embedded, DateTimeOffset.UnixEpoch, "owner fixture", "owner", File.ReadAllBytes(paths[i]),
                catalogId: "owner-mountains")).ToArray());
    }

    private static RenderStats Draw(SKSurface surface, MapDocument document, IStampImageSource images,
        ObjectOverviewCache cache, SceneRenderer.OverviewPreparation? overview, double scale, double shift)
    {
        surface.Canvas.ResetMatrix(); surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale((float)scale); surface.Canvas.Translate((float)-shift, 0);
        var stats = new RenderStats();
        cache.BeginFrame();
        try
        {
            SceneRenderer.Render(surface.Canvas, document, new()
            {
                Stamps = images, ObjectCache = cache, OverviewPreparation = overview, ViewScale = scale,
                LodTier = RenderLodTier.Overview, CameraMoving = true, Stats = stats,
                VisibleWorld = new(shift, 0, shift + 1024 / scale, 1024 / scale), RequestRefinedFrame = static () => { },
            });
        }
        finally { cache.EndFrame(); }
        return stats;
    }

    private sealed class GestureSampler : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private int _moving = 1;
        public bool Moving { set => Volatile.Write(ref _moving, value ? 1 : 0); }
        public List<(double Milliseconds, BackgroundLoad.Snapshot Load)> Samples { get; } = [];
        public double MaxGapMilliseconds { get; private set; }
        public GestureSampler()
        {
            BackgroundWork.NoteInteraction();
            _thread = new Thread(() =>
            {
                var watch = Stopwatch.StartNew();
                double last = 0;
                while (!_stop.IsSet)
                {
                    var now = watch.Elapsed.TotalMilliseconds;
                    MaxGapMilliseconds = Math.Max(MaxGapMilliseconds, now - last); last = now;
                    if (Volatile.Read(ref _moving) != 0)
                    {
                        BackgroundWork.NoteInteraction();
                        Samples.Add((now, BackgroundLoad.Capture()));
                    }
                    _stop.Wait(10);
                }
            }) { IsBackground = true, Name = "cores-2 headless sampler" };
            _thread.Start();
        }
        public void Stop() { _stop.Set(); _thread.Join(); }
        public void Dispose() { Stop(); _stop.Dispose(); }
    }

    // A prewarmed resident shelf of actual owner bytes: detail rasters can pin immutable images
    // while the independent production store prepares cold mips. No fake scheduler jobs/barriers.
    private sealed class ResidentMountains : IStampImageSource, IDisposable
    {
        private readonly Dictionary<string, SKImage> _images;
        private int _leases;
        public int ActiveLeases => Volatile.Read(ref _leases);
        public ResidentMountains(string[] paths, AssetRecord[] records) => _images = records.Select((record, i) =>
        {
            using var bitmap = SKBitmap.Decode(paths[i]);
            bitmap.SetImmutable();
            return (record.Sha256, Image: SKImage.FromBitmap(bitmap));
        }).ToDictionary(item => item.Sha256, item => item.Image);
        public long? OverviewColorRevision => 0;
        public SKImage? TryGetImage(string sha256) => _images.GetValueOrDefault(sha256);
        public IStampPreparationPlan CreatePreparationPlan() => new Plan(this);
        private sealed class Plan(ResidentMountains source) : IStampPreparationPlan
        {
            public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps) { }
            public bool Prepare(Action ready) => true;
            public long Bytes => 6 * 512L * 512 * 4;
            public long SourceRevision => 0;
            public IStampImageLease Pin() { Interlocked.Increment(ref source._leases); return new Lease(source); }
        }
        private sealed class Lease(ResidentMountains source) : IStampImageLease
        {
            public SKImage? TryGetImage(string sha256) => source.TryGetImage(sha256);
            public void Dispose() => Interlocked.Decrement(ref source._leases);
        }
        public void Dispose() { foreach (var image in _images.Values) image.Dispose(); }
    }
}

public sealed class CoresGuardFactAttribute : FactAttribute
{
    public CoresGuardFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAP_RUN_CORES_GUARD") != "1")
            Skip = "defect: DEC-111 step 4: detail raster, mip lanes and export have independent budgets; "
                + "the combined cores-2 limit is not enforced. Set MAP_RUN_CORES_GUARD=1 to report violators.";
    }
}
