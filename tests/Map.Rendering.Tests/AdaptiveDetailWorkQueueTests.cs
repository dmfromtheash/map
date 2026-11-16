namespace Map.Rendering.Tests;

[Collection("OverviewExactLayers")]
public sealed class AdaptiveDetailWorkQueueTests
{
    [Fact]
    public void Detail_workers_apply_the_current_limit_and_drain_running_work_before_admitting_more()
    {
        var limit = 1;
        using var queue = new SceneRenderer.DetailWorkQueue(2, () => Volatile.Read(ref limit));
        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        using var third = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        try
        {
            queue.Add(() => { first.Set(); releaseFirst.Wait(); });
            Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
            queue.Add(() => { second.Set(); releaseSecond.Wait(); });
            Assert.False(second.Wait(TimeSpan.FromMilliseconds(50)), "a second worker escaped the one-lane budget");
            Volatile.Write(ref limit, 2);
            queue.Add(() => { }); // Wake the idle worker after the profile recovers.
            Assert.True(second.Wait(TimeSpan.FromSeconds(5)));
            Volatile.Write(ref limit, 1);
            queue.Add(third.Set);
            releaseFirst.Set();
            Assert.False(third.Wait(TimeSpan.FromMilliseconds(50)), "new work competed with a lane still draining");
            releaseSecond.Set();
            Assert.True(third.Wait(TimeSpan.FromSeconds(5)));
        }
        finally { releaseFirst.Set(); releaseSecond.Set(); }
    }
}
