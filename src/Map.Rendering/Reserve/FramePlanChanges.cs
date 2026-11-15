namespace Map.Rendering.Reserve;

/// <summary>All arbiter writes pass this ownership and single-knob boundary.</summary>
internal static class FramePlanChanges
{
    internal static FramePlan Apply(FramePlan current, FramePlan next, FramePlanKnob knob, FramePlanOwner owner)
    {
        if (owner != FramePlanner.OwnerOf(knob))
            throw new InvalidOperationException("A frame-plan knob has a different owner.");
        var allowed = knob switch
        {
            FramePlanKnob.SceneResolution => current with { SceneResolutionScale = next.SceneResolutionScale },
            FramePlanKnob.UploadBudget => current with { UploadBudget = next.UploadBudget },
            FramePlanKnob.AdoptionBudget => current with { AdoptionBudget = next.AdoptionBudget },
            FramePlanKnob.CameraFrame => current with { UseCameraFrame = next.UseCameraFrame },
            FramePlanKnob.BackgroundConcurrency => current with { BackgroundConcurrencyHint = next.BackgroundConcurrencyHint },
            FramePlanKnob.Prefetch => current with { StopPrefetch = next.StopPrefetch },
            FramePlanKnob.Exactness when next.RequireExact => current with { SceneResolutionScale = 1, UseCameraFrame = false },
            _ => throw new InvalidOperationException("Invalid frame-plan change.")
        };
        if (allowed != next) throw new InvalidOperationException("A frame-plan write changed another owner's knob.");
        return next;
    }
}
