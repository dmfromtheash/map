using System.Diagnostics;
using System.Reflection;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

public abstract class FrameSourceFixture : IDisposable
{
    protected readonly string _root = Path.Combine(Path.GetTempPath(), "map-frame-source", Guid.NewGuid().ToString("N"));
    protected static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    protected static MapDocument Document() => new(DocumentId.New(), "Frame", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF")], []);
    protected static string OwnerImage()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        return Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FrameSourceLatencyTests(ITestOutputHelper output) : FrameSourceFixture
{

    [Fact]
    public async Task Visible_frame_latency_while_background_pins_5000_keys()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(OwnerImage(), new(), DateTimeOffset.UnixEpoch);
        await using var source = new CatalogStampImageSource(catalog, Document, () => null, derivedRoot: Path.Combine(_root, "derived"));
        var plan = source.CreatePreparationPlan()!;
        plan.Add([(record.Sha256, 16, 16)]);
        var keys = (HashSet<StampDerivedAssetStore.PixelKey>)plan.GetType().GetField("Keys", Private)!.GetValue(plan)!;
        for (var i = 0; i < 4999; i++) keys.Add(new(i.ToString("x64"), 0, 0));
        var samples = (AssetImageCache)typeof(CatalogStampImageSource).GetField("_samples", Private)!.GetValue(source)!;
        using var owner = SKBitmap.Decode(OwnerImage());
        using var tiny = owner.Resize(new SKImageInfo(16, 16), new SKSamplingOptions(SKFilterMode.Linear));
        tiny.SetImmutable();
        foreach (var key in keys)
            Assert.NotNull(samples.AdoptPixels((key.Step == 0 ? key.Sha : key.Sha + "@" + key.Step) + "#" + key.Level, SKImage.FromBitmap(tiny)));
        source.SetFrameReady(() => { });
        using (source.BeginFrame()) Assert.NotNull(source.TryGetImageForPixelSize(record.Sha256, 16, 16));
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var pins = 0;
        var worker = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var lease = plan.Pin();
                if (lease is not null) Interlocked.Increment(ref pins);
                started.Set();
            }
        });
        worker.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var times = new List<double>();
        var images = 0;
        using var surface = SKSurface.Create(new SKImageInfo(512, 512));
        var draw = new SKImage?[2000];
        try
        {
            for (var frame = 0; frame < 80; frame++)
            {
                var watch = Stopwatch.StartNew();
                surface.Canvas.Clear(SKColors.Transparent);
                using (source.BeginFrame())
                {
                    // Gather the frame's image commands before submitting its raster work. Interleaving a
                    // raster draw with every old blocking lookup can starve a baseline run for minutes.
                    for (var stamp = 0; stamp < draw.Length; stamp++)
                        draw[stamp] = source.TryGetImageForPixelSize(record.Sha256, 16, 16);
                    for (var stamp = 0; stamp < draw.Length; stamp++)
                        if (draw[stamp] is { } image)
                        {
                            images++;
                            surface.Canvas.DrawImage(image, SKRect.Create(stamp % 32 * 16, stamp / 32 % 32 * 16, 16, 16),
                                new SKSamplingOptions(SKFilterMode.Linear));
                        }
                }
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        finally { stop.Cancel(); Assert.True(worker.Join(TimeSpan.FromSeconds(10))); }
        times.Sort();
        output.WriteLine($"FRAME 2000 visible; 5000 pinned keys; 80 frames; p50={times[39]:F3} ms p95={times[75]:F3} ms max={times[^1]:F3} ms; pins={pins}; resident={images}; gate-misses={source.FrameGateMisses}");
        output.WriteLine("FRAME-SAMPLES " + System.Text.Json.JsonSerializer.Serialize(times));
        Assert.Equal(160000, images);
        Assert.True(pins > 0);
        using var rendered = surface.Snapshot();
        using var pixels = SKBitmap.FromImage(rendered);
        Assert.True(pixels.Bytes.Any(value => value != 0));
        output.WriteLine("FRAME-PIXELS " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels.Bytes)));
    }
}

