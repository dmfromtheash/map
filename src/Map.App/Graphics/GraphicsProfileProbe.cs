using System;
using System.Threading;
using System.Threading.Tasks;

namespace Map.App.Graphics;

public sealed record GraphicsProfileProbeResult(
    GraphicsAdapterInfo Info, GraphicsStartupMeasurement Measured, GraphicsProfile Profile, string? CacheKey);

/// <summary>Opt-in startup operation only; no startup wiring, file IO or rendering changes.</summary>
public sealed class GraphicsProfileProbe(IGraphicsAdapterSource source, IGraphicsStartupBenchmark benchmark,
    GraphicsProfileTuning? tuning = null)
{
    public async ValueTask<GraphicsProfileProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backend = TryRead(source.ReadBackend) ?? new();
        var adapter = TryRead(() => source.ReadAdapter(backend.AdapterLuid)) ?? new();
        if (backend.AdapterLuid is not ulong selected || adapter.AdapterLuid != selected)
            adapter = new();
        var info = adapter with
        {
            Backend = backend.Backend,
            AngleAvailable = backend.AngleAvailable,
            IsWarp = backend.IsWarp,
            IsRemoteSession = TryRead(source.ReadRemoteSession)
        };
        GraphicsStartupMeasurement measured = default;
        try { measured = await benchmark.MeasureAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Failure is unknown measurement, not a slow/absent GPU. */ }
        cancellationToken.ThrowIfCancellationRequested();
        return new(info, measured, GraphicsProfile.Classify(info, measured, tuning), GraphicsProfile.CreateCacheKey(info));
    }

    private static T? TryRead<T>(Func<T?> read)
    {
        try { return read(); }
        catch (Exception) { return default; }
    }
}
