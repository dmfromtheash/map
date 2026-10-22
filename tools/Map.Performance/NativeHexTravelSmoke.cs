using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Presentation.ViewModels;

namespace Map.Performance;

public sealed partial class RegionalApplication
{
    // R18: --profile native-hex-workshop --output artifacts/r18/smoke.json.
    // NativeRegionalSmoke задаёт изолированный профиль; живой проход отдельно: --hex-travel true.
    private async Task VerifyHexTravelR18()
    {
        HexLayer Layer() => _editor.ActiveHexLayer!;
        var l = Layer(); var board = l.Board;
        var cells = Enumerable.Range(0, board.Rows).SelectMany(row => Enumerable.Range(0, board.Columns).Select(column =>
            new KeyValuePair<HexCoord, HexCell?>(board.Layout.FromOffset(new(column, row)), HexCell.Of(row < 4 ? "starter/forest" : row > 16 ? "starter/hills" : "starter/grass"))));
        var from = board.Layout.FromOffset(new(2, 9)); var to = board.Layout.FromOffset(new(26, 9));
        var camp = HexCoord.Line(from, to).ElementAt(4);
        var river = Enumerable.Range(1, board.Rows - 2).Select(row => board.Layout.FromOffset(new(16, row))).ToImmutableArray();
        var path = HexCoord.Line(from, to).ToImmutableArray();
        var fixture = l with { Board = board.With(cells), FogEnabled = false, Party = new(), CampaignDay = 90, Journey = new(),
            Notes = System.Collections.Immutable.ImmutableDictionary<HexCoord, HexNote>.Empty,
            Paths = [new(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander, river, []),
                new(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, path, [])] };
        fixture = fixture with { Board = fixture.Board.With([new(camp, HexCell.Of("starter/grass").WithPointsOfInterest([new("starter/village", "Мельница")]))]) };
        _editor.Session.Execute(new HexLayerEditCommand("Проверочная долина R18", l, fixture)); _editor.Undo(); _editor.Redo();
        PressHex(_view!, "HexModeMeasure"); await Settle();
        T Find<T>(string name) where T : Control => _view!.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);
        Find<TextBox>("HexJourneyFromBox").Text = "0310"; Find<TextBox>("HexJourneyToBox").Text = "2710";
        PressHex(_view!, "HexJourneyBuildButton"); await Settle();
        Check("R18: маршрут и две ограниченные альтернативы из панели", _editor.HexJourneyRoutes.Count == 3
            && _editor.HexJourneyRoutes.Skip(1).All(r => r.Route.Days <= _editor.HexJourneyRoutes[0].Route.Days * 1.3 + 1e-9));
        PressHex(_view!, "HexJourneyRoute_1"); await Settle();
        Check("R18: клик переключает альтернативу", _editor.HexJourneySelected == 1);
        PressHex(_view!, "HexJourneyRoute_0"); await Settle();
        var viewport = _editor.Session.Viewport; PressHex(_view!, "HexJourneyDay_1"); await Settle();
        var centre = Layer().Board.Layout.Center(_editor.HexJourneyRoutes[0].Days[0].Camp);
        Check("R18: навигатор центрирует ночёвку", Math.Abs(centre.X - viewport.OffsetX - viewport.ViewWidth / (2 * viewport.Scale)) < .01
            && _editor.HexJourneyRoutes[0].Days[0].Description.Contains("Мельница", StringComparison.Ordinal));
        Find<CheckBox>("HexJourneyReachBox").IsChecked = true; await Settle();
        Check("R18: три кольца досягаемости", _editor.HexJourneyReach is { Limited: false, Bands.Count: 3 }
            && _editor.HexJourneyReach.Bands.All(b => b.Contours.Count > 0));
        var reachBefore = _editor.HexJourneyReach!.Costs.Count; var costBefore = _editor.HexJourneyRoutes[0].Route.Days;
        _editor.HexCampaignDay = 270; await Settle();
        Check("R18: сезон меняет стоимость и кольца", _editor.HexJourneyRoutes[0].Route.Days > costBefore && _editor.HexJourneyReach!.Costs.Count < reachBefore);
        _editor.Undo(); await Settle(); Check("R18: отмена возвращает дату и маршрут", _editor.HexCampaignDay == 90 && _editor.HexJourneyRoutes[0].Route.Days == costBefore);
        Find<ComboBox>("HexJourneyModeBox").SelectedIndex = 1; await Settle();
        RevealHex(_view!, "HexJourneyProfileSaveButton"); await Settle(); Find<TextBox>("HexJourneyMilesBox").Text = "42";
        PressHex(_view!, "HexJourneyProfileSaveButton"); await Settle();
        Check("R18: профиль редактируется из панели", _editor.HexJourney.Profile.MilesPerDay == 42);
        _editor.Undo(); await Settle(); Check("R18: профиль имеет отмену", _editor.HexJourney.Profile.MilesPerDay == 36);
        _editor.Redo(); await Settle();
        RevealHex(_view!, "HexEncounterSaveButton"); await Settle(); Find<TextBox>("HexEncounterEntriesBox").Text = "Караван\nСледы оленя";
        PressHex(_view!, "HexEncounterSaveButton"); await Settle();
        var encounter = CampaignCalendar.Encounter(Layer(), camp, 90);
        Check("R18: встреча стабильна по дню и зерну", _editor.HexJourney.Encounters.Count == 1
            && encounter == CampaignCalendar.Encounter(Layer(), camp, 90) && encounter is "Караван" or "Следы оленя");
        _editor.SetHexTravelMode(TravelMode.Walk); _editor.HexPresentation = HexPresentation.Travel;
        Find<Expander>("HexJourneyProfileExpander").IsExpanded = false; Find<Expander>("HexJourneyCalendarExpander").IsExpanded = false;
        _view!.Canvas.FitToCanvas(); await Settle();
        var file = Path.Combine(_workspace, "r18-live.mapproj");
        MapProjectStore.Save(_editor.Session.Document, file, new("r18", DateTimeOffset.UtcNow), PreviewPng());
        var opened = MapProjectStore.Open(file).Document.Layers.OfType<HexLayer>().Single(t => t.Level == 0);
        Check("R18: v21 возвращает профиль, маршрут, календарь и встречи", opened.Journey.From == from && opened.Journey.To == to
            && opened.Journey.Profiles.Single(p => p.Mode == TravelMode.Ride).MilesPerDay == 42 && opened.Journey.Encounters.Count == 1
            && opened.CampaignDay == 90 && opened.Journey.ShowReach);
        Check("R18: пакет игрока не содержит плана и таблиц ведущего", ExportPlayerVisibility.Sanitize(opened).Journey.IsDefault);
    }
}
