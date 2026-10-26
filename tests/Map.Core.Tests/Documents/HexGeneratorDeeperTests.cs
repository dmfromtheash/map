using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

/// <summary>R21: flow rivers that merge, states grown from capitals, latitudes, an open rim, world modes and progress.</summary>
public sealed class HexGeneratorDeeperTests
{
    private static HexLayer Board(int columns, int rows) =>
        new(LayerId.New(), "Гексы", true, false, 1, HexBoard.Create(HexOrientation.Flat, 32, columns, rows));

    private static readonly HexGeneratorPalette Palette = new();

    private static HexLayer Steps(HexLayer layer, HexGeneratorSettings settings, params HexGenerationStep[] steps)
    {
        foreach (var step in steps) layer = HexWorldGenerator.Run(layer, step, settings);
        return layer;
    }

    private static readonly HexGenerationStep[] ToRivers =
        [HexGenerationStep.Land, HexGenerationStep.Relief, HexGenerationStep.Climate, HexGenerationStep.Rivers];

    [Fact]
    public void Flow_rivers_end_in_water_or_join_another_river_and_some_join()
    {
        var settings = new HexGeneratorSettings { Seed = 21, RiverModel = HexGeneratorSettings.FlowRivers, RiversPerThousand = 14, LandShare = .55 };
        var world = Steps(Board(120, 90), settings, ToRivers);
        var rivers = world.Paths.Where(p => p.Kind == HexPathKind.River).ToList();
        Assert.True(rivers.Count >= 4, $"{rivers.Count} rivers");
        var joined = 0;
        foreach (var river in rivers)
        {
            Assert.True(river.Hexes.Length >= 4);
            var end = river.Hexes[^1];
            var intoWater = Palette.IsWater(world.Board[end]?.Terrain);
            var intoRiver = rivers.Any(other => other != river && other.Hexes.Take(other.Hexes.Length - 1).Contains(end));
            Assert.True(intoWater || intoRiver, $"river ends at {end} on {world.Board[end]?.Terrain}");
            if (intoRiver) joined++;
            // Every step goes to a neighbour: a river is one connected course.
            for (var i = 1; i < river.Hexes.Length; i++) Assert.Equal(1, HexCoord.Distance(river.Hexes[i - 1], river.Hexes[i]));
        }
        Assert.True(joined >= 1, "tributaries join a main river");
        // Same seed, same rivers.
        var again = Steps(Board(120, 90), settings, ToRivers);
        Assert.Equal(rivers.Select(r => r.Hexes.ToArray()), again.Paths.Where(p => p.Kind == HexPathKind.River).Select(r => r.Hexes.ToArray()));
    }

    [Fact]
    public void States_grow_from_the_capitals_into_named_regions()
    {
        var settings = new HexGeneratorSettings { Seed = 7, SettlementsPerThousand = 20, LandShare = .6 };
        var world = Steps(Board(100, 80), settings, [.. ToRivers, HexGenerationStep.Settlements, HexGenerationStep.States]);
        var capitals = world.Board.Cells.Where(p => p.Value.Feature is { } f && (f == Palette.City || f == Palette.Port)).ToList();
        Assert.NotEmpty(capitals);
        Assert.Equal(capitals.Count, world.Regions.Count);
        Assert.Equal(world.Regions.Count, world.Regions.Select(r => r.Name).Distinct().Count());
        // Each capital lies in its own state, and the land is divided.
        Assert.Equal(capitals.Count, capitals.Select(c => c.Value.Region).Distinct().Count());
        Assert.All(capitals, c => Assert.NotEqual(0, c.Value.Region));
        var land = world.Board.Cells.Where(p => !Palette.IsWater(p.Value.Terrain) && p.Value.Terrain != Palette.Peaks).ToList();
        Assert.True(land.Count(p => p.Value.Region != 0) > land.Count * 0.6);
        Assert.All(world.Board.Cells.Where(p => Palette.IsWater(p.Value.Terrain)), p => Assert.Equal(0, p.Value.Region));
    }

