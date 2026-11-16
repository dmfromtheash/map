using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class BuildFixRiverToSeaTests
{
    [Theory]
    [InlineData(128, 128)]
    [InlineData(40, 40)]
    [InlineData(120, 120)]
    [InlineData(200, 120)]
    [InlineData(80, 80)]
    public void Flat_land_routes_reach_the_sea_through_the_click_command_and_undo(int x, int y)
    {
        var island = LandLayer.CreateEmpty(LayerId.New(), "Island") with
        {
            GeometryTiles = TileMap.Empty(1).With(new(0, 0), Enumerable.Range(0, 65536)
                .Select(i => (byte)(i % 256 is > 10 and < 245 && i / 256 is > 10 and < 245 ? 255 : 0)).ToArray()),
        };
        var objects = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, []);
        var document = new MapDocument(DocumentId.New(), "Flat land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF"), island, objects]);
        var route = Assert.IsAssignableFrom<IReadOnlyList<VectorPoint>>(Geography.RouteRiver(document, new(x, y)));
        Console.WriteLine($"RIVER from={x},{y} simplifiedPoints={route.Count}");
        Assert.InRange(route.Count, 2, int.MaxValue);
        var probe = new TerrainProbe(document);
        Assert.NotNull(probe.At(route[0].X, route[0].Y).Land);
        Assert.Null(probe.At(route[^1].X, route[^1].Y).Land);
        var editor = new EditorViewModel(document, null, false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.SelectLayer(objects.Id);
        editor.ActiveTool = EditorTool.River;
        editor.Session.Viewport.Set(0, 0, 1);
        editor.ArmRiverToSea();
        var router = new CanvasInputRouter(editor);
        router.Press(CanvasPointerButton.Left, x, y); router.Release(x, y);
        Assert.Equal(GeographyPick.None, editor.GeographyPick);
        var river = Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(objects.Id)!).Rivers);
        var channel = Assert.Single(river.Channels);
        Assert.Equal(route.ToArray(), channel.Nodes.Select(n => new VectorPoint(n.X, n.Y)).ToArray());
        Assert.Equal(2, channel.Nodes[0].Width);
        Assert.Equal(12, channel.Nodes[^1].Width);
        Assert.NotEmpty(RiverGeometry.Evaluate(river).Channels[channel.Id].Samples);
        Assert.DoesNotContain(editor.CheckGeography(), issue => issue.ObjectId == river.Id);
        Assert.Equal(1, editor.Session.UndoDepth);
        var after = editor.Session.Document;
        editor.Undo(); Assert.Same(document, editor.Session.Document);
        editor.Redo(); Assert.Same(after, editor.Session.Document);
    }
}
