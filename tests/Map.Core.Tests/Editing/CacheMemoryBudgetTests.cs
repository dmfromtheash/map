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

    // ---------------------------------------------------------------- dynamic budget, DEC-111 p.3

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Machine
    {
        public long Load;
        public WorkingMemorySnapshot Read() => new(Physical, Physical - Volatile.Read(ref Load), 512 * MiB);
    }

    private static long Threshold => (long)(Physical * GcThresholdRatio());
    private static long GrowLine => Threshold - Physical * WorkingMemoryBudget.CacheBudgetGrowMarginPercent / 100;
    private static long ShrinkLine => Threshold - Physical * WorkingMemoryBudget.CacheBudgetShrinkMarginPercent / 100;

    [Theory]
    [InlineData(4L * 1024, 1024)]
    [InlineData(16L * 1024, 4096)]
    [InlineData(64L * 1024, 4096)]
    [InlineData(1L * 1024, 256)]
    public void The_maximum_is_a_quarter_of_physical_memory_within_its_clamp(long physicalMiB, long expectedMiB) =>
        Assert.Equal(expectedMiB * MiB, WorkingMemoryBudget.CalculateMaximumCacheBudget(physicalMiB * MiB));

    [Fact]
    public void Free_memory_grows_the_budget_step_by_step_to_a_quarter_of_ram()
    {
        var machine = new Machine { Load = Physical / 4 };
        var clock = new ManualClock();
        var budget = new WorkingMemoryBudget(machine.Read, clock);
        using var cache = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 8L * 1024 * MiB);
        Assert.Equal(2048 * MiB, budget.CacheBudgetBytes);
        budget.PollCachePressure();
        Assert.Equal(2560 * MiB, budget.CacheBudgetBytes);
        Assert.Equal(2560 * MiB, cache.LimitBytes);
        // One step per interval, however often the poll runs.
        budget.PollCachePressure();
        Assert.Equal(2560 * MiB, budget.CacheBudgetBytes);
        for (var i = 0; i < 10; i++) { clock.Advance(WorkingMemoryBudget.CacheBudgetGrowInterval); budget.PollCachePressure(); }
        Assert.Equal(4096 * MiB, budget.CacheBudgetBytes);
        Assert.Equal(4096 * MiB, cache.LimitBytes);
        Assert.Equal(2048 * MiB, budget.BaseCacheBudgetBytes);
        Assert.Equal(4, budget.CacheBudgetChanges);
    }

    [Fact]
    public void Busy_memory_returns_to_an_eighth_at_once_and_waits_before_growing_again()
    {
        var machine = new Machine { Load = Physical / 4 };
        var clock = new ManualClock();
        var budget = new WorkingMemoryBudget(machine.Read, clock);
        using var cache = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 8L * 1024 * MiB);
        for (var i = 0; i < 5; i++) { budget.PollCachePressure(); clock.Advance(WorkingMemoryBudget.CacheBudgetGrowInterval); }
        Assert.Equal(4096 * MiB, budget.CacheBudgetBytes);

        Volatile.Write(ref machine.Load, ShrinkLine + 16 * MiB);
        budget.PollCachePressure();
        Assert.Equal(2048 * MiB, budget.CacheBudgetBytes);
        Assert.Equal(2048 * MiB, cache.LimitBytes);
        Assert.False(budget.CachesUnderPressure); // still below the GC threshold: no trimming, only a lower cap

        // Memory is free again at once, but the budget waits out the regrow delay.
        Volatile.Write(ref machine.Load, Physical / 4);
        clock.Advance(WorkingMemoryBudget.CacheBudgetRegrowDelay - TimeSpan.FromSeconds(1));
        budget.PollCachePressure();
        Assert.Equal(2048 * MiB, budget.CacheBudgetBytes);
        clock.Advance(TimeSpan.FromSeconds(1));
        budget.PollCachePressure();
        Assert.Equal(2560 * MiB, budget.CacheBudgetBytes);
    }

    [Fact]
    public void Between_the_grow_and_shrink_lines_the_budget_holds_still()
    {
        var machine = new Machine { Load = Physical / 4 };
        var clock = new ManualClock();
        var budget = new WorkingMemoryBudget(machine.Read, clock);
        using var cache = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 8L * 1024 * MiB);
        budget.PollCachePressure();
        var held = budget.CacheBudgetBytes;
        Assert.True(held > 2048 * MiB);
        foreach (var load in new[] { GrowLine + 16 * MiB, (GrowLine + ShrinkLine) / 2, ShrinkLine - 16 * MiB, GrowLine + 16 * MiB })
        {
            Volatile.Write(ref machine.Load, load);
            clock.Advance(WorkingMemoryBudget.CacheBudgetRegrowDelay);
            budget.PollCachePressure();
            Assert.Equal(held, budget.CacheBudgetBytes);
        }
        Assert.Equal(1, budget.CacheBudgetChanges);
    }

    [Fact]
    public void Bytes_the_caches_may_still_fill_count_so_a_full_cache_stays_below_the_grow_line()
    {
        // Room for exactly one 512 MiB step once the granted-but-empty 2 GiB is counted.
        var machine = new Machine { Load = GrowLine - 2560 * MiB };
        var clock = new ManualClock();
        var budget = new WorkingMemoryBudget(machine.Read, clock);
        using var cache = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 8L * 1024 * MiB);
        budget.PollCachePressure();
        Assert.Equal(2560 * MiB, budget.CacheBudgetBytes);
        clock.Advance(WorkingMemoryBudget.CacheBudgetGrowInterval);
        budget.PollCachePressure();
        Assert.Equal(2560 * MiB, budget.CacheBudgetBytes);
        // The caches fill what was granted; the load rises by exactly that and reaches the grow line, not the shrink line.
        cache.Report(2560 * MiB);
        Volatile.Write(ref machine.Load, GrowLine);
        for (var i = 0; i < 20; i++) { clock.Advance(WorkingMemoryBudget.CacheBudgetRegrowDelay); budget.PollCachePressure(); }
        Assert.Equal(2560 * MiB, budget.CacheBudgetBytes);
        Assert.Equal(1, budget.CacheBudgetChanges);
    }

    [Fact]
    public void Pressure_keeps_the_budget_at_an_eighth()
    {
        var machine = new Machine { Load = Physical / 4 };
        var budget = new WorkingMemoryBudget(machine.Read, new ManualClock()) { SimulatedCachePressure = true };
        using var cache = budget.RegisterCache("overview", CacheRebuildCost.Expensive, 8L * 1024 * MiB);
        budget.PollCachePressure();
        Assert.Equal(2048 * MiB, budget.CacheBudgetBytes);
        Assert.True(budget.CachesUnderPressure);
    }

    [Fact]
    public void The_fixed_test_machine_keeps_a_fixed_budget()
    {
        var shared = WorkingMemoryBudget.Shared;
        shared.PollCachePressure();
        Assert.Equal(WorkingMemoryBudget.CalculateCacheBudget(16L * 1024 * MiB), shared.CacheBudgetBytes);
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
