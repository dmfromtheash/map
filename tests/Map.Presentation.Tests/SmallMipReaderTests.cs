using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

public sealed class SmallMipReaderTests
{
    [Fact]
    public async Task Compact_source_preparation_stays_serial_without_blocking_persisted_mips()
    {
        await using var fixture = new OwnerMipFixture();
        var small = fixture.Import(1)[0];
        var large = fixture.Import(1, ladder: true)[0];
        await using var store = new StampDerivedAssetStore(fixture.Catalog, fixture.Derived, startWatcher: _ => { });
        var smallHeader = await store.Request(small.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
        var largeHeader = await store.Request(large.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(smallHeader);
        Assert.NotNull(largeHeader);
        Assert.True(AssetCatalog.DetailLadderDepth(large.Width, large.Height) > 0);
        var sourceReader = (SemaphoreSlim)typeof(StampDerivedAssetStore).GetField("_compactSourceReader",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        await sourceReader.WaitAsync();
        var waiting = store.RequestPixels(largeHeader, 1, 3);
        try
        {
            await Task.Delay(100);
            Assert.False(waiting.IsCompleted, "a second source decode escaped the single-source memory bound");
            var mip = await store.RequestMip(smallHeader, 2).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(mip.Pixels);
            Assert.True(store.TryTakeMip(smallHeader.Sha, 2, out _));
            store.CancelUnwantedPixels(new HashSet<StampDerivedAssetStore.PixelKey>());
            var cancelled = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cancelled.Cancelled);
            Assert.True(store.TryTakePixels(new(largeHeader.Sha, 1, 3), out _));
        }
        finally { sourceReader.Release(); }
        var prepared = await store.RequestPixels(largeHeader, 1, 3).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(prepared.Image);
        Assert.Equal(large.Width >> 4, prepared.Image.Width);
        Assert.Equal(large.Height >> 4, prepared.Image.Height);
        Assert.True(store.TryTakePixels(new(largeHeader.Sha, 1, 3), out var taken));
        taken!.Image!.Dispose();
    }

    [Fact]
    public async Task A_small_mip_can_finish_while_another_reader_is_busy()
    {
        await using var fixture = new OwnerMipFixture();
        var records = fixture.Import(1);
        await using var store = new StampDerivedAssetStore(fixture.Catalog, fixture.Derived, startWatcher: _ => { });
        var header = await store.Request(records[0].Sha256).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(header);
        // test-change: the production reader now adjusts concurrency when machine memory changes.
        var reader = (AdaptiveWorkGate)typeof(StampDerivedAssetStore).GetField("_mipReader",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        await reader.WaitAsync();
        try
        {
            // Hold a lane deterministically instead of depending on a lucky disk/JIT overlap.
            var mip = await store.RequestMip(header, 2).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(mip.Cancelled);
            Assert.Equal(store.ReadMip(header, 2, out var width, out var height), mip.Pixels);
            Assert.Equal(width, mip.Width);
            Assert.Equal(height, mip.Height);
            Assert.True(store.TryTakeMip(header.Sha, 2, out _));
        }
        finally { reader.Release(); }
    }

    [Fact]
    public async Task Parallel_readers_keep_only_64_finished_small_results_and_reuse_the_slots()
    {
        await using var fixture = new OwnerMipFixture();
        var records = fixture.Import(9);
        await using var store = new StampDerivedAssetStore(fixture.Catalog, fixture.Derived, startWatcher: _ => { });
        var headers = (await Task.WhenAll(records.Select(r => store.Request(r.Sha256))))
            .Select(h => { Assert.NotNull(h); return h!; }).ToArray();
        var requests = headers.SelectMany(h => Enumerable.Range(h.FirstMip, h.MipCount)
            .Select(level => (Header: h, Level: level))).Take(65).ToArray();
        Assert.Equal(65, requests.Length);
        var tasks = requests.Select(r => store.RequestMip(r.Header, r.Level)).ToArray();
        var watch = Stopwatch.StartNew();
        while (tasks.Count(t => t.IsCompletedSuccessfully) < 64 && watch.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(10);
        await Task.Delay(100); // Give an unbounded/65th reader time to expose the defect.
        Assert.Equal(64, tasks.Count(t => t.IsCompletedSuccessfully));
        Assert.InRange(tasks.Where(t => t.IsCompletedSuccessfully).Sum(t => (long)t.Result.Width * t.Result.Height * 4),
            1, 16L * 1024 * 1024);
        var ready = Array.FindIndex(tasks, t => t.IsCompletedSuccessfully);
        Assert.True(store.TryTakeMip(requests[ready].Header.Sha, requests[ready].Level, out _));
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        for (var i = 0; i < tasks.Length; i++)
        {
            Assert.False(tasks[i].Result.Cancelled);
            Assert.NotNull(tasks[i].Result.Pixels);
            if (i != ready) Assert.True(store.TryTakeMip(requests[i].Header.Sha, requests[i].Level, out _));
        }
        Assert.Equal(0, store.PendingMips);
        var again = await store.RequestMip(headers[0], 2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(again.Pixels);
        Assert.True(store.TryTakeMip(headers[0].Sha, 2, out _));
    }
}

public sealed class DenseMipZoomTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_thousand_visible_owner_stamps_reach_the_exact_level_after_a_zoom(bool moving)
    {
        await using var fixture = new OwnerMipFixture();
        var records = fixture.Import(2000);
        Assert.Equal(2000, records.Select(r => r.Sha256).Distinct().Count());
        await using (var preparation = new StampDerivedAssetStore(fixture.Catalog, fixture.Derived, startWatcher: _ => { }))
        {
            var headers = await Task.WhenAll(records.Select(r => preparation.Request(r.Sha256)))
                .WaitAsync(TimeSpan.FromMinutes(2));
            Assert.All(headers, h => Assert.NotNull(h));
        }
        var objects = records.Select((r, i) => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Mountain",
            i % 50 * 96, i / 50 * 66, r.Width / 2d, r.Height / 2d, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(r.Sha256) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Zoom", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(5120, 2880, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, objects)]);
        using var surface = SKSurface.Create(new SKImageInfo(2560, 1440))!;
        for (var trial = 1; trial <= 3; trial++)
        {
            await using var source = new CatalogStampImageSource(fixture.Catalog, () => document, () => null,
                derivedRoot: fixture.Derived, startWatcher: _ => { }) { CaptureMipSelections = true, MeasureTimings = true };
            // The previous zoom is fully resident. Every target level is still absent.
            var warm = false;
            var deadline = Stopwatch.StartNew();
            while (!warm && deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                using (source.BeginFrame()) warm = source.PrepareForPixelSizes(
                    records.Select(r => (r.Sha256, r.Width / 8d, r.Height / 8d)), () => { });
                if (!warm) await Task.Delay(16);
            }
            Assert.True(warm);
            var previous = Draw(.25f);
            Assert.Equal(0, previous.UnresolvedImages);
            Assert.Equal(2000, previous.StampInstances);
            source.FrameMoving = moving;
            var adoptBefore = source.Timings.AdoptMs;
            var watch = Stopwatch.StartNew();
            var frames = 0;
            var unresolved = 2000;
            var firstUnresolved = 0;
            double maxFrameMs = 0;
            while (unresolved > 0 && watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                var frameStart = Stopwatch.GetTimestamp();
                var stats = Draw(.5f);
                unresolved = stats.UnresolvedImages;
                Assert.Equal(2000, stats.StampInstances);
                frames++;
                if (frames == 1)
                {
                    firstUnresolved = unresolved;
                    var first = JsonSerializer.SerializeToElement(source.ReadMipSelections()).GetProperty("LastPerSha");
                    Assert.Equal(2000, first.EnumerateArray().Count(s => s.GetProperty("Path").GetString()!.StartsWith("ready-fallback/")));
                }
                var spent = Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                maxFrameMs = Math.Max(maxFrameMs, spent);
                if (unresolved > 0 && spent < 16.667) await Task.Delay(TimeSpan.FromMilliseconds(16.667 - spent));
            }
            var elapsed = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(2000, firstUnresolved);
            Assert.Equal(0, unresolved);
            Assert.False(source.ImageRequestsPending);
            Assert.Equal(0, source.DerivedStatistics.Builds);
            var selections = JsonSerializer.SerializeToElement(source.ReadMipSelections()).GetProperty("LastPerSha");
            Assert.Equal(2000, selections.GetArrayLength());
            var shapes = records.ToDictionary(r => r.Sha256, StringComparer.OrdinalIgnoreCase);
            foreach (var selection in selections.EnumerateArray())
            {
                var sha = selection.GetProperty("Sha").GetString()!;
                Assert.StartsWith("exact-request/", selection.GetProperty("Path").GetString());
                Assert.Equal(shapes[sha].Width >> 2, selection.GetProperty("ImageWidth").GetInt32());
                Assert.Equal(shapes[sha].Height >> 2, selection.GetProperty("ImageHeight").GetInt32());
            }
            using var snapshot = surface.Snapshot();
            using var pixels = SKBitmap.FromImage(snapshot);
            output.WriteLine($"MIP_ZOOM moving={moving} trial={trial} cores={Environment.ProcessorCount} visible=2000 firstUnresolved={firstUnresolved} " +
                $"finalUnresolved={unresolved} fallback=0 exact=2000 timeMs={elapsed:F3} frames={frames} maxFrameMs={maxFrameMs:F3} " +
                $"adoptMs={source.Timings.AdoptMs - adoptBefore:F3} byteLimitedFrames={source.ByteLimitedFrames} " +
                $"sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels.Bytes))}");

            RenderStats Draw(float scale)
            {
                surface.Canvas.Clear(SKColors.White);
                surface.Canvas.ResetMatrix();
                surface.Canvas.Scale(scale);
                var stats = new RenderStats();
                using (source.BeginFrame())
                    SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                    {
                        Stamps = source, Stats = stats, ViewScale = scale,
                        VisibleWorld = new(0, 0, 5120, 2880), LodTier = RenderLodTier.Exact,
                    });
                return stats;
            }
        }
    }
}

internal sealed class OwnerMipFixture : IAsyncDisposable
{
    // Sources never change in these tests. The existing watcher seam also excludes macOS
    // FSEvents delivering an import's old creation event after a watcher starts.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-mip-zoom", Guid.NewGuid().ToString("N"));
    internal AssetCatalog Catalog { get; }
    internal string Derived => Path.Combine(_root, "derived");
    internal OwnerMipFixture()
    {
        Directory.CreateDirectory(_root);
        Catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
    }

    internal AssetRecord[] Import(int count, bool ladder = false)
    {
        var fixture = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixture is not null && !File.Exists(Path.Combine(fixture.FullName, "Map.slnx"))) fixture = fixture.Parent;
        Assert.NotNull(fixture);
        var owners = Enumerable.Range(1, 6).Select(i => SKBitmap.Decode(Path.Combine(fixture.FullName, "tests", "fixtures", "assets",
            $"owner-mountain-{i:00}.png"))).ToArray();
        try
        {
            var paths = new string[count];
            for (var i = 0; i < count; i++)
            {
                // Unique dimensions, only resampling owner pixels; no marker pixels or new art.
                using var resized = owners[i % owners.Length].Resize(new SKImageInfo(ladder ? 2051 : 192 + i % 64, ladder ? 1027 : 160 + i / 64),
                    new SKSamplingOptions(SKFilterMode.Linear));
                var path = paths[i] = Path.Combine(_root, $"owner-{i:0000}.png");
                using (var png = resized.Encode(SKEncodedImageFormat.Png, 100))
                using (var file = File.Create(path)) png.SaveTo(file);
            }
            var set = Catalog.ImportSet(paths, "Owner zoom", new(), DateTimeOffset.UnixEpoch);
            foreach (var path in paths) File.Delete(path);
            return set.VariantIds.Select(id => Catalog.Find(Catalog.FindVariant(id)!.Sha256)!).ToArray();
        }
        finally { foreach (var owner in owners) owner.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await Catalog.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
