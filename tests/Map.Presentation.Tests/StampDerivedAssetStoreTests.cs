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
    [InlineData("garbage-pixels")]
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
        if (damage == "garbage-pixels")
        {
            var garbage = new byte[new FileInfo(stem + ".pixels").Length];
            new Random(13013).NextBytes(garbage);
            File.WriteAllBytes(stem + ".pixels", garbage);
        }
        else if (damage == "missing-pixels") File.Delete(stem + ".pixels");
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

    [Fact]
    public async Task Every_owner_zoom_is_prepared_off_frame_with_exact_pixels_and_no_sync_load_or_sample()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = Enumerable.Range(1, 6).Select(n => ImportOwner(catalog, n)).ToArray();
        var document = new MapDocument(DocumentId.New(), "All levels", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        using var source = new CatalogStampImageSource(catalog, () => document, () => null,
            derivedRoot: Path.Combine(_root, "derived")) { MeasureTimings = true };
        using var baseline = new CatalogStampImageSource(catalog, () => document, () => null);
        foreach (var pixels in new[] { 16d, 64d, 256d, 512d, 4096d, 64d, 4096d })
        {
            var ready = false;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!ready && DateTime.UtcNow < deadline)
            {
                using (source.BeginFrame())
                    ready = source.PrepareForPixelSizes(records.Select(r => (r.Sha256, pixels, pixels)), () => { });
                if (!ready) await Task.Delay(10);
            }
            Assert.True(ready);
            using (source.BeginFrame())
                foreach (var record in records)
                {
                    using var actual = SKBitmap.FromImage(source.TryGetImageForPixelSize(record.Sha256, pixels, pixels)!);
                    using var expected = SKBitmap.FromImage(baseline.TryGetImageForPixelSize(record.Sha256, pixels, pixels)!);
                    Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
                    Assert.Equal(expected.Bytes, actual.Bytes);
                }
        }
        Assert.Equal(0, source.Timings.LoadCalls);
        Assert.Equal(0, source.Timings.SampleCalls);
        Assert.InRange(source.CacheStatistics.ResidentBytes, 1, 512L * 1024 * 1024);
    }

    [Fact]
    public async Task Missing_asset_does_not_hide_another_pending_request()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        var document = new MapDocument(DocumentId.New(), "Pending", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null,
            derivedRoot: Path.Combine(_root, "derived"));
        using (source.BeginFrame())
            Assert.False(source.PrepareForPixelSizes([(record.Sha256, 4096d, 4096d), (new string('f', 64), 4096d, 4096d)], () => { }));
    }

    [Fact]
    public async Task A_pending_zoom_uses_resident_pixels_without_requesting_a_working_original()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        var document = new MapDocument(DocumentId.New(), "Resident fallback", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null,
            derivedRoot: Path.Combine(_root, "derived")) { MeasureTimings = true };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var ready = false;
        while (!ready && DateTime.UtcNow < deadline)
        {
            using (source.BeginFrame()) ready = source.PrepareForPixelSizes([(record.Sha256, 16d, 16d)], () => { });
            if (!ready) await Task.Delay(10);
        }
        Assert.True(ready);
        using (source.BeginFrame())
        {
            var small = source.TryGetImageForPixelSize(record.Sha256, 16, 16);
            Assert.NotNull(small);
            Assert.Null(source.TryGetImageForPixelSize(record.Sha256, 64, 64));
            var pending = source.DerivedStatistics.PendingMips;
            Assert.Equal(1, pending);
            var fallback = source.TryGetReadyImageForPixelSize(record.Sha256, 64, 64);
            Assert.NotNull(fallback);
            Assert.NotSame(small, fallback);
            Assert.Same(fallback, source.TryGetReadyImageForPixelSize(record.Sha256, 64, 64));
            using var smallPixels = SKBitmap.FromImage(small);
            using var fallbackPixels = SKBitmap.FromImage(fallback);
            Assert.Equal(smallPixels.Bytes, fallbackPixels.Bytes);
            Assert.Equal(pending, source.DerivedStatistics.PendingMips);
            Assert.True(source.ImageRequestsPending);
            Assert.True(source.LastImageRequestPending);
            Exception? backgroundFailure = null;
            var background = new Thread(() =>
            {
                try
                {
                    using var lease = source.Lease();
                    Assert.NotNull(lease.TryGetImageForPixelSize(record.Sha256, 16, 16));
                }
                catch (Exception ex) { backgroundFailure = ex; }
            });
            background.Start();
            Assert.True(background.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(backgroundFailure);
            Assert.True(source.LastImageRequestPending); // The background lease cannot overwrite this thread's result.
        }
        Assert.Equal(0, source.Timings.LoadCalls);
        Assert.Equal(0, source.SourceRevision);
        using (source.BeginFrame()) { } // Camera left that requested size.
        Assert.False(source.ImageRequestsPending);
        Assert.Equal(0, source.SourceRevision);
        ready = false;
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready && DateTime.UtcNow < deadline)
        {
            using (source.BeginFrame()) ready = source.TryGetImageForPixelSize(record.Sha256, 64, 64) is not null;
            if (!ready) await Task.Delay(10);
        }
        Assert.True(ready);
        Assert.Equal(0, source.SourceRevision); // A late arrival cannot discard unrelated scene caches.
        source.ForgetMissing();
        Assert.False(source.ImageRequestsPending);
    }

    [Fact]
    public async Task Unwritable_cache_keeps_exact_colors_and_pixels_in_memory_and_queue_continues()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = Enumerable.Range(1, 6).Select(n => ImportOwner(catalog, n)).ToArray();
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "A file, not a writable cache directory");
        var store = new StampDerivedAssetStore(catalog, blocked);
        try
        {
            foreach (var record in records.Concat(records))
            {
                var header = await store.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotNull(header);
                var mip = await store.RequestMip(header, header.FirstMip).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(store.TryTakeMip(record.Sha256, header.FirstMip, out _));
                Assert.NotNull(mip.Pixels);
                using var expected = new AssetImageCache();
                var original = expected.GetVerified(record.Sha256, catalog.SourcePath(record), record.Sha256)!;
                Assert.Equal(SceneRenderer.StampOverviewColor(original), new SKColor(header.Color));
                using var pixels = SKBitmap.FromImage(expected.GetForPixelSize(record.Sha256,
                    original.Width / Math.Pow(2, header.FirstMip), original.Height / Math.Pow(2, header.FirstMip))!);
                using var rgba = new SKBitmap(new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                using var image = SKImage.FromBitmap(pixels);
                Assert.True(image.ReadPixels(rgba.Info, rgba.GetPixels(), rgba.RowBytes, 0, 0));
                Assert.Equal(rgba.Bytes, mip.Pixels);
            }
            Assert.True(store.Failures >= 6);
            Assert.Equal(0, store.PendingMips);
        }
        finally { store.Dispose(); await store.Completion; }
    }

    [Fact]
    public async Task Existing_ladder_keeps_its_own_pixels_at_odd_dimensions_and_missing_step_falls_back()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var owner = ImportOwner(catalog, 1);
        // Only the owner's mountain, resized in the disposable fixture to exercise ladder
        // selection and odd rounding. The repository fixture is never modified.
        using var original = SKBitmap.Decode(catalog.SourcePath(owner));
        using var large = original.Resize(new SKImageInfo(2051, 1027), new SKSamplingOptions(SKFilterMode.Linear));
        var path = Path.Combine(_root, "owner-large.png");
        using (var png = large.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(path)) png.SaveTo(file);
        var record = catalog.Import(path, new(), DateTimeOffset.UnixEpoch);
        Assert.True(AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0);
        var document = new MapDocument(DocumentId.New(), "Ladder", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        foreach (var missing in new[] { false, true })
        {
            if (missing && catalog.TryDetailLevelPath(record, 2, out var step)) File.Delete(step);
            using var source = new CatalogStampImageSource(catalog, () => document, () => null,
                derivedRoot: Path.Combine(_root, "derived")) { MeasureTimings = true };
            using var baseline = new CatalogStampImageSource(catalog, () => document, () => null);
            foreach (var size in new[] { 4096d, 800d, 400d, 200d, 16d, 800d, 400d })
            {
                var ready = false;
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!ready && DateTime.UtcNow < deadline)
                {
                    using (source.BeginFrame()) ready = source.PrepareForPixelSizes([(record.Sha256, size, size / 2)], () => { });
                    if (!ready) await Task.Delay(10);
                }
                Assert.True(ready);
                using (source.BeginFrame())
                {
                    using var expected = SKBitmap.FromImage(baseline.TryGetImageForPixelSize(record.Sha256, size, size / 2)!);
                    using var actual = SKBitmap.FromImage(source.TryGetImageForPixelSize(record.Sha256, size, size / 2)!);
                    Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height);
                    Assert.Equal(expected.Bytes, actual.Bytes);
                }
            }
            if (missing)
            {
                var revision = source.SourceRevision;
                AssetCatalog.BuildDetailLadder(catalog.ReadVerified(record).Content!,
                    Path.Combine(catalog.RootFor(record), "ladder", record.Sha256.ToLowerInvariant()));
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (source.SourceRevision == revision && DateTime.UtcNow < deadline)
                { using (source.BeginFrame()) { } await Task.Delay(10); }
                Assert.True(source.SourceRevision > revision);
                var ready = false;
                while (!ready && DateTime.UtcNow < deadline)
                {
                    using (source.BeginFrame()) ready = source.PrepareForPixelSizes([(record.Sha256, 400d, 200d)], () => { });
                    if (!ready) await Task.Delay(10);
                }
                Assert.True(ready);
                using var refreshed = new CatalogStampImageSource(catalog, () => document, () => null);
                using (source.BeginFrame())
                {
                    using var expected = SKBitmap.FromImage(refreshed.TryGetImageForPixelSize(record.Sha256, 400, 200)!);
                    using var actual = SKBitmap.FromImage(source.TryGetImageForPixelSize(record.Sha256, 400, 200)!);
                    Assert.Equal(expected.Bytes, actual.Bytes);
                }
            }
            Assert.Equal(0, source.Timings.LoadCalls); Assert.Equal(0, source.Timings.SampleCalls);
        }
    }

    [Fact]
    public async Task Temporary_source_read_failure_does_not_poison_the_queue_or_the_asset()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var first = ImportOwner(catalog, 1);
        var second = ImportOwner(catalog, 2);
        await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"));
        using (var locked = new FileStream(catalog.SourcePath(first)!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(await store.Request(first.Sha256).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.NotNull(await store.Request(second.Sha256).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await Task.Delay(2100);
        Assert.NotNull(await store.Request(first.Sha256).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Full_pixel_queue_preserves_current_demand_and_wakes_refused_requests()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = Enumerable.Range(1, 3).Select(n => ImportOwner(catalog, n)).ToArray();
        await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"), pixelCapacity: 2);
        var headers = (await Task.WhenAll(records.Select(r => store.Request(r.Sha256)))).Select(h => h!).ToArray();
        Assert.True(store.TryRequestPixels(headers[0], 0, headers[0].FirstMip, out var a));
        Assert.True(store.TryRequestPixels(headers[1], 0, headers[1].FirstMip, out var b));
        Assert.False(store.TryRequestPixels(headers[2], 0, headers[2].FirstMip, out var capacity));
        Assert.False(store.TryRequestPixels(headers[2], 0, headers[2].FirstMip + 1, out var sameCapacity));
        Assert.Same(capacity, sameCapacity);
        Assert.False(capacity.IsCompleted);
        Assert.Equal(2, store.PendingMips);
        Assert.True(store.TryRequestPixels(headers[0], 0, headers[0].FirstMip, out var original));
        Assert.Same(a, original);
        await a.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(store.TryTakeMip(headers[0].Sha, headers[0].FirstMip, out _));
        await capacity.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(store.TryRequestPixels(headers[2], 0, headers[2].FirstMip, out var c));
        Assert.NotNull((await b.WaitAsync(TimeSpan.FromSeconds(10))).Pixels);
        Assert.NotNull((await c.WaitAsync(TimeSpan.FromSeconds(10))).Pixels);
        Assert.Equal(0, store.DiscardedMips);
    }

    [Fact]
    public async Task Persistent_missing_source_stops_retries_until_watched_file_is_restored()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        var path = catalog.SourcePath(record)!;
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"));
        var previous = store.Request(record.Sha256);
        Assert.Null(await previous.WaitAsync(TimeSpan.FromSeconds(10)));
        foreach (var delay in new[] { 2100, 4100, 8100 })
        {
            await Task.Delay(delay);
            var retry = store.Request(record.Sha256);
            Assert.NotSame(previous, retry);
            Assert.Null(await retry.WaitAsync(TimeSpan.FromSeconds(10)));
            previous = retry;
        }
        await Task.Delay(2100);
        Assert.Same(previous, store.Request(record.Sha256));
        File.WriteAllBytes(path, bytes);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ReferenceEquals(previous, store.Request(record.Sha256)) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.NotNull(await store.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Raster_ownership_transfer_keeps_exact_pixels_without_retaining_an_unbudgeted_master()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog, 1);
        await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"), preparedSourceByteCapacity: 0);
        var header = (await store.Request(record.Sha256))!;
        using var expected = new AssetImageCache();
        var original = expected.Get(record.Sha256, catalog.SourcePath(record))!;
        await store.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(store.TryTakePixels(new(record.Sha256, 0, 0), out var result));
        using var owned = result!.Image!;
        Assert.NotNull(owned);
        Assert.Equal(0, store.PreparedSourceBytes);
        using var a = SKBitmap.FromImage(original);
        using var b = SKBitmap.FromImage(owned);
        Assert.Equal(a.Bytes, b.Bytes);
        // Moving to a different source must not free the transferred original.
        var second = ImportOwner(catalog, 2);
        var h2 = (await store.Request(second.Sha256))!;
        await store.RequestPixels(h2, 0, 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(store.TryTakePixels(new(second.Sha256, 0, 0), out var other));
        other!.Image?.Dispose();
        Assert.NotEqual(IntPtr.Zero, owned.Handle);
        Assert.Equal(0, store.PreparedSourceBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
