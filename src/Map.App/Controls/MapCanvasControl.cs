using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using Map.Core.Editing;
using Map.Platform.Windows;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Controls;

/// <summary>
/// The editor canvas: hands its drawing straight to Skia through a custom draw operation - no
/// WebView, no intermediate bitmap. Input: left click selects, left drag moves the selected
/// object (one history entry per drag), middle drag pans, wheel zooms around the cursor.
/// </summary>
public sealed class MapCanvasControl : Control
{
    private EditorViewModel? _editor;
    private Point? _lastPointer;
    private (double X, double Y)? _dragStartWorld;
    private bool _isPanning;
    private bool _isStroking;
    private bool _isVectorDrawing;
    private bool _isTextPlacing;
    private bool _isVectorElementDragging;
    private bool _isMarquee;
    private bool _isRegionSelecting;
    private bool _visualInvalidationQueued;
    private Point _marqueePressScreen;
    private readonly TileImageCache _tileCache = new(512);
    private readonly ObjectOverviewCache _objectCache = new(1_024, 64L * 1024 * 1024);
    private readonly RenderStats _renderStats = new();

    public bool IsSpacePanHeld { get; set; }
    public CanvasPerformanceMode PerformanceMode { get; set; } = CanvasPerformanceMode.Balanced;
    public double LastPressureSample { get; private set; } = 1;

    public MapCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Name = "CanvasControl";
        AutomationProperties.SetAutomationId(this, "MapCanvas");
        AutomationProperties.SetName(this, "Холст карты");
        DetachedFromVisualTree += (_, _) =>
        {
            _editor?.CancelPlacement();
            DisposeRenderCaches();
        };
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MapCanvasAutomationPeer(this);

    private sealed class MapCanvasAutomationPeer(MapCanvasControl owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(MapCanvasControl);
    }

    public event EventHandler? ViewportChanged;

    /// <summary>Raised after a stamp gesture so the library panel can re-read the tool state.</summary>
    public event EventHandler? StampPlaced;

    /// <summary>Raised when Alt+wheel moved the current variant of the selected set.</summary>
    public event EventHandler? VariantCycled;

    /// <summary>
    /// Own-process instrumentation hook raised after the Skia scene pass finishes for an Avalonia
    /// frame. It is used by the hidden Stage 13 native profile, never persisted or telemetered.
    /// </summary>
    public event EventHandler? FrameRendered;

    public RenderLodTier LastLodTier { get; private set; } = RenderLodTier.Exact;
    public double LastSceneRenderMilliseconds { get; private set; }
    public int ObjectCacheCount => _objectCache.Count;
    public long ObjectCacheBytes => _objectCache.Bytes;
    public long ObjectCacheDisposals => _objectCache.Disposals;
    public int TileCacheCount => _tileCache.Count;
    public RenderStats LastRenderStats => _renderStats;

    /// <summary>Own-process native-smoke hook; normal lifetime calls the same path on detach.</summary>
    public void DisposeRenderCaches()
    {
        _tileCache.Dispose();
        _objectCache.Dispose();
    }

