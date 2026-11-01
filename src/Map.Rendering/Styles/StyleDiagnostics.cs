using System.Collections.Concurrent;

namespace Map.Rendering.Styles;

/// <summary>
/// Opt-in performance-tool diagnostics. Every stage flushes and synchronously submits GPU work;
/// timings include submission/wait overhead, not only command recording. Off in normal use.
/// </summary>
public static class StyleDiagnostics
{
    public static bool MeasureStages { get; set; }
    /// <summary>Collect reuse/follow counters without GPU waits (native FPS profiles).</summary>
    public static bool CollectFrames { get; set; }
    public static ConcurrentQueue<StyleFrameSample> Samples { get; } = new();

    // А2 step 2: how long the one-time builds took, on any thread (form maps and height maps are also built by the
    // background preparation). Stopwatch ticks, added on every build; the tools read the change around a click.
    private static long _formMapTicks, _heightMapTicks, _coastTicks;
    public static double FormMapMs => _formMapTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static double HeightMapMs => _heightMapTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static double CoastMs => _coastTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    internal static void AddFormMap(long ticks) => Interlocked.Add(ref _formMapTicks, ticks);
    internal static void AddHeightMap(long ticks) => Interlocked.Add(ref _heightMapTicks, ticks);
    internal static void AddCoast(long ticks) => Interlocked.Add(ref _coastTicks, ticks);
}

/// <summary>Scene is excluded from PassMs. A reused painted frame only pays the composite cost.</summary>
public readonly record struct StyleFrameSample(double SceneMs, double TensorMs, double SoftMs,
    double WideMs, double ShaderMs, double CompositeMs, bool Reused, int Width, int Height)
{
    public bool Followed { get; init; }
    public double PrintMs { get; init; }
    /// <summary>The same paint pass run once more on the same buffers (measured frames only): what is not one-time.</summary>
    public double ShaderRepeatMs { get; init; }
    public int PendingTerrain { get; init; }
    public int PendingDetails { get; init; }
    public int PendingHex { get; init; }
    public int MissingAssets { get; init; }
    public double PassMs => TensorMs + SoftMs + WideMs + PrintMs + ShaderMs + CompositeMs;
}
