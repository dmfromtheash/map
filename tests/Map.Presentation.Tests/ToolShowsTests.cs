using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>The shows of the section navigation (DEC-093): frames of real maps, from the map before to the result.</summary>
public sealed class ToolShowsTests
{
    private static EditorViewModel Editor() => new(
        new MapDocument(DocumentId.New(), "Показы", DateTimeOffset.UnixEpoch, new CanvasDefinition(800, 600, "px", "#FF9DBBC4"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF9DBBC4")])
        { Lighting = new MapLighting { Enabled = true, TimeOfDay = 1 } },
        null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());

    [Fact]
    public void A_river_grows_from_none_to_the_whole_channel_and_the_open_map_is_untouched()
    {
        var editor = Editor();
        var before = editor.Session.Document;
        int Nodes(MapDocument d) => d.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).SelectMany(r => r.Channels).Sum(c => c.Nodes.Length);
        var counts = Enumerable.Range(0, EditorViewModel.ShowFrames).Select(i => Nodes(editor.ShowFrame(ShowKind.River, i))).ToArray();
        Assert.Equal(0, counts[0]);
        Assert.True(counts[^1] > counts[EditorViewModel.ShowFrames / 2]);
        Assert.Equal(counts.Order(), counts);
        Assert.Same(before, editor.Session.Document);
        var frame = editor.ShowFrame(ShowKind.River, 3);
        Assert.Equal((EditorViewModel.ShowWidth, EditorViewModel.ShowHeight), (frame.Canvas.Width, frame.Canvas.Height));
    }

    [Fact]
    public void The_land_show_grows_and_the_assets_show_keeps_the_stock_look()
    {
        var editor = Editor();
        Assert.Empty(editor.ShowFrame(ShowKind.Land, 0).Layers.OfType<LandLayer>());
        Assert.NotEmpty(editor.ShowFrame(ShowKind.Land, EditorViewModel.ShowFrames - 1).Layers.OfType<LandLayer>());
        // The owner's mountains as in the catalog: no light or style of the map (owner, 2026-11-10).
        var assets = editor.ShowFrame(ShowKind.Assets, EditorViewModel.ShowFrames - 1);
        Assert.Null(assets.Lighting);
        Assert.Null(assets.Style);
        Assert.NotNull(editor.ShowFrame(ShowKind.River, 0).Lighting);
    }
}
