using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Core.Resources;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class HexTravelTests
{
    private static EditorViewModel Editor()
    {
        var d = HexMapFactory.Create("Путь", DateTimeOffset.UnixEpoch, new("hex"), HexOrientation.Flat, 40, 26, 20, "#FFFFFFFF");
        return new(d, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
            memory: new WorkingMemoryBudget(() => new(16 * 1024 * WorkingMemoryBudget.MiB, 8 * 1024 * WorkingMemoryBudget.MiB, 128 * WorkingMemoryBudget.MiB)));
    }
    [Fact]
    public void Profile_route_encounters_and_calendar_are_undoable_and_navigation_centers()
    {
        var e = Editor(); e.HexCampaignDay = 90;
        Assert.True(e.SetHexJourneyNumber(true, "0310")); Assert.True(e.SetHexJourneyNumber(false, "2310"));
        var routes = e.HexJourneyRoutes; Assert.Equal(3, routes.Count);
        e.SelectHexJourney(1); Assert.Equal(routes[1].Route, e.HexRoute);
        e.CenterHexJourneyDay(0); var camp = e.ActiveHexLayer!.Board.Layout.Center(routes[1].Days[0].Camp);
        var v = e.Session.Viewport; Assert.Equal(camp.X, v.OffsetX + v.ViewWidth / (2 * v.Scale), 5);
        e.SetHexTravelMode(TravelMode.Ride); e.SetHexTravelProfile(42, new Dictionary<string, double> { ["starter/forest"] = 1.4 });
        Assert.Equal(42, e.HexJourney.Profile.MilesPerDay); e.Undo(); Assert.Equal(36, e.HexJourney.Profile.MilesPerDay); e.Redo();
        e.SetHexEncounterTable("", 0, ["Странник", "Нет встречи"]); Assert.Single(e.HexJourney.Encounters);
        e.Undo(); Assert.Empty(e.HexJourney.Encounters); e.Redo();
        var before = e.HexJourneyRoutes[0].Route.Days; e.HexCampaignDay = 270;
        Assert.Contains("зима", e.HexJourneyDate); Assert.True(e.HexJourneyRoutes[0].Route.Days > before);
        e.Undo(); Assert.Equal(90, e.HexCampaignDay); Assert.Equal(before, e.HexJourneyRoutes[0].Route.Days);
    }
    [Fact]
    public void Road_edit_recalculates_routes_reach_and_undo_restores_both()
    {
        var e = Editor(); e.HexCampaignDay = 90;
        e.SetHexJourneyNumber(true, "0310"); e.SetHexJourneyNumber(false, "2310");
        e.SetHexJourney(e.HexJourney with { Mode = TravelMode.Cart, ShowReach = true });
        var l = e.ActiveHexLayer!; var from = e.HexJourney.From!.Value; var path = HexCoord.Line(from, e.HexJourney.To!.Value).ToImmutableArray();
        var old = e.HexJourneyRoutes[0].Route.Days; var reach = e.HexJourneyReach!.Costs;
        e.Session.Execute(new HexLayerEditCommand("Дорога", l, l with { Paths = [new(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, path, [])] }));
        // Undo/Redo uses the same public refresh pipeline as every road command.
        e.Undo(); e.Redo(); Assert.True(e.HexJourneyRoutes[0].Route.Days < old);
        Assert.True(e.HexJourneyReach!.Costs.Count > reach.Count);
        e.Undo(); Assert.Equal(old, e.HexJourneyRoutes[0].Route.Days); Assert.Equal(reach.Count, e.HexJourneyReach!.Costs.Count);
    }
}
