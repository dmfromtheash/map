using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Map.Spike.App.Session;
using Map.Spike.Core.Geometry;
using Map.Spike.Rendering;
using SkiaSharp;

namespace Map.Spike.App.Canvas;

/// <summary>
/// The spike canvas: an Avalonia control that hands its drawing straight to Skia through a custom
/// draw operation. No WebView, no intermediate bitmap, no HTTP anything.
/// </summary>
public sealed class MapCanvasControl : Control
{
    private readonly SpikeSession _session;
    private readonly RendererProbe _probe = new();

    private readonly List<double> _frameIntervalsMs = [];
    private readonly List<double> _drawTimesMs = [];

    private DispatcherTimer? _benchmarkTimer;
    private int _benchmarkFramesLeft;
    private int _benchmarkFrameIndex;

    private long _lastFrameTimestamp;
    private Point? _lastPointerPosition;
    private bool _isPanning;
    private bool _isStroking;

    public MapCanvasControl(SpikeSession session)
    {
        _session = session;
        Focusable = true;
        ClipToBounds = true;
    }

    public event EventHandler? StateChanged;

    public RendererProbe Probe => _probe;

    public bool IsBenchmarkRunning => _benchmarkFramesLeft > 0;

    public string BenchmarkSummary { get; private set; } = "frame benchmark: not run yet";

