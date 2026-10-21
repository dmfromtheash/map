using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class HexImportTests
{
    private static MapDocument Document() => HexMapFactory.Create("Import", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"), HexOrientation.Pointy, 16, 4, 4, "#FFFFFFFF");
    private const string Json = """{"format":"map-hex-json","layers":[{"name":"Новый мир","cells":[{"at":[9,8],"terrain":"starter/grass"},{"at":[1,1],"terrain":"missing/snow"},{"at":[2,2],"terrain":"starter/grass","feature":"missing/tower"},{"at":[-1,0],"terrain":"starter/grass"},{"at":[2048,0],"terrain":"starter/grass"},{"at":[9,8],"terrain":"starter/grass"}],"key":[{"at":[11,10],"title":"Ключ"},{"at":[-1,0],"title":"Вне поля"}],"paths":[{"kind":"Road","route":"Centers","hexes":[[0,0],[15,0]]},{"kind":"Road","route":"Centers","hexes":[[-1,0],[0,0]]}]}]}""";

    [Fact]
    public void Every_cell_is_imported_or_counted_and_unknown_ids_are_listed()
    {
        var layer = Document().Layers.OfType<HexLayer>().Single();
        var result = HexJsonExporter.ReadWithReport(Json, layer);
        Assert.Equal((16, 11), (result.Layer.Board.Columns, result.Layer.Board.Rows));
        Assert.Equal(1, result.Imported); Assert.Equal(5, result.Skipped);
        Assert.Equal(2, result.Outside); Assert.Equal(2, result.UnknownTiles); Assert.Equal(1, result.Duplicates);
        Assert.Equal(new[] { "missing/snow", "missing/tower" }, result.UnknownTileIds);
        Assert.Equal(1, result.SkippedPaths); Assert.Equal(1, result.SkippedNotes);
        Assert.Single(result.Layer.Paths); Assert.Single(result.Layer.Notes);
        Assert.Contains("missing/tower", result.Summary); Assert.True(result.Expanded);
        Assert.Equal(0, layer.Board.CellCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void View_model_targets_existing_or_new_layer_and_reports_in_status(bool newLayer)
    {
        var doc = Document();
        var editor = new EditorViewModel(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        var layer = doc.Layers.OfType<HexLayer>().Single();
        Assert.True(editor.ImportHexJson(Json, newLayer ? null : layer.Id));
        Assert.Equal(editor.LastHexImportResult!.Summary, editor.StatusDetail);
        Assert.Equal(newLayer ? 2 : 1, editor.Session.Document.Layers.OfType<HexLayer>().Count());
        Assert.True(editor.Session.Undo()); Assert.Same(doc, editor.Session.Document);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{bad")]
    [InlineData("{\"format\":\"map-hex-json\",\"layers\":{}}")]
    public void Invalid_input_has_no_document_change(string json)
    {
        var layer = Document().Layers.OfType<HexLayer>().Single();
        Assert.Throws<InvalidDataException>(() => HexJsonExporter.ReadWithReport(json, layer));
    }
}
