using System.IO.Compression;
using System.Text.Json;
using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class HexLayerPersistenceTests
{
    private static MapDocument HexMap(HexOrientation orientation)
    {
        var document = HexMapFactory.Create("Гексы", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            orientation, 40, 30, 20, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var board = layer.Board;
        var cells = new List<KeyValuePair<HexCoord, HexCell?>>();
        for (var column = 0; column < 30; column += 3)
        {
            for (var row = 0; row < 20; row += 2)
            {
                var hex = board.Layout.FromOffset(new HexOffset(column, row));
                cells.Add(new(hex, new HexCell(row % 4 == 0 ? "starter/forest" : "starter/sea", column % 3,
                    column % 6 == 0 ? "starter/village" : null, 1)));
            }
        }

        var painted = layer with
        {
            Board = board.With(cells),
            Grid = new HexGridStyle { Numbers = true, Color = "#80102030", Width = 2.5, ShowEmpty = false },
        };
        return document.WithLayer(layer.Id, _ => painted);
    }

    [Theory]
    [InlineData(HexOrientation.Flat)]
    [InlineData(HexOrientation.Pointy)]
    public void A_hex_map_round_trips_every_hex_and_its_grid_in_schema_v12(HexOrientation orientation)
    {
        using var workspace = new TempWorkspace();
        var document = HexMap(orientation);
        var path = workspace.File("hex.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        using (var archive = ZipFile.OpenRead(path))
        using (var stream = archive.GetEntry(MapProjectFormat.ManifestEntry)!.Open())
        using (var manifest = JsonDocument.Parse(stream))
        {
            Assert.Equal(MapProjectFormat.HexLayersSchemaVersion, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("hex", manifest.RootElement.GetProperty("category").GetString());
        }

        var reopened = MapProjectStore.Open(path).Document;
        Assert.Equal("hex", reopened.Category.Value);
        var expected = document.Layers.OfType<HexLayer>().Single();
        var actual = reopened.Layers.OfType<HexLayer>().Single();
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Board.Layout, actual.Board.Layout);
        Assert.Equal(expected.Board.Columns, actual.Board.Columns);
        Assert.Equal(expected.Board.Rows, actual.Board.Rows);
        Assert.Equal(expected.Grid, actual.Grid);
        Assert.Equal(expected.Board.CellCount, actual.Board.CellCount);
        Assert.Equal(expected.Board.Cells.ToList(), actual.Board.Cells.ToList());
    }

    [Fact]
    public void A_map_without_hex_layers_keeps_its_old_schema()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("standard.mapproj");
        MapProjectStore.Save(ProjectFixtures.Document("Standard"), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        using var archive = ZipFile.OpenRead(path);
        using var stream = archive.GetEntry(MapProjectFormat.ManifestEntry)!.Open();
        using var manifest = JsonDocument.Parse(stream);
        Assert.True(manifest.RootElement.GetProperty("schemaVersion").GetInt32() < MapProjectFormat.HexLayersSchemaVersion);
    }
}
