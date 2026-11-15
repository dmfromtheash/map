using Map.Rendering.Reserve;
using static Map.Rendering.Tests.FramePlanTestData;

namespace Map.Rendering.Tests;

public sealed class FramePlanOwnershipGuardTests
{
    [Theory]
    [InlineData(FramePlanKnob.SceneResolution)]
    [InlineData(FramePlanKnob.UploadBudget)]
    [InlineData(FramePlanKnob.AdoptionBudget)]
    [InlineData(FramePlanKnob.CameraFrame)]
    [InlineData(FramePlanKnob.BackgroundConcurrency)]
    [InlineData(FramePlanKnob.Prefetch)]
    [InlineData(FramePlanKnob.Exactness)]
    public void A_second_owner_cannot_write_a_knob(FramePlanKnob knob)
    {
        var current = new FramePlanner(Tuning).Current;
        var next = knob switch
        {
            FramePlanKnob.SceneResolution => current with { SceneResolutionScale = .75 },
            FramePlanKnob.UploadBudget => current with { UploadBudget = new(10, 1) },
            FramePlanKnob.AdoptionBudget => current with { AdoptionBudget = new(10, 1) },
            FramePlanKnob.CameraFrame => current with { UseCameraFrame = true },
            FramePlanKnob.BackgroundConcurrency => current with { BackgroundConcurrencyHint = 2 },
            FramePlanKnob.Prefetch => current with { StopPrefetch = true },
            _ => current
        };
        var owner = FramePlanner.OwnerOf(knob);
        Assert.Equal(next, FramePlanChanges.Apply(current, next, knob, owner));
        foreach (var second in Enum.GetValues<FramePlanOwner>().Where(candidate => candidate != owner))
            Assert.Throws<InvalidOperationException>(() => FramePlanChanges.Apply(next, current, knob, second));
    }

    [Fact]
    public void An_owner_cannot_hide_another_knob_in_its_write()
    {
        var current = new FramePlanner(Tuning).Current;
        Assert.Throws<InvalidOperationException>(() => FramePlanChanges.Apply(current,
            current with { SceneResolutionScale = .75, StopPrefetch = true },
            FramePlanKnob.SceneResolution, FramePlanOwner.GestureResolution));
    }
}
