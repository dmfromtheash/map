using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>R33: UWP checks, the T5 table and the older column «.sec» read and written, systems under the fog hidden from players.</summary>
public sealed class StarSectorTests
{
    [Fact]
    public void A_uwp_is_checked_and_read()
    {
        Assert.True(StarSystem.IsValidUwp("A788899-C"));
        Assert.True(StarSystem.IsValidUwp("X000000-0"));
        Assert.False(StarSystem.IsValidUwp("A788899C"));
        Assert.False(StarSystem.IsValidUwp("I788899-C"));
        var system = new StarSystem { Name = "Регина", Uwp = "A788899-C", Stars = "F7 V" };
        Assert.Equal('A', system.Starport);
        Assert.Equal(12, system.TechLevel);
        Assert.Equal("#FFF8F7FF", system.StarColor);
        Assert.Equal("0101", StarSector.Number(new HexOffset(0, 0)));
        Assert.Equal(new HexOffset(31, 39), StarSector.Parse("3240"));
    }

    [Fact]
    public void The_t5_table_round_trips_and_the_older_sec_is_read()
    {
        var entries = new[]
        {
            new SecFormat.Entry(new HexOffset(0, 0), new StarSystem { Name = "Регина", Uwp = "A788899-C", Bases = "NS", Remarks = "Ri Pa Ph", Zone = TravelZone.Green, Pbg = "703", Allegiance = "ImDd", Stars = "F7 V BD M3 V" }),
            new SecFormat.Entry(new HexOffset(2, 4), new StarSystem { Name = "Ифиген", Uwp = "C200423-7", Remarks = "Na Ni Va", Zone = TravelZone.Amber, Pbg = "604", Allegiance = "ImDd", Stars = "M0 V" }),
        };
        var text = SecFormat.Write(entries);
        Assert.StartsWith("Hex\tName\tUWP", text);
        var read = SecFormat.Read(text, out var problems);
        Assert.Empty(problems);
        Assert.Equal(entries, read);

        var legacy = """
            # Subsector A
            Regina       0101 A788899-C N  Ri Pa Ph     703 Im F7 V BD M3 V
            Efate        0402 A646930-D NS Hi In      A 701 Im M0 V
            garbage line 99
            """;
        var old = SecFormat.Read(legacy, out var oldProblems);
        Assert.Equal(2, old.Count);
        Assert.Equal(("Regina", "A788899-C", "N", "Ri Pa Ph", "703", "Im"), (old[0].System.Name, old[0].System.Uwp, old[0].System.Bases, old[0].System.Remarks, old[0].System.Pbg, old[0].System.Allegiance));
        Assert.Equal("F7 V BD M3 V", old[0].System.Stars);
        Assert.Equal(TravelZone.Amber, old[1].System.Zone);
        Assert.Equal(new HexOffset(3, 1), old[1].Hex);
        Assert.Single(oldProblems);
    }

    [Fact]
    public void Players_do_not_get_systems_under_the_fog_or_the_masters_notes()
    {
        var board = HexBoard.Create(HexOrientation.Flat, 40, 4, 4);
        var (seen, hidden) = (board.Layout.FromOffset(new HexOffset(0, 0)), board.Layout.FromOffset(new HexOffset(2, 2)));
        board = board.With([new(seen, HexCell.Of("plains", 0).WithRevealed(true)), new(hidden, HexCell.Of("plains", 0))]);
        var layer = new HexLayer(LayerId.New(), "Гексы", true, false, 1, board)
        {
            FogEnabled = true,
            Systems = new Dictionary<HexCoord, StarSystem>
            {
                [seen] = new() { Name = "Видно", Uwp = "A788899-C", Notes = "секрет" },
                [hidden] = new() { Name = "Скрыто", Uwp = "B000000-0" },
            }.ToImmutableDictionary(),
        };
        var players = ExportPlayerVisibility.Sanitize(layer);
        var system = Assert.Single(players.Systems).Value;
        Assert.Equal("Видно", system.Name);
        Assert.Equal("", system.Notes);
    }
}
