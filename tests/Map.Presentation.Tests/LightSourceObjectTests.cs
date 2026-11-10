using System.Text.Json;
using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>R14: a light source with colour, radius, dim zone and flicker - undoable, and written into dd2vtt.</summary>
public sealed class LightSourceObjectTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-r14-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static EditorViewModel Editor() => new(
        new MapDocument(DocumentId.New(), "Свет", DateTimeOffset.UnixEpoch, new CanvasDefinition(800, 600, "px", "#FF9DBBC4"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF9DBBC4")]),
        null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());

    [Fact]
    public void A_placed_light_is_selected_and_its_settings_are_undoable()
    {
        var editor = Editor();
        editor.PlaceLightAt(200, 150);
        var light = Assert.IsType<MapLightSource>(editor.SelectedLight);
        editor.SetSelectedLightColor("#FF66AAFF");
        editor.SetSelectedLightRadius(120, true);
        editor.SetSelectedLightDimZone(150, true);
        editor.SetSelectedLightIntensity(60, true);
        editor.SetSelectedLightFlicker(false);
        var edited = editor.SelectedLight!;
        Assert.Equal(light.Id, edited.Id);
        Assert.Equal(120, edited.Radius);
        Assert.Equal(1.5, edited.DimZone, 6);
        Assert.Equal(300, edited.DimRadius, 6);
        Assert.Equal(.6, edited.Intensity, 6);
        Assert.False(edited.Flicker);
        editor.Undo(); editor.Undo(); editor.Undo();
        Assert.Equal(0, editor.SelectedLight!.DimZone);
        // A dragged slider previews without a step, the release commits one.
        var depth = editor.Session.UndoDepth;
        editor.SetSelectedLightDimZone(80, false);
        Assert.Equal(depth, editor.Session.UndoDepth);
        editor.SetSelectedLightDimZone(80, true);
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        editor.SetSelectedLightDimZone(900, true);
        Assert.Equal(MapLightSource.MaxDimZone, editor.SelectedLight!.DimZone);
    }

    [Fact]
    public async Task Dd2vtt_lists_the_lights_in_grid_cells_with_the_dim_reach()
    {
        var editor = Editor();
        editor.PlaceLightAt(200, 160);
        editor.SetSelectedLightColor("#FFFF8800");
        editor.SetSelectedLightRadius(80, true);
        editor.SetSelectedLightDimZone(50, true);
        var document = editor.Session.Document;
        var engine = new ImageExportEngine();
        var plan = engine.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            document.Canvas.Width, document.Canvas.Height, ExportOutputMode.SingleImage, false, false));
        var path = Path.Combine(_root, "lights.dd2vtt");
        await UniversalVttExporter.ExportAsync(engine, document, plan, path, null, 40);
        using var vtt = JsonDocument.Parse(File.ReadAllText(path));
        var light = Assert.Single(vtt.RootElement.GetProperty("lights").EnumerateArray());
        Assert.Equal(5, light.GetProperty("position").GetProperty("x").GetDouble(), 4);
        Assert.Equal(4, light.GetProperty("position").GetProperty("y").GetDouble(), 4);
        Assert.Equal(3, light.GetProperty("range").GetDouble(), 4);
        Assert.Equal("ffff8800", light.GetProperty("color").GetString());

        // With the light section off, a tabletop gets no lights.
        editor.SetLightingEnabled(false);
        var dark = Path.Combine(_root, "dark.dd2vtt");
        await UniversalVttExporter.ExportAsync(engine, editor.Session.Document, plan, dark, null, 40);
        using var none = JsonDocument.Parse(File.ReadAllText(dark));
        Assert.Equal(0, none.RootElement.GetProperty("lights").GetArrayLength());
    }

    [Fact]
    public void A_click_on_a_light_mark_chooses_it_only_while_the_marks_are_shown()
    {
        var editor = Editor();
        editor.PlaceLightAt(200, 150);
        var first = editor.SelectedLight!.Id;
        editor.PlaceLightAt(500, 400);
        var second = editor.SelectedLight!.Id;
        var depth = editor.Session.UndoDepth;

        // Settings of Свет closed: no marks, the click goes to the tool.
        Assert.False(editor.SelectLightAt(203, 148, 12));
        Assert.Equal(second, editor.SelectedLight!.Id);

        editor.ShowLightMarkers(true);
        Assert.True(editor.SelectLightAt(203, 148, 12));
        Assert.Equal(first, editor.SelectedLight!.Id);
        // Off every mark: nothing is chosen and the click falls through.
        Assert.False(editor.SelectLightAt(350, 280, 12));
        Assert.Equal(first, editor.SelectedLight!.Id);
        // Choosing is not an edit of the map.
        Assert.Equal(depth, editor.Session.UndoDepth);
        Assert.Equal(second, editor.LightAt(498, 404, 12)!.Id);
    }

    [Fact]
    public async Task Dd2vtt_of_a_part_of_the_map_counts_lights_from_its_corner_and_drops_far_ones()
    {
        var editor = Editor();
        editor.PlaceLightAt(200, 160);
        editor.SetSelectedLightRadius(80, true);
        editor.SetSelectedLightDimZone(0, true);
        // A second light far outside the exported part: its light does not reach it.
        editor.PlaceLightAt(790, 590);
        editor.SetSelectedLightRadius(80, true);
        editor.SetSelectedLightDimZone(0, true);
        var document = editor.Session.Document;
        var engine = new ImageExportEngine();
        // The part 400×300 from (100, 80), written twice as large: the grid stays in map units.
        var plan = engine.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.Region,
            new Map.Core.Editing.CanvasRegion(100, 80, 400, 300), 800, 600, ExportOutputMode.SingleImage, false, false));
        var path = Path.Combine(_root, "part.dd2vtt");
        await UniversalVttExporter.ExportAsync(engine, document, plan, path, null, 40);
        using var vtt = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(80, vtt.RootElement.GetProperty("resolution").GetProperty("pixels_per_grid").GetInt32());
        var light = Assert.Single(vtt.RootElement.GetProperty("lights").EnumerateArray());
        Assert.Equal(2.5, light.GetProperty("position").GetProperty("x").GetDouble(), 4);
        Assert.Equal(2, light.GetProperty("position").GetProperty("y").GetDouble(), 4);
        Assert.Equal(2, light.GetProperty("range").GetDouble(), 4);
    }
}