    public string LastFrameSummary { get; private set; } = "frame: not rendered yet";

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        _session.Viewport.Resize(bounds.Width, bounds.Height);

        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp != 0)
        {
            var interval = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalMilliseconds;
            if (IsBenchmarkRunning)
            {
                _frameIntervalsMs.Add(interval);
            }
        }

        _lastFrameTimestamp = now;

        var settings = new RenderSettings
        {
            IncludeOverlay = true,
            ShowMaskEditOverlay = _session.ShowMaskEditOverlay && _session.Mode == ToolMode.MaskErase,
            MaskEnabled = _session.MaskEnabled,
            SelectedObjectId = _session.SelectedObjectId,
            BackgroundColor = new SKColor(22, 24, 28, 255),
        };

        var operation = new SceneDrawOperation(bounds, this, settings);
        context.Custom(operation);

        if (IsBenchmarkRunning)
        {
            _benchmarkFramesLeft--;
            _benchmarkFrameIndex++;
            if (_benchmarkFramesLeft == 0)
            {
                // Posted, never called inline: FinishBenchmark writes the status text, and Avalonia
                // throws "Visual was invalidated during the render pass" if a visual is touched
                // while the compositor is walking the tree. Doing it inline killed the process.
                Dispatcher.UIThread.Post(FinishBenchmark, DispatcherPriority.Background);
            }
        }
    }

    /// <summary>Called from inside the Skia lease. Kept here so the draw operation stays a thin shim.</summary>
    private void RenderScene(ISkiaSharpApiLease lease, RenderSettings settings, Rect bounds)
    {
        _probe.Sample(lease.GrContext, lease.SkSurface);

        var canvas = lease.SkCanvas;
        var stats = default(RenderStats);

        var start = Stopwatch.GetTimestamp();

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));

        if (IsBenchmarkRunning)
        {
            ApplyBenchmarkViewport(_benchmarkFrameIndex);
        }

        SceneRenderer.Render(
            canvas, _session.Document, _session.Viewport, settings,
            _session.TileCache, _session.GeometryCache, ref stats);

        canvas.Restore();

        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (IsBenchmarkRunning)
        {
            _drawTimesMs.Add(elapsed);
        }

        LastFrameSummary =
            $"frame: draw {elapsed:F2} ms, {stats}, " +
            $"tileCache hits={_session.TileCache.Hits} misses={_session.TileCache.Misses}";
    }

    // ---------------------------------------------------------------- benchmark

    /// <summary>
    /// Drives a fixed pan/zoom path for a fixed number of frames and reports the frame interval
    /// actually observed on the UI thread, plus the time spent inside the Skia draw call.
    /// The two are reported separately because only the first one is what the user feels.
    /// </summary>
    public void StartBenchmark(int frames = 300)
    {
        if (IsBenchmarkRunning)
        {
            return;
        }

        _frameIntervalsMs.Clear();
        _drawTimesMs.Clear();
        _benchmarkFrameIndex = 0;

        // Warm-up frames are rendered first and then discarded, so the first-frame shader and
        // texture upload costs do not land in the reported numbers.
        _benchmarkFramesLeft = frames + WarmupFrames;
        BenchmarkSummary = "frame benchmark: running...";

        _benchmarkTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1),
        };

        _benchmarkTimer.Tick += (_, _) => InvalidateVisual();
        _benchmarkTimer.Start();
        InvalidateVisual();
        RaiseStateChanged();
    }

    private const int WarmupFrames = 60;

    private void ApplyBenchmarkViewport(int frameIndex)
    {
        var canvasSize = _session.Document.CanvasWidth;
        var fitScale = Math.Min(Bounds.Width / canvasSize, Bounds.Height / canvasSize);

        // Deterministic path: a slow orbit at fit zoom for the first half, then a zoomed-in pan.
        if (frameIndex < (WarmupFrames + 150))
        {
            _session.Viewport.Set(new Vec2(0, 0), fitScale);
            _session.Viewport.PanByScreenDelta(
                Math.Sin(frameIndex * 0.05) * 60,
                Math.Cos(frameIndex * 0.05) * 60);
        }
        else
        {
            _session.Viewport.Set(
                new Vec2(
                    1400 + (Math.Sin(frameIndex * 0.04) * 500),
                    1400 + (Math.Cos(frameIndex * 0.04) * 500)),
                1.0);
        }
    }

    private void FinishBenchmark()
    {
        _benchmarkTimer?.Stop();
        _benchmarkTimer = null;

        // Drop the warm-up window from both series.
        var intervals = _frameIntervalsMs.Skip(WarmupFrames).ToList();
        var draws = _drawTimesMs.Skip(WarmupFrames).ToList();

        BenchmarkSummary = intervals.Count == 0
            ? "frame benchmark: no samples collected"
            : $"frame benchmark ({intervals.Count} frames after {WarmupFrames} warm-up): " +
              $"interval median {Percentile(intervals, 0.5):F2} ms " +
              $"(={1000 / Percentile(intervals, 0.5):F1} fps), " +
              $"p95 {Percentile(intervals, 0.95):F2} ms " +
              $"(={1000 / Percentile(intervals, 0.95):F1} fps), " +
              $"max {Percentile(intervals, 1):F2} ms | " +
              $"skia draw median {Percentile(draws, 0.5):F2} ms, p95 {Percentile(draws, 0.95):F2} ms";

        RaiseStateChanged();
    }

    private static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var sorted = values.OrderBy(x => x).ToArray();
        var index = (int)Math.Round(q * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var position = e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        _lastPointerPosition = position;

        if (point.Properties.IsMiddleButtonPressed
            || (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            _isPanning = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_session.Mode == ToolMode.Select)
        {
            _session.Select(new Vec2(position.X, position.Y));
        }
        else
        {
            _isStroking = true;
            _session.BeginStroke(ToWorld(position));
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
        RaiseStateChanged();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        if (_isPanning && _lastPointerPosition is { } previous)
        {
            _session.Viewport.PanByScreenDelta(position.X - previous.X, position.Y - previous.Y);
            _lastPointerPosition = position;
            InvalidateVisual();
            RaiseStateChanged();
            e.Handled = true;
            return;
        }

        if (_isStroking)
        {
            _session.ContinueStroke(ToWorld(position));
            _lastPointerPosition = position;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _lastPointerPosition = position;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isStroking)
        {
            _isStroking = false;
            _session.EndStroke();
        }

        _isPanning = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
        RaiseStateChanged();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var position = e.GetPosition(this);
        var factor = Math.Pow(1.15, e.Delta.Y);
        _session.Viewport.ZoomAt(new Vec2(position.X, position.Y), factor);

        e.Handled = true;
        InvalidateVisual();
        RaiseStateChanged();
    }

    public void ZoomToFit()
    {
        _session.ZoomToFit(Bounds.Width, Bounds.Height);
        InvalidateVisual();
        RaiseStateChanged();
    }

    private Vec2 ToWorld(Point screen) =>
        _session.Viewport.ScreenToWorld(new Vec2(screen.X, screen.Y));

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- draw operation

    private sealed class SceneDrawOperation(Rect bounds, MapCanvasControl owner, RenderSettings settings) : ICustomDrawOperation
    {
        private readonly MapCanvasControl _owner = owner;
        private readonly RenderSettings _settings = settings;

        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        // Every frame carries fresh viewport and document state, so two operations are never
        // interchangeable and Avalonia must not reuse a previous frame's result.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                // No Skia backend at all: report it rather than silently drawing nothing.
                _owner.LastFrameSummary = "frame: NO SKIA LEASE AVAILABLE - the scene was not drawn";
                return;
            }

            using var lease = feature.Lease();
            _owner.RenderScene(lease, _settings, Bounds);
        }

        public void Dispose()
        {
        }
    }
}
