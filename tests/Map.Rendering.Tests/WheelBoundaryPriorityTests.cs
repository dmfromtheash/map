using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection(RealTimeSettleCollection.Name)]
public sealed class WheelBoundaryPriorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moving_boundary_frames_prepare_exact_images_before_the_bounded_neighbor_warmup(bool keptNeighbor)
    {
        using var disk = new DenseDetailTilesTests.MipDisk();
        using var additions = new ManualResetEventSlim(true);
        var sha = new string('a', 64);
        var source = new ProbeSource(disk.Read(0, 32, 32), additions);
        var objects = Enumerable.Range(0, 6000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Гора", i % 100 * 320, i / 100 * 530, 64, 64, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Boundary", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects)]);
        using var tiles = new ObjectOverviewCache();
        using var preparation = new SceneRenderer.OverviewPreparation();
        using var surface = SKSurface.Create(new SKImageInfo(1280, 720))!;
        preparation.UpdateDocument(document, source, static () => { });

        bool Frame(double scale, bool moving)
        {
            var c = surface.Canvas;
            c.ResetMatrix(); c.Clear(SKColors.White);
            var halfW = 640 / scale; var halfH = 360 / scale;
            c.Scale((float)scale); c.Translate((float)-(16384 - halfW), (float)-(16384 - halfH));
            var stats = new RenderStats();
            tiles.BeginFrame();
            try
            {
                SceneRenderer.Render(c, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = tiles, OverviewPreparation = preparation, Stats = stats,
                    ViewScale = scale, CameraMoving = moving, FastTerrain = moving, LodTier = RenderLodTier.Overview,
                    VisibleWorld = new(16384 - halfW, 16384 - halfH, 16384 + halfW, 16384 + halfH),
                    RequestRefinedFrame = static () => { },
                });
            }
            finally { tiles.EndFrame(); }
            Assert.Null(preparation.Statistics.Error);
            return stats.DetailTilesPending == 0;
        }

        var wait = Stopwatch.StartNew();
        while (!Frame(.02, false) && wait.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
        Assert.True(Frame(.02, false), "initial overview did not settle");
        Assert.Empty(source.Plans);

        if (keptNeighbor)
        {
            source.Ready = true;
            wait.Restart();
            while (!Frame(.0625, false) && wait.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(5);
            Assert.True(Frame(.0625, false), "the neighboring detail level did not settle");
            source.Ready = false;
            source.Plans.Clear();
            source.PreparedWidths.Clear();
        }

        const double handoff = .033258;
        if (!keptNeighbor) additions.Reset();
        try
        {
            Frame(handoff, true);
            Assert.InRange(source.Plans.Count, 1, 2);
            if (!keptNeighbor)
            {
                var queued = source.Plans.Count;
                Frame(handoff, true);
                Frame(handoff, true);
                Assert.Equal(queued, source.Plans.Count); // blocked builders cannot accumulate across frames.
            }
        }
        finally { additions.Set(); }
        wait.Restart();
        do { Thread.Sleep(5); Frame(handoff, true); }
        while (source.PreparedWidths.Count == 0 && wait.Elapsed < TimeSpan.FromSeconds(10));
        Assert.NotEmpty(source.PreparedWidths); // wheelstep's early low-scale return failed here.

        source.PreparedWidths.Clear();
        wait.Restart();
        do { Frame(handoff, false); Thread.Sleep(5); }
        while ((source.PreparedWidths.Count == 0 || source.Plans.Any(p => !p.Sealed))
            && wait.Elapsed < TimeSpan.FromSeconds(10));
        var exactWidth = 64 * Math.Pow(2, Math.Ceiling(Math.Log2(handoff) * 4) / 4);
        Assert.NotEmpty(source.PreparedWidths);
        Assert.Equal(exactWidth, source.PreparedWidths[0], 10);
        Assert.All(source.PreparedWidths, width => Assert.Equal(exactWidth, width, 10));
        var neighborWidth = 64 * Math.Pow(2, Math.Max(-20, Math.Ceiling(Math.Log2(handoff) * 4) - 4) / 4);
        Assert.All(source.Plans, plan =>
        {
            Assert.True(plan.Sealed, "the frame must not materialize the plan's key buffers");
            Assert.True(Math.Abs(plan.Width - exactWidth) < 1e-10 || Math.Abs(plan.Width - neighborWidth) < 1e-10,
                "a whole-map seed competed with the exact boundary level");
        });
    }

    // Leaves every detail image cold so all requested levels remain observable. The overview
    // samples an owner's fixture; no raster or image request can hide a competing coarse plan.
    private sealed class ProbeSource(SKImage fixture, ManualResetEventSlim additions) : IStampImageSource
    {
        public bool Ready;
        public ManualResetEventSlim Additions { get; } = additions;
        public List<ProbePlan> Plans { get; } = [];
        public List<double> PreparedWidths { get; } = [];
        public SKImage? TryGetImage(string sha256) => fixture;
        public (int Width, int Height)? TryGetImageSize(string sha256) => (400, 400);
        public IStampPreparationPlan CreatePreparationPlan()
        {
            var plan = new ProbePlan(this);
            Plans.Add(plan);
            return plan;
        }
    }

    private sealed class ProbePlan(ProbeSource source) : IStampPreparationPlan
    {
        public double Width;
        public volatile bool Sealed;
        public long Bytes => 1;
        public long SourceRevision => 0;
        public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps)
        {
            if (!source.Additions.IsSet) source.Additions.Wait();
            foreach (var stamp in stamps) Width = Math.Max(Width, stamp.Width);
        }
        public void Seal() => Sealed = true;
        public bool Prepare(Action ready)
        {
            source.PreparedWidths.Add(Width);
            return source.Ready;
        }
    }
}
