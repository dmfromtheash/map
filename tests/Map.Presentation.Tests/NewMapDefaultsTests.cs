using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// А1.2, DEC-085 (owner chose «B · средне», 2026-11-01): the tools start at sizes from the canvas. Audit: a 24 px land
/// brush under a 64 px coast roughness gave a chain of islets; mountains, rivers and roads ignored the canvas.
/// </summary>
public sealed class NewMapDefaultsTests
{
    [Theory]
    [InlineData(1920, 74, 30, 175, 13, 9)]
    [InlineData(4096, 158, 63, 372, 27, 19)]
    [InlineData(16384, 256, 102, 1489, 109, 74)]
    public void The_tools_start_at_sizes_that_fit_the_canvas(int side, double brush, double roughness, double stamp, double river, double road)
    {
        var defaults = NewMapDefaults.For(new CanvasDefinition(side, side, "px", "#FFFFFFFF"));

        Assert.Equal(brush, defaults.LandBrushRadius);
        Assert.Equal(roughness, defaults.LandRoughness);
        Assert.Equal(stamp, defaults.StampWidth);
        Assert.Equal(river, defaults.RiverWidth);
        Assert.Equal(road, defaults.RoadWidth);
        Assert.Equal(.5, defaults.AreaStampFactor);
        // The coast cells sit well inside the brush, so one stroke is one shore, not beads.
        Assert.True(defaults.LandRoughness * 2.5 <= defaults.LandBrushRadius + 1);
    }

    [Fact]
    public void The_long_side_sets_the_sizes_of_a_wide_canvas()
    {
        Assert.Equal(NewMapDefaults.For(new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF")),
            NewMapDefaults.For(new CanvasDefinition(4096, 1024, "px", "#FFFFFFFF")));
    }

    [Fact]
    public void A_new_editor_takes_them_and_the_panel_shows_them()
    {
        var document = new MapDocument(DocumentId.New(), "А1.2", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog());

        Assert.Equal(158, editor.BrushSettings.Radius);
        Assert.Equal(63, editor.LandRoughness);
        Assert.Equal(27, editor.NewRiverWidth);
        Assert.Equal(19, editor.NewRoadWidth);
    }
}