public sealed class FrameSourceBoundaryTests(ITestOutputHelper output) : FrameSourceFixture
{
    [Fact]
    public async Task Frame_has_zero_disk_calls_for_warm_and_cold_catalog_images()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(OwnerImage(), new(), DateTimeOffset.UnixEpoch);
        await using var source = new CatalogStampImageSource(catalog, Document, () => null);
        Assert.NotNull(source.TryGetImageForPixelSize(record.Sha256, 16, 16));
        var frameThread = Environment.CurrentManagedThreadId;
        var calls = 0;
        source.FileAccess = _ => { if (Environment.CurrentManagedThreadId == frameThread) Interlocked.Increment(ref calls); };
        using (source.BeginFrame())
        {
            for (var stamp = 0; stamp < 2000; stamp++)
            {
                source.TryGetImage(record.Sha256);
                source.TryGetImageSize(record.Sha256);
                source.TryGetImageForPixelSize(record.Sha256, 16, 16);
                source.TryGetReadyImageForPixelSize(record.Sha256, 32, 32);
                source.TryGetOverviewColor(record.Sha256);
            }
            // A missing ladder step and a new size must also stay off disk.
            source.TryGetImageForPixelSize(record.Sha256, record.Width, record.Height);
            source.PrepareForPixelSizes([(record.Sha256, record.Width, record.Height)], () => { });
        }
        output.WriteLine($"FRAME source filesystem boundaries on canvas thread={calls}; derived reads={source.DerivedIoDiagnostics.ReadsOnCanvasThread}");
        Assert.Equal(0, calls);
        Assert.Equal(0, source.DerivedIoDiagnostics.ReadsOnCanvasThread);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Every_frame_entry_uses_resident_answers_when_gate_is_held(bool heldBeforeBegin, bool derivedGate)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var bytes = File.ReadAllBytes(OwnerImage());
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var doc = Document() with { Assets = [new MapAssetReference(sha, "Mountain", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "test", "Map", bytes)] };
        await using var source = new CatalogStampImageSource(catalog, () => doc, () => null,
            derivedRoot: derivedGate ? Path.Combine(_root, "derived") : null) { CaptureMipSelections = true };
        var image = source.TryGetImageForPixelSize(sha, 16, 16);
        Assert.NotNull(image);
        using var expected = SKBitmap.FromImage(image);
        var refinements = 0;
        source.SetFrameReady(() => Interlocked.Increment(ref refinements));
        using (source.BeginFrame())
        {
            Assert.NotNull(source.TryGetImage(sha));
            Assert.NotNull(source.TryGetImageForPixelSize(sha, 16, 16));
            Assert.NotNull(source.TryGetReadyImageForPixelSize(sha, 16, 16));
            Assert.NotNull(source.TryGetImageSize(sha));
            Assert.NotNull(source.TryGetOverviewColor(sha));
            source.TryGetImageForOcclusion(sha);
            source.IsMissingCacheable(sha);
        }
        var sourceGate = typeof(CatalogStampImageSource).GetField("_gate", Private)!.GetValue(source)!;
        var derived = typeof(CatalogStampImageSource).GetField("_derived", Private)!.GetValue(source);
        var gate = derivedGate ? typeof(StampDerivedAssetStore).GetField("_requestsGate", Private)!.GetValue(derived)! : sourceGate;
        var cache = (AssetImageCache)typeof(CatalogStampImageSource).GetField("_cache", Private)!.GetValue(source)!;
        var samples = (AssetImageCache)typeof(CatalogStampImageSource).GetField("_samples", Private)!.GetValue(source)!;
        var begun = heldBeforeBegin ? null : source.BeginFrame();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Timed release makes the broken version fail rather than hanging the test runner.
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                lock (sourceGate)
                {
                    cache.Remove(sha); // eviction without a source change: the resident snapshot must keep its pixels
                    for (var level = 0; level <= 15; level++) samples.Remove(sha + "#" + level);
                }
                held.Set();
                release.Wait(TimeSpan.FromMilliseconds(350));
            }
        });
        holder.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
        var watch = Stopwatch.StartNew();
        try
        {
            source.SetFrameReady(() => Interlocked.Increment(ref refinements));
            var scope = heldBeforeBegin ? source.BeginFrame() : begun;
            using (scope)
            {
                Assert.True(watch.ElapsedMilliseconds < 100, $"BeginFrame waited {watch.ElapsedMilliseconds} ms");
                var working = source.TryGetImage(sha);
                Assert.True(watch.ElapsedMilliseconds < 100, $"image lookup waited {watch.ElapsedMilliseconds} ms");
                Assert.NotNull(working);
                var resident = source.TryGetImageForPixelSize(sha, 16, 16);
                Assert.NotNull(resident);
                Assert.NotEqual(IntPtr.Zero, resident.Handle);
                using var pixels = SKBitmap.FromImage(resident);
                Assert.Equal(expected.Bytes, pixels.Bytes);
                Assert.NotNull(source.TryGetReadyImageForPixelSize(sha, 16, 16));
                var wheelFallback = source.TryGetReadyImageForPixelSize(sha, 20, 20);
                Assert.NotNull(wheelFallback);
                Assert.NotEqual(IntPtr.Zero, wheelFallback.Handle);
                Assert.NotNull(source.TryGetImageSize(sha));
                Assert.NotNull(source.TryGetOverviewColor(sha));
                source.TryGetImageForOcclusion(sha);
                source.IsMissingCacheable(sha);
                _ = source.ImageRequestsPending;
                if (source.CreatePreparationPlan() is { } plan)
                    Assert.False(plan.Prepare(() => Interlocked.Increment(ref refinements)));
                source.PrepareOverview([sha], () => Interlocked.Increment(ref refinements));
                source.PrepareForPixelSizes([(sha, 16, 16)], () => { });
            }
            Assert.True(watch.ElapsedMilliseconds < 100, $"frame waited {watch.ElapsedMilliseconds} ms");
            Assert.True(source.LastImageRequestPending);
            Assert.True(refinements > 0);
        }
        finally { release.Set(); Assert.True(holder.Join(TimeSpan.FromSeconds(10))); }
        var selections = System.Text.Json.JsonSerializer.SerializeToElement(source.ReadMipSelections()).GetProperty("LastPerSha");
        var selected = Assert.Single(selections.EnumerateArray());
        Assert.StartsWith("ready-fallback/", selected.GetProperty("Path").GetString());
        Assert.Equal(20, selected.GetProperty("RequestedWidth").GetDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_catalog_file_invalidates_resident_pixels_and_recovers_without_frame_disk_calls(bool failedWatcher)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(OwnerImage(), new(), DateTimeOffset.UnixEpoch);
        var path = catalog.Resolve(record).Path!;
        var bytes = File.ReadAllBytes(path);
        using var changed = new ManualResetEventSlim();
        await using var source = new CatalogStampImageSource(catalog, Document, () => null,
            startWatcher: failedWatcher ? _ => throw new IOException("watcher unavailable") : null);
        var original = source.TryGetImageForPixelSize(record.Sha256, record.Width, record.Height);
        Assert.NotNull(original);
        using var expected = SKBitmap.FromImage(original);
        var frameThread = 0;
        var disk = 0;
        source.FileAccess = _ => { if (Environment.CurrentManagedThreadId == Volatile.Read(ref frameThread)) Interlocked.Increment(ref disk); };
        source.SetFrameReady(changed.Set);
        var revision = source.SourceRevision;
        File.AppendAllText(path, "changed");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(source.SourceRevision > revision);
        frameThread = Environment.CurrentManagedThreadId;
        using (source.BeginFrame()) Assert.Null(source.TryGetImageForPixelSize(record.Sha256, record.Width, record.Height));
        frameThread = 0;
        changed.Reset();
        File.WriteAllBytes(path, bytes);
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)));
        SKImage? recovered = null;
        for (var attempt = 0; attempt < 200 && recovered is null; attempt++)
        {
            frameThread = Environment.CurrentManagedThreadId;
            using (source.BeginFrame()) recovered = source.TryGetImageForPixelSize(record.Sha256, record.Width, record.Height);
            frameThread = 0;
            if (recovered is null) await Task.Delay(10);
        }
        Assert.NotNull(recovered);
        using var actual = SKBitmap.FromImage(recovered);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Equal(0, disk);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task Cold_frame_converges_to_the_same_exact_owner_pixels_as_a_worker(bool embedded, bool ladder, bool tightBudget)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var doc = Document();
        string sha;
        if (embedded)
        {
            var bytes = File.ReadAllBytes(OwnerImage());
            sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            doc = doc with { Assets = [new MapAssetReference(sha, "Mountain", "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "test", "Map", bytes)] };
        }
        else
        {
            var path = OwnerImage();
            if (ladder)
            {
                using var owner = SKBitmap.Decode(path);
                using var large = owner.Resize(new SKImageInfo(2101, 1577), new SKSamplingOptions(SKFilterMode.Linear));
                using var encoded = large.Encode(SKEncodedImageFormat.Png, 100);
                path = Path.Combine(_root, "owner-ladder.png");
                Directory.CreateDirectory(_root);
                File.WriteAllBytes(path, encoded.ToArray());
            }
            sha = catalog.Import(path, new(), DateTimeOffset.UnixEpoch).Sha256;
        }
        var budget = 512L * 1024 * 1024;
        if (tightBudget)
        {
            var metadata = AssetImagePolicy.Inspect(doc.Assets.Single().EmbeddedContent!);
            budget = (metadata.DecodedBytes + metadata.EncodedBytes + 1024) * 4 / 3;
        }
        await using var reference = new CatalogStampImageSource(catalog, () => doc, () => null, cacheBytes: budget);
        using var expected = SKBitmap.FromImage(reference.TryGetImageForPixelSize(sha, 16, 16)!);
        await using var source = new CatalogStampImageSource(catalog, () => doc, () => null, cacheBytes: budget);
        var frameThread = Environment.CurrentManagedThreadId;
        var disk = 0;
        source.FileAccess = _ => { if (Environment.CurrentManagedThreadId == Volatile.Read(ref frameThread)) Interlocked.Increment(ref disk); };
        using (source.BeginFrame())
        {
            Assert.Null(source.TryGetImageForPixelSize(sha, 16, 16));
            Assert.True(source.LastImageRequestPending);
        }
        frameThread = 0;
        SKImage? settled = null;
        for (var attempt = 0; attempt < 200 && settled is null; attempt++)
        {
            frameThread = Environment.CurrentManagedThreadId;
            using (source.BeginFrame())
            {
                var ready = source.TryGetImageForPixelSize(sha, 16, 16);
                if (!source.LastImageRequestPending) settled = ready;
            }
            frameThread = 0;
            if (settled is null) await Task.Delay(10);
        }
        Assert.NotNull(settled);
        using var pixels = SKBitmap.FromImage(settled);
        Assert.Equal(expected.Bytes, pixels.Bytes);
        Assert.Equal(0, disk);
    }

    [Fact]
    public async Task Frame_snapshots_cannot_pin_an_unbounded_screen_beyond_the_cache_budget()
    {
        const long budget = 128 * 1024;
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        await using var source = new CatalogStampImageSource(catalog, Document, () => null, imageCapacity: 8, cacheBytes: budget);
        var cache = (AssetImageCache)typeof(CatalogStampImageSource).GetField("_cache", Private)!.GetValue(source)!;
        using var owner = SKBitmap.Decode(OwnerImage());
        using var tiny = owner.Resize(new SKImageInfo(16, 16), new SKSamplingOptions(SKFilterMode.Linear));
        tiny.SetImmutable();
        for (var frame = 0; frame < 3; frame++)
        {
            using (source.BeginFrame())
                for (var stamp = 0; stamp < 100; stamp++)
                {
                    var sha = (frame * 100 + stamp).ToString("x64");
                    Assert.NotNull(cache.AdoptPixels(sha, SKImage.FromBitmap(tiny)));
                    Assert.NotNull(source.TryGetImage(sha));
                }
            Assert.InRange(source.FrameSnapshotPinnedBytes, 1, budget / 4);
            Assert.True(source.CacheStatistics.Evictions > 0);
            Assert.InRange(source.CacheStatistics.ResidentBytes, 1, budget);
        }
    }

    [Fact]
    public async Task Resident_preflight_does_not_queue_a_worker_again()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var bytes = File.ReadAllBytes(OwnerImage());
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var doc = Document() with { Assets = [new MapAssetReference(sha, "Mountain", "image/png", "png", ProjectAssetStorage.Embedded,
            DateTimeOffset.UnixEpoch, "test", "Map", bytes)] };
        await using var source = new CatalogStampImageSource(catalog, () => doc, () => null) { MeasureTimings = true };
        Assert.NotNull(source.TryGetImageForPixelSize(sha, 16, 16));
        var samples = source.Timings.SampleCalls;
        var scheduled = source.FrameWorkSchedules;
        using (source.BeginFrame())
            Assert.True(source.PrepareForPixelSizes([(sha, 16, 16)], () => { }));
        var gate = typeof(CatalogStampImageSource).GetField("_gate", Private)!.GetValue(source)!;
        Task[] pending;
        lock (gate)
            pending = [.. (HashSet<Task>)typeof(CatalogStampImageSource).GetField("_frameTasks", Private)!.GetValue(source)!];
        await Task.WhenAll(pending);
        Assert.Equal(scheduled, source.FrameWorkSchedules);
        Assert.Equal(samples, source.Timings.SampleCalls);
    }

}
