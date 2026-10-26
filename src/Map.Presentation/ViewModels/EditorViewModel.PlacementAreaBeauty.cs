using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R10 «Красота» of an editable area and its density brush. Every property has its own switch or
/// slider and is one Undo step; an area of an older map has none and looks as before. A new area
/// gets <see cref="PlacementAreaBeauty.ForNewArea"/> unless the switches were changed before it was
/// drawn. The brush paints an 8-bit raster over the selected area: a whole stroke is one Undo step,
/// and only the cells under it are filled again.
/// </summary>
public sealed partial class EditorViewModel
{
    private PlacementAreaBeauty? _naturalAreaBeauty;
    private bool _areaDensityBrush;
    private PlacementAreaDensityBrushMode _areaDensityMode = PlacementAreaDensityBrushMode.Thinner;
    private double _areaDensityRadius = 120, _areaDensityStrength = .35;
    private (double Left, double Top, double Cell, int Width, int Height, byte[] Values)? _areaDensityWork;
    private (double X, double Y)? _areaDensityLast;
    private int _areaDensityVersion;
    private PlacementAreaDensityMask? _overlayMask;
    private byte[]? _overlayValues;

    /// <summary>The «Красота» of the shown area (or of the next area while none is shown).</summary>
    public PlacementAreaBeauty EditableAreaBeauty => DisplayedEditableArea is { } area
        ? area.Beauty ?? new() : _naturalAreaBeauty ?? PlacementAreaBeauty.ForNewArea(96, _naturalAreaPattern);

    /// <summary>What a new area starts with: the switches set before drawing it, or the defaults.</summary>
    private PlacementAreaBeauty? NewAreaBeauty(double baseWidth)
    {
        var beauty = _naturalAreaBeauty ?? PlacementAreaBeauty.ForNewArea(baseWidth, _naturalAreaPattern);
        return beauty.Any ? beauty : null;
    }

    public void SetEditableAreaSoftEdge(bool value) =>
        SetEditableAreaBeauty(b => b with { SoftEdge = value }, value ? "Мягкий край области" : "Без мягкого края");

    public void SetEditableAreaEdgeWidth(double value, bool commit = true)
    {
        if (!double.IsFinite(value) || !commit) return;
        var width = Math.Clamp(Math.Round(value), 1, PlacementAreaBeauty.MaxEdgeWidth);
        SetEditableAreaBeauty(b => b with { EdgeWidth = width }, "Ширина мягкого края");
    }

    public void SetEditableAreaClumps(bool value) =>
        SetEditableAreaBeauty(b => b with { Clumps = value }, value ? "Кучки и поляны" : "Без кучек");

    public void SetEditableAreaClumpStrength(double value, bool commit = true)
    {
        if (!double.IsFinite(value) || !commit) return;
        var strength = Math.Clamp(Math.Round(value), 0, 100);
        SetEditableAreaBeauty(b => b with { ClumpStrength = strength }, "Сила кучек");
    }

    public void SetEditableAreaClumpSize(double value, bool commit = true)
    {
        if (!double.IsFinite(value) || !commit) return;
        var size = Math.Clamp(Math.Round(value), 10, PlacementAreaBeauty.MaxClumpSize);
        SetEditableAreaBeauty(b => b with { ClumpSize = size }, "Размер кучек");
    }

    public void SetEditableAreaSpeciesPatches(bool value) =>
        SetEditableAreaBeauty(b => b with { SpeciesPatches = value }, value ? "Рощи одного вида" : "Виды вперемешку");

    public void SetEditableAreaGroundShadow(bool value) =>
        SetEditableAreaBeauty(b => b with { GroundShadow = value }, value ? "Тень под объектами" : "Без тени под объектами");

