namespace Map.Rendering.Reserve;

/// <summary>
/// Starting policy from RESERVE_ENGINE §3; these are not measured hardware guarantees.
/// Frame-work max replaces GPU p95 here: the governor also protects CPU/software frames.
/// Transfer ceilings start at the existing 8/2 MiB adoption limits; other clients supply theirs.
/// </summary>
public sealed record FramePlanTuning
{
    public double WindowMs { get; init; } = 1_000;
    public double EnterRatio { get; init; } = 0.75;
    public double ExitRatio { get; init; } = 0.55;
    public int PressureWindows { get; init; } = 2;
    public double HealthyReleaseMs { get; init; } = 4_000;
    public double WorseningPeriodMs { get; init; } = 3_000;
    public double RecoveryPeriodMs { get; init; } = 5_000;
    public double ReducedResolutionScale { get; init; } = 0.75;
    public double MinimumResolutionScale { get; init; } = 0.5;
    public double BusyTransferFactor { get; init; } = 0.5;
    public FrameWorkBudget UploadBudget { get; init; } = new(8 * 1024 * 1024, 16);
    public FrameWorkBudget AdoptionBudget { get; init; } = new(8 * 1024 * 1024, 16);
    public FrameWorkBudget GestureUploadBudget { get; init; } = new(2 * 1024 * 1024, 16);
    public FrameWorkBudget GestureAdoptionBudget { get; init; } = new(2 * 1024 * 1024, 16);
    public int BackgroundConcurrency { get; init; } = 1;
    public int MaxBackgroundConcurrency { get; init; } = 4;
    public int BusyBackgroundConcurrency { get; init; } = 1;

    internal void Validate()
    {
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        static bool Fraction(double n) => Positive(n) && n <= 1;
        static bool Budget(FrameWorkBudget b) => b.Bytes >= 0 && b.Count >= 0;
        static bool Within(FrameWorkBudget a, FrameWorkBudget b) => a.Bytes <= b.Bytes && a.Count <= b.Count;
        if (!Positive(WindowMs) || !Fraction(EnterRatio) || !Fraction(ExitRatio) || ExitRatio >= EnterRatio
            || PressureWindows < 1 || !Positive(HealthyReleaseMs) || !Positive(WorseningPeriodMs)
            || !Positive(RecoveryPeriodMs) || RecoveryPeriodMs <= WorseningPeriodMs
            || !Fraction(ReducedResolutionScale) || ReducedResolutionScale >= 1
            || !Fraction(MinimumResolutionScale) || MinimumResolutionScale >= ReducedResolutionScale
            || !Fraction(BusyTransferFactor) || !Budget(UploadBudget) || !Budget(AdoptionBudget)
            || !Budget(GestureUploadBudget) || !Budget(GestureAdoptionBudget)
            || !Within(GestureUploadBudget, UploadBudget) || !Within(GestureAdoptionBudget, AdoptionBudget)
            || BackgroundConcurrency < 1 || MaxBackgroundConcurrency < BackgroundConcurrency
            || BusyBackgroundConcurrency < 1 || BusyBackgroundConcurrency > BackgroundConcurrency)
            throw new ArgumentException("Invalid frame-plan policy thresholds.");
    }
}
