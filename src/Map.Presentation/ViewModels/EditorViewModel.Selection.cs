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
    // ------------------------------------------------------------- raster tool targets

    /// <summary>Whether one layer is the kind of surface a given raster tool paints on.</summary>
    private static bool FitsRasterTool(MapLayer layer, EditorTool tool) => tool switch
    {
        EditorTool.Brush => layer is BrushLayer,
        EditorTool.Land => layer is LandLayer,
        EditorTool.Mask => layer is not BaseColorLayer && layer.Mask is not null,
        _ => false,
    };

    /// <summary>
    /// The layer a raster tool would paint on: the active one when it fits, otherwise the topmost
    /// visible unlocked layer of the kind the tool needs.
    /// <para>
    /// The vector tools have always resolved their layer like this - see
    /// <see cref="ActiveObjectLayer"/> - which is why Stamp, Line and Shape "just work". Brush,
    /// Land and Mask instead demanded that the right kind of layer was already active, and a new
    /// map starts on its object layer, so all three refused every stroke and looked broken.
    /// </para>
    /// <para>
    /// A layer that fits but is hidden or locked is deliberately NOT replaced by another: the user
    /// locked it on purpose, and quietly painting somewhere else would be worse than refusing.
    /// </para>
    /// </summary>
    private MapLayer? RasterTargetLayer(EditorTool tool)
    {
        if (ActiveLayer is { } active && FitsRasterTool(active, tool))
        {
            return active is { Visible: true, Locked: false } ? active : null;
        }

        return Session.Document.Layers.LastOrDefault(
            layer => layer is { Visible: true, Locked: false } && FitsRasterTool(layer, tool));
    }

    /// <summary>True when the active tool has somewhere to paint right now.</summary>
    public bool RasterToolReady =>
        ActiveTool is not (EditorTool.Brush or EditorTool.Land or EditorTool.Mask)
        || RasterTargetLayer(ActiveTool) is not null;

    /// <summary>
    /// What the panel tells the user about the active raster tool: either which layer it will
    /// paint on, or what is missing. Empty for the tools this does not apply to.
    /// </summary>
    public string RasterToolHint
    {
        get
        {
            if (ActiveTool is not (EditorTool.Brush or EditorTool.Land or EditorTool.Mask))
            {
                return "";
            }

            return RasterTargetLayer(ActiveTool) is { } target
                ? $"Рисует по слою «{target.Name}»."
                : RasterToolRefusal(ActiveTool);
        }
    }

    /// <summary>The label of the panel action that makes the active raster tool usable.</summary>
    public string RasterToolFixLabel => ActiveTool switch
    {
        EditorTool.Brush => "Создать слой кисти и продолжить",
        EditorTool.Land => "Создать слой земли и продолжить",
        EditorTool.Mask => "Добавить маску активному слою",
        _ => "",
    };

    private string RasterToolRefusal(EditorTool tool)
    {
        var blocked = ActiveLayer is { } active && FitsRasterTool(active, tool)
            && (!active.Visible || active.Locked);
        if (blocked)
        {
            return $"Слой «{ActiveLayer!.Name}» скрыт или заблокирован — штрих не применён.";
        }

        return tool switch
        {
            EditorTool.Mask => "Ни у одного слоя нет маски. Добавьте маску слою — кнопкой ниже или на вкладке «Слои».",
            EditorTool.Land => "В карте нет слоя земли. Создайте его кнопкой ниже.",
            _ => "В карте нет слоя кисти. Создайте его кнопкой ниже.",
        };
    }

    /// <summary>
    /// Creates whatever the active raster tool is missing and leaves it ready to paint. One
    /// command, so a wrong click is one undo.
    /// </summary>
    public bool CreateLayerForActiveTool()
    {
        switch (ActiveTool)
        {
            case EditorTool.Brush:
                AddBrushLayer();
                StatusDetail = "Создан слой кисти. Можно рисовать.";
                return true;
            case EditorTool.Land:
                AddLandLayer();
                StatusDetail = "Создан слой земли. Можно рисовать.";
                return true;
            case EditorTool.Mask:
                // A mask belongs to an existing layer, so pick one that can hold it first.
                if (ActiveLayer is null or BaseColorLayer)
                {
                    if (Session.Document.Layers.LastOrDefault(
                        layer => layer is not BaseColorLayer) is not { } host)
                    {
                        StatusDetail = "Нет слоя, которому можно добавить маску.";
                        return false;
                    }

                    SelectLayer(host.Id);
                }

                if (ActiveLayer?.Mask is not null)
                {
                    StatusDetail = $"У слоя «{ActiveLayer.Name}» уже есть маска.";
                    return false;
                }

                ToggleActiveMask();
                StatusDetail = $"Маска добавлена слою «{ActiveLayer?.Name}». Можно рисовать.";
                return true;
            default:
                return false;
        }
    }

    /// <summary>Makes one layer active, with the same notifications a click in the panel sends.</summary>
    private void UseAsActiveLayer(MapLayer layer)
    {
        if (_activeLayerId != layer.Id)
        {
            SelectLayer(layer.Id);
        }
    }

    public bool BeginStroke(double worldX, double worldY, double pressure = 1)
    {
        if (_editingStampForeground)
        {
            if (ActiveTool != EditorTool.Brush || Session.Document.StampForeground is not { } foreground)
            {
                StatusDetail = "Поверхность переднего плана недоступна для рисования.";
                return false;
            }
            BrushSettings = BrushSettings with
            {
                Seed = GestureSeed.From(Session.Revision, "stamp-foreground-stroke", 0, ++_brushSeedCounter),
            };
            _stroke = new RasterStrokeSession(foreground.Tiles, BrushSettings);
            _stroke.AddPoint(worldX, worldY, pressure);
            PublishStrokeSnapshot();
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return true;
        }

        if (RasterTargetLayer(ActiveTool) is not { } layer)
        {
            StatusDetail = RasterToolRefusal(ActiveTool);
            return false;
        }

        // Painting and the layers panel have to agree: the stroke is committed against
        // _activeLayerId, so the layer the tool fell back to becomes the active one.
        UseAsActiveLayer(layer);

        TileMap target = ActiveTool switch
        {
            EditorTool.Brush => ((BrushLayer)layer).Tiles,
            EditorTool.Mask => layer.Mask!.Tiles,
            _ => LandReliefMode ? ((LandLayer)layer).ReliefTiles
                : LandCliffMaskMode ? CliffMaskTargets((LandLayer)layer).Primary
                : LandPaintMode ? ((LandLayer)layer).PaintTiles : ((LandLayer)layer).GeometryTiles,
        };

        // The stroke's randomness is decided here, BEFORE any command exists: the committed
        // tiles never depend on replay, so undo/redo restore bytes instead of re-rolling.
        BrushSettings = BrushSettings with
        {
            Seed = GestureSeed.From(Session.Revision, "brush-stroke", 0, ++_brushSeedCounter),
        };

        _stroke = new RasterStrokeSession(target,
            ActiveTool == EditorTool.Land
                ? LandReliefMode ? ReliefSettings() : LandCliffMaskMode ? CliffMaskSettings() : LandPaintMode ? LandPaintSettings() : LandShapeSettings()
                : BrushSettings);
        _cliffCompanion = ActiveTool == EditorTool.Land && LandCliffMaskMode && !LandReliefMode
            ? new RasterStrokeSession(CliffMaskTargets((LandLayer)layer).Companion, CliffMaskSettings()) : null;
        _stroke.AddPoint(worldX, worldY, pressure);
        _cliffCompanion?.AddPoint(worldX, worldY, pressure);
        PublishStrokeSnapshot();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    private double _landRoughness = 64;

    /// <summary>
    /// Feature size, in world units, of the coast a freehand Land stroke leaves: 0 draws a plain
    /// circle whose edge is the smoothing; any other value makes the dab out of the irregular
    /// cells of <see cref="RoughCells"/>, so the shore comes out jagged like a drawn coastline
    /// instead of a row of circles. Default 64, the size of a small bay on a regional map.
    /// </summary>
    public double LandRoughness
    {
        get => _landRoughness;
        set
        {
            var bounded = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 512);
            if (Math.Abs(_landRoughness - bounded) < 1e-9) return;
            CancelStroke();
            _landRoughness = bounded;
            Raise(nameof(LandRoughness));
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The footprint the raster tools would paint at the pointer, for the canvas to outline:
    /// the brush circle, or the cells of a rough land dab. Null when the pointer is not over a
    /// painting tool - a region drag, a workflow dialog or any other tool shows nothing.
    /// </summary>
    public Map.Rendering.BrushCursorPreview? BrushCursorAt(double worldX, double worldY)
    {
        if (CanvasWorkflowOpen) return null;
        if (FogBrushOn) return new(worldX, worldY, FogBrushRadius, 0, !FogReveal);
        switch (ActiveTool)
        {
            case EditorTool.Brush:
            case EditorTool.Mask:
                return new(worldX, worldY, BrushSettings.Radius, 0, BrushSettings.Eraser || (ActiveTool == EditorTool.Mask && MaskMode == MaskPaintMode.Hide));
            case EditorTool.Land when LandReliefMode:
                return new(worldX, worldY, BrushSettings.Radius, 0, LandReliefAction == ReliefAction.Lower);
            case EditorTool.Land when LandCliffMaskMode:
                return new(worldX, worldY, BrushSettings.Radius, 0, LandCliffMaskAction == CliffMaskAction.Erase);
            case EditorTool.Land when LandPaintMode:
                return new(worldX, worldY, BrushSettings.Radius, 0, LandPaintEraser);
            case EditorTool.Land when !LandDrawsRegion:
                return new(worldX, worldY, BrushSettings.Radius, LandRoughness, LandSubtract);
            default:
                return null;
        }
    }

    /// <summary>The brush of a freehand Land stroke: the shared settings with the land's roughness.</summary>
    private BrushSettings LandShapeSettings() =>
        BrushSettings with { RoughCellSize = LandRoughness, RoughSmoothing = BrushSettings.Softness, Scatter = 0 };

    public void ContinueStroke(double worldX, double worldY, double pressure = 1)
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke.AddPoint(worldX, worldY, pressure);
        _cliffCompanion?.AddPoint(worldX, worldY, pressure);
        PublishStrokeSnapshot();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void EndStroke()
    {
        var activeLayerId = _activeLayerId;
        if (_stroke is null || (!_editingStampForeground && activeLayerId is null))
        {
            return;
        }

        var stroke = _stroke;
        var companion = _cliffCompanion;
        _stroke = null;
        _cliffCompanion = null;
        _strokeSnapshot = null;
        if (stroke.IsEmpty)
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return;
        }

        if (CollectEditTimings) LastEditTimings.Clear();
        if (_editingStampForeground)
        {
            Dictionary<TileKey, byte[]?> Before = null!, After = null!;
            Timed("stroke-commit", () => (Before, After) = stroke.Commit());
            Timed("session-execute", () => Session.Execute(new StampForegroundStrokeCommand(
                Before, After, BrushSettings.Eraser ? "Erase stamp foreground" : "Paint stamp foreground")));
        }
        else if (ActiveTool == EditorTool.Mask)
        {
            Dictionary<TileKey, byte[]?> Before = null!, After = null!;
            Timed("stroke-commit", () => (Before, After) = stroke.CommitCoverage(
                missingTileValue: 255,
                subtract: MaskMode == MaskPaintMode.Hide));
            Timed("session-execute", () => Session.Execute(new MaskStrokeCommand(activeLayerId.GetValueOrDefault(), Before, After)));
        }
        else if (ActiveTool == EditorTool.Land && LandReliefMode)
        {
            Timed("session-execute", () => Session.Execute(CommitRelief(activeLayerId.GetValueOrDefault(), stroke)));
        }
        else if (ActiveTool == EditorTool.Land && LandCliffMaskMode && companion is not null)
        {
            Timed("session-execute", () => Session.Execute(CommitCliffMask(activeLayerId.GetValueOrDefault(), stroke, companion)));
        }
        else if (ActiveTool == EditorTool.Land && LandPaintMode)
        {
            Dictionary<TileKey, byte[]?> before = null!, after = null!;
            Timed("stroke-commit", () => (before, after) = stroke.Commit());
            Timed("session-execute", () => Session.Execute(new LandPaintCommand(activeLayerId.GetValueOrDefault(), before, after,
                stroke.Settings.Eraser ? "Erase land material" : "Paint land material")));
        }
        else if (ActiveTool == EditorTool.Land)
        {
            Dictionary<TileKey, byte[]?> Before = null!, After = null!;
            Timed("stroke-commit", () => (Before, After) = stroke.CommitCoverage(missingTileValue: 0, subtract: LandSubtract));
            Timed("session-execute", () => Session.Execute(new BrushStrokeCommand(
                activeLayerId.GetValueOrDefault(), Before, After,
                LandSubtract ? "Subtract land" : "Add land")));
        }
        else
        {
            Dictionary<TileKey, byte[]?> Before = null!, After = null!;
            Timed("stroke-commit", () => (Before, After) = stroke.Commit());
            Timed("session-execute", () => Session.Execute(new BrushStrokeCommand(
                activeLayerId.GetValueOrDefault(), Before, After,
                BrushSettings.Eraser ? "Erase stroke" : "Brush stroke")));
        }

        AfterEdit();
    }

    public void ApplyLandShape(LandShapeKind kind)
    {
        var centre = Session.Viewport.ScreenToWorld(
            Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);
        const double size = 512;
        var bounds = new WorldBounds(
            centre.X - (size / 2), centre.Y - (size / 2),
            centre.X + (size / 2), centre.Y + (size / 2));
        ApplyLandShape(kind, bounds);
    }

    public bool ApplyLandShape(
        LandShapeKind kind, WorldBounds bounds, IReadOnlyList<LandPoint>? points = null)
    {
        if (RasterTargetLayer(EditorTool.Land) is not LandLayer land)
        {
            StatusDetail = RasterToolRefusal(EditorTool.Land);
            return false;
        }

        UseAsActiveLayer(land);
        var centre = ((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
        IReadOnlyList<LandPoint> polygon = kind == LandShapeKind.Polygon
            ? points is { Count: >= 3 } ? points :
            [
                new LandPoint(bounds.Left, bounds.Bottom),
                new LandPoint(centre.Item1, bounds.Top),
                new LandPoint(bounds.Right, bounds.Bottom),
            ]
            : [];
        var (Before, After) = LandRasterizer.ApplyShape(land.GeometryTiles, new LandShape
        {
            Kind = kind,
            Bounds = bounds,
            Points = polygon,
            GridSize = 64,
        }, LandSubtract);
        if (Before.Count == 0)
        {
            return false;
        }

        Session.Execute(new BrushStrokeCommand(
            land.Id, Before, After,
            LandSubtract ? $"Subtract land {kind}" : $"Add land {kind}"));
        AfterEdit();
        return true;
    }

    public void CycleActiveLandStyle()
    {
        if (ActiveLayer is not LandLayer land)
        {
            return;
        }

        var coast = land.EdgeStyle.RippleCount == 0;
        var style = land.EdgeStyle with
        {
            FillColor = coast ? "#FF8CA36B" : "#FFC69B62",
            OutlineColor = coast ? "#FF405533" : "#FF65452B",
            RippleCount = coast ? 3 : 0,
            RippleSpacing = coast ? 12 : 10,
            OuterShadowOpacity = coast ? 0.3 : 0.15,
        };
        Session.Execute(new UpdateLandStyleCommand(land, style));
        AfterEdit();
    }

    public void MoveActiveLandArea(int deltaX, int deltaY)
    {
        if (ActiveLayer is not LandLayer land || land.Locked)
        {
            return;
        }

        var centre = Session.Viewport.ScreenToWorld(
            Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2);
        MoveActiveLandArea(new WorldBounds(centre.X - 256, centre.Y - 256, centre.X + 256, centre.Y + 256),
            deltaX, deltaY, snapToGrid: false);
    }

    public bool MoveActiveLandArea(WorldBounds region, int deltaX, int deltaY, bool snapToGrid)
    {
        if (ActiveLayer is not LandLayer land || land.Locked) return false;
        if (snapToGrid && ActiveSnapGrid() is { } grid)
        {
            var size = grid.CellSize / Math.Max(1, grid.Subdivisions);
            deltaX = (int)Math.Round(deltaX / size) * (int)Math.Max(1, Math.Round(size));
            deltaY = (int)Math.Round(deltaY / size) * (int)Math.Max(1, Math.Round(size));
        }
        var (before, after) = LandRasterizer.MoveRegion(land.GeometryTiles, region, deltaX, deltaY);
        if (before.Count == 0) return false;
        var geometry = new BrushStrokeCommand(land.Id, before, after, "Move selected land area");
        var commands = new List<IEditCommand> { geometry };
        if (land.ReliefTiles.TileCount > 0)
        {
            // The heights belong to the ground and travel with it.
            var (reliefBefore, reliefAfter) = LandRasterizer.MoveRegion(land.ReliefTiles, region, deltaX, deltaY);
            commands.Add(new LandReliefCommand(land.Id, reliefBefore, reliefAfter, "Move relief"));
        }
        if (land.PaintTiles.TileCount > 0)
        {
            var (paintBefore, paintAfter) = LandRasterizer.MoveRegion(land.PaintTiles, region, deltaX, deltaY);
            commands.Add(new LandPaintCommand(land.Id, paintBefore, paintAfter, "Move material painting"));
        }
        if (land.HasCliffMask)
        {
            // The cliff mask belongs to the coast it was painted on and travels with it.
            var (wallBefore, wallAfter) = LandRasterizer.MoveRegion(land.CliffWallTiles, region, deltaX, deltaY);
            var (beachBefore, beachAfter) = LandRasterizer.MoveRegion(land.CliffBeachTiles, region, deltaX, deltaY);
            commands.Add(new LandCliffMaskCommand(land.Id, wallBefore, wallAfter, beachBefore, beachAfter, "Move cliff mask"));
        }
        Session.Execute(commands.Count == 1 ? geometry : new CompositeCommand("Move land with its painting", commands));
        AfterEdit(); return true;
    }

    public bool ClearActiveLand()
    {
        if (ActiveLayer is not LandLayer { Locked: false } land || !land.GeometryTiles.Keys.Any()) return false;
        Session.Execute(UpdateLayerCommand.Replace(land,
            land with { GeometryTiles = TileMap.Empty(1), PaintTiles = TileMap.Empty(4), CliffWallTiles = TileMap.Empty(1), CliffBeachTiles = TileMap.Empty(1), ReliefTiles = TileMap.Empty(1) }, "Clear land"));
        AfterEdit(); return true;
    }

    public bool SetActiveLandStyle(Func<LandEdgeStyle, LandEdgeStyle> change)
    {
        if (ActiveLayer is not LandLayer { Locked: false } land) return false;
        var proposed = change(land.EdgeStyle);
        var updated = (proposed with
        {
            FillColor = AppearanceColor.Normalize(proposed.FillColor) ?? land.EdgeStyle.FillColor,
            OutlineColor = AppearanceColor.Normalize(proposed.OutlineColor) ?? land.EdgeStyle.OutlineColor,
            OutlineWidth = Math.Clamp(proposed.OutlineWidth, 0, 64),
            InnerShadowOpacity = Math.Clamp(proposed.InnerShadowOpacity, 0, 1),
            OuterShadowOpacity = Math.Clamp(proposed.OuterShadowOpacity, 0, 1),
            ShadowOffsetX = Math.Clamp(proposed.ShadowOffsetX, -512, 512),
            ShadowOffsetY = Math.Clamp(proposed.ShadowOffsetY, -512, 512),
            ShadowBlur = Math.Clamp(proposed.ShadowBlur, 0, 64),
            RippleColor = AppearanceColor.Normalize(proposed.RippleColor) ?? land.EdgeStyle.RippleColor,
            RippleWidth = Math.Clamp(proposed.RippleWidth, 0, 32),
            RippleSpacing = Math.Clamp(proposed.RippleSpacing, 1, 128),
            RippleCount = Math.Clamp(proposed.RippleCount, 0, 12),
            FillTextureId = MapFillTextures.Find(proposed.FillTextureId) is { Kind: MapFillTextureKind.Ground } ground
                ? ground.Id : null,
            FillTextureOpacity = Math.Clamp(proposed.FillTextureOpacity, 0, 1),
            FillTextureScale = Math.Clamp(proposed.FillTextureScale, MapFillTextures.MinimumScale, MapFillTextures.MaximumScale),
            Cliff = (proposed.Cliff with
            {
                Color = AppearanceColor.Normalize(proposed.Cliff.Color) ?? land.EdgeStyle.Cliff.Color,
                ShadeColor = AppearanceColor.Normalize(proposed.Cliff.ShadeColor) ?? land.EdgeStyle.Cliff.ShadeColor,
                InkColor = AppearanceColor.Normalize(proposed.Cliff.InkColor) ?? land.EdgeStyle.Cliff.InkColor,
                BaseShadowColor = AppearanceColor.Normalize(proposed.Cliff.BaseShadowColor) ?? land.EdgeStyle.Cliff.BaseShadowColor,
            }).Bounded(),
        });
        if (updated == land.EdgeStyle) return false;
        Session.Execute(new UpdateLandStyleCommand(land, updated)); AfterEdit(); return true;
    }

    /// <summary>
    /// The map background colour. The command existed and the layer is mandatory, but nothing in
    /// the editor could reach it: the layer property sheet hid itself for the base colour layer,
    /// so the background could only ever be chosen once, in the new-map wizard.
    /// </summary>
    public void SetBaseColor(string? colorText)
    {
        if (RequireColorInput(colorText) is not { } color)
        {
            return;
        }

        var layer = Session.Document.BaseColor;
        if (string.Equals(layer.Color, color, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Session.Execute(UpdateLayerCommand.SetBaseColor(layer, color));
        AfterEdit();
    }

    /// <summary>
    /// The water material of the open sea, laid over the background colour; null keeps the
    /// plain colour. One undo step. Only water materials that the renderer can draw are taken.
    /// </summary>
    public bool SetBaseMaterial(string? textureId)
    {
        if (textureId is not null && (MapFillTextures.Find(textureId) is not { Kind: MapFillTextureKind.Water }
            || Map.Rendering.MapTextureLibrary.Image(textureId) is null))
            return false;
        var layer = Session.Document.BaseColor;
        if (layer.TextureId == textureId) return false;
        Session.Execute(UpdateLayerCommand.Replace(layer, layer with { TextureId = textureId }, "Материал моря"));
        AfterEdit();
        return true;
    }

    public void CancelStroke()
    {
        if (_stroke is null)
        {
            return;
        }

        _stroke = null;
        _cliffCompanion = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>
    /// One canvas click. Outside an open group a grouped object stands for its whole group;
    /// inside the open group the click picks the child itself. Additive (Ctrl) toggles the unit.
    /// </summary>
    public void SelectAt(double worldX, double worldY, bool additive = false)
    {
        var hit = SceneGeometry.HitTest(Session.Document, worldX, worldY);
        SelectHit(hit, additive);
    }

    private void SelectHit(SceneObject? hit, bool additive)
    {
        if (hit is null)
        {
            if (!additive)
            {
                Session.ClearSelection();
            }

            AfterSelectionChange();
            return;
        }

        var unit = SelectionUnit(hit);
        if (additive)
        {
            if (unit.All(id => Session.SelectedObjectIds.Contains(id)))
            {
                Session.SetSelection(Session.SelectedObjectIds.Where(id => !unit.Contains(id)));
            }
            else
            {
                Session.SetSelection([.. Session.SelectedObjectIds, .. unit], hit.Id);
            }
        }
        else if (unit.All(id => Session.SelectedObjectIds.Contains(id))
            && (_openGroupId is null || hit.GroupId != _openGroupId))
        {
            // Clicking inside the current selection keeps it, so a drag can move the whole set.
            // Inside an open group the click means "this child", never "keep the group".
            Session.SetSelection(Session.SelectedObjectIds, hit.Id);
        }
        else
        {
            Session.SetSelection(unit, hit.Id);
        }

        AfterSelectionChange();
    }

    /// <summary>The ids one clicked object stands for: its whole group, unless that group is open.</summary>
    private IReadOnlyList<ObjectId> SelectionUnit(SceneObject hit) =>
        hit.GroupId is { } groupId && _openGroupId != groupId
            ? [.. SceneGeometry.GroupMembers(Session.Document, groupId).Select(o => o.Id)]
            : [hit.Id];

    public void SelectObject(ObjectId id, bool additive = false)
    {
        var layer = DocumentEditor.FindLayerOf(Session.Document, id);
        if (layer is null)
        {
            return;
        }

        if (additive)
        {
            Session.ToggleSelection(id);
        }
        else
        {
            Session.SetSelection([id], id);
        }

        _activeLayerId = layer.Id;
        AfterSelectionChange();
        Raise(nameof(ActiveLayer));
        Raise(nameof(ActiveLayerId));
        Raise(nameof(ActiveLayerSummary));
    }

    /// <summary>Selects one existing group whole, in one action.</summary>
    public void SelectGroup(Guid groupId)
    {
        var members = SceneGeometry.GroupMembers(Session.Document, groupId);
        if (members.Count == 0)
        {
            return;
        }

        Session.SetSelection(members.Select(o => o.Id));
        AfterSelectionChange();
    }

    public void DeselectObject()
    {
        Session.ClearSelection();
        AfterSelectionChange();
    }

    /// <summary>The ` / ~ shortcut: the whole previous set comes back, not one object.</summary>
    public void RestoreLastSelection()
    {
        if (Session.RestorePreviousSelection())
        {
            AfterSelectionChange();
        }
    }

    // ---------------------------------------------------------------- select all

    /// <summary>Live search text of the objects panel; Ctrl+A honours it.</summary>
    public string ObjectSearchText
    {
        get => _objectSearchText;
        set => SetField(ref _objectSearchText, value ?? "");
    }

    public ObjectTypeFilter ObjectTypeFilter
    {
        get => _objectTypeFilter;
        set
        {
            // The "Прочее" section remembers its own sub-filter; the other sections own theirs.
            if (_panelSection == PanelSection.Other && ObjectTreeRows.FilterOf(PanelSection.Other, value) == value)
                _otherFilter = value;
            SetField(ref _objectTypeFilter, value);
        }
    }

    /// <summary>The rows the objects panel shows for the current search, filter and open group.</summary>
    public IReadOnlyList<ObjectTreeRow> ObjectTree => ObjectTreeRows.Build(
        Session.Document, _objectSearchText, _objectTypeFilter, _openGroupId,
        [.. Session.SelectedObjectIds], [.. _waterSelection], _selectedLandId, [.. _roadSelection],
        _openAssetKey, AssetKindTitle);

    private string? _openAssetKey;

    /// <summary>The asset kind opened in the Assets section, or null for the list of kinds.</summary>
    public string? OpenAssetKey => _openAssetKey;

    public void OpenAssetKind(string key)
    {
        _openAssetKey = key;
        Raise(nameof(ObjectTree));
    }

    public void CloseAssetKind()
    {
        if (_openAssetKey is null) return;
        _openAssetKey = null;
        Raise(nameof(ObjectTree));
    }

    /// <summary>
    /// A selection made on the map opens the kind that holds it, so its row can be seen and
    /// scrolled to; a selection of several kinds shows the list of kinds.
    /// </summary>
    public void RevealSelectionInAssetKinds()
    {
        var keys = Session.SelectedObjects.Where(o => o.Kind == SceneObjectKind.Stamp)
            .Select(ObjectTreeRows.AssetKeyOf).Distinct().Take(2).ToArray();
        var next = keys.Length == 1 && Session.SelectedObjects.Count > 0 ? keys[0] : null;
        if (next is null && Session.SelectedObjects.Count == 0) return;
        if (next == _openAssetKey) return;
        _openAssetKey = next;
        Raise(nameof(ObjectTree));
    }

    /// <summary>The name a kind row shows: the variant label from the library, else the file name.</summary>
    private string? AssetKindTitle(string sha)
    {
        if (_assetCatalog is null) return null;
        foreach (var variant in _assetCatalog.Variants)
            if (string.Equals(variant.Sha256, sha, StringComparison.OrdinalIgnoreCase)) return variant.Label;
        return _assetCatalog.Find(sha)?.DisplayName;
    }

    /// <summary>
    /// Ctrl+A: selects the visible, unlocked objects that match BOTH the panel search and the
    /// type filter. Hidden and locked objects never enter; the tree stays their only door.
    /// </summary>
    public void SelectAllByFilter()
    {
        if (SelectAllTerrainByFilter())
        {
            AfterSelectionChange();
            return;
        }

        // Ctrl+A over objects addresses objects only: water, roads or land picked earlier would
        // otherwise ride along invisibly and be moved or deleted with them.
        ClearWaterSelection(); ClearRoadSelection(); ClearLandSelection();
        var availableIds = AvailableObjectIds();
        var matching = new List<ObjectId>();
        foreach (var row in ObjectTree)
        {
            // An asset-kind row stands for all its copies: Ctrl+A over the Assets list still
            // selects every placed asset the list holds.
            if (row.Kind == ObjectTreeRowKind.AssetKind && row.MemberIds is { } kindMembers)
            {
                // A closed group stays atomic here too: a copy that belongs to a group brings
                // the whole group along, or stays out when any member is unavailable (R04).
                foreach (var memberId in kindMembers)
                {
                    if (!availableIds.Contains(memberId) || matching.Contains(memberId)) continue;
                    if (DocumentEditor.FindObject(Session.Document, memberId) is { GroupId: { } memberGroup })
                    {
                        var members = SceneGeometry.GroupMembers(Session.Document, memberGroup);
                        if (members.Count > 0 && members.All(member => availableIds.Contains(member.Id)))
                            matching.AddRange(members.Select(member => member.Id).Where(id => !matching.Contains(id)));
                        continue;
                    }
                    matching.Add(memberId);
                }
                continue;
            }

            if (row.Kind == ObjectTreeRowKind.Group && row.GroupId is { } groupId)
            {
                var members = SceneGeometry.GroupMembers(Session.Document, groupId);
                if (members.Count > 0 && members.All(member => availableIds.Contains(member.Id)))
                {
                    matching.AddRange(members.Select(member => member.Id));
                }

                continue;
            }

            if (row.Kind is ObjectTreeRowKind.Object or ObjectTreeRowKind.GroupChild
                && row.Object is { } sceneObject
                && availableIds.Contains(sceneObject.Id))
            {
                matching.Add(sceneObject.Id);
            }
        }

        // The panel lists the topmost object first; the selection keeps document order, so the
        // primary object and every order-dependent command stay what they were before the panel
        // was turned around.
        var chosen = matching.ToHashSet();
        matching = [.. Session.Document.Layers.OfType<ObjectLayer>()
            .SelectMany(layer => layer.Objects).Select(item => item.Id).Where(chosen.Contains)];

        Session.SetSelection(matching);
        StatusDetail = matching.Count == 0
            ? "Под текущий поиск и фильтр не попал ни один видимый объект."
            : $"Выбрано: {EditorLabels.Objects(matching.Count)}.";
        AfterSelectionChange();
    }

    private HashSet<ObjectId> AvailableObjectIds() =>
    [
        .. Session.Document.Layers
            .OfType<ObjectLayer>()
            .Where(layer => layer.Visible && !layer.Locked)
            .SelectMany(layer => layer.Objects.Where(sceneObject =>
                layer.StateFor(sceneObject.Sublayer) is { Visible: true, Locked: false }))
            .Where(sceneObject => sceneObject.Visible && !sceneObject.Locked)
            .Select(sceneObject => sceneObject.Id),
    ];

    private bool IsObjectAvailable(SceneObject sceneObject)
    {
        if (!DocumentRevisionIndex.For(Session.Document).TryFind(sceneObject.Id, out var location)
            || Session.Document.Layers[location.LayerIndex] is not ObjectLayer { Visible: true, Locked: false } layer)
            return false;
        return location.Object is { Visible: true, Locked: false }
            && layer.StateFor(location.Object.Sublayer) is { Visible: true, Locked: false };
    }

    // ---------------------------------------------------------------- groups

    /// <summary>At least two existing, ungrouped objects are selected.</summary>
    public bool CanGroupSelection
    {
        get
        {
            var selected = Session.SelectedObjects;
            return selected.Count >= 2 && selected.All(o => o.GroupId is null);
        }
    }

    /// <summary>
    /// Creates a named group from the whole selection as one undoable command and selects the
    /// new group. A blank name receives the Stage 14 automatic group label. Refusals (too few
    /// objects or partial regrouping) change nothing and explain themselves in the status line.
    /// </summary>
    public bool GroupSelection(string? name)
    {
        var selected = Session.SelectedObjects;
        if (selected.Count < 2)
        {
            StatusDetail = "Для группы выберите минимум два объекта.";
            return false;
        }

        SetGroupCommand command;
        try
        {
            var groupName = string.IsNullOrWhiteSpace(name)
                ? EntityNameGenerator.Group(selected.Count)
                : name;
            command = SetGroupCommand.Group(
                Session.Document, [.. selected.Select(o => o.Id)], groupName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusDetail = ex.Message;
            return false;
        }

        Session.Execute(command);
        Session.SetSelection(selected.Select(o => o.Id));
        AfterEdit();
        StatusDetail = $"Создана группа «{SelectedGroupName}»: {EditorLabels.Objects(selected.Count)}.";
        return true;
    }

    /// <summary>Dissolves the one fully selected group. Anything else is refused untouched.</summary>
    public bool UngroupSelection()
    {
        if (SelectedGroupId is not { } groupId)
        {
            StatusDetail = "Разгруппировать можно одну полностью выбранную группу.";
            return false;
        }

        var members = SceneGeometry.GroupMembers(Session.Document, groupId);
        var memberIds = members.Select(o => o.Id).ToList();
        var name = members[0].GroupName;
        if (_openGroupId == groupId)
        {
            _openGroupId = null;
        }

        Session.Execute(SetGroupCommand.Ungroup(Session.Document, memberIds));
        Session.SetSelection(memberIds);
        AfterEdit();
        StatusDetail = $"Группа «{name ?? "Группа"}» разгруппирована.";
        return true;
    }

    /// <summary>Renames the currently selected group as one undoable command.</summary>
    public bool RenameSelectedGroup(string? name)
    {
        if (SelectedGroupId is not { } groupId)
        {
            StatusDetail = "Сначала выберите группу целиком.";
            return false;
        }

        var memberIds = SceneGeometry.GroupMembers(Session.Document, groupId)
            .Select(o => o.Id).ToList();
        try
        {
            Session.Execute(SetGroupCommand.Rename(Session.Document, groupId, name ?? ""));
        }
        catch (ArgumentException ex)
        {
            StatusDetail = ex.Message;
            return false;
        }

        Session.SetSelection(memberIds);
        AfterEdit();
        StatusDetail = $"Группа переименована: «{SelectedGroupName}».";
        return true;
    }

    /// <summary>Opens the selected group so its children become directly selectable.</summary>
    public void EnterSelectedGroup()
    {
        var selected = Session.SelectedObjects;
        var groupId = SelectedGroupId ?? (selected.Count > 0 ? selected[0].GroupId : null);
        if (groupId is null)
        {
            StatusDetail = "Сначала выберите группу.";
            return;
        }

        EnterGroup(groupId.Value);
    }

    public void EnterGroup(Guid groupId)
    {
        if (SceneGeometry.GroupMembers(Session.Document, groupId).Count == 0)
        {
            return;
        }

        _openGroupId = groupId;
        AfterSelectionChange();
    }

    /// <summary>The breadcrumb: closes the open group and selects it whole again.</summary>
    public void ExitGroup()
    {
        if (_openGroupId is not { } groupId)
        {
            return;
        }

        _openGroupId = null;
        SelectGroup(groupId);
    }

    /// <summary>
    /// Esc ladder: an open group closes first (selecting the group whole); a second press clears
    /// the selection. Returns false when there was nothing to do.
    /// </summary>
    /// <summary>
    /// One Escape undoes one layer of "I am in the middle of something", from the most transient
    /// to the least: a live gesture, an armed placement, an open group, the selection, and finally
    /// the drawing tool itself. The last step matters as much as the first - after Escape the
    /// pointer is always back to plain selecting, so nothing can leave the user stuck inside a
    /// tool with no obvious way out.
    /// </summary>
    public bool HandleEscape()
    {
        if (CancelEditableAreaWork()) { CancelEditableAreaGesture(); CanvasGestureCancelled?.Invoke(this, EventArgs.Empty); return true; }
        if (HasAppearanceTransferPreview)
        { CancelAppearanceTransfer(); StatusDetail = "Предпросмотр оформления отменён."; return true; }
        if (CancelRoadGesture())
        { CanvasGestureCancelled?.Invoke(this, EventArgs.Empty); StatusDetail = "Правка дороги отменена."; return true; }
        if (CancelRiverGesture())
        { CanvasGestureCancelled?.Invoke(this, EventArgs.Empty); StatusDetail = "Правка реки отменена."; return true; }
        if (CancelEditableAreaGesture())
        { CanvasGestureCancelled?.Invoke(this, EventArgs.Empty); PendingPlacement = null; StatusDetail = "Правка области отменена."; return true; }
        if (CancelEditablePathGesture())
        { CanvasGestureCancelled?.Invoke(this, EventArgs.Empty); StatusDetail = "Правка пути отменена."; return true; }
        if (_pendingShapeBoolean is not null)
        {
            CancelShapeBooleanPreview();
            return true;
        }

        // A drag, marquee or node grab is as much "an action in progress" as a half-drawn shape,
        // and the canvas host has to forget its own matching state in the same breath.
        if (_vectorElementDrag is not null || _vectorPoints is not null
            || _penPoints is not null || _landShapeStart is not null
            || _textPlacementStart is not null || _stroke is not null
            || DragPreview is not null || _marqueeStart is not null)
        {
            CancelVectorElementDrag();
            CancelVectorDraw();
            CancelPenPath();
            CancelLandShapeGesture();
            CancelTextPlacement();
            CancelStroke();
            CancelDrag();
            CancelMarquee();
            CancelAppearanceGesture();
            CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
            StatusDetail = "Действие отменено.";
            return true;
        }

        if (CancelPlacement())
        {
            CanvasGestureCancelled?.Invoke(this, EventArgs.Empty);
            StatusDetail = "Размещение отменено.";
            return true;
        }

        if (_openGroupId is not null)
        {
            ExitGroup();
            return true;
        }

        if (SelectionCount > 0)
        {
            DeselectObject();
            return true;
        }

        if (ActiveTool != EditorTool.Select)
        {
            ActiveTool = EditorTool.Select;
            StatusDetail = "Обычное управление: выбор и перемещение объектов.";
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- marquee

    /// <summary>Live rubber-band rectangle for the renderer, or null when no marquee drag runs.</summary>
    public WorldBounds? MarqueeWorld => _marqueeWorld;

    /// <summary>Starts a marquee drag on empty canvas. Additive keeps the current selection.</summary>
    public bool BeginMarquee(double worldX, double worldY, bool additive)
    {
        _marqueeStart = (worldX, worldY, additive);
        _marqueeWorld = null;
        return true;
    }

    public void UpdateMarquee(double worldX, double worldY)
    {
        if (_marqueeStart is not { } start)
        {
            return;
        }

        _marqueeWorld = MultiSelectionGeometry.MarqueeBounds(start.X, start.Y, worldX, worldY);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>
    /// Completes the marquee: visible unlocked objects intersecting the rectangle, each grouped
    /// object standing for its whole group, replace or extend the selection.
    /// </summary>
    public void EndMarquee()
    {
        var start = _marqueeStart;
        var rect = _marqueeWorld;
        _marqueeStart = null;
        _marqueeWorld = null;
        if (start is not { } begun || rect is not { } marquee)
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return;
        }

        // A press on land that never moved is a click on that land, not an empty rubber band.
        var threshold = 3 / Math.Max(0.001, Session.Viewport.Scale);
        if (_selectedLandId is not null && marquee.Right - marquee.Left < threshold && marquee.Bottom - marquee.Top < threshold)
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return;
        }
        ClearLandSelection();

        var found = new List<ObjectId>();
        foreach (var hit in MultiSelectionGeometry.ObjectsInMarquee(Session.Document, marquee))
        {
            found.AddRange(SelectionUnit(hit));
        }

        if (begun.Additive)
        {
            Session.AddToSelection(found);
        }
        else
        {
            Session.SetSelection(found);
        }

        // A rubber band over the map takes the water systems and roads it crosses too, so a mixed
        // selection of stamps, rivers and roads is one gesture and one Delete.
        AddWaterSelection(WatersIn(marquee), begun.Additive);
        AddRoadSelection(RoadsIn(marquee), begun.Additive);
        AfterSelectionChange();
    }

    public void CancelMarquee()
    {
        if (_marqueeStart is null && _marqueeWorld is null)
        {
            return;
        }

        _marqueeStart = null;
        _marqueeWorld = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Shared frame of the current multi-selection (live drag included), or null.</summary>
    public WorldBounds? SelectionFrame
    {
        get
        {
            if (DragPreview is { } preview && preview.Proposed.Count > 1)
            {
                return MultiSelectionGeometry.CombinedBounds(preview.Proposed);
            }

            return SelectionCount > 1
                ? MultiSelectionGeometry.CombinedBounds(Session.SelectedObjects)
                : null;
        }
    }

    /// <summary>One notification bundle for every way the selection can change.</summary>
    private void AfterSelectionChange()
    {
        if (Session.SelectedObjectIds.Count > 0) ClearLandSelection();
        if (_selectedVectorNodeId is { } nodeId && SelectedVectorNodes.All(node => node.Id != nodeId))
            _selectedVectorNodeId = null;
        if (_selectedPortalId is { } portalId && SelectedVectorPortals.All(portal => portal.Id != portalId))
            _selectedPortalId = null;
        if (_pendingShapeBoolean is { } pending
            && !Session.SelectedObjectIds.SequenceEqual(new[] { pending.First, pending.Second }))
        {
            _pendingShapeBoolean = null; _shapeBooleanPreviewObject = null;
            Raise(nameof(PlacementPreviewObjects));
        }
        RefreshInspector();
        Raise(nameof(SelectionStatus));
        Raise(nameof(SelectionKind));
        Raise(nameof(SelectionCount));
        Raise(nameof(ShowSingleInspector));
        Raise(nameof(SelectedGroupId));
        Raise(nameof(SelectedGroupName));
        Raise(nameof(OpenGroupId));
        Raise(nameof(SelectionVisibilitySummary));
        Raise(nameof(SelectionLockSummary));
        InvalidateScene(EditorChangeReason.Selection | EditorChangeReason.CanvasInvalidation);
    }

    public void NudgeSelectedObject(double deltaX, double deltaY)
    {
        if (EditableSelectionList(allowAreaMembers: true) is not { } before) return;
        var after = QuantizeTransformedGeometries(before, MultiSelectionGeometry.Translate(before, deltaX, deltaY));
        // Other tools expose moved node coordinates; integer normalization can also alter an
        // older stamp's size/angle. Those cases still need the complete context-panel refresh.
        var positionOnly = before.Zip(after).All(pair => pair.First.Kind == SceneObjectKind.Stamp
            && pair.First.Width == pair.Second.Width && pair.First.Height == pair.Second.Height
            && pair.First.RotationDegrees == pair.Second.RotationDegrees);
        ExecuteWholeSelection(before, after, "Nudge selection", geometryOnly: positionOnly, manualAreaEdit: true);
    }

    public void ResizeSelectedObject(int direction, bool largerStep = false)
    {
        if (EditableSelectionList(allowAreaMembers: true) is not { } before || direction == 0) return;
        var factor = MultiSelectionGeometry.ClampScaleFactor(
            before, 1 + Math.Sign(direction) * (largerStep ? 0.1 : 0.01));
        var frame = MultiSelectionGeometry.CombinedBounds(before)!.Value;
        ExecuteWholeSelection(before, QuantizeTransformedGeometries(before, MultiSelectionGeometry.ScaleAroundCentre(
            before, factor, (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2)),
            "Resize selection from shortcut", manualAreaEdit: true);
    }

    public void RotateSelectedObject(int direction, bool largerStep = false, bool fineStep = false)
    {
        if (EditableSelectionList(allowAreaMembers: true) is not { } before || direction == 0) return;
        // Geometry is intentionally integer-only. Alt therefore keeps the normal one-degree
        // step instead of creating hidden quarter-degree values.
        var step = largerStep ? 15 : 1;
        var frame = MultiSelectionGeometry.CombinedBounds(before)!.Value;
        ExecuteWholeSelection(before, QuantizeTransformedGeometries(before, MultiSelectionGeometry.RotateAroundCentre(
            before, Math.Sign(direction) * step,
            (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2)),
            "Rotate selection from shortcut", manualAreaEdit: true);
    }

    public void AdjustSelectedSublayer(int direction)
    {
        // The one level shortcut serves the whole map: objects, then water and roads, then land.
        if (Session.SelectedObjects.Count == 0 && SurfaceSelectionCount > 0) { ShiftSelectedSurfaceLevel(direction); return; }
        if (Session.SelectedObjects.Count == 0 && SurfaceSelectionCount == 0 && SelectedLand is not null) { ShiftLandLevel(direction); return; }
        if (EditableSelectionList() is not { } before || direction == 0) return;
        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o) with
            {
                Sublayer = Math.Clamp(o.Sublayer + Math.Sign(direction), -10, 10),
            })],
            "Change selection sublayer");
    }

    // ------------------------------------- common properties of the whole selection

    /// <summary>The lowest and highest level an object may hold. One integer band, no fractions.</summary>
    public const int MinimumSublayer = -10;

    public const int MaximumSublayer = 10;

    /// <summary>An object level as the user reads it: a signed integer, never a bare "3".</summary>
    public static string FormatSublayer(int value) =>
        value > 0 ? "+" + value.ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The level every selected object shares, or null when they disagree or none is selected.</summary>
    public int? SelectionSublayer
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return null;
            }

            var first = selected[0].Sublayer;
            return selected.All(item => item.Sublayer == first) ? first : null;
        }
    }

    /// <summary>True when the selection holds more than one level, so no single number is honest.</summary>
    public bool SelectionSublayerIsMixed =>
        Session.SelectedObjects.Count > 1 && SelectionSublayer is null;

    /// <summary>Signed level, the word "разные" for a mixed selection, or a dash for none.</summary>
    public string SelectionSublayerReadout => Session.SelectedObjects.Count == 0
        ? "—"
        : SelectionSublayer is { } value ? FormatSublayer(value) : "разные";

    /// <summary>
    /// Gives the whole selection one level. Used when the objects already agree, so the slider
    /// shows a real shared number and dragging it means exactly what it says.
    /// </summary>
    public bool SetSelectionSublayer(int value, bool commit = true)
    {
        var bounded = Math.Clamp(value, MinimumSublayer, MaximumSublayer);
        return EditSelection("Change selection level", commit,
            before => before.Sublayer == bounded ? before : CopyObject(before) with { Sublayer = bounded });
    }

    /// <summary>
    /// Moves every selected object by the same number of levels, keeping the differences between
    /// them. This is what a mixed selection needs: nothing is flattened onto one value, and the
    /// whole shift is one undoable command.
    /// </summary>
    public bool ShiftSelectionSublayer(int delta, bool commit = true)
    {
        if (delta == 0)
        {
            return false;
        }

        // A member already at the edge would silently swallow part of the shift and change the
        // relative order, so the shift is trimmed to what every member can actually travel.
        var selected = Session.SelectedObjects;
        if (selected.Count == 0)
        {
            return false;
        }

        var room = delta > 0
            ? selected.Min(item => MaximumSublayer - item.Sublayer)
            : selected.Min(item => item.Sublayer - MinimumSublayer);
        var applied = Math.Sign(delta) * Math.Min(Math.Abs(delta), Math.Max(0, room));
        if (applied == 0)
        {
            StatusDetail = delta > 0
                ? "Выделение уже на максимальном уровне."
                : "Выделение уже на минимальном уровне.";
            return false;
        }

        return EditSelection("Shift selection level", commit, before => CopyObject(before) with
        {
            Sublayer = Math.Clamp(before.Sublayer + applied, MinimumSublayer, MaximumSublayer),
        });
    }

    /// <summary>Opacity shared by the whole selection, or null when the members disagree.</summary>
    public double? SelectionOpacityPercent
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return null;
            }

            var first = selected[0].ObjectOpacity;
            return selected.All(item => Math.Abs(item.ObjectOpacity - first) < 0.0001)
                ? Math.Round(first * 100, 1)
                : null;
        }
    }

    public bool SetSelectionOpacityPercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Непрозрачность задаётся числом 0..100.";
            return false;
        }

        var opacity = Math.Clamp(percent, 0, 100) / 100.0;
        return EditSelection("Change selection opacity", commit,
            before => before.ObjectOpacity == opacity
                ? before
                : CopyObject(before) with { ObjectOpacity = opacity });
    }

    /// <summary>Rotation shared by the whole selection, or null when the members disagree.</summary>
    public double? SelectionRotationDegrees
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return null;
            }

            var first = NormalizeUnsignedAngle(WholeGeometryValue(selected[0].RotationDegrees));
            return selected.All(item =>
                NormalizeUnsignedAngle(WholeGeometryValue(item.RotationDegrees)) == first)
                ? first
                : null;
        }
    }

    /// <summary>Turns every selected object to the same angle around its own centre.</summary>
    public bool SetSelectionRotationDegrees(double degrees, bool commit = true)
    {
        if (!double.IsFinite(degrees))
        {
            InspectorError = "Поворот задаётся числом 0..359 градусов.";
            return false;
        }

        var normalized = NormalizeUnsignedAngle(WholeGeometryValue(degrees));
        return EditSelection("Change selection rotation", commit,
            before => Math.Abs(before.RotationDegrees - normalized) < 0.0001
                ? before
                : WithGeometry(before, before.X, before.Y, before.Width, before.Height, normalized));
    }

    /// <summary>
    /// True when every selected object is a placed asset, which is the only case where a single
    /// "scale %" has a shared meaning: each stamp carries its own 100 % reference size.
    /// </summary>
    public bool SelectionIsAllStamps =>
        Session.SelectedObjects.Count > 0 && Session.SelectedObjects.All(item => item.Stamp is not null);

    /// <summary>Scale shared by an all-stamp selection, or null when the members disagree.</summary>
    public double? SelectionScalePercent
    {
        get
        {
            if (!SelectionIsAllStamps)
            {
                return null;
            }

            var values = Session.SelectedObjects
                .Select(item =>
                {
                    var reference = item.Stamp!.ReferenceWidth ?? item.Width;
                    return reference <= 0 ? (double?)null : WholeGeometryValue(item.Width / reference * 100);
                })
                .ToList();
            return values.All(value => value is not null)
                && values.Skip(1).All(value => value == values[0])
                ? values[0]
                : null;
        }
    }

    public bool SetSelectionScalePercent(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent))
        {
            InspectorError = "Масштаб задаётся числом в процентах.";
            return false;
        }

        var factor = Math.Clamp(WholeGeometryValue(percent), 10, 400) / 100.0;
        return EditSelection("Change selection scale", commit, before =>
        {
            if (before.Stamp is not { } stamp)
            {
                return before;
            }

            var referenceWidth = stamp.ReferenceWidth ?? before.Width;
            var referenceHeight = stamp.ReferenceHeight ?? before.Height;
            var width = Math.Max(0.5, referenceWidth * factor);
            var height = Math.Max(0.5, referenceHeight * factor);
            var adopted = stamp.ReferenceWidth is null
                ? stamp with { ReferenceWidth = referenceWidth, ReferenceHeight = referenceHeight }
                : stamp;
            var moved = WithGeometry(
                before,
                before.X + ((before.Width - width) / 2),
                before.Y + ((before.Height - height) / 2),
                width, height, before.RotationDegrees);
            return moved with { Stamp = adopted };
        });
    }

    private IReadOnlyList<SceneObject>? _selectionGestureBefore;

    /// <summary>
    /// One property gesture over the WHOLE selection: previews while the pointer is down and lands
    /// exactly one history entry when it is released. The "before" list belongs to the gesture, so
    /// a preview that started on this selection can never commit onto a different one.
    /// </summary>
    private bool EditSelection(string name, bool commit, Func<SceneObject, SceneObject> change)
    {
        var current = EditableSelectionList();
        if (_selectionGestureBefore is { } pending
            && (current is null
                || !pending.Select(item => item.Id).SequenceEqual(current.Select(item => item.Id))))
        {
            _selectionGestureBefore = null;
            DragPreview = null;
        }

        var before = _selectionGestureBefore ?? current;
        if (before is null || before.Count == 0)
        {
            return false;
        }

        var after = before.Select(change).ToList();
        if (!commit)
        {
            _selectionGestureBefore = before;
            DragPreview = new DragPreview(before, after, DragMode.Move, null, 0, 0, null);
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        _selectionGestureBefore = null;
        DragPreview = null;
        if (before.SequenceEqual(after))
        {
            SceneChanged?.Invoke(this, EventArgs.Empty);
            return false; // no-op: no history entry
        }

        Session.Execute(new ReplaceObjectsCommand(before, after, name));
        AfterEdit();
        return true;
    }

    // ------------------------------------------------ owner layer and draw order

    /// <summary>
    /// The layer that owns the selection, by its user-visible name. Never a GUID or an internal
    /// key: the user has to recognise the layer they are about to move an object out of.
    /// </summary>
    public string SelectedOwnerLayerName
    {
        get
        {
            var owners = SelectionOwnerLayerIds();
            return owners.Count switch
            {
                0 => "—",
                1 => Session.Document.FindLayer(owners[0])?.Name ?? "—",
                _ => "Разные слои",
            };
        }
    }

    /// <summary>
    /// Every object layer of the document, in document order, each with the name the layers panel
    /// shows. Hidden and locked layers stay in the list and carry the reason they cannot be
    /// chosen, so the selector never hides a layer the user is looking for.
    /// </summary>
    public IReadOnlyList<ObjectLayerTarget> ObjectLayerTargets
    {
        get
        {
            // Water is a selection too: with no ordinary object selected, the list speaks for
            // the selected rivers and lakes, and choosing a layer moves them.
            var owners = Session.SelectedObjects.Count > 0 ? SelectionOwnerLayerIds()
                : [.. WaterOwnerLayerIds().Concat(RoadOwnerLayerIds()).Distinct()];
            var current = owners.Count == 1 ? owners[0] : (LayerId?)null;
            return
            [
                .. Session.Document.Layers.OfType<ObjectLayer>()
                    .Select(layer => new ObjectLayerTarget(
                        layer.Id,
                        layer.Name,
                        layer.Id == current,
                        layer.Visible && !layer.Locked,
                        !layer.Visible ? "скрыт" : layer.Locked ? "заблокирован" : null)),
            ];
        }
    }

    /// <summary>Object layers that would actually accept the selection right now.</summary>
    public IReadOnlyList<ObjectLayerTarget> AvailableObjectLayerTargets =>
        [.. ObjectLayerTargets.Where(target => target.Available)];

    private IReadOnlyList<LayerId> SelectionOwnerLayerIds() =>
    [
        .. Session.SelectedObjects
            .Select(sceneObject => DocumentEditor.FindLayerOf(Session.Document, sceneObject.Id))
            .OfType<MapLayer>()
            .Select(layer => layer.Id)
            .Distinct(),
    ];

    /// <summary>The object layers holding the selected water systems, without duplicates.</summary>
    private IReadOnlyList<LayerId> WaterOwnerLayerIds() =>
    [
        .. SelectedWaters
            .Select(river => RiverLayerOf(river.Id))
            .OfType<ObjectLayer>()
            .Select(layer => layer.Id)
            .Distinct(),
    ];

    /// <summary>
    /// The selection's own level inside its layer, as the user sees it: one number, or a plain
    /// word when the selected objects disagree.
    /// </summary>
    public string SelectedSublayerSummary
    {
        get
        {
            var selected = Session.SelectedObjects;
            if (selected.Count == 0)
            {
                return "—";
            }

            var levels = selected.Select(item => item.Sublayer).Distinct().ToList();
            return levels.Count == 1 ? levels[0].ToString() : "разные";
        }
    }

    /// <summary>
    /// Moves the selection to the next compatible object layer, wrapping around. This is the one
    /// click behind the "Слой" control: the user reads the current layer and steps to another one
    /// without hunting for a separate dialog.
    /// </summary>
    public bool CycleSelectionLayer()
    {
        var targets = AvailableObjectLayerTargets;
        if (targets.Count == 0)
        {
            StatusDetail = "Нет другого видимого разблокированного слоя объектов.";
            return false;
        }

        if (targets.Count == 1 && targets[0].IsCurrent)
        {
            StatusDetail = "Это единственный доступный слой объектов.";
            return false;
        }

        var currentIndex = targets.ToList().FindIndex(target => target.IsCurrent);
        var next = targets[(currentIndex + 1 + targets.Count) % targets.Count];
        return MoveSelectionToLayer(next.Id);
    }

    /// <summary>
    /// Moves the whole selection to another object layer as one undoable step. Whole groups travel
    /// together, and a selection that is not fully editable refuses outright rather than moving
    /// part of itself. Each object keeps its own sublayer, so the level the user set survives.
    /// </summary>
    public bool MoveSelectionToLayer(LayerId targetId)
    {
        // The one "Слой" control serves water as well: with only rivers or lakes selected, it
        // moves them, as one undoable step, exactly like the river panel's own picker.
        if (Session.SelectedObjects.Count == 0 && SurfaceSelectionCount > 0)
        {
            var moved = false;
            if (WaterSelectionCount > 0) moved |= MoveSelectedWaterToLayer(targetId);
            if (RoadSelectionCount > 0) moved |= MoveSelectedRoadsToLayer(targetId);
            return moved;
        }

        if (EditableSelectionList(allowAreaMembers: true) is null)
        {
            return false;
        }

        if (Session.Document.FindLayer(targetId) is not ObjectLayer { Visible: true, Locked: false } target)
        {
            StatusDetail = "Слой недоступен: он скрыт, заблокирован или не хранит объекты.";
            return false;
        }

        var ids = SelectionWithWholeGroups();
        var bySource = ids
            .Select(id => (Id: id, Layer: DocumentEditor.FindLayerOf(Session.Document, id)))
            .Where(pair => pair.Layer is not null && pair.Layer.Id != targetId)
            .GroupBy(pair => pair.Layer!.Id)
            .ToList();
        if (bySource.Count == 0)
        {
            StatusDetail = $"Объекты уже находятся на слое «{target.Name}».";
            return false;
        }

        if (ids.Select(id => DocumentEditor.FindObject(Session.Document, id)!).Any(item => !IsObjectAvailable(item)
            || target.StateFor(item.Sublayer) is not { Visible: true, Locked: false }))
        {
            StatusDetail = "Объект, исходный слой или целевой подуровень скрыт либо заблокирован — перенос отклонён целиком.";
            return false;
        }

        var steps = bySource
            .Select(group => (IEditCommand)new MoveObjectsToLayerCommand(
                Session.Document, group.Key, targetId, [.. group.Select(pair => pair.Id)]))
            .ToList();
        if (!ExecuteManualAreaSelection(steps.Count == 1
            ? steps[0]
            : new CompositeCommand("Move selection to another layer", steps), ids)) return false;
        _activeLayerId = targetId;
        Session.SetSelection(ids);
        AfterSelectionChange();
        StatusDetail = $"Перенесено на слой «{target.Name}»: {EditorLabels.Objects(ids.Count)}.";
        return true;
    }

    /// <summary>
    /// Moves the selection above or below its neighbours inside its own layer. This is the fine
    /// order that breaks ties inside a sublayer band; <see cref="AdjustSelectedSublayer"/> is the
    /// coarse band itself. <paramref name="toExtreme"/> sends the selection all the way.
    /// </summary>
    public bool ReorderSelection(int direction, bool toExtreme = false)
    {
        if (Session.SelectedObjects.Count == 0 && SurfaceSelectionCount > 0)
        {
            // Water alone keeps its own command; with roads in the layer the one surface order decides.
            var roadsInvolved = RoadSelectionCount > 0
                || SelectedWaters.Any(river => RiverLayerOf(river.Id) is { Roads.Length: > 0 });
            return roadsInvolved ? ReorderSelectedSurfaces(direction, toExtreme) : ReorderSelectedWater(direction, toExtreme);
        }

        if (direction == 0 || EditableSelectionList() is not { } selection)
        {
            return false;
        }

        var owners = SelectionOwnerLayerIds();
        if (owners.Count != 1 || Session.Document.FindLayer(owners[0]) is not ObjectLayer layer)
        {
            StatusDetail = "Порядок меняется в пределах одного слоя: выделение лежит на разных слоях.";
            return false;
        }

        var order = layer.Objects.ToList();
        var selected = selection.Select(item => item.Id).ToHashSet();
        var up = direction > 0;

        if (toExtreme)
        {
            var moved = order.Where(item => selected.Contains(item.Id)).ToList();
            var rest = order.Where(item => !selected.Contains(item.Id)).ToList();
            order = up ? [.. rest, .. moved] : [.. moved, .. rest];
        }
        else if (up)
        {
            // Walk from the front so a run of selected objects keeps its own internal order.
            for (var index = order.Count - 2; index >= 0; index--)
            {
                if (selected.Contains(order[index].Id) && !selected.Contains(order[index + 1].Id)
                    && order[index].Sublayer == order[index + 1].Sublayer)
                {
                    (order[index], order[index + 1]) = (order[index + 1], order[index]);
                }
            }
        }
        else
        {
            for (var index = 1; index < order.Count; index++)
            {
                if (selected.Contains(order[index].Id) && !selected.Contains(order[index - 1].Id)
                    && order[index].Sublayer == order[index - 1].Sublayer)
                {
                    (order[index], order[index - 1]) = (order[index - 1], order[index]);
                }
            }
        }

        if (order.Select(item => item.Id).SequenceEqual(layer.Objects.Select(item => item.Id)))
        {
            StatusDetail = up
                ? "Выделение уже выше всех соседей своего подслоя."
                : "Выделение уже ниже всех соседей своего подслоя.";
            return false;
        }

        Session.Execute(new ReorderObjectsCommand(
            layer.Id, layer.Objects, order, up ? "Move selection up" : "Move selection down"));
        AfterEdit();
        return true;
    }

    public void AdjustSelectedOpacity(int direction, bool largerStep = false)
    {
        if (EditableSelectionList() is not { } before || direction == 0) return;
        var step = largerStep ? 0.1 : 0.05;
        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o) with
            {
                ObjectOpacity = Math.Clamp(o.ObjectOpacity + Math.Sign(direction) * step, 0, 1),
            })],
            "Change selection opacity");
    }

    /// <summary>One whole-selection edit = one history entry; an unchanged result = none.</summary>
    private void ExecuteWholeSelection(
        IReadOnlyList<SceneObject> before, IReadOnlyList<SceneObject> after, string name, bool geometryOnly = false,
        bool manualAreaEdit = false)
    {
        if (before.SequenceEqual(after))
        {
            return;
        }

        if (manualAreaEdit)
        {
            ExecuteManualAreaSelection(new ReplaceObjectsCommand(before, after, name),
                before.Select(o => o.Id).ToArray(), geometryOnly);
            return;
        }
        Session.Execute(new ReplaceObjectsCommand(before, after, name));
        AfterEdit(geometryOnly);
    }

    private bool ExecuteManualAreaSelection(IEditCommand command, IReadOnlyCollection<ObjectId> ids, bool geometryOnly = false)
    {
        var hasAreaMembers = ids.Any(id => PlacementAreaOwnership.Owner(Session.Document, id) is not null);
        var before = Session.Document;
        Session.Execute(hasAreaMembers ? new ManualAreaSelectionCommand(command, ids) : command);
        if (ReferenceEquals(before, Session.Document))
        {
            if (Session.LastResourceError is { } error) StatusDetail = error;
            return false;
        }
        AfterEdit(geometryOnly && !hasAreaMembers);
        if (hasAreaMembers) StatusDetail = "Выбранные объекты изменены и отделены от области. Отмена вернёт объекты и связь с областью.";
        return true;
    }

    /// <summary>
    /// The whole selection, editable or nothing: a hidden or locked member refuses the entire
    /// geometric change so no partial result can ever land (Stage 10C contract).
    /// </summary>
    private IReadOnlyList<SceneObject>? EditableSelectionList(bool allowAreaMembers = false)
    {
        var selected = Session.SelectedObjects;
        if (selected.Count == 0)
        {
            return null;
        }
        if (selected.Any(o => PlacementPathOwnership.Owner(Session.Document, o.Id) is not null
            || !allowAreaMembers && PlacementAreaOwnership.Owner(Session.Document, o.Id) is not null))
        { StatusDetail = PlacementOwnership.DetachFirst; return null; }

        if (selected.Any(sceneObject => !IsObjectAvailable(sceneObject)))
        {
            StatusDetail = "В выделении есть скрытые или заблокированные объекты либо слои — изменение отклонено целиком.";
            return null;
        }

        return selected;
    }

    public void AdjustBrushSize(int direction, bool largerStep = false)
    {
        if (direction == 0) return;
        BrushSettings = BrushSettings with
        {
            Radius = Math.Clamp(BrushSettings.Radius + Math.Sign(direction) * (largerStep ? 10 : 1), 1, 256),
        };
        StatusDetail = $"Размер кисти: {BrushSettings.Radius:0}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AdjustBrushOpacity(int direction, bool largerStep = false)
    {
        if (direction == 0) return;
        BrushSettings = BrushSettings with
        {
            Opacity = Math.Clamp(BrushSettings.Opacity + Math.Sign(direction) * (largerStep ? 0.1 : 0.05), 0.05, 1),
        };
        StatusDetail = $"Непрозрачность кисти: {BrushSettings.Opacity:P0}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AdjustBrushSoftness(int direction, bool largerStep = false)
    {
        if (direction == 0) return;
        BrushSettings = BrushSettings with
        {
            Softness = Math.Clamp(BrushSettings.Softness + Math.Sign(direction) * (largerStep ? 0.1 : 0.05), 0, 1),
        };
        StatusDetail = $"Мягкость кисти: {BrushSettings.Softness:P0}.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleMaskPaintMode()
    {
        MaskMode = MaskMode == MaskPaintMode.Hide ? MaskPaintMode.Reveal : MaskPaintMode.Hide;
        StatusDetail = MaskMode == MaskPaintMode.Hide ? "Маска: скрывать." : "Маска: возвращать.";
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AdjustSelectedTextFontSize(int direction, bool largerStep = false)
    {
        if (EditableSelection() is not { Text: { } text } before || direction == 0) return;
        var value = Math.Clamp(text.FontSize + Math.Sign(direction) * (largerStep ? 5 : 1), 1, 512);
        ReplaceSelected(before, before with { Text = text with { FontSize = value } }, "Change text size");
    }

    public void AdjustSelectedTextCurve(int direction, bool largerStep = false)
    {
        if (EditableSelection() is not { Text: { } text } before || direction == 0) return;
        var value = Math.Clamp(text.Curve + Math.Sign(direction) * (largerStep ? 10 : 1), -100, 100);
        ReplaceSelected(before, before with { Text = text with { Curve = value } }, "Change text curve");
    }

    /// <summary>
    /// Visibility over the whole selection. A mixed state resolves towards showing everything
    /// first - hiding objects the user cannot see being toggled would read as loss.
    /// </summary>
    public void ToggleSelectedObjectVisibility()
        => SetSelectedObjectVisibility(!Session.SelectedObjects.All(o => o.Visible));

    /// <summary>Applies the chosen value to the whole selection as one undoable edit.</summary>
    public void SetSelectedObjectVisibility(bool visible)
    {
        var before = Session.SelectedObjects;
        if (before.Count == 0 || before.All(o => o.Visible == visible))
        {
            return;
        }

        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o, visible: visible))], "Selection visibility");
    }

    /// <summary>Lock over the whole selection: any unlocked member means "lock all" first.</summary>
    public void ToggleSelectedObjectLock()
        => SetSelectedObjectLock(!Session.SelectedObjects.All(o => o.Locked));

    /// <summary>Applies a named lock state; mixed values never force an intermediate state.</summary>
    public void SetSelectedObjectLock(bool locked)
    {
        var before = Session.SelectedObjects;
        if (before.Count == 0 || before.All(o => o.Locked == locked))
        {
            return;
        }

        ExecuteWholeSelection(before,
            [.. before.Select(o => CopyObject(o, locked: locked))], "Selection lock");
    }

    /// <summary>Deletes the whole selection, across layers, as one history entry.</summary>
    public void DeleteSelectedObject()
    {
        if (EditableSelectionList(allowAreaMembers: true) is null) return;
        var ids = Session.SelectedObjectIds;
        if (ids.Count == 0)
        {
            return;
        }

        if (!ExecuteManualAreaSelection(new DeleteObjectsCommand(Session.Document, [.. ids]), ids)) return;
        Session.ClearSelection();
    }

    /// <summary>Selected, editable object - locked and hidden objects cannot be edited.</summary>
    private SceneObject? EditableSelection()
    {
        var selected = Session.SelectedObject;
        if (selected is not null && PlacementOwnership.Owner(Session.Document, selected.Id) is not null) return null;
        return selected is { Locked: false, Visible: true } ? selected : null;
    }

    /// <summary>World-space reach of a handle grab and the rotation handle's stem, per zoom.</summary>
    public static double HandleHitRadius(double viewScale) => 8.0 / Math.Max(viewScale, 0.01);

    public static double RotationHandleOffset(double viewScale) => 28.0 / Math.Max(viewScale, 0.01);

    /// <summary>
    /// The gizmo handle under the cursor, for pointer feedback. One object hit-tests its own
    /// rotated gizmo; a multi-selection hit-tests the shared axis-aligned frame. Null when the
    /// selection is empty or holds a hidden/locked member.
    /// </summary>
    public HandleKind? HitHandle(double worldX, double worldY, double viewScale)
    {
        var selected = Session.SelectedObjects;
        if (selected.Count == 0 || selected.Any(o => !IsObjectAvailable(o)))
        {
            return null;
        }

        if (selected.Count == 1)
        {
            return SelectionHandles.HitTest(
                selected[0], worldX, worldY, HandleHitRadius(viewScale), RotationHandleOffset(viewScale));
        }

        var frame = MultiSelectionGeometry.CombinedBounds(selected)!.Value;
        return MultiSelectionGeometry.HitTestFrame(
            frame, worldX, worldY, HandleHitRadius(viewScale), RotationHandleOffset(viewScale));
    }

    /// <summary>
    /// One canvas press: a handle grab starts resize/rotate of the whole selection; a press on an
    /// object selects (Ctrl toggles) and starts a move; a press on empty canvas starts a marquee.
    /// Whatever the gesture, the completed edit lands as exactly one history entry.
    /// </summary>
    public CanvasGesture BeginCanvasInteraction(
        double worldX, double worldY, double viewScale, bool additive = false)
    {
        if (!additive && HitHandle(worldX, worldY, viewScale) is { } grabbed)
        {
            if (EditableSelectionList(allowAreaMembers: true) is not { } selected) return CanvasGesture.None;
            DragPreview = new DragPreview(
                selected, selected,
                grabbed == HandleKind.Rotate ? DragMode.Rotate : DragMode.Resize,
                grabbed, worldX, worldY,
                selected.Count > 1 ? MultiSelectionGeometry.CombinedBounds(selected) : null);
            return CanvasGesture.Drag;
        }

        var hit = SceneGeometry.HitTest(Session.Document, worldX, worldY);
        if (hit is null)
        {
            // The empty space inside a multi-selection frame is also a move handle.
            if (!additive && SelectionCount > 1 && MultiSelectionGeometry.CombinedBounds(Session.SelectedObjects) is { } frame
                && worldX >= frame.Left && worldX <= frame.Right && worldY >= frame.Top && worldY <= frame.Bottom)
                return BeginDrag(worldX, worldY) ? CanvasGesture.Drag : CanvasGesture.None;

            // Rivers and lakes are drawn under the objects of their layer, so they are picked only
            // where no object answers. From here on they behave like anything else on the canvas:
            // a click selects, Ctrl extends, and a drag moves the whole water system.
            if (SelectSurfaceAt(worldX, worldY, viewScale, additive))
            {
                if (!additive && SelectedRoads.Count > 0 && BeginRoadDrag(worldX, worldY)) return CanvasGesture.Drag;
                if (!additive && SelectedWaters.Count > 0 && BeginWaterDrag(worldX, worldY)) return CanvasGesture.Drag;
                return CanvasGesture.None;
            }

            if (!additive && (SelectionCount > 0 || WaterSelectionCount > 0 || RoadSelectionCount > 0))
            {
                Session.ClearSelection();
                ClearWaterSelection();
                ClearRoadSelection();
                AfterSelectionChange();
            }

            // Land under the pointer is a piece of the map like any other: a press selects it.
            // The marquee still starts, so a drag from here rubber-bands as it always did; a
            // press that does not move keeps the land selected (see EndMarquee).
            if (!additive && !SelectLandAt(worldX, worldY))
            {
                ClearLandSelection();
            }

            BeginMarquee(worldX, worldY, additive);
            return CanvasGesture.Marquee;
        }

        if (!additive && WaterSelectionCount > 0) ClearWaterSelection();
        if (!additive && RoadSelectionCount > 0) ClearRoadSelection();
        ClearLandSelection();

        SelectHit(hit, additive);
        RefreshInspector();
        if (Session.SelectedObjectIds.Any(id => PlacementPathOwnership.Owner(Session.Document, id) is not null))
        { StatusDetail = PlacementOwnership.DetachFirst; return CanvasGesture.None; }
        if (additive)
        {
            return CanvasGesture.None;
        }

        if (BeginDrag(worldX, worldY))
        {
            return CanvasGesture.Drag;
        }

        // The object is selected and its properties stay readable, but it cannot be moved. Saying
        // why beats a frame that silently refuses to follow the pointer.
        if (Session.SelectedObjects is { Count: > 0 } blocked)
        {
            // Name the real reason: the object itself, or the layer it sits on (R04).
            var layerLocked = blocked.Any(item => !item.Locked && item.Visible
                && DocumentEditor.FindLayerOf(Session.Document, item.Id) is { Locked: true });
            StatusDetail = blocked.Any(item => item.Locked)
                ? "Объект заблокирован: снимите блокировку, чтобы перемещать его."
                : layerLocked
                    ? "Слой заблокирован: снимите блокировку слоя объектов, чтобы перемещать его."
                    : "Объект скрыт: включите видимость, чтобы перемещать его.";
        }

        return CanvasGesture.None;
    }

    public bool BeginDrag(double startWorldX = 0, double startWorldY = 0)
    {
        if (EditableSelectionList(allowAreaMembers: true) is not { } selected) return false;

        DragPreview = new DragPreview(
            selected, selected, DragMode.Move, null, startWorldX, startWorldY,
            selected.Count > 1 ? MultiSelectionGeometry.CombinedBounds(selected) : null);
        return true;
    }

    /// <summary>Absolute-cursor drag update covering move, resize and rotate of the whole set.</summary>
    public void UpdateDragTo(double worldX, double worldY)
    {
        if (IsDraggingRoads)
        {
            UpdateRoadDrag(worldX, worldY);
            return;
        }
        if (IsDraggingWater)
        {
            UpdateWaterDrag(worldX, worldY);
            return;
        }

        if (DragPreview is not { } preview)
        {
            return;
        }

        var before = preview.Before;
        IReadOnlyList<SceneObject> proposed;
        if (preview.Mode == DragMode.Resize && before.Count == 1)
        {
            proposed = [SelectionHandles.Resize(before[0], preview.Handle!.Value, worldX, worldY)];
        }
        else if (preview.Mode == DragMode.Resize)
        {
            var frame = preview.StartFrame!.Value;
            var factor = MultiSelectionGeometry.ClampScaleFactor(
                before, MultiSelectionGeometry.ResizeFactor(frame, preview.Handle!.Value, worldX, worldY));
            proposed = MultiSelectionGeometry.ScaleAroundCentre(
                before, factor, (frame.Left + frame.Right) / 2, (frame.Top + frame.Bottom) / 2);
        }
        else if (preview.Mode == DragMode.Rotate && before.Count == 1)
        {
            proposed = [SelectionHandles.Rotate(
                before[0], preview.StartWorldX, preview.StartWorldY, worldX, worldY)];
        }
        else if (preview.Mode == DragMode.Rotate)
        {
            var frame = preview.StartFrame!.Value;
            var centreX = (frame.Left + frame.Right) / 2;
            var centreY = (frame.Top + frame.Bottom) / 2;
            var delta = (Math.Atan2(worldY - centreY, worldX - centreX)
                - Math.Atan2(preview.StartWorldY - centreY, preview.StartWorldX - centreX))
                * 180.0 / Math.PI;
            proposed = MultiSelectionGeometry.RotateAroundCentre(before, delta, centreX, centreY);
        }
        else
        {
            proposed = MultiSelectionGeometry.Translate(
                before, worldX - preview.StartWorldX, worldY - preview.StartWorldY);
        }

        DragPreview = preview with { Proposed = proposed };
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void UpdateDrag(double worldDx, double worldDy)
    {
        if (DragPreview is { } preview)
        {
            UpdateDragTo(preview.StartWorldX + worldDx, preview.StartWorldY + worldDy);
        }
    }

    /// <summary>The whole drag becomes exactly one history entry; a no-move drag becomes none.</summary>
    public void EndDrag()
    {
        if (IsDraggingRoads)
        {
            EndRoadsDrag();
            return;
        }
        if (IsDraggingWater)
        {
            EndWaterDrag();
            return;
        }

        if (DragPreview is not { } preview)
        {
            return;
        }

        DragPreview = null;

        // A click must not round saved geometry or detach an area member without a drag.
        if (preview.Proposed.SequenceEqual(preview.Before))
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
            return;
        }
        var proposed = QuantizeTransformedGeometries(preview.Before, preview.Proposed);
        if (!proposed.SequenceEqual(preview.Before))
        {
            ExecuteManualAreaSelection(new ReplaceObjectsCommand(
                preview.Before, proposed, "Transform selection"), preview.Before.Select(o => o.Id).ToArray());
        }
        else
        {
            InvalidateScene(EditorChangeReason.CanvasInvalidation);
        }
    }

    public void CancelDrag()
    {
        CancelWaterDrag();
        CancelRoadDrag();
        if (DragPreview is null)
        {
            return;
        }

        DragPreview = null;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void Undo()
    {
        // Inside an unfinished area or path, undo steps back one vertex. It never also reaches
        // into the map history: that used to discard the draft AND the previous finished edit.
        if (RemoveLastAreaDraftNode() || RemoveLastPathDraftNode()) return;
        // A large area change still being computed is simply dropped: it never happened.
        if (CancelEditableAreaWork()) { RaiseSessionState(); return; }
        CancelEditablePathGesture(); CancelEditableAreaGesture(); CancelRiverGesture(); CancelRoadGesture();
        if (Session.Undo())
        {
            SyncEditablePathSettings(); SyncEditableAreaSettings();
            AfterEdit();
        }
    }

    public void Redo()
    {
        if (_areaCreating || _pathCreating) return;
        CancelEditableAreaWork();
        CancelEditablePathGesture(); CancelEditableAreaGesture(); CancelRiverGesture(); CancelRoadGesture();
        if (Session.Redo())
        {
            SyncEditablePathSettings(); SyncEditableAreaSettings();
            AfterEdit();
        }
    }

    public bool CreateVersion()
    {
        var timestamp = _clock();
        return CreateCheckpoint(NextVersionName(timestamp), timestamp, userTerm: "версия");
    }

    private string NextVersionName(DateTimeOffset timestamp)
    {
        var used = new HashSet<int>();
        foreach (var checkpoint in Session.Document.Checkpoints)
        {
            const string prefix = "Версия ";
            if (checkpoint.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && checkpoint.Name.Length >= prefix.Length + 2
                && int.TryParse(checkpoint.Name.AsSpan(prefix.Length, 2), out var number))
            {
                used.Add(number);
            }
        }

        var next = Enumerable.Range(1, MapCheckpointLimits.MaxCount + 1)
            .First(number => !used.Contains(number));
        return $"Версия {next:00} · {timestamp.ToLocalTime():dd.MM.yyyy HH:mm}";
    }

    /// <summary>The native command prepares the immutable snapshot off the UI thread. The same
    /// operation gate as Save/close prevents publication after the editor has closed.</summary>
    public async Task<bool> CreateVersionAsync()
    {
        if (!await _saveGate.WaitAsync(0))
        {
            StatusDetail = "Дождитесь завершения текущего сохранения или создания версии.";
            return false;
        }
        try
        {
            var source = Session.Document;
            if (source.Checkpoints.Count >= MapCheckpointLimits.MaxCount)
            {
                StatusDetail = $"Достигнут лимит: не больше {MapCheckpointLimits.MaxCount} версий.";
                return false;
            }
            var timestamp = _clock();
            var name = NextVersionName(timestamp);
            using var images = new ExportStampImageSource(source, DocumentPath, _assetCatalog);
            var checkpoint = await RunResourceOperationAsync(() => Task.Run(() =>
            {
                var preview = ThumbnailRenderer.RenderPng(source,
                    MapCheckpointLimits.PreviewWidth, MapCheckpointLimits.PreviewHeight, images);
                var result = new MapCheckpoint(CheckpointId.New(), name, timestamp,
                    MapCheckpointSnapshot.Capture(source), preview);
                MapProjectStore.ValidateCheckpointAddition(source, result);
                return result;
            }));
            if (!ReferenceEquals(Session.Document, source))
            {
                StatusDetail = "Карта изменилась во время подготовки версии. Повторите создание версии.";
                return false;
            }
            PublishCheckpoint(checkpoint, "версия");
            return true;
        }
        catch (Exception ex) when (ex is MapProjectResourceLimitException or ArgumentException or InvalidOperationException or IOException)
        {
            StatusDetail = "Не удалось создать версию: " + ex.Message;
            _log.Error("Checkpoint creation failed", ex);
            return false;
        }
        finally { _saveGate.Release(); }
    }

    public bool CreateCheckpoint(string? name) =>
        CreateCheckpoint(name, _clock(), userTerm: "версия");

    private bool CreateCheckpoint(string? name, DateTimeOffset timestamp, string userTerm)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusDetail = "Введите непустое название версии.";
            return false;
        }

        if (name.Trim().Length > MapCheckpointLimits.MaxNameLength)
        {
            StatusDetail = $"Название должно быть не длиннее {MapCheckpointLimits.MaxNameLength} символов.";
            return false;
        }

        if (Session.Document.Checkpoints.Count >= MapCheckpointLimits.MaxCount)
        {
            StatusDetail = $"Достигнут лимит: не больше {MapCheckpointLimits.MaxCount} версий.";
            return false;
        }

        try
        {
            var source = Session.Document;
            var preview = RenderPreview(
                source, MapCheckpointLimits.PreviewWidth, MapCheckpointLimits.PreviewHeight);
            var checkpoint = new MapCheckpoint(
                CheckpointId.New(),
                name,
                timestamp,
                MapCheckpointSnapshot.Capture(source),
                preview);

            MapProjectStore.ValidateCheckpointAddition(source, checkpoint);
            PublishCheckpoint(checkpoint, userTerm);
            return true;
        }
        catch (MapProjectResourceLimitException ex)
        {
            StatusDetail = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось создать версию: " + ex.Message;
            _log.Error("Checkpoint creation failed", ex);
            return false;
        }
    }

    private void PublishCheckpoint(MapCheckpoint checkpoint, string userTerm)
    {
        Session.Execute(new AddCheckpointCommand(checkpoint));
        AfterEdit();
        StatusDetail = string.IsNullOrWhiteSpace(DocumentPath)
            ? $"Создана {userTerm} «{checkpoint.Name}» в памяти. Укажите путь командой «Сохранить»."
            : $"Создана {userTerm} «{checkpoint.Name}». Она будет сохранена автоматически.";
    }

    public bool RenameVersion(CheckpointId id, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusDetail = "Название версии не может быть пустым.";
            return false;
        }
        if (name.Trim().Length > MapCheckpointLimits.MaxNameLength)
        {
            StatusDetail = $"Название должно быть не длиннее {MapCheckpointLimits.MaxNameLength} символов.";
            return false;
        }

        try
        {
            Session.Execute(RenameCheckpointCommand.Create(Session.Document, id, name));
            AfterEdit();
            StatusDetail = $"Версия переименована в «{name.Trim()}».";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось переименовать версию: " + ex.Message;
            return false;
        }
    }

    public async Task<bool> RestoreCheckpointAsync(CheckpointId id)
    {
        var checkpoint = Session.Document.Checkpoints.FirstOrDefault(item => item.Id == id);
        if (checkpoint is null)
        {
            StatusDetail = "Версия не найдена. Текущая карта не изменена.";
            return false;
        }

        if (!await _prompts.ConfirmRestoreCheckpointAsync(checkpoint.Name))
        {
            StatusDetail = $"Восстановление «{checkpoint.Name}» отменено. Карта не изменена.";
            return false;
        }

        try
        {
            Session.Execute(RestoreCheckpointCommand.Create(Session.Document, id));
            AfterEdit();
            StatusDetail = $"Восстановлена версия «{checkpoint.Name}». Действие можно отменить одной командой (Ctrl+Z).";
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось восстановить версию. Текущая карта не изменена.";
            _log.Error("Checkpoint restore failed", ex);
            return false;
        }
    }

    public async Task<bool> DeleteCheckpointAsync(CheckpointId id)
    {
        var checkpoint = Session.Document.Checkpoints.FirstOrDefault(item => item.Id == id);
        if (checkpoint is null)
        {
            StatusDetail = "Версия не найдена. Ничего не удалено.";
            return false;
        }

        if (!await _prompts.ConfirmDeleteCheckpointAsync(checkpoint.Name))
        {
            StatusDetail = $"Удаление «{checkpoint.Name}» отменено. Ничего не изменено.";
            return false;
        }

        try
        {
            Session.Execute(DeleteCheckpointCommand.Create(Session.Document, id));
            AfterEdit();
            StatusDetail = $"Версия «{checkpoint.Name}» удалена. Изменение будет сохранено автоматически.";
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            StatusDetail = "Не удалось удалить версию. Ничего не изменено.";
            _log.Error("Checkpoint deletion failed", ex);
            return false;
        }
    }

    /// <summary>Refreshes the derived session state the status bar and the toolbar read.</summary>
    private void RaiseSessionState()
    {
        Raise(nameof(PendingChanges));
        Raise(nameof(SaveSummary));
        Raise(nameof(AutosaveSummary));
        Raise(nameof(PrimaryAutosaveSummary));
        Raise(nameof(RecoveryAvailable));
        Raise(nameof(CanUndo));
        Raise(nameof(CanRedo));
    }

    /// <summary>Own-process smoke hook: one prepared edit through the same path a panel command takes (history, panels, canvas).</summary>
    public void ExecuteEditForSmoke(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Session.Execute(command);
        AfterEdit();
    }

    /// <summary>
    /// Diagnostics for the native profiles: when set, every edit records how long each step of
    /// <see cref="AfterEdit"/> and of a stroke's commit took on the UI thread (the handlers of
    /// the raised properties included). Never on in the product.
    /// </summary>
    private static bool _collectEditTimings;
    public static bool CollectEditTimings
    {
        get => _collectEditTimings;
        set
        {
            _collectEditTimings = value;
            EditingSession.StepTimed = value ? (step, ms) => _timingSink?.LastEditTimings.Add((step, ms)) : null;
        }
    }
    private static EditorViewModel? _timingSink;
    public List<(string Step, double Ms)> LastEditTimings { get { _timingSink = this; return _lastEditTimings; } }
    private readonly List<(string Step, double Ms)> _lastEditTimings = [];

    private void Timed(string step, Action action)
    {
        if (!CollectEditTimings) { action(); return; }
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        action();
        LastEditTimings.Add((step, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    private void AfterEdit(bool geometryOnly = false)
    {
        RefreshHexJourney();
        RefreshComparison();
        if (CollectEditTimings) { LastEditTimings.Add(("after-edit", 0)); }
        Timed("path/area/river/road state", () => { RaisePathState(); RaiseAreaState(); RaiseRiverState(); RaiseRoadState(); });
        _lastCommittedEditUtc = _clock();
        Timed("save-state", () => SaveState = Session.IsDirty ? SaveState.Unsaved : SaveState.Saved);
        Timed("status", () => StatusDetail = "");
        Timed("canvas-workflow", RefreshCanvasWorkflowAfterDocumentChange);

        if (_activeLayerId is null || Session.Document.FindLayer(_activeLayerId.Value) is null)
        {
            _activeLayerId = Session.Document.Layers
                .LastOrDefault(layer => layer is not BaseColorLayer)?.Id
                ?? Session.Document.BaseColor.Id;
        }

        // An edit (or an undo/redo) may have emptied the open group; an empty open group would
        // strand the tree behind a breadcrumb with nothing under it.
        if (_openGroupId is { } openGroup
            && SceneGeometry.GroupMembers(Session.Document, openGroup).Count == 0)
        {
            _openGroupId = null;
        }
        Timed("inspector", RefreshInspector);
        if (geometryOnly)
        {
            // A nudge changes coordinates, not layer/library/note/version rows. Keep the
            // inspector, dirty/save state and canvas current without rebuilding every panel.
            Raise(nameof(Title));
            RaiseSessionState();
            InvalidateScene(EditorChangeReason.ObjectProperties | EditorChangeReason.CanvasInvalidation);
            return;
        }
        foreach (var name in new[]
        {
            nameof(SelectionStatus), nameof(SelectionKind), nameof(SelectionCount), nameof(ShowSingleInspector),
            nameof(SelectedGroupId), nameof(SelectedGroupName), nameof(OpenGroupId), nameof(SelectionVisibilitySummary),
            nameof(SelectionLockSummary), nameof(Title), nameof(Layers), nameof(Notes), nameof(Checkpoints),
            nameof(PrimaryAutosaveSummary), nameof(ActiveLayer), nameof(ActiveLayerId), nameof(ActiveLayerSummary),
            nameof(GridSummary), nameof(MaskEditLayerId), nameof(SelectedTextFontResolution),
        })
        {
            Timed("raise " + name, () => Raise(name));
        }
        Timed("session-state", RaiseSessionState);
        Timed("scene-changed", () => SceneChanged?.Invoke(this, EventArgs.Empty));
    }
}
