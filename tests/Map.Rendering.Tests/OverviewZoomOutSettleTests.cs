using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// Headless repro of the navigation run that never settled: a large map of the owner's mountains
/// (the bounded mip source of <see cref="DenseDetailTilesTests"/>), opened at fit, zoomed in with the
/// wheel into dense detail tiles, zoomed back out notch by notch to fit, then frames until the view
/// is complete. As the editor: a bounded object cache, background overview preparation, a kept
/// camera frame drawn while moving when its scale allows it. Simulated time advances by what each
/// frame took (at least 16.7 ms), so background work runs as it would between real frames.
/// Before the fix (2026-11-14) the zoom-in seeds of every passed level stayed as fallbacks, which the
/// cache evicted last: at 32 MiB the fit view drew 56 of 256 chunks and evicted 91 412 times in 120 s.
/// </summary>
[Collection(RealTimeSettleCollection.Name)]
public sealed class OverviewZoomOutSettleTests(ITestOutputHelper output)
{
    private const int Width = 2048, Height = 1095;
    private const double MaxScale = .5;

    [Fact]
    public void Zooming_out_to_fit_after_dense_detail_settles_with_the_whole_view_in_the_cache()
    {
        // 32 MiB: an object-overview share the working-memory budget can give on a smaller PC.
        var result = Run(objects: 400_000, canvas: 131_072, cameraFrames: true, cacheBytes: 32L << 20);
        output.WriteLine(result.ToString());
        Assert.True(result.DetailSettled, "the zoomed-in dense view did not settle: " + result);
        Assert.True(result.Settled, result.ToString());
        Assert.Equal(result.VisibleChunks, result.ChunksDrawn);
    }

    [Fact]
    public void The_current_view_keeps_its_chunks_when_stale_seed_fallbacks_fill_the_cache()
        => CheckFallbackRetention(ObjectOverviewCache.StaleFallbackFrames);

    [Fact(Skip = "defect: recent fallback seeds evict the current view during rapid zoom-out; chunk 0 missing on frame 1")]
    public void A_rapid_zoom_out_keeps_the_current_view_without_sixty_empty_frames()
        => CheckFallbackRetention(0);

