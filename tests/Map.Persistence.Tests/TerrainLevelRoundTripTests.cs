using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

/// <summary>The terrain level of a piece of land and of a river survives save and reopen; zero is not written.</summary>
public sealed class TerrainLevelRoundTripTests
{
    [Fact]
    public void Land_and_river_levels_round_trip()
    {
        using var workspace = new TempWorkspace();
        var file = workspace.File("levels.mapproj");
        var document = ProjectFixtures.Document("Levels");
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var land = new LandLayer(LayerId.New(), "Плато", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage), new LandEdgeStyle()) { Level = 2 };
        var river = new River
        {
            Name = "Река",
            Level = -1,
            Channels = [RiverEditor.NewChannel([new(10, 10), new(200, 40)], 8)],
        };
        river.Validate();
        var objects = document.Layers.OfType<ObjectLayer>().First();
        var withTerrain = document with
        {
            Layers = [document.Layers[0], land, .. document.Layers.Skip(1).Select(l => l.Id == objects.Id ? objects with { Rivers = [river] } : l)],
        };

        MapProjectStore.Save(withTerrain, file, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var opened = MapProjectStore.Open(file).Document;

        Assert.Equal(2, ((LandLayer)opened.FindLayer(land.Id)!).Level);
        Assert.Equal(-1, ((ObjectLayer)opened.FindLayer(objects.Id)!).Rivers.Single().Level);
    }

    [Fact]
    public void Cliff_look_round_trips_and_the_default_is_not_written()
    {
        using var workspace = new TempWorkspace();
        var document = ProjectFixtures.Document("Cliffs");
        var coverage = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var cliff = new LandEdgeStyle
        {
            Cliff = LandCliffStyle.Looks.Single(look => look.Id == "ink-dots").Style with
            {
                Height = 52, Lean = -.4, Foot = CliffFoot.Plain, Strata = 2, BaseShadowColor = "#FF102030",
            },
        };
        var land = new LandLayer(LayerId.New(), "Плато", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage), cliff) { Level = 1 };
        var plain = new LandLayer(LayerId.New(), "Берег", true, false, 1,
            TileMap.Empty(1).With(new TileKey(1, 0), coverage), new LandEdgeStyle());
        var file = workspace.File("cliffs.mapproj");
        MapProjectStore.Save(document with { Layers = [document.Layers[0], plain, land, .. document.Layers.Skip(1)] },
            file, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var opened = MapProjectStore.Open(file).Document;
        Assert.Equal(cliff, ((LandLayer)opened.FindLayer(land.Id)!).EdgeStyle);
        Assert.Equal(new LandEdgeStyle(), ((LandLayer)opened.FindLayer(plain.Id)!).EdgeStyle);
        using var archive = System.IO.Compression.ZipFile.OpenRead(file);
        var json = string.Concat(archive.Entries.Where(entry => entry.FullName.EndsWith(".json", StringComparison.Ordinal))
            .Select(entry => new StreamReader(entry.Open()).ReadToEnd()));
        Assert.Equal(1, json.Split("\"cliff\"").Length - 1);
    }
}
