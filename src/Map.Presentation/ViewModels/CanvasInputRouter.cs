using System;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>The canvas buttons the router understands, independent of any UI framework.</summary>
public enum CanvasPointerButton
{
    None,
    Left,
    Middle,
    Right,
}

/// <summary>The gesture the canvas is currently running, or <see cref="None"/> when it is idle.</summary>
public enum CanvasInputGesture
{
    None,
    Measure,
    Pan,
    RegionSelect,
    Stroke,
    StampSeries,
    /// <summary>Alt held in the scatter mode: the circle removes stamps instead of placing them.</summary>
    StampErase,
    PlacementPathNode,
    PlacementAreaNode,
    RiverNode,
    RiverStroke,
    RoadNode,
    RoadStroke,
    VectorDraw,
    LandShape,
    TextPlacement,
    VectorElementDrag,
    ObjectDrag,
    Marquee,
    /// <summary>A drag of the hex tool: places or erases hexes along the way.</summary>
    HexPaint,
}

/// <summary>What the hosting control has to do for its UI framework after a routed event.</summary>
public readonly record struct CanvasInputResult
{
    public bool Handled { get; init; }

    public bool CapturePointer { get; init; }

    public bool ReleasePointer { get; init; }

    public bool Invalidate { get; init; }

    public bool ViewportChanged { get; init; }

    public bool StampPlaced { get; init; }

    public static CanvasInputResult Ignored => default;
}

/// <summary>
/// The single entry point that turns raw canvas pointer input into editor gestures.
/// <para>
/// It exists as its own production type for two reasons. First, the routing order used to live
/// inline in the Avalonia control, where a drawing tool could swallow a press before anything
/// hit-tested the scene, so an existing object became unreachable while that tool was active.
/// Second, the gesture flags used to be private control fields that no tool switch, capture loss
/// or detach ever reset, which left phantom drags that committed history the user never asked for.
/// Keeping both the order and the state here means one place owns the contract - and the contract
/// is testable without a window.
/// </para>
/// <para>
/// The routing contract, for every tool:
/// a press first finishes or cancels whatever gesture is already pending; a press on a handle of
/// the current selection always transforms that selection; a press that starts no tool gesture
/// falls through to selection instead of being silently dropped; and a press-release without real
/// movement is a click, which selects the object underneath rather than drawing nothing.
/// </para>
/// </summary>
public sealed class CanvasInputRouter
{
    /// <summary>Screen-space travel below which a press-release counts as a click, not a drag.</summary>
    public const double ClickThresholdPixels = 3;

    private readonly EditorViewModel _editor;

    public CanvasInputRouter(EditorViewModel editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        // The editor drops unfinished gestures on its own when the tool changes. The router has to
        // forget its matching state in the same step, or the next release would end a gesture the
        // editor no longer has - which is exactly how a tool switch used to commit a stray edit.
        _editor.CanvasGestureCancelled += (_, _) => ForgetGesture();
    }

    private CanvasInputGesture _gesture;
    private bool _vectorDrawIsTentative;
    private bool _notePendingAtPress;
    private double _pressScreenX;
    private double _pressScreenY;
    private double _pressWorldX;
    private double _pressWorldY;
    private bool _pressAdditive;
    private double _lastScreenX;
    private double _lastScreenY;
    private bool _hasLastScreen;

    public CanvasInputGesture ActiveGesture => _gesture;

    public bool IsPanning => _gesture == CanvasInputGesture.Pan;

    public bool IsStroking => _gesture == CanvasInputGesture.Stroke;

    /// <summary>Set by the host while the space bar is held, which turns a left drag into a pan.</summary>
    public bool SpacePanHeld { get; set; }

    public double LastPressureSample { get; private set; } = 1;

    // ------------------------------------------------------------------ press

