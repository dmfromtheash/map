using System.Collections.Generic;
using System.Diagnostics;

namespace Map.App.Controls;

public sealed partial class MapCanvasControl
{
    private long _requestedFrame;
    private long _countedFrame = -1;
    private long _statisticsFrameCount;
    private double _statisticsRenderMilliseconds;
    private readonly object _statisticsGate = new();
    private readonly Queue<double> _frameTimes = new();

    private void RecordStatisticsFrame(long request, double renderMilliseconds)
    {
        lock (_statisticsGate)
        {
            // Compositor replay (including repainting this readout) is not a new map frame.
            if (request <= _countedFrame) return;
            _countedFrame = request;
            _statisticsFrameCount++;
            _statisticsRenderMilliseconds = renderMilliseconds;
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            while (_frameTimes.Count > 0 && (now - _frameTimes.Peek() > 1 || _frameTimes.Count >= 512))
                _frameTimes.Dequeue();
            _frameTimes.Enqueue(now);
        }
    }

    /// <summary>Completed distinct map frames, not estimated monitor refresh or 1000/render time.</summary>
    public (double? Fps, double RenderMilliseconds, long Frames) ReadFrameStatistics()
    {
        lock (_statisticsGate)
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var times = _frameTimes.ToArray();
            double? fps = times.Length >= 2 && now - times[^1] < .75 && times[^1] > times[0]
                ? (times.Length - 1) / (times[^1] - times[0])
                : null;
            return (fps, _statisticsRenderMilliseconds, _statisticsFrameCount);
        }
    }
}
