using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R24: roads A→B over the land, rivers down to the sea, and the geography check with «намеренно».</summary>
public sealed class GeographyTests
{
    /// <summary>A 1024×768 island with a raised plateau (level 1) in its east half, its heights rising to the west.</summary>
    private static (MapDocument Document, LandLayer Island, LandLayer Plateau, ObjectLayer Objects) World()
    {
        TileMap Coverage(Func<int, int, bool> inside)
        {
            var tiles = TileMap.Empty(1);
            for (var ty = 0; ty < 3; ty++)
                for (var tx = 0; tx < 4; tx++)
                {
                    var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                    var any = false;
                    for (var y = 0; y < TileMap.TileSize; y++)
                        for (var x = 0; x < TileMap.TileSize; x++)
                            if (inside((tx * TileMap.TileSize) + x, (ty * TileMap.TileSize) + y)) { bytes[(y * TileMap.TileSize) + x] = 255; any = true; }
                    if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
                }
            return tiles;
        }
        var heights = TileMap.Empty(1);
        for (var ty = 0; ty < 3; ty++)
            for (var tx = 0; tx < 4; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                        bytes[(y * TileMap.TileSize) + x] = (byte)Math.Clamp(255 - (((tx * TileMap.TileSize) + x) / 3), 0, 255);
                heights = heights.With(new TileKey(tx, ty), bytes);
            }
        var island = new LandLayer(LayerId.New(), "Остров", true, false, 1,
            Coverage((x, y) => x is > 60 and < 960 && y is > 60 and < 700), new LandEdgeStyle()) { ReliefTiles = heights };
        var plateau = new LandLayer(LayerId.New(), "Плато", true, false, 1,
            Coverage((x, y) => x is > 600 and < 900 && y is > 200 and < 560), new LandEdgeStyle()) { Level = 1 };
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "География", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", "#FF9DBBC4"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF9DBBC4"), island, plateau, objects]);
        return (document, island, plateau, objects);
    }

    [Fact]
    public void A_road_goes_round_a_cliff_and_never_over_the_sea()
    {
        var (document, _, _, _) = World();
        var probe = new TerrainProbe(document);
        // From the west of the island to its south-east corner, past the plateau.
        var route = Geography.RouteRoad(document, new VectorPoint(200, 380), new VectorPoint(930, 650));
        Assert.NotNull(route);
        for (var k = 1; k < route!.Count; k++)
            for (var t = 0.0; t <= 1; t += 0.05)
            {
                var x = route[k - 1].X + ((route[k].X - route[k - 1].X) * t);
                var y = route[k - 1].Y + ((route[k].Y - route[k - 1].Y) * t);
                var (land, _) = probe.At(x, y);
                Assert.NotNull(land);
                Assert.Equal(0, land!.Level);
            }
        // Into the sea there is no way.
        Assert.Null(Geography.RouteRoad(document, new VectorPoint(200, 380), new VectorPoint(1000, 740)));
    }

    [Fact]
    public void A_river_from_a_point_runs_down_to_the_sea()
    {
        var (document, _, _, _) = World();
        var route = Geography.RouteRiver(document, new VectorPoint(150, 380));
        Assert.NotNull(route);
        var probe = new TerrainProbe(document);
        Assert.Null(probe.At(route![^1].X, route[^1].Y).Land);
        Assert.True(route.Count >= 3);
    }

    [Fact]
    public void The_check_finds_an_uphill_river_a_closed_lake_and_a_road_into_a_cliff_and_spares_what_is_intentional()
    {
        var (document, _, _, objects) = World();
        // East (low) to west (high): uphill.
        var uphill = new River { Name = "Вверх", Channels = [RiverEditor.NewChannel([new(500, 650), new(120, 650)], 6)] };
        var lake = new River { Name = "Озеро", Channels = [RiverEditor.NewLake([new(300, 300), new(360, 300), new(330, 350)])] };
        var road = RoadEditor.NewRoad([new(400, 380), new(750, 380)], 3);
        var layer = objects with { Rivers = [uphill, lake], Roads = [road] };
        document = document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? layer : l)]);
        var issues = Geography.Inspect(document);
        Assert.Contains(issues, i => i.Kind == GeographyIssueKind.RiverUphill && i.ObjectId == uphill.Id);
        Assert.Contains(issues, i => i.Kind == GeographyIssueKind.LakeWithoutOutflow && i.ObjectId == lake.Id);
        Assert.Contains(issues, i => i.Kind == GeographyIssueKind.RoadIntoCliff && i.ObjectId == road.Id);

        var intentional = layer with { Rivers = [uphill with { GeographyIntentional = true }, lake], Roads = [road with { GeographyIntentional = true }] };
        var spared = Geography.Inspect(document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? intentional : l)]));
        Assert.DoesNotContain(spared, i => i.ObjectId == uphill.Id || i.ObjectId == road.Id);
        Assert.Contains(spared, i => i.ObjectId == lake.Id);
    }

    [Fact]
    public void A_town_placed_by_hand_is_checked_for_a_road_like_a_generated_one()
    {
        var (document, _, _, objects) = World();
        var house = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Деревня", 300, 300, 40, 40, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(new string('b', 64)) };
        var layer = objects with { Objects = [house] };
        document = document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? layer : l)]);

        // Without knowing the role of the asset the hand-placed house is not a settlement.
        Assert.DoesNotContain(Geography.Inspect(document), i => i.Kind == GeographyIssueKind.TownWithoutRoad);
        var isTown = (SceneObject o) => o.Id == house.Id;
        var issue = Assert.Single(Geography.Inspect(document, settlement: isTown), i => i.Kind == GeographyIssueKind.TownWithoutRoad);
        Assert.Equal(house.Id.Value, issue.ObjectId);

        // A road reaching it, or «намеренно», ends the finding.
        var road = RoadEditor.NewRoad([new(150, 320), new(310, 320)], 3);
        var reached = document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? layer with { Roads = [road] } : l)]);
        Assert.DoesNotContain(Geography.Inspect(reached, settlement: isTown), i => i.Kind == GeographyIssueKind.TownWithoutRoad);
        var alone = document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? layer with { Objects = [house with { GeographyIntentional = true }] } : l)]);
        Assert.DoesNotContain(Geography.Inspect(alone, settlement: isTown), i => i.Kind == GeographyIssueKind.TownWithoutRoad);
    }
}
