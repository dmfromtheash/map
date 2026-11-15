using Map.Rendering.Reserve;

namespace Map.Rendering.Tests;

internal static class FramePlanTestData
{
    public static readonly ReserveDecision Normal = new(ReserveState.Normal, ReserveReasons.None, 0, 1, false, 1, 0, false, false);
    public static readonly FramePlanSample Healthy = new(Normal, 4, 10, true);
    public static readonly FramePlanSample Hot = Healthy with { FrameWorkMs = 8 };

    // Keep transfer ceilings equal here to isolate gesture hysteresis from admission policy.
    public static FramePlanTuning Tuning => new()
    {
        WindowMs = 100, PressureWindows = 2, HealthyReleaseMs = 200,
        WorseningPeriodMs = 300, RecoveryPeriodMs = 500,
        UploadBudget = new(1_000, 10), GestureUploadBudget = new(1_000, 10),
        AdoptionBudget = new(2_000, 20), GestureAdoptionBudget = new(2_000, 20)
    };

    public sealed class Stream(FramePlanTuning? tuning = null)
    {
        public FramePlanner Planner { get; } = new(tuning ?? Tuning);
        public double NextTime { get; private set; }
        public FramePlan Last => Planner.Current;

        public FramePlan To(double inclusiveTime, FramePlanSample sample)
        {
            while (NextTime <= inclusiveTime)
            {
                Planner.Plan(sample, NextTime);
                NextTime += 20;
            }
            return Last;
        }
    }

    public static int ChangedKnobs(FramePlan before, FramePlan after) =>
        (before.SceneResolutionScale != after.SceneResolutionScale ? 1 : 0)
        + (before.UploadBudget != after.UploadBudget ? 1 : 0)
        + (before.AdoptionBudget != after.AdoptionBudget ? 1 : 0)
        + (before.UseCameraFrame != after.UseCameraFrame ? 1 : 0)
        + (before.BackgroundConcurrencyHint != after.BackgroundConcurrencyHint ? 1 : 0)
        + (before.StopPrefetch != after.StopPrefetch ? 1 : 0);
}
