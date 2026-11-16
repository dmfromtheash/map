using Map.App.Controls;

namespace Map.Presentation.Tests;

/// <summary>
/// PC 2026-11-16 (100 000 objects): the fixed 256 MiB GPU cache re-uploaded full-size stamps every frame at 400-1600 %
/// (pan/1600 5.5 FPS, 138.7 with 1.5 GiB). The cache follows the adapter's budget: a quarter, 256 MiB .. 3 GiB.
/// </summary>
public sealed class GpuMemoryBudgetTests
{
    private const long MiB = 1024 * 1024;

    [Theory]
    [InlineData(-1L, 256L)]          // unknown adapter: the old size
    [InlineData(512L, 256L)]         // a tiny budget never goes below the old size
    [InlineData(4096L, 1024L)]       // integrated GPU with ~4 GiB of shared budget
    [InlineData(7680L, 1920L)]       // RTX 3070 Laptop (8 GiB, ~7.5 GiB budget)
    [InlineData(65536L, 3072L)]      // a huge card is capped
    public void The_cache_is_a_quarter_of_the_adapter_budget_within_bounds(long budgetMiB, long expectedMiB) =>
        Assert.Equal(expectedMiB * MiB, GpuMemoryBudget.ForBudget(budgetMiB < 0 ? null : budgetMiB * MiB));
}
