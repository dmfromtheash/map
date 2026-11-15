namespace Map.Rendering.Tests;

public sealed class FrameBudgetTests
{
    [Theory]
    [InlineData(FrameWorkKind.MipAdoption)]
    [InlineData(FrameWorkKind.Overview)]
    [InlineData(FrameWorkKind.Hex)]
    [InlineData(FrameWorkKind.Trims)]
    public void Lower_priority_cannot_spend_the_visible_detail_reserve(FrameWorkKind lower)
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(8, 0);
        Assert.False(budget.TryTake(lower, 1)); // higher reservations leave no share
        budget.Report(lower, 2);
        Assert.False(budget.TryTake(lower, 1));
        Assert.True(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 1));
        budget.Report(FrameWorkKind.VisibleDetailTiles, 1);
        Assert.True(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 1));
    }

    [Theory]
    [InlineData(FrameWorkKind.MipAdoption, FrameWorkKind.Overview, 2)]
    [InlineData(FrameWorkKind.Overview, FrameWorkKind.Hex, 6)]
    [InlineData(FrameWorkKind.Hex, FrameWorkKind.Trims, 14)]
    public void Every_priority_reserves_its_ceiling_for_later_calls(FrameWorkKind higher, FrameWorkKind lower, double reserve)
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(reserve + 1.5, 0);
        budget.Complete(FrameWorkKind.VisibleDetailTiles);
        budget.Complete(FrameWorkKind.DetailPreparation);
        budget.Report(lower, 1);
        Assert.False(budget.TryTake(lower, .9));
        budget.Complete(higher);
        // Complete every intervening higher priority too.
        for (var kind = FrameWorkKind.MipAdoption; kind < higher; kind++) budget.Complete(kind);
        Assert.True(budget.TryTake(lower, .9));
    }

    [Fact]
    public void Ewma_uses_real_cost_and_survives_begin()
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(100, 0);
        budget.Report(FrameWorkKind.VisibleDetailTiles, 4);
        budget.Report(FrameWorkKind.VisibleDetailTiles, 8);
        Assert.Equal(5, budget.EstimateMilliseconds(FrameWorkKind.VisibleDetailTiles));
        budget.Begin(6, 0);
        Assert.Equal(0, budget.SpentMilliseconds(FrameWorkKind.VisibleDetailTiles));
        Assert.True(budget.TryTake(FrameWorkKind.VisibleDetailTiles, .01));
        budget.Report(FrameWorkKind.VisibleDetailTiles, 2); // EWMA = 4.25; remaining = 4
        Assert.Equal(4.25, budget.EstimateMilliseconds(FrameWorkKind.VisibleDetailTiles));
        Assert.False(budget.TryTake(FrameWorkKind.VisibleDetailTiles, .01));
    }

    [Fact]
    // test-change: owner requires deferral when the frame has no time, including the first unit.
    public void Already_late_frame_defers_every_client_each_frame()
    {
        var budget = new FrameBudget(() => 0);
        for (var frame = 0; frame < 3; frame++)
        {
            budget.Begin(16.7, 30);
            foreach (var kind in Enum.GetValues<FrameWorkKind>().Reverse())
            {
                Assert.False(budget.TryTake(kind, 100));
                Assert.False(budget.TryTake(kind, 0));
                Assert.Equal(0, budget.SpentMilliseconds(kind));
            }
            Assert.Equal(0, budget.RemainingMilliseconds);
        }
    }

    [Fact]
    // test-change: a client share is global across layers, not a new allowance per layer.
    public void Generous_frame_still_enforces_the_client_share()
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(100, 0);
        budget.Report(FrameWorkKind.VisibleDetailTiles, 7.9);
        Assert.False(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 20));
        budget.Report(FrameWorkKind.VisibleDetailTiles, .2);
        Assert.False(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 0));
    }

    [Fact]
    public void Unused_completed_reserve_is_available_to_lower_priorities()
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(11, 0);
        budget.Report(FrameWorkKind.Overview, 1);
        Assert.False(budget.TryTake(FrameWorkKind.Overview, 2));
        budget.Complete(FrameWorkKind.VisibleDetailTiles);
        budget.Complete(FrameWorkKind.DetailPreparation);
        Assert.True(budget.TryTake(FrameWorkKind.Overview, 2));
        Assert.False(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 0));
    }

    [Fact]
    public void Ordinary_render_time_counts_and_reported_work_is_not_charged_twice()
    {
        var now = 0d;
        var budget = new FrameBudget(() => now);
        budget.Begin(16, 2);
        now = 5;
        budget.Report(FrameWorkKind.VisibleDetailTiles, 3);
        Assert.Equal(9, budget.RemainingMilliseconds);
        now = 15;
        Assert.Equal(0, budget.RemainingMilliseconds);
        Assert.False(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 1));
    }

    [Fact]
    public void Checking_unfinished_work_does_not_spend_the_share()
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(20, 0);
        Assert.True(budget.TryTake(FrameWorkKind.Overview, .1));
        Assert.True(budget.TryTake(FrameWorkKind.Overview, .1));
        budget.Report(FrameWorkKind.Overview, .1);
        Assert.Equal(.1, budget.SpentMilliseconds(FrameWorkKind.Overview));
        Assert.True(budget.TryTake(FrameWorkKind.Overview, .1));
    }

    [Fact]
    public void Invalid_times_are_rejected()
    {
        var budget = new FrameBudget(() => 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Begin(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Begin(16, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Begin(double.NaN, 0));
        Assert.Throws<InvalidOperationException>(() => budget.TryTake(FrameWorkKind.Hex, 1));
        budget.Begin(16, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Report(FrameWorkKind.Hex, double.PositiveInfinity));
    }

    [Fact]
    public void A_cold_outlier_requests_the_whole_share_without_permanently_deadlocking()
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(16, 0);
        budget.Report(FrameWorkKind.DetailPreparation, 20);
        Assert.False(budget.TryTake(FrameWorkKind.DetailPreparation, .1));
        budget.Begin(16, 0);
        Assert.True(budget.TryTake(FrameWorkKind.DetailPreparation, .1));
        Assert.False(budget.TryTake(FrameWorkKind.DetailPreparation, 4));
    }

    [Fact]
    public void Detail_preparation_and_tiles_share_one_reserve()
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(11, 0);
        budget.Report(FrameWorkKind.MipAdoption, 1);
        Assert.Equal(1, budget.AvailableMilliseconds(FrameWorkKind.MipAdoption));
        budget.Report(FrameWorkKind.DetailPreparation, 3);
        Assert.Equal(1, budget.AvailableMilliseconds(FrameWorkKind.MipAdoption));
    }

    [Fact]
    public void Preparing_a_tile_cannot_spend_its_adoption_share()
    {
        var budget = new FrameBudget(() => 0);
        budget.Begin(16.7, 0);
        Assert.True(budget.TryTake(FrameWorkKind.DetailPreparation, 1));
        budget.Report(FrameWorkKind.DetailPreparation, 4);
        Assert.False(budget.TryTake(FrameWorkKind.DetailPreparation, 1));
        Assert.True(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 1));
        budget.Report(FrameWorkKind.VisibleDetailTiles, 1);
        Assert.True(budget.TryTake(FrameWorkKind.VisibleDetailTiles, 1));
    }

    [Fact]
    public void A_frame_of_the_budget_allocates_nothing()
    {
        // The canvas runs this every frame on the render thread with the real clock.
        var budget = new FrameBudget();
        void Frame()
        {
            budget.Begin(1000d / 60, 1);
            budget.SetCeiling(FrameWorkKind.VisibleDetailTiles, 8);
            foreach (var kind in (ReadOnlySpan<FrameWorkKind>)[FrameWorkKind.MipAdoption, FrameWorkKind.DetailPreparation,
                FrameWorkKind.VisibleDetailTiles, FrameWorkKind.Overview, FrameWorkKind.Hex, FrameWorkKind.Trims])
            {
                if (budget.TryTake(kind, .1)) budget.Report(kind, .01);
                _ = budget.AvailableMilliseconds(kind) + budget.RemainingMilliseconds;
                budget.Complete(kind);
            }
        }
        // Tiered compilation can allocate once on the calling thread when it installs optimised
        // code (0e2aa37): warm until two consecutive batches allocate nothing, then measure.
        for (int warm = 0, clean = 0; clean < 2 && warm < 200; warm++)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 2_000; i++) Frame();
            clean = GC.GetAllocatedBytesForCurrentThread() == start ? clean + 1 : 0;
            if (clean == 0) Thread.Sleep(20);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 2_000; i++) Frame();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
