using System.Diagnostics;
using Map.Presentation.ViewModels;
using Map.Presentation.Services;

namespace Map.App.Controls;

public sealed partial class MapCanvasControl
{
    /// <summary>Optional host-owned journal. Desktop windows use the application journal by default.</summary>
    public PerformanceJournal? PerformanceJournal { get; set; }

    /// <summary>Diagnostic snapshot for native probes, read between gestures rather than on every frame.</summary>
    public (bool Idle, int Queued, int ReadyImages, long Plans, long Prefetched) BackgroundOverviewStatistics
    {
        get
        {
            var state = _overviewPreparation.Statistics;
            return (state.Idle, state.Queued, state.ReadyImages, state.Plans, state.Prefetched);
        }
    }

    private long _journalPreviousFrame;
    private PerformanceQueues _journalQueues;
    private PerformanceGesture _journalCameraGesture;
    private long _journalOverviewStarts;

    private void RecordPerformanceFrame()
    {
        if ((PerformanceJournal ?? Program.PerformanceJournal) is not { } journal) return;
        var now = Stopwatch.GetTimestamp();
        var interval = _journalPreviousFrame == 0 ? 0 : Stopwatch.GetElapsedTime(_journalPreviousFrame, now).TotalMilliseconds;
        _journalPreviousFrame = now;
        var unavailable = false;
        if (_editor?.StampImages is CatalogStampImageSource images)
        {
            unavailable = !images.TryReadPerformanceQueues(out var queues);
            if (!unavailable) _journalQueues = queues;
        }
        else _journalQueues = default;
        var gesture = _input is { ActiveGesture: CanvasInputGesture.Pan } ? PerformanceGesture.Pan
            : CameraMoving ? _journalCameraGesture
            : _input is { ActiveGesture: not CanvasInputGesture.None } || _editor?.StrokePreview is not null
                || _editor is { HasRiverGesture: true } or { HasRoadGesture: true } or { HasHexGesture: true }
                ? PerformanceGesture.Edit : PerformanceGesture.Idle;
        var started = _overviewPreparation.GestureStatistics.StartedDuringGesture;
        var startedDelta = (int)System.Math.Min(int.MaxValue, System.Math.Max(0, started - _journalOverviewStarts));
        _journalOverviewStarts = started;
        journal.RecordFrame(new PerformanceFrame(LastFrameWorkMilliseconds, interval, gesture,
            _renderStats.ObjectCacheHits + LastFrameStyledLandHits,
            _renderStats.ObjectCacheMisses + LastFrameStyledLandMisses, _renderStats.ObjectCacheInvalidations,
            _renderStats.DetailTilesPending, _renderStats.UnresolvedImages, _renderStats.MissingAssets,
            _journalQueues, unavailable, _renderStats.TerrainRefinementsPending, _renderStats.HexChunksPending,
            _renderStats.OverviewAdoptedPixels, _renderStats.OverviewAdoptionBudgetPixels, startedDelta,
            _editor?.StampImages is CatalogStampImageSource budgetImages ? new Map.Rendering.FrameWorkBudget(
                budgetImages.AdoptedMipBytesPerFrame, budgetImages.GestureAdoptedMipBytesPerFrame, budgetImages.DirectDrawBudget) : null,
            _editor?.StampImages is CatalogStampImageSource targetImages ? targetImages.FrameBudgets.TargetMilliseconds : 0));
    }
}
