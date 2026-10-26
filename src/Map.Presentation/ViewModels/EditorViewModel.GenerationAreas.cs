using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private bool _areaProtectionBrush;
    private HashSet<PlacementAreaSlot>? _areaProtectionStroke;
    public void StartAreaGeneration() => StartAreaGeneration(null);
    private void StartAreaGeneration(Func<PlacementArea, PlacementArea>? change, Guid? targetId = null)
    {
        var area = targetId is { } id ? EditableAreas.FirstOrDefault(a => a.Id == id) : SelectedEditableArea;
        if (area is null || EditableAreaLayer(area) is not { } layer || IsEditableAreaBusy) return;
        var parameters = change?.Invoke(area) ?? area;
        var ecology = AreaForestEcology(parameters);
        _recalculateGeneration = () => { StartAreaGeneration(change, area.Id); return PendingGeneration; };
        PendingGeneration = CalculateGenerationAsync((document, token) => ControlledAreaGeneration.Create(document, layer.Id, area.Id, token, ecology, parameters));
    }

    public void SetAreaProtectionBrush(bool active)
    {
        _areaProtectionBrush = active;
        _areaProtectionStroke = null;
        if (active) { SetAreaDensityBrush(false); StatusDetail = "Кисть защиты области: ведите по объектам. Размер — радиус кисти плотности."; }
        RaiseAreaState();
    }

    private void DabAreaProtection(double x, double y)
    {
        if (_areaProtectionStroke is null || SelectedEditableArea is not { } area || EditableAreaLayer(area) is not { } layer) return;
        if (_areaDensityWork is { } work)
        {
            var x0 = Math.Max(0, (int)Math.Floor((x - _areaDensityRadius - work.Left) / work.Cell));
            var x1 = Math.Min(work.Width - 1, (int)Math.Floor((x + _areaDensityRadius - work.Left) / work.Cell));
            var y0 = Math.Max(0, (int)Math.Floor((y - _areaDensityRadius - work.Top) / work.Cell));
            var y1 = Math.Min(work.Height - 1, (int)Math.Floor((y + _areaDensityRadius - work.Top) / work.Cell));
            for (var row = y0; row <= y1; row++)
            for (var column = x0; column <= x1; column++)
                if (double.Hypot(work.Left + (column + .5) * work.Cell - x, work.Top + (row + .5) * work.Cell - y) <= _areaDensityRadius)
                    work.Values[row * work.Width + column] = GenerationProtectionErase ? PlacementAreaDensityMask.Neutral : (byte)0;
        }
        var members = area.Members.ToDictionary(m => m.ObjectId, m => m.Slot);
        foreach (var o in layer.Objects)
            if (members.TryGetValue(o.Id, out var slot) && double.Hypot(o.X + o.Width / 2 - x, o.Y + o.Height / 2 - y) <= _areaDensityRadius)
            { if (GenerationProtectionErase) _areaProtectionStroke.Remove(slot); else _areaProtectionStroke.Add(slot); }
    }

    private bool FinishAreaProtection()
    {
        if (_areaProtectionStroke is not { } slots || SelectedEditableArea is not { } area || EditableAreaLayer(area) is not { } layer) return false;
        _areaProtectionStroke = null;
        var mask = _areaDensityWork is { } work
            ? PlacementAreaDensityBrush.Finish(work.Left, work.Top, work.Cell, work.Width, work.Height, work.Values) : area.GenerationProtectionMask;
        _areaDensityWork = null; _areaDensityLast = null;
        var next = area with { GenerationProtection = slots.Count == 0 ? null : slots.ToImmutableHashSet(), GenerationProtectionMask = mask };
        Session.Execute(UpdateLayerCommand.Replace(layer, layer with { PlacementAreas = layer.PlacementAreas.Replace(area, next) }, "Кисть защиты области"));
        AfterEdit(); RaiseAreaState(); return true;
    }
}
