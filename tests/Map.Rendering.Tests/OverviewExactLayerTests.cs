using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// Headless model of the live fit view (pack 4, 14.11): a 131 072 px page, small stamps of many distinct images
/// (the owner's mountains resized into one image per asset) and a share of large ones that stay exact objects at fit.
/// As the editor: a bounded mip source, background overview preparation, simulated time. Measured on the render
/// thread: the settled refinement frames at fit, a pan at fit and a wheel zoom-out to fit; then the settled fit
/// pixels with the exact layer against the direct draw (<see cref="SceneRenderer.OverviewExactLayers"/> off).
/// Without MAP_OVERVIEW_EXACT_BENCH it runs a small map and asserts; with it (objects,images, e.g. 2000000,16208)
/// it prints before/after for that size.
/// </summary>
[Collection("OverviewExactLayers")]
public sealed class OverviewExactLayerTests(ITestOutputHelper output)
{
    private const int Width = 1280, Height = 720;
    private const double Canvas = 131_072;

    [Fact]
    public void Fit_view_copies_the_exact_layer_and_keeps_the_settled_pixels()
    {
        var bench = Environment.GetEnvironmentVariable("MAP_OVERVIEW_EXACT_BENCH") is { Length: > 0 } text
            ? text.Split(',').Select(int.Parse).ToArray() : null;
        var (objects, images) = bench is null ? (120_000, 1_500) : (bench[0], bench[1]);
        var before = Run(objects, images, layers: false, bench is not null);
        var after = Run(objects, images, layers: true, bench is not null);
        output.WriteLine($"objects={objects} images={images} exact at fit={after.Exact}");
        foreach (var (name, b, a) in new[] { ("settled", before.Settled, after.Settled), ("pan", before.Pan, after.Pan), ("wheel-out", before.Wheel, after.Wheel) })
            output.WriteLine($"{name} frame: before {Describe(b)} | after {Describe(a)}");
        foreach (var (name, index) in new[] { ("settled", 0), ("pan", 1), ("wheel-out", 2) })
            output.WriteLine($"{name} exact objects: before {Describe(before.ExactMs[index])} | after {Describe(after.ExactMs[index])}");
        output.WriteLine($"settle frames: before {before.SettleFrames} after {after.SettleFrames}; layers started {after.Started}, copied 1:1 in {after.Copied}/{after.Settled.Count} settled frames");
        output.WriteLine($"log: before [{before.Log}] after [{after.Log}]");
        var (beforeMax, beforeDiffering, _) = Compare(before.Pixels, after.Pixels);
        output.WriteLine($"settled fit pixels against the before run (valid only if it settled: {before.Complete}): max {beforeMax}/255, {beforeDiffering} pixels differ");
        var (maxDiff, differing, histogram) = Compare(after.Direct!, after.Pixels);
        output.WriteLine($"settled fit pixels, layer against the direct draw of the same settled view: max channel difference {maxDiff}/255, {differing} of {after.Pixels.Length / 4} pixels differ (by 1: {histogram[1]}, 2: {histogram[2]}, 3: {histogram[3]}, more: {histogram[4]})");
        Assert.True(after.Complete, "the layer run did not settle");
        Assert.True(after.Exact >= 128, $"only {after.Exact} exact objects: the model does not reach the layer");
        Assert.Equal(after.Settled.Count, after.Copied);
        // Source-over into a transparent layer and then over the chunks rounds in another order than drawing each
        // stamp over the chunks: a channel moves by 1-2/255, by 3-4 only where many translucent edges overlap
        // (2 000 000 objects: 118 of 921 600 pixels). A different image level or a missing stamp would be far more.
        Assert.True(maxDiff <= 4 && (histogram[3] + histogram[4]) * 2_000L <= after.Pixels.Length / 4,
            $"settled pixels differ by {maxDiff}/255 ({histogram[3]} by 3, {histogram[4]} by more)");
    }

    private static string Describe(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? "-" : $"n={sorted.Count} p50={sorted[sorted.Count / 2]:F1} p95={sorted[(int)Math.Ceiling(sorted.Count * .95) - 1]:F1} max={sorted[^1]:F1} ms";
    }

    private static (int Max, int Pixels, int[] Histogram) Compare(byte[] a, byte[] b)
    {
        var max = 0; var pixels = 0; var histogram = new int[5];
        for (var i = 0; i < a.Length; i += 4)
        {
            var pixel = 0;
            for (var c = 0; c < 4; c++) pixel = Math.Max(pixel, Math.Abs(a[i + c] - b[i + c]));
            max = Math.Max(max, pixel);
            if (pixel > 0) pixels++;
            histogram[Math.Min(4, pixel)]++;
        }
        return (max, pixels, histogram);
    }

