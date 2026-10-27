using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    // ---------------------------------------------------------------- editing

    public bool BeginVectorDraw(double worldX, double worldY)
    {
        if (ActiveTool is not (EditorTool.Line or EditorTool.Shape))
        {
            return false;
        }

        if (ActiveObjectLayer() is not { Visible: true, Locked: false })
        {
            // Refusing silently used to swallow the press whole. Naming the reason lets the user
            // fix it, and the caller falls back to plain selection so the scene stays reachable.
            StatusDetail = "Нет видимого разблокированного слоя объектов — фигура не создана.";
            return false;
        }

        var point = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        _vectorPoints = [point];
        _vectorPreviewObjectId = ObjectId.New();
        _vectorPreviewLayerId = ActiveObjectLayer()!.Id;
        _vectorPreviewObject = null;
        Raise(nameof(PlacementPreviewObjects));
        return true;
    }

    public void ContinueVectorDraw(double worldX, double worldY)
    {
        if (_vectorPoints is not { Count: > 0 } points)
        {
            return;
        }

        var next = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));

        // Free Draw follows the pointer; Brush does too, because the band it sweeps is built from
        // the same path. The primitives keep only the press point and the current one.
        var continuous = ActiveTool == EditorTool.Line
            ? ActiveLineDrawMode == LineDrawMode.FreeDraw
            : ActiveShapeDrawMode is ShapeDrawMode.FreeDraw or ShapeDrawMode.Brush;
        if (continuous)
        {
            var last = points[^1];
            if (Math.Pow(next.X - last.X, 2) + Math.Pow(next.Y - last.Y, 2) >= 4)
                points.Add(next);
        }
        else if (points.Count == 1)
            points.Add(next);
        else
            points[^1] = next;

        _vectorPreviewObject = BuildVectorGestureObject(points, preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndVectorDraw()
    {
        if (_vectorPoints is not { } points)
        {
            return;
        }

        var layerId = _vectorPreviewLayerId;
        var added = BuildVectorGestureObject(points, preview: false);
        _vectorPoints = null;
        _vectorPreviewObject = null;
        _vectorPreviewLayerId = null;
        _vectorPreviewObjectId = null;
        Raise(nameof(PlacementPreviewObjects));
        if (layerId is null || Session.Document.FindLayer(layerId.Value) is not ObjectLayer layer || added is null)
        {
            return;
        }
        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CancelVectorDraw()
    {
        _vectorPoints = null;
        _vectorPreviewObject = null;
        _vectorPreviewLayerId = null;
        _vectorPreviewObjectId = null;
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private VectorPoint SnapVectorGesturePoint(VectorPoint point) =>
        ActiveSnapGrid() is { SnapEnabled: true } grid
            ? GridSnapService.Snap(grid, point)
            : new VectorPoint(WholeGeometryValue(point.X), WholeGeometryValue(point.Y));

    private SceneObject? BuildVectorGestureObject(IReadOnlyList<VectorPoint> source, bool preview)
    {
        if (_vectorPreviewObjectId is not { } objectId || source.Count < 2) return null;
        var points = source;
        SceneObject? result = null;
        try
        {
            if (ActiveTool == EditorTool.Line)
            {
                var linePoints = ActiveLineDrawMode == LineDrawMode.Pen ? [source[0], source[^1]] : source;
                if (DistanceSquared(linePoints[0], linePoints[^1]) < 1) return null;
                result = VectorGeometry.CreateLine(objectId,
                    EntityNameGenerator.Object(Session.Document, SceneObjectKind.Line),
                    linePoints, NewLineStroke());
            }
            else if (ActiveTool == EditorTool.Shape)
            {
                var start = source[0]; var end = source[^1];
                if (DistanceSquared(start, end) < 1) return null;
                var name = EntityNameGenerator.Object(Session.Document, SceneObjectKind.Shape);
                var fill = NewShapeFill();
                var stroke = NewShapeStroke();
                result = ActiveShapeDrawMode switch
                {
                    ShapeDrawMode.Rectangle => VectorGeometry.CreateShape(objectId, name,
                        ShapePrimitiveKind.Rectangle, GestureBounds(start, end), fill, stroke),
                    ShapeDrawMode.Ellipse => VectorGeometry.CreateShape(objectId, name,
                        ShapePrimitiveKind.Ellipse, GestureBounds(start, end), fill, stroke),
                    ShapeDrawMode.Polygon => VectorGeometry.CreateFreeShape(objectId, name,
                        RegularPolygon(GestureBounds(start, end), ShapePolygonSides), fill, stroke),
                    ShapeDrawMode.Brush => VectorGeometry.CreateFreeShape(objectId, name,
                        StrokeOutline(source, ShapeBrushWidth), fill, stroke),
                    _ when source.Count >= 3 => VectorGeometry.CreateFreeShape(
                        objectId, name, source, fill, stroke),
                    _ => null,
                };
            }
        }
        catch (ArgumentException)
        {
            return null;
        }
        return result is null ? null : result with { ObjectOpacity = preview ? .55 : 1 };

        static IReadOnlyList<VectorPoint> RegularPolygon(WorldBounds bounds, int sides)
        {
            var cx = (bounds.Left + bounds.Right) / 2; var cy = (bounds.Top + bounds.Bottom) / 2;
            var rx = (bounds.Right - bounds.Left) / 2; var ry = (bounds.Bottom - bounds.Top) / 2;
            return [.. Enumerable.Range(0, sides).Select(index =>
            {
                var angle = (-Math.PI / 2) + index * Math.PI * 2 / sides;
                return new VectorPoint(cx + Math.Cos(angle) * rx, cy + Math.Sin(angle) * ry);
            })];
        }
    }

    /// <summary>
    /// The rectangle a press-drag describes, with the two modifier rules applied: Shift makes the
    /// two sides equal, Alt treats the press point as the centre. Both are what every drawing
    /// program does, and without them a square or a circle was a matter of luck.
    /// </summary>
    private WorldBounds GestureBounds(VectorPoint start, VectorPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (VectorConstrainProportions)
        {
            var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -size : size;
            dy = dy < 0 ? -size : size;
        }

        if (VectorDrawFromCenter)
        {
            return new WorldBounds(
                start.X - Math.Abs(dx), start.Y - Math.Abs(dy),
                start.X + Math.Abs(dx), start.Y + Math.Abs(dy));
        }

        return new WorldBounds(
            Math.Min(start.X, start.X + dx), Math.Min(start.Y, start.Y + dy),
            Math.Max(start.X, start.X + dx), Math.Max(start.Y, start.Y + dy));
    }

    /// <summary>
    /// The closed outline of a stroked path: one offset side forward, the other back. It is what
    /// turns the Brush mode into an actual band instead of a hairline copy of Free Draw.
    /// </summary>
    private static IReadOnlyList<VectorPoint> StrokeOutline(IReadOnlyList<VectorPoint> path, double width)
    {
        var half = Math.Max(0.5, width / 2);
        var forward = new List<VectorPoint>(path.Count);
        var back = new List<VectorPoint>(path.Count);
        for (var index = 0; index < path.Count; index++)
        {
            var previous = path[Math.Max(0, index - 1)];
            var next = path[Math.Min(path.Count - 1, index + 1)];
            var dx = next.X - previous.X;
            var dy = next.Y - previous.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length < 1e-6)
            {
                dx = 1; dy = 0; length = 1;
            }

            var normalX = -dy / length * half;
            var normalY = dx / length * half;
            forward.Add(new VectorPoint(path[index].X + normalX, path[index].Y + normalY));
            back.Add(new VectorPoint(path[index].X - normalX, path[index].Y - normalY));
        }

        back.Reverse();
        forward.AddRange(back);
        return forward;
    }

    // --------------------------------------------------------------------- pen paths

    private List<VectorPoint>? _penPoints;
    private VectorPoint? _penCursor;

    /// <summary>True when the active tool draws by placing nodes rather than by dragging.</summary>
    public bool IsPenMode => ActiveTool switch
    {
        EditorTool.Shape => ActiveShapeDrawMode == ShapeDrawMode.Pen,
        EditorTool.Line => ActiveLineDrawMode == LineDrawMode.Pen,
        _ => false,
    };

    /// <summary>True while a pen path is being built, so the canvas keeps previewing it.</summary>
    public bool PenPathActive => _penPoints is { Count: > 0 };

    /// <summary>How many nodes the pen path already has; the panel shows it.</summary>
    public int PenNodeCount => _penPoints?.Count ?? 0;

    /// <summary>
    /// Places one pen node. Pen used to be a drag gesture that always produced the same right
    /// triangle whatever the user did, which is the single worst defect of the Shape tool: the
    /// mode existed, was selectable, and could not draw what it named.
    /// </summary>
    public bool AddPenNode(double worldX, double worldY)
    {
        if (!IsPenMode)
        {
            return false;
        }

        if (ActiveObjectLayer() is not { Visible: true, Locked: false } layer)
        {
            StatusDetail = "Нет видимого разблокированного слоя объектов — узел не поставлен.";
            return false;
        }

        var point = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        if (_penPoints is null)
        {
            _penPoints = [point];
            _vectorPreviewObjectId = ObjectId.New();
            _vectorPreviewLayerId = layer.Id;
            StatusDetail = ActiveTool == EditorTool.Shape
                ? "Ставьте узлы фигуры. Клик по первому узлу или Enter — замкнуть, Esc — отмена."
                : "Ставьте узлы линии. Enter — завершить, Esc — отмена.";
        }
        else
        {
            // Clicking the first node closes the path, the way a pen tool is expected to work.
            var closing = _penPoints.Count >= 2
                && DistanceSquared(point, _penPoints[0]) <= CloseToleranceSquared();
            if (closing)
            {
                return FinishPenPath();
            }

            _penPoints.Add(point);
        }

        _penCursor = point;
        RefreshPenPreview();
        return true;
    }

    /// <summary>Rubber-bands the segment between the last node and the pointer.</summary>
    public void UpdatePenPreview(double worldX, double worldY)
    {
        if (_penPoints is null)
        {
            return;
        }

        _penCursor = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        RefreshPenPreview();
    }

    /// <summary>Commits the pen path as one object, or reports why it could not be one.</summary>
    public bool FinishPenPath()
    {
        if (_penPoints is null)
        {
            return false;
        }

        var layerId = _vectorPreviewLayerId;
        var added = BuildPenObject(preview: false);
        CancelPenPath();
        if (added is null || layerId is null
            || Session.Document.FindLayer(layerId.Value) is not ObjectLayer layer)
        {
            StatusDetail = "Контур не создан: фигуре нужно не меньше трёх узлов, линии — двух.";
            return false;
        }

        Session.Execute(new AddObjectCommand(layer.Id, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
        return true;
    }

    /// <summary>Drops the unfinished pen path; nothing reaches the document or the history.</summary>
    public void CancelPenPath()
    {
        if (_penPoints is null)
        {
            return;
        }

        _penPoints = null;
        _penCursor = null;
        _vectorPreviewObject = null;
        _vectorPreviewLayerId = null;
        _vectorPreviewObjectId = null;
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Cancels a pen path if there is one, and says whether it did.</summary>
    public bool CancelPenPathIfActive()
    {
        if (!PenPathActive)
        {
            return false;
        }

        CancelPenPath();
        StatusDetail = "Контур отменён.";
        return true;
    }

    private double CloseToleranceSquared()
    {
        var world = 8 / Math.Max(0.0001, Session.Viewport.Scale);
        return world * world;
    }

    private void RefreshPenPreview()
    {
        _vectorPreviewObject = BuildPenObject(preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private SceneObject? BuildPenObject(bool preview)
    {
        if (_penPoints is not { Count: > 0 } placed || _vectorPreviewObjectId is not { } objectId)
        {
            return null;
        }

        var points = new List<VectorPoint>(placed);
        if (preview && _penCursor is { } cursor
            && DistanceSquared(points[^1], cursor) > 0)
        {
            points.Add(cursor);
        }

        try
        {
            SceneObject? result = ActiveTool == EditorTool.Line
                ? points.Count < 2
                    ? null
                    : VectorGeometry.CreateLine(objectId,
                        EntityNameGenerator.Object(Session.Document, SceneObjectKind.Line),
                        points, NewLineStroke())
                : points.Count < 3
                    ? null
                    : VectorGeometry.CreateFreeShape(objectId,
                        EntityNameGenerator.Object(Session.Document, SceneObjectKind.Shape),
                        points, NewShapeFill(), NewShapeStroke());

            return result is null ? null : result with { ObjectOpacity = preview ? .55 : 1 };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static double DistanceSquared(VectorPoint a, VectorPoint b) =>
        ((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y));

    public DragPreview? DragPreview
    {
        get => _dragPreview;
        private set => SetField(ref _dragPreview, value);
    }

    /// <summary>What the canvas should draw: the snapshot, with the live drag substituted in.</summary>
    public MapDocument RenderDocument
    {
        get
        {
            if (CanvasWorkflowOpen
                && CanvasWorkflowMode == ResizeRegionMode.Resize
                && CanvasResizePreviewDocument is { } resizePreview)
            {
                return resizePreview;
            }

            // R30 «До»: the compared version, read-only.
            if (CompareShowsBefore && _compareBefore is { } before) return before;
            if (GenerationPreview is { } generationPreview) return generationPreview;
            if (ValidAppearancePreview is { } appearancePreview) return appearancePreview;

            // A light slider being dragged shows its value without a history entry.
            if (ValidLightingPreview is { } lightingPreview && DragPreview is null)
                return Session.Document with { Lighting = lightingPreview };

            // So does a style slider.
            if (ValidStylePreview is { } stylePreview && DragPreview is null)
                return Session.Document with { Style = stylePreview };

            if (DragPreview is not { } preview)
            {
                // R31: the year of the timeline and the political look.
                return TimelineShown(Session.Document);
            }

            var document = Session.Document;
            foreach (var proposed in preview.Proposed)
            {
                document = DocumentEditor.ReplaceObject(document, proposed);
            }

            return document;
        }
    }

    public void AddObject(SceneObjectKind kind)
    {
        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null)
        {
            StatusDetail = NoUsableObjectLayer;
            return;
        }

        var canvas = Session.Document.Canvas;

        // Stagger new objects so consecutive adds stay individually visible.
        var offset = (Session.Document.Layers.OfType<ObjectLayer>().Sum(l => l.Objects.Count) % 5) * 40;
        var width = Math.Min(240.0, canvas.Width / 4.0);
        var height = Math.Min(160.0, canvas.Height / 4.0);

        var added = new SceneObject(
            ObjectId.New(), kind,
            EntityNameGenerator.Object(Session.Document, kind),
            ((canvas.Width - width) / 2) + offset,
            ((canvas.Height - height) / 2) + offset,
            width, height, 0,
            kind == SceneObjectKind.Ellipse ? "#FF5B8266" : "#FF3E5C76",
            "#FF10131A", 2,
            visible: true, locked: false);

        ExecuteWithOptionalLayer(
            new AddObjectCommand(layer.Id, added), createLayer, "Add object on a new layer");
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddLine()
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        var line = VectorGeometry.CreateLine(
            ObjectId.New(), EntityNameGenerator.Object(Session.Document, SceneObjectKind.Line),
        [
            new VectorPoint(centre.X - 240, centre.Y + 80),
            new VectorPoint(centre.X, centre.Y - 100),
            new VectorPoint(centre.X + 240, centre.Y + 40),
        ]);
        Session.Execute(new AddObjectCommand(layer.Id, line));
        Session.SelectedObjectId = line.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddShape(ShapePrimitiveKind kind)
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        var shape = VectorGeometry.CreateShape(
            ObjectId.New(), EntityNameGenerator.Object(Session.Document, SceneObjectKind.Shape), kind,
            new WorldBounds(centre.X - 180, centre.Y - 130, centre.X + 180, centre.Y + 130));
        Session.Execute(new AddObjectCommand(layer.Id, shape));
        Session.SelectedObjectId = shape.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddText()
    {
        var layer = ActiveObjectLayer();
        if (layer is null) return;
        var centre = ViewCentre();
        const string defaultContent = "Новый текст\nна карте";
        var text = new SceneObject(
            ObjectId.New(), SceneObjectKind.Text,
            EntityNameGenerator.Text(Session.Document, defaultContent),
            centre.X - 180, centre.Y - 70, 360, 140, 0,
            "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = new TextStyle { Content = defaultContent },
        };
        Session.Execute(new AddObjectCommand(layer.Id, text));
        Session.SelectedObjectId = text.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CycleSelectedTextStyle()
    {
        if (EditableSelection() is not { Text: { } style } before) return;
        var after = before with
        {
            Text = style with
            {
                Alignment = style.Alignment == TextAlignment.Left ? TextAlignment.Center : TextAlignment.Left,
                OutlineWidth = style.OutlineWidth == 0 ? 2 : 0,
                ShadowOpacity = style.ShadowOpacity == 0 ? 0.35 : 0,
                Curve = style.Curve == 0 ? 12 : 0,
            },
        };
        ReplaceSelected(before, after, "Edit text style");
    }

    public TextFontResolution? SelectedTextFontResolution =>
        Session.SelectedObject?.Text is { } text
            ? TextFontResolver.Resolve(text.RequestedFontFamily, text.FallbackFontFamily)
            : null;

    public IReadOnlyList<string> LocalFontFamilies => _localFontFamilies;

    /// <summary>
    /// Case-insensitive local search. A missing requested family remains visible so fallback is
    /// never mistaken for a silent document rewrite.
    /// </summary>
    public IReadOnlyList<string> FontFamiliesForSearch(string? search)
    {
        var query = search?.Trim() ?? "";
        var requested = Session.SelectedObject?.Text?.RequestedFontFamily;
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            result.Add(requested);
        }

        result.AddRange(_localFontFamilies.Where(family =>
            !string.Equals(family, requested, StringComparison.OrdinalIgnoreCase)
            && (query.Length == 0 || family.Contains(query, StringComparison.OrdinalIgnoreCase))));
        return result;
    }

    public bool IsLocalFontAvailable(string family) =>
        _localFontFamilies.Contains(family, StringComparer.OrdinalIgnoreCase);

    public void SetSelectedTextFontFamily(string? family)
    {
        var requested = family?.Trim();
        if (Session.SelectedObjectIds.Count != 1
            || string.IsNullOrWhiteSpace(requested)
            || requested.Length > 256)
        {
            return;
        }

        EditText("Text font family", commit: true,
            text => text with { RequestedFontFamily = requested });
        Raise(nameof(SelectedTextFontResolution));
    }

    public void SelectAdjacentTextFont(int direction)
    {
        if (direction == 0
            || Session.SelectedObjectIds.Count != 1
            || _localFontFamilies.Count == 0
            || EditableSelection() is not { Text: { } text })
        {
            return;
        }

        var current = -1;
        for (var index = 0; index < _localFontFamilies.Count; index++)
        {
            if (string.Equals(_localFontFamilies[index], text.RequestedFontFamily,
                    StringComparison.OrdinalIgnoreCase))
            {
                current = index;
                break;
            }
        }

        var next = current < 0
            ? direction > 0 ? 0 : _localFontFamilies.Count - 1
            : (current + Math.Sign(direction) + _localFontFamilies.Count) % _localFontFamilies.Count;
        SetSelectedTextFontFamily(_localFontFamilies[next]);
    }

    public void UseMissingFontOnSelectedText()
    {
        if (EditableSelection() is not { Text: { } style } before) return;
        ReplaceSelected(before, before with
        {
            Text = style with { RequestedFontFamily = "Map Stage 7 Missing Font", FallbackFontFamily = "Arial" },
        }, "Set requested text font");
        Raise(nameof(SelectedTextFontResolution));
    }

    public IReadOnlyList<VectorNode> SelectedVectorNodes => Session.SelectedObject is { } selected
        ? selected.Line?.Nodes ?? selected.Shape?.Regions.SelectMany(region => region.Nodes).ToList() ?? []
        : [];

    public IReadOnlyList<LinePortal> SelectedVectorPortals => Session.SelectedObject is { } selected
        ? selected.Line?.Portals ?? selected.Shape?.Portals ?? []
        : [];

    public VectorNode? SelectedVectorNode => _selectedVectorNodeId is { } id
        ? SelectedVectorNodes.FirstOrDefault(node => node.Id == id)
        : null;

    public LinePortal? SelectedVectorPortal => _selectedPortalId is { } id
        ? SelectedVectorPortals.FirstOrDefault(portal => portal.Id == id)
        : null;

    public bool SelectVectorNode(VectorNodeId id)
    {
        if (SelectedVectorNodes.All(node => node.Id != id)) return false;
        _selectedVectorNodeId = id; _selectedPortalId = null;
        Raise(nameof(SelectedVectorNode)); Raise(nameof(SelectedVectorPortal)); return true;
    }

    public bool SelectVectorPortal(PortalId id)
    {
        if (SelectedVectorPortals.All(portal => portal.Id != id)) return false;
        _selectedPortalId = id; _selectedVectorNodeId = null;
        Raise(nameof(SelectedVectorNode)); Raise(nameof(SelectedVectorPortal)); return true;
    }

    public void AddNodeToSelectedLine(double localX = .5, double localY = .5)
    {
        if (EditableSelection() is not { Line: not null } before || !double.IsFinite(localX) || !double.IsFinite(localY)) return;
        var after = VectorObjectEditor.AddLineNode(before, localX, localY);
        _selectedVectorNodeId = after.Line!.Nodes[^1].Id;
        ReplaceSelected(before, after, "Add line node");
    }

    public void AddNodeToSelectedShape(double localX, double localY)
    {
        if (EditableSelection() is not { Shape: { Regions.Count: > 0 } shape } before
            || !double.IsFinite(localX) || !double.IsFinite(localY)) return;
        var after = VectorObjectEditor.AddShapeNode(before, shape.Regions[0].Id, localX, localY);
        _selectedVectorNodeId = after.Shape!.Regions[0].Nodes[^1].Id;
        ReplaceSelected(before, after, "Add shape node");
    }

    public bool SetSelectedVectorNode(
        double localX, double localY, VectorNodeKind kind,
        double controlInX, double controlInY, double controlOutX, double controlOutY)
    {
        if (EditableSelection() is not { } before || _selectedVectorNodeId is not { } id
            || new[] { localX, localY, controlInX, controlInY, controlOutX, controlOutY }.Any(value => !double.IsFinite(value)))
            return false;
        ReplaceSelected(before, VectorObjectEditor.SetNode(before, id, localX, localY, kind,
            controlInX, controlInY, controlOutX, controlOutY), "Edit selected vector node");
        return true;
    }

    public void MoveSelectedVectorNode()
    {
        if (SelectedVectorNode is { } node)
            SetSelectedVectorNode(node.X, node.Y, node.Kind, node.ControlInX, node.ControlInY,
                node.ControlOutX, node.ControlOutY);
    }

    public void ToggleSelectedNodeCurve()
    {
        if (EditableSelection() is not { } before || SelectedVectorNode is not { } node) return;
        ReplaceSelected(before, VectorObjectEditor.SetNodeCurve(before, node.Id, node.Kind != VectorNodeKind.Curve),
            "Toggle selected node curve");
    }

    public void DeleteSelectedVectorNode()
    {
        if (EditableSelection() is not { } before || _selectedVectorNodeId is not { } id) return;
        var after = before.Line is not null
            ? VectorObjectEditor.DeleteLineNode(before, id)
            : VectorObjectEditor.DeleteShapeNode(before, id);
        _selectedVectorNodeId = null;
        ReplaceSelected(before, after, "Delete selected vector node");
    }

    public void AddPortalToSelected(double position = .5, double width = 48)
    {
        if (EditableSelection() is not { } before || before.Line is null && before.Shape is null
            || !double.IsFinite(position) || !double.IsFinite(width)) return;
        var portal = new LinePortal(PortalId.New(), Math.Clamp(position, 0, 1), Math.Max(0, width));
        _selectedPortalId = portal.Id;
        ReplaceSelected(before, VectorObjectEditor.AddPortal(before, portal), "Add portal");
    }

    public bool SetSelectedPortal(double position, double width)
    {
        if (EditableSelection() is not { } before || _selectedPortalId is not { } id
            || !double.IsFinite(position) || !double.IsFinite(width)) return false;
        ReplaceSelected(before, VectorObjectEditor.SetPortal(before, id, Math.Clamp(position, 0, 1), Math.Max(0, width)),
            "Edit selected portal");
        return true;
    }

    public void MoveSelectedPortal()
    {
        if (SelectedVectorPortal is { } portal) SetSelectedPortal(portal.Position, portal.Width);
    }

    public void DeleteSelectedPortal()
    {
        if (EditableSelection() is not { } before || _selectedPortalId is not { } id) return;
        ReplaceSelected(before, VectorObjectEditor.DeletePortal(before, id), "Delete selected portal");
        _selectedPortalId = null;
    }

    public bool SelectVectorElementAt(double worldX, double worldY, double tolerance)
    {
        if (Session.SelectedObject is not { } selected || selected.Line is null && selected.Shape is null)
            return false;
        tolerance = Math.Max(.001, tolerance);
        var best = double.PositiveInfinity;
        VectorNodeId? nodeId = null; PortalId? portalId = null;
        foreach (var node in selected.Line?.Nodes ?? selected.Shape!.Regions.SelectMany(region => region.Nodes))
        {
            var point = VectorNodeWorld(selected, node);
            var distance = Math.Sqrt(Math.Pow(point.X - worldX, 2) + Math.Pow(point.Y - worldY, 2));
            if (distance <= tolerance && distance < best)
            {
                best = distance; nodeId = node.Id; portalId = null;
            }
        }
        var path = SelectedVectorPath(selected);
        foreach (var portal in selected.Line?.Portals ?? selected.Shape!.Portals)
        {
            var point = PointAtPosition(path, portal.Position);
            var distance = Math.Sqrt(Math.Pow(point.X - worldX, 2) + Math.Pow(point.Y - worldY, 2));
            if (distance <= tolerance && distance < best)
            {
                best = distance; portalId = portal.Id; nodeId = null;
            }
        }
        if (nodeId is { } selectedNodeId) return SelectVectorNode(selectedNodeId);
        if (portalId is { } selectedPortalId) return SelectVectorPortal(selectedPortalId);
        return false;
    }

    public bool BeginVectorElementDrag(double worldX, double worldY, double viewScale)
    {
        if (EditableSelection() is not { } before || before.Line is null && before.Shape is null
            || !SelectVectorElementAt(worldX, worldY, 10 / Math.Max(.001, viewScale))) return false;
        _vectorElementDrag = (before, _selectedVectorNodeId, _selectedPortalId);
        DragPreview = new DragPreview([before], [before], DragMode.Move, null, worldX, worldY, null);
        return true;
    }

    public void UpdateVectorElementDrag(double worldX, double worldY)
    {
        if (_vectorElementDrag is not { } drag) return;
        SceneObject proposed;
        if (drag.NodeId is { } nodeId)
        {
            var world = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
            var local = ObjectLocalPoint(drag.Before, world);
            proposed = VectorObjectEditor.MoveNode(drag.Before, nodeId, local.X, local.Y);
        }
        else if (drag.PortalId is { } portalId)
        {
            var position = NearestPosition(SelectedVectorPath(drag.Before), new VectorPoint(worldX, worldY));
            proposed = VectorObjectEditor.MovePortal(drag.Before, portalId, position);
        }
        else return;
        DragPreview = new DragPreview([drag.Before], [proposed], DragMode.Move, null, worldX, worldY, null);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndVectorElementDrag()
    {
        if (_vectorElementDrag is not { } drag) return;
        var proposed = DragPreview?.Proposed.FirstOrDefault();
        _vectorElementDrag = null; DragPreview = null;
        if (proposed is not null && proposed != drag.Before)
        {
            Session.Execute(new ReplaceObjectsCommand([drag.Before], [proposed], "Drag vector node or portal"));
            AfterEdit();
        }
        else InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void CancelVectorElementDrag()
    {
        if (_vectorElementDrag is null) return;
        _vectorElementDrag = null; DragPreview = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private static VectorPoint VectorNodeWorld(SceneObject owner, VectorNode node) =>
        RotateObjectPoint(owner, new VectorPoint(owner.X + node.X * owner.Width, owner.Y + node.Y * owner.Height));

    private static VectorPoint ObjectLocalPoint(SceneObject owner, VectorPoint world)
    {
        var unrotated = RotateObjectPoint(owner, world, inverse: true);
        return new VectorPoint((unrotated.X - owner.X) / owner.Width, (unrotated.Y - owner.Y) / owner.Height);
    }

    private static VectorPoint RotateObjectPoint(SceneObject owner, VectorPoint point, bool inverse = false)
    {
        var radians = owner.RotationDegrees * (inverse ? -1 : 1) * Math.PI / 180;
        var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var cx = owner.X + owner.Width / 2; var cy = owner.Y + owner.Height / 2;
        var dx = point.X - cx; var dy = point.Y - cy;
        return new VectorPoint(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
    }

    private static IReadOnlyList<VectorPoint> SelectedVectorPath(SceneObject owner)
    {
        var raw = owner.Line is { } line
            ? VectorGeometry.Flatten(owner, line.Nodes, line.Closed)
            : VectorGeometry.Flatten(owner, owner.Shape!.Regions[0].Nodes, closed: true);
        return [.. raw.Select(point => RotateObjectPoint(owner, point))];
    }

    private static VectorPoint PointAtPosition(IReadOnlyList<VectorPoint> path, double position)
    {
        if (path.Count == 0) return default;
        var lengths = path.Zip(path.Skip(1)).Select(pair => Math.Sqrt(
            Math.Pow(pair.Second.X - pair.First.X, 2) + Math.Pow(pair.Second.Y - pair.First.Y, 2))).ToList();
        var target = lengths.Sum() * Math.Clamp(position, 0, 1); var cursor = 0.0;
        for (var i = 0; i < lengths.Count; i++)
        {
            if (target <= cursor + lengths[i])
            {
                var t = (target - cursor) / Math.Max(.001, lengths[i]);
                return new VectorPoint(path[i].X + (path[i + 1].X - path[i].X) * t,
                    path[i].Y + (path[i + 1].Y - path[i].Y) * t);
            }
            cursor += lengths[i];
        }
        return path[^1];
    }

    private static double NearestPosition(IReadOnlyList<VectorPoint> path, VectorPoint target)
    {
        if (path.Count < 2) return 0;
        var lengths = path.Zip(path.Skip(1)).Select(pair => Math.Sqrt(
            Math.Pow(pair.Second.X - pair.First.X, 2) + Math.Pow(pair.Second.Y - pair.First.Y, 2))).ToList();
        var total = Math.Max(.001, lengths.Sum()); var cursor = 0.0; var best = double.PositiveInfinity; var result = 0.0;
        for (var i = 0; i < lengths.Count; i++)
        {
            var a = path[i]; var b = path[i + 1]; var dx = b.X - a.X; var dy = b.Y - a.Y;
            var t = Math.Clamp(((target.X - a.X) * dx + (target.Y - a.Y) * dy)
                / Math.Max(.000001, dx * dx + dy * dy), 0, 1);
            var px = a.X + dx * t; var py = a.Y + dy * t;
            var distance = Math.Pow(target.X - px, 2) + Math.Pow(target.Y - py, 2);
            if (distance < best) { best = distance; result = (cursor + lengths[i] * t) / total; }
            cursor += lengths[i];
        }
        return Math.Clamp(result, 0, 1);
    }

    public void CutSelectedLine()
    {
        if (EditableSelection() is not { Line: { Nodes.Count: >= 3 } line } before
            || _selectedVectorNodeId is not { } selectedNode
            || DocumentEditor.FindLayerOf(Session.Document, before.Id) is not { } layer)
        {
            return;
        }

        var points = line.Nodes.Select(node => new VectorPoint(
            before.X + node.X * before.Width, before.Y + node.Y * before.Height)).ToList();
        var cut = line.Nodes.ToList().FindIndex(node => node.Id == selectedNode);
        if (cut <= 0 || cut >= points.Count - 1)
        {
            StatusDetail = "Для разреза выберите внутренний узел линии.";
            return;
        }
        var pieces = new[]
        {
            VectorGeometry.CreateLine(ObjectId.New(), before.Name + " A", points.Take(cut + 1).ToList(), line.Stroke),
            VectorGeometry.CreateLine(ObjectId.New(), before.Name + " B", points.Skip(cut).ToList(), line.Stroke),
        };
        Session.Execute(new ReplaceObjectWithManyCommand(Session.Document, layer.Id, before.Id, pieces));
        Session.SelectedObjectId = pieces[0].Id;
        AfterEdit();
    }

    public void CycleSelectedVectorStroke()
    {
        if (EditableSelection() is not { } before) return;
        var current = before.Line?.Stroke ?? before.Shape?.Stroke;
        if (current is null) return;
        var changed = current with
        {
            Offset = current.Offset == 0 ? 12 : 0,
            EndCap = current.EndCap == VectorCap.Round ? VectorCap.Square : VectorCap.Round,
            ShadowOpacity = current.ShadowOpacity == 0 ? 0.35 : 0,
        };
        var after = before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, changed, line.Portals, line.Closed) }
            : before with
            {
                Shape = new ShapeStyle(before.Shape!.Regions, before.Shape.Fill, changed, before.Shape.Portals),
            };
        ReplaceSelected(before, after, "Edit vector stroke");
    }

    public void UseAssetAsSelectedVectorStroke(AssetRecord record)
    {
        if (_assetResolver is null || EditableAppearanceSelection() is not { } before)
        {
            return;
        }

        var stroke = before.Line?.Stroke ?? before.Shape?.Stroke;
        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (stroke is null || reference is null)
        {
            StatusDetail = assetRefusal ?? "Выберите Line/Shape и доступный локальный ассет.";
            return;
        }

        var changed = stroke with { AssetSha256 = record.Sha256, AssetSpacing = 1.25 };
        var after = before.Line is { } line
            ? before with { Line = new LineStyle(line.Nodes, changed, line.Portals, line.Closed) }
            : before with
            {
                Shape = new ShapeStyle(before.Shape!.Regions, before.Shape.Fill, changed, before.Shape.Portals),
            };
        Session.Execute(new VectorAssetStyleCommand(before, after, reference));
        AfterEdit();
        StatusDetail = $"Ассет «{record.DisplayName}» используется для обводки.";
    }

    /// <summary>Embeds one available library asset as the selected shape's repeating fill.</summary>
    public void UseAssetAsSelectedShapeFill(AssetRecord record)
    {
        if (_assetResolver is null || EditableAppearanceSelection() is not { Shape: { } shape } before)
        {
            StatusDetail = "Выберите доступную фигуру и локальный ассет.";
            return;
        }

        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (reference is null)
        {
            StatusDetail = assetRefusal ?? "Файл заливки отсутствует или не совпадает с его SHA-256.";
            return;
        }

        var changedFill = shape.Fill with { TextureAssetSha256 = record.Sha256 };
        if (changedFill.Equals(shape.Fill))
        {
            return;
        }

        var after = before with
        {
            Shape = new ShapeStyle(shape.Regions, changedFill, shape.Stroke, shape.Portals),
        };
        Session.Execute(new VectorAssetStyleCommand(before, after, reference, "Use asset fill"));
        AfterEdit();
        StatusDetail = $"Ассет «{record.DisplayName}» используется для заливки фигуры.";
    }

    public void ApplyShapeBoolean(ShapeBooleanOperation operation)
    {
        if (ResolveShapeBooleanSelection() is not { } operands)
        {
            StatusDetail = "Для объединения или вычитания выделите две совместимые фигуры на одном доступном слое объектов.";
            return;
        }

        var command = new ShapeBooleanCommand(Session.Document, operands.Layer.Id,
            operands.First.Id, operands.Second.Id, operation);
        Session.Execute(command);
        Session.SelectedObjectId = command.ResultId;
        CancelShapeBooleanPreview();
        AfterEdit();
    }

    public bool PreviewShapeBoolean(ShapeBooleanOperation operation)
    {
        if (ResolveShapeBooleanSelection() is not { } operands)
        {
            StatusDetail = "Для предпросмотра выделите две фигуры на одном доступном слое объектов.";
            return false;
        }
        var command = new ShapeBooleanCommand(Session.Document, operands.Layer.Id,
            operands.First.Id, operands.Second.Id, operation);
        _pendingShapeBoolean = (operands.Layer.Id, operands.First.Id, operands.Second.Id, operation);
        _shapeBooleanPreviewObject = command.Result with { ObjectOpacity = .55 };
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public bool CommitShapeBooleanPreview()
    {
        if (_pendingShapeBoolean is not { } pending
            || Session.Document.FindLayer(pending.LayerId) is not ObjectLayer layer
            || layer.Objects.All(item => item.Id != pending.First)
            || layer.Objects.All(item => item.Id != pending.Second)) return false;
        var command = new ShapeBooleanCommand(Session.Document, pending.LayerId,
            pending.First, pending.Second, pending.Operation);
        CancelShapeBooleanPreview();
        Session.Execute(command); Session.SelectedObjectId = command.ResultId; AfterEdit(); return true;
    }

    public void CancelShapeBooleanPreview()
    {
        _pendingShapeBoolean = null; _shapeBooleanPreviewObject = null;
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private (ObjectLayer Layer, SceneObject First, SceneObject Second)? ResolveShapeBooleanSelection()
    {
        var shapes = Session.SelectedObjectIds.Select(id => DocumentEditor.FindObject(Session.Document, id))
            .Where(item => item?.Shape is not null).Select(item => item!).ToList();
        if (shapes.Count != 2
            || DocumentEditor.FindLayerOf(Session.Document, shapes[0].Id) is not { } layer
            || DocumentEditor.FindLayerOf(Session.Document, shapes[1].Id)?.Id != layer.Id
            || layer.Locked || !layer.Visible) return null;
        return (layer, shapes[0], shapes[1]);
    }

    private ObjectLayer? ActiveObjectLayer() => ActiveLayer is ObjectLayer { Visible: true, Locked: false } active
        ? active
        : Session.Document.Layers.OfType<ObjectLayer>()
            .LastOrDefault(layer => layer.Visible && !layer.Locked);

    private (double X, double Y) ViewCentre() => Session.Viewport.ScreenToWorld(
        Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);

    private void ReplaceSelected(SceneObject before, SceneObject after, string name)
    {
        Session.Execute(new ReplaceObjectsCommand([before], [after], name));
        AfterEdit();
    }

    public void SelectLayer(LayerId id)
    {
        if (Session.Document.FindLayer(id) is null)
        {
            return;
        }

        _activeLayerId = id;
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerSummary));
        Raise(nameof(MaskEditLayerId));
        Raise(nameof(IsEditingStampForeground));
        Raise(nameof(StampClipMaskStatus));
        Raise(nameof(StampClipMaskCanToggle));
        Raise(nameof(StampForegroundStatus));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddObjectLayer()
    {
        var layer = new ObjectLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(ObjectLayer)),
            true, false, 1.0, []);
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void AddBrushLayer()
    {
        var layer = BrushLayer.CreateEmpty(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(BrushLayer)));
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    /// <summary>
    /// The ground texture the next new land starts with. It is chosen in the land panel right
    /// under "Новая земля", so it can be picked before any land exists. Null is plain colour.
    /// </summary>
    public string? NewLandTextureId { get; private set; } = MapFillTextures.OwnerSnow;

    /// <summary>
    /// Picks the ground texture for new land and, when a land layer is active, for that land too
    /// (one undoable step). The owner's own grounds are painted pictures, so they are shown as
    /// painted - fully opaque and at their own size - instead of mixed into the fill colour.
    /// </summary>
    public bool ChooseLandTexture(string? id)
    {
        if (id is not null && MapFillTextures.Find(id) is not { Kind: MapFillTextureKind.Ground }) return false;
        NewLandTextureId = id;
        return SetActiveLandStyle(style => WithGroundTexture(style, id));
    }

    public static LandEdgeStyle WithGroundTexture(LandEdgeStyle style, string? id)
    {
        return id switch
        {
            null => style with { FillTextureId = null },
            MapFillTextures.OwnerSnow or MapFillTextures.OwnerSnowSparkle or MapFillTextures.OwnerSnowSoft or MapFillTextures.OwnerIceWash =>
                style with { FillTextureId = id, FillTextureOpacity = 1, FillTextureScale = 1024 },
            _ => style with { FillTextureId = id },
        };
    }

    public void AddLandLayer()
    {
        var layer = LandLayer.CreateEmpty(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(LandLayer)));
        layer = layer with { EdgeStyle = WithGroundTexture(layer.EdgeStyle, NewLandTextureId) };
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        ActiveTool = EditorTool.Land;
        AfterEdit();
    }

    /// <summary>
    /// A further hex layer laid out exactly like the board below it (same orientation, size and
    /// extent), so hexes of the two layers line up - an overlay of features or a second terrain.
    /// </summary>
    public void AddHexLayer()
    {
        var model = ActiveLayer as HexLayer ?? Session.Document.Layers.OfType<HexLayer>().LastOrDefault();
        var board = model is null
            ? HexBoard.Create(HexOrientation.Flat, 64, 30, 20)
            : HexBoard.FromCells(model.Board.Layout, model.Board.Columns, model.Board.Rows, []);
        var layer = new HexLayer(LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(HexLayer)),
            true, false, 1, board) { Grid = model?.Grid ?? new HexGridStyle(), Level = model?.Level ?? 0 };
        // Hexes are the ground: a new hex layer goes over the other hex layers but stays under
        // the labels, which would otherwise hide under it.
        var topHexes = Session.Document.Layers.OfType<HexLayer>().LastOrDefault()?.Id;
        Session.Execute(new AddLayerCommand(layer, topHexes ?? _activeLayerId));
        _activeLayerId = layer.Id;
        ActiveTool = EditorTool.Hex;
        AfterEdit();
    }

    public void AddGridLayer()
    {
        var grid = new GridLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(GridLayer)),
            true, false, 1,
        [
            new GridDefinition(GridId.New(), GridGeometry.Square, 128)
            {
                Subdivisions = 2, IncludeInExport = false,
            },
        ]);
        Session.Execute(new AddLayerCommand(grid, _activeLayerId));
        _activeLayerId = grid.Id;
        AfterEdit();
    }

    public void AddFilterLayer()
    {
        var layer = new FilterLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(FilterLayer)),
            true, false, 1,
            new FilterSettings { Brightness = 0.08, Contrast = 0.12, Saturation = 0.1 });
        Session.Execute(new AddLayerCommand(layer, _activeLayerId));
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void CycleActiveFilter()
    {
        if (ActiveLayer is not FilterLayer { Locked: false } before) return;
        var warm = before.Settings.Temperature <= 0;
        var after = before with
        {
            Settings = before.Settings with
            {
                Brightness = warm ? 0.1 : -0.05,
                Contrast = warm ? 0.18 : 0.05,
                Saturation = warm ? 0.16 : -0.12,
                HueDegrees = warm ? 12 : -18,
                Temperature = warm ? 0.25 : -0.2,
            },
        };
        Session.Execute(new UpdateFilterCommand(before, after)); AfterEdit();
    }

    public bool SetActiveFilter(
        double? brightness = null, double? contrast = null, double? saturation = null,
        double? hueDegrees = null, double? temperature = null,
        double? textureOpacity = null, double? textureScale = null,
        bool resetTexture = false)
    {
        if (ActiveLayer is not FilterLayer { Locked: false } before) return false;
        var current = before.Settings;
        var updated = current with
        {
            Brightness = Math.Clamp(brightness ?? current.Brightness, -1, 1),
            Contrast = Math.Clamp(contrast ?? current.Contrast, -1, 1),
            Saturation = Math.Clamp(saturation ?? current.Saturation, -1, 1),
            HueDegrees = Math.Clamp(hueDegrees ?? current.HueDegrees, -180, 180),
            Temperature = Math.Clamp(temperature ?? current.Temperature, -1, 1),
            TextureOpacity = Math.Clamp(textureOpacity ?? current.TextureOpacity, 0, 1),
            TextureScale = Math.Clamp(textureScale ?? current.TextureScale, 0.05, 64),
            TextureAssetSha256 = resetTexture ? null : current.TextureAssetSha256,
        };
        if (updated == current) return false;
        Session.Execute(new UpdateFilterCommand(before, before with { Settings = updated }));
        AfterEdit();
        return true;
    }

    /// <summary>One click: the active filter layer takes a colour preset at full strength (one Undo step).</summary>
    public bool ApplyActiveFilterPreset(string presetId)
    {
        if (ActiveLayer is not FilterLayer { Locked: false } before
            || FilterColorEffects.Preset(presetId) is not { } preset) return false;
        return CommitFilter(before, FilterColorEffects.WithPreset(before.Settings, preset));
    }

    /// <summary>Removes the gradient map and post-processing of the active filter layer; old overlays stay.</summary>
    public bool ClearActiveFilterColor()
    {
        if (ActiveLayer is not FilterLayer { Locked: false } before) return false;
        return CommitFilter(before, FilterColorEffects.WithoutColor(before.Settings));
    }

    /// <summary>
    /// Edits the colour pass of the active filter layer. Strength is 0..1; saturation, brightness
    /// and contrast -1..1; sharpness 0..1. A manual change of any colour value detaches the preset
    /// caption; changing only the effect strength keeps it.
    /// </summary>
    public bool SetActiveFilterColor(
        double? colorStrength = null, double? gradientStrength = null,
        string? shadowColor = null, string? midtoneColor = null, string? highlightColor = null,
        double? saturation = null, double? brightness = null, double? contrast = null, double? sharpness = null)
    {
        if (ActiveLayer is not FilterLayer { Locked: false } before) return false;
        var current = before.Settings;
        static string Color(string? value, string fallback) => FilterColorEffects.IsColor(value) ? value! : fallback;
        var map = current.GradientMap with
        {
            ShadowColor = Color(shadowColor, current.GradientMap.ShadowColor),
            MidtoneColor = Color(midtoneColor, current.GradientMap.MidtoneColor),
            HighlightColor = Color(highlightColor, current.GradientMap.HighlightColor),
            Strength = Math.Clamp(gradientStrength ?? current.GradientMap.Strength, 0, 1),
        };
        var post = current.PostProcess with
        {
            Saturation = Math.Clamp(saturation ?? current.PostProcess.Saturation, -1, 1),
            Brightness = Math.Clamp(brightness ?? current.PostProcess.Brightness, -1, 1),
            Contrast = Math.Clamp(contrast ?? current.PostProcess.Contrast, -1, 1),
            Sharpness = Math.Clamp(sharpness ?? current.PostProcess.Sharpness, 0, 1),
        };
        var manual = map != current.GradientMap || post != current.PostProcess;
        var updated = current with
        {
            GradientMap = map,
            PostProcess = post,
            ColorStrength = Math.Clamp(colorStrength ?? current.ColorStrength, 0, 1),
            ColorPresetId = manual ? null : current.ColorPresetId,
        };
        return CommitFilter(before, updated);
    }

    private bool CommitFilter(FilterLayer before, FilterSettings updated)
    {
        if (updated == before.Settings) return false;
        Session.Execute(new UpdateFilterCommand(before, before with { Settings = updated }));
        AfterEdit();
        return true;
    }

    public void UseAssetAsActiveFilterTexture(AssetRecord record)
    {
        if (_assetResolver is null || ActiveLayer is not FilterLayer { Locked: false } before) return;
        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (reference is null)
        { StatusDetail = assetRefusal ?? "Текстура фильтра недоступна или изменила SHA-256."; return; }
        var after = before with
        {
            Settings = before.Settings with
            { TextureAssetSha256 = record.Sha256, TextureOpacity = 0.28, TextureScale = 1 },
        };
        Session.Execute(new UpdateFilterWithAssetCommand(
            before, after, reference)); AfterEdit();
    }

    public void CreateEditableComposition(
        bool includeBackground = false, bool includeGrids = false, bool includeFilters = false)
    {
        if (Session.SelectedObject is not { } selected
            || DocumentEditor.FindLayerOf(Session.Document, selected.Id) is not { } layer) return;
        var ids = selected.GroupId is { } group
            ? SceneGeometry.GroupMembers(Session.Document, group).Select(item => item.Id).ToArray()
            : [selected.Id];
        var sourceObjects = layer.Objects.Where(item => ids.Contains(item.Id)).ToList();
        ids = [.. sourceObjects.Select(item => item.Id)];
        var bounds = sourceObjects.Select(SceneGeometry.BoundsOf).Aggregate((a, b) => new WorldBounds(
            Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom)));
        var reference = ActiveSnapGrid()?.CellSize ?? 1;
        var package = CompositionTransfer.Copy(Session.Document, ids, new CompositionCaptureOptions
        {
            IncludeBackground = includeBackground,
            IncludeGrids = includeGrids,
            IncludeFilters = includeFilters,
            ReferenceGridScale = reference,
        }, selected.Name + " composition");
        var instance = CompositionTransfer.CreateInstance(package,
            (bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2, reference);
        Session.Execute(new ReplaceObjectsWithCompositionCommand(Session.Document, layer.Id, ids, instance));
        Session.SelectedObjectId = instance.Id; CompositionClipboard.Set(package); AfterEdit();
    }

    /// <summary>
    /// The selection as one nested composition package, for the explicit "Копировать композицию"
    /// command and for other map windows. This is a different product feature from Ctrl+C, which
    /// copies the objects themselves - see <see cref="CopySelectedObjects"/>.
    /// </summary>
    public void CopySelectedComposition()
    {
        var ids = SelectionWithWholeGroups();
        if (ids.Count == 0 || Session.SelectedObject is not { } primary) return;
        var package = CompositionTransfer.Copy(Session.Document, ids,
            new CompositionCaptureOptions { ReferenceGridScale = ActiveSnapGrid()?.CellSize ?? 1 },
            primary.Name + " copy");
        CompositionClipboard.Set(package); StatusDetail = "Композиция скопирована для другого окна карты.";
    }

    /// <summary>Pastes the composition buffer as one nested composition object.</summary>
    public void PasteComposition()
    {
        var package = CompositionClipboard.Get();
        if (package is null) { StatusDetail = "Сначала скопируйте композицию."; return; }
        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return; }
        var centre = ViewCentre();
        var instance = CompositionTransfer.CreateInstance(package, centre.X, centre.Y, ActiveSnapGrid()?.CellSize);
        ExecuteWithOptionalLayer(
            new PasteCompositionCommand(layer.Id, instance, package.Assets),
            createLayer, "Paste composition on a new layer");
        Session.SelectedObjectId = instance.Id; _activeLayerId = layer.Id; AfterEdit();
    }

    /// <summary>
    /// Every selected object, whole groups included, into Map's own object buffer. Copying shows
    /// no duplicate on the canvas and does not touch the document, so it is not an undoable edit.
    /// </summary>
    public void CopySelectedObjects()
    {
        var ids = SelectionWithWholeGroups();
        if (ids.Count == 0)
        {
            StatusDetail = "Сначала выделите хотя бы один объект.";
            return;
        }

        ObjectClipboard.Set(ObjectTransfer.Copy(Session.Document, ids));
        StatusDetail = $"Скопировано: {EditorLabels.Objects(ids.Count)}.";
        if (ids.Any(id => PlacementOwnership.Owner(Session.Document, id) is not null))
            StatusDetail += " Копии будут обычными объектами; исходная конструкция сохранена.";
        Raise(nameof(CanPasteObjects));
    }

    /// <summary>True when the object buffer holds something this window could paste.</summary>
    public bool CanPasteObjects => ObjectClipboard.Count > 0;

    /// <summary>
    /// The selection widened to whole groups: copying one member of a group copies the group, the
    /// same unit the user selects and moves on the canvas.
    /// </summary>
    private IReadOnlyList<ObjectId> SelectionWithWholeGroups()
    {
        var selected = Session.SelectedObjects;
        if (selected.Count == 0)
        {
            return [];
        }

        var ids = new List<ObjectId>();
        var seen = new HashSet<ObjectId>();
        foreach (var sceneObject in selected)
        {
            var unit = sceneObject.GroupId is { } group
                ? SceneGeometry.GroupMembers(Session.Document, group).Select(member => member.Id)
                : [sceneObject.Id];
            foreach (var id in unit)
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    public AssetRecord? RepairAssetSource(AssetRecord record, string replacementPath)
    {
        if (_assetCatalog is null) return null;
        try
        {
            var repaired = _assetCatalog.RepairSource(record, replacementPath);
            RefreshLibrary();
            StatusDetail = "Исходник ассета восстановлен и проверен по SHA-256 и формату.";
            return repaired;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Ассет не заменён: " + error.Message;
            _log.Error("ASSET_REPAIR_REJECTED", error);
            return null;
        }
    }

    public bool RemoveAssetFromCatalog(AssetRecord record)
    {
        if (_assetCatalog is null) return false;
        try
        {
            _assetCatalog.RemoveRecord(record.Sha256);
            RefreshLibrary();
            StatusDetail = "Ассет удалён из каталога; сохранённые исходные bytes не уничтожены.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Ассет не удалён из каталога: " + error.Message;
            _log.Error("ASSET_CATALOG_REMOVE_FAILED", error);
            return false;
        }
    }

    public bool ExportSelectedCompositionPackage(string path)
    {
        if (Session.SelectedObjectIds.Count == 0)
        {
            StatusDetail = "Чтобы сохранить композицию в файл, сначала выделите хотя бы один объект.";
            return false;
        }
        try
        {
            var package = CompositionTransfer.Copy(
                Session.Document, Session.SelectedObjectIds,
                new CompositionCaptureOptions { ReferenceGridScale = ActiveSnapGrid()?.CellSize ?? 1 },
                Session.SelectedObject?.Name + " package" ?? "Composition package");
            var now = _clock();
            CompositionPackageStore.Export(
                package, path, new SaveMetadata(_appVersion, now),
                RenderPreview(Session.Document));
            StatusDetail = "Композиция сохранена в файл вместе с контрольными суммами и сведениями об источниках.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or MapProjectException or ArgumentException)
        {
            StatusDetail = "Композиция не сохранена в файл; карта не изменена.";
            _log.Error("COMPOSITION_PACKAGE_EXPORT_FAILED", error);
            return false;
        }
    }

    public bool ImportCompositionPackage(string path)
    {
        var layer = ActiveObjectLayer();
        if (layer is null)
        {
            StatusDetail = "Чтобы вставить композицию из файла, выберите разблокированный слой объектов.";
            return false;
        }
        try
        {
            var package = CompositionPackageStore.Import(path);
            if (!MapProjectStore.CanAdmitAdditionalAssets(Session.Document, package.Assets, out var reason))
            {
                StatusDetail = "Пакет не помещается в бюджет проекта: " + reason;
                return false;
            }
            var centre = ViewCentre();
            var instance = CompositionTransfer.CreateInstance(
                package, centre.X, centre.Y, ActiveSnapGrid()?.CellSize);
            Session.Execute(new PasteCompositionCommand(layer.Id, instance, package.Assets));
            Session.SelectedObjectId = instance.Id;
            _activeLayerId = layer.Id;
            AfterEdit();
            StatusDetail = "Композиция из файла вставлена как редактируемая.";
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or MapProjectException or ArgumentException)
        {
            StatusDetail = "Файл композиции отклонён; карта не изменена.";
            _log.Error("COMPOSITION_PACKAGE_IMPORT_FAILED", error);
            return false;
        }
    }

    /// <summary>
    /// Restores the whole copied buffer as real, separately editable objects: the same count, the
    /// same mutual layout, sizes, rotations, flips, opacity, sublayers, styles and asset identity,
    /// in the same draw order, but with fresh ids. It is ONE history entry, so a single undo takes
    /// the entire pasted group away and a single redo brings all of it back, and the new group -
    /// not one arbitrary member of it - becomes the selection. Repeating the paste cascades by a
    /// steady step instead of stacking every copy on one spot.
    /// </summary>
    public void PasteObjects()
    {
        if (ObjectClipboard.Get() is not { } payload)
        {
            StatusDetail = "Сначала скопируйте объекты.";
            return;
        }

        if (payload.Assets.Count > 0
            && !MapProjectStore.CanAdmitAdditionalAssets(Session.Document, payload.Assets, out var budgetRefusal))
        {
            StatusDetail = "Вставка не помещается в бюджет проекта: " + budgetRefusal;
            return;
        }

        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null)
        {
            StatusDetail = NoUsableObjectLayer;
            return;
        }

        var step = Math.Clamp(ActiveSnapGrid()?.CellSize ?? 24, 8, 128);
        var shift = step * ObjectClipboard.TakePasteOrdinal();
        var objects = ObjectTransfer.Instantiate(
            payload, payload.SourceLeft + shift, payload.SourceTop + shift);

        var paste = new PasteObjectsCommand(layer.Id, objects, payload.Assets);
        ExecuteWithOptionalLayer(paste, createLayer, "Paste objects on a new layer");
        _activeLayerId = layer.Id;
        Session.SetSelection(paste.PastedObjectIds);
        AfterEdit();
        AfterSelectionChange();
        StatusDetail = $"Вставлено: {EditorLabels.Objects(objects.Count)}.";
    }

    public void FlattenSelectedComposition()
    {
        if (EditableSelection() is not { Composition: not null } source) return;
        var flattened = CompositionFlattener.Flatten(source, StampImages);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(flattened.Png));
        var provenance = source.Composition!.Assets.Count == 0
            ? "flattened user composition"
            : "flattened composition: " + string.Join(", ", source.Composition.Assets.Select(item => item.Provenance).Distinct());
        var asset = new MapAssetReference(hash, source.Name + " flattened", "image/png", "png",
            ProjectAssetStorage.Embedded, _clock(), provenance, "derived from user-provided content",
            embeddedContent: flattened.Png);
        var stamp = new SceneObject(source.Id, SceneObjectKind.Stamp, source.Name + " flattened",
            source.X, source.Y, source.Width, source.Height, source.RotationDegrees,
            "#FFFFFFFF", "#00000000", 0, source.Visible, source.Locked)
        { Stamp = new StampStyle(hash), ObjectOpacity = source.ObjectOpacity };
        Session.Execute(new FlattenCompositionCommand(source, stamp, asset)); AfterEdit();
    }

    public void CycleActiveGridGeometry()
    {
        if (ActiveLayer is not GridLayer { Locked: false, Grids.Count: > 0 } before) return;
        var current = before.Grids[0];
        var next = current.Geometry switch
        {
            GridGeometry.Square => GridGeometry.IsometricSquares,
            GridGeometry.IsometricSquares => GridGeometry.HexFlat,
            GridGeometry.HexFlat => GridGeometry.HexPointy,
            _ => GridGeometry.Square,
        };
        SetActiveGridGeometry(next);
    }

    public void ToggleActiveGridExport()
    {
        if (ActiveLayer is GridLayer { Grids.Count: > 0 } layer)
            SetActiveGridIncludeInExport(!layer.Grids[0].IncludeInExport);
    }

    public bool SetActiveGridGeometry(GridGeometry value) =>
        EditActiveGrid(grid => grid with { Geometry = value }, "геометрию");

    public bool SetActiveGridCellSize(double value) =>
        EditActiveGrid(grid => grid with { CellSize = value }, "размер ячейки");

    public bool SetActiveGridSubdivisions(double value)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value))
            return RejectGridValue("«Деления» должно быть целым числом.");
        return EditActiveGrid(grid => grid with { Subdivisions = checked((int)value) }, "деления");
    }

    public bool SetActiveGridLineWidth(double value) =>
        EditActiveGrid(grid => grid with { LineWidth = value }, "толщину линии");

    public bool SetActiveGridColor(string value) =>
        EditActiveGrid(grid => grid with { Color = value }, "цвет");

    public bool SetActiveGridOpacityPercent(double value) =>
        EditActiveGrid(grid => grid with { Opacity = value / 100 }, "непрозрачность");

    public bool SetActiveGridOffsetX(double value) =>
        EditActiveGrid(grid => grid with { OffsetX = value }, "смещение по X");

    public bool SetActiveGridOffsetY(double value) =>
        EditActiveGrid(grid => grid with { OffsetY = value }, "смещение по Y");

    public bool SetActiveGridRotation(double value) =>
        EditActiveGrid(grid => grid with { RotationDegrees = value }, "поворот");

    public bool SetActiveGridSnapEnabled(bool value) =>
        EditActiveGrid(grid => grid with { SnapEnabled = value }, "привязку");

    public bool SetActiveGridIncludeInExport(bool value) =>
        EditActiveGrid(grid => grid with { IncludeInExport = value }, "экспорт");

    private bool EditActiveGrid(Func<GridDefinition, GridDefinition> rewrite, string propertyName)
    {
        if (ActiveLayer is not GridLayer { Grids.Count: > 0 } before)
            return RejectGridValue("Сначала выберите слой сетки.");
        if (before.Locked)
        {
            StatusDetail = "Заблокированный слой сетки доступен только для чтения.";
            return false;
        }

        try
        {
            var current = before.Grids[0];
            var updated = rewrite(current);
            if (updated == current) return false;
            Session.Execute(new ReplaceGridLayerCommand(before, before with
            {
                Grids = [updated, .. before.Grids.Skip(1)],
            }));
            AfterEdit();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            return RejectGridValue($"Не удалось изменить {propertyName}: значение вне допустимого диапазона.");
        }
    }

    private bool RejectGridValue(string message)
    {
        ReportInspectorError(message);
        StatusDetail = message;
        return false;
    }

    /// <summary>Global snap switch used by the G shortcut. The topmost visible grid is authoritative.</summary>
    public void ToggleGridSnap()
    {
        var before = Session.Document.Layers.OfType<GridLayer>()
            .LastOrDefault(layer => layer.Visible && !layer.Locked && layer.Grids.Count > 0);
        if (before is null)
        {
            StatusDetail = "Нет видимой разблокированной сетки для привязки.";
            return;
        }

        var current = before.Grids[0];
        Session.Execute(new ReplaceGridLayerCommand(before, before with
        {
            Grids = [current with { SnapEnabled = !current.SnapEnabled }, .. before.Grids.Skip(1)],
        }));
        AfterEdit();
        StatusDetail = current.SnapEnabled ? "Привязка к сетке выключена." : "Привязка к сетке включена.";
    }

    private GridDefinition? ActiveSnapGrid() => Session.Document.Layers
        .OfType<GridLayer>().Where(layer => layer.Visible)
        .SelectMany(layer => layer.Grids).LastOrDefault(grid => grid.SnapEnabled);

    public void SnapSelectedObjectToGrid()
    {
        if (IsHexWorkshop && EditableSelection() is { } label && ActiveHexLayer is { } hexes)
        {
            // In a hex map "the grid" is the hexes: a label goes to the middle of its hex.
            var layout = hexes.Board.Layout;
            var (cx, cy) = layout.Center(layout.HexAt(label.X + label.Width / 2, label.Y + label.Height / 2));
            var moved = MultiSelectionGeometry.Translate([label], cx - label.Width / 2 - label.X, cy - label.Height / 2 - label.Y)[0];
            if (moved != label) ReplaceSelected(label, moved, "Snap object to hex");
            StatusDetail = "Объект поставлен в центр гекса.";
            return;
        }

        if (EditableSelection() is not { } before || ActiveSnapGrid() is not { } grid) return;
        ReplaceSelected(before, GridSnapService.SnapObject(before, grid), "Snap object to grid");
    }

    public void SnapSelectedNodeToGrid()
    {
        if (EditableSelection() is not { } before || ActiveSnapGrid() is not { } grid
            || _selectedVectorNodeId is not { } nodeId) return;
        ReplaceSelected(before, GridSnapService.SnapNode(before, nodeId, grid), "Snap selected node to grid");
    }

    public void SnapSelectedPortalToGrid()
    {
        if (EditableSelection() is not { Line: not null } before || ActiveSnapGrid() is not { } grid) return;
        // A line with a single portal needs no portal click first: the button means that one.
        var chosen = _selectedPortalId ?? (before.Line.Portals.Count == 1 ? before.Line.Portals[0].Id : null);
        if (chosen is not { } portalId) return;
        var after = GridSnapService.SnapPortal(before, portalId, grid, out var outcome);
        if (outcome == PortalSnapOutcome.AlreadySnapped)
        {
            StatusDetail = "Проход уже привязан к сетке.";
            return;
        }

        if (outcome == PortalSnapOutcome.NotRepresentable)
        {
            StatusDetail = "Точная привязка прохода невозможна без перемещения линии.";
            return;
        }

        ReplaceSelected(before, after, "Snap portal to grid");
        StatusDetail = "Проход привязан к сетке.";
    }

    public void AddNoteAtViewCentre()
    {
        var centre = ViewCentre();
        AddNoteAt(centre.X, centre.Y);
    }

    /// <summary>
    /// Creates one world-anchored note where the user pointed. The Notes tool had no canvas
    /// behaviour at all, so it looked active while the only way to make a note was a panel button
    /// that always dropped it in the middle of the view.
    /// </summary>
    public NoteId AddNoteAt(double worldX, double worldY)
    {
        var note = new MapNote(NoteId.New(), $"Заметка {Notes.Count + 1}", "Локальная заметка",
            NoteAnchorKind.World, worldX, worldY);
        Session.Execute(new AddNoteCommand(note));
        AfterEdit();
        StatusDetail = $"Создана заметка «{note.Title}».";
        return note.Id;
    }

    public void AddNoteForSelection()
    {
        if (Session.SelectedObject is not { } selected) return;
        Session.Execute(new AddNoteCommand(new MapNote(
            NoteId.New(), $"О {selected.Name}", "Локальная заметка объекта",
            NoteAnchorKind.Object, objectId: selected.Id)));
        AfterEdit();
    }

    public void EditNote(NoteId id, string title, string markdown)
    {
        var before = Notes.FirstOrDefault(note => note.Id == id);
        if (before is null || string.IsNullOrWhiteSpace(title)) return;
        try
        {
            // A key note of the city the user rewrites becomes theirs: «Перестроить город» keeps it (R35b).
            var changed = before.Title != title.Trim() || before.Markdown != markdown;
            Session.Execute(new ReplaceNoteCommand(before, before with
            {
                Title = title.Trim(), Markdown = markdown, Origin = changed ? NoteOrigin.Manual : before.Origin,
            }));
            AfterEdit();
        }
        catch (ArgumentException ex) { StatusDetail = ex.Message; }
    }

    public void DeleteNote(NoteId id)
    {
        if (Notes.All(note => note.Id != id)) return;
        Session.Execute(new DeleteNoteCommand(Session.Document, id));
        AfterEdit();
    }

    public void GoToNote(NoteId id)
    {
        var note = Notes.FirstOrDefault(item => item.Id == id);
        if (note is null) return;
        var point = (X: note.WorldX, Y: note.WorldY);
        if (note.AnchorKind == NoteAnchorKind.Object && note.ObjectId is { } objectId)
        {
            var target = Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)
                .FirstOrDefault(item => item.Id == objectId);
            if (target is null) { StatusDetail = "Якорный объект заметки удалён."; return; }
            Session.SelectedObjectId = target.Id;
            point = (target.X + target.Width / 2, target.Y + target.Height / 2);
        }
        var viewport = Session.Viewport;
        viewport.Set(point.X - viewport.ViewWidth / (2 * viewport.Scale),
            point.Y - viewport.ViewHeight / (2 * viewport.Scale), viewport.Scale);
        Raise(nameof(ViewportStatus)); SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenameActiveLayer(string name)
    {
        if (ActiveLayer is not { } layer || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.Rename(layer, name.Trim()));
        AfterEdit();
    }

    public void ToggleActiveLayerVisibility() => ToggleLayerVisibility(_activeLayerId);

    public void ToggleActiveLayerLock() => ToggleLayerLock(_activeLayerId);

    /// <summary>
    /// Hides or shows one named layer without making it active. The list rows used to select the
    /// layer first, so clicking an eye quietly moved the target of the next new object.
    /// </summary>
    public void ToggleLayerVisibility(LayerId? layerId)
    {
        if (layerId is not { } id || Session.Document.FindLayer(id) is not { } layer)
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetVisible(layer, !layer.Visible));
        AfterEdit();
    }

    /// <summary>Locks or unlocks one named layer without making it active.</summary>
    public void ToggleLayerLock(LayerId? layerId)
    {
        if (layerId is not { } id || Session.Document.FindLayer(id) is not { } layer)
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetLocked(layer, !layer.Locked));
        AfterEdit();
    }

    public void CycleActiveLayerBlend()
    {
        if (ActiveLayer is not { } layer)
        {
            return;
        }

        var next = layer.Blend switch
        {
            "Normal" => "Multiply",
            "Multiply" => "Screen",
            _ => "Normal",
        };
        Session.Execute(UpdateLayerCommand.SetBlend(layer, next));
        AfterEdit();
    }

    public bool BeginTextPlacement(double worldX, double worldY)
    {
        if (ActiveTool != EditorTool.Text) return false;
        if (ActiveObjectLayer() is not { } layer)
        {
            StatusDetail = "Нет видимого разблокированного слоя объектов — текст не создан.";
            return false;
        }

        _textPlacementStart = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        _textPreviewLayerId = layer.Id;
        _textPlacementTemplate = TextInheritFromSelection && Session.SelectedObject?.Text is not null
            ? Session.SelectedObject
            : null;
        _textPlacementStyle = (_textPlacementTemplate?.Text ?? new TextStyle()) with
        {
            Content = "Новый текст\nна карте",
        };
        _textPreviewObject = BuildTextPlacement(_textPlacementStart.Value, _textPlacementStart.Value, preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    public void ContinueTextPlacement(double worldX, double worldY)
    {
        if (_textPlacementStart is not { } start) return;
        var end = SnapVectorGesturePoint(new VectorPoint(worldX, worldY));
        _textPreviewObject = BuildTextPlacement(start, end, preview: true);
        Raise(nameof(PlacementPreviewObjects));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndTextPlacement()
    {
        if (_textPlacementStart is null || _textPreviewLayerId is not { } layerId
            || _textPreviewObject is not { } preview
            || Session.Document.FindLayer(layerId) is not ObjectLayer)
        {
            CancelTextPlacement(); return;
        }
        var added = preview with { ObjectOpacity = _textPlacementTemplate?.ObjectOpacity ?? 1 };
        Session.Execute(new AddObjectCommand(layerId, added));
        Session.SelectedObjectId = added.Id;
        _activeLayerId = layerId;
        CancelTextPlacement(clearOnly: true);
        AfterEdit();
    }

    public void CancelTextPlacement() => CancelTextPlacement(clearOnly: false);

    private void CancelTextPlacement(bool clearOnly)
    {
        _textPlacementStart = null;
        _textPlacementStyle = null;
        _textPlacementTemplate = null;
        _textPreviewObject = null;
        _textPreviewLayerId = null;
        Raise(nameof(PlacementPreviewObjects));
        if (!clearOnly) InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    private SceneObject BuildTextPlacement(VectorPoint start, VectorPoint end, bool preview)
    {
        var dragged = Math.Abs(end.X - start.X) >= 8 || Math.Abs(end.Y - start.Y) >= 8;
        var left = dragged ? Math.Min(start.X, end.X) : start.X - 180;
        var top = dragged ? Math.Min(start.Y, end.Y) : start.Y - 70;
        var width = dragged ? Math.Max(32, Math.Abs(end.X - start.X)) : 360;
        var height = dragged ? Math.Max(24, Math.Abs(end.Y - start.Y)) : 140;
        var template = _textPlacementTemplate;
        return new SceneObject(
            _textPreviewObject?.Id ?? ObjectId.New(), SceneObjectKind.Text,
            EntityNameGenerator.Text(Session.Document, _textPlacementStyle!.Content),
            left, top, width, height, template?.RotationDegrees ?? 0,
            "#FF20242B", "#FFFFFFFF", 0, true, false)
        {
            Text = _textPlacementStyle,
            Sublayer = template?.Sublayer ?? 0,
            ObjectOpacity = preview ? .55 : template?.ObjectOpacity ?? 1,
        };
    }

    public bool SetActiveLayerOpacity(double percent)
    {
        if (ActiveLayer is not { Locked: false } layer || !double.IsFinite(percent)) return false;
        var value = Math.Clamp(percent / 100, 0, 1);
        if (Math.Abs(value - layer.Opacity) < 0.000001) return false;
        Session.Execute(UpdateLayerCommand.SetOpacity(layer, value)); AfterEdit(); return true;
    }

    public bool SetActiveLayerBlend(string blend)
    {
        if (ActiveLayer is not { Locked: false } layer || !BlendModes.IsSupported(blend)) return false;
        if (layer.Blend == blend) return false;
        Session.Execute(UpdateLayerCommand.SetBlend(layer, blend)); AfterEdit(); return true;
    }

    public LayerEffects? ActiveObjectLayerEffects => (ActiveLayer as ObjectLayer)?.Effects;

    public ObjectSublayerState? ActiveObjectSublayerState(int sublayer) =>
        ActiveLayer is ObjectLayer layer ? layer.StateFor(Math.Clamp(sublayer, -10, 10)) : null;

    public bool SetActiveObjectLayerEffects(LayerEffects value)
    {
        if (ActiveLayer is not ObjectLayer { Locked: false } before || value is null) return false;
        var color = AppearanceColor.Normalize(value.ShadowColor);
        if (color is null) return false;
        var safe = value with
        {
            ShadowColor = color,
            OuterShadowOpacity = Math.Clamp(value.OuterShadowOpacity, 0, 1),
            InnerShadowOpacity = Math.Clamp(value.InnerShadowOpacity, 0, 1),
            ShadowOffsetX = Math.Clamp(value.ShadowOffsetX, -512, 512),
            ShadowOffsetY = Math.Clamp(value.ShadowOffsetY, -512, 512),
            ShadowBlur = Math.Clamp(value.ShadowBlur, 0, 256),
        };
        if (safe == before.Effects) return false;
        Session.Execute(UpdateLayerCommand.Replace(before, before with { Effects = safe }, "Object layer effects"));
        AfterEdit(); return true;
    }

    public bool SetActiveObjectSublayerState(int sublayer, bool? visible = null, bool? locked = null)
    {
        if (ActiveLayer is not ObjectLayer { Locked: false } before) return false;
        sublayer = Math.Clamp(sublayer, -10, 10);
        var old = before.StateFor(sublayer);
        var next = old with { Visible = visible ?? old.Visible, Locked = locked ?? old.Locked };
        if (next == old) return false;
        var states = before.SublayerStates.Where(item => item.Sublayer != sublayer).ToList();
        if (!next.Visible || next.Locked) states.Add(next);
        states.Sort((left, right) => left.Sublayer.CompareTo(right.Sublayer));
        Session.Execute(UpdateLayerCommand.Replace(before,
            before with { SublayerStates = states }, "Object sublayer state"));
        AfterEdit(); return true;
    }

    public void ReorderActiveLayer(bool up)
    {
        if (ActiveLayer is null)
        {
            return;
        }

        if (!LayerOrderPolicy.CanMove(Session.Document, ActiveLayer.Id, up))
        {
            StatusDetail = ActiveLayer switch
            {
                LandLayer when up => "Слой земли остаётся ниже слоёв объектов.",
                ObjectLayer when !up => "Слой объектов остаётся выше слоёв земли.",
                _ => up ? "Слой уже находится сверху." : "Слой уже находится снизу.",
            };
            return;
        }

        Session.Execute(new ReorderLayerCommand(ActiveLayer.Id, up));
        AfterEdit();
    }

    public void DuplicateActiveLayer()
    {
        if (ActiveLayer is ObjectLayer owned && (!owned.PlacementPaths.IsEmpty || !owned.PlacementAreas.IsEmpty))
        { StatusDetail = "Перед дублированием слоя превратите его пути и области в обычные объекты."; return; }
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        var command = new DuplicateLayerCommand(ActiveLayer.Id);
        Session.Execute(command);
        _activeLayerId = command.CopyLayerId;
        AfterEdit();
    }

    public void DeleteActiveLayer()
    {
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        var deleted = ActiveLayer.Id;
        Session.Execute(new DeleteLayerCommand(Session.Document, deleted));
        _activeLayerId = Session.Document.Layers[Session.Document.Layers.Count - 1].Id;
        AfterEdit();
    }

    public void ToggleActiveMask()
    {
        if (ActiveLayer is null or BaseColorLayer)
        {
            return;
        }

        Session.Execute(ActiveLayer.Mask is null
            ? UpdateMaskCommand.AddMask(ActiveLayer)
            : UpdateMaskCommand.SetEnabled(ActiveLayer, !ActiveLayer.Mask.Enabled));
        AfterEdit();
        Raise(nameof(MaskEditLayerId));
        Raise(nameof(Notes));
        Raise(nameof(SelectedTextFontResolution));
    }

    public void RemoveActiveMask()
    {
        if (ActiveLayer is not { Mask: not null } layer || layer is BaseColorLayer) return;
        Session.Execute(UpdateMaskCommand.RemoveMask(layer)); AfterEdit();
    }

    public void CopyActiveMask()
    {
        if (ActiveLayer is not { Mask: not null } layer) return;
        _copiedMaskLayerId = layer.Id;
        StatusDetail = "Маска скопирована; исходный слой не изменён.";
    }

    public bool PasteCopiedMask()
    {
        if (_copiedMaskLayerId is not { } sourceId
            || Session.Document.FindLayer(sourceId) is not { Mask: not null } source
            || ActiveLayer is not { } target || target is BaseColorLayer || source.Id == target.Id)
        {
            StatusDetail = "Выберите другой совместимый слой для копии маски.";
            return false;
        }
        Session.Execute(UpdateMaskCommand.CopyMask(source, target)); AfterEdit(); return true;
    }
}
