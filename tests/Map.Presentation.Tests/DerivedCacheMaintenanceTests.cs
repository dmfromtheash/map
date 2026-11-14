using Map.Assets;
using Map.Core.Documents;
using Map.Core.Resources;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// DEC-110 p.2 / DEC-111 p.3-4: the machine cache of asset preparations is cleaned by the program itself, in the
/// background, within the shared 5 GiB prepared-data budget; removed, tagged and unused contents can be dropped on
/// purpose while frames keep reading. Pictures are the owner's mountain fixtures.
/// </summary>
public sealed class DerivedCacheMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-derived-maintenance-" + Guid.NewGuid().ToString("N"));
    private string Derived => Path.Combine(_root, "derived");

    private static DerivedCacheMaintenancePolicy Quiet(TimeProvider? clock = null) => new()
    {
        Automatic = false, Budget = new PreparedDataBudget(), AvailableFreeBytes = () => 1L << 50,
        Clock = clock ?? TimeProvider.System,
    };

    internal static string Fixture(int number)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Map.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{number:00}.png");
    }

    private string Stem(string sha) => Path.Combine(Derived, "v1", sha.ToLowerInvariant());

    [Fact]
    public void Default_policy_runs_by_itself_soon_after_start_periodically_and_after_builds()
    {
        var policy = DerivedCacheMaintenancePolicy.Default;
        Assert.True(policy.Automatic);
        Assert.InRange(policy.StartupDelay, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        Assert.InRange(policy.Period, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        Assert.Equal(64, policy.BuildsBetweenCleanups);
        Assert.Equal(TimeSpan.FromDays(14), policy.UnmountedAging);
        Assert.Same(PreparedDataBudget.Shared, policy.Budget);
    }

    [Fact]
    public async Task The_janitor_starts_by_itself_and_removes_a_stale_interrupted_write()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var stale = Path.Combine(Derived, "v1", new string('a', 64) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "interrupted");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        await using var store = new StampDerivedAssetStore(catalog, Derived,
            maintenance: Quiet() with { Automatic = true, StartupDelay = TimeSpan.Zero });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (store.Cleanups == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(store.Cleanups >= 1);
        Assert.NotNull(store.LastCleanup);
        Assert.True(store.LastCleanup!.LockAcquired);
        Assert.Equal(5 * PreparedDataBudget.GiB, store.LastCleanup.CeilingBytes);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(Derived, "mounts.json")));
    }

    [Fact]
    public async Task Removing_a_record_deletes_its_machine_preparation_and_nothing_else()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var removed = catalog.Import(Fixture(1), new(), DateTimeOffset.UnixEpoch);
        var kept = catalog.Import(Fixture(2), new(), DateTimeOffset.UnixEpoch);
        await using var store = new StampDerivedAssetStore(catalog, Derived, maintenance: Quiet());
        Assert.NotNull(await store.Request(removed.Sha256).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.NotNull(await store.Request(kept.Sha256).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(Stem(removed.Sha256) + ".pixels"));

        catalog.RemoveRecord(removed.Sha256);
        // Jobs run in order: this pass follows the automatic purge the removal queued.
        await store.CleanNowAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(Stem(removed.Sha256) + ".json"));
        Assert.False(File.Exists(Stem(removed.Sha256) + ".pixels"));
        Assert.True(File.Exists(Stem(kept.Sha256) + ".json"));
        Assert.True(File.Exists(Stem(kept.Sha256) + ".pixels"));
        // The owner's library files are never touched by a cache cleanup.
        Assert.True(File.Exists(catalog.SourcePath(kept)!));
    }

    [Fact]
    public async Task Purge_by_tag_drops_only_the_tagged_test_assets()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var test = catalog.Import(Fixture(3), new AssetImportOptions { Tags = [EditorViewModel.TestAssetTag] }, DateTimeOffset.UnixEpoch);
        var own = catalog.Import(Fixture(4), new(), DateTimeOffset.UnixEpoch);
        await using var store = new StampDerivedAssetStore(catalog, Derived, maintenance: Quiet());
        await store.Request(test.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
        await store.Request(own.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
        var bytes = new FileInfo(Stem(test.Sha256) + ".pixels").Length + new FileInfo(Stem(test.Sha256) + ".json").Length;

        var result = await store.PurgeDerivedByTag(EditorViewModel.TestAssetTag).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new DerivedCachePurgeResult(1, 2, bytes, 0, true), result);
        Assert.False(File.Exists(Stem(test.Sha256) + ".pixels"));
        Assert.True(File.Exists(Stem(own.Sha256) + ".pixels"));
        Assert.NotNull(catalog.Find(test.Sha256)); // the card stays; only the cache went
    }

    [Fact]
    public async Task Purge_unused_drops_preparations_no_attached_catalog_lists()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var listed = catalog.Import(Fixture(5), new(), DateTimeOffset.UnixEpoch);
        await using var store = new StampDerivedAssetStore(catalog, Derived, maintenance: Quiet());
        await store.Request(listed.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
        var orphan = Stem(new string('b', 64));
        File.WriteAllBytes(orphan + ".json", new byte[10]);
        File.WriteAllBytes(orphan + ".pixels", new byte[20]);

        var result = await store.PurgeUnused().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new DerivedCachePurgeResult(1, 2, 30, 0, true), result);
        Assert.False(File.Exists(orphan + ".json"));
        Assert.True(File.Exists(Stem(listed.Sha256) + ".pixels"));
        Assert.Equal(DerivedCachePurgeResult.Nothing, await store.PurgeDerived(["not-a-sha"]));
    }

    [Fact]
    public async Task Frames_keep_reading_while_their_preparations_are_purged()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var records = Enumerable.Range(1, 3).Select(n => catalog.Import(Fixture(n), new(), DateTimeOffset.UnixEpoch)).ToArray();
        await using var store = new StampDerivedAssetStore(catalog, Derived, maintenance: Quiet());
        var headers = new List<StampDerivedAssetStore.Header>();
        foreach (var record in records) headers.Add((await store.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(30)))!);
        var expected = headers.Select(h => store.ReadMip(h, h.FirstMip, out _, out _)).ToArray();
        using var stop = new CancellationTokenSource();
        var reads = 0; var corrupt = 0; var empty = 0;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                for (var i = 0; i < headers.Count; i++)
                {
                    var mip = await store.RequestMip(headers[i], headers[i].FirstMip).WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.True(store.TryTakeMip(headers[i].Sha, headers[i].FirstMip, out _));
                    // An empty result is the ordinary "not ready" answer the canvas already retries.
                    if (mip.Pixels is null) Interlocked.Increment(ref empty);
                    else if (!mip.Pixels.AsSpan().SequenceEqual(expected[i])) Interlocked.Increment(ref corrupt);
                    else Interlocked.Increment(ref reads);
                }
        });
        long purged = 0;
        for (var round = 0; round < 15; round++)
        {
            purged += (await store.PurgeDerived(records.Select(r => r.Sha256)).WaitAsync(TimeSpan.FromSeconds(30))).Entries;
            await Task.Delay(15);
        }
        stop.Cancel();
        await reader.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(purged > 0);
        // A purged pair is rebuilt from the verified original on the reader's worker: same exact pixels, never torn ones.
        Assert.Equal(0, corrupt);
        Assert.True(reads > empty, $"exact reads {reads}, empty {empty}");
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(1, false)]
    public async Task A_library_unplugged_past_the_aging_period_gives_up_its_preparations_first(int days, bool unpluggedFirst)
    {
        var driveRoot = Path.Combine(_root, "drive", "MapLibrary", "refs");
        var drive = new AssetCatalog(driveRoot);
        var onDrive = drive.Import(Fixture(2), new(), DateTimeOffset.UnixEpoch);
        await drive.FlushMetadataAsync();
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var own = catalog.Import(Fixture(1), new(), DateTimeOffset.UnixEpoch);
        catalog.SetMounts([driveRoot]);
        var start = new ManualClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        await using (var first = new StampDerivedAssetStore(catalog, Derived, maintenance: Quiet(start)))
        {
            Assert.NotNull(await first.Request(own.Sha256).WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.NotNull(await first.Request(onDrive.Sha256).WaitAsync(TimeSpan.FromSeconds(30)));
            await first.CleanNowAsync();
        }
        // The own asset is the least recently used one; the drive asset was used just now.
        foreach (var extension in new[] { ".json", ".pixels" })
        {
            File.SetLastWriteTimeUtc(Stem(own.Sha256) + extension, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(Stem(onDrive.Sha256) + extension, DateTime.UtcNow);
        }
        long Size(string sha) => new FileInfo(Stem(sha) + ".json").Length + new FileInfo(Stem(sha) + ".pixels").Length;
        var (a, b) = (Size(own.Sha256), Size(onDrive.Sha256));
        var ceiling = (long)Math.Ceiling(Math.Max(a, b) / 0.7) + 1;
        Assert.True(a + b > ceiling * 0.9, "both entries must exceed the trigger line");
        catalog.SetMounts([]);

        var later = new ManualClock(start.GetUtcNow().AddDays(days));
        await using var second = new StampDerivedAssetStore(catalog, Derived, diskBudgetBytes: ceiling, maintenance: Quiet(later));
        var report = await second.CleanNowAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(report.CleanupTriggered);
        Assert.Equal(unpluggedFirst, !File.Exists(Stem(onDrive.Sha256) + ".pixels"));
        Assert.Equal(!unpluggedFirst, !File.Exists(Stem(own.Sha256) + ".pixels"));
        Assert.Equal(unpluggedFirst ? 1 : 0, report.EvictedUnmounted);
        Assert.Equal(unpluggedFirst ? 0 : 1, report.EvictedLive);
        Assert.True(File.Exists(drive.SourcePath(onDrive)!)); // the library itself is never touched
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

[CollectionDefinition(nameof(DerivedCacheWiring), DisableParallelization = true)]
public sealed class DerivedCacheWiring;

/// <summary>The product path: an editor over a catalog with the profile's cache root runs the janitor by itself.</summary>
[Collection(nameof(DerivedCacheWiring))]
public sealed class DerivedCacheWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-derived-wiring-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task An_editor_with_the_profile_cache_root_starts_its_janitor_without_any_call()
    {
        var root = EditorViewModel.DerivedAssetCacheRoot;
        var policy = EditorViewModel.DerivedCacheMaintenance;
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        catalog.Import(DerivedCacheMaintenanceTests.Fixture(1), new(), DateTimeOffset.UnixEpoch);
        EditorViewModel.DerivedAssetCacheRoot = Path.Combine(_root, "cache", "derived");
        EditorViewModel.DerivedCacheMaintenance = new DerivedCacheMaintenancePolicy
        {
            StartupDelay = TimeSpan.Zero, Budget = new PreparedDataBudget(), AvailableFreeBytes = () => 1L << 50,
        };
        CatalogStampImageSource? source = null;
        try
        {
            var document = new MapDocument(DocumentId.New(), "Кэш", DateTimeOffset.UnixEpoch, new CanvasDefinition(64, 64, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF")]);
            var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
                new FakePrompts(), new FakeLog(), assetCatalog: catalog);
            source = Assert.IsType<CatalogStampImageSource>(editor.StampImages);
            Assert.True(source.HasDerivedCache);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (source.DerivedCleanups == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(source.DerivedCleanups >= 1);
            Assert.True(source.LastDerivedCleanup!.LockAcquired);
            Assert.Equal(DerivedCachePurgeResult.Nothing, await editor.PurgeUnusedDerivedCache());
            Assert.Equal(DerivedCachePurgeResult.Nothing, await editor.PurgeTestAssetsDerivedCache());
        }
        finally
        {
            EditorViewModel.DerivedAssetCacheRoot = root;
            EditorViewModel.DerivedCacheMaintenance = policy;
            if (source is not null) await source.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