    public CanvasInputResult Press(
        CanvasPointerButton button,
        double screenX,
        double screenY,
        bool additive = false,
        double pressure = 1)
    {
        RememberScreen(screenX, screenY);

        if (button == CanvasPointerButton.Middle
            || (button == CanvasPointerButton.Left && SpacePanHeld))
        {
            _gesture = CanvasInputGesture.Pan;
            return new CanvasInputResult { Handled = true, CapturePointer = true };
        }

        if (button == CanvasPointerButton.Right)
        {
            // A secondary press is an escape hatch, never a destructive action. It abandons the
            // gesture in flight and only reports itself handled when it actually cancelled
            // something, so a plain right-click stays available to the rest of the shell.
            var cancelled = CancelActiveGesture() | _editor.CancelPlacement()
                | _editor.CancelPenPathIfActive();
            return new CanvasInputResult { Handled = cancelled, Invalidate = true };
        }

        if (button != CanvasPointerButton.Left)
        {
            return CanvasInputResult.Ignored;
        }

        var (worldX, worldY) = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
        _pressScreenX = screenX;
        _pressScreenY = screenY;
        _pressWorldX = worldX;
        _pressWorldY = worldY;
        _pressAdditive = additive;
        _vectorDrawIsTentative = false;

        if (_editor.CanvasWorkflowOpen)
        {
            var capture = _editor.CanvasWorkflowMode == ResizeRegionMode.Region
                && _editor.BeginRegionSelection(worldX, worldY);
            _gesture = capture ? CanvasInputGesture.RegionSelect : CanvasInputGesture.None;
            return new CanvasInputResult { Handled = true, CapturePointer = capture, Invalidate = true };
        }

        if (_editor.ActiveTool == EditorTool.Measure)
        {
            _editor.BeginMeasure(worldX, worldY);
            _gesture = CanvasInputGesture.Measure;
            return new CanvasInputResult { Handled = true, CapturePointer = true, Invalidate = true };
        }

        // An armed placement is a pending gesture: finishing it always wins over starting a new one.
        if (_editor.PressEditableArea(worldX, worldY))
        {
            _gesture = _editor.IsDraggingAreaNode ? CanvasInputGesture.PlacementAreaNode : CanvasInputGesture.None;
            return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = _editor.IsDraggingAreaNode };
        }
        if (_editor.PressEditablePath(worldX, worldY))
        {
            _gesture = _editor.IsDraggingPathNode ? CanvasInputGesture.PlacementPathNode : CanvasInputGesture.None;
            return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = _editor.IsDraggingPathNode };
        }
        if (_editor.BeginRiverStroke(worldX, worldY))
        {
            _gesture = CanvasInputGesture.RiverStroke;
            return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = true };
        }
        if (_editor.PressRiver(worldX, worldY))
        {
            _gesture = _editor.IsDraggingRiver ? CanvasInputGesture.RiverNode : CanvasInputGesture.None;
            return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = _editor.IsDraggingRiver };
        }
        if (_editor.BeginRoadStroke(worldX, worldY))
        {
            _gesture = CanvasInputGesture.RoadStroke;
            return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = true };
        }
        if (_editor.PressRoad(worldX, worldY))
        {
            _gesture = _editor.IsDraggingRoad ? CanvasInputGesture.RoadNode : CanvasInputGesture.None;
            return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = _editor.IsDraggingRoad };
        }
        if (_editor.HasPendingPlacement)
        {
            _editor.UpdatePlacementPreview(worldX, worldY);
            if (_editor.PendingPlacement is { Target: PlacementTargetKind.Stamp, Mode: StampPlacementMode.Area }
                && _editor.VectorDrawFromCenter)
            {
                // Alt turns the scatter circle into an eraser: the same radius, taking stamps away.
                _editor.EraseStampsAround(worldX, worldY);
                _gesture = CanvasInputGesture.StampErase;
                return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = true };
            }
            if (_editor.PendingPlacement is { Target: PlacementTargetKind.Stamp, Mode: StampPlacementMode.Series })
            {
                var began = _editor.BeginStampSeries(worldX, worldY);
                _gesture = began ? CanvasInputGesture.StampSeries : CanvasInputGesture.None;
                return new CanvasInputResult { Handled = true, Invalidate = true, CapturePointer = began };
            }
            var stamp = _editor.PendingPlacement?.Target == PlacementTargetKind.Stamp;
            var committed = _editor.CommitPendingPlacement();
            _gesture = CanvasInputGesture.None;
            return new CanvasInputResult
            {
                Handled = true,
                Invalidate = true,
                StampPlaced = stamp && committed,
            };
        }

        if (_editor.ActiveTool == EditorTool.Hex)
        {
            // Hexes are the hex constructor's only content: a press always belongs to the tool.
            // Fill and pick finish on the press; place and erase continue as a drag.
            var dragging = _editor.BeginHexGesture(worldX, worldY, additive);
            _gesture = dragging ? CanvasInputGesture.HexPaint : CanvasInputGesture.None;
            return new CanvasInputResult { Handled = true, CapturePointer = dragging, Invalidate = true };
        }

        if (_editor.ActiveTool is EditorTool.Brush or EditorTool.Mask or EditorTool.Land)
        {
            // A chosen land shape turns the drag into a region: press and release describe the
            // rectangle it fills, exactly like the shape tools do.
            if (_editor.LandDrawsRegion)
            {
                var region = _editor.BeginLandShapeGesture(worldX, worldY);
                _gesture = region ? CanvasInputGesture.LandShape : CanvasInputGesture.None;
                return new CanvasInputResult
                {
                    Handled = true,
                    CapturePointer = region,
                    Invalidate = true,
                };
            }

            LastPressureSample = Math.Clamp(pressure, 0, 1);
            var stroking = _editor.BeginStroke(worldX, worldY, LastPressureSample);
            _gesture = stroking ? CanvasInputGesture.Stroke : CanvasInputGesture.None;
            return new CanvasInputResult { Handled = true, CapturePointer = stroking, Invalidate = true };
        }

        // The gizmo belongs to the selection, not to the active tool, and it outranks everything
        // else under the pointer. Two reasons: a freshly drawn shape would otherwise be selected
        // yet impossible to transform without a tool switch; and a vector node sitting exactly on
        // a corner handle - which is every rectangle and ellipse Shape - used to swallow the grab
        // with its wider tolerance, so those objects could never be resized by their own gizmo.
        // Nodes away from the handles still drag directly, and any node stays editable through the
        // inspector.
        if (GrabbedSelectionHandle(worldX, worldY, additive))
        {
            return BeginSelectionInteraction(worldX, worldY, additive);
        }

        if (_editor.ActiveTool is EditorTool.Line or EditorTool.Shape)
        {
            // Pen places nodes with single clicks, so it must not become a drag gesture: a press
            // adds one node and releases nothing, and the release below has no gesture to end.
            if (_editor.IsPenMode)
            {
                if (_editor.AddPenNode(worldX, worldY))
                {
                    _gesture = CanvasInputGesture.None;
                    return new CanvasInputResult { Handled = true, Invalidate = true };
                }

                return BeginSelectionInteraction(worldX, worldY, additive);
            }

            if (_editor.BeginVectorDraw(worldX, worldY))
            {
                _gesture = CanvasInputGesture.VectorDraw;

                // Every Line/Shape mode is a press-drag-release gesture, and a zero-length gesture
                // builds no object at all. So a click that never moves is spare input, and it is
                // spent on the thing the user actually pointed at: selecting that object.
                _vectorDrawIsTentative = true;
                return new CanvasInputResult { Handled = true, CapturePointer = true, Invalidate = true };
            }

            // No usable object layer: fall through to selection rather than swallowing the press,
            // so existing objects stay reachable and the refusal reason stays visible.
            return BeginSelectionInteraction(worldX, worldY, additive);
        }

        if (_editor.ActiveTool == EditorTool.Text)
        {
            if (_editor.BeginTextPlacement(worldX, worldY))
            {
                _gesture = CanvasInputGesture.TextPlacement;
                return new CanvasInputResult { Handled = true, CapturePointer = true, Invalidate = true };
            }

            return BeginSelectionInteraction(worldX, worldY, additive);
        }

        if (_editor.ActiveTool == EditorTool.Select
            && _editor.BeginVectorElementDrag(worldX, worldY, _editor.Session.Viewport.Scale))
        {
            _gesture = CanvasInputGesture.VectorElementDrag;
            return new CanvasInputResult { Handled = true, CapturePointer = true, Invalidate = true };
        }

        var result = BeginSelectionInteraction(worldX, worldY, additive);

        // Notes had no canvas behaviour at all: the tool looked armed while the only way to make
        // a note was a panel button that always dropped it in the middle of the view. A click on
        // empty canvas now anchors one where the user pointed; a click on an object still selects,
        // so the "note about this object" path keeps working.
        if (_editor.ActiveTool == EditorTool.Notes && _gesture == CanvasInputGesture.Marquee)
        {
            _notePendingAtPress = true;
        }

        return result;
    }

    private bool GrabbedSelectionHandle(double worldX, double worldY, bool additive) =>
        !additive
        && _editor.SelectionCount > 0
        && _editor.HitHandle(worldX, worldY, _editor.Session.Viewport.Scale) is not null;

    private CanvasInputResult BeginSelectionInteraction(double worldX, double worldY, bool additive)
    {
        _gesture = _editor.BeginCanvasInteraction(
            worldX, worldY, _editor.Session.Viewport.Scale, additive) switch
        {
            CanvasGesture.Drag => CanvasInputGesture.ObjectDrag,
            CanvasGesture.Marquee => CanvasInputGesture.Marquee,
            _ => CanvasInputGesture.None,
        };

        return new CanvasInputResult { Handled = true, CapturePointer = true, Invalidate = true };
    }

    // ------------------------------------------------------------------- move

    public CanvasInputResult Move(double screenX, double screenY, double pressure = 1)
    {
        var previousX = _lastScreenX;
        var previousY = _lastScreenY;
        var hadPrevious = _hasLastScreen;
        RememberScreen(screenX, screenY);

        if (_gesture == CanvasInputGesture.Pan)
        {
            if (!hadPrevious)
            {
                return new CanvasInputResult { Handled = true };
            }

            _editor.Session.Viewport.PanByScreenDelta(screenX - previousX, screenY - previousY);
            return new CanvasInputResult { Handled = true, Invalidate = true, ViewportChanged = true };
        }

        var (worldX, worldY) = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);

        if (_gesture == CanvasInputGesture.None && _editor.HasPendingPlacement)
        {
            _editor.UpdatePlacementPreview(worldX, worldY);
            return new CanvasInputResult { Handled = true, Invalidate = true };
        }

        if (_gesture == CanvasInputGesture.None && _editor.PenPathActive)
        {
            _editor.UpdatePenPreview(worldX, worldY);
            return new CanvasInputResult { Handled = true, Invalidate = true };
        }

        switch (_gesture)
        {
            case CanvasInputGesture.Measure:
                _editor.UpdateMeasure(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.PlacementAreaNode:
                _editor.MoveEditableAreaNode(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.PlacementPathNode:
                _editor.MoveEditablePathNode(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.RiverNode:
                _editor.MoveRiverPointer(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.RiverStroke:
                _editor.ContinueRiverStroke(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.RoadNode:
                _editor.MoveRoadPointer(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.RoadStroke:
                _editor.ContinueRoadStroke(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.StampSeries:
                _editor.ContinueStampSeries(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.StampErase:
                _editor.UpdatePlacementPreview(worldX, worldY);
                _editor.EraseStampsAround(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.RegionSelect:
                _editor.UpdateRegionSelection(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.Stroke:
                LastPressureSample = Math.Clamp(pressure, 0, 1);
                _editor.ContinueStroke(worldX, worldY, LastPressureSample);
                return new CanvasInputResult { Handled = true };
            case CanvasInputGesture.HexPaint:
                _editor.ContinueHexGesture(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.VectorDraw:
                if (TravelledPastClickThreshold(screenX, screenY))
                {
                    _vectorDrawIsTentative = false;
                }

                _editor.ContinueVectorDraw(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.LandShape:
                _editor.UpdateLandShapeGesture(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.TextPlacement:
                _editor.ContinueTextPlacement(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.VectorElementDrag:
                _editor.UpdateVectorElementDrag(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.ObjectDrag:
                _editor.UpdateDragTo(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            case CanvasInputGesture.Marquee:
                _editor.UpdateMarquee(worldX, worldY);
                return new CanvasInputResult { Handled = true, Invalidate = true };
            default:
                return CanvasInputResult.Ignored;
        }
    }

    // ---------------------------------------------------------------- release

    public CanvasInputResult Release(double screenX, double screenY)
    {
        var gesture = _gesture;
        var tentative = _vectorDrawIsTentative;
        var note = _notePendingAtPress;
        var clicked = !TravelledPastClickThreshold(screenX, screenY);
        _gesture = CanvasInputGesture.None;
        _vectorDrawIsTentative = false;
        _notePendingAtPress = false;
        RememberScreen(screenX, screenY);

        // Exactly one gesture ends per release. The old control ran an unguarded sequence of ifs,
        // so two flags left set by a mid-gesture tool switch produced two history entries at once.
        switch (gesture)
        {
            case CanvasInputGesture.Measure:
                var measureEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.UpdateMeasure(measureEnd.X, measureEnd.Y);
                return new CanvasInputResult { Handled = true, Invalidate = true, ReleasePointer = true };
            case CanvasInputGesture.PlacementAreaNode:
                var areaEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.MoveEditableAreaNode(areaEnd.X, areaEnd.Y);
                _editor.EndEditableAreaNodeDrag();
                return new CanvasInputResult { Handled = true, Invalidate = true, ReleasePointer = true };
            case CanvasInputGesture.PlacementPathNode:
                var pathEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.MoveEditablePathNode(pathEnd.X, pathEnd.Y);
                _editor.EndEditablePathNodeDrag();
                return new CanvasInputResult { Handled = true, Invalidate = true, ReleasePointer = true };
            case CanvasInputGesture.RiverNode:
                var riverEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.MoveRiverPointer(riverEnd.X, riverEnd.Y);
                _editor.EndRiverDrag();
                return new CanvasInputResult { Handled = true, Invalidate = true, ReleasePointer = true };
            case CanvasInputGesture.RiverStroke:
                var strokeEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.ContinueRiverStroke(strokeEnd.X, strokeEnd.Y);
                _editor.EndRiverStroke();
                return new CanvasInputResult { Handled = true, Invalidate = true, ReleasePointer = true };
            case CanvasInputGesture.RoadNode:
                var roadEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.MoveRoadPointer(roadEnd.X, roadEnd.Y);
                _editor.EndRoadDrag();
                return new CanvasInputResult { Handled = true, Invalidate = true, ReleasePointer = true };
            case CanvasInputGesture.RoadStroke:
                var roadStrokeEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.ContinueRoadStroke(roadStrokeEnd.X, roadStrokeEnd.Y);
                _editor.EndRoadStroke();
                return new CanvasInputResult { Handled = true, Invalidate = true, ReleasePointer = true };
            case CanvasInputGesture.StampSeries:
                var end = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.ContinueStampSeries(end.X, end.Y);
                var placed = _editor.EndStampSeries();
                return new CanvasInputResult { Handled = true, ReleasePointer = true, Invalidate = true, StampPlaced = placed };
            case CanvasInputGesture.StampErase:
                _editor.EndStampErase();
                return new CanvasInputResult { Handled = true, ReleasePointer = true, Invalidate = true };
            case CanvasInputGesture.ObjectDrag:
                _editor.EndDrag();
                break;
            case CanvasInputGesture.Marquee:
                if (clicked)
                {
                    // The press already applied its selection effect; an empty frame is dropped.
                    _editor.CancelMarquee();
                    if (note)
                    {
                        _editor.AddNoteAt(_pressWorldX, _pressWorldY);
                    }
                }
                else
                {
                    _editor.EndMarquee();
                }

                break;
            case CanvasInputGesture.RegionSelect:
                _editor.EndRegionSelection();
                break;
            case CanvasInputGesture.Stroke:
                _editor.EndStroke();
                break;
            case CanvasInputGesture.HexPaint:
                var hexEnd = _editor.Session.Viewport.ScreenToWorld(screenX, screenY);
                _editor.ContinueHexGesture(hexEnd.X, hexEnd.Y);
                _editor.EndHexGesture();
                break;
            case CanvasInputGesture.VectorDraw:
                if (tentative && clicked)
                {
                    _editor.CancelVectorDraw();
                    SelectAtPressWithoutHistory();
                }
                else
                {
                    _editor.EndVectorDraw();
                }

                break;
            case CanvasInputGesture.LandShape:
                _editor.EndLandShapeGesture();
                break;
            case CanvasInputGesture.TextPlacement:
                _editor.EndTextPlacement();
                break;
            case CanvasInputGesture.VectorElementDrag:
                _editor.EndVectorElementDrag();
                break;
        }

        return new CanvasInputResult
        {
            Handled = gesture != CanvasInputGesture.None,
            ReleasePointer = true,
            Invalidate = true,
        };
    }

    /// <summary>
    /// Applies the press as a plain selection click: it selects the topmost available object under
    /// the press, or clears the selection on empty canvas, and leaves no history entry behind.
    /// </summary>
    private void SelectAtPressWithoutHistory()
    {
        switch (_editor.BeginCanvasInteraction(
            _pressWorldX, _pressWorldY, _editor.Session.Viewport.Scale, _pressAdditive))
        {
            case CanvasGesture.Drag:
                _editor.CancelDrag();
                break;
            case CanvasGesture.Marquee:
                _editor.CancelMarquee();
                break;
        }
    }

    // ----------------------------------------------------------------- cancel

    /// <summary>
    /// Abandons whatever gesture is running without touching the document or the history. The
    /// host calls this on tool switch, pointer-capture loss, focus loss and detach, which is what
    /// keeps a half-finished drag from committing on the next unrelated release.
    /// </summary>
    public bool Cancel()
    {
        var cancelled = CancelActiveGesture();
        _hasLastScreen = false;
        return cancelled;
    }

    /// <summary>Drops the router's own gesture state only; the editor has already cleaned up.</summary>
    private void ForgetGesture()
    {
        _gesture = CanvasInputGesture.None;
        _vectorDrawIsTentative = false;
        _notePendingAtPress = false;
    }

    private bool CancelActiveGesture()
    {
        var gesture = _gesture;
        _gesture = CanvasInputGesture.None;
        _vectorDrawIsTentative = false;
        _notePendingAtPress = false;

        switch (gesture)
        {
            case CanvasInputGesture.PlacementAreaNode:
                return _editor.CancelEditableAreaGesture();
            case CanvasInputGesture.PlacementPathNode:
                return _editor.CancelEditablePathGesture();
            case CanvasInputGesture.RiverNode:
            case CanvasInputGesture.RiverStroke:
                return _editor.CancelRiverGesture();
            case CanvasInputGesture.RoadNode:
            case CanvasInputGesture.RoadStroke:
                return _editor.CancelRoadGesture();
            case CanvasInputGesture.StampSeries:
                return _editor.CancelStampSeries();
            case CanvasInputGesture.StampErase:
                _editor.EndStampErase();
                return true;
            case CanvasInputGesture.ObjectDrag:
                _editor.CancelDrag();
                return true;
            case CanvasInputGesture.Marquee:
                _editor.CancelMarquee();
                return true;
            case CanvasInputGesture.Stroke:
                _editor.CancelStroke();
                return true;
            case CanvasInputGesture.HexPaint:
                return _editor.CancelHexGesture();
            case CanvasInputGesture.VectorDraw:
                _editor.CancelVectorDraw();
                return true;
            case CanvasInputGesture.LandShape:
                _editor.CancelLandShapeGesture();
                return true;
            case CanvasInputGesture.TextPlacement:
                _editor.CancelTextPlacement();
                return true;
            case CanvasInputGesture.VectorElementDrag:
                _editor.CancelVectorElementDrag();
                return true;
            case CanvasInputGesture.RegionSelect:
            case CanvasInputGesture.Pan:
                return true;
            default:
                return false;
        }
    }

    /// <summary>The pointer left the canvas: hide the placement ghost but keep the tool armed.</summary>
    public CanvasInputResult PointerLeft()
    {
        if (_gesture == CanvasInputGesture.StampSeries) CancelActiveGesture();
        _editor.SetPlacementPointerVisible(false);
        _hasLastScreen = false;
        return new CanvasInputResult { Invalidate = true };
    }

    private bool TravelledPastClickThreshold(double screenX, double screenY) =>
        Math.Abs(screenX - _pressScreenX) >= ClickThresholdPixels
        || Math.Abs(screenY - _pressScreenY) >= ClickThresholdPixels;

    private void RememberScreen(double screenX, double screenY)
    {
        _lastScreenX = screenX;
        _lastScreenY = screenY;
        _hasLastScreen = true;
    }
}
