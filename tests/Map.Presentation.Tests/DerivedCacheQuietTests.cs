using Map.Assets;
using Map.Core.Resources;
using Map.Presentation.Services;

namespace Map.Presentation.Tests;

public sealed class DerivedCacheQuietTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-janitor-quiet-" + Guid.NewGuid().ToString("N"));
    private string Derived => Path.Combine(_root, "derived");

    private static DerivedCacheMaintenancePolicy Policy(Func<bool> idle) => new()
    {
        StartupDelay = TimeSpan.Zero, IdleDelay = TimeSpan.FromMilliseconds(100),
        ActivityPollInterval = TimeSpan.FromMilliseconds(10), MaximumDeferral = TimeSpan.FromMinutes(10),
        IsViewIdle = idle, Budget = new PreparedDataBudget(), AvailableFreeBytes = () => 1L << 50,
    };

    private string StaleWrite()
    {
        var path = Path.Combine(Derived, "v1", new string('a', 64) + "." + new string('b', 32) + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "interrupted");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        return path;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "background work did not reach the expected state");
    }

    [Fact]
    public async Task Pending_view_does_not_even_start_a_scan_then_sustained_idle_cleans()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var stale = StaleWrite();
        var idle = 0;
        var polls = 0;
        await using var store = new StampDerivedAssetStore(catalog, Derived,
            maintenance: Policy(() => { Interlocked.Increment(ref polls); return Volatile.Read(ref idle) != 0; }));
        await Until(() => Volatile.Read(ref polls) >= 5);
        Assert.Equal(0, store.Cleanups);
        Assert.False(File.Exists(Path.Combine(Derived, DerivedCacheJanitor.LockFileName)));
        Assert.True(File.Exists(stale));

        Volatile.Write(ref idle, 1);
        await Until(() => store.Cleanups == 1);
        Assert.False(File.Exists(stale));
        Assert.True(store.LastCleanup!.ScanComplete);
    }

    [Fact]
    public async Task Unconsumed_detail_result_keeps_automatic_cleanup_waiting()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var allow = 0;
        await using var store = new StampDerivedAssetStore(catalog, Derived,
            maintenance: Policy(() => Volatile.Read(ref allow) != 0));
        // A missing detail result still waits for the canvas to consume it. Exercise the real request
        // lifetime without depending on the platform's native mip resampler.
        var stamp = StampDerivedAssetStore.FileStamp.Read(DerivedCacheMaintenanceTests.Fixture(1))!;
        var header = new StampDerivedAssetStore.Header(1, new string('c', 64), 16, 16, 0,
            stamp, stamp, 0, 1, new string('d', 64));
        await store.RequestMip(header, header.FirstMip).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(store.PerformanceQueueLengths.Pixels > 0);
        Volatile.Write(ref allow, 1);
        await Task.Delay(300);
        Assert.Equal(0, store.Cleanups);
        Assert.False(File.Exists(Path.Combine(Derived, DerivedCacheJanitor.LockFileName)));

        Assert.True(store.TryTakeMip(header.Sha, header.FirstMip, out var mip));
        mip.Image?.Dispose();
        await Until(() => store.Cleanups == 1);
        Assert.True(store.LastCleanup!.ScanComplete);
    }

    [Fact]
    public async Task Explicit_clean_and_purge_bypass_a_deferred_automatic_pass()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var stale = StaleWrite();
        var polls = 0;
        await using var store = new StampDerivedAssetStore(catalog, Derived,
            maintenance: Policy(() => { Interlocked.Increment(ref polls); return false; }));
        await Until(() => Volatile.Read(ref polls) >= 3);
        var clean = await store.CleanNowAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clean.ScanComplete);
        Assert.False(File.Exists(stale));
        var stem = Path.Combine(Derived, "v1", new string('c', 64));
        File.WriteAllBytes(stem + ".json", new byte[10]);
        File.WriteAllBytes(stem + ".pixels", new byte[20]);
        var purge = await store.PurgeDerived([new string('c', 64)]).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(purge.Completed);
        Assert.Equal(1, purge.Entries);
        Assert.False(File.Exists(stem + ".pixels"));
    }

    [Fact]
    public async Task Ceiling_exceeded_cleans_immediately_even_with_pending_requests_and_automatic_disabled()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var stem = Path.Combine(Derived, "v1", new string('c', 64));
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllBytes(stem + ".json", new byte[10]);
        File.WriteAllBytes(stem + ".pixels", new byte[20]);
        await using var store = new StampDerivedAssetStore(catalog, Derived, diskBudgetBytes: 1,
            maintenance: Policy(() => false) with { Automatic = false });
        // The same pressure decision used after publishing a preparation; no native image build needed.
        store.CheckCleanupPressure(30, 1, 30);
        await Until(() => store.Cleanups > 0);
        Assert.True(store.LastCleanup!.LockAcquired);
        Assert.True(store.LastCleanup.CleanupTriggered);
        Assert.True(store.LastCleanup.BytesDeleted > 0);
        Assert.Equal(0, store.LastCleanup.BytesKept);
        Assert.False(File.Exists(stem + ".pixels"));
    }

    [Fact]
    public async Task Long_timeout_allows_automatic_cleanup_even_if_the_view_stays_busy()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var stale = StaleWrite();
        await using var store = new StampDerivedAssetStore(catalog, Derived,
            maintenance: Policy(() => false) with { MaximumDeferral = TimeSpan.FromMilliseconds(100) });
        await Until(() => store.Cleanups > 0);
        Assert.False(File.Exists(stale));
        Assert.True(store.LastCleanup!.ScanComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_running_pass_pauses_for_new_demand_and_resumes_or_accepts_an_explicit_command(bool explicitCommand)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var stale = StaleWrite();
        // Enough distinct groups to put the pause inside the first scan, before any deletion.
        for (var i = 1; i <= 50; i++)
        {
            var stem = Path.Combine(Derived, "v1", i.ToString("x64"));
            File.WriteAllBytes(stem + ".json", new byte[10]);
            File.WriteAllBytes(stem + ".pixels", new byte[20]);
        }
        var idle = 1;
        var scanning = 0;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool ViewIdle()
        {
            if (File.Exists(Path.Combine(Derived, DerivedCacheJanitor.LockFileName))
                && Interlocked.Increment(ref scanning) == 20)
            {
                Volatile.Write(ref idle, 0);
                paused.TrySetResult();
            }
            return Volatile.Read(ref idle) != 0;
        }
        await using var store = new StampDerivedAssetStore(catalog, Derived, maintenance: Policy(ViewIdle));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.Equal(0, store.Cleanups);
        Assert.True(File.Exists(stale));
        if (explicitCommand)
        {
            var report = await store.CleanNowAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(report.ScanComplete);
        }
        else
        {
            Volatile.Write(ref idle, 1);
            await Until(() => store.Cleanups > 0);
        }
        Assert.False(File.Exists(stale));
        Assert.True(store.LastCleanup!.FilesScanned >= 300);
    }

    [Fact]
    public async Task Scans_yield_in_slices_without_losing_entries()
    {
        Directory.CreateDirectory(Path.Combine(Derived, "v1"));
        for (var i = 1; i <= 10; i++)
        {
            var stem = Path.Combine(Derived, "v1", i.ToString("x64"));
            File.WriteAllBytes(stem + ".json", new byte[10]);
            File.WriteAllBytes(stem + ".pixels", new byte[20]);
        }
        var janitor = new DerivedCacheJanitor(Derived, new()
        {
            ScanSlice = TimeSpan.FromTicks(1), ScanPause = TimeSpan.FromMilliseconds(1),
        }, () => 1L << 50);
        var report = await janitor.CleanAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(report.ScanComplete);
        Assert.Empty(report.Errors);
        Assert.Equal(60, report.FilesScanned); // three complete scans of twenty owned files
        Assert.True(report.ScanYields >= 60);
        Assert.Equal(300, report.BytesKept);
        Assert.Equal(0, report.FilesDeleted);
    }

    [Fact]
    public async Task Pressure_classification_also_checks_activity_between_header_reads()
    {
        Directory.CreateDirectory(Path.Combine(Derived, "v1"));
        for (var i = 1; i <= 10; i++)
        {
            var stem = Path.Combine(Derived, "v1", i.ToString("x64"));
            File.WriteAllText(stem + ".json", "{}");
            File.WriteAllBytes(stem + ".pixels", new byte[20]);
        }
        using var resume = new ManualResetEventSlim();
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var classified = 0;
        var janitor = new DerivedCacheJanitor(Derived, new()
        {
            MaximumBytes = 1,
            Classify = entry =>
            {
                _ = entry.OriginalPath;
                Interlocked.Increment(ref classified);
                return DerivedCacheEntryClass.Live;
            },
        }, () => 1L << 50);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cleanup = Task.Run(() => janitor.Clean(stop.Token, () =>
        {
            if (Volatile.Read(ref classified) > 0 && !resume.IsSet)
            {
                paused.TrySetResult();
                resume.Wait(stop.Token);
            }
        }));
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref classified));
        }
        finally { resume.Set(); await cleanup; }
        Assert.True(cleanup.Result.ScanComplete);
        Assert.Equal(10, classified);
        Assert.Equal(0, cleanup.Result.BytesKept);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
