using Map.Core.Resources;

namespace Map.Core.Tests.Editing;

public sealed class CacheAccountResizeTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public void Recovered_cache_can_grow_its_request_and_still_shares_the_machine_budget()
    {
        var budget = new WorkingMemoryBudget(() => new(16 * 1024 * MiB, 12 * 1024 * MiB, 512 * MiB));
        using var images = budget.RegisterCache("images", CacheRebuildCost.Moderate, 256 * MiB);
        using var samples = budget.RegisterCache("samples", CacheRebuildCost.Cheap, 1024 * MiB);
        Assert.Equal(256 * MiB, images.LimitBytes);
        images.Resize(3 * 1024 * MiB);
        Assert.Equal(1536 * MiB, images.LimitBytes);
        Assert.Equal(512 * MiB, samples.LimitBytes);
        Assert.Equal(budget.CacheBudgetBytes, images.LimitBytes + samples.LimitBytes);
        images.Resize(256 * MiB);
        Assert.Equal(256 * MiB, images.LimitBytes);
        Assert.Equal(1024 * MiB, samples.LimitBytes);
    }

    [Fact]
    public void Resizing_keeps_pressure_watermarks_and_rejects_invalid_requests()
    {
        var budget = new WorkingMemoryBudget(() => new(16 * 1024 * MiB, 12 * 1024 * MiB, 512 * MiB))
        { SimulatedCachePressure = true };
        using var images = budget.RegisterCache("images", CacheRebuildCost.Cheap, 256 * MiB);
        budget.PollCachePressure();
        images.Resize(512 * MiB);
        Assert.Equal(256 * MiB, images.TargetBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => images.Resize(0));
        images.Dispose();
        Assert.Throws<ObjectDisposedException>(() => images.Resize(512 * MiB));
    }
}
