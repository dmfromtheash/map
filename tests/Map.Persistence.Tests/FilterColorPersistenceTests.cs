using Map.Core.Documents;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public sealed class FilterColorPersistenceTests
{
    [Fact]
    public void Gradient_map_post_processing_strength_and_preset_survive_reopen()
    {
        using var workspace = new TempWorkspace();
        var settings = FilterColorEffects.WithPreset(new FilterSettings { Brightness = 0.1 }, FilterColorEffects.Preset("parchment")!)
            with { ColorStrength = 0.6 };
        var custom = new FilterSettings
        {
            GradientMap = new() { ShadowColor = "#FF102030", MidtoneColor = "#FF405060", HighlightColor = "#FFF0E0D0", Strength = 0.35 },
            PostProcess = new() { Saturation = -0.4, Brightness = 0.2, Contrast = 0.15, Sharpness = 0.7 },
        };
        var path = workspace.File("filters.mapproj");
        MapProjectStore.Save(Document(settings, custom, new FilterSettings()), path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var reopened = MapProjectStore.Open(path).Document.Layers.OfType<FilterLayer>().Select(layer => layer.Settings).ToList();

        Assert.Equal(settings, reopened[0]);
        Assert.Equal(custom, reopened[1]);
        // A filter without the colour pass reads back neutral, exactly as files written before it.
        Assert.Equal(new FilterSettings(), reopened[2]);
        Assert.False(FilterColorEffects.IsActive(reopened[2]));
    }

    private static MapDocument Document(params FilterSettings[] filters) => new(
        DocumentId.New(), "filters", DateTimeOffset.UnixEpoch, new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            .. filters.Select((filter, index) => (MapLayer)new FilterLayer(LayerId.New(), "filter " + index, true, false, 1, filter)),
        ]);
}
