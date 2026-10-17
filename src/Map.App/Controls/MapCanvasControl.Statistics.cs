using System;
using System.Collections.Generic;
using System.Diagnostics;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Controls;

public sealed partial class MapCanvasControl
{
    /// <summary>
    /// A readout in the corner of the canvas: distinct map frames completed in the last second
    /// and the time of the last scene pass. Green from 55 FPS, amber from 30, red below. At rest
    /// the map is not redrawn, so the badge says so instead of counting stray repaints.
    /// </summary>
    public bool ShowFpsBadge { get; set; } = true;

    /// <summary>Whether the last badge drawn showed a moving map; the status timer repaints on a change.</summary>
    public bool FpsBadgeShowedMotion { get; private set; }

    private static readonly SKTypeface BadgeTypeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;

    /// <summary>
    /// Diagnostics: with MAP_FRAME_TRACE=&lt;file&gt; every completed map frame appends one line -
    /// time since the previous frame, scene pass, tier, terrain/objects/stamp costs and cache
    /// counters - so a live session can be read frame by frame afterwards. Off in the product.
    /// </summary>
    private static readonly string? FrameTracePath = Environment.GetEnvironmentVariable("MAP_FRAME_TRACE");
    private static readonly object FrameTraceGate = new();
    private static System.IO.StreamWriter? _frameTrace;
    private static long _frameTraceLast;
    private static readonly Stopwatch FrameTraceClock = Stopwatch.StartNew();

    private void TraceFrame(double sceneMs, RenderLodTier tier, bool fastTerrain, RenderStats stats, long requested, long completed)
    {
        if (FrameTracePath is null) return;
        lock (FrameTraceGate)
        {
            _frameTrace ??= new System.IO.StreamWriter(FrameTracePath, append: true) { AutoFlush = true };
            var now = FrameTraceClock.ElapsedMilliseconds;
            var gap = _frameTraceLast == 0 ? 0 : now - _frameTraceLast;
            _frameTraceLast = now;
            _frameTrace.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{now}\t{gap}\t{sceneMs:0.0}\t{tier}\t{(fastTerrain ? "fast" : "exact")}\trev={completed}\tterrain={stats.TerrainMilliseconds:0.0}\tland={stats.LandMilliseconds:0.0}\twater={stats.WaterMilliseconds:0.0}\tobjects={stats.ObjectsMilliseconds:0.0}\tdrawn={stats.ObjectsDrawn}\tbatch={stats.StampBatchMilliseconds:0.0}/{stats.StampAtlasBatches}\tsingle={stats.StampSingleMilliseconds:0.0}\tmip={stats.StampMipBlendMilliseconds:0.0}/{stats.StampMipBlends}\ttilePrep={stats.DetailPrepareMilliseconds:0.0}\ttileRaster={stats.DetailRasterMilliseconds:0.0}\tterrainReused={stats.TerrainReused}\trefining={stats.TerrainRefinementsPending}\tobjCache={stats.ObjectCacheHits}/{stats.ObjectCacheMisses}\tbehind={requested - completed}\tscale={_editor?.Session.Viewport.Scale:0.###}\tgpu={LastFrameGpuFlushMilliseconds:0.0}\tuncovered={stats.TerrainUncovered}{(stats.TerrainUncovered > 0 ? " " + stats.TerrainUncoveredDetail : "")}"));
        }
    }

    private void DrawFpsBadge(SKCanvas canvas, float width, float height)
    {
        var (fps, renderMs, _) = ReadFrameStatistics();
        var moving = fps is not null && RecentFrameCount() >= 6;
        FpsBadgeShowedMotion = moving;
        // Small and out of the way: the bottom-right corner of the canvas, one line.
        var text = moving ? $"{fps:0} FPS · {renderMs:0.0} мс" : "FPS · покой";
        var colour = !moving ? new SKColor(0xA8, 0x97, 0x7A) : fps >= 55 ? new SKColor(0x8F, 0xD9, 0x8F) : fps >= 30 ? new SKColor(0xE7, 0xC9, 0x82) : new SKColor(0xE5, 0x90, 0x7A);
        using var font = new SKFont(BadgeTypeface, 11);
        var textWidth = font.MeasureText(text);
        var boxWidth = textWidth + 14;
        const float boxHeight = 20;
        var right = width - 10; var bottom = height - 10;
        var box = new SKRect(right - boxWidth, bottom - boxHeight, right, bottom);
        using var back = new SKPaint { Color = new SKColor(0x1A, 0x14, 0x10, 0xB4), IsAntialias = true };
        using var border = new SKPaint { Color = new SKColor(0x8C, 0x6E, 0x3C, 0xA0), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRoundRect(box, 5, 5, back);
        canvas.DrawRoundRect(box, 5, 5, border);
        using var ink = new SKPaint { Color = colour, IsAntialias = true };
        canvas.DrawText(text, right - 7 - textWidth, bottom - 6, font, ink);
    }

    /// <summary>Diagnostics (MAP_FRAME_TRACE): one line per input the canvas handled, with the UI-thread time of its handler.</summary>
    private void TraceInput(string kind, long startedTimestamp)
    {
        if (FrameTracePath is null) return;
        var handlerMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        lock (FrameTraceGate)
        {
            _frameTrace ??= new System.IO.StreamWriter(FrameTracePath, append: true) { AutoFlush = true };
            _frameTrace.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"input\t{FrameTraceClock.ElapsedMilliseconds}\t{kind}\t{handlerMs:0.00}\trev={System.Threading.Interlocked.Read(ref _sceneRevision)}"));
        }
    }

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

    /// <summary>
    /// Distinct map frames completed in the last second. Continuous work (pan, zoom, a stroke)
    /// gives dozens; a count of one or two is a stray repaint, not a frame rate worth showing.
    /// </summary>
    public int RecentFrameCount()
    {
        lock (_statisticsGate)
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var count = 0;
            foreach (var time in _frameTimes) if (now - time <= 1) count++;
            return count;
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