    private sealed record Result(List<double> Settled, List<double> Pan, List<double> Wheel, int SettleFrames, bool Complete,
        int Exact, int Started, int Copied, byte[] Pixels, string Log, List<double>[] ExactMs, byte[]? Direct);

    private static Result Run(int count, int distinct, bool layers, bool finalSettle)
    {
        var previous = SceneRenderer.OverviewExactLayers;
        SceneRenderer.OverviewExactLayers = layers;
        var disk = new DenseDetailTilesTests.MipDisk();
        try
        {
            var random = new Random(13013);
            var shas = Enumerable.Range(0, distinct).Select(i => Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i + 91_000)))).ToArray();
            // One resized image of an owner mountain per asset: as many distinct images as the live library.
            var records = shas.Select((sha, i) =>
            {
                var side = 400 + random.Next(1600);
                return new DenseDetailTilesTests.AssetShape(sha, side, (int)(side * (0.6 + random.NextDouble() * 0.8)), i % DenseDetailTilesTests.MipDisk.Fixtures);
            }).ToDictionary(r => r.Sha, StringComparer.OrdinalIgnoreCase);
            var items = new SceneObject[count];
            for (var i = 0; i < count; i++)
            {
                var record = records[shas[i % distinct]];
                // As the live grid: 32-128 px stamps, rotated, uniform over the page; 0.78 % are large enough
                // (900-2600 px) to stay exact at fit - 15 560 of two million, as in the live trace.
                var width = random.NextDouble() < .0078 ? 900 + random.NextDouble() * 1700 : 32 + random.NextDouble() * 96;
                items[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Ассет " + i,
                    random.NextDouble() * (Canvas - width), random.NextDouble() * (Canvas - width),
                    width, width * record.Height / record.Width, random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
                    { Stamp = new StampStyle(record.Sha) };
            }
            var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, items);
            var document = new MapDocument(DocumentId.New(), "exact layer", DateTimeOffset.UnixEpoch,
                new CanvasDefinition((int)Canvas, (int)Canvas, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"), layer])
                .WithAssets(shas.Select(sha => new MapAssetReference(sha, sha, "image/png", "png", ProjectAssetStorage.Linked,
                    DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)).ToArray());

            var log = new List<string>();
            var moving = false;
            using var tiles = new ObjectOverviewCache(1024, 256L << 20);
            using var atlas = new ObjectOverviewCache();
            using var preparation = new SceneRenderer.OverviewPreparation(() => Volatile.Read(ref moving));
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
            var source = new DenseDetailTilesTests.BoundedDerivedSource(records, disk, 512L << 20);
            preparation.UpdateDocument(document, source, static () => { });
            var wall = Stopwatch.StartNew();
            var lastStart = 0.0;
            var lastFrame = 16.7;
            RenderStats last = new();

            (bool Complete, double Ms) Frame(double cx, double cy, double scale, bool camMoving)
            {
                Volatile.Write(ref moving, camMoving);
                var c = surface.Canvas;
                c.ResetMatrix(); c.Clear(SKColors.White);
                var halfW = Width / 2 / scale; var halfH = Height / 2 / scale;
                c.Scale((float)scale); c.Translate((float)-(cx - halfW), (float)-(cy - halfH));
                var stats = new RenderStats();
                var started = Stopwatch.GetTimestamp();
                using (source.BeginFrame(lastFrame))
                {
                    tiles.BeginFrame(); atlas.BeginFrame();
                    try
                    {
                        SceneRenderer.Render(c, document, new RenderOptions
                        {
                            Stamps = source, ObjectCache = tiles, AtlasCache = atlas, Stats = stats, ViewScale = scale,
                            OverviewPreparation = preparation, CameraMoving = camMoving, FastTerrain = camMoving,
                            LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                            VisibleWorld = new(cx - halfW, cy - halfH, cx + halfW, cy + halfH),
                            RequestRefinedFrame = static () => { },
                        });
                        c.Flush();
                    }
                    finally { tiles.EndFrame(); atlas.EndFrame(); }
                }
                Assert.Null(preparation.Statistics.Error);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var now = wall.Elapsed.TotalMilliseconds;
                lastFrame = Math.Max(16.7, Math.Max(elapsed, now - lastStart));
                lastStart = now;
                last = stats;
                return (stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && !source.ImageRequestsPending, elapsed);
            }

            // Real time for the background workers (overview chunks, mips in simulated credit, exact layers).
            (bool Complete, int Frames) Settle(double cx, double cy, double scale)
            {
                var timeout = Stopwatch.StartNew();
                for (var frame = 1; timeout.Elapsed < TimeSpan.FromSeconds(60); frame++)
                {
                    if (Frame(cx, cy, scale, false).Complete) return (true, frame);
                    Thread.Sleep(5);
                }
                log.Add($"not settled at {scale:F4}: pending={last.DetailTilesPending} unresolved={last.UnresolvedImages} requests={source.ImageRequestsPending} chunks={last.OverviewChunks} layer={last.OverviewExactLayer} prep={preparation.Statistics.Queued}/{preparation.Statistics.ReadyImages}/idle={preparation.Statistics.Idle} adopted={last.OverviewAdoptions} deferred={last.OverviewAdoptionsDeferred} hits={last.ObjectCacheHits} misses={last.ObjectCacheMisses}");
                return (false, -1);
            }

            var centre = Canvas / 2;
            var fit = Height * .9 / Canvas; // 0.0049, as the live fit view
            var settle = Settle(centre, centre, fit);
            var exact = last.ExactObjects;
            // Refinement frames over the complete fit view (each one is what a refining frame redraws).
            var settled = new List<double>();
            List<double>[] exactMs = [[], [], []];
            var copied = 0;
            for (var frame = 0; frame < 60; frame++)
            {
                settled.Add(Frame(centre, centre, fit, false).Ms); exactMs[0].Add(last.OverviewExactMilliseconds);
                if (last.OverviewExactLayer == 1) copied++;
            }
            byte[] pixels;
            using (var snapshot = surface.Snapshot())
            using (var bitmap = SKBitmap.FromImage(snapshot)) pixels = bitmap.Bytes;
            // The reference: the same settled view drawn object by object, once every image it samples is resident.
            byte[]? direct = null;
            if (layers)
            {
                SceneRenderer.OverviewExactLayers = false;
                var reference = Settle(centre, centre, fit);
                SceneRenderer.OverviewExactLayers = true;
                log.Add($"direct reference settled={reference.Complete}");
                using var snapshot = surface.Snapshot();
                using var bitmap = SKBitmap.FromImage(snapshot);
                direct = bitmap.Bytes;
            }
            var started = 0;
            var kinds = new int[3];
            var directMs = 0.0;
            // A pan at fit: a quarter of the view over two seconds.
            var pan = new List<double>();
            for (var frame = 0; frame < 120; frame++)
            {
                pan.Add(Frame(centre + frame * (Width / 4.0 / fit) / 120, centre, fit, true).Ms); exactMs[1].Add(last.OverviewExactMilliseconds);
                started += last.OverviewExactLayerStarted; kinds[last.OverviewExactLayer]++;
                Thread.Sleep(5);
            }
            log.Add($"pan frames direct/1:1/stand-in={kinds[0]}/{kinds[1]}/{kinds[2]}");
            kinds = new int[3];
            Settle(centre, centre, fit);
            // Wheel out to fit from eight times closer (still the overview): six moving frames per notch.
            var wheel = new List<double>();
            var scale = fit * 8;
            Settle(centre, centre, scale);
            while (scale > fit * 1.0001)
            {
                scale = Math.Max(fit, scale / Math.Pow(2, .25));
                for (var f = 0; f < 6; f++) { wheel.Add(Frame(centre, centre, scale, true).Ms); exactMs[2].Add(last.OverviewExactMilliseconds); started += last.OverviewExactLayerStarted; kinds[last.OverviewExactLayer]++; if (last.OverviewExactLayer == 0) directMs += last.OverviewExactMilliseconds; Thread.Sleep(5); }
            }
            log.Add($"wheel frames direct/1:1/stand-in={kinds[0]}/{kinds[1]}/{kinds[2]} directMs={directMs:F0}");
            // Diagnostics only: after this wheel-out the fit view does not complete with or without the layer
            // (16 overview rasters wait in the preparation handoff that no frame takes), so the default run skips it.
            if (finalSettle) log.Add($"final settle={Settle(centre, centre, fit).Complete}");
            return new Result(settled, pan, wheel, settle.Frames, settle.Complete, exact, started, copied, pixels, string.Join("; ", log), exactMs, direct);
        }
        finally
        {
            SceneRenderer.OverviewExactLayers = previous;
            disk.Dispose();
        }
    }
}
