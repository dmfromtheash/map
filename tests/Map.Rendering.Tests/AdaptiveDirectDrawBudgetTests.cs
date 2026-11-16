namespace Map.Rendering.Tests;

[Collection("OverviewExactLayers")]
public sealed class AdaptiveDirectDrawBudgetTests
{
    [Fact]
    public void Scene_renderer_consumes_the_sources_budget_instead_of_a_fixed_512()
    {
        using var view = new ColdLibraryWholeMapTests.WholeMapView(50_000);
        view.Images.DirectDrawBudget = 256;
        var frames = view.PendingFrames();
        Assert.All(frames, frame => Assert.InRange(frame.DirectObjects, 0, 256));
        Assert.Contains(frames, frame => frame.DirectObjects == 256);
    }
}
