using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection("Memory pressure")]
public sealed class EdgePanDemandTests
{
    [Fact]
    public void Leaving_detail_releases_requests_even_when_detail_is_never_drawn_again()
    {
        // test-change: zoom-out and fully kept frames must cancel the retained demand too.
        var (document, source) = Fixture();
        using var cache = new ObjectOverviewCache();
        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024))!;
        var deadline = Environment.TickCount64 + 5000;
        while (source.Active == 0 && Environment.TickCount64 < deadline)
        {
            cache.BeginFrame();
            try { Draw(surface, document, source, cache); }
            finally { cache.EndFrame(); }
            Thread.Sleep(5);
        }
        Assert.True(source.Active > 0, "A pending tile must retain its image requests across frames.");
        // A fit/overview or kept-image frame never calls DrawDetailedStampTiles.
        cache.BeginFrame();
        cache.EndFrame();
        deadline = Environment.TickCount64 + 5000;
        while (source.Active > 0 && Environment.TickCount64 < deadline) Thread.Sleep(5);
        Assert.Equal(0, source.Active);
    }

    [Fact]
    public void The_frame_defers_submission_instead_of_waiting_for_a_busy_worker_queue()
    {
        var (document, source) = Fixture();
        using var queue = new SceneRenderer.DetailWorkQueue(1);
        var original = SceneRenderer.DetailRasterQueue;
        var gate = typeof(SceneRenderer.DetailWorkQueue).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(queue)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (gate) { entered.Set(); release.Wait(); } }) { IsBackground = true };
        holder.Start();
        Task<RenderStats>? frame = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            SceneRenderer.DetailRasterQueue = queue;
            frame = Task.Run(() =>
            {
                using var cache = new ObjectOverviewCache();
                using var surface = SKSurface.Create(new SKImageInfo(1024, 1024))!;
                cache.BeginFrame();
                try { return Draw(surface, document, source, cache); }
                finally { cache.EndFrame(); }
            });
            Assert.True(frame.Wait(TimeSpan.FromSeconds(1)), "The frame waited for a worker's queue gate.");
            Assert.True(frame.Result.DetailQueueDeferrals > 0);
        }
        finally
        {
            release.Set();
            var joined = holder.Join(TimeSpan.FromSeconds(5));
            SceneRenderer.DetailRasterQueue = original;
            Assert.True(joined);
            if (frame is not null) Assert.True(frame.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    private static RenderStats Draw(SKSurface surface, MapDocument document, WaitingImages source, ObjectOverviewCache cache)
    {
        var stats = new RenderStats();
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            Stamps = source, ObjectCache = cache, Stats = stats, ViewScale = 1,
            VisibleWorld = new(0, 0, 1023, 1023), RequestRefinedFrame = () => { },
        });
        return stats;
    }

    private static (MapDocument, WaitingImages) Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        using var owner = SKImage.FromEncodedData(path);
        var source = new WaitingImages(SceneRenderer.StampOverviewColor(owner), owner.Width, owner.Height);
        var objects = Enumerable.Range(0, 2500).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Mountain", i % 50 * 16, i / 50 * 16, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Pending pan", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, objects)])
            .WithAssets([new MapAssetReference(sha, sha, "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)]);
        return (document, source);
    }

    private sealed class WaitingImages(SKColor color, int width, int height) : IStampImageSource
    {
        private int _active;
        public int Active => Volatile.Read(ref _active);
        // No pixel ever arrives in this fixture; workers use values only, never the owner's image handle.
        public SKImage? TryGetImage(string sha256) => null;
        public (int Width, int Height)? TryGetImageSize(string sha256) => (width, height);
        public SKColor? TryGetOverviewColor(string sha256) => color;
        public IStampPreparationPlan CreatePreparationPlan() => new Plan(this);
        private sealed class Plan(WaitingImages source) : IStampPreparationPlan
        {
            public bool BackgroundPreparation => true;
            public long SourceRevision => 0;
            public long Bytes => 1;
            public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps) { }
            public bool Prepare(Action ready) => false;
            public IDisposable KeepRequests()
            {
                Interlocked.Increment(ref source._active);
                return new Interest(source);
            }
        }
        private sealed class Interest(WaitingImages source) : IDisposable
        {
            private int _closed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _closed, 1) == 0) Interlocked.Decrement(ref source._active);
            }
        }
    }
}