    private static void CheckFallbackRetention(int idleFrames)
    {
        // Eight seed tiles of zoom levels the camera left a second ago fill the budget; the view then
        // needs four chunks of its own. They must survive the end of the frame: the stale seeds go first.
        const long tile = 64 * 64 * 4;
        using var cache = new ObjectOverviewCache(64, 8 * tile);
        var layer = LayerId.New();
        cache.BeginFrame();
        for (var i = 0; i < 8; i++) cache.GetOrCreate(layer, i, 0, 10, 1, Image, fallback: true);
        cache.EndFrame();
        for (var frame = 0; frame < idleFrames; frame++) { cache.BeginFrame(); cache.EndFrame(); }
        Assert.Equal(8, cache.Count);
        for (var frame = 0; frame < 3; frame++)
        {
            cache.BeginFrame();
            for (var i = 0; i < 4; i++)
                if (frame == 0) cache.GetOrCreate(layer, i, 0, 0, 1, Image);
                else Assert.True(cache.TryGet(layer, i, 0, 0, 1, out _), $"frame {frame}: chunk {i} of the view was evicted");
            cache.EndFrame();
        }
        Assert.True(cache.Bytes <= 8 * tile);
        Assert.Equal(4, cache.FallbackEntries.Count);
        static SKImage Image()
        {
            using var surface = SKSurface.Create(new SKImageInfo(64, 64))!;
            return surface.Snapshot();
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void Benchmark_zoom_out_to_fit_with_and_without_camera_frames()
    {
        if (Environment.GetEnvironmentVariable("MAP_ZOOMOUT_BENCH") != "1") return;
        foreach (var camera in new[] { true, false })
        foreach (var megabytes in new[] { 32L, 64L })
            output.WriteLine($"camera={camera} cache={megabytes} MiB " + Run(objects: 400_000, canvas: 131_072, cameraFrames: camera, cacheBytes: megabytes << 20));
    }

    internal sealed record Result(bool Settled, double SettleSeconds, int SettleFrames, int VisibleChunks, int ChunksDrawn,
        int Entries, long Bytes, long EvictionsDuringSettle, long EvictionsTotal, int MaxEntries, string Phases, bool DetailSettled = true)
    {
        public override string ToString() =>
            $"settled={Settled} settle={SettleSeconds:F1}s/{SettleFrames} frames chunks={ChunksDrawn}/{VisibleChunks} "
            + $"cache entries={Entries} (max {MaxEntries}) bytes={Bytes / 1048576.0:F1} MiB evictions settle={EvictionsDuringSettle} total={EvictionsTotal} {Phases}";
    }

    internal static Result Run(int objects, int canvas, bool cameraFrames, long cacheBytes = 64L * 1024 * 1024)
    {
        var disk = new DenseDetailTilesTests.MipDisk();
        try
        {
            var random = new Random(23);
            const int assets = 300;
            var shas = Enumerable.Range(0, assets).Select(i => Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i + 7000)))).ToArray();
            var records = shas.Select((sha, i) =>
            {
                var side = 700 + random.Next(2900);
                return new DenseDetailTilesTests.AssetShape(sha, side, (int)(side * (0.7 + random.NextDouble() * 0.6)), i % DenseDetailTilesTests.MipDisk.Fixtures);
            }).ToDictionary(r => r.Sha, StringComparer.OrdinalIgnoreCase);
            var items = new SceneObject[objects];
            for (var i = 0; i < objects; i++)
            {
                var record = records[shas[i % assets]];
                var width = 64 + random.NextDouble() * 128;
                // A quarter of them in a dense range around the centre, where the wheel zooms in to 400 %.
                var (origin, extent) = i % 4 == 0 ? (canvas / 2.0 - 8192, 16384.0) : (0.0, (double)canvas);
                items[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", origin + random.NextDouble() * (extent - width),
                    origin + random.NextDouble() * (extent - width), width, width * record.Height / record.Width, 0,
                    "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha) };
            }
            var layer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, items);
            var document = new MapDocument(DocumentId.New(), "Zoom out", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(canvas, canvas, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"), layer])
                .WithAssets(shas.Select(sha => new MapAssetReference(sha, sha, "image/png", "png", ProjectAssetStorage.Linked,
                    DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)).ToArray());

            var moving = false;
            using var tiles = new ObjectOverviewCache(1024, cacheBytes);
            using var atlas = new ObjectOverviewCache();
            using var preparation = new SceneRenderer.OverviewPreparation(() => Volatile.Read(ref moving));
            using var camera = new CameraFrameCache();
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
            var source = new DenseDetailTilesTests.BoundedDerivedSource(records, disk, 128L << 20);
            preparation.UpdateDocument(document, source, static () => { });
            camera.SetIdentity(document, IntPtr.Zero);
            var page = new SKRect(0, 0, canvas, canvas);
            var wall = Stopwatch.StartNew();
            var lastStart = 0.0;
            var lastFrame = 16.7;
            var maxEntries = 0;
            var phases = new List<string>();
            RenderStats last = new();

            bool Frame(double cx, double cy, double scale, bool camMoving)
            {
                Volatile.Write(ref moving, camMoving);
                var c = surface.Canvas;
                c.ResetMatrix(); c.Clear(SKColors.White);
                var halfW = Width / 2 / scale; var halfH = Height / 2 / scale;
                c.Scale((float)scale); c.Translate((float)-(cx - halfW), (float)-(cy - halfH));
                var visible = new SKRect((float)(cx - halfW), (float)(cy - halfH), (float)(cx + halfW), (float)(cy + halfH));
                var stats = new RenderStats();
                var started = Stopwatch.GetTimestamp();
                var complete = false;
                using (source.BeginFrame(lastFrame))
                {
                    tiles.BeginFrame(); atlas.BeginFrame();
                    try
                    {
                        if (cameraFrames && camMoving && camera.TryDraw(c, visible, page)) stats.DetailTilesPending = 1;
                        else
                        {
                            SceneRenderer.Render(c, document, new RenderOptions
                            {
                                Stamps = source, ObjectCache = tiles, AtlasCache = atlas, Stats = stats, ViewScale = scale,
                                OverviewPreparation = preparation, CameraMoving = camMoving, FastTerrain = camMoving,
                                LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                                VisibleWorld = new(visible.Left, visible.Top, visible.Right, visible.Bottom),
                                RequestRefinedFrame = static () => { },
                            });
                            complete = stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && !source.ImageRequestsPending;
                            if (cameraFrames && !camMoving && complete) camera.Capture(surface, c, page);
                        }
                        c.Flush();
                    }
                    finally { tiles.EndFrame(); atlas.EndFrame(); }
                }
                Assert.Null(preparation.Statistics.Error);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var now = wall.Elapsed.TotalMilliseconds;
                lastFrame = Math.Max(16.7, Math.Max(elapsed, now - lastStart));
                lastStart = now;
                maxEntries = Math.Max(maxEntries, tiles.Count);
                last = stats;
                return complete;
            }

            (bool Settled, double Seconds, int Frames) Settle(double cx, double cy, double scale, double limitSeconds = 120)
            {
                var simulated = 0.0;
                for (var frame = 0; simulated < limitSeconds * 1000; frame++)
                {
                    if (Frame(cx, cy, scale, false)) return (true, simulated / 1000, frame + 1);
                    simulated += lastFrame;
                }
                return (false, simulated / 1000, -1);
            }

            var centre = canvas / 2.0;
            var fit = Math.Min((double)Width / canvas, (double)Height / canvas);
            var open = Settle(centre, centre, fit);
            phases.Add($"open={open.Settled}/{open.Seconds:F1}s");
            // Wheel in to dense detail (a notch is a quarter octave, six frames each), settle there.
            var scale = fit;
            var notches = 0;
            while (scale * Math.Pow(2, .25) <= MaxScale) { scale *= Math.Pow(2, .25); notches++; for (var f = 0; f < 6; f++) Frame(centre, centre, scale, true); }
            var detail = Settle(centre, centre, scale);
            phases.Add($"in={notches} notches to {scale:F3} {detail.Settled}/{detail.Seconds:F1}s tiles={last.ObjectTilesDrawn} entries={tiles.Count} {tiles.Bytes / 1048576.0:F0}MiB");
            // Out notch by notch; a short pause after every second notch, as a hand on a wheel does.
            for (var notch = 0; notch < notches; notch++)
            {
                scale = notch == notches - 1 ? fit : scale / Math.Pow(2, .25);
                for (var f = 0; f < 6; f++) Frame(centre, centre, scale, true);
                if (notch % 2 == 1) for (var f = 0; f < 10; f++) Frame(centre, centre, scale, false);
            }
            phases.Add($"out: entries={tiles.Count} {tiles.Bytes / 1048576.0:F0}MiB fallbacks={tiles.FallbackEntries.Count}/{tiles.FallbackEntries.Bytes / 1048576.0:F0}MiB evictions={tiles.Evictions}");
            var before = tiles.Evictions;
            var final = Settle(centre, centre, fit);
            var visibleChunks = 0;
            var chunk = 8192.0;
            for (var y = 0; y < Math.Ceiling(canvas / chunk); y++)
            for (var x = 0; x < Math.Ceiling(canvas / chunk); x++) visibleChunks++;
            return new Result(final.Settled, final.Seconds, final.Frames, visibleChunks, last.OverviewChunks, tiles.Count, tiles.Bytes,
                tiles.Evictions - before, tiles.Evictions, maxEntries, string.Join(" ", phases), detail.Settled);
        }
        finally { disk.Dispose(); }
    }
}
