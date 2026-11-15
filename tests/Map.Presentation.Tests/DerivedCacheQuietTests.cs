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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
