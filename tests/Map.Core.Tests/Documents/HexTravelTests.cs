using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public sealed class HexTravelTests
{
    private static HexLayer Layer(int columns = 28, int rows = 24)
    {
        var board = HexBoard.Create(HexOrientation.Flat, 40, columns, rows);
        return new(LayerId.New(), "Гексы", true, false, 1, board) { CampaignDay = 90 };
    }
    private static HexJourneyPlanner Planner(HexLayer l) => new(l, id => id == "water" ? null : id == "mountains" ? 3 : id == "forest" ? 2 : 1, id => id == "water");

    [Fact]
    public void Route_has_two_distinct_bounded_alternatives_and_no_loops()
    {
        var l = Layer(); var a = l.Board.Layout.FromOffset(new(2, 10)); var b = l.Board.Layout.FromOffset(new(24, 10));
        var routes = Planner(l).Routes(a, b);
        Assert.Equal(3, routes.Count);
        foreach (var r in routes)
        {
            Assert.Equal(a, r.Route.Hexes[0]); Assert.Equal(b, r.Route.Hexes[^1]);
            Assert.Equal(r.Route.Hexes.Count, r.Route.Hexes.Distinct().Count());
            Assert.True(r.Route.Days <= routes[0].Route.Days * 1.3 + 1e-9);
            Assert.True(r.Route.Distance <= routes[0].Route.Distance * 1.3 + 1e-9);
        }
        Assert.True(HexJourneyPlanner.Overlap(routes[0].Route, routes[1].Route) < .6);
        Assert.True(HexJourneyPlanner.Overlap(routes[0].Route, routes[2].Route) < .6);
        Assert.True(HexJourneyPlanner.Overlap(routes[1].Route, routes[2].Route) < .6);
    }
    [Fact]
    public void Road_improves_one_tier_and_cart_has_offroad_penalty()
    {
        var l = Layer(); var a = new HexCoord(2, 2); var b = a.Neighbor(0);
        l = l with { Board = l.Board.With([new(b, HexCell.Of("mountains"))]) };
        var cost = Planner(l).StepCost(a, b)!.Value;
        var road = new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, [a, b], []);
        Assert.Equal(cost * 2 / 3, Planner(l with { Paths = [road] }).StepCost(a, b)!.Value, 9);
        var cart = l with { Journey = l.Journey with { Mode = TravelMode.Cart } };
        Assert.Equal(cost * 4, Planner(cart).StepCost(a, b)!.Value, 9); // 24/18 × 3
    }
    [Fact]
    public void Boat_only_follows_water_or_river_edges()
    {
        var l = Layer(); var a = new HexCoord(2, 2); var b = a.Neighbor(0);
        l = l with { Journey = l.Journey with { Mode = TravelMode.Boat } };
        Assert.Null(Planner(l).StepCost(a, b));
        var river = new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander, [a, b], []);
        Assert.NotNull(Planner(l with { Paths = [river] }).StepCost(a, b));
        l = l with { Board = l.Board.With([new(a, HexCell.Of("water")), new(b, HexCell.Of("water"))]) };
        Assert.NotNull(Planner(l).StepCost(a, b));
        Assert.Null(Planner(l with { Journey = new() }).StepCost(a, b));
    }
    [Fact]
    public void Road_and_bridge_edit_change_route_cost_and_reach()
    {
        var l = Layer(); var a = new HexCoord(2, 2); var path = Enumerable.Range(0, 9).Select(i => new HexCoord(2 + i, 2)).ToImmutableArray();
        l = l with { Board = l.Board.With(Enumerable.Range(0, l.Board.Rows).SelectMany(row => Enumerable.Range(0, l.Board.Columns)
            .Select(column => new KeyValuePair<HexCoord, HexCell?>(l.Board.Layout.FromOffset(new(column, row)), HexCell.Of("mountains"))))) };
        var planner = Planner(l); var before = planner.Routes(a, path[^1])[0].Route.Days; var reach = planner.Reach(a);
        var road = new HexPath(HexPathId.New(), HexPathKind.Road, HexRouteStyle.Centers, path, []);
        var river = new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander, [path[1], path[2]], []);
        var changed = Planner(l with { Paths = [road, river] });
        Assert.True(changed.Routes(a, path[^1])[0].Route.Days < before);
        Assert.True(changed.Reach(a).Costs[path[2]] < reach.Costs[path[2]]);
        Assert.True(Planner(l with { Paths = [river] }).StepCost(path[1], path[2]) > changed.StepCost(path[1], path[2]));
    }
    [Theory]
    [InlineData(HexOrientation.Flat)] [InlineData(HexOrientation.Pointy)]
    public void Reach_has_three_nonoverlapping_contour_bands(HexOrientation orientation)
    {
        var l = Layer() with { Board = HexBoard.Create(orientation, 40, 28, 24) };
        var r = Planner(l).Reach(l.Board.Layout.FromOffset(new(10, 10)));
        Assert.False(r.Limited); Assert.Equal(3, r.Bands.Count);
        Assert.All(r.Bands, b => Assert.NotEmpty(b.Contours));
        Assert.All(r.Costs.Values, c => Assert.InRange(c, 0, 3));
        static double Area(IReadOnlyList<VectorPoint> p) => Math.Abs(Enumerable.Range(0, p.Count).Sum(i => p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y) / 2);
        var expected = r.Costs.Count * 3 * Math.Sqrt(3) / 2 * 40 * 40;
        // Signed ring area subtracts the inner holes; no band covers the same hex twice.
        var total = r.Bands.Sum(b => Math.Abs(b.Contours.Sum(p => Enumerable.Range(0, p.Count).Sum(i => p[i].X * p[(i + 1) % p.Count].Y - p[(i + 1) % p.Count].X * p[i].Y) / 2)));
        Assert.Equal(expected, total, 3);
        Assert.True(Area(r.Bands[0].Contours[0]) > 0);
    }
    [Fact]
    public void Navigation_snaps_camp_to_named_poi_within_ten_percent()
    {
        var l = Layer(); var a = new HexCoord(2, 2); var b = new HexCoord(14, 2); var camp = new HexCoord(6, 2);
        l = l with { Board = l.Board.With([new(camp, HexCell.Of("grass").WithPointsOfInterest([new HexPoi("village", "Мельница")]))]) };
        var r = Planner(l).Routes(a, b)[0];
        Assert.Equal(camp, r.Days[0].Camp); Assert.Contains("Мельница", r.Days[0].Description);
        Assert.Equal(r.Route.Distance, r.Days.Sum(d => d.Miles));
    }
    [Fact]
    public void Nearby_offroute_camp_is_included_in_geometry_distance_and_daily_navigation()
    {
        var l = Layer(60, 60); var a = new HexCoord(2, 2); var b = new HexCoord(47, 2); var camp = new HexCoord(22, 3);
        l = l with { Travel = new HexTravelScale(.2), Journey = l.Journey with
        { Profiles = l.Journey.Profiles.Replace(l.Journey.Profile, l.Journey.Profile with { MilesPerDay = 4 }) },
            Board = l.Board.With([new(camp, HexCell.Of("grass").WithPointsOfInterest([new HexPoi("village", "Приют")]))]) };
        var r = Planner(l).Routes(a, b)[0];
        Assert.Contains(camp, r.Route.Hexes); Assert.Contains(r.Days, d => d.Camp == camp);
        Assert.True(r.Route.Distance > HexCoord.Distance(a, b) * .2);
        Assert.Equal(r.Route.Distance, r.Days.Sum(d => d.Miles), 8);
    }
    [Fact]
    public void Journey_endpoints_follow_insertions_and_are_dropped_by_crop()
    {
        var l = Layer(); var a = l.Board.Layout.FromOffset(new(2, 2)); var b = l.Board.Layout.FromOffset(new(20, 2));
        l = l with { Journey = l.Journey with { From = a, To = b } };
        var moved = HexBoardOps.InsertColumns(l, 1, 2);
        Assert.Equal(new HexOffset(4, 2), moved.Board.Layout.ToOffset(moved.Journey.From!.Value));
        var cropped = HexBoardOps.Resize(moved, 10, 10);
        Assert.NotNull(cropped.Journey.From); Assert.Null(cropped.Journey.To);
    }
    [Fact]
    public void Calendar_season_weather_and_encounters_are_stable()
    {
        var l = Layer(); var h = new HexCoord(2, 2); var b = h.Neighbor(0);
        l = l with { Board = l.Board.With([new(b, HexCell.Of("mountains"))]), Journey = l.Journey with
        { Encounters = [new("mountains", 0, ["Следы великана", "Караван"])] } };
        Assert.Equal(CampaignSeason.Winter, CampaignCalendar.Season(270));
        Assert.Equal(CampaignSeason.Spring, CampaignCalendar.Season(360));
        Assert.Equal(Planner(l).StepCost(h, b)!.Value * 1.5, Planner(l with { CampaignDay = 270 }).StepCost(h, b)!.Value);
        Assert.Equal(CampaignCalendar.Encounter(l, b, 123), CampaignCalendar.Encounter(l, b, 123));
        Assert.Contains(CampaignCalendar.Encounter(l, b, 123), new[] { "Следы великана", "Караван" });
        Assert.Equal(CampaignCalendar.Weather(l, b, 123), CampaignCalendar.Weather(l, b, 123));
    }
    [Fact]
    public void Player_layer_never_contains_journey_or_encounter_secrets()
    {
        var l = Layer(); l = l with { Journey = l.Journey with { From = new(1, 1), To = new(8, 1), SecretRoute = true,
            Encounters = [new("", 0, ["Секретная засада"])] } };
        Assert.True(ExportPlayerVisibility.Sanitize(l).Journey.IsDefault);
    }
}
