namespace Map.Rendering.Reserve;

/// <summary>Initial policy values, not hardware guarantees. Times are monotonic milliseconds.</summary>
public sealed record ReserveTuning
{
    public double WindowMs { get; init; } = 1_000;
    public int MaxSamplesPerWindow { get; init; } = 512;
    public double ProbeMs { get; init; } = 1_000;
    public double GpuPercentile { get; init; } = 0.95;
    public double GpuEnterRatio { get; init; } = 0.75;
    public double GpuExitRatio { get; init; } = 0.55;
    public int PressureWindows { get; init; } = 2;
    public double GpuReleaseMs { get; init; } = 4_000;
    public double MissIntervalRatio { get; init; } = 1.5;
    public double PacingEnterFraction { get; init; } = 0.05;
    public double PacingExitFraction { get; init; } = 0.01;
    public double PacingReleaseMs { get; init; } = 10_000;
    public double MemoryEnterRatio { get; init; } = 0.90;
    public double MemoryExitRatio { get; init; } = 0.75;
    public long AdoptionBytesPerFrame { get; init; } = 8 * 1024 * 1024;
    public long GestureAdoptionBytesPerFrame { get; init; } = 2 * 1024 * 1024;
    public double UploadExitRatio { get; init; } = 0.5;
    public double UploadReleaseMs { get; init; } = 4_000;
    public double EscalationDwellMs { get; init; } = 3_000;
    public double ImprovementDwellMs { get; init; } = 5_000;
    public double InitialReentryDelayMs { get; init; } = 5_000;
    public double MaxReentryDelayMs { get; init; } = 60_000;
    public double ReentryMultiplier { get; init; } = 2;
    public double DeviceCooldownMs { get; init; } = 30_000;
    public double SoftwareProbeCooldownMs { get; init; } = 60_000;
    public double CpuBusyRatio { get; init; } = 0.75;
    public int BusyCpuTileConcurrency { get; init; } = 1;
    public int Level2CpuTileConcurrency { get; init; } = 2;
    public int Level3CpuTileConcurrency { get; init; } = 4;
    public double Level1UploadFactor { get; init; } = 0.75;
    public double Level2UploadFactor { get; init; } = 0.5;
    public double Level3UploadFactor { get; init; } = 0.25;
    public double Level1ResolutionScale { get; init; } = 0.85;
    public double Level2ResolutionScale { get; init; } = 0.75;
    public double Level3ResolutionScale { get; init; } = 0.5;

    internal void Validate()
    {
        static bool Positive(double n) => double.IsFinite(n) && n > 0;
        static bool Fraction(double n) => Positive(n) && n <= 1;
        if (!Positive(WindowMs) || MaxSamplesPerWindow < 1 || !Positive(ProbeMs)
            || !Fraction(GpuPercentile) || !Fraction(GpuEnterRatio) || !Fraction(GpuExitRatio)
            || GpuExitRatio >= GpuEnterRatio || PressureWindows < 1 || !Positive(GpuReleaseMs)
            || !Positive(MissIntervalRatio) || !Fraction(PacingEnterFraction)
            || !Fraction(PacingExitFraction) || PacingExitFraction >= PacingEnterFraction
            || !Positive(PacingReleaseMs) || !Fraction(MemoryEnterRatio) || !Fraction(MemoryExitRatio)
            || MemoryExitRatio >= MemoryEnterRatio || AdoptionBytesPerFrame < 1
            || GestureAdoptionBytesPerFrame < 1 || !Fraction(UploadExitRatio)
            || !Positive(UploadReleaseMs) || !Positive(EscalationDwellMs)
            || !Positive(ImprovementDwellMs) || ImprovementDwellMs < EscalationDwellMs
            || !Positive(InitialReentryDelayMs) || !Positive(MaxReentryDelayMs)
            || MaxReentryDelayMs < InitialReentryDelayMs || !Positive(ReentryMultiplier)
            || ReentryMultiplier < 1 || !Positive(DeviceCooldownMs) || !Positive(SoftwareProbeCooldownMs)
            || !Fraction(CpuBusyRatio) || BusyCpuTileConcurrency < 1 || Level2CpuTileConcurrency < 1
            || Level3CpuTileConcurrency < 1 || !Fraction(Level1UploadFactor)
            || !Fraction(Level2UploadFactor) || !Fraction(Level3UploadFactor)
            || Level2UploadFactor > Level1UploadFactor || Level3UploadFactor > Level2UploadFactor
            || !Fraction(Level1ResolutionScale) || !Fraction(Level2ResolutionScale)
            || !Fraction(Level3ResolutionScale) || Level2ResolutionScale > Level1ResolutionScale
            || Level3ResolutionScale > Level2ResolutionScale)
            throw new ArgumentException("Invalid reserve policy thresholds.");
    }
}
