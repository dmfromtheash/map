using Map.Presentation.Services;

namespace Map.Presentation.Tests;

public sealed class RecentMipRequestsTests
{
    [Fact]
    public void Empty_frames_age_requests_and_repeated_submits_extend_retention()
    {
        var history = new RecentMipRequests<string>();
        history.BeginFrame(1);
        history.Submit("edge");
        for (var frame = 2; frame <= 9; frame++)
        {
            history.BeginFrame(frame);
            Assert.True(history.IsWanted("edge"));
        }
        history.Submit("edge");
        history.BeginFrame(17);
        Assert.True(history.IsWanted("edge"));
        history.BeginFrame(18);
        Assert.False(history.IsWanted("edge"));
        history.Cancelled("edge");
        Assert.Equal(0, history.CancelledStillWanted);
    }

    [Fact]
    public void Counter_detects_premature_cancellation_and_a_return_within_N_frames()
    {
        var history = new RecentMipRequests<string>(new(Frames: 2));
        history.BeginFrame(1);
        history.Submit("early");
        history.Cancelled("early");
        Assert.Equal(1, history.CancelledStillWanted);
        history.BeginFrame(4);
        history.Cancelled("returning");
        history.BeginFrame(6);
        history.Submit("returning");
        history.Submit("returning");
        Assert.Equal(2, history.CancelledStillWanted);
        history.Cancelled("old");
        history.BeginFrame(9);
        history.Submit("old");
        Assert.Equal(2, history.CancelledStillWanted);
    }
}
