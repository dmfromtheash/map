namespace Map.Rendering.Lighting;

/// <summary>
/// Measurement of the light pass for the performance tool (R09 threshold: ≤ 4 ms p95 on the
/// owner's window). With <see cref="MeasureStages"/> each stage waits for its GPU work, so the
/// times are what the GPU spent, not only the cost of recording the commands. Off in the program.
/// </summary>
public static class LightingDiagnostics
{
    /// <summary>Wait for the GPU after each stage of the pass and record the times below.</summary>
    public static bool MeasureStages { get; set; }

    /// <summary>The map itself drawn into the off-screen scene surface (not part of the light pass).</summary>
    public static double LastSceneMilliseconds { get; internal set; }

    /// <summary>Shape buffers: normals, data and shadow silhouettes of the visible stamps, the shadow blur.</summary>
    public static double LastBuffersMilliseconds { get; internal set; }

    /// <summary>The SkSL light pass over the frame.</summary>
    public static double LastShaderMilliseconds { get; internal set; }

    /// <summary>Stamps drawn into the shape buffers in the last frame.</summary>
    public static int LastShapeDraws { get; internal set; }

    /// <summary>Shape buffers taken from the previous frame (only the light changed).</summary>
    public static bool LastBuffersReused { get; internal set; }

    /// <summary>The light pass alone: buffers and shader, without drawing the map.</summary>
    public static double LastPassMilliseconds => LastBuffersMilliseconds + LastShaderMilliseconds;

    /// <summary>Every measured frame, in order; the tool drains it per route.</summary>
    public static System.Collections.Concurrent.ConcurrentQueue<LightingFrameSample> Samples { get; } = new();
}

/// <summary>One measured frame of the light pass.</summary>
public readonly record struct LightingFrameSample(double SceneMs, double BuffersMs, double ShaderMs, int ShapeDraws, bool BuffersReused)
{
    public double PassMs => BuffersMs + ShaderMs;
}
