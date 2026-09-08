using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

/// <summary>
/// Land and water share one level band, -10..+10, and the terrain is drawn level by level:
/// water lies on land of its own level or below and passes under land of a higher level.
/// </summary>
public sealed class TerrainOrderTests
{
    private static LandLayer Land(string name, int level, bool full)
    {
        var tiles = TileMap.Empty(1);
        if (full)
        {
            var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
            tiles = tiles.With(new TileKey(0, 0), coverage);
        }
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, new LandEdgeStyle()) { Level = level };
    }

    private static MapDocument Document(params MapLayer[] layers) => new(
        DocumentId.New(), "terrain", DateTimeOffset.UnixEpoch, new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), .. layers]);

    [Fact]
    public void Lands_are_ordered_by_level_then_by_stack_position()
    {
        var low = Land("низ", -2, false);
        var first = Land("первый", 0, false);
        var second = Land("второй", 0, false);
        var high = Land("верх", 3, false);
        var document = Document(high, second, low, first);

        Assert.Equal(["низ", "второй", "первый", "верх"], TerrainOrder.Lands(document).Select(l => l.Name).ToArray());
    }

    [Fact]
    public void Water_lies_on_land_at_its_level_or_below_and_under_higher_land()
    {
        var river = new River { Channels = [RiverEditor.NewChannel([new(0, 0), new(100, 0)], 10)], Level = 1 };
        Assert.True(TerrainOrder.Covers(river, Land("a", 0, false)));
        Assert.True(TerrainOrder.Covers(river, Land("b", 1, false)));
        Assert.False(TerrainOrder.Covers(river, Land("c", 2, false)));
    }

    [Fact]
    public void The_land_under_a_point_is_the_topmost_visible_piece_by_terrain_order()
    {
        var lower = Land("нижняя", 0, true);
        var upper = Land("верхняя", 2, true);
        var hidden = new LandLayer(LayerId.New(), "скрытая", false, false, 1, upper.GeometryTiles, new LandEdgeStyle()) { Level = 5 };
        var document = Document(upper, lower, hidden);

        Assert.Equal("верхняя", TerrainOrder.LandAt(document, 10, 10)?.Name);
        Assert.Null(TerrainOrder.LandAt(document, 400, 400));
        Assert.Equal(new WorldBounds(0, 0, TileMap.TileSize, TileMap.TileSize), TerrainOrder.Bounds(lower));
        Assert.Null(TerrainOrder.Bounds(Land("пусто", 0, false)));
    }

    [Fact]
    public void A_river_level_outside_the_band_is_rejected()
    {
        var river = new River { Channels = [RiverEditor.NewChannel([new(0, 0), new(100, 0)], 10)], Level = 11 };
        Assert.Throws<ArgumentException>(river.Validate);
        (river with { Level = -10 }).Validate();
    }

    [Fact]
    public void Reordering_rivers_changes_only_their_order_and_reverts_exactly()
    {
        var a = new River { Name = "a", Channels = [RiverEditor.NewChannel([new(0, 0), new(100, 0)], 10)] };
        var b = new River { Name = "b", Channels = [RiverEditor.NewChannel([new(0, 50), new(100, 50)], 10)] };
        var c = new River { Name = "c", Channels = [RiverEditor.NewChannel([new(0, 90), new(100, 90)], 10)] };
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [a, b, c] };
        var document = Document(layer);

        var command = new ReorderRiversCommand(layer.Id, [c.Id, a.Id], "test");
        var after = command.Apply(document);
        var order = ((ObjectLayer)after.FindLayer(layer.Id)!).Rivers.Select(r => r.Name).ToArray();
        Assert.Equal(["c", "a", "b"], order);

        var reverted = command.Revert(after);
        Assert.Equal(["a", "b", "c"], ((ObjectLayer)reverted.FindLayer(layer.Id)!).Rivers.Select(r => r.Name).ToArray());
        Assert.Same(a, ((ObjectLayer)reverted.FindLayer(layer.Id)!).Rivers[0]);
    }

    [Fact]
    public void The_land_level_command_sets_and_restores_the_level()
    {
        var land = Land("земля", 0, false);
        var document = Document(land);
        var command = UpdateLayerCommand.SetLandLevel(land, 4);
        var after = command.Apply(document);
        Assert.Equal(4, ((LandLayer)after.FindLayer(land.Id)!).Level);
        Assert.Equal(0, ((LandLayer)command.Revert(after).FindLayer(land.Id)!).Level);
        Assert.Equal(10, ((LandLayer)UpdateLayerCommand.SetLandLevel(land, 99).Apply(document).FindLayer(land.Id)!).Level);
    }
}
