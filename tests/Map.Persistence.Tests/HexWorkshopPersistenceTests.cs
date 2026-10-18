using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class HexWorkshopPersistenceTests
{
    private static int SchemaOf(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(MapProjectFormat.ManifestEntry)!.Open();
        using var manifest = JsonDocument.Parse(stream);
        return manifest.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void Turned_tiles_regions_fog_paths_keys_levels_and_travel_round_trip_in_schema_v13(HexOrientation orientation)
    {
        using var workspace = new TempWorkspace();
        var document = HexMapFactory.Create("Мир", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            orientation, 40, 20, 16, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        HexCoord At(int c, int r) => layout.FromOffset(new HexOffset(c, r));
        var cells = new List<KeyValuePair<HexCoord, HexCell?>>();
        for (var c = 0; c < 20; c++)
        {
            for (var r = 0; r < 16; r++)
            {
                cells.Add(new(At(c, r), HexCell.Of("starter/grass", c % 3, r % 5 == 0 ? "starter/village" : null, 1,
                    rotation: (c + r) % 6, region: c < 10 ? 1 : 2, revealed: r < 8)));
            }
        }

        var river = new HexPath(HexPathId.New(), HexPathKind.River, HexRouteStyle.Meander,
            [.. Map.Core.Editing.HexPaths.Walk([At(1, 1), At(9, 6)])], [], 1.5, "#FF3070C0", "Светлая", 7);
        var border = new HexPath(HexPathId.New(), HexPathKind.Border, HexRouteStyle.Edges, [],
            [.. Map.Core.Editing.HexPaths.EdgeWalk(layout, new HexVertex(At(10, 0), 2), new HexVertex(At(10, 8), 4))]);
        var painted = layer with
        {
            Board = layer.Board.With(cells),
            Paths = [river, border],
            Regions = [new HexRegion(1, "Запад", "#FFB04030"), new HexRegion(2, "Восток", "#FF3060B0")],
            Notes = ImmutableDictionary<HexCoord, HexNote>.Empty
                .Add(At(3, 4), new HexNote("Логово", HexNoteCategory.Danger, "Тролль спит днём.", "Пахнет серой.", "logovo.mapproj"))
                .Add(At(12, 9), new HexNote("Брод")),
            FogEnabled = true,
            Travel = new HexTravelScale(12, "км", 30),
        };
        var under = (HexLayer)(painted with
        {
            Name = "Подземелье", Level = -1, Paths = [], Notes = ImmutableDictionary<HexCoord, HexNote>.Empty,
            Board = HexBoard.FromCells(layout, 20, 16, [new(At(3, 4), HexCell.Of("starter/mountains"))]),
        }).WithNewIds();
        document = document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? painted : l), under]);

        var path = workspace.File("world.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(MapProjectFormat.HexWorkshopSchemaVersion, SchemaOf(path));

        var reopened = MapProjectStore.Open(path).Document;
        var actual = reopened.Layers.OfType<HexLayer>().Single(l => l.Id == painted.Id);
        Assert.Equal(painted.Board.Cells.ToList(), actual.Board.Cells.ToList());
        Assert.Equal(painted.Regions, actual.Regions);
        Assert.Equal(painted.Notes.OrderBy(p => p.Key.Q).ThenBy(p => p.Key.R), actual.Notes.OrderBy(p => p.Key.Q).ThenBy(p => p.Key.R));
        Assert.True(actual.FogEnabled);
        Assert.Equal(painted.Travel, actual.Travel);
        Assert.Equal(2, actual.Paths.Count);
        Assert.True(painted.Paths[0].SameAs(actual.Paths[0]));
        Assert.True(painted.Paths[1].SameAs(actual.Paths[1]));
        var level = reopened.Layers.OfType<HexLayer>().Single(l => l.Id == under.Id);
        Assert.Equal(-1, level.Level);
        Assert.Equal("starter/mountains", level.Board[At(3, 4)]!.Terrain);
    }

    [Fact]
    public void A_plain_hex_map_still_saves_as_schema_v12_with_six_ints_per_hex()
    {
        using var workspace = new TempWorkspace();
        var document = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Flat, 40, 6, 6, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        document = document.WithLayer(layer.Id, _ => layer with
        {
            Board = layer.Board.With([new(new HexCoord(1, 1), HexCell.Of("starter/forest", 2))]),
        });
        var path = workspace.File("plain.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        Assert.Equal(MapProjectFormat.HexLayersSchemaVersion, SchemaOf(path));
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(MapProjectFormat.SceneEntry)!.Open();
        using var scene = JsonDocument.Parse(stream);
        var text = scene.RootElement.GetRawText();
        Assert.DoesNotContain("\"stride\"", text);
        Assert.DoesNotContain("\"paths\"", text);
    }
}
