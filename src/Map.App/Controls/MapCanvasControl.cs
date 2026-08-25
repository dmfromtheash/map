using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Map.Core.Editing;
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
    private readonly TileImageCache _tileCache = new(512);

    public MapCanvasControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Name = "CanvasControl";
        AutomationProperties.SetAutomationId(this, "MapCanvas");
        AutomationProperties.SetName(this, "Холст карты");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new MapCanvasAutomationPeer(this);

    private sealed class MapCanvasAutomationPeer(MapCanvasControl owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(MapCanvasControl);
    }

    public event EventHandler? ViewportChanged;

    public void Attach(EditorViewModel editor)
    {
        _editor?.SceneChanged -= OnSceneChanged;

        _editor = editor;
        _editor.SceneChanged += OnSceneChanged;
        InvalidateVisual();
    }

    private void OnSceneChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext context)
    {
        if (_editor is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        _editor.Session.Viewport.Resize(Bounds.Width, Bounds.Height);
        context.Custom(new SceneDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height), _editor, _tileCache));
    }

    private sealed class SceneDrawOperation(
        Rect bounds, EditorViewModel editor, TileImageCache tileCache) : ICustomDrawOperation
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

            SceneRenderer.Render(canvas, editor.RenderDocument, new Map.Rendering.RenderOptions
            {
                SelectedObjectId = editor.Session.SelectedObjectId,
                ViewScale = viewport.Scale,
                VisibleWorld = new Map.Core.Documents.WorldBounds(
                    X, Y, bottomRight.X, bottomRight.Y),
                TileCache = tileCache,
                Preview = editor.StrokePreview,
                MaskEditLayerId = editor.MaskEditLayerId,
            });

            canvas.Restore();
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

        if (point.Properties.IsMiddleButtonPressed)
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

        var world = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);

        if (_editor.ActiveTool is EditorTool.Brush or EditorTool.Mask)
        {
            _isStroking = _editor.BeginStroke(world.X, world.Y);
            if (_isStroking)
            {
                e.Pointer.Capture(this);
            }

            e.Handled = true;
            InvalidateVisual();
            return;
        }

        // One entry point: a gizmo handle grab resizes/rotates the current selection; otherwise
        // the press selects and, on an editable object, starts a move.
        if (_editor.BeginCanvasInteraction(world.X, world.Y, _editor.Session.Viewport.Scale))
        {
            _dragStartWorld = world;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
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
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var (X, Y) = _editor.Session.Viewport.ScreenToWorld(position.X, position.Y);

        if (_isStroking)
        {
            _editor.ContinueStroke(X, Y);
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Handled = true;
            return;
        }

        if (_dragStartWorld is not null)
        {
            _editor.UpdateDragTo(X, Y);
            e.Handled = true;
        }


        if (_isStroking)
        {
            _isStroking = false;
            _editor.EndStroke();
        }
        else
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

        _isPanning = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editor is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        _editor.Session.Viewport.ZoomAt(position.X, position.Y, Math.Pow(1.15, e.Delta.Y));

        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
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
        InvalidateVisual();
    }
}
