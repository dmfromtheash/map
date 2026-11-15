namespace Map.Rendering.Reserve;

[Flags]
public enum ReserveReasons
{
    None = 0,
    WeakProfile = 1 << 0,
    GpuCost = 1 << 1,
    UploadPressure = 1 << 2,
    MemoryPressure = 1 << 3,
    Pacing = 1 << 4,
    DeviceFailure = 1 << 5,
    UnknownTelemetry = 1 << 6
}

public enum ReserveState { Probing, Normal, Assisted, Recovering, Software, RecoveryProbe }

/// <summary>
/// Measurements of one frame. GpuAvailable describes the backend, not timer validity.
/// DeviceLost remains true until the recovery owner reports a usable device; the controller
/// never creates devices or switches rendering backends. VRAM values use the same byte unit.
/// </summary>
public readonly record struct RenderHealthSample(
    double FrameIntervalMs,
    double CpuWorkMs,
    double GpuMs,
    bool GpuTimeValid,
    bool GestureActive,
    long AdoptionBytes,
    long? VramUsageBytes,
    long? VramBudgetBytes,
    bool DeviceLost,
    double RefreshPeriodMs,
    bool GpuAvailable = true,
    bool WeakProfile = false);

/// <summary>
/// Requests for the common frame-plan governor; these values do not change rendering.
/// Resolution/effect requests apply only to gestures. A concurrency hint of zero means
/// no CPU-tile request, not an instruction to disable the governor's existing workers.
/// StopPrefetch is independent of the visible-frame upload limit.
/// </summary>
public readonly record struct ReserveDecision(
    ReserveState State,
    ReserveReasons Reasons,
    int AssistLevel,
    double UploadLimitFactor,
    bool KeepCameraFrameLonger,
    double GestureResolutionScale,
    int CpuTileConcurrencyHint,
    bool SimplerEffects,
    bool StopPrefetch);
