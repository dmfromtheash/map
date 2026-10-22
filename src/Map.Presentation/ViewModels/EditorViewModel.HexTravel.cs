using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private HexLayer? _journeySource;
    private IReadOnlyList<JourneyRoute> _journeyRoutes = [];
    private TravelReach? _journeyReach;
    private int _journeySelected;
    private bool? _journeyPick;
    public HexTravelSettings HexJourney => ActiveHexLayer?.Journey ?? new();
    public IReadOnlyList<JourneyRoute> HexJourneyRoutes { get { RefreshHexJourney(); return _journeyRoutes; } }
    public int HexJourneySelected => _journeySelected;
    public TravelReach? HexJourneyReach => _journeyReach;
    public string HexJourneyDate => CampaignCalendar.Date(HexCampaignDay);
    public string HexJourneySummary => _journeyRoutes.Count == 0 ? "Выберите начало и конец пути."
        : _journeyRoutes[_journeySelected].Route.Label + " · Усталость: " + _journeyRoutes[_journeySelected].Fatigue;
    public string HexJourneyNumber(HexCoord? h) => h is { } hex && ActiveHexLayer is { } l
        ? HexLayerRenderer.NumberOf(l.Board, l.Board.Layout.ToOffset(hex)) : "";
    public bool SetHexJourneyNumber(bool from, string text)
    {
        if (ActiveHexLayer is not { } l) return false;
        var parts = text.Trim().Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int column, row;
        if (parts.Length == 2 && int.TryParse(parts[0], out column) && int.TryParse(parts[1], out row)) { }
        else if (text.Trim().Length == 4 && int.TryParse(text.Trim()[..2], out column) && int.TryParse(text.Trim()[2..], out row)) { }
        else { StatusDetail = "Введите номер гекса, например 0308, или колонку и ряд через запятую."; return false; }
        var hex = l.Board.Layout.FromOffset(new HexOffset(column - 1, row - 1));
        if (!l.Board.Contains(hex)) { StatusDetail = "Этот гекс за пределами карты."; return false; }
        SetHexJourney(from ? l.Journey with { From = hex } : l.Journey with { To = hex }); return true;
    }
    public void PickHexJourneyEndpoint(bool from)
    {
        _journeyPick = from; HexMode = HexToolMode.Measure;
        StatusDetail = from ? "Щёлкните по началу пути." : "Щёлкните по концу пути.";
    }
    public void SetHexJourney(HexTravelSettings settings)
    {
        if (ActiveHexLayer is not { Locked: false } l || settings == l.Journey) return;
        ExecuteHexLayerEdit("Параметры путешествия", l, l with { Journey = settings });
    }
    public void SetHexTravelMode(TravelMode mode) => SetHexJourney(HexJourney with { Mode = mode });
    public void SetHexTravelProfile(double miles, IReadOnlyDictionary<string, double> costs)
    {
        if (!double.IsFinite(miles) || miles is < .1 or > 100_000 || costs.Count > 256
            || costs.Any(p => p.Key.Length > 200 || !double.IsFinite(p.Value) || p.Value is < .1 or > 100))
        { StatusDetail = "За день: 0,1–100000 миль; стоимость местности: 0,1–100."; return; }
        var settings = HexJourney; var before = settings.Profile;
        SetHexJourney(settings with { Profiles = settings.Profiles.Replace(before, new(before.Mode, miles, costs.ToImmutableDictionary())) });
    }
    public void SetHexEncounterTable(string terrain, int region, IEnumerable<string> entries)
    {
        if (ActiveHexLayer is not { } l || region < 0 || region != 0 && l.Region(region) is null) return;
        var lines = entries.Select(s => s.Trim()).Where(s => s.Length != 0).ToArray();
        if (lines.Length > 100 || lines.Any(s => s.Length > 1000)) { StatusDetail = "В таблице до 100 встреч по 1000 знаков."; return; }
        var tables = l.Journey.Encounters.RemoveAll(t => t.Terrain == terrain && t.Region == region);
        if (lines.Length > 0) tables = tables.Add(new(terrain, region, [.. lines]));
        if (tables.Count > 256) return;
        SetHexJourney(l.Journey with { Encounters = tables });
    }
    public void SelectHexJourney(int index)
    {
        if (index < 0 || index >= _journeyRoutes.Count) return;
        _journeySelected = index; _hexRoute = _journeyRoutes[index].Route;
        Raise(nameof(HexJourneySelected)); Raise(nameof(HexJourneySummary)); Raise(nameof(HexRoute)); Raise(nameof(HexOverlay));
    }
    public void CenterHexJourneyDay(int index)
    {
        if (_journeyRoutes.Count == 0 || index < 0 || index >= _journeyRoutes[_journeySelected].Days.Count || ActiveHexLayer is not { } l) return;
        var h = _journeyRoutes[_journeySelected].Days[index].Camp; var (x, y) = l.Board.Layout.Center(h); var viewport = Session.Viewport;
        viewport.Set(x - viewport.ViewWidth / (2 * viewport.Scale), y - viewport.ViewHeight / (2 * viewport.Scale), viewport.Scale);
        Raise(nameof(ViewportStatus)); SceneChanged?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshHexJourney()
    {
        var l = ActiveHexLayer;
        if (ReferenceEquals(l, _journeySource)) return;
        _journeySource = l; _journeyRoutes = []; _journeyReach = null; _journeySelected = 0;
        if (l is not null && l.Journey.From is { } from)
        {
            var planner = new HexJourneyPlanner(l, TravelCostOf, id => HexStarterTiles.Find(id)?.IsWater == true);
            if (l.Journey.To is { } to) _journeyRoutes = planner.Routes(from, to);
            if (l.Journey.ShowReach) _journeyReach = planner.Reach(from);
        }
        _hexRoute = _journeyRoutes.FirstOrDefault()?.Route;
        foreach (var name in new[] { nameof(HexJourney), nameof(HexJourneyRoutes), nameof(HexJourneyReach), nameof(HexJourneySelected),
            nameof(HexJourneyDate), nameof(HexJourneySummary), nameof(HexRoute), nameof(HexOverlay), nameof(HexCampaignDay) }) Raise(name);
    }
}
