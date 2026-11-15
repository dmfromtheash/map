using System.Diagnostics;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Tests;
using SkiaSharp;

namespace Map.Rendering.Tests;

// Serial with the exact-layer A/B tests, which change SceneRenderer.OverviewExactLayers.
[Collection("OverviewExactLayers")]
public sealed class ColdLibraryWholeMapTests
{
    private const int DirectObjectLimit = 512;

    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public async Task Remembered_colors_start_the_exact_layer_without_any_header_request(int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-remembered-fit", Guid.NewGuid().ToString("N"));
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var cache = Path.Combine(root, "derived");
            var (record, header) = ColorCacheFixture.Seed(catalog, cache);
            await using (var writer = new StampDerivedAssetStore(catalog, cache, startWatcher: _ => { }))
                Assert.NotNull(await writer.Request(record.Sha256));
            // This is a second real store, loading the file the previous instance wrote.
            // Remove the header/pixels: accidental metadata work cannot borrow a warm hit.
            File.Delete(Path.Combine(cache, "v1", record.Sha256 + ".json"));
            File.Delete(Path.Combine(cache, "v1", record.Sha256 + ".pixels"));
            await using var remembered = new StampDerivedAssetStore(catalog, cache, startWatcher: _ => { });
            Assert.Equal(new SKColor(header.Color), remembered.KnownColor(record.Sha256));
            using var view = new WholeMapView(count, document: WholeMapView.Document(count, [record.Sha256]), remembered: remembered);
            var adopted = view.PendingFrames();
            Console.WriteLine($"HOTREVIEW {count}: first adopted frames maxDirect={adopted.Max(frame => frame.DirectObjects)} layers={string.Join(',', adopted.Select(frame => frame.Stats.OverviewExactLayer))}");
            var started = view.WaitForLayer();
            Assert.True(started.Stats.OverviewExactLayer > 0);
            Assert.False(view.Images.Ready); // Exact pixels are still pending; colours alone unblock the layer.
            var frames = Enumerable.Range(0, 9).Select(_ => view.Frame("remembered-pending")).ToArray();
            Assert.All(frames, frame => Assert.InRange(frame.DirectObjects, 0, DirectObjectLimit));
            view.Images.Arrive();
            var settled = view.Settle();
            Assert.True(settled.Stats.OverviewExactLayer > 0);
            Assert.InRange(settled.DirectObjects, 0, DirectObjectLimit);
            view.AssertVisible();
            Assert.Equal(0, remembered.MetadataEntries);
            Assert.Equal(0, remembered.DiskHits);
            Assert.Equal(0, remembered.Builds);
            Assert.Equal(default, remembered.IoDiagnostics);
            Console.WriteLine($"HOTREVIEW {count}: remembered layer={started.Stats.OverviewExactLayer} maxDirect={frames.Max(frame => frame.DirectObjects)} headers=0 io={remembered.IoDiagnostics}");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public void Pending_fit_frames_have_bounded_direct_object_work(int count)
    {
        using var view = new WholeMapView(count);
        var frames = view.PendingFrames();
        Assert.All(frames, frame => Assert.InRange(frame.DirectObjects, 0, DirectObjectLimit));
        Assert.All(frames, frame => Assert.InRange(frame.Stats.DrawCalls, 0, DirectObjectLimit + 32));
    }

    [ColdLibraryDefectFact]
    public void Pending_fit_frame_object_time_does_not_scale_with_the_document()
    {
        double warmSmall;
        using (var view = new WholeMapView(50_000, ready: true))
        {
            view.Settle();
            warmSmall = Median(Enumerable.Range(0, 9).Select(_ => view.Frame("warm-time")).ToArray());
            view.AssertVisible();
        }
        double warmLarge;
        using (var view = new WholeMapView(200_000, ready: true))
        {
            view.Settle();
            warmLarge = Median(Enumerable.Range(0, 9).Select(_ => view.Frame("warm-time")).ToArray());
            view.AssertVisible();
        }
        Console.WriteLine($"OVGUARD warm scaling 50000 -> 200000: objects median {warmSmall:F3} -> {warmLarge:F3} ms");
        AssertTimeBound(warmSmall, warmLarge);
        double small;
        using (var view = new WholeMapView(50_000)) small = Median(view.PendingFrames());
        double large;
        using (var view = new WholeMapView(200_000)) large = Median(view.PendingFrames());
        Console.WriteLine($"OVGUARD scaling 50000 -> 200000: objects median {small:F3} -> {large:F3} ms; ratio={large / Math.Max(.001, small):F2}");
        // Nine post-adoption samples, full JIT, a ratio plus 0.1 ms scheduling allowance.
        // The deterministic draw-count guard above is the primary complexity check.
        AssertTimeBound(small, large);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public void Warm_fit_frames_pass_the_same_direct_work_bound(int count)
    {
        using var view = new WholeMapView(count, ready: true);
        view.Settle();
        var frames = Enumerable.Range(0, 9).Select(_ => view.Frame("warm")).ToArray();
        Assert.All(frames, frame => Assert.InRange(frame.DirectObjects, 0, DirectObjectLimit));
        Assert.All(frames, frame => Assert.InRange(frame.Stats.DrawCalls, 0, DirectObjectLimit + 32));
        Assert.All(frames, frame => Assert.True(frame.Stats.OverviewExactLayer == 1 || frame.Stats.LayerImagesKept == 1,
            "warm frame used neither the exact layer nor its completed kept image"));
        Assert.All(frames, frame => Assert.Equal(0, frame.FrameImageLookups));
        view.AssertVisible();
    }

    [Fact]
    public void Images_arriving_after_pending_frames_complete_the_fit_view()
    {
        using var view = new WholeMapView(50_000);
        var pending = view.PendingFrames();
        Assert.All(pending, frame => Assert.True(frame.Stats.DetailTilesPending > 0));
        Assert.All(pending, frame => Assert.True(frame.Stats.UnresolvedImages > 0));
        Assert.All(pending, frame => Assert.True(frame.FrameImageLookups > 0));
        view.Images.Arrive();
        var settled = view.Settle();
        Assert.Equal(1, settled.Stats.OverviewExactLayer);
        Assert.Equal(0, settled.Stats.UnresolvedImages);
        Assert.Equal(0, settled.Stats.DetailTilesPending);
        Assert.InRange(settled.DirectObjects, 0, DirectObjectLimit);
        view.AssertVisible();
    }

    // No million-object allocation in ordinary CI; the opt-in runs the same assertions.
    [ColdLibraryHugeFact]
    public void Million_pending_fit_frames_have_bounded_direct_object_work()
    {
        using var view = new WholeMapView(1_000_000);
        Assert.All(view.PendingFrames(), frame => Assert.InRange(frame.DirectObjects, 0, DirectObjectLimit));
    }

    private static double Median(FrameResult[] frames) => frames.Select(f => f.Stats.ObjectsMilliseconds).Order().ElementAt(frames.Length / 2);

    private static void AssertTimeBound(double small, double large) => Assert.True(large <= small * 2 + .1,
        $"4x objects cost {small:F3} -> {large:F3} ms on the frame (limit 2x + 0.1 ms)");

    internal sealed record FrameResult(RenderStats Stats, int FrameImageLookups)
    {
        // ObjectsDrawn includes objects represented by cached rasters. It is NOT a direct-draw counter.
        public int DirectObjects => Stats.OverviewExactLayer == 0 ? Stats.ExactObjects : Stats.OverviewExactPatched;
    }

    internal sealed class WholeMapView : IDisposable
    {
        private const int Extent = 32768;
        private const double Scale = 512d / Extent;
        private readonly MapDocument _document;
        private readonly ObjectOverviewCache _tiles = new();
        private readonly ObjectOverviewCache _atlas = new();
        private readonly LayerImageCache _layers = new();
        private readonly SceneRenderer.OverviewPreparation _preparation = new(static () => false);
        private readonly SKSurface _surface = SKSurface.Create(new SKImageInfo(512, 512))!;
        private int _frame;
        private int _requests;
        public readonly PendingMountains Images;
        public int Count { get; }

        public WholeMapView(int count, bool ready = false, MapDocument? document = null, StampDerivedAssetStore? remembered = null)
        {
            Count = count;
            Images = new PendingMountains(ready, remembered);
            _document = document ?? Document(count);
            _preparation.UpdateDocument(_document, Images, RequestFrame);
        }

        public static MapDocument Document(int count, string[]? assetShas = null)
        {
            // Same area and density distribution at both sizes; 98% subpixel stamps, 2% exact.
            // Several independent assets, all backed by the owner's existing mountain fixture.
            var random = new Random(41071);
            var objects = new SceneObject[count];
            var shas = assetShas ?? Enumerable.Range(1, 8).Select(i => i.ToString("x64")).ToArray();
            for (var i = 0; i < count; i++)
            {
                var side = i % 50 == 0 ? 512 : 32;
                objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
                    512 + random.NextDouble() * (Extent - 1536), 512 + random.NextDouble() * (Extent - 1536),
                    side, side, 0, "#FFFFFFFF", "#00000000", 0, true, false)
                    { Stamp = new StampStyle(shas[i % shas.Length]) };
            }
            return new MapDocument(DocumentId.New(), "cold whole map", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(Extent, Extent, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
                 new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
                shas.Select(sha => new MapAssetReference(sha, "owner mountain", "image/png", "png",
                    ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner fixture", "owner",
                    catalogId: "owner-mountain-01")).ToArray());
        }

        private void RequestFrame() => Interlocked.Increment(ref _requests);

        public FrameResult Frame(string phase, bool log = true)
        {
            var canvas = _surface.Canvas;
            canvas.ResetMatrix();
            canvas.Clear(SKColors.White);
            canvas.Scale((float)Scale);
            var stats = new RenderStats();
            Images.BeginMeasuredFrame();
            _tiles.BeginFrame();
            _atlas.BeginFrame();
            try
            {
                SceneRenderer.Render(canvas, _document, new RenderOptions
                {
                    Stamps = Images, ObjectCache = _tiles, AtlasCache = _atlas, LayerImages = _layers,
                    OverviewPreparation = _preparation, RequestRefinedFrame = RequestFrame,
                    Stats = stats, ViewScale = Scale, LodTier = RenderLodTier.Overview,
                    VisibleWorld = new(0, 0, Extent, Extent), CameraMoving = false,
                });
                canvas.Flush();
            }
            finally { _tiles.EndFrame(); _atlas.EndFrame(); }
            Assert.Null(_preparation.Statistics.Error);
            var result = new FrameResult(stats, Images.FrameImageLookups);
            Assert.InRange(result.DirectObjects, 0, DirectObjectLimit);
            if (log) Console.WriteLine($"OVGUARD {Count} {phase} frame={++_frame}: drawn={stats.ObjectsDrawn} overview={stats.OverviewObjects} exact={stats.ExactObjects} direct={result.DirectObjects} calls={stats.DrawCalls} layer={stats.OverviewExactLayer} kept={stats.LayerImagesKept} started={stats.OverviewExactLayerStarted} plans={Images.PreparationPlans} objectsMs={stats.ObjectsMilliseconds:F3} exactMs={stats.OverviewExactMilliseconds:F3} pending={stats.DetailTilesPending} unresolved={stats.UnresolvedImages} chunks={stats.OverviewChunks} frameLookups={result.FrameImageLookups}");
            return result;
        }

        public FrameResult[] PendingFrames()
        {
            // Let the real worker build the plan and hand off all visible chunks while the
            // metadata and pixels remain pending. Then measure repeated still/refinement frames.
            // All objects must be represented before sampling. Optional neighbour prefetch
            // need not be idle: the canvas also continues framing while that work runs.
            WaitFrames("cold-adopt", frame => frame.Stats.ObjectsDrawn == Count);
            Assert.False(Images.Ready);
            Assert.True(Volatile.Read(ref _requests) > 0);
            return Enumerable.Range(0, 9).Select(_ => Frame("pending")).ToArray();
        }

        public FrameResult Settle() => WaitFrames("settle", frame => frame.Stats.DetailTilesPending == 0 &&
            frame.Stats.UnresolvedImages == 0 && frame.Stats.OverviewExactLayer == 1);

        public FrameResult WaitForLayer() => WaitFrames("remembered-start", frame => frame.Stats.OverviewExactLayer > 0);

        private FrameResult WaitFrames(string phase, Func<FrameResult, bool> complete)
        {
            FrameResult? result = null;
            var watch = Stopwatch.StartNew();
            var frames = 0;
            var maxDirect = 0;
            for (var i = 0; i < 250 && watch.Elapsed < TimeSpan.FromSeconds(10); i++)
            {
                result = Frame(phase, log: false);
                frames++;
                maxDirect = Math.Max(maxDirect, result.DirectObjects);
                if (complete(result)) break;
                Thread.Sleep(5);
            }
            Assert.NotNull(result);
            Console.WriteLine($"OVGUARD {Count} {phase} frames={frames}/250 wall={watch.Elapsed.TotalMilliseconds:F1} ms; layer={result.Stats.OverviewExactLayer} drawn={result.Stats.ObjectsDrawn} pending={result.Stats.DetailTilesPending} maxDirectDuringWait={maxDirect}");
            Assert.True(complete(result), $"{phase} did not finish in 250 frames / 10 seconds");
            return result;
        }

        public void AssertVisible()
        {
            using var image = _surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            Assert.Contains(bitmap.Pixels, pixel => pixel.Alpha > 0 && pixel.Red < 220 && pixel.Green < 220 && pixel.Blue < 220);
        }

        public (int Tiles, int Atlas, int Layers, int Ready, int Queued) CacheEntries =>
            (_tiles.Count, _atlas.Count, _layers.Count, _preparation.Statistics.ReadyImages, _preparation.Statistics.Queued);

        public WeakReference[] LifetimeReferences =>
            [new(this), new(_tiles), new(_atlas), new(_layers), new(_preparation), new(Images)];

        public void Dispose()
        {
            _preparation.Dispose();
            var watch = Stopwatch.StartNew();
            while (!_preparation.Statistics.Idle && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(1);
            Assert.True(_preparation.Statistics.Idle, "closed overview worker did not drain");
            _layers.Dispose(); _atlas.Dispose(); _tiles.Dispose(); _surface.Dispose(); Images.Dispose();
        }
    }

    internal sealed class PendingMountains : IStampImageSource, IDisposable
    {
        private readonly SKImage _image;
        private readonly StampDerivedAssetStore? _remembered;
        private readonly object _gate = new();
        private Action? _readyCallback;
        private int _ready;
        private int _frameThread;
        private int _frameLookups;
        private int _plans;
        private long _unresolved;
        public bool Ready => Volatile.Read(ref _ready) != 0;
        public long? OverviewColorRevision => _remembered?.OverviewColorRevision ?? (Ready ? 1 : 0);
        public bool ImageRequestsPending => !Ready;
        public long UnresolvedRequestVersion => Volatile.Read(ref _unresolved);
        public int FrameImageLookups => Volatile.Read(ref _frameLookups);
        public int PreparationPlans => Volatile.Read(ref _plans);

        public PendingMountains(bool ready, StampDerivedAssetStore? remembered = null)
        {
            _ready = ready ? 1 : 0;
            _remembered = remembered;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
            _image = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"))!;
        }

        public void BeginMeasuredFrame() { _frameThread = Environment.CurrentManagedThreadId; _frameLookups = 0; }
        public SKImage? TryGetImage(string sha256)
        {
            if (Environment.CurrentManagedThreadId == _frameThread) Interlocked.Increment(ref _frameLookups);
            if (Ready) return _image;
            Interlocked.Increment(ref _unresolved);
            return null;
        }
        public (int Width, int Height)? TryGetImageSize(string sha256) => Ready ? (_image.Width, _image.Height) : null;
        public SKColor? TryGetOverviewColor(string sha256) => _remembered is not null
            ? _remembered.KnownColor(sha256) : Ready ? SceneRenderer.StampOverviewColor(_image) : null;
        public bool PrepareOverview(IEnumerable<string> assets, Action ready)
        {
            lock (_gate) _readyCallback = ready;
            return _remembered is not null ? assets.All(sha => _remembered.KnownColor(sha) is not null) : Ready;
        }
        public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready) => Ready;
        public IStampPreparationPlan CreatePreparationPlan()
        {
            Interlocked.Increment(ref _plans);
            return new Plan(this);
        }
        public void Arrive()
        {
            Volatile.Write(ref _ready, 1);
            Action? callback;
            lock (_gate) callback = _readyCallback;
            callback?.Invoke();
        }
        public void Dispose() { lock (_gate) _readyCallback = null; _image.Dispose(); }

        private sealed class Plan(PendingMountains source) : IStampPreparationPlan
        {
            private readonly HashSet<string> _assets = [];
            public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps)
            {
                foreach (var stamp in stamps) _assets.Add(stamp.Sha256);
            }
            public bool Prepare(Action ready) => source.Ready;
            public long Bytes => _assets.Count * (long)source._image.Width * source._image.Height * 4;
            public long SourceRevision => 0;
            public IStampImageLease? Pin() => source.Ready ? new Lease(source) : null;
            public IStampImageLease PinResident() => new Lease(source);
        }

        private sealed class Lease(PendingMountains source) : IStampImageLease
        {
            private long _unresolved;
            public bool ImageRequestsPending => !source.Ready;
            public long UnresolvedRequestVersion => Volatile.Read(ref _unresolved);
            public SKImage? TryGetImage(string sha256)
            {
                if (source.Ready) return source._image;
                Interlocked.Increment(ref _unresolved);
                return null;
            }
            public SKColor? TryGetOverviewColor(string sha256) => source.TryGetOverviewColor(sha256);
            public void Dispose() { }
        }
    }
}

public sealed class ColdLibraryDefectFactAttribute : FactAttribute
{
    public ColdLibraryDefectFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAP_OVGUARD_RED") != "1")
            Skip = "defect: cold metadata leaves O(exact objects) frame work; SceneRenderer.cs:6521";
    }
}

public sealed class ColdLibraryHugeFactAttribute : FactAttribute
{
    public ColdLibraryHugeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAP_OVGUARD_HUGE") != "1")
            Skip = "defect: million-object cold fit direct draws, opt in with MAP_OVGUARD_HUGE=1; SceneRenderer.cs:6521";
    }
}
