namespace Map.Rendering.Reserve;

/// <summary>Joint byte/count ceiling. A zero in either dimension admits no work.</summary>
public readonly record struct FrameWorkBudget(long Bytes, int Count);

/// <summary>
/// Immutable decision to obtain BEFORE StampImages.BeginFrame and retain for the entire frame.
/// Camera reuse means reuse of a valid cached frame, with scene fallback if none is available.
/// RequireExact describes the requested scene, not completion of asynchronous refinement.
/// This contract has no renderer consumers yet; constructing it never changes rendering.
/// </summary>
public readonly record struct FramePlan(
    double SceneResolutionScale,
    FrameWorkBudget UploadBudget,
    FrameWorkBudget AdoptionBudget,
    bool UseCameraFrame,
    int BackgroundConcurrencyHint,
    bool StopPrefetch)
{
    // Exactness is derived, never another independently adjustable quality knob.
    public bool RequireExact => SceneResolutionScale == 1 && !UseCameraFrame;
}

public enum FramePlanKnob
{
    SceneResolution, UploadBudget, AdoptionBudget, CameraFrame, BackgroundConcurrency, Prefetch,
    Exactness
}

public enum FramePlanOwner { GestureResolution, FrameBudget, CameraPolicy, WorkScheduler, ExactnessPolicy }

/// <summary>
/// Cost of a completed frame (not an idle/present interval), normalized by its own refresh period.
/// Invalid cost/refresh measurements are unknown, never recovery evidence. Reserve values are
/// requests, not overrides. ForceExact covers export and other callers requiring an exact scene.
/// </summary>
public readonly record struct FramePlanSample(
    ReserveDecision Reserve,
    double FrameWorkMs,
    double RefreshPeriodMs,
    bool GestureActive,
    bool ForceExact = false);
