using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R23: a drawn land becomes a continent step by step; each step replaces only what it made before.</summary>
public sealed class ContinentGenerationTests
{
    private static (MapDocument Document, LandLayer Land) Continent()
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty < 4; ty++)
            for (var tx = 0; tx < 6; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                var any = false;
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var wx = (tx * TileMap.TileSize) + x; var wy = (ty * TileMap.TileSize) + y;
                        var angle = Math.Atan2(wy - 512, wx - 768);
                        var edge = 1 + (0.12 * Math.Sin(angle * 4)) + (0.06 * Math.Sin((angle * 9) + 1));
                        var d = Math.Sqrt(Math.Pow((wx - 768) / 620.0, 2) + Math.Pow((wy - 512) / 400.0, 2)) / edge;
                        var v = (byte)Math.Clamp((1 - d) * 255 * 30, 0, 255);
                        bytes[(y * TileMap.TileSize) + x] = v; any |= v > 0;
                    }
                if (any) tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        var land = new LandLayer(LayerId.New(), "Материк", true, false, 1, tiles, new LandEdgeStyle());
        var document = new MapDocument(DocumentId.New(), "Материк", DateTimeOffset.UnixEpoch, new CanvasDefinition(1536, 1024, "px", "#FF9DBBC4"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF9DBBC4"), land, new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        return (document, land);
    }

    private static LandLayer Land(MapDocument document, LayerId id) => (LandLayer)document.FindLayer(id)!;
    private static ObjectLayer Objects(MapDocument document) => document.Layers.OfType<ObjectLayer>().First();

    [Fact]
    public void The_steps_build_heights_climate_rivers_and_towns_on_the_drawn_land()
    {
        var (document, land) = Continent();
        var settings = new ContinentSettings { Seed = 5, RiversPerMillion = 8, TownsPerMillion = 8 };
        var heights = ContinentGenerator.Run(document, land.Id, ContinentStep.Heights, settings);
        var relief = Land(heights, land.Id).ReliefTiles;
        Assert.True(relief.TileCount > 0);
        // Higher inland than at the coast.
        Assert.True(ReliefEditor.HeightAt(relief, 768, 512) > ReliefEditor.HeightAt(relief, 768, 140));
        // Nothing over the sea.
        Assert.Equal(0, ReliefEditor.HeightAt(relief, 20, 20));
        Assert.Same(land.GeometryTiles, Land(heights, land.Id).GeometryTiles);

        var eroded = ContinentGenerator.Run(heights, land.Id, ContinentStep.Erosion, settings);
        Assert.NotSame(Land(heights, land.Id).ReliefTiles, Land(eroded, land.Id).ReliefTiles);

        var climate = ContinentGenerator.Run(eroded, land.Id, ContinentStep.Climate, settings);
        Assert.True(Land(climate, land.Id).PaintTiles.TileCount > 0);

        var rivers = ContinentGenerator.Run(climate, land.Id, ContinentStep.Rivers, settings);
        var made = Objects(rivers).Rivers;
        Assert.NotEmpty(made);
        Assert.All(made, r => { r.Validate(); Assert.Equal(ContinentGenerator.RiverReason, r.GenerationReason); });

        var towns = ContinentGenerator.Run(rivers, land.Id, ContinentStep.Settlements, settings);
        var labels = Objects(towns).Objects.Where(o => o.GenerationReason == ContinentGenerator.TownReason).ToList();
        Assert.True(labels.Count >= 2, $"{labels.Count} towns");
        Assert.Equal(labels.Count, labels.Select(l => l.Text!.Content).Distinct().Count());
        Assert.All(labels, l => Assert.True(ReliefEditor.HeightAt(land.GeometryTiles, (int)(l.X + l.Width / 2), (int)(l.Y + l.Height / 2)) >= 128));
        Assert.NotEmpty(Objects(towns).Roads);
        Assert.All(Objects(towns).Roads, r => Assert.Equal(ContinentGenerator.RoadReason, r.GenerationReason));
    }

    [Fact]
    public void A_step_run_again_replaces_its_own_objects_and_keeps_the_hand_drawn_ones()
    {
        var (document, land) = Continent();
        var hand = new River { Name = "Своя", Channels = [RiverEditor.NewChannel([new(100, 900), new(300, 950)], 6)] };
        var objects = Objects(document) with { Rivers = [hand] };
        document = document.WithLayers([.. document.Layers.Select(l => l.Id == objects.Id ? objects : l)]);
        var settings = new ContinentSettings { Seed = 9, RiversPerMillion = 8 };
        var heights = ContinentGenerator.Run(document, land.Id, ContinentStep.Heights, settings);
        var once = ContinentGenerator.Run(heights, land.Id, ContinentStep.Rivers, settings);
        var twice = ContinentGenerator.Run(once, land.Id, ContinentStep.Rivers, settings with { Seed = 9 });
        Assert.Equal(Objects(once).Rivers.Length, Objects(twice).Rivers.Length);
        Assert.Contains(Objects(twice).Rivers, r => r.Id == hand.Id);
        Assert.Throws<InvalidOperationException>(() => ContinentGenerator.Run(document, LayerId.New(), ContinentStep.Heights, settings));
    }
}
