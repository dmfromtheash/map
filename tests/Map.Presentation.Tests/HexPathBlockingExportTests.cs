using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class HexPathBlockingExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-r01-").FullName;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Dd2vtt_contains_only_selected_level_walls_and_the_same_picture_as_that_level_png(int level)
    {
        var document = World();
        var engine = new ImageExportEngine();
        var plan = engine.Plan(document, new ExportRequest(ExportImageFormat.Png, ExportAreaMode.WholeCanvas, null,
            document.Canvas.Width, document.Canvas.Height, ExportOutputMode.SingleImage, false, false)
            { Hex = new(false, level) });
        var path = Path.Combine(_root, "world.dd2vtt");
        var result = await UniversalVttExporter.ExportAsync(engine, document, plan, path, null, 40);
        Assert.Equal(1, result.Walls);
        using var vtt = JsonDocument.Parse(File.ReadAllText(path));
        var wall = Assert.Single(vtt.RootElement.GetProperty("line_of_sight").EnumerateArray());
        var selected = document.Layers.OfType<HexLayer>().Single(l => l.Level == level);
        var expected = HexPathGeometry.Flatten(selected.Paths[0], selected.Board.Layout);
        Assert.Equal(expected.Count, wall.GetArrayLength());
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X / 40, wall[i].GetProperty("x").GetDouble(), 4);
            Assert.Equal(expected[i].Y / 40, wall[i].GetProperty("y").GetDouble(), 4);
        }
        var onlySelected = document.WithLayers([.. document.Layers.Select(l =>
            l is HexLayer hex && hex.Level != level ? l with { Visible = false } : l)]);
        var png = Path.Combine(_root, "level.png");
        await engine.ExportAsync(onlySelected, plan with { Hex = null }, png, null);
        Assert.Equal(File.ReadAllBytes(png), vtt.RootElement.GetProperty("image").GetBytesFromBase64());
        Assert.Equal(2, document.Layers.OfType<HexLayer>().Count(l => l.Visible));
    }

    [Fact]
    public void Path_switches_are_undoable_and_level_choice_updates_export_summary_without_changing_edit_level()
    {
        var editor = new EditorViewModel(World(), null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());
        var surface = editor.Session.Document.Layers.OfType<HexLayer>().Single(l => l.Level == 0);
        editor.SelectHexPath(surface.Paths[0].Id);
        var depth = editor.Session.UndoDepth;
        Assert.True(editor.SetSelectedHexPathBlocking(sight: false, move: true, light: true));
        Assert.Equal(depth + 1, editor.Session.UndoDepth);
        Assert.False(editor.SetSelectedHexPathBlocking(sight: false, move: true, light: true));
        Assert.False(editor.SelectedHexPath!.BlocksSight);
        Assert.True(editor.SelectedHexPath.BlocksMove);
        Assert.True(editor.SelectedHexPath.BlocksLight);
        editor.Undo();
        Assert.True(editor.SelectedHexPath!.BlocksSight);
        Assert.False(editor.SelectedHexPath.BlocksMove);
        editor.Redo();
        Assert.False(editor.SelectedHexPath!.BlocksSight);
        editor.BeginExport();
        Assert.Equal(0, editor.ExportHexLevel);
        Assert.Contains("Стен нет", editor.ExportVttSummary);
        editor.ExportHexLevel = -1;
        Assert.Equal(-1, editor.CurrentExportPlan!.Hex!.Level);
        Assert.Contains("Стен: 1", editor.ExportVttSummary);
        Assert.Equal(0, editor.HexLevel);
        editor.CloseExport();
    }

    [Fact]
    public void Hex_json_uses_scene_level_and_visibility_and_round_trips_independent_path_flags()
    {
        var document = World();
        var layer = document.Layers.OfType<HexLayer>().Single(l => l.Level == -1);
        var flags = layer.Paths[0] with { BlocksSight = false, BlocksMove = true, BlocksLight = true };
        document = document.WithLayer(layer.Id, _ => layer with { Paths = [flags] });
        var scene = new ExportScene(document, -1);
        var json = HexJsonFormat.ToJson(scene);
        Assert.Single(json["layers"]!.AsArray());
        Assert.Equal(-1, json["layers"]![0]!["level"]!.GetValue<int>());
        var imported = HexJsonFormat.Read(json.ToJsonString(), layer);
        var path = Assert.Single(imported.Paths);
        Assert.False(path.BlocksSight);
        Assert.True(path.BlocksMove);
        Assert.True(path.BlocksLight);
        var hidden = new ExportScene(document, -1, visibleLayerIds: []);
        Assert.Empty(HexJsonFormat.ToJson(hidden)["layers"]!.AsArray());
    }

    private static MapDocument World()
    {
        var document = HexMapFactory.Create("Два уровня", DateTimeOffset.UnixEpoch, new MapCategoryId("hex"),
            HexOrientation.Pointy, 24, 8, 6, "#FFF2E9DC");
        var layer = document.Layers.OfType<HexLayer>().Single();
        var layout = layer.Board.Layout;
        HexPath Wall(int c)
        {
            var h = layout.FromOffset(new HexOffset(c, 2));
            return new(HexPathId.New(), HexPathKind.Border, HexRouteStyle.Edges, [],
                [layout.Canonical(new(h, 0)), layout.Canonical(new(h, 1)), layout.Canonical(new(h, 2))])
                { BlocksSight = true };
        }
        var surface = layer with { Paths = [Wall(2)] };
        var under = (HexLayer)(layer with { Level = -1, Paths = [Wall(5)], Board = layer.Board.With(
            [new(layout.FromOffset(new(4, 2)), HexCell.Of("starter/mountains"))]) }).WithNewIds();
        return document.WithLayers([.. document.Layers.Select(l => l.Id == layer.Id ? surface : l), under]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
