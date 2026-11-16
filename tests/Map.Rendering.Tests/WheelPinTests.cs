using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection(RealTimeSettleCollection.Name)]
public sealed class WheelPinTests
{
    [Fact]
    public void A_blocked_worker_pin_does_not_block_the_frame_and_a_pin_miss_retries_without_a_synchronous_raster()
    {
        using var disk = new DenseDetailTilesTests.MipDisk();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var previousQueue = SceneRenderer.DetailRasterQueue;
        using var queue = new SceneRenderer.DetailWorkQueue(2);
        SceneRenderer.DetailRasterQueue = queue;
        var frameThread = Environment.CurrentManagedThreadId;
        var source = new PinSource(disk.Read(0, 32, 32), frameThread, entered, release);
        var sha = new string('a', 64);
        var objects = Enumerable.Range(0, 6000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Гора", i % 100 * 10, i / 100 * 10, 64, 64, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Pin", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects)]);
        using var tiles = new ObjectOverviewCache();
        using var surface = SKSurface.Create(new SKImageInfo(1280, 720))!;
        var onFrame = 0;

        bool Frame()
        {
            var c = surface.Canvas;
            c.ResetMatrix(); c.Clear(SKColors.White);
            var stats = new RenderStats();
            tiles.BeginFrame();
            try
            {
                SceneRenderer.Render(c, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = tiles, Stats = stats, ViewScale = 1,
                    VisibleWorld = new(0, 0, 1280, 720), RequestRefinedFrame = static () => { },
                });
            }
            finally { tiles.EndFrame(); }
            onFrame += stats.DetailTilesBuiltOnFrame;
            return stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0;
        }

        try
        {
            var wait = Stopwatch.StartNew();
            do { Frame(); Thread.Sleep(5); }
            while (!entered.IsSet && source.FramePins == 0 && wait.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Equal(0, source.FramePins);
            Assert.True(entered.IsSet, "no background pin started");
            var work = Stopwatch.StartNew();
            Assert.False(Frame());
            Assert.True(work.ElapsedMilliseconds < 100, "the frame waited for a blocked worker pin");
            release.Set();
            wait.Restart();
            while (!Frame() && wait.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
            Assert.True(Frame(), "the view did not settle after retrying the pin miss");
            Assert.True(source.WorkerPins >= 2);
            Assert.Equal(0, onFrame);
            using var actual = surface.Snapshot();
            using var actualPixels = SKBitmap.FromImage(actual);
            using var referenceTiles = new ObjectOverviewCache();
            surface.Canvas.Clear(SKColors.White);
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                Stamps = source, ObjectCache = referenceTiles, ViewScale = 1, VisibleWorld = new(0, 0, 1280, 720),
            });
            using var expected = surface.Snapshot();
            using var expectedPixels = SKBitmap.FromImage(expected);
            Assert.Equal(expectedPixels.Bytes, actualPixels.Bytes);
        }
        finally
        {
            release.Set();
            SceneRenderer.DetailRasterQueue = previousQueue;
            // Keep the fixture and events alive until every pin that used them has returned.
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref source.PinsRunning) == 0, TimeSpan.FromSeconds(10)));
        }
    }

    private sealed class PinSource(SKImage fixture, int frameThread, ManualResetEventSlim entered, ManualResetEventSlim release)
        : IStampImageSource
    {
        private readonly int _frameThread = frameThread;
        private readonly ManualResetEventSlim _entered = entered, _release = release;
        public int FramePins, WorkerPins, PinsRunning;
        public SKImage? TryGetImage(string sha256) => fixture;
        public (int Width, int Height)? TryGetImageSize(string sha256) => (400, 400);
        public IStampPreparationPlan CreatePreparationPlan() => new Plan(this);

        private sealed class Plan(PinSource source) : IStampPreparationPlan
        {
            public long Bytes => 1;
            public long SourceRevision => 0;
            public bool CanPinOnWorker => true;
            public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps) { }
            public bool Prepare(Action ready) => true;
            public IStampImageLease? Pin()
            {
                if (Environment.CurrentManagedThreadId == source._frameThread)
                {
                    Interlocked.Increment(ref source.FramePins);
                    return null;
                }
                Interlocked.Increment(ref source.PinsRunning);
                try
                {
                    var attempt = Interlocked.Increment(ref source.WorkerPins);
                    source._entered.Set();
                    source._release.Wait();
                    return attempt == 1 ? null : ((IStampImageSource)source).Lease();
                }
                finally { Interlocked.Decrement(ref source.PinsRunning); }
            }
        }
    }
}
