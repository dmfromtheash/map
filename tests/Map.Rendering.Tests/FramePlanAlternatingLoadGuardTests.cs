using Map.Rendering.Reserve;
using static Map.Rendering.Tests.FramePlanTestData;

namespace Map.Rendering.Tests;

public sealed class FramePlanAlternatingLoadGuardTests
{
    [Fact]
    public void Alternating_load_cannot_flip_quality_twice_in_fifteen_frames()
    {
        const int minimumFrames = 15; // 300 ms worsening dwell / 20 ms per replay frame
        var planner = new FramePlanner(Tuning);
        var lastChange = -minimumFrames;
        var changes = 0;
        var previous = planner.Current.SceneResolutionScale;
        for (var frame = 0; frame < 500; frame++)
        {
            var sample = (frame % 2 == 0 ? Hot : Healthy) with
            {
                Reserve = Normal with { GestureResolutionScale = frame % 2 == 0 ? .5 : 1 }
            };
            var scale = planner.Plan(sample, frame * 20).SceneResolutionScale;
            if (scale == previous) continue;
            Assert.True(frame - lastChange >= minimumFrames,
                $"Quality changed at frames {lastChange} and {frame}; minimum separation is {minimumFrames}.");
            lastChange = frame;
            previous = scale;
            changes++;
        }
        Assert.Equal(2, changes); // exercise both reductions; a permanently frozen planner fails too
    }
}
