namespace Map.Rendering.Tests;

[CollectionDefinition("OverviewExactLayers", DisableParallelization = true)]
public sealed class OverviewExactLayersCollection;

[Collection("OverviewExactLayers")]
public sealed class OverviewExactQueueTests
{
    [Fact]
    public void Visible_work_overtakes_queued_layers_and_layers_wait_for_running_tiles()
    {
        using var queue = new SceneRenderer.DetailWorkQueue(2);
        using var entered = new CountdownEvent(2);
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var visibleRan = new ManualResetEventSlim();
        using var layerRan = new ManualResetEventSlim();
        try
        {
            queue.Add(() => { entered.Signal(); releaseFirst.Wait(); });
            queue.Add(() => { entered.Signal(); releaseSecond.Wait(); });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            queue.Add(layerRan.Set, overviewLayer: true);
            queue.Add(visibleRan.Set);
            releaseFirst.Set();
            Assert.True(visibleRan.Wait(TimeSpan.FromSeconds(10)), "the queued layer delayed a visible tile");
            Assert.False(layerRan.Wait(TimeSpan.FromMilliseconds(30)), "the layer competed with a running visible tile");
            releaseSecond.Set();
            Assert.True(layerRan.Wait(TimeSpan.FromSeconds(10)), "the optional layer never ran after the tiles drained");
        }
        finally { releaseFirst.Set(); releaseSecond.Set(); }
    }
}