    public void SetEditableAreaShadowOpacity(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent) || !commit) return;
        var opacity = Math.Clamp(Math.Round(percent), 0, 100) / 100;
        SetEditableAreaBeauty(b => b with { ShadowOpacity = opacity }, "Сила тени под объектами");
    }

    public void SetEditableAreaUnderlay(bool value) =>
        SetEditableAreaBeauty(b => b with { Underlay = value }, value ? "Подложка массива" : "Без подложки");

    public void SetEditableAreaUnderlayOpacity(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent) || !commit) return;
        var opacity = Math.Clamp(Math.Round(percent), 0, 100) / 100;
        SetEditableAreaBeauty(b => b with { UnderlayOpacity = opacity }, "Сила подложки");
    }

    public void SetEditableAreaColorVariation(bool value) =>
        SetEditableAreaBeauty(b => b with { ColorVariation = value }, value ? "Разброс цвета" : "Без разброса цвета");

    public void SetEditableAreaColorVariationPercent(double value, bool commit = true)
    {
        if (!double.IsFinite(value) || !commit) return;
        var percent = Math.Clamp(Math.Round(value), 0, PlacementAreaBeauty.MaxColorVariation);
        SetEditableAreaBeauty(b => b with { ColorVariationPercent = percent }, "Сила разброса цвета");
    }

    private void SetEditableAreaBeauty(Func<PlacementAreaBeauty, PlacementAreaBeauty> change, string name)
    {
        PlacementAreaBeauty? Next(PlacementAreaBeauty? current)
        {
            var next = change(current ?? new());
            next.Validate();
            // Nothing switched on: store nothing, so the area stays as older files describe it.
            return next.Any ? next : null;
        }

        if (_areaCreating && _areaDraft is { } draft)
        {
            var next = change(draft.Beauty ?? new()); next.Validate();
            _naturalAreaBeauty = next;
            _areaDraft = draft with { Beauty = next.Any ? next : null };
            UpdateAreaPreview(); RaiseAreaState();
        }
        else if (SelectedEditableArea is { LayoutVersion: >= 3 })
        {
            CommitAreaChange(a => a with { Beauty = Next(a.Beauty) }, name);
        }
        else if (SelectedEditableArea is not null)
        {
            StatusDetail = "«Красота» работает у естественной расстановки: нажмите «Расположить естественно».";
        }
        else
        {
            var next = change(_naturalAreaBeauty ?? PlacementAreaBeauty.ForNewArea(96, _naturalAreaPattern)); next.Validate();
            _naturalAreaBeauty = next;
            RaiseAreaState();
        }
    }

    // ---- The density brush ----

    /// <summary>Whether a press on the map paints the density of the selected area.</summary>
    public bool IsAreaDensityBrushActive => (_areaDensityBrush || _areaProtectionBrush) && SelectedEditableArea is { LayoutVersion: >= 3 };

    public PlacementAreaDensityBrushMode AreaDensityBrushMode => _areaDensityMode;
    public double AreaDensityBrushRadius => _areaDensityRadius;
    public double AreaDensityBrushStrength => _areaDensityStrength * 100;
    public bool EditableAreaHasDensityMask => DisplayedEditableArea?.DensityMask is not null;

    /// <summary>The last point of the brush on the map (for its circle), or null.</summary>
    public (double X, double Y)? AreaDensityBrushPoint { get; private set; }

    public void SetAreaDensityBrush(bool active)
    {
        if (active) _areaProtectionBrush = false;
        if (active && SelectedEditableArea is not { LayoutVersion: >= 3 })
        {
            StatusDetail = SelectedEditableArea is null ? "Выберите область, чтобы рисовать её плотность."
                : "Кисть плотности работает у естественной расстановки: нажмите «Расположить естественно».";
            active = false;
        }
        _areaDensityBrush = active; _areaDensityWork = null; _areaDensityLast = null;
        if (!active) AreaDensityBrushPoint = null;
        else StatusDetail = "Кисть плотности: ведите по области. «Реже» убирает объекты, «Гуще» добавляет, «Как было» возвращает.";
        RaiseAreaState(); InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void SetAreaDensityBrushMode(PlacementAreaDensityBrushMode mode) { _areaDensityMode = mode; RaiseAreaState(); }

    public void SetAreaDensityBrushRadius(double value, bool commit = true)
    {
        if (!double.IsFinite(value)) return;
        _areaDensityRadius = Math.Clamp(Math.Round(value), 4, 2000); RaiseAreaState();
    }

    public void SetAreaDensityBrushStrength(double percent, bool commit = true)
    {
        if (!double.IsFinite(percent)) return;
        _areaDensityStrength = Math.Clamp(Math.Round(percent), 1, 100) / 100; RaiseAreaState();
    }

    /// <summary>«Сбросить кисть»: the whole raster goes, one Undo step.</summary>
    public void ClearEditableAreaDensityMask()
    {
        if (SelectedEditableArea is not { DensityMask: not null }) return;
        CommitAreaChange(a => a with { DensityMask = null }, "Сбросить кисть плотности");
    }

    public bool BeginAreaDensityStroke(double x, double y)
    {
        if (!IsAreaDensityBrushActive || ActiveTool != EditorTool.Stamp || _areaCreating) return false;
        if (_areaProtectionBrush) { _areaProtectionStroke = [.. SelectedEditableArea!.GenerationProtection ?? []]; DabAreaProtection(x, y); return true; }
        if (IsEditableAreaBusy) { StatusDetail = EditableAreaPlacementHint; return true; }
        _areaDensityWork = PlacementAreaDensityBrush.Begin(SelectedEditableArea!);
        _areaDensityLast = null;
        ContinueAreaDensityStroke(x, y);
        return true;
    }

    public void HoverAreaDensityBrush(double x, double y)
    {
        if (!IsAreaDensityBrushActive) return;
        AreaDensityBrushPoint = (x, y);
    }

    public void ContinueAreaDensityStroke(double x, double y)
    {
        if (_areaProtectionBrush) { DabAreaProtection(x, y); return; }
        AreaDensityBrushPoint = (x, y);
        if (_areaDensityWork is not { } work) return;
        // Dabs a quarter of the radius apart, so a fast stroke stays continuous.
        var (fromX, fromY) = _areaDensityLast ?? (x, y);
        var length = Math.Sqrt((x - fromX) * (x - fromX) + (y - fromY) * (y - fromY));
        var steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, _areaDensityRadius / 4)));
        for (var i = _areaDensityLast is null ? steps : 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            PlacementAreaDensityBrush.Dab(work.Left, work.Top, work.Cell, work.Width, work.Height, work.Values,
                fromX + (x - fromX) * t, fromY + (y - fromY) * t, _areaDensityRadius, _areaDensityStrength, _areaDensityMode);
        }
        _areaDensityLast = (x, y); _areaDensityVersion++;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool EndAreaDensityStroke()
    {
        if (_areaProtectionBrush) return FinishAreaProtection();
        if (_areaDensityWork is not { } work) return false;
        _areaDensityWork = null; _areaDensityLast = null; _areaDensityVersion++;
        var mask = PlacementAreaDensityBrush.Finish(work.Left, work.Top, work.Cell, work.Width, work.Height, work.Values);
        if (SelectedEditableArea is not { } area || Equals(area.DensityMask, mask)) { InvalidateScene(EditorChangeReason.CanvasInvalidation); return false; }
        return CommitAreaChange(a => a with { DensityMask = mask }, "Кисть плотности");
    }

    /// <summary>Esc during a stroke: the stroke is dropped, the area keeps its raster.</summary>
    public bool CancelAreaDensityStroke()
    {
        if (_areaProtectionStroke is not null) { _areaProtectionStroke = null; return true; }
        if (_areaDensityWork is null) return false;
        _areaDensityWork = null; _areaDensityLast = null; _areaDensityVersion++;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        return true;
    }

    /// <summary>The raster to show over the area while the brush is on: the stroke in progress or the stored one.</summary>
    public AreaDensityOverlay? AreaDensityOverlayToDraw
    {
        get
        {
            if (!IsAreaDensityBrushActive || SelectedEditableArea is not { } area) return null;
            if (_areaDensityWork is { } work)
                return new(work.Left, work.Top, work.Cell, work.Width, work.Height, work.Values, _areaDensityVersion, area.Nodes);
            if (area.DensityMask is { } mask)
            {
                // One copy per stored raster, not per frame.
                if (!ReferenceEquals(_overlayMask, mask)) { _overlayMask = mask; _overlayValues = mask.CopyValues(); _areaDensityVersion++; }
                return new(mask.Left, mask.Top, mask.CellSize, mask.Width, mask.Height, _overlayValues!, _areaDensityVersion, area.Nodes);
            }
            return new(0, 0, 1, 0, 0, [], 0, area.Nodes);
        }
    }
}

/// <summary>What the canvas draws for the density brush: the raster (128 = as set) inside the area.</summary>
public sealed record AreaDensityOverlay(double Left, double Top, double CellSize, int Width, int Height, byte[] Values,
    int Version, System.Collections.Immutable.ImmutableArray<PlacementPathNode> Nodes);
