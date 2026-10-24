using System.Collections.Concurrent;

namespace Map.Rendering.Styles;

/// <summary>
/// Opt-in performance-tool diagnostics. Every stage flushes and synchronously submits GPU work;
/// timings include submission/wait overhead, not only command recording. Off in normal use.
/// </summary>
public static class StyleDiagnostics
{
    public static bool MeasureStages { get; set; }
    public static ConcurrentQueue<StyleFrameSample> Samples { get; } = new();
}

/// <summary>Scene is excluded from PassMs. A reused painted frame only pays the composite cost.</summary>
public readonly record struct StyleFrameSample(double SceneMs, double TensorMs, double SoftMs,
    double WideMs, double ShaderMs, double CompositeMs, bool Reused, int Width, int Height)
{
    public double PassMs => TensorMs + SoftMs + WideMs + ShaderMs + CompositeMs;
}
