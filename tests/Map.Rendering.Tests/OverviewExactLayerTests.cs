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
    public void Exact_layers_do_not_delay_visible_readiness()
    {
        var previousQueue = SceneRenderer.DetailRasterQueue;
        using var queue = new SceneRenderer.DetailWorkQueue(2);
        SceneRenderer.DetailRasterQueue = queue;
        try
        {
            // Run this comparison with DOTNET_PROCESSOR_COUNT=4 as well: two detail workers and two
            // BackgroundWork slots, the budget of the owner's four-core machine.
            Run(20_000, 100, false, false, readinessOnly: true);
            Run(20_000, 100, true, false, readinessOnly: true);
            var off = new List<double>(); var on = new List<double>();
            for (var pair = 0; pair < 5; pair++)
                foreach (var layers in pair % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    var result = Run(120_000, 1500, layers, false, readinessOnly: true);
                    Assert.True(result.Complete);
                    (layers ? on : off).Add(result.ReadyMs);
                }
            output.WriteLine($"CPU count={Environment.ProcessorCount}; detail workers=2; cold settled wall: layers off {Describe(off)} | on {Describe(on)}");
            var ratio = on.Order().ElementAt(2) / off.Order().ElementAt(2);
            output.WriteLine($"readiness on/off median ratio={ratio:F3}, worst ratio={on.Max() / off.Max():F3}");
            Assert.True(ratio <= 1.25, $"exact layers delay readiness by {(ratio - 1) * 100:F1}%");
            Assert.True(on.Max() <= off.Max() * 1.25, "exact layers delay the worst measured readiness by more than 25%");
        }
        finally { SceneRenderer.DetailRasterQueue = previousQueue; }
    }

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
        output.WriteLine($"zoom stop pixels against synchronous direct: transformed {after.ZoomBefore}/255 -> settled {after.ZoomAfter}/255; {after.ZoomFrames} refinement frames, {after.ZoomMs:F1} ms");
        Assert.True(after.Complete, "the layer run did not settle");
        Assert.True(after.Exact >= 128, $"only {after.Exact} exact objects: the model does not reach the layer");
        Assert.Equal(after.Settled.Count, after.Copied);
        Assert.True(after.ZoomAfter <= 4, $"settled zoom pixels differ by {after.ZoomAfter}/255");
        foreach (var edit in before.Edits)
            output.WriteLine($"direct {edit.Name}: frames={edit.Frames.Count}; max exact={edit.Frames.Max(f => f.Ms):F2} ms; worst direct={edit.Frames.Max(f => f.Direct)}; pixels max={edit.MaxDifference}/255");
        foreach (var edit in after.Edits)
        {
            var worst = edit.Frames.MaxBy(f => f.Ms)!;
            output.WriteLine($"{edit.Name}: settled={edit.Complete}, kinds={string.Join(',', edit.Frames.Select(f => f.Kind))}; max exact={worst.Ms:F2} ms ({worst.Direct} direct objects); worst direct={edit.Frames.Max(f => f.Direct)}; pixels max={edit.MaxDifference}/255; resident patch pixels max={edit.PatchDifference}/255 ({edit.Patches} frames)");
        }
        foreach (var edit in after.Edits)
        {
            Assert.True(edit.Complete, $"{edit.Name} did not settle");
            Assert.True(edit.Frames.Max(f => f.Direct) * 4 <= after.Exact,
                $"{edit.Name} drew {edit.Frames.Max(f => f.Direct)} of {after.Exact} exact objects directly");
            Assert.Contains(edit.Frames, frame => frame.Kind == 3);
            Assert.True(edit.PatchDifference <= 4, $"{edit.Name}: patch pixels differ by {edit.PatchDifference}/255");
            if (edit.Name is "move-large" or "delete-large") Assert.True(edit.Patches > 0, $"{edit.Name}: no resident patch checked");
            Assert.True(edit.MaxDifference <= 4, $"{edit.Name}: edited pixels differ by {edit.MaxDifference}/255");
            Assert.True(edit.ChangedPixels > 0, $"{edit.Name}: edited objects never reached the settled pixels");
        }
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
        int Exact, int Started, int Copied, byte[] Pixels, string Log, List<double>[] ExactMs, byte[]? Direct)
    {
        public List<EditResult> Edits { get; init; } = [];
        public int ZoomBefore { get; init; }
        public int ZoomAfter { get; init; }
        public int ZoomFrames { get; init; }
        public double ZoomMs { get; init; }
        public double ReadyMs { get; init; }
    }

    private sealed record EditFrame(int Kind, double Ms, int Direct);
    private sealed record EditResult(string Name, bool Complete, List<EditFrame> Frames, int MaxDifference, int ChangedPixels, int PatchDifference, int Patches);

    private static Result Run(int count, int distinct, bool layers, bool finalSettle, bool readinessOnly = false)
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
            var edits = new List<EditResult>();
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
            byte[] Pixels()
            {
                using var snapshot = surface.Snapshot();
                using var bitmap = SKBitmap.FromImage(snapshot);
                return bitmap.Bytes;
            }

            (bool Complete, double Ms) Frame(double cx, double cy, double scale, bool camMoving, bool synchronous = false)
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
                            OverviewPreparation = synchronous ? null : preparation, CameraMoving = camMoving, FastTerrain = camMoving,
                            LodTier = scale < .125 ? RenderLodTier.Overview : RenderLodTier.Exact,
                            VisibleWorld = new(cx - halfW, cy - halfH, cx + halfW, cy + halfH),
                            RequestRefinedFrame = synchronous ? null : static () => { },
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
            (bool Complete, int Frames) Settle(double cx, double cy, double scale, List<EditFrame>? frames = null,
                bool synchronous = false, List<byte[]>? patches = null)
            {
                var timeout = Stopwatch.StartNew();
                for (var frame = 1; timeout.Elapsed < TimeSpan.FromSeconds(60); frame++)
                {
                    var rendered = Frame(cx, cy, scale, false, synchronous);
                    frames?.Add(new(last.OverviewExactLayer, last.OverviewExactMilliseconds,
                        last.OverviewExactLayer == 0 ? last.ExactObjects : last.OverviewExactPatched));
                    // One resident patch per edit is enough to check its clip; copying every frame's 3.5 MB
                    // bitmap would add collection pauses to the very frame timings this test measures.
                    if (patches is { Count: 0 } && last.OverviewExactLayer == 3
                        && last.UnresolvedImages == 0 && !source.ImageRequestsPending)
                        patches?.Add(Pixels());
                    if (rendered.Complete) return (true, frame);
                    Thread.Sleep(5);
                }
                log.Add($"not settled at {scale:F4}: pending={last.DetailTilesPending} unresolved={last.UnresolvedImages} requests={source.ImageRequestsPending} chunks={last.OverviewChunks} layer={last.OverviewExactLayer} prep={preparation.Statistics.Queued}/{preparation.Statistics.ReadyImages}/idle={preparation.Statistics.Idle} adopted={last.OverviewAdoptions} deferred={last.OverviewAdoptionsDeferred} hits={last.ObjectCacheHits} misses={last.ObjectCacheMisses}");
                return (false, -1);
            }

            var centre = Canvas / 2;
            var fit = Height * .9 / Canvas; // 0.0049, as the live fit view
            var settle = Settle(centre, centre, fit);
            var exact = last.ExactObjects;
            if (readinessOnly)
                return new([], [], [], settle.Frames, settle.Complete, exact, 0, 0, [], string.Join("; ", log), [[], [], []], null)
                    { ReadyMs = wall.Elapsed.TotalMilliseconds };
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
            // An edit on the complete fit view: ten large stamps placed near the centre (pack-5 live A/B 2026-11-15:
            // place-10 p95 18 -> 378 ms, the settled frame drew every exact object one by one until the new layer landed).
            {
                Settle(centre, centre, fit);
                var placed = Enumerable.Range(0, 10).Select(i =>
                {
                    var record = records[shas[i]];
                    var width = 1200.0;
                    return new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Новый " + i, centre + i * 2500 - 12_000, centre - 4000 + i * 700,
                        width, width * record.Height / record.Width, 15 * i, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha) };
                }).ToArray();
                void Edit(string name, Map.Core.Editing.IEditCommand command)
                {
                    Assert.True(Settle(centre, centre, fit).Complete);
                    var oldPixels = Pixels();
                    document = command.Apply(document);
                    preparation.UpdateDocument(document, source, static () => { });
                    var frames = new List<EditFrame>();
                    var patches = new List<byte[]>();
                    var edited = Settle(centre, centre, fit, frames, patches: patches);
                    var editedPixels = Pixels();
                    var reference = Settle(centre, centre, fit, synchronous: true);
                    Assert.True(reference.Complete, "edited synchronous reference did not settle");
                    var directPixels = Pixels();
                    edits.Add(new(name, edited.Complete, frames, Compare(editedPixels, directPixels).Max,
                        Compare(oldPixels, editedPixels).Pixels, patches.Count == 0 ? 0 : patches.Max(p => Compare(p, directPixels).Max), patches.Count));
                }
                Edit("place-10", new Map.Core.Editing.AddObjectsCommand(layer.Id, placed));
                // A large rotated stamp crosses several chunk boundaries. Move it far enough that the old
                // and new patches are disjoint; deletion must erase all its old pixels too.
                var large = new SceneObject(placed[0].Id, SceneObjectKind.Stamp, "Large", placed[0].X, placed[0].Y,
                    12_000, 9000, 43, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = placed[0].Stamp };
                Edit("enlarge", new Map.Core.Editing.TransformObjectCommand(placed[0], large));
                var moved = new SceneObject(large.Id, SceneObjectKind.Stamp, "Large", large.X + 42_000, large.Y - 32_000,
                    large.Width, large.Height, large.RotationDegrees, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = large.Stamp };
                Edit("move-large", new Map.Core.Editing.TransformObjectCommand(large, moved));
                Edit("delete-large", new Map.Core.Editing.DeleteObjectsCommand(document, [moved.Id]));
            }
            var started = 0;
            var kinds = new int[4];
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
            kinds = new int[4];
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
            // Stop between bucket scales: the old raster must remain only a temporary stand-in. The completed
            // view must be rastered at this exact screen matrix, never a stretched bucket-scale layer.
            var stopScale = fit * 1.037;
            Frame(centre, centre, stopScale, true);
            Frame(centre, centre, stopScale, false);
            var zoomBefore = Pixels();
            var zoomWall = Stopwatch.StartNew();
            var zoomSettle = Settle(centre, centre, stopScale);
            var zoomMs = zoomWall.Elapsed.TotalMilliseconds;
            Assert.True(zoomSettle.Complete, "stopped zoom did not settle");
            if (layers) Assert.Equal(1, last.OverviewExactLayer);
            var zoomAfter = Pixels();
            Assert.True(Settle(centre, centre, stopScale, synchronous: true).Complete);
            var zoomDirect = Pixels();
            if (finalSettle) log.Add($"final settle={Settle(centre, centre, fit).Complete}");
            return new Result(settled, pan, wheel, settle.Frames, settle.Complete, exact, started, copied, pixels, string.Join("; ", log), exactMs, direct)
                { Edits = edits, ZoomBefore = Compare(zoomBefore, zoomDirect).Max, ZoomAfter = Compare(zoomAfter, zoomDirect).Max,
                    ZoomFrames = zoomSettle.Frames, ZoomMs = zoomMs };
        }
        finally
        {
            SceneRenderer.OverviewExactLayers = previous;
            disk.Dispose();
        }
    }
}
