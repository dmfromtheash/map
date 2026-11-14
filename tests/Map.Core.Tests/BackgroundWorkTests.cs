using System.Diagnostics;
using Map.Core.Resources;

namespace Map.Core.Tests;

public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task Own_threads_are_below_normal_and_wait_out_a_gesture_while_other_threads_never_wait()
    {
        BackgroundWork.NoteInteraction();
        var watch = Stopwatch.StartNew();
        BackgroundWork.YieldToInteraction(); // the test thread is not a background thread: no wait
        Assert.True(watch.ElapsedMilliseconds < 100);

        ThreadPriority? priority = null;
        var owned = false;
        var yieldsBefore = BackgroundWork.Yields;
        BackgroundWork.NoteInteraction();
        var noted = Stopwatch.StartNew();
        await BackgroundWork.RunAsync(() =>
        {
            priority = Thread.CurrentThread.Priority;
            owned = BackgroundWork.IsBackgroundThread;
        }, "background work probe");
        // The item itself started only after the gesture's hold ran out.
        Assert.True(noted.Elapsed >= BackgroundWork.InteractionHold - TimeSpan.FromMilliseconds(30), $"started after {noted.ElapsedMilliseconds} ms");
        Assert.Equal(ThreadPriority.BelowNormal, priority);
        Assert.True(owned);
        Assert.True(BackgroundWork.Yields > yieldsBefore);
        Assert.InRange(BackgroundWork.Concurrency, 1, Math.Max(1, Environment.ProcessorCount));
    }

    [Fact]
    public async Task A_failure_reaches_the_awaiting_caller()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BackgroundWork.RunAsync(() => throw new InvalidOperationException("probe"), "background failure probe"));
    }
}
