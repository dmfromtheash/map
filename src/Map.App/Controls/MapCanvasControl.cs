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
    private CanvasInputRouter? _input;
    private Point? _lastPointer;
    private bool _visualInvalidationQueued;
    private readonly TileImageCache _tileCache = new(512);
    private readonly ObjectOverviewCache _objectCache = new(1_024, 64L * 1024 * 1024);
    private readonly RenderStats _renderStats = new();

    public bool IsSpacePanHeld
    {
        get => _input?.SpacePanHeld == true;
        set
        {
            if (_input is not null)
            {
                _input.SpacePanHeld = value;
            }
        }
    }

    public CanvasPerformanceMode PerformanceMode { get; set; } = CanvasPerformanceMode.Balanced;

    public double LastPressureSample => _input?.LastPressureSample ?? 1;

    /// <summary>The routing state machine shared by input, rendering and the native smokes.</summary>
    internal CanvasInputRouter? Input => _input;

    /// <summary>Own-process smoke hook: the very router a real pointer event goes through.</summary>
    public CanvasInputRouter InputForSmoke => _input
        ?? throw new InvalidOperationException("The canvas has no editor attached yet.");

    public MapCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Name = "CanvasControl";
        AutomationProperties.SetAutomationId(this, "MapCanvas");
        AutomationProperties.SetName(this, "Холст карты");
        DetachedFromVisualTree += (_, _) =>
        {
            _input?.Cancel();
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
        if (_input is null)
        {
            return;
        }

        Apply(_input.Move(screenX, screenY));
    }

    /// <summary>Own-process smoke hook through the same commit path as a left canvas press.</summary>
    public bool CommitPlacementForSmoke()
    {
        var committed = _editor?.CommitPendingPlacement() == true;
        QueueVisualInvalidation();
        return committed;
    }

    /// <summary>Own-process smoke hook through the same secondary-pointer cancel path as input.</summary>
    public bool CancelPlacementWithSecondaryPointerForSmoke()
    {
        if (_input is null)
        {
            return false;
        }

        var result = _input.Press(CanvasPointerButton.Right, _lastPointer?.X ?? 0, _lastPointer?.Y ?? 0);
        Apply(result);
        return result.Handled;
    }

    /// <summary>
    /// Own-process smoke hook: one complete left press-move-release through the production router,
    /// so a smoke proves the same routing order a real pointer would take.
    /// </summary>
    public void PressMoveReleaseForSmoke(
        double pressScreenX, double pressScreenY,
        double releaseScreenX, double releaseScreenY,
        bool additive = false)
    {
        if (_input is null)
        {
            return;
        }

        Apply(_input.Press(CanvasPointerButton.Left, pressScreenX, pressScreenY, additive));
        Apply(_input.Move(releaseScreenX, releaseScreenY));
        Apply(_input.Release(releaseScreenX, releaseScreenY));
        _lastPointer = new Point(releaseScreenX, releaseScreenY);
    }

    /// <summary>Own-process smoke hook: a click that never moves, i.e. the selection gesture.</summary>
    public void ClickForSmoke(double screenX, double screenY, bool additive = false) =>
        PressMoveReleaseForSmoke(screenX, screenY, screenX, screenY, additive);

    /// <summary>
    /// The three halves of a gesture, separately, so an own-process check can look at the screen
    /// while the pointer is still down. The composite helper above cannot see a live preview at
    /// all - it presses and releases before anything is ever painted.
    /// </summary>
    public void PressForSmoke(double screenX, double screenY, bool additive = false)
    {
        if (_input is null) return;
        Apply(_input.Press(CanvasPointerButton.Left, screenX, screenY, additive));
    }

    public void MoveForSmoke(double screenX, double screenY)
    {
        if (_input is null) return;
        Apply(_input.Move(screenX, screenY));
    }

    public void ReleaseForSmoke(double screenX, double screenY)
    {
        if (_input is null) return;
        Apply(_input.Release(screenX, screenY));
    }

    /// <summary>Applies one routed result to the Avalonia-facing parts of the control.</summary>
    private void Apply(CanvasInputResult result, PointerEventArgs? source = null)
    {
        if (result.CapturePointer)
        {
            source?.Pointer.Capture(this);
        }

        if (result.ReleasePointer)
        {
            source?.Pointer.Capture(null);
        }

        if (result.ViewportChanged)
        {
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        if (result.StampPlaced)
        {
            StampPlaced?.Invoke(this, EventArgs.Empty);
        }

        if (result.Invalidate)
        {
            QueueVisualInvalidation();
        }

        if (source is not null && result.Handled)
        {
            source.Handled = true;
        }
    }

    public void Attach(EditorViewModel editor)
    {
        var panHeld = IsSpacePanHeld;
        if (_editor is not null)
        {
            _editor.SceneChanged -= OnSceneChanged;
            _editor.SceneInvalidated -= OnSceneInvalidated;
            _editor.CanvasGestureCancelled -= OnCanvasGestureCancelled;
        }

        _editor = editor;
        _input = new CanvasInputRouter(editor) { SpacePanHeld = panHeld };
        _editor.SceneChanged += OnSceneChanged;
        _editor.SceneInvalidated += OnSceneInvalidated;
        _editor.CanvasGestureCancelled += OnCanvasGestureCancelled;
        QueueVisualInvalidation();
    }

    /// <summary>
    /// The editor dropped an unfinished gesture (a tool switch today). The control drops its own
    /// pointer state in the same step, so no stale flag can commit on a later release.
    /// </summary>
    private void OnCanvasGestureCancelled(object? sender, EventArgs e)
    {
        _input?.Cancel();
        Cursor = Cursor.Default;
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

    /// <summary>
    /// The viewport learns its size during layout, not during rendering. It used to be resized
    /// inside <see cref="Render"/>, so a resize could publish editor state from the middle of a
    /// render pass and make the side panel rebuild its rows mid-frame - which showed up as a
    /// briefly empty layer list right after the window changed size.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        if (_editor is not null
            && (Math.Abs(_editor.Session.Viewport.ViewWidth - arranged.Width) > 0.01
                || Math.Abs(_editor.Session.Viewport.ViewHeight - arranged.Height) > 0.01))
        {
            _editor.Session.Viewport.Resize(arranged.Width, arranged.Height);
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            QueueVisualInvalidation();
        }

        return arranged;
    }

    public override void Render(DrawingContext context)
    {
        if (_editor is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        context.Custom(new SceneDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height), _editor, _tileCache, _objectCache,
            _renderStats, PerformanceMode,
            _input is { ActiveGesture: not CanvasInputGesture.None and not CanvasInputGesture.Marquee },
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
            // The parent paints the approved watercolor surround. Clearing this leased surface
            // would cover that real bitmap with the editor's former near-black background.

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
                // The land region drag borrows the marquee outline: it is the same idea on
                // screen - "this is the area the next action covers".
                Marquee = editor.CanvasWorkflowOpen
                    ? null
                    : editor.MarqueeWorld ?? editor.LandShapePreview,
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

    // The control no longer decides what a press means. It translates Avalonia input into router
    // calls and applies the routed result: capture, cursor, invalidation and events. The routing
    // order itself lives in CanvasInputRouter so it can be regression-tested without a window.

    private static CanvasPointerButton ButtonOf(PointerPoint point)
    {
        if (point.Properties.IsMiddleButtonPressed) return CanvasPointerButton.Middle;
        if (point.Properties.IsRightButtonPressed) return CanvasPointerButton.Right;
        return point.Properties.IsLeftButtonPressed ? CanvasPointerButton.Left : CanvasPointerButton.None;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_input is null)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        _lastPointer = position;
        ApplyDrawModifiers(e.KeyModifiers);

        Apply(
            _input.Press(
                ButtonOf(point),
                position.X,
                position.Y,
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                point.Properties.Pressure),
            e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is null || _input is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        ApplyDrawModifiers(e.KeyModifiers);
        Apply(_input.Move(position.X, position.Y, e.GetCurrentPoint(this).Properties.Pressure), e);
        _lastPointer = position;
        SyncCursor(position);
    }

    /// <summary>
    /// The two drawing modifiers, read on every pointer event rather than tracked as key state:
    /// what matters is what is held at the moment the shape is being sized, and a missed KeyUp
    /// must never leave the tool stuck constraining every later gesture.
    /// </summary>
    private void ApplyDrawModifiers(KeyModifiers modifiers)
    {
        if (_editor is null)
        {
            return;
        }

        _editor.VectorConstrainProportions = modifiers.HasFlag(KeyModifiers.Shift);
        _editor.VectorDrawFromCenter = modifiers.HasFlag(KeyModifiers.Alt);
    }

    /// <summary>Pointer feedback: the gizmo cursors win, then the gesture cursor, then default.</summary>
    private void SyncCursor(Point position)
    {
        if (_editor is null || _input is null)
        {
            return;
        }

        if (_input.IsStroking || _editor.CanvasWorkflowOpen)
        {
            return;
        }

        switch (_input.ActiveGesture)
        {
            case CanvasInputGesture.VectorDraw:
            case CanvasInputGesture.TextPlacement:
                Cursor = new Cursor(StandardCursorType.Cross);
                return;
            case CanvasInputGesture.VectorElementDrag:
                Cursor = new Cursor(StandardCursorType.Hand);
                return;
        }

        if (_editor.HasPendingPlacement && _input.ActiveGesture == CanvasInputGesture.None)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        var (worldX, worldY) = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);
        Cursor = _editor.HitHandle(worldX, worldY, _editor.Session.Viewport.Scale) switch
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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_input is not null)
        {
            Apply(_input.PointerLeft());
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_input is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        Apply(_input.Release(position.X, position.Y), e);
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        _lastPointer = position;
    }

    /// <summary>
    /// Capture can be revoked without a release - a modal dialog, an Alt+Tab, a device change.
    /// Without this the control kept believing a drag was running and committed it later.
    /// </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_input?.Cancel() == true)
        {
            Cursor = Cursor.Default;
            QueueVisualInvalidation();
        }
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