    [Fact]
    public void A_narrow_latitude_span_has_no_poles_and_an_open_rim_lets_land_reach_the_edge()
    {
        var poles = Steps(Board(80, 80), new HexGeneratorSettings { Seed = 3, LandShare = .6 }, HexGenerationStep.Land, HexGenerationStep.Climate);
        var tropics = Steps(Board(80, 80), new HexGeneratorSettings { Seed = 3, LandShare = .6, LatitudeSpan = .2 }, HexGenerationStep.Land, HexGenerationStep.Climate);
        Assert.Contains(poles.Board.Cells, p => p.Value.Terrain == Palette.Snow);
        Assert.DoesNotContain(tropics.Board.Cells, p => p.Value.Terrain == Palette.Snow);

        var closed = Steps(Board(80, 60), new HexGeneratorSettings { Seed = 5, LandShare = .6 }, HexGenerationStep.Land);
        var open = Steps(Board(80, 60), new HexGeneratorSettings { Seed = 5, LandShare = .6, OpenEdges = true }, HexGenerationStep.Land);
        int RimLand(HexLayer l) => l.Board.Cells.Count(p =>
        {
            var o = l.Board.Layout.ToOffset(p.Key);
            return (o.Column == 0 || o.Row == 0 || o.Column == 79 || o.Row == 59) && !Palette.IsWater(p.Value.Terrain);
        });
        // The closed rim presses the sea in; the open one lets the land run on.
        Assert.True(RimLand(open) > RimLand(closed) * 2 + 20, $"rim land: open {RimLand(open)}, closed {RimLand(closed)}");
    }

    [Fact]
    public void World_modes_make_archipelagos_deserts_and_caverns()
    {
        int Count(HexLayer l, string terrain) => l.Board.Cells.Count(p => p.Value.Terrain == terrain);
        var all = new[] { HexGenerationStep.Land, HexGenerationStep.Relief, HexGenerationStep.Climate };
        var normal = Steps(Board(90, 70), new HexGeneratorSettings { Seed = 11, LandShare = .5 }, all);
        var islands = Steps(Board(90, 70), new HexGeneratorSettings { Seed = 11, LandShare = .5, Mode = HexWorldMode.Archipelago }, all);
        var desert = Steps(Board(90, 70), new HexGeneratorSettings { Seed = 11, LandShare = .5, Mode = HexWorldMode.Desert }, all);
        var under = Steps(Board(90, 70), new HexGeneratorSettings { Seed = 11, LandShare = .5, Mode = HexWorldMode.Underdark }, all);
        double LandShare(HexLayer l) => l.Board.Cells.Count(p => !Palette.IsWater(p.Value.Terrain)) / (double)l.Board.CellCount;
        Assert.True(LandShare(islands) < .36, $"archipelago land {LandShare(islands):0.00}");
        Assert.True(Count(desert, Palette.Desert) > Count(normal, Palette.Desert) * 2 + 20);
        Assert.Equal(0, Count(under, Palette.Sea) + Count(under, Palette.Shallows) + Count(under, Palette.Snow));
        Assert.True(Count(under, Palette.Mountains) > under.Board.CellCount / 3, "rock around the caverns");
        Assert.True(Count(under, Palette.Lake) > 0, "underground lakes");
    }

    [Fact]
    public void Progress_climbs_to_one_over_all_steps_and_old_recipes_keep_their_rivers()
    {
        var reported = new List<double>();
        var recipe = new HexGenerationRecipe
        {
            Settings = new HexGeneratorSettings { Seed = 2, RiverModel = HexGeneratorSettings.FlowRivers },
            Steps = [.. Enum.GetValues<HexGenerationStep>()],
        };
        ControlledHexGeneration.Run(Board(60, 50), recipe, default, reported.Add);
        Assert.NotEmpty(reported);
        Assert.Equal(1, reported[^1], 6);
        for (var i = 1; i < reported.Count; i++) Assert.True(reported[i] >= reported[i - 1] - 1e-9, $"progress went back at {i}");

        // A recipe of R20 (no river model written) keeps the downhill rivers it was made with.
        Assert.Equal(HexGeneratorSettings.DownhillRivers, new HexGeneratorSettings().RiverModel);
        Assert.True(new HexGeneratorSettings().IsValid);
        Assert.False((new HexGeneratorSettings() with { Mode = (HexWorldMode)9 }).IsValid);
    }
}
