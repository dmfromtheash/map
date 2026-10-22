using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private bool _hexPartyDragging;
    public int HexCampaignDay
    {
        get => ActiveHexLayer?.CampaignDay ?? 0;
        set
        {
            if (value < 0 || ActiveHexLayer is not { } l || l.CampaignDay == value) return;
            ExecuteHexLayerEdit("Дата кампании", l, l with { CampaignDay = value });
        }
    }
    public int HexPartyRadius
    {
        get => ActiveHexLayer?.Party.SightRadius ?? 1;
        set
        {
            if (ActiveHexLayer is not { } l) return;
            value = Math.Clamp(value, 0, HexGameplay.MaxSightRadius);
            if (value == l.Party.SightRadius) return;
            ExecuteHexLayerEdit("Обзор партии", l, l with { Party = l.Party with { SightRadius = value } });
        }
    }
    public string HexPartySummary => ActiveHexLayer is { } l && l.Party.Position is { } h
        ? $"Партия: {Map.Rendering.HexLayerRenderer.NumberOf(l.Board, l.Board.Layout.ToOffset(h))}. Шагов в истории: {l.Party.History.Count}."
        : "Партия ещё не размещена.";

    public void MoveHexParty(HexCoord h)
    {
        if (ActiveHexLayer is not { } l) return;
        ExecuteHexLayerEdit("Шаг партии", l, HexGameplay.MoveParty(l, h));
    }
    public void ClearHexPartyHistory()
    {
        if (ActiveHexLayer is not { } l || l.Party.History.Count == 0) return;
        ExecuteHexLayerEdit("Очистить историю партии", l, l with { Party = l.Party with { History = [] } });
    }
    public void SetHexRegionKnowledge(int id, string rumor, string label)
    {
        if (ActiveHexLayer is not { } l || l.Region(id) is not { } r) return;
        var after = r with { Rumor = rumor[..Math.Min(rumor.Length, HexNote.MaxTextLength)],
            ApproximateLabel = label[..Math.Min(label.Length, HexNote.MaxTitleLength)] };
        if (after == r) return;
        ExecuteHexLayerEdit("Слух о регионе", l, l with { Regions = l.Regions.Replace(r, after) });
    }

    private bool BeginHexParty(HexLayer l, HexCoord h)
    {
        _hexPartyDragging = true;
        MoveHexParty(h);
        return true;
    }
}
