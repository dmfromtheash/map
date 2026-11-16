using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class AdaptiveResourceBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-resource-budget", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Memory_drop_reduces_catalog_limits_and_runs_derived_janitor_without_a_user_edit()
    {
        var profile = new MachineProfile(16, 16 * MachineProfile.GiB, 12 * MachineProfile.GiB);
        using var machine = new MachineProfileMonitor(() => profile);
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var document = new MapDocument(DocumentId.New(), "Budget", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null,
            derivedRoot: Path.Combine(_root, "derived"), machine: machine,
            derivedMaintenance: new() { Automatic = false }, startWatcher: _ => { });
        Assert.Equal((1536 * MachineProfile.MiB, 512 * MachineProfile.MiB), source.CacheByteLimits);
        var cleanupBefore = source.DerivedCleanups;
        var awakened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetFrameReady(() => awakened.TrySetResult());
        profile = profile with { AvailableMemoryBytes = MachineProfile.GiB };
        machine.Refresh();
        await awakened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (source.BeginFrame()) { }
        Assert.Equal((384 * MachineProfile.MiB, 128 * MachineProfile.MiB), source.CacheByteLimits);
        Assert.Equal(128 * MachineProfile.MiB, source.PreparationBytes);
        Assert.Equal(512, source.DirectDrawBudget);
        Assert.Equal(8 * MachineProfile.MiB, source.AdoptedMipBytesPerFrame);
        Assert.Equal(2 * MachineProfile.MiB, source.GestureAdoptedMipBytesPerFrame);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (source.DerivedCleanups == cleanupBefore && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        Assert.True(source.DerivedCleanups > cleanupBefore);
        Assert.NotNull(source.LastDerivedCleanup);
        profile = profile with { AvailableMemoryBytes = 12 * MachineProfile.GiB };
        machine.Refresh();
        using (source.BeginFrame()) { }
        Assert.Equal((1536 * MachineProfile.MiB, 512 * MachineProfile.MiB), source.CacheByteLimits);
        Assert.True(source.EffectiveCacheByteLimits.Sources > 384 * MachineProfile.MiB);
        Assert.True(source.EffectiveCacheByteLimits.Samples > 128 * MachineProfile.MiB);
    }

    [Fact]
    public async Task Catalog_opened_under_pressure_regrows_its_effective_shared_allowance()
    {
        var profile = new MachineProfile(16, 16 * MachineProfile.GiB, MachineProfile.GiB);
        using var machine = new MachineProfileMonitor(() => profile);
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var document = new MapDocument(DocumentId.New(), "Budget", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null, machine: machine);
        Assert.Equal(384 * MachineProfile.MiB, source.EffectiveCacheByteLimits.Sources);
        profile = profile with { AvailableMemoryBytes = 12 * MachineProfile.GiB };
        machine.Refresh();
        using (source.BeginFrame()) { }
        Assert.Equal((1536 * MachineProfile.MiB, 512 * MachineProfile.MiB), source.EffectiveCacheByteLimits);
    }

    [Fact]
    public void Default_asset_cache_follows_pressure_and_recovery_including_its_shared_account()
    {
        var profile = new MachineProfile(16, 16 * MachineProfile.GiB, 12 * MachineProfile.GiB);
        using var machine = new MachineProfileMonitor(() => profile);
        var budget = new Map.Core.Resources.WorkingMemoryBudget(() => new(16 * MachineProfile.GiB, 12 * MachineProfile.GiB, 512 * MachineProfile.MiB));
        using var cache = new AssetImageCache(machine: machine);
        cache.TrackMemory(budget, "default-assets", Map.Core.Resources.CacheRebuildCost.Cheap);
        Assert.Equal(512 * MachineProfile.MiB, cache.EffectiveByteCapacity);
        profile = profile with { AvailableMemoryBytes = MachineProfile.GiB };
        machine.Refresh();
        Assert.Equal(256 * MachineProfile.MiB, cache.EffectiveByteCapacity);
        profile = profile with { AvailableMemoryBytes = 12 * MachineProfile.GiB };
        machine.Refresh();
        Assert.Equal(512 * MachineProfile.MiB, cache.EffectiveByteCapacity);
        Assert.Equal(512 * MachineProfile.MiB, budget.ReadCacheStatistics().Single().LimitBytes);
    }

    [Fact]
    public async Task Real_frame_scopes_feed_the_controller_and_keep_explicit_experiment_overrides()
    {
        using var machine = new MachineProfileMonitor(() => new(16, 16 * MachineProfile.GiB, 12 * MachineProfile.GiB));
        var clock = new ResourceClock();
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var document = new MapDocument(DocumentId.New(), "Budget", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null, machine: machine, frameClock: clock);
        source.DisplayRefreshHz = 144;
        for (var i = 0; i < 700; i++)
        {
            clock.Advance(5);
            using (source.BeginFrame()) clock.Advance(1);
        }
        Assert.Equal(machine.Limits.AdoptBytes, source.AdoptedMipBytesPerFrame);
        Assert.Equal(machine.Limits.GestureAdoptBytes, source.GestureAdoptedMipBytesPerFrame);
        Assert.Equal(machine.Limits.DirectDraws, source.DirectDrawBudget);
        using (source.BeginFrame()) clock.Advance(20);
        Assert.True(source.AdoptedMipBytesPerFrame < machine.Limits.AdoptBytes);
        source.AdoptedMipBytesPerFrame = 123;
        source.GestureAdoptedMipBytesPerFrame = 456;
        Assert.Equal(123, source.AdoptedMipBytesPerFrame);
        Assert.Equal(456, source.GestureAdoptedMipBytesPerFrame);
    }

    [Fact]
    public void Smaller_cache_evicts_unpinned_owner_pixels_but_preserves_a_live_lease()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var pixels = SKBitmap.Decode(Path.Combine(directory.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        Assert.NotNull(pixels);
        var bytes = (long)pixels.Width * pixels.Height * 4;
        using var cache = new AssetImageCache(4, bytes * 4);
        var pinned = cache.AdoptPixels("pinned", SKImage.FromBitmap(pixels))!;
        cache.Pin(pinned);
        for (var i = 0; i < 3; i++) Assert.NotNull(cache.AdoptPixels("owner" + i, SKImage.FromBitmap(pixels)));
        cache.SetLimits(1, bytes);
        cache.TrimToLimits(100);
        Assert.Equal(bytes, cache.ResidentBytes);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Contains("pinned"));
        using var retained = SKBitmap.FromImage(pinned);
        Assert.Equal(pixels.Bytes, retained.Bytes);
        cache.Unpin(pinned);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ResourceClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += milliseconds * TimeSpan.TicksPerMillisecond;
    }
}
