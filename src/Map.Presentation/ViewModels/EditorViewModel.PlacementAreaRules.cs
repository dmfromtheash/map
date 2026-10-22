using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R10 rules of an editable area: «не на воду», «только на воду», «не на дорогу», «только на суше».
/// Each has its own switch; all are off by default and an area without rules stores none, so older
/// maps keep their schema and look. Every switch is one Undo step.
/// </summary>
public sealed partial class EditorViewModel
{
    private PlacementAreaRules? _naturalAreaRules;

    public PlacementAreaRules EditableAreaRules => (DisplayedEditableArea is { } area ? area.Rules : _naturalAreaRules) ?? new();

    /// <summary>How many places of the selected area the rules keep empty.</summary>
    public int EditableAreaRuleBlockedCount => DisplayedEditableArea?.RuleBlocked.Count ?? 0;

    /// <summary>Land pieces an area can be limited to, in terrain order.</summary>
    public IReadOnlyList<(LayerId Id, string Name)> EditableAreaLandChoices =>
        [.. TerrainOrder.Lands(Session.Document).Select(l => (l.Id, l.Name))];

    public void SetEditableAreaAvoidWater(bool value) =>
        SetEditableAreaRules(r => r with { AvoidWater = value, OnlyOnWater = value ? false : r.OnlyOnWater },
            value ? "Не ставить на воду" : "Разрешить на воде");

    public void SetEditableAreaOnlyOnWater(bool value) =>
        SetEditableAreaRules(r => r with { OnlyOnWater = value, AvoidWater = value ? false : r.AvoidWater, OnlyOnLand = value ? false : r.OnlyOnLand },
            value ? "Ставить только на воду" : "Не только на воду");

    public void SetEditableAreaAvoidRoads(bool value) =>
        SetEditableAreaRules(r => r with { AvoidRoads = value }, value ? "Не ставить на дорогу" : "Разрешить на дороге");

    public void SetEditableAreaRoadMargin(double value, bool commit = true)
    {
        if (!double.IsFinite(value) || !commit) return;
        var margin = Math.Clamp(Math.Round(value), 0, PlacementAreaRules.MaxRoadMargin);
        SetEditableAreaRules(r => r with { RoadMargin = margin }, "Отступ от дороги");
    }

    public void SetEditableAreaOnlyOnLand(bool value) =>
        SetEditableAreaRules(r => r with { OnlyOnLand = value, OnlyOnWater = value ? false : r.OnlyOnWater },
            value ? "Ставить только на суше" : "Не только на суше");

    /// <summary>Null = any land; otherwise only on that land layer.</summary>
    public void SetEditableAreaLandLayer(LayerId? land) =>
        SetEditableAreaRules(r => r with { LandLayerId = land }, "Суша для области");

    public bool EditableAreaDepthSort => DisplayedEditableArea?.DepthSort ?? false;

    /// <summary>«Ближние поверх дальних»: draw the area's objects by their depth on the map.</summary>
    public void SetEditableAreaDepthSort(bool value)
    {
        if (_areaCreating && _areaDraft is { } draft) { _areaDraft = draft with { DepthSort = value }; UpdateAreaPreview(); RaiseAreaState(); }
        else if (SelectedEditableArea is not null)
            CommitAreaChange(a => a with { DepthSort = value }, value ? "Ближние поверх дальних" : "Порядок области как расставлено");
    }

    /// <summary>«Пересчитать по карте»: fills the area again after rivers, roads or land were edited.</summary>
    public void RefreshEditableAreaRules()
    {
        if (SelectedEditableArea is not { Rules: { Any: true } rules }) { StatusDetail = "У области нет включённых правил."; return; }
        CommitAreaChange(a => a with { Rules = rules with { Revision = rules.Revision + 1 } }, "Пересчитать область по карте");
    }

    private void SetEditableAreaRules(Func<PlacementAreaRules, PlacementAreaRules> change, string name)
    {
        PlacementAreaRules? Next(PlacementAreaRules? current)
        {
            var next = change(current ?? new());
            next.Validate();
            // No switch on: store nothing, so the area stays as older files describe it.
            return next.Any ? next : null;
        }

        if (_areaCreating && _areaDraft is { } draft)
        {
            _areaDraft = draft with { Rules = Next(draft.Rules) };
            _naturalAreaRules = _areaDraft.Rules;
            UpdateAreaPreview(); RaiseAreaState();
        }
        else if (SelectedEditableArea is not null)
        {
            CommitAreaChange(a => a with { Rules = Next(a.Rules) }, name);
        }
        else
        {
            _naturalAreaRules = Next(_naturalAreaRules);
            RaiseAreaState();
        }
    }
}
