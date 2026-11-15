using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Rendering.Tests;

/// <summary>
/// A dense small canvas (8192 px) with many objects over ~1800 distinct images, drawn through a
/// bounded image source that prepares mips off the frame like the derived store: a limited
/// number of finished results may wait for the frame, the frame adopts them under its own budget
/// and the resident shelf is a byte-bounded LRU. The settled frame must equal the synchronous one.
/// </summary>
[Collection(RealTimeSettleCollection.Name)]
public sealed class DenseDetailTilesTests(ITestOutputHelper output)
{
    private const int Width = 2048, Height = 1095;

    [Fact]
    public void Dense_detail_tiles_settle_with_bounded_frames_and_equal_the_synchronous_frame()
    {
        // Small enough for every test run; the benchmark below uses the live sizes.
        var result = Run(objects: 24_000, assets: 300, canvas: 4096, simulatedSeconds: 60, residentBytes: 128L << 20);
        Assert.True(result.Settled, result.ToString());
        Assert.True(result.PixelsEqual, "The settled frame must equal the synchronous frame.");
        Assert.Equal(0, result.NeededEvictions);
    }

    [Fact]
    public void A_working_set_larger_than_the_shelf_still_makes_progress_tile_by_tile()
    {
        // The shelf holds a fraction of what the whole view samples: drawing must advance per tile
        // and never evict the pixels of the tile being drawn.
        var result = Run(objects: 24_000, assets: 300, canvas: 4096, simulatedSeconds: 120, residentBytes: 3L << 20, clustered: true);
        output.WriteLine(result.ToString());
        Assert.True(result.Settled, result.ToString());
        Assert.True(result.PixelsEqual, "The settled frame must equal the synchronous frame.");
        Assert.True(result.RequestedBytes > result.ResidentBudget, "The view must sample more than the shelf holds.");
    }

