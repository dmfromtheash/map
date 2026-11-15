namespace Map.Rendering.Tests;

public sealed class FrameBudgetAdmissionGuardTests
{
    [Theory]
    [InlineData(FrameWorkKind.VisibleDetailTiles)]
    [InlineData(FrameWorkKind.DetailPreparation)]
    [InlineData(FrameWorkKind.MipAdoption)]
    [InlineData(FrameWorkKind.Overview)]
    [InlineData(FrameWorkKind.Hex)]
    [InlineData(FrameWorkKind.Trims)]
    public void Pending_work_defers_before_exceeding_its_share(FrameWorkKind kind)
    {
        var budget = new FrameBudget(() => 0);
        budget.SetCeiling(kind, 1);
        budget.Begin(100, 0);
        var pending = 20;
        while (pending > 0 && budget.TryTake(kind, .25))
        {
            pending--;
            budget.Report(kind, .25);
        }
        Assert.Equal(16, pending);
        Assert.Equal(1, budget.SpentMilliseconds(kind));
        Assert.False(budget.TryTake(kind, .01));
        budget.Begin(16, 30);
        Assert.False(budget.TryTake(kind, .01));
        Assert.Equal(0, budget.SpentMilliseconds(kind));
    }
}