    /// <summary>Own-process native-smoke pan through the same viewport/event/invalidation path.</summary>
    public void PanByScreenDeltaForSmoke(double dx, double dy)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.Session.Viewport.PanByScreenDelta(dx, dy);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        QueueVisualInvalidation();
    }

    /// <summary>Own-process smoke hook through the same snapped preview state as pointer move.</summary>
    public void MovePlacementPointerForSmoke(double screenX, double screenY)
    {
        if (_editor is null)
        {
            return;
        }

        var world = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
        _editor.UpdatePlacementPreview(world.X, world.Y);
        QueueVisualInvalidation();
    }

    /// <summary>Own-process smoke hook through the same commit path as a left canvas press.</summary>
    public bool CommitPlacementForSmoke()
    {
        var committed = _editor?.CommitPendingPlacement() == true;
        QueueVisualInvalidation();
        return committed;
    }

    /// <summary>Own-process smoke hook through the same secondary-pointer cancel path as input.</summary>
    public bool CancelPlacementWithSecondaryPointerForSmoke() => CancelPlacementFromSecondaryPointer();

    public void Attach(EditorViewModel editor)
    {
        _editor?.SceneChanged -= OnSceneChanged;
        _editor?.SceneInvalidated -= OnSceneInvalidated;

        _editor = editor;
        _editor.SceneChanged += OnSceneChanged;
        _editor.SceneInvalidated += OnSceneInvalidated;
        QueueVisualInvalidation();
    }

    private void OnSceneChanged(object? sender, EventArgs e) =>
        QueueVisualInvalidation();

    private void OnSceneInvalidated(object? sender, EditorSceneInvalidatedEventArgs e)
    {
        if (e.Has(EditorChangeReason.CanvasInvalidation))
        {
            QueueVisualInvalidation();
        }
    }

    /// <summary>At most one Avalonia invalidation is queued for the next render tick.</summary>
    private void QueueVisualInvalidation()
    {
        if (_visualInvalidationQueued)
        {
            return;
        }

        _visualInvalidationQueued = true;
        InvalidateVisual();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _visualInvalidationQueued = false;
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext context)
    {
        if (_editor is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        _editor.Session.Viewport.Resize(Bounds.Width, Bounds.Height);
        context.Custom(new SceneDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height), _editor, _tileCache, _objectCache,
            _renderStats, PerformanceMode,
            _isPanning || _isStroking || _isVectorDrawing || _isTextPlacing
            || _isVectorElementDragging || _isRegionSelecting
            || _dragStartWorld is not null,
            (tier, elapsedMilliseconds) =>
            {
                LastLodTier = tier;
                LastSceneRenderMilliseconds = elapsedMilliseconds;
                FrameRendered?.Invoke(this, EventArgs.Empty);
            }));
    }

    private sealed class SceneDrawOperation(
        Rect bounds, EditorViewModel editor, TileImageCache tileCache,
        ObjectOverviewCache objectCache, RenderStats renderStats,
        CanvasPerformanceMode performanceMode, bool fastPreview,
        Action<RenderLodTier, double> frameRendered) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => Bounds.Contains(p);

        // Every frame carries fresh viewport/document state; never reuse a previous frame.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (feature is null)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var viewport = editor.Session.Viewport;
            var (X, Y) = viewport.ScreenToWorld(0, 0);
            var bottomRight = viewport.ScreenToWorld(Bounds.Width, Bounds.Height);

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            canvas.Clear(new SKColor(16, 18, 22, 255)); // canvas surround

            canvas.Scale((float)viewport.Scale);
            canvas.Translate((float)-viewport.OffsetX, (float)-viewport.OffsetY);

            // A drag stays a bounded render overlay over the immutable session revision. Canvas
            // resize preview is a different workflow and still supplies its explicit document.
            var renderDocument = editor.DragPreview is null
                ? editor.RenderDocument
                : editor.Session.Document;
            var hasLiveObjectPreview = editor.DragPreview is not null || editor.HasPendingPlacement;
            var tier = performanceMode == CanvasPerformanceMode.Performance && viewport.Scale < 0.25
                ? RenderLodTier.Overview
                : viewport.Scale < 0.125
                ? RenderLodTier.Overview
                : performanceMode != CanvasPerformanceMode.Quality
                    && (fastPreview || hasLiveObjectPreview || editor.StrokePreview is not null)
                    ? RenderLodTier.Interactive
                    : RenderLodTier.Exact;
            var sceneStopwatch = Stopwatch.StartNew();
            SceneRenderer.Render(canvas, renderDocument, new Map.Rendering.RenderOptions
            {
                SelectedObjectId = editor.CanvasWorkflowOpen ? null : editor.Session.SelectedObjectId,
                CoSelectedObjectIds = !editor.CanvasWorkflowOpen && editor.Session.SelectedObjectIds.Count > 1
                    ? editor.Session.SelectedObjectIds
                    : null,
                SelectionFrame = editor.CanvasWorkflowOpen ? null : editor.SelectionFrame,
                Marquee = editor.CanvasWorkflowOpen ? null : editor.MarqueeWorld,
                ViewScale = viewport.Scale,
                VisibleWorld = new Map.Core.Documents.WorldBounds(
                    X, Y, bottomRight.X, bottomRight.Y),
                ObjectOverrides = editor.DragPreview?.Proposed,
                PlacementPreviewObjects = editor.PlacementPreviewObjects,
                PlacementPreviewLayerId = editor.PlacementPreviewLayerId,
                PlacementArea = editor.PendingPlacement is
                    { PointerVisible: true, Target: PlacementTargetKind.Stamp,
                      Mode: StampPlacementMode.Area } placement
                    ? new Map.Core.Documents.WorldBounds(
                        placement.WorldX - editor.StampAreaRadius,
                        placement.WorldY - editor.StampAreaRadius,
                        placement.WorldX + editor.StampAreaRadius,
                        placement.WorldY + editor.StampAreaRadius)
                    : null,
                PlacementInvalid = editor.PendingPlacement?.InvalidReason is not null,
                TileCache = tileCache,
                ObjectCache = objectCache,
                LodTier = tier,
                Preview = editor.StrokePreview,
                MaskEditLayerId = editor.MaskEditLayerId,
                Stamps = editor.StampImages,
                FastPreview = fastPreview || hasLiveObjectPreview || editor.StrokePreview is not null
                    || editor.HasPendingPlacement,
                Stats = renderStats,
                BoundaryOverlay = editor.CanvasBoundaryOverlay,
            });
            sceneStopwatch.Stop();

            canvas.Restore();
            frameRendered(tier, sceneStopwatch.Elapsed.TotalMilliseconds);
        }

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor is null)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        _lastPointer = position;

        if (point.Properties.IsMiddleButtonPressed
            || point.Properties.IsLeftButtonPressed && IsSpacePanHeld)
        {
            _isPanning = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            CancelPlacementFromSecondaryPointer();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var world = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);

        if (_editor.CanvasWorkflowOpen)
        {
            if (_editor.CanvasWorkflowMode == ResizeRegionMode.Region)
            {
                _isRegionSelecting = _editor.BeginRegionSelection(world.X, world.Y);
                if (_isRegionSelecting)
                {
                    e.Pointer.Capture(this);
                }
            }

            e.Handled = true;
            QueueVisualInvalidation();
            return;
        }

        if (_editor.ActiveTool is EditorTool.Brush or EditorTool.Mask or EditorTool.Land)
        {
            LastPressureSample = Math.Clamp(point.Properties.Pressure, 0, 1);
            _isStroking = _editor.BeginStroke(world.X, world.Y, LastPressureSample);
            if (_isStroking)
            {
                e.Pointer.Capture(this);
            }

            e.Handled = true;
            QueueVisualInvalidation();
            return;
        }

        if (_editor.ActiveTool is EditorTool.Line or EditorTool.Shape)
        {
            _isVectorDrawing = _editor.BeginVectorDraw(world.X, world.Y);
            if (_isVectorDrawing) e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (_editor.ActiveTool == EditorTool.Text)
        {
            _isTextPlacing = _editor.BeginTextPlacement(world.X, world.Y);
            if (_isTextPlacing) e.Pointer.Capture(this);
            e.Handled = true;
            QueueVisualInvalidation();
            return;
        }

        if (_editor.HasPendingPlacement)
        {
            _editor.UpdatePlacementPreview(world.X, world.Y);
            var stamp = _editor.PendingPlacement?.Target == PlacementTargetKind.Stamp;
            _editor.CommitPendingPlacement();
            if (stamp)
            {
                StampPlaced?.Invoke(this, EventArgs.Empty);
            }
            e.Handled = true;
            QueueVisualInvalidation();
            return;
        }

        if (_editor.ActiveTool == EditorTool.Select
            && _editor.BeginVectorElementDrag(world.X, world.Y, _editor.Session.Viewport.Scale))
        {
            _isVectorElementDragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            QueueVisualInvalidation();
            return;
        }

        // One entry point: a gizmo/frame handle grab resizes/rotates the whole selection; a press
        // on an object selects (Ctrl toggles) and starts a move; empty canvas starts a marquee.
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (_editor.BeginCanvasInteraction(world.X, world.Y, _editor.Session.Viewport.Scale, additive))
        {
            case CanvasGesture.Drag:
                _dragStartWorld = world;
                break;
            case CanvasGesture.Marquee:
                _isMarquee = true;
                _marqueePressScreen = position;
                break;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        QueueVisualInvalidation();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is null)
        {
            return;
        }

        var position = e.GetPosition(this);

        if (_isPanning && _lastPointer is { } previous)
        {
            _editor.Session.Viewport.PanByScreenDelta(position.X - previous.X, position.Y - previous.Y);
            _lastPointer = position;
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            QueueVisualInvalidation();
            e.Handled = true;
            return;
        }

        var (X, Y) = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);

        if (_editor.HasPendingPlacement
            && !_isPanning && !_isStroking && !_isVectorDrawing && !_isTextPlacing
            && _dragStartWorld is null && !_isMarquee && !_isRegionSelecting)
        {
            _editor.UpdatePlacementPreview(X, Y);
            Cursor = new Cursor(StandardCursorType.Cross);
            _lastPointer = position;
            e.Handled = true;
            QueueVisualInvalidation();
            return;
        }

        if (_isRegionSelecting)
        {
            _editor.UpdateRegionSelection(X, Y);
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Handled = true;
            QueueVisualInvalidation();
            return;
        }

        if (_isStroking)
        {
            LastPressureSample = Math.Clamp(e.GetCurrentPoint(this).Properties.Pressure, 0, 1);
            _editor.ContinueStroke(X, Y, LastPressureSample);
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Handled = true;
            return;
        }

        if (_isVectorDrawing)
        {
            _editor.ContinueVectorDraw(X, Y);
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Handled = true;
            return;
        }

        if (_isTextPlacing)
        {
            _editor.ContinueTextPlacement(X, Y);
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Handled = true;
            return;
        }

        if (_isVectorElementDragging)
        {
            _editor.UpdateVectorElementDrag(X, Y);
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Handled = true;
            return;
        }

        if (_dragStartWorld is not null)
        {
            _editor.UpdateDragTo(X, Y);
            e.Handled = true;
        }

        if (_isMarquee)
        {
            _editor.UpdateMarquee(X, Y);
            e.Handled = true;
            QueueVisualInvalidation();
        }


        if (!_isStroking && !_editor.CanvasWorkflowOpen)
        {
            // Pointer feedback over the gizmo: resize arrows on the handles, a hand on rotate.
            Cursor = _editor.HitHandle(X, Y, _editor.Session.Viewport.Scale) switch
            {
                HandleKind.Rotate => new Cursor(StandardCursorType.Hand),
                HandleKind.TopLeft => new Cursor(StandardCursorType.TopLeftCorner),
                HandleKind.TopRight => new Cursor(StandardCursorType.TopRightCorner),
                HandleKind.BottomLeft => new Cursor(StandardCursorType.BottomLeftCorner),
                HandleKind.BottomRight => new Cursor(StandardCursorType.BottomRightCorner),
                HandleKind.Top => new Cursor(StandardCursorType.TopSide),
                HandleKind.Bottom => new Cursor(StandardCursorType.BottomSide),
                HandleKind.Left => new Cursor(StandardCursorType.LeftSide),
                HandleKind.Right => new Cursor(StandardCursorType.RightSide),
                _ => Cursor.Default,
            };
        }

        _lastPointer = position;
    }

    private bool CancelPlacementFromSecondaryPointer()
    {
        if (_editor is null)
        {
            return false;
        }

        var cancelled = _editor.CancelPlacement();
        QueueVisualInvalidation();
        return cancelled;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _editor?.SetPlacementPointerVisible(false);
        QueueVisualInvalidation();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_editor is null)
        {
            return;
        }

        if (_dragStartWorld is not null)
        {
            _dragStartWorld = null;
            _editor.EndDrag();
        }

        if (_isMarquee)
        {
            _isMarquee = false;

            // A press-and-release without real movement is a click, not a frame: the press
            // already applied its selection effect, so the empty marquee is simply dropped.
            var moved = e.GetPosition(this) - _marqueePressScreen;
            if (Math.Abs(moved.X) < 3 && Math.Abs(moved.Y) < 3)
            {
                _editor.CancelMarquee();
            }
            else
            {
                _editor.EndMarquee();
            }
        }

        if (_isRegionSelecting)
        {
            _isRegionSelecting = false;
            _editor.EndRegionSelection();
            Cursor = Cursor.Default;
        }

        if (_isStroking)
        {
            _isStroking = false;
            _editor.EndStroke();
            Cursor = Cursor.Default;
        }

        if (_isVectorDrawing)
        {
            _isVectorDrawing = false;
            _editor.EndVectorDraw();
            Cursor = Cursor.Default;
        }

        if (_isTextPlacing)
        {
            _isTextPlacing = false;
            _editor.EndTextPlacement();
            Cursor = Cursor.Default;
        }

        if (_isVectorElementDragging)
        {
            _isVectorElementDragging = false;
            _editor.EndVectorElementDrag();
            Cursor = Cursor.Default;
        }

        _isPanning = false;
        e.Pointer.Capture(null);
        QueueVisualInvalidation();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editor is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var direction = Math.Sign(e.Delta.Y);

        if (_editor.CanvasWorkflowOpen)
        {
            _editor.Session.Viewport.ZoomAt(position.X, position.Y, Math.Pow(1.15, e.Delta.Y));
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            QueueVisualInvalidation();
            e.Handled = true;
            return;
        }

        // Alt+wheel cycles the current variant, but only inside the stamp tool and only when a
        // set is actually selected; everywhere else the wheel keeps its existing meaning.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            && _editor.ActiveTool == EditorTool.Stamp
            && _editor.SelectedSetVariants.Count > 1)
        {
            if (direction > 0)
            {
                _editor.NextVariant();
            }
            else if (direction < 0)
            {
                _editor.PreviousVariant();
            }

            VariantCycled?.Invoke(this, EventArgs.Empty);
            QueueVisualInvalidation();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _editor.RotateSelectedObject(direction,
                fineStep: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _editor.ResizeSelectedObject(direction, largerStep: true);
        }
        else
        {
            _editor.Session.Viewport.ZoomAt(position.X, position.Y, Math.Pow(1.15, e.Delta.Y));
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        QueueVisualInvalidation();
        e.Handled = true;
    }

    public void FitToCanvas()
    {
        if (_editor is null)
        {
            return;
        }

        _editor.Session.Viewport.Resize(Bounds.Width, Bounds.Height);
        _editor.Session.Viewport.FitTo(
            _editor.Session.Document.Canvas.Width,
            _editor.Session.Document.Canvas.Height);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        QueueVisualInvalidation();
    }
}
