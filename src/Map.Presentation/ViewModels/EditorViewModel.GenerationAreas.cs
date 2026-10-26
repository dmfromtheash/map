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
        var members = area.Members.ToDictionary(m => m.ObjectId, m => m.Slot);
        foreach (var o in layer.Objects)
            if (members.TryGetValue(o.Id, out var slot) && double.Hypot(o.X - x, o.Y - y) <= _areaDensityRadius)
            { if (GenerationProtectionErase) _areaProtectionStroke.Remove(slot); else _areaProtectionStroke.Add(slot); }
    }

    private bool FinishAreaProtection()
    {
        if (_areaProtectionStroke is not { } slots || SelectedEditableArea is not { } area || EditableAreaLayer(area) is not { } layer) return false;
        _areaProtectionStroke = null;
        var next = area with { GenerationProtection = slots.Count == 0 ? null : slots.ToImmutableHashSet() };
        Session.Execute(UpdateLayerCommand.Replace(layer, layer with { PlacementAreas = layer.PlacementAreas.Replace(area, next) }, "Кисть защиты области"));
        AfterEdit(); RaiseAreaState(); return true;
    }
}
