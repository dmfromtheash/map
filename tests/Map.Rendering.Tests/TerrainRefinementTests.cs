using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class TerrainRefinementTests
{
    // The worker starts on the thread pool; on a busy CI runner the start alone took over 5 s
    // (run 39802054399). The tests check ordering, not speed, so the bound is generous.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_busy_worker_skips_superseded_requests_and_never_returns_an_old_revision()
    {
        using var worker = new TerrainRefinement();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = new WorldBounds(0, 0, 16, 16);
        var first = new object(); var skipped = new object(); var current = new object();
        var skippedBuilds = 0;
        try
        {
            Assert.True(worker.Queue([first], view, 1, () =>
            {
                entered.Set();
                if (!release.Wait(Patience)) throw new TimeoutException();
                return Image(SKColors.Red);
            }, () => ready.TrySetException(new InvalidOperationException("Stale result was published."))));
            Assert.True(entered.Wait(Patience));
            worker.Queue([skipped], view, 1, () => { skippedBuilds++; return Image(SKColors.Green); }, () => { });
            worker.Queue([current], view, 2, () => Image(SKColors.Blue), () => ready.TrySetResult());
            release.Set();
            await ready.Task.WaitAsync(Patience);
            Assert.Equal(0, skippedBuilds);
            Assert.Null(worker.Take([first], view, 1));
            Assert.Null(worker.Take([current], view, 1));
            using var result = worker.Take([current], view, 2);
            Assert.NotNull(result);
            using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
            surface.Canvas.DrawImage(result, 0, 0);
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(0, 0));
            Assert.Null(worker.Take([current], view, 2));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task A_failed_refinement_requests_a_frame_and_allows_a_synchronous_fallback()
    {
        using var worker = new TerrainRefinement();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var part = new object(); var view = new WorldBounds(0, 0, 16, 16);
        worker.Queue([part], view, 1, () => throw new InvalidOperationException("test failure"), () => ready.TrySetResult());
        await ready.Task.WaitAsync(Patience);
        Assert.Equal("test failure", worker.LastError);
        Assert.Null(worker.Take([part], view, 1));
        Assert.False(worker.Queue([part], view, 1, () => Image(SKColors.Green), () => { }));
    }

    private static SKImage Image(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }
}
