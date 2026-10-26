using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// R28: a parent's river and road carried into a child - clipped at the child's edge, scaled, kept
/// in step with later parent edits, a hand-edited copy left alone, a vanished source removed; and
/// hexes of a hex child taken from the painted parent.
/// </summary>
public sealed class ScaleSyncTests
{
    private static readonly AtlasTransform Area = new(200, 200, 400, 300, 1600, 1200);

    [Fact]
    public void A_river_and_a_road_are_carried_clipped_and_scaled()
    {
        var road = RoadEditor.NewRoad([new(0, 350), new(1000, 350)], 12, RoadStyle.Country(), "Тракт");
        var river = new River { Name = "Сельва", Channels = [RiverEditor.NewChannel([new(400, 0), new(400, 1000)], 8)] };
        var parent = Doc(1000, 1000, road, river);
        var child = Doc(1600, 1200);
        var plan = ScaleSync.Plan(parent, child, Area);
        Assert.Equal((1, 1, 0), (plan.AddRoads.Count, plan.AddRivers.Count, plan.KeptByHand.Count));
        var copy = plan.AddRoads[0];
        // The road crosses the whole area at y 350 → child y 600, from the left edge to the right edge.
        Assert.Equal(0, copy.Nodes[0].X, 1);
        Assert.Equal(1600, copy.Nodes[^1].X, 1);
        Assert.All(copy.Nodes, n => Assert.Equal(600, n.Y, 1));
        Assert.Equal(parent.Id.Value, copy.Inherited!.ParentMap);
        // The river meets the top and bottom edges exactly where the parent has it: x 400 → 800.
        var channel = plan.AddRivers[0].Channels[0];
        Assert.Equal((800, 0), (channel.Nodes[0].X, channel.Nodes[0].Y));
        Assert.Equal(1200, channel.Nodes[^1].Y, 1);
        Assert.Equal(32, channel.Nodes[0].Width, 1);

        var synced = ScaleSync.Apply(child, plan);
        Assert.Contains(synced.Layers, l => l is ObjectLayer { Name: ScaleSync.LayerName });
        Assert.Equal(0, ScaleSync.Plan(parent, synced, Area).Changes);
    }

    [Fact]
    public void Parent_edits_update_copies_but_not_hand_edited_ones_and_vanished_sources_go()
    {
        var road = RoadEditor.NewRoad([new(0, 350), new(1000, 350)], 12, RoadStyle.Country(), "Тракт");
        var river = new River { Name = "Сельва", Channels = [RiverEditor.NewChannel([new(400, 0), new(400, 1000)], 8)] };
        var parent = Doc(1000, 1000, road, river);
        var child = ScaleSync.Apply(Doc(1600, 1200), ScaleSync.Plan(parent, Doc(1600, 1200), Area));

        // The road is edited by hand in the child.
        var copy = child.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).Single();
        var handEdited = copy with { Nodes = [copy.Nodes[0] with { Y = 650 }, .. copy.Nodes.Skip(1)] };
        child = child.WithLayers([.. child.Layers.Select(l => l is ObjectLayer o ? o with { Roads = [.. o.Roads.Select(r => r.Id == copy.Id ? handEdited : r)] } : l)]);

        // The parent moves the river and the road; the river stays, the road is renamed.
        var movedRiver = river with { Channels = [RiverEditor.NewChannel([new(450, 0), new(450, 1000)], 8)] };
        parent = parent.WithLayers(Doc(1000, 1000, road with { Name = "Новый тракт" }, movedRiver).Layers);
        var plan = ScaleSync.Plan(parent, child, Area);
        Assert.Single(plan.UpdateRivers);
        Assert.Empty(plan.UpdateRoads);
        Assert.Contains(plan.KeptByHand, k => k.Contains("Тракт", StringComparison.Ordinal));

        // The river leaves the parent: its untouched copy goes.
        var gone = ScaleSync.Plan(parent.WithLayers(Doc(1000, 1000, road).Layers), ScaleSync.Apply(child, plan), Area);
        Assert.Single(gone.RemoveRivers);
    }

    [Fact]
    public void Hexes_take_the_terrain_painted_under_them()
    {
        // Land on the left half of the parent's area; nothing on the right.
        var land = LandLayer.CreateEmpty(LayerId.New(), "Суша") with
        { GeometryTiles = TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, 256 * 256).ToArray()) };
        var parent = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 1024, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF8FB0BA"), land]);
        var hexes = new HexLayer(LayerId.New(), "Гексы", true, false, 1, HexBoard.Create(HexOrientation.Flat, 40, 6, 4));
        var transform = new AtlasTransform(0, 0, 512, 256, hexes.Board.PixelWidth, hexes.Board.PixelHeight);
        var result = ScaleSync.HexesFromArt(hexes, parent, transform, _ => false);
        var left = result.Board[result.Board.Layout.FromOffset(new HexOffset(0, 1))]!;
        var right = result.Board[result.Board.Layout.FromOffset(new HexOffset(5, 1))]!;
        Assert.Equal("starter/grass", left.Terrain);
        Assert.Equal("starter/sea", right.Terrain);
    }

    [Fact]
    public void The_coast_comes_into_a_city_and_the_town_keeps_to_the_land()
    {
        // Land is the left 256 px of the parent; the city area straddles that shore.
        var land = LandLayer.CreateEmpty(LayerId.New(), "Суша") with
        { GeometryTiles = TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, 256 * 256).ToArray()) };
        var parent = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 1024, "px", "#FF8FB0BA"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF8FB0BA"), land]);
        var region = new AtlasTransform(100, 20, 200, 200, 2400, 2400);
        var coast = ScaleSync.Coast(parent, region, 2400, 2400);
        Assert.NotNull(coast);
        // The parent's shore at x = 256 is x = (256 - 100) * 12 = 1872 in the child.
        Assert.Equal(255, TerrainOrder.CoverageAt(coast, 1500, 1200));
        Assert.Equal(0, TerrainOrder.CoverageAt(coast, 2200, 1200));
        Assert.InRange(Enumerable.Range(1800, 150).First(x => TerrainOrder.CoverageAt(coast, x, 600) < 128), 1860, 1885);
        Assert.Null(ScaleSync.Coast(parent, new AtlasTransform(600, 600, 200, 200, 2400, 2400), 2400, 2400));

        var city = CityBuilder.NewCityMap("Пристань", 2400, new CitySettings { Seed = 5, Port = true }, new MapCategoryId("city"), DateTimeOffset.UnixEpoch, coast, "#FF8FB0BA");
        Assert.Contains(city.Layers, l => l is LandLayer);
        var objects = city.Layers.OfType<ObjectLayer>().Single().Objects;
        var houses = objects.Where(o => o.Name == "Дом").ToList();
        Assert.True(houses.Count > 50);
        Assert.All(houses, h => Assert.True(h.X + h.Width < 1885, "a house stands in the water"));
        var piers = objects.Where(o => o.Name == "Пирс").ToList();
        Assert.NotEmpty(piers);
        Assert.All(piers, p => Assert.InRange(p.X + (p.Width / 2), 1800, 2100));
        Assert.DoesNotContain(objects, o => o.Name == "Гавань");
    }

    private static MapDocument Doc(int width, int height, Road? road = null, River? river = null) =>
        new(DocumentId.New(), "Карта", DateTimeOffset.UnixEpoch, new CanvasDefinition(width, height, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Roads = road is null ? [] : [road], Rivers = river is null ? [] : [river] }]);
}
