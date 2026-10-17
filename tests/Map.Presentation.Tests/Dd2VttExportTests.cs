using System.Text.Json;
using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.Presentation.Tests;

/// <summary>Universal VTT (*.dd2vtt) export: structure, picture, walls, doors, and the «стена» switch.</summary>
public sealed class Dd2VttExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("map-dd2vtt-").FullName;

    /// <summary>
    /// The file's schema written as data: property → expected JSON kind. Nested objects and the
    /// elements of arrays are described the same way, so the test is a small JSON-schema check
    /// without adding a schema library.
    /// </summary>
    private static readonly Dictionary<string, object> Schema = new()
    {
        ["format"] = JsonValueKind.Number,
        ["resolution"] = new Dictionary<string, object>
        {
            ["map_origin"] = Point,
            ["map_size"] = Point,
            ["pixels_per_grid"] = JsonValueKind.Number,
        },
        ["line_of_sight"] = new object[] { new object[] { Point } },
        ["objects_line_of_sight"] = new object[] { new object[] { Point } },
        ["portals"] = new object[] { new Dictionary<string, object>
        {
            ["position"] = Point,
            ["bounds"] = new object[] { Point },
            ["rotation"] = JsonValueKind.Number,
            ["closed"] = JsonValueKind.True,
            ["freestanding"] = JsonValueKind.False,
        } },
        ["environment"] = new Dictionary<string, object>
        {
            ["baked_lighting"] = JsonValueKind.False,
            ["ambient_light"] = JsonValueKind.String,
        },
        ["lights"] = Array.Empty<object>(),
        ["image"] = JsonValueKind.String,
    };

    private static Dictionary<string, object> Point => new() { ["x"] = JsonValueKind.Number, ["y"] = JsonValueKind.Number };

    [Fact]
    public async Task Exported_file_matches_the_schema_and_carries_picture_grid_walls_and_doors()
    {
        var editor = Editor(out var wallId);
        editor.SelectObject(wallId);
        Assert.False(editor.SelectedBlocksLight);
        Assert.True(editor.SetSelectedBlocksLight(true));
        Assert.True(editor.SelectedBlocksLight);
        Assert.Contains("Стен: 2, дверей: 1", editor.ExportVttSummary);
        editor.BeginExport();
        await editor.WaitForExportPreviewAsync();
        Assert.Equal(64, editor.ExportVttGridSize);

        Assert.True(await editor.StartVttExportAsync());
        var path = Path.Combine(_root, "map.dd2vtt");
        Assert.True(File.Exists(path));
        Assert.DoesNotContain(Directory.GetFiles(_root), f => f != path);

        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;
        Validate(root, Schema, "$");
        Assert.Equal(64, root.GetProperty("resolution").GetProperty("pixels_per_grid").GetInt32());
        Assert.Equal(10, root.GetProperty("resolution").GetProperty("map_size").GetProperty("x").GetDouble(), 3);
        Assert.Equal(6, root.GetProperty("resolution").GetProperty("map_size").GetProperty("y").GetDouble(), 3);

        // The wall from (64, 128) to (576, 128) with a 64-wide door in the middle: two runs, one door.
        var walls = root.GetProperty("line_of_sight").EnumerateArray().ToArray();
        Assert.Equal(2, walls.Length);
        Assert.Equal(1, walls[0][0].GetProperty("x").GetDouble(), 3);
        Assert.Equal(2, walls[0][0].GetProperty("y").GetDouble(), 3);
        Assert.Equal(4.5, walls[0][walls[0].GetArrayLength() - 1].GetProperty("x").GetDouble(), 3);
        Assert.Equal(9, walls[1][walls[1].GetArrayLength() - 1].GetProperty("x").GetDouble(), 3);
        var door = root.GetProperty("portals").EnumerateArray().Single();
        Assert.Equal(5, door.GetProperty("position").GetProperty("x").GetDouble(), 3);
        Assert.Equal(0, door.GetProperty("rotation").GetDouble(), 6);

        using var picture = SKBitmap.Decode(Convert.FromBase64String(root.GetProperty("image").GetString()!));
        Assert.Equal((640, 384), (picture.Width, picture.Height));
        Assert.Equal(new SKColor(0x4C, 0x90, 0xB8), picture.GetPixel(5, 5));
        Assert.Contains("стен 2, дверей 1", editor.LastVttExportText);
    }

    [Fact]
    public void Wall_switch_is_one_undo_step_and_survives_save()
    {
        var editor = Editor(out var wallId);
        editor.SelectObject(wallId);
        Assert.True(editor.SetSelectedBlocksLight(true));
        Assert.False(editor.SetSelectedBlocksLight(true));
        var path = Path.Combine(_root, "walls.mapproj");
        using var surface = SKSurface.Create(new SKImageInfo(4, 4))!;
        using var snapshot = surface.Snapshot();
        using var preview = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        MapProjectStore.Save(editor.Session.Document, path, new SaveMetadata("test", DateTimeOffset.UnixEpoch), preview.ToArray());
        var reopened = MapProjectStore.Open(path).Document;
        Assert.True(reopened.Layers.OfType<ObjectLayer>().Single().Objects.Single().Line!.Stroke.BlocksLight);
        editor.Undo();
        Assert.False(editor.SelectedBlocksLight);
        Assert.Contains("Стен нет", editor.ExportVttSummary);
    }

    private static void Validate(JsonElement element, object schema, string at)
    {
        switch (schema)
        {
            case JsonValueKind kind:
                Assert.True(element.ValueKind == kind, $"{at}: expected {kind}, got {element.ValueKind}");
                break;
            case Dictionary<string, object> properties:
                Assert.True(element.ValueKind == JsonValueKind.Object, $"{at}: expected object");
                foreach (var (name, child) in properties)
                {
                    Assert.True(element.TryGetProperty(name, out var value), $"{at}.{name} is missing");
                    Validate(value, child, $"{at}.{name}");
                }
                break;
            case object[] items:
                Assert.True(element.ValueKind == JsonValueKind.Array, $"{at}: expected array");
                if (items.Length == 1)
                {
                    var index = 0;
                    foreach (var item in element.EnumerateArray()) Validate(item, items[0], $"{at}[{index++}]");
                }
                break;
        }
    }

    private EditorViewModel Editor(out ObjectId wallId)
    {
        var wall = new SceneObject(ObjectId.New(), SceneObjectKind.Line, "Стена", 64, 128, 512, 1, 0,
            "#00000000", "#FF3D566E", 8, true, false)
        {
            Line = new LineStyle(
                [new VectorNode(VectorNodeId.New(), 0, 0), new VectorNode(VectorNodeId.New(), 1, 0)],
                new VectorStrokeStyle { Width = 8 }, [new LinePortal(PortalId.New(), 0.5, 64)]),
        };
        wallId = wall.Id;
        var document = new MapDocument(DocumentId.New(), "dd2vtt", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(640, 384, "px", "#FF4C90B8"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF4C90B8"),
                new ObjectLayer(LayerId.New(), "Стены", true, false, 1, [wall]),
                new GridLayer(LayerId.New(), "Сетка", true, false, 1, [new GridDefinition(GridId.New(), GridGeometry.Square, 64)]),
            ]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog(), imageExporter: new ImageExportEngine(),
            exportDestinationPicker: new VttPicker(Path.Combine(_root, "map.dd2vtt")));
    }

    private sealed class VttPicker(string path) : IExportDestinationPicker
    {
        public Task<string?> PickImageAsync(string suggestedFileName, ExportImageFormat format) => Task.FromResult<string?>(null);
        public Task<string?> PickTilePackageAsync(string suggestedFolderName) => Task.FromResult<string?>(null);
        public Task<string?> PickVttAsync(string suggestedFileName) => Task.FromResult<string?>(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
