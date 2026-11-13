using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class StampDerivedAssetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-derived-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Persisted_owner_overview_and_each_small_mip_equal_the_previous_pixels()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var fixture = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixture is not null && !File.Exists(Path.Combine(fixture.FullName, "Map.slnx"))) fixture = fixture.Parent;
        Assert.NotNull(fixture);
        var root = Path.Combine(_root, "derived");
        var records = Enumerable.Range(1, 6).Select(n => catalog.Import(
            Path.Combine(fixture.FullName, "tests", "fixtures", "assets", $"owner-mountain-{n:00}.png"), new(), DateTimeOffset.UnixEpoch)).ToArray();
        using (var store = new StampDerivedAssetStore(catalog, root))
        {
            foreach (var record in records)
            {
                var header = await store.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotNull(header);
                using var expected = new AssetImageCache(64, 512L * 1024 * 1024);
                var original = catalog.ReadVerified(record);
                var encoded = catalog.TryDetailLevelPath(record, 1, out var step)
                    ? AssetImagePolicy.ReadEncodedFile(step) : original.Content!;
                var image = expected.GetEncoded(record.Sha256, encoded)!;
                Assert.Equal(SceneRenderer.StampOverviewColor(image), new SKColor(header.Color));
                for (var level = header.FirstMip; level < header.FirstMip + header.MipCount; level++)
                {
                    // Use an exact ratio, including odd source dimensions.
                    var mip = expected.GetForPixelSize(record.Sha256,
                        image.Width / Math.Pow(2, level), image.Height / Math.Pow(2, level))!;
                    using var pixels = new SKBitmap(new SKImageInfo(mip.Width, mip.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    Assert.True(mip.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
                    var actual = store.ReadMip(header, level, out var width, out var height);
                    Assert.Equal(mip.Width, width); Assert.Equal(mip.Height, height);
                    Assert.Equal(pixels.Bytes, actual);
                }
            }
            Assert.Equal(6, store.Builds);
        }
        using var reopened = new StampDerivedAssetStore(catalog, root);
        foreach (var record in records) Assert.NotNull(await reopened.Request(record.Sha256));
        Assert.Equal(6, reopened.DiskHits); Assert.Equal(0, reopened.Builds);
    }

    [Fact]
    public async Task Changed_original_does_not_reuse_the_persisted_color()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var fixture = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixture is not null && !File.Exists(Path.Combine(fixture.FullName, "Map.slnx"))) fixture = fixture.Parent;
        var record = catalog.Import(Path.Combine(fixture!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"), new(), DateTimeOffset.UnixEpoch);
        var root = Path.Combine(_root, "derived");
        using (var store = new StampDerivedAssetStore(catalog, root)) Assert.NotNull(await store.Request(record.Sha256));
        // The imported test copy only; the owner's fixture remains untouched.
        File.WriteAllBytes(catalog.SourcePath(record)!, [0, 1, 2, 3]);
        using var reopened = new StampDerivedAssetStore(catalog, root);
        Assert.Null(await reopened.Request(record.Sha256));
        Assert.Equal(0, reopened.DiskHits);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("pixels")]
    [InlineData("missing-pixels")]
    public async Task Interrupted_cache_pair_is_rebuilt_from_verified_owner_bytes(string damage)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        var root = Path.Combine(_root, "derived");
        var initial = new StampDerivedAssetStore(catalog, root);
        var header = (await initial.Request(record.Sha256))!;
        var expected = initial.ReadMip(header, header.FirstMip, out _, out _);
        initial.Dispose(); await initial.Completion;
        var stem = Path.Combine(root, "v1", record.Sha256.ToLowerInvariant());
        if (damage == "missing-pixels") File.Delete(stem + ".pixels");
        else File.WriteAllBytes(stem + "." + damage, [0, 1, 2]);
        var reopened = new StampDerivedAssetStore(catalog, root);
        try
        {
            var restored = await reopened.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(restored);
            var mip = await reopened.RequestMip(restored, restored.FirstMip).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(expected, mip.Pixels);
            Assert.Equal(1, reopened.Builds);
            Assert.True(reopened.TryTakeMip(record.Sha256, restored.FirstMip, out _));
        }
        finally { reopened.Dispose(); await reopened.Completion; }
    }

    [Fact]
    public async Task Disk_budget_evicts_owned_pairs_and_cleans_stale_interrupted_writes()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        var root = Path.Combine(_root, "derived");
        var folder = Path.Combine(root, "v1");
        Directory.CreateDirectory(folder);
        var interrupted = Path.Combine(folder, record.Sha256 + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(interrupted, "interrupted");
        File.SetLastWriteTimeUtc(interrupted, DateTime.UtcNow.AddHours(-2));
        var unrelated = Path.Combine(folder, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");
        var store = new StampDerivedAssetStore(catalog, root, diskBudgetBytes: 1);
        try
        {
            Assert.NotNull(await store.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(store.DiskBytes <= 1);
            Assert.Equal(1, store.DiskEvictions);
            Assert.Empty(Directory.GetFiles(folder, "*.json"));
            Assert.Empty(Directory.GetFiles(folder, "*.pixels"));
            Assert.False(File.Exists(interrupted));
            Assert.True(File.Exists(unrelated));
        }
        finally { store.Dispose(); await store.Completion; }
    }

    [Fact]
    public async Task Leaving_a_zoom_cancels_queued_work_and_does_not_starve_the_next_zoom()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = Enumerable.Range(1, 6).Select(n => ImportOwner(catalog, n)).ToArray();
        var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"));
        try
        {
            var headers = (await Task.WhenAll(records.Select(r => store.Request(r.Sha256)))).Select(h => h!).ToArray();
            var jobs = headers.SelectMany(h => Enumerable.Range(h.FirstMip, h.MipCount)
                .Select(level => (Header: h, Level: level, Task: store.RequestMip(h, level)))).ToArray();
            Assert.True(jobs.Length > 8);
            store.CancelUnwantedMips(new HashSet<(string, int)>());
            await Task.WhenAll(jobs.Select(j => j.Task)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(store.DiscardedMips > 0);
            foreach (var job in jobs) Assert.True(store.TryTakeMip(job.Header.Sha, job.Level, out _));
            Assert.Equal(0, store.PendingMips);
            // All eight completion slots must be reusable after cancellation or catalog reload.
            var next = headers[0];
            var ready = await store.RequestMip(next, next.FirstMip).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(ready.Pixels); Assert.False(ready.Cancelled);
            store.Forget();
            var reloaded = await store.RequestMip(next, next.FirstMip).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(reloaded.Pixels);
            Assert.True(store.TryTakeMip(next.Sha, next.FirstMip, out _));
        }
        finally { store.Dispose(); await store.Completion; }
    }

    [Fact]
    public async Task Metadata_eviction_preserves_completed_results_and_reuses_disk_on_revisit()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = Enumerable.Range(1, 6).Select(n => ImportOwner(catalog, n)).ToArray();
        var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"), metadataCapacity: 2);
        try
        {
            var headers = await Task.WhenAll(records.Select(r => store.Request(r.Sha256))).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(headers, Assert.NotNull);
            Assert.True(store.MetadataEntries <= 2);
            Assert.NotNull(await store.Request(records[0].Sha256));
            Assert.Equal(6, store.Builds);
            Assert.Equal(1, store.DiskHits);
        }
        finally { store.Dispose(); await store.Completion; }
    }

    [Fact]
    public async Task Changed_source_invalidates_ready_metadata_without_a_canvas_disk_check()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived")) { SourceChanged = () => changed.TrySetResult() };
        try
        {
            Assert.NotNull(await store.Request(record.Sha256));
            File.WriteAllBytes(catalog.SourcePath(record)!, [0, 1, 2, 3]);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(record.Sha256, store.TakeInvalidated());
            Assert.Null(await store.Request(record.Sha256));
        }
        finally { store.Dispose(); await store.Completion; }
    }

    [Fact]
    public async Task Canvas_consumes_ready_small_pixels_without_loading_originals_and_observes_source_changes()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        var document = new MapDocument(DocumentId.New(), "Derived", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        using var source = new CatalogStampImageSource(catalog, () => document, () => null,
            derivedRoot: Path.Combine(_root, "derived")) { MeasureTimings = true };
        using var baseline = new CatalogStampImageSource(catalog, () => document, () => null);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var ready = false;
        while (!ready && DateTime.UtcNow < deadline)
        {
            using (source.BeginFrame())
                ready = source.PrepareOverview([record.Sha256], () => { })
                    && source.PrepareForPixelSizes([(record.Sha256, 16d, 16d)], () => { });
            if (!ready) await Task.Delay(10);
        }
        Assert.True(ready);
        using (source.BeginFrame())
        {
            using var actual = SKBitmap.FromImage(source.TryGetImageForPixelSize(record.Sha256, 16, 16)!);
            using var expected = SKBitmap.FromImage(baseline.TryGetImageForPixelSize(record.Sha256, 16, 16)!);
            Assert.Equal(expected.Bytes, actual.Bytes);
        }
        Assert.Equal(0, source.Timings.LoadCalls);
        var before = source.SourceRevision;
        File.WriteAllBytes(catalog.SourcePath(record)!, [0, 1, 2, 3]);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (source.SourceRevision == before && DateTime.UtcNow < deadline)
        {
            using (source.BeginFrame()) { }
            await Task.Delay(10);
        }
        Assert.True(source.SourceRevision > before);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (!source.PrepareOverview([record.Sha256], () => { }) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Null(source.TryGetOverviewColor(record.Sha256));
    }

    private static AssetRecord ImportOwner(AssetCatalog catalog, int number)
    {
        var fixture = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixture is not null && !File.Exists(Path.Combine(fixture.FullName, "Map.slnx"))) fixture = fixture.Parent;
        return catalog.Import(Path.Combine(fixture!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{number:00}.png"), new(), DateTimeOffset.UnixEpoch);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
