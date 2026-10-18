using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public sealed class HexGeneratorTests
{
    private static HexLayer Board(int columns, int rows, HexOrientation orientation = HexOrientation.Flat) =>
        new(LayerId.New(), "Гексы", true, false, 1, HexBoard.Create(orientation, 32, columns, rows));

    private static readonly HexGeneratorPalette Palette = new();

    [Fact]
    public void The_same_seed_makes_the_same_world_and_another_seed_another()
    {
        var settings = new HexGeneratorSettings { Seed = 7 };
        var a = HexWorldGenerator.RunAll(Board(60, 40), settings);
        var b = HexWorldGenerator.RunAll(Board(60, 40), settings);
        Assert.Equal(a.Board.Cells.ToList(), b.Board.Cells.ToList());
        Assert.Equal(a.Paths.Count, b.Paths.Count);
        for (var i = 0; i < a.Paths.Count; i++)
        {
            Assert.True(a.Paths[i].Hexes.SequenceEqual(b.Paths[i].Hexes),
                $"path {i} {a.Paths[i].Kind}: {string.Join(" ", a.Paths[i].Hexes)} / {string.Join(" ", b.Paths[i].Hexes)}");
        }
        var c = HexWorldGenerator.Run(Board(60, 40), HexGenerationStep.Land, settings with { Seed = 8 });
        Assert.NotEqual(a.Board.Cells.Select(p => Palette.IsWater(p.Value.Terrain)), c.Board.Cells.Select(p => Palette.IsWater(p.Value.Terrain)));
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.6)]
    public void Land_takes_the_asked_share_and_the_rim_is_sea(double share)
    {
        var land = HexWorldGenerator.Run(Board(80, 60), HexGenerationStep.Land, new HexGeneratorSettings { Seed = 3, LandShare = share });
        Assert.Equal(80 * 60, land.Board.CellCount);
        var landCount = land.Board.Cells.Count(p => !Palette.IsWater(p.Value.Terrain));
        Assert.InRange(landCount / (double)land.Board.CellCount, share - 0.02, share + 0.02);
        var corner = land.Board[land.Board.Layout.FromOffset(new HexOffset(0, 0))]!;
        Assert.True(Palette.IsWater(corner.Terrain));
        // Shallows always touch land.
        Assert.All(land.Board.Cells.Where(p => p.Value.Terrain == Palette.Shallows),
            p => Assert.Contains(p.Key.Neighbors(), n => land.Board[n] is { } cell && !Palette.IsWater(cell.Terrain)));
    }

    [Fact]
    public void Each_step_builds_on_the_board_as_it_is_and_only_land_gets_relief_and_biomes()
    {
        var settings = new HexGeneratorSettings { Seed = 11 };
        var land = HexWorldGenerator.Run(Board(70, 50), HexGenerationStep.Land, settings);
        var relief = HexWorldGenerator.Run(land, HexGenerationStep.Relief, settings);
        Assert.Contains(relief.Board.Cells, p => Palette.IsHigh(p.Value.Terrain));
        foreach (var (hex, cell) in relief.Board.Cells)
        {
            if (Palette.IsWater(land.Board[hex]!.Terrain)) Assert.Equal(land.Board[hex], cell);
        }

        var climate = HexWorldGenerator.Run(relief, HexGenerationStep.Climate, settings);
        Assert.True(climate.Board.Cells.Select(p => p.Value.Terrain).Distinct().Count() >= 6);

        // A hex painted by hand between steps survives a later step that does not own it.
        var painted = climate with { Board = climate.Board.With([new(land.Board.Layout.FromOffset(new HexOffset(35, 25)), HexCell.Of("starter/volcano"))]) };
        var rivers = HexWorldGenerator.Run(painted, HexGenerationStep.Rivers, settings);
        Assert.Equal("starter/volcano", rivers.Board[land.Board.Layout.FromOffset(new HexOffset(35, 25))]!.Terrain);
    }

    [Fact]
    public void Rivers_run_downhill_to_water_and_roads_join_the_settlements()
    {
        var settings = new HexGeneratorSettings { Seed = 5, RiversPerThousand = 12, SettlementsPerThousand = 12 };
        var world = HexWorldGenerator.RunAll(Board(90, 60), settings);
        var rivers = world.Paths.Where(p => p.Kind == HexPathKind.River).ToList();
        Assert.NotEmpty(rivers);
        foreach (var river in rivers)
        {
            Assert.True(river.Hexes.Length >= 5);
            Assert.True(Palette.IsHigh(world.Board[river.Hexes[0]]?.Terrain) || world.Board[river.Hexes[0]]?.Terrain == Palette.Snow);
            var end = world.Board[river.Hexes[^1]]!.Terrain;
            Assert.True(Palette.IsWater(end) || rivers.Any(other => other != river && other.Hexes.Contains(river.Hexes[^1])), end);
        }

        var towns = world.Board.Cells.Where(p => p.Value.Feature is not null && p.Value.Feature != Palette.Mine).Select(p => p.Key).ToList();
        Assert.True(towns.Count >= 3);
        var roads = world.Paths.Where(p => p.Kind == HexPathKind.Road).ToList();
        Assert.NotEmpty(roads);
        Assert.All(roads, road => Assert.All(road.Hexes, hex => Assert.False(Palette.IsWater(world.Board[hex]?.Terrain))));
        Assert.True(towns.Count(town => roads.Any(r => r.Hexes.Contains(town))) >= towns.Count / 2);
    }

    [Fact]
    public void A_512_board_is_generated_step_by_step_within_seconds()
    {
        var watch = Stopwatch.StartNew();
        var world = HexWorldGenerator.RunAll(Board(512, 512), new HexGeneratorSettings { Seed = 2 });
        watch.Stop();
        Assert.Equal(512 * 512, world.Board.CellCount);
        Assert.True(watch.Elapsed.TotalSeconds < 30, $"{watch.Elapsed.TotalSeconds:0.0} s");
    }
}
