using System.Diagnostics;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection(RealTimeSettleCollection.Name)]
public sealed class FrameAllocationTests
{
    [Theory]
    [InlineData("settled", 2_000, 1d, false)]
    [InlineData("pan", 2_000, 1d, true)]
    [InlineData("pan-new-tiles", 18_000, 1d, true)]
    [InlineData("wheel", 2_000, 1.189207115, true)]
    [InlineData("fit", 50_000, 1d / 64, false)]
    public async Task Warm_frames_have_bounded_managed_allocations(string phase, int count, double scale, bool moving)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-frame-alloc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var fixtures = new DirectoryInfo(AppContext.BaseDirectory);
            while (fixtures is not null && !File.Exists(Path.Combine(fixtures.FullName, "Map.slnx"))) fixtures = fixtures.Parent;
            Assert.NotNull(fixtures);
            var records = Enumerable.Range(1, 6).Select(i => catalog.Import(Path.Combine(fixtures.FullName,
                "tests", "fixtures", "assets", $"owner-mountain-{i:00}.png"), new(), DateTimeOffset.UnixEpoch)).ToArray();
            var objects = new SceneObject[count];
            var side = (int)Math.Ceiling(Math.Sqrt(count));
            var extent = phase == "pan-new-tiles" ? 1536 : count == 2_000 ? 512 : 32768;
            var step = (extent - 64d) / side;
            for (var i = 0; i < count; i++)
                objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
                    32 + i % side * step, 32 + i / side * step, phase == "fit" ? 512 : 16,
                    phase == "fit" ? 512 : 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
                { Stamp = new StampStyle(records[i % records.Length].Sha256) };
            var document = new MapDocument(DocumentId.New(), "allocation probe", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(extent, extent, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
                 new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
                records.Select(r => new MapAssetReference(r.Sha256, r.Sha256, r.MediaType, r.Extension,
                    ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: r.Sha256)).ToArray());
            await using var source = new CatalogStampImageSource(catalog, () => document, () => null,
                derivedRoot: Path.Combine(root, "derived"), startWatcher: static _ => { });
            using var cache = new ObjectOverviewCache();
            using var atlas = new ObjectOverviewCache();
            using var preparation = new SceneRenderer.OverviewPreparation(static () => false) { PrefetchAfterOpenMilliseconds = 0 };
            using var surface = SKSurface.Create(new SKImageInfo(512, 512))!;
            var stats = new RenderStats();
            var options = new RenderOptions
            {
                Stamps = source, ObjectCache = cache, AtlasCache = atlas, Stats = stats,
                OverviewPreparation = preparation, RequestRefinedFrame = static () => { },
                ViewScale = scale, LodTier = phase == "fit" ? RenderLodTier.Overview : RenderLodTier.Exact,
                VisibleWorld = new(0, 0, 512 / scale, 512 / scale),
            };
            preparation.UpdateDocument(document, source, static () => { });
            (long Begin, long Render, long End, double Ms) Frame(double x, bool camera)
            {
                var canvas = surface.Canvas;
                var frameScale = phase == "wheel" && !camera ? 1 : scale;
                canvas.ResetMatrix(); canvas.Clear(SKColors.White);
                canvas.Scale((float)frameScale); canvas.Translate((float)-x, 0);
                var frameOptions = new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, AtlasCache = atlas, Stats = stats,
                    OverviewPreparation = preparation, RequestRefinedFrame = options.RequestRefinedFrame,
                    ViewScale = frameScale, LodTier = options.LodTier,
                    VisibleWorld = new(x, 0, x + 512 / frameScale, 512 / frameScale), CameraMoving = camera,
                };
                source.FrameMoving = camera;
                var started = Stopwatch.GetTimestamp();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var scope = source.BeginFrame(); cache.BeginFrame(); atlas.BeginFrame();
                var begin = GC.GetAllocatedBytesForCurrentThread();
                SceneRenderer.Render(canvas, document, frameOptions);
                canvas.Flush();
                var render = GC.GetAllocatedBytesForCurrentThread();
                atlas.EndFrame(); cache.EndFrame(); scope?.Dispose();
                var end = GC.GetAllocatedBytesForCurrentThread();
                return (begin - before, render - begin, end - render, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            var timeout = Stopwatch.StartNew();
            var settled = false;
            for (var i = 0; i < 1000 && timeout.Elapsed < TimeSpan.FromSeconds(30); i++)
            {
                Frame(0, false);
                if (stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && stats.ObjectsDrawn > 0
                    && (phase != "fit" || stats.OverviewExactLayer == 1) && preparation.Statistics.Idle)
                { settled = true; break; }
                Thread.Sleep(5);
            }
            Assert.True(settled, $"{phase} did not settle: pending={stats.DetailTilesPending}, unresolved={stats.UnresolvedImages}");
            for (var i = 0; i < 8; i++) Frame(0, false);
            var samples = new (long Begin, long Render, long End, double Ms)[9];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = Frame(phase == "pan-new-tiles" ? 513 + i * 2 : moving ? (i + 1) * 2 : 0, moving);
            var max = samples.Max(s => s.Begin + s.Render + s.End);
            Console.WriteLine($"ALLOC {phase} objects={count} scale={scale:F6}: bytes min={samples.Min(s => s.Begin + s.Render + s.End)} max={max}; begin={samples.Max(s => s.Begin)} render={samples.Max(s => s.Render)} end={samples.Max(s => s.End)} ms median={samples.Select(s => s.Ms).Order().ElementAt(4):F3} max={samples.Max(s => s.Ms):F3}; drawn={stats.ObjectsDrawn} pending={stats.DetailTilesPending}");
            var lastX = phase == "pan-new-tiles" ? 529 : moving ? 18 : 0;
            var lastView = new WorldBounds(lastX, 0, lastX + 512 / scale, 512 / scale);
            Console.WriteLine($"VISIBLE {phase}: {objects.Count(o => SceneGeometry.BoundsOf(o).Intersects(lastView))} stamps");
            Assert.True(stats.ObjectsDrawn > 0);
            Assert.Null(preparation.Statistics.Error);
            using var image = surface.Snapshot();
            using var pixels = SKBitmap.FromImage(image);
            Console.WriteLine($"PIXELS {phase} {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pixels.Bytes))}");
            var requests = objects.Select(o => (o.Stamp!.AssetSha256, o.Width, o.Height)).ToArray();
            var bytesBeforePrepare = GC.GetAllocatedBytesForCurrentThread();
            source.PrepareForPixelSizes(requests, static () => { });
            var prepareBytes = GC.GetAllocatedBytesForCurrentThread() - bytesBeforePrepare;
            Console.WriteLine($"COUNT PrepareForPixelSizes({count}) = {prepareBytes} bytes");
            if (phase is "settled" or "pan") Assert.True(prepareBytes < 4096, $"preflight allocated {prepareBytes} bytes");
            var limit = phase == "fit" ? 12 * 1024 : 4 * 1024;
            if (phase is not ("wheel" or "pan-new-tiles"))
                Assert.True(max < limit, $"{phase}: {max} managed bytes per frame (limit {limit})");
        }
        finally { Directory.Delete(root, true); }
    }
}
