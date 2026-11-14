using Map.Core.Resources;

namespace Map.Core.Tests.Editing;

public sealed class PreparedDataBudgetTests
{
    private const long GiB = PreparedDataBudget.GiB;

    [Theory]
    // free GiB, prepared GiB, expected total GiB: 5 GiB at most, at most 10 % of free, 10 GiB always left free.
    [InlineData(500, 0, 5)]
    [InlineData(51, 0, 5)]
    [InlineData(30, 0, 3)]
    [InlineData(12, 1, 1.2)]
    [InlineData(10, 0, 0)]
    [InlineData(8, 4, 0.8)]
    [InlineData(8, 1, 0)]
    public void Total_is_five_gib_clamped_by_free_space(double free, double prepared, double expected)
    {
        var budget = new PreparedDataBudget();
        Assert.Equal((long)(expected * GiB), PreparedDataBudget.CalculateTotal(budget.TotalBytes, budget.FreeSpaceFraction,
            budget.MinimumFreeBytes, (long)(free * GiB), (long)(prepared * GiB)));
    }

    [Fact]
    public void Default_policy_is_five_gib_with_derived_assets_and_a_reserved_pyramid_share()
    {
        var budget = PreparedDataBudget.Shared;
        Assert.Equal(5 * GiB, budget.TotalBytes);
        Assert.Equal(10 * GiB, budget.MinimumFreeBytes);
        Assert.Equal(0.10m, budget.FreeSpaceFraction);
        Assert.Equal([PreparedDataBudget.DerivedAssets, PreparedDataBudget.DocumentPyramid], budget.Shares);
        Assert.Equal(1m, budget.Shares.Sum(share => share.GuaranteedFraction));
    }

    [Fact]
    public void A_share_borrows_what_the_others_leave_and_gives_back_their_guarantee()
    {
        var budget = new PreparedDataBudget(1000, 1, 0);
        var derived = PreparedDataBudget.DerivedAssets;
        var pyramid = PreparedDataBudget.DocumentPyramid;
        // No pyramid yet: the asset cache may use the whole total.
        Assert.Equal(1000, budget.CeilingFor(derived, 1_000_000, 0));
        budget.ReportUsage(pyramid, 300);
        Assert.Equal(700, budget.CeilingFor(derived, 1_000_000, 0));
        // The pyramid grows past its guarantee: the asset cache still keeps its own 60 %.
        budget.ReportUsage(pyramid, 900);
        Assert.Equal(600, budget.CeilingFor(derived, 1_000_000, 0));
        // And the pyramid, with the assets holding 1000 borrowed bytes, is held to its 40 % until they shrink.
        budget.ReportUsage(derived, 1000);
        Assert.Equal(400, budget.CeilingFor(pyramid, 1_000_000, 0));
        Assert.Equal(1000, budget.UsageOf(derived));
    }

    [Fact]
    public void Free_space_clamp_applies_to_all_shares_together()
    {
        var budget = new PreparedDataBudget();
        budget.ReportUsage(PreparedDataBudget.DocumentPyramid, 1 * GiB);
        // 20 GiB free, 1 GiB pyramid + 1 GiB assets: total = min(5, 2, 12) = 2 GiB; assets get max(1.2, 1) GiB.
        Assert.Equal((long)(1.2 * GiB), budget.CeilingFor(PreparedDataBudget.DerivedAssets, 20 * GiB, 1 * GiB));
        Assert.Equal(0, budget.CeilingFor(PreparedDataBudget.DerivedAssets, 9 * GiB, 0));
    }

    [Fact]
    public void Invalid_shares_and_unknown_consumers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new PreparedDataBudget(shares: [new("a", .7m), new("b", .7m)]));
        Assert.Throws<ArgumentException>(() => new PreparedDataBudget(shares: [new("a", .5m), new("a", .1m)]));
        Assert.Throws<ArgumentException>(() => new PreparedDataBudget().ReportUsage(new("other", .1m), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreparedDataBudget(freeSpaceFraction: 2));
    }
}
