using System.Threading;
using System.Threading.Tasks;

namespace Map.App.Graphics;

/// <summary>
/// P95 frame time of a future fixed, versioned startup workload on the selected backend.
/// Null, nonpositive and nonfinite values are unknown. This is not live frame telemetry.
/// The real implementation must bound its work and honor cancellation.
/// </summary>
public readonly record struct GraphicsStartupMeasurement(double? FrameP95Ms = null);

public interface IGraphicsStartupBenchmark
{
    ValueTask<GraphicsStartupMeasurement> MeasureAsync(CancellationToken cancellationToken);
}

/// <summary>Deterministic placeholder; defaults to unmeasured, never invented performance.</summary>
public sealed class FakeGraphicsStartupBenchmark(GraphicsStartupMeasurement measurement = default)
    : IGraphicsStartupBenchmark
{
    public ValueTask<GraphicsStartupMeasurement> MeasureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(measurement);
    }
}