    [Fact]
    public async Task A_read_queued_past_the_end_of_a_run_fails_cleanly_instead_of_crashing_the_host()
    {
        // The harness disposes its mip disk in a finally while reads of the last frames are still queued.
        var disk = new MipDisk();
        using var gate = new ManualResetEventSlim();
        var late = Task.Run(() => { gate.Wait(); return disk.Read(0, 97, 61); });
        disk.Dispose();
        gate.Set();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => late);
    }

    public static TheoryData<int> DenseSizes => new() { 250_000, 1_000_000 };

    [Theory]
    [MemberData(nameof(DenseSizes))]
    [Trait("Category", "Benchmark")]
    public void Benchmark_dense_8192(int objects)
    {
        if (Environment.GetEnvironmentVariable("MAP_DENSE_BENCH") != "1") return;
        var result = Run(objects, assets: 1804, canvas: 8192, simulatedSeconds: 180, residentBytes: 128L << 20, pan: true);
        output.WriteLine(result.ToString());
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "map-dense-bench.txt"), result + Environment.NewLine);
    }

    internal sealed record Result(int Objects, bool Settled, double SettleSimulatedSeconds, int Frames, double FrameP99, double FrameMax,
        double FrameMedian, long RequestedKeys, long RequestedBytes, long ResidentBudget, long NeededEvictions, long Evictions,
        long Adopted, int TilesExact, bool PixelsEqual, double PanP99, double PanMax, double PreflightMsTotal, long PreflightItems,
        double ReferenceSeconds, long Refetched, string Diagnostics = "")
    {
        public override string ToString() =>
            $"objects={Objects} settled={Settled} settle={SettleSimulatedSeconds:F1}s frames={Frames} frame p50/p99/max={FrameMedian:F1}/{FrameP99:F1}/{FrameMax:F1} ms "
            + $"requestedKeys={RequestedKeys} requestedBytes={RequestedBytes / 1024.0 / 1024:F1} MiB shelf={ResidentBudget / 1024.0 / 1024:F1} MiB "
            + $"neededEvictions={NeededEvictions} evictions={Evictions} adopted={Adopted} exactTiles={TilesExact} equal={PixelsEqual} "
            + $"pan p99/max={PanP99:F1}/{PanMax:F1} ms preflight={PreflightMsTotal:F0} ms/{PreflightItems} items reference={ReferenceSeconds:F1}s refetched={Refetched} {Diagnostics}";
    }

    internal static Result Run(int objects, int assets, int canvas, double simulatedSeconds, long residentBytes, bool pan = false, bool clustered = false)
    {
        var disk = new MipDisk();
        try
        {
            var (records, document) = Scene(objects, assets, canvas, clustered);
            var scale = Math.Min((double)Width / canvas, (double)Height / canvas);
            var offsetX = -(Width / scale - canvas) / 2;

            using var progressiveTiles = new ObjectOverviewCache();
            using var progressiveAtlas = new ObjectOverviewCache();
            // As the editor: overview chunks of cold areas are prepared off the frame.
            using var overviewPreparation = new SceneRenderer.OverviewPreparation();
            var oldStore = Environment.GetEnvironmentVariable("MAP_DENSE_STORE") == "old";
            var source = new BoundedDerivedSource(records, disk, residentBytes) { LargeByOutput = !oldStore, SmallWaiting = oldStore ? 8 : 64,
                Background = Environment.GetEnvironmentVariable("MAP_DENSE_BACKGROUND") != "0" };
            var wall = Stopwatch.StartNew();
            var lastStart = 0.0;
            var frames = new List<double>();
            var other = new List<double>();
            var worst = new List<(double Total, double Build, double Gc, double Overview, int Frame)>();
            var first = 0.0;
            var phases = new Dictionary<int, string>();
            int started = 0, adoptedTiles = 0, onFrame = 0, waitingFrames = 0;
            var breakdown = new List<(double NonBuild, double Preflight, double Begin, double Overview)>();
            var simulated = 0.0;
            var settledAt = -1.0;
            byte[]? settled = null;
            var exactTiles = 0;
            var lastFrame = 16.7;
            for (var frame = 0; simulated < simulatedSeconds * 1000; frame++)
            {
                var stats = new RenderStats();
                var preflightBefore = source.PreflightMilliseconds;
                var pauseBefore = GC.GetTotalPauseDuration().TotalMilliseconds;
                var pixels = Draw(source, progressiveTiles, progressiveAtlas, stats, refine: true, offsetX, scale, document, lastFrame, out var elapsed,
                    overview: overviewPreparation);
                var build = stats.DetailRasterMilliseconds + stats.DetailPrepareMilliseconds;
                started += stats.DetailRastersStarted; adoptedTiles += stats.DetailRastersAdopted; onFrame += stats.DetailTilesBuiltOnFrame;
                if (stats.DetailRastersWaiting > 0) waitingFrames++;
                if (frame == 0) first = elapsed; else { frames.Add(elapsed); other.Add(elapsed - build); }
                breakdown.Add((elapsed - build, source.PreflightMilliseconds - preflightBefore, source.LastBeginFrameMilliseconds, stats.OverviewPlanMilliseconds + stats.OverviewPaletteMilliseconds + stats.OverviewVariantMilliseconds + stats.OverviewRasterMilliseconds));
                var pause = GC.GetTotalPauseDuration().TotalMilliseconds - pauseBefore;
                phases[frame] = $"ready {stats.DetailReadyMilliseconds:F0} loop {stats.DetailLoopMilliseconds:F0} cold {stats.DetailColdMilliseconds:F0} seed {stats.DetailSeedMilliseconds:F0} objects {stats.ObjectsMilliseconds:F0} terrain {stats.TerrainMilliseconds:F0} begin {source.LastBeginFrameMilliseconds:F0} end {LastEndMilliseconds:F0}";
                worst.Add((elapsed, build, pause, stats.OverviewPlanMilliseconds + stats.OverviewPaletteMilliseconds
                    + stats.OverviewVariantMilliseconds + stats.OverviewRasterMilliseconds, frame));
                exactTiles = Math.Max(exactTiles, stats.ObjectTilesDrawn);
                // Background rasters run in real time: a frame advances the clock by what the wall clock saw.
                var now = wall.Elapsed.TotalMilliseconds;
                lastFrame = Math.Max(16.7, Math.Max(elapsed, now - lastStart));
                lastStart = now;
                simulated += lastFrame;
                if (stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && !source.ImageRequestsPending)
                {
                    settledAt = simulated / 1000;
                    settled = pixels;
                    break;
                }
            }
            var panFrames = new List<double>();
            if (pan && settled is not null)
                for (var i = 0; i < 60; i++)
                {
                    Draw(source, progressiveTiles, progressiveAtlas, new RenderStats(), refine: true,
                        offsetX + (i < 30 ? -i : i - 60) * 3 / scale, scale, document, 16.7, out var panned, fastTerrain: true,
                        overview: overviewPreparation);
                    panFrames.Add(panned);
                }
            var equal = false;
            var difference = "";
            var referenceStarted = Stopwatch.GetTimestamp();
            if (settled is not null)
            {
                using var referenceTiles = new ObjectOverviewCache();
                using var referenceAtlas = new ObjectOverviewCache();
                var synchronous = new BoundedDerivedSource(records, disk, long.MaxValue) { Synchronous = true };
                var expectedPixels = Draw(synchronous, referenceTiles, referenceAtlas, new RenderStats(), refine: false,
                    offsetX, scale, document, 16.7, out _);
                equal = settled.AsSpan().SequenceEqual(expectedPixels);
                if (!equal)
                {
                    var differing = 0; var largest = 0; var firstAt = -1;
                    for (var i = 0; i < settled.Length; i++)
                        if (settled[i] != expectedPixels[i])
                        {
                            differing++; largest = Math.Max(largest, Math.Abs(settled[i] - expectedPixels[i]));
                            if (firstAt < 0) firstAt = i / 4;
                        }
                    difference = $" diff bytes={differing} max={largest} first=({firstAt % Width},{firstAt / Width})";
                }
            }
            var reference = Stopwatch.GetElapsedTime(referenceStarted).TotalSeconds;
            static double Percentile(List<double> values, double p) => values.Count == 0 ? 0
                : values.Order().ElementAt(Math.Min(values.Count - 1, (int)Math.Ceiling(p * values.Count) - 1));
            return new Result(objects, settledAt >= 0, settledAt >= 0 ? settledAt : simulated / 1000, frames.Count,
                Percentile(frames, .99), frames.Max(), Percentile(frames, .5), source.RequestedKeys, source.RequestedBytes, residentBytes,
                source.NeededEvictions, source.Evictions, source.Adopted, exactTiles, equal, Percentile(panFrames, .99),
                panFrames.Count == 0 ? 0 : panFrames.Max(), source.PreflightMilliseconds, source.PreflightItems, reference, source.Refetched,
                difference + $" rasters started={started} adopted={adoptedTiles} onFrame={onFrame} waitingFrames={waitingFrames} background={source.BackgroundRasters} working={source.WorkingLookups} lateMisses={source.LateMisses} maxPlan={source.MaxPlanBytes} prepares={source.PlanPrepares} "
                + $"first={first:F0} ms nonBuild p50/p99/max={Percentile(other, .5):F1}/{Percentile(other, .99):F1}/{(other.Count == 0 ? 0 : other.Max()):F1} ms worst=["
                + string.Join("; ", worst.OrderByDescending(w => w.Total).Take(5).Select(w => $"#{w.Frame} {w.Total:F0} build {w.Build:F0} gc {w.Gc:F0} overview {w.Overview:F0} {phases[w.Frame]}")) + "] median nonBuild parts: preflight " + $"{Median(breakdown.Select(b => b.Preflight)):F1} begin {Median(breakdown.Select(b => b.Begin)):F1} overview {Median(breakdown.Select(b => b.Overview)):F1}");
            static double Median(IEnumerable<double> values) => values.Order().ElementAtOrDefault(values.Count() / 2);
        }
        finally { disk.Dispose(); }
    }

    private static (Dictionary<string, AssetShape> Records, MapDocument Document) Scene(int objects, int assets, int canvas, bool clustered)
    {
        var random = new Random(17);
        var shas = Enumerable.Range(0, assets).Select(i => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)))).ToArray();
        var records = shas.Select((sha, i) =>
        {
            var side = 700 + random.Next(2900);
            return new AssetShape(sha, side, (int)(side * (0.7 + random.NextDouble() * 0.6)), i % MipDisk.Fixtures);
        }).ToDictionary(r => r.Sha, StringComparer.OrdinalIgnoreCase);
        var items = new SceneObject[objects];
        for (var i = 0; i < objects; i++)
        {
            var width = 64 * (0.5 + random.NextDouble() * 1.5);
            var x = random.NextDouble() * (canvas - width);
            var y = random.NextDouble() * (canvas - width);
            // Clustered: each sixteenth of the page has its own sixteenth of the library (forests here, peaks there).
            var region = (int)(x * 4 / canvas) + 4 * (int)(y * 4 / canvas);
            var record = records[shas[clustered ? region * (assets / 16) + i % (assets / 16) : i % assets]];
            items[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Гора", x, y, width, width * record.Height / record.Width, random.Next(-180, 181),
                "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(record.Sha) };
        }
        var layer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, items);
        var document = new MapDocument(DocumentId.New(), "Dense", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(canvas, canvas, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"), layer])
            .WithAssets(shas.Select(sha => new MapAssetReference(sha, sha, "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)).ToArray());
        return (records, document);
    }

    [Fact]
    public void The_first_frame_after_a_pan_at_400_percent_shows_no_empty_tiles()
    {
        // Live 2026-11-15 (USER, 32768/500k): right after a pan stops at 400 %, parts of the view were EMPTY until their
        // exact tiles arrived. The map was seen whole first (as on open), so a coarser kept level exists for every tile
        // of the new place: a pending tile must show it, not nothing (MapLibre retained parents, Chromium low-res tiling).
        const int canvas = 4096;
        var disk = new MipDisk();
        try
        {
            var (records, document) = Scene(objects: 24_000, assets: 300, canvas, clustered: false);
            using var tiles = new ObjectOverviewCache();
            using var atlas = new ObjectOverviewCache();
            using var overview = new SceneRenderer.OverviewPreparation();
            // A product-sized shelf (the RAM cache is 1/8-1/4 of memory): a 400 % view needs more than 128 MiB, and a
            // shelf smaller than the view's working set thrashes - that is step B.1 (needed images are not evicted).
            var source = new BoundedDerivedSource(records, disk, 1L << 30) { LargeByOutput = true, SmallWaiting = 64, Background = true };
            var fit = Math.Min((double)Width / canvas, (double)Height / canvas);
            var last = "";
            bool SettleAt(double x, double y, double scale)
            {
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(60))
                {
                    var stats = new RenderStats();
                    Draw(source, tiles, atlas, stats, refine: true, x, scale, document, 16.7, out _, overview: overview, offsetY: y);
                    if (stats.DetailTilesPending == 0 && stats.UnresolvedImages == 0 && !source.ImageRequestsPending) return true;
                    last = $"pending={stats.DetailTilesPending} unresolved={stats.UnresolvedImages} requests={source.ImageRequestsPending} "
                        + $"started={stats.DetailRastersStarted} adopted={stats.DetailRastersAdopted} waiting={stats.DetailRastersWaiting} onFrame={stats.DetailTilesBuiltOnFrame} "
                        + $"standIn={stats.DetailTilesStandIn} uncovered={stats.DetailTilesUncovered} drawn={stats.ObjectTilesDrawn}";
                    Thread.Sleep(5);
                }
                return false;
            }
            Assert.True(SettleAt(-(Width / fit - canvas) / 2, 0, fit), "The whole-map view did not settle: " + last);
            const double zoom = 4;
            var viewWidth = Width / zoom; var viewHeight = Height / zoom;
            var (ax, ay) = (canvas * .3, canvas * .3);
            Assert.True(SettleAt(ax, ay, zoom), "The 400 % view did not settle: " + last);
            // A gesture: thirty moving frames carry the view one and a half screens right and down.
            for (var i = 1; i <= 30; i++)
                Draw(source, tiles, atlas, new RenderStats(), refine: true, ax + i * viewWidth * 1.5 / 30, zoom, document, 16.7, out _,
                    fastTerrain: true, overview: overview, offsetY: ay + i * viewHeight * 1.5 / 30);
            var stopped = new RenderStats();
            Draw(source, tiles, atlas, stopped, refine: true, ax + viewWidth * 1.5, zoom, document, 16.7, out _, overview: overview,
                offsetY: ay + viewHeight * 1.5);
            output.WriteLine($"pending={stopped.DetailTilesPending} standIn={stopped.DetailTilesStandIn} uncovered={stopped.DetailTilesUncovered}");
            Assert.Equal(0, stopped.DetailTilesUncovered);
        }
        finally { disk.Dispose(); }
    }

    [ThreadStatic] private static double LastEndMilliseconds;

    private static byte[] Draw(BoundedDerivedSource source, ObjectOverviewCache tiles, ObjectOverviewCache atlas, RenderStats stats,
        bool refine, double offsetX, double scale, MapDocument document, double sinceLastFrame, out double renderMilliseconds, bool fastTerrain = false, SceneRenderer.OverviewPreparation? overview = null,
        double offsetY = 0)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate((float)-offsetX, (float)-offsetY);
        var started = Stopwatch.GetTimestamp();
        using (source.BeginFrame(sinceLastFrame))
        {
            tiles.BeginFrame(); atlas.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = tiles, AtlasCache = atlas, Stats = stats, ViewScale = scale,
                    VisibleWorld = new(offsetX, offsetY, offsetX + Width / scale, offsetY + Height / scale),
                    RequestRefinedFrame = refine ? () => { } : null, FastTerrain = fastTerrain, OverviewPreparation = overview,
                });
            }
            finally { var ending = Stopwatch.GetTimestamp(); tiles.EndFrame(); atlas.EndFrame(); LastEndMilliseconds = Stopwatch.GetElapsedTime(ending).TotalMilliseconds; }
        }
        renderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Only a settled (or reference) frame is read back: a 9 MB array per frame is the harness's own
        // large-object garbage, and its collections pause the frame thread like any other.
        if (refine && (stats.DetailTilesPending != 0 || stats.UnresolvedImages != 0 || source.ImageRequestsPending)) return [];
        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        return bitmap.Bytes;
    }

    internal sealed record AssetShape(string Sha, int Width, int Height, int Fixture);

    /// <summary>
    /// The "derived disk": deterministic mip pixels per asset and level, shared by both sources. Reads run on pool
    /// tasks the frame may never collect: a run that ends (or fails) with reads still queued must not free the
    /// fixtures under them. Before 2026-11-14 it did, and a late <c>SKBitmap.Resize</c> of a disposed fixture
    /// (null handle into <c>sk_bitmap_peek_pixels</c>) crashed the whole CI test host with 0xC0000005.
    /// </summary>
    internal sealed class MipDisk : IDisposable
    {
        public const int Fixtures = 6;
        private readonly SKBitmap[] _fixtures;
        private readonly Dictionary<(int Fixture, int Width, int Height), SKImage> _mips = [];
        private bool _disposed;
        public MipDisk()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
            _fixtures = [.. Enumerable.Range(1, Fixtures).Select(i => SKBitmap.Decode(Path.Combine(root!.FullName, "tests", "fixtures",
                "assets", $"owner-mountain-{i:00}.png")))];
        }
        public SKImage Read(int fixture, int width, int height)
        {
            lock (_mips)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_mips.TryGetValue((fixture, width, height), out var held)) return held;
                using var resized = _fixtures[fixture].Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                resized.SetImmutable();
                return _mips[(fixture, width, height)] = SKImage.FromBitmap(resized);
            }
        }
        public void Dispose()
        {
            // Under the read lock: a read in progress finishes first, every later one sees the flag.
            lock (_mips)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var image in _mips.Values) image.Dispose();
                _mips.Clear();
                foreach (var bitmap in _fixtures) bitmap.Dispose();
            }
        }
    }

    /// <summary>
    /// Mimics <c>CatalogStampImageSource</c> over <c>StampDerivedAssetStore</c>: pixel keys by the
    /// same ladder/mip rule, a worker that prepares one mip at a time in simulated time, at most
    /// <see cref="SmallWaiting"/> small / <see cref="LargeWaiting"/> large finished results waiting
    /// for the frame, adoption in BeginFrame under a 2 ms budget and a byte-bounded resident LRU.
    /// </summary>
    internal sealed class BoundedDerivedSource(IReadOnlyDictionary<string, AssetShape> records, MipDisk disk, long residentBytes)
        : IStampImageSource
    {
        public readonly record struct Key(string Sha, int Step, int Level);
        private readonly IReadOnlyDictionary<string, AssetShape> _records = records;
        public bool Synchronous { get; init; }
        public int SmallWaiting { get; init; } = 64;
        public int LargeWaiting { get; init; } = 1;
        public int InFlightCapacity { get; init; } = 4096;
        public double SmallMipMilliseconds { get; init; } = .3;
        public double LargeMipMilliseconds { get; init; } = 3;
        private readonly LinkedList<(Key Key, SKImage Image, long Bytes)> _lru = new();
        private readonly Dictionary<Key, LinkedListNode<(Key Key, SKImage Image, long Bytes)>> _resident = [];
        private long _residentBytes;
        private readonly List<Key> _queue = [];
        private readonly HashSet<Key> _inFlight = [];
        private readonly List<(Key Key, bool Large, Task<SKImage> Pixels)> _finished = [];
        private HashSet<Key> _wanted = [], _previousWanted = [];
        private readonly HashSet<Key> _late = [];
        private readonly HashSet<Key> _requested = [];
        private double _workerCredit;
        private bool _inFrame;
        public long RequestedKeys => _requested.Count;
        public long RequestedBytes { get; private set; }
        public long NeededEvictions { get; private set; }
        /// <summary>Mips adopted again after their eviction: preparation work the shelf threw away.</summary>
        public long Refetched { get; private set; }
        public long WorkingLookups { get; private set; }
        public long LateMisses { get; private set; }
        public long MaxPlanBytes { get; set; }
        public long PlanPrepares { get; set; }
        public double LastBeginFrameMilliseconds { get; private set; }
        private readonly HashSet<Key> _everAdopted = [];
        public long Evictions { get; private set; }
        public long Adopted { get; private set; }
        public double PreflightMilliseconds { get; private set; }
        public long PreflightItems { get; private set; }
        public long UnresolvedRequestVersion { get; private set; }
        public bool ImageRequestsPending => _late.Count > 0;
        [ThreadStatic] private static bool _pending;
        public bool LastImageRequestPending => _pending;

        public IDisposable BeginFrame(double sinceLastFrame)
        {
            var begun = Stopwatch.GetTimestamp();
            _inFrame = true;
            (_previousWanted, _wanted) = (_wanted, _previousWanted);
            _wanted.Clear();
            // The worker ran meanwhile: one mip at a time, blocked while its kind of waiting slot is full.
            _workerCredit = Math.Min(_workerCredit + sinceLastFrame, 1000);
            var waitingLarge = _finished.Count(f => f.Large);
            var waitingSmall = _finished.Count - waitingLarge;
            var taken = new HashSet<int>();
            for (var index = 0; index < _queue.Count && _workerCredit > 0
                && (waitingLarge < LargeWaiting || waitingSmall < SmallWaiting); index++)
            {
                var key = _queue[index];
                var large = IsLarge(key);
                if (large ? waitingLarge >= LargeWaiting : waitingSmall >= SmallWaiting) continue;
                _workerCredit -= large ? LargeMipMilliseconds : SmallMipMilliseconds;
                if (large) waitingLarge++; else waitingSmall++;
                taken.Add(index);
                // The worker reads and decodes off the frame, as the store does; the frame only adopts.
                var (fw, fh) = Size(key);
                var fixture = _records[key.Sha].Fixture;
                _finished.Add((key, large, Task.Run(() => disk.Read(fixture, fw, fh))));
            }
            if (taken.Count > 0)
            {
                var index = 0;
                _queue.RemoveAll(_ => taken.Contains(index++));
            }
            if (_queue.Count == 0) _workerCredit = 0;
            var started = Stopwatch.GetTimestamp();
            for (var next = 0; next < _finished.Count && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2;)
            {
                var (key, _, pixels) = _finished[next];
                if (!pixels.IsCompleted) { next++; continue; }
                _finished.RemoveAt(next);
                _inFlight.Remove(key);
                // The real adoption copies the prepared pixels into a frame-owned image.
                var image = pixels.Result;
                using var copy = SKBitmap.FromImage(image);
                Adopt(key, SKImage.FromBitmap(copy));
                _late.Remove(key);
            }
            LastBeginFrameMilliseconds = Stopwatch.GetElapsedTime(begun).TotalMilliseconds;
            return new End(this);
        }

        private sealed class End(BoundedDerivedSource source) : IDisposable
        {
            public void Dispose()
            {
                source._inFrame = false;
                // CancelUnwantedPixels: requests nobody asked for in this frame are dropped before they start.
                source._queue.RemoveAll(key => !source._wanted.Contains(key) && source._inFlight.Remove(key));
                source._late.RemoveWhere(key => !source._wanted.Contains(key));
                foreach (var image in source._retired) image.Dispose();
                source._retired.Clear();
            }
        }

        private void Adopt(Key key, SKImage image)
        {
            if (_resident.ContainsKey(key)) { image.Dispose(); return; }
            if (!_everAdopted.Add(key)) Refetched++;
            var bytes = (long)image.Width * image.Height * 4;
            _resident[key] = _lru.AddFirst((key, image, bytes));
            _residentBytes += bytes;
            Adopted++;
            while (_residentBytes > residentBytes && _lru.Count > 1)
            {
                var oldest = _lru.Last!.Value;
                _lru.RemoveLast();
                _resident.Remove(oldest.Key);
                _residentBytes -= oldest.Bytes;
                Evictions++;
                if (_wanted.Contains(oldest.Key) || _previousWanted.Contains(oldest.Key)) NeededEvictions++;
                if (TryRetire(oldest.Image)) continue;
                if (_inFrame) _retired.Add(oldest.Image); else oldest.Image.Dispose();
            }
        }
        private readonly List<SKImage> _retired = [];

        private (int Width, int Height) Size(Key key)
        {
            var record = _records[key.Sha];
            return (Math.Max(1, Math.Max(1, record.Width >> key.Step) >> key.Level), Math.Max(1, Math.Max(1, record.Height >> key.Step) >> key.Level));
        }

        /// <summary>
        /// The store before 2026-11-14 put every mip of a ladder asset in the single large slot, even an 8 px one;
        /// now only a result larger than 256 px waits there, as its memory is what that slot bounds.
        /// </summary>
        public bool LargeByOutput { get; init; } = true;
        private bool IsLarge(Key key) => LargeByOutput
            ? Size(key) is var (w, h) && Math.Max(w, h) > 256
            : Depth(_records[key.Sha]) > 0;

        private static int Depth(AssetShape record)
        {
            var side = Math.Max(record.Width, record.Height);
            if (side <= 2048) return 0;
            var depth = 0;
            while ((side >> depth) > 512) depth++;
            return depth;
        }

        private static int SampleLevel(int width, int height, double w, double h) =>
            Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, Math.Min(width / Math.Max(1, w), height / Math.Max(1, h))))), 0, 15);

        public Key PixelKey(AssetShape record, double width, double height)
        {
            var depth = Depth(record);
            var step = Math.Min(depth, SampleLevel(record.Width, record.Height, width, height));
            var level = depth > 0 && step == 0 ? 0
                : SampleLevel(Math.Max(1, record.Width >> step), Math.Max(1, record.Height >> step), width, height);
            return new(record.Sha, step, level);
        }

        private SKImage? Resident(Key key)
        {
            if (!_resident.TryGetValue(key, out var node)) return null;
            _lru.Remove(node); _lru.AddFirst(node);
            return node.Value.Image;
        }

        private bool Request(Key key)
        {
            _wanted.Add(key);
            if (_requested.Add(key)) { var (w, h) = Size(key); RequestedBytes += (long)w * h * 4; }
            if (_inFlight.Contains(key)) return false;
            if (_inFlight.Count >= InFlightCapacity) return false;
            _inFlight.Add(key);
            _queue.Add(key);
            return false;
        }

        private SKImage? Exact(Key key)
        {
            if (Synchronous)
            {
                var (w, h) = Size(key);
                return disk.Read(_records[key.Sha].Fixture, w, h);
            }
            return Resident(key);
        }

        public (int Width, int Height)? TryGetImageSize(string sha256) =>
            _records.TryGetValue(sha256, out var r) ? (Math.Max(1, r.Width >> (Depth(r) > 0 ? 1 : 0)), Math.Max(1, r.Height >> (Depth(r) > 0 ? 1 : 0))) : null;

        public SKColor? TryGetOverviewColor(string sha256) => _records.TryGetValue(sha256, out var r)
            ? new SKColor((byte)(60 + r.Fixture * 30), 90, 70) : null;

        public SKImage? TryGetImageForOcclusion(string sha256) => null;

        public SKImage? TryGetImage(string sha256)
        {
            var record = _records[sha256];
            WorkingLookups++;
            return ForKey(new Key(record.Sha, Depth(record) > 0 ? 1 : 0, 0));
        }

        public SKImage? TryGetImageForPixelSize(string sha256, double width, double height) =>
            ForKey(PixelKey(_records[sha256], width, height));

        private SKImage? ForKey(Key key)
        {
            _pending = false;
            if (Exact(key) is { } image) { _wanted.Add(key); return image; }
            if (!_wanted.Contains(key)) _late.Add(key);
            LateMisses++;
            Request(key);
            _pending = true;
            UnresolvedRequestVersion++;
            return null;
        }

        public SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height)
        {
            if (Synchronous) return TryGetImageForPixelSize(sha256, width, height);
            var record = _records[sha256];
            var wanted = PixelKey(record, width, height);
            var depth = Depth(record);
            var desired = wanted.Step + wanted.Level;
            SKImage? At(int level)
            {
                var step = Math.Min(depth, level);
                return _resident.TryGetValue(new Key(record.Sha, step, level - step), out var node) ? node.Value.Image : null;
            }
            for (var level = desired; level <= 15; level++) if (At(level) is { } smaller) return smaller;
            for (var level = desired - 1; level >= 0; level--) if (At(level) is { } larger) return larger;
            return null;
        }

        /// <summary>False mimics a source without plans: every check walks all stamps of the area.</summary>
        public bool Plans { get; init; } = true;
        /// <summary>False: plans cannot be pinned, so tiles are rastered on the frame thread.</summary>
        public bool Background { get; init; } = true;
        public long PreparationBytes => residentBytes;
        public IStampPreparationPlan? CreatePreparationPlan() => Plans && !Synchronous ? new Plan(this) : null;

        private sealed class Plan(BoundedDerivedSource source) : IStampPreparationPlan
        {
            private readonly HashSet<Key> _keys = [];
            private Key[]? _array;
            private long _readyAt = -1, _checkedAt = -1;
            private Key[]? _missing;
            public long Bytes { get; private set; }
            public long SourceRevision => 0;
            public IStampImageLease? Pin() => source.Background ? source.Pin(_keys) : null;
            public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps)
            {
                var started = Stopwatch.GetTimestamp();
                foreach (var (sha, width, height) in stamps)
                {
                    source.PreflightItems++;
                    var key = source.PixelKey(source._records[sha], width, height);
                    if (_keys.Add(key)) { var (w, h) = source.Size(key); Bytes += (long)w * h * 4; }
                }
                _array = null; _missing = null; _checkedAt = -1;
                source.PreflightMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            public bool Prepare(Action ready)
            {
                source.MaxPlanBytes = Math.Max(source.MaxPlanBytes, Bytes); source.PlanPrepares++;
                if (_readyAt == source.Evictions) return true;
                var started = Stopwatch.GetTimestamp();
                // As the real source: without an eviction only the keys missing last time can change.
                var keys = _checkedAt == source.Evictions && _missing is { } missing ? missing : _array ??= [.. _keys];
                var still = new List<Key>();
                var asked = 0;
                foreach (var key in keys)
                {
                    source.PreflightItems++;
                    if (source._resident.ContainsKey(key)) continue;
                    source._wanted.Add(key);
                    // As the real source: at most 512 new requests per check.
                    if (asked++ < 512) source.Request(key);
                    still.Add(key);
                }
                _missing = [.. still];
                _checkedAt = source.Evictions;
                var resident = still.Count == 0;
                if (resident) _readyAt = source.Evictions;
                source.PreflightMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return resident;
            }
        }

        // Images a background raster holds: an eviction frees them only when the last holder lets go.
        private readonly object _pins = new();
        private readonly Dictionary<SKImage, int> _pinned = [];
        private readonly HashSet<SKImage> _evictedWhilePinned = [];
        public int BackgroundRasters { get; private set; }

        private bool TryRetire(SKImage image)
        {
            lock (_pins)
            {
                if (!_pinned.ContainsKey(image)) return false;
                _evictedWhilePinned.Add(image);
                return true;
            }
        }

        internal IStampImageLease? Pin(IEnumerable<Key> keys)
        {
            var images = new Dictionary<Key, SKImage>();
            foreach (var key in keys)
            {
                if (!_resident.TryGetValue(key, out var node)) return null;
                images[key] = node.Value.Image;
            }
            lock (_pins) foreach (var image in images.Values.Distinct()) _pinned[image] = _pinned.GetValueOrDefault(image) + 1;
            var sizes = images.Keys.Select(k => k.Sha).Distinct().ToDictionary(sha => sha, sha => TryGetImageSize(sha)!.Value);
            BackgroundRasters++;
            return new Pinned(this, images, sizes);
        }

        private void Unpin(IEnumerable<SKImage> images)
        {
            lock (_pins)
                foreach (var image in images.Distinct())
                {
                    var left = _pinned[image] - 1;
                    if (left > 0) { _pinned[image] = left; continue; }
                    _pinned.Remove(image);
                    if (_evictedWhilePinned.Remove(image)) image.Dispose();
                }
        }

        /// <summary>What a background tile draws from: a fixed snapshot, never a request.</summary>
        private sealed class Pinned(BoundedDerivedSource source, Dictionary<Key, SKImage> images,
            Dictionary<string, (int Width, int Height)> sizes) : IStampImageLease
        {
            private bool _disposed;
            public long UnresolvedRequestVersion { get; private set; }
            public bool LastImageRequestPending { get; private set; }
            public (int Width, int Height)? TryGetImageSize(string sha256) => sizes.TryGetValue(sha256, out var size) ? size : null;
            public SKImage? TryGetImage(string sha256) => Miss();
            public SKImage? TryGetImageForOcclusion(string sha256) => null;
            public SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height) => null;
            public SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
            {
                LastImageRequestPending = false;
                return images.TryGetValue(source.PixelKey(source._records[sha256], width, height), out var image) ? image : Miss();
            }
            private SKImage? Miss() { LastImageRequestPending = true; UnresolvedRequestVersion++; return null; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                source.Unpin(images.Values);
            }
        }

        public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
        {
            if (Synchronous) return true;
            var started = Stopwatch.GetTimestamp();
            var resident = true;
            var seen = new HashSet<Key>();
            foreach (var (sha, width, height) in stamps)
            {
                PreflightItems++;
                var key = PixelKey(_records[sha], width, height);
                if (!seen.Add(key)) continue;
                _wanted.Add(key);
                if (Resident(key) is null) resident &= Request(key);
            }
            PreflightMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return resident;
        }
    }
}
