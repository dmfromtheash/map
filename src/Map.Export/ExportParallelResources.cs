using Map.Core.Resources;

namespace Map.Export;

/// <summary>
/// How many render and compression workers one export may use, with their extra memory reserved
/// on top of the ordinary export reservation. Too little memory steps the counts down, ending at
/// the serial route.
/// </summary>
internal sealed class ExportParallelResources : IDisposable
{
    public const long StampSnapshotBytes = 512 * WorkingMemoryBudget.MiB;
    public int RenderWorkers { get; private init; } = 1;
    public int CompressionWorkers { get; private init; } = 1;
    private IDisposable? _reservation;

    public static ExportParallelResources Acquire(ExportPlan plan, ExportEngineSettings settings, bool hasStamps,
        Map.Core.Documents.MapDocument snapshot)
    {
        // A map under a program style (R09) is drawn tile after tile: the style keeps the previous
        // frame's buffers for the next one, so its pixels depend on that order (found by
        // ParallelExportTests). Its own paint pass is already split over the cores for export.
        var render = snapshot.Style is { Enabled: true, Strength: > 0 } ? 1
            : Math.Clamp(settings.RenderParallelism, 1, Math.Max(1, plan.UsesStreamingPng ? plan.Columns : plan.TileCount));
        var blocks = Math.Max(1, ((long)plan.OutputWidth * 4 + 1) * plan.OutputHeight / (4 * WorkingMemoryBudget.MiB) + 1);
        var compression = plan.UsesStreamingPng ? (int)Math.Clamp(settings.CompressionParallelism, 1, blocks) : 1;
        var surface = checked((long)(Math.Min(plan.TileEdge, plan.OutputWidth) + plan.EffectBleedPixels * 2)
            * (Math.Min(plan.RasterTileHeight, plan.OutputHeight) + plan.EffectBleedPixels * 2) * 16);
        var cache = (long)settings.TileCacheCapacity * Map.Core.Documents.TileMap.TileSize * Map.Core.Documents.TileMap.TileSize * 4;
        while (render > 1 || compression > 1)
        {
            // Surfaces and tile caches of the extra workers, the shared stamp copies, and the raw
            // plus compressed blocks of the compressors. The ordinary export reservation is held.
            var extra = checked((render - 1L) * (surface + cache)
                + (render > 1 && hasStamps ? settings.ConcurrentStampByteCapacity : 0)
                + (compression > 1 ? (compression + 2L) * 16 * WorkingMemoryBudget.MiB : 0));
            var available = settings.AvailableMemoryBytes();
            if (extra <= available - available / 4 - plan.EstimatedRawWorkingBytes
                && WorkingMemoryBudget.Shared.TryReserve(extra, out var held, out _, measured: true))
                return new ExportParallelResources { RenderWorkers = render, CompressionWorkers = compression, _reservation = held };
            if (render >= compression && render > 1) render--;
            else compression--;
        }
        return new ExportParallelResources();
    }

    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
}
