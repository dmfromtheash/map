using Map.Core.Resources;

namespace Map.Core.Tests.Editing;

public sealed class CacheMemoryBudgetTests
{
    private const long MiB = WorkingMemoryBudget.MiB;
    private const long Physical = 16L * 1024 * MiB;

    private static double GcThresholdRatio()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 && info.HighMemoryLoadThresholdBytes > 0
            ? Math.Clamp((double)info.HighMemoryLoadThresholdBytes / info.TotalAvailableMemoryBytes, .5, .99) : .9;
    }

    [Theory]
    [InlineData(1L * 1024, 256)]
    [InlineData(4L * 1024, 512)]
    [InlineData(16L * 1024, 2048)]
    [InlineData(64L * 1024, 2048)]
    public void The_cache_budget_follows_physical_memory_within_its_clamp(long physicalMiB, long expectedMiB) =>
        Assert.Equal(expectedMiB * MiB, WorkingMemoryBudget.CalculateCacheBudget(physicalMiB * MiB));

    [Fact]
    public void Kinds_share_one_budget_but_instances_of_one_kind_do_not_halve_each_other()
    {
        var budget = new WorkingMemoryBudget(() => new(Physical, 12L * 1024 * MiB, 512 * MiB));
        using var overview = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 1536 * MiB);
        Assert.Equal(1536 * MiB, overview.LimitBytes);
        using var secondWindow = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 1536 * MiB);
        Assert.Equal(1536 * MiB, overview.LimitBytes);
        var assets = budget.RegisterCache("assets", CacheRebuildCost.Moderate, 1536 * MiB);
        // 2 GiB for 16 GiB of RAM, split by what each kind asked for.
        Assert.Equal(1024 * MiB, overview.LimitBytes);
        Assert.Equal(1024 * MiB, assets.LimitBytes);
        assets.Dispose();
        Assert.Equal(1536 * MiB, overview.LimitBytes);
        Assert.DoesNotContain(budget.ReadCacheStatistics(), statistics => statistics.Name == "assets");
    }

    [Fact]
    public void Without_pressure_nothing_is_asked_to_shrink()
    {
        var budget = new WorkingMemoryBudget(() => new(Physical, Physical / 2, 512 * MiB));
        using var cheap = budget.RegisterCache("samples", CacheRebuildCost.Cheap, 64 * MiB);
        budget.PollCachePressure();
        Assert.False(budget.CachesUnderPressure);
        Assert.Equal(long.MaxValue, cheap.TargetBytes);
        Assert.Equal(0, budget.CachePressureEvents);
    }

    [Fact]
    public void Pressure_reaches_the_cheap_caches_first_and_the_expensive_ones_last()
    {
        var budget = new WorkingMemoryBudget(() => new(Physical, Physical / 100, 512 * MiB));
        using var cheap = budget.RegisterCache("samples", CacheRebuildCost.Cheap, 64 * MiB);
        using var moderate = budget.RegisterCache("assets", CacheRebuildCost.Moderate, 64 * MiB);
        using var expensive = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 64 * MiB);
        budget.PollCachePressure();
        Assert.Equal(0, budget.CachePressureStage);
        Assert.Equal(32 * MiB, cheap.TargetBytes);
        Assert.Equal(long.MaxValue, moderate.TargetBytes);
        Assert.Equal(long.MaxValue, expensive.TargetBytes);
        budget.PollCachePressure();
        Assert.Equal(32 * MiB, moderate.TargetBytes);
        Assert.Equal(long.MaxValue, expensive.TargetBytes);
        budget.PollCachePressure();
        budget.PollCachePressure();
        Assert.Equal(2, budget.CachePressureStage);
        Assert.Equal(32 * MiB, expensive.TargetBytes);
        Assert.Equal(1, budget.CachePressureEvents);
    }

    [Fact]
    public void Pressure_ends_only_below_the_threshold_margin()
    {
        var ratio = GcThresholdRatio();
        var load = (long)(Physical * ratio) + 64 * MiB;
        var budget = new WorkingMemoryBudget(() => new(Physical, Physical - Volatile.Read(ref load), 512 * MiB));
        using var cheap = budget.RegisterCache("samples", CacheRebuildCost.Cheap, 64 * MiB);
        budget.PollCachePressure();
        Assert.True(budget.CachesUnderPressure);
        // Just under the threshold, inside the 5 % margin: still short of memory, no flapping.
        Volatile.Write(ref load, (long)(Physical * ratio) - Physical / 50);
        budget.PollCachePressure();
        Assert.True(budget.CachesUnderPressure);
        Volatile.Write(ref load, (long)(Physical * (ratio - .1)));
        budget.PollCachePressure();
        Assert.False(budget.CachesUnderPressure);
        Assert.Equal(long.MaxValue, cheap.TargetBytes);
        // The same in-margin load does not start pressure by itself.
        Volatile.Write(ref load, (long)(Physical * ratio) - Physical / 50);
        budget.PollCachePressure();
        Assert.False(budget.CachesUnderPressure);
    }

    [Fact]
    public void Statistics_carry_bytes_peaks_and_trims()
    {
        var budget = new WorkingMemoryBudget(() => new(Physical, Physical / 2, 512 * MiB));
        using var account = budget.RegisterCache("assets", CacheRebuildCost.Moderate, 64 * MiB);
        account.Report(40 * MiB);
        account.Report(10 * MiB);
        account.RecordTrim(30 * MiB, 7);
        var statistics = Assert.Single(budget.ReadCacheStatistics());
        Assert.Equal(10 * MiB, statistics.Bytes);
        Assert.Equal(40 * MiB, statistics.PeakBytes);
        Assert.Equal(1, statistics.Trims);
        Assert.Equal(30 * MiB, statistics.TrimmedBytes);
        Assert.Equal(7, statistics.TrimmedItems);
    }
}
