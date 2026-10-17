using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class FilterColorRenderingTests
{
    private const string Background = "#FF506070";

    [Theory]
    [InlineData("night")]
    [InlineData("sepia")]
    [InlineData("grey")]
    [InlineData("vivid")]
    [InlineData("winter")]
    public void Preset_pixels_match_the_core_formula(string presetId)
    {
        var settings = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset(presetId)!);
        using var before = Render(Document(null));
        using var after = Render(Document(settings));

        var original = before.GetPixel(50, 50);
        var expected = FilterColorEffects.Apply(settings, original.Red, original.Green, original.Blue);
        var actual = after.GetPixel(50, 50);
        Assert.NotEqual(original, actual);
        Assert.InRange(Math.Abs(actual.Red - expected.R), 0, 3);
        Assert.InRange(Math.Abs(actual.Green - expected.G), 0, 3);
        Assert.InRange(Math.Abs(actual.Blue - expected.B), 0, 3);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void Gradient_strength_mixes_toward_the_gradient_colour(double strength)
    {
        var settings = new FilterSettings { GradientMap = new() { ShadowColor = "#FF2B1A0E", MidtoneColor = "#FF9A6B3F", HighlightColor = "#FFF5E2C0", Strength = strength } };
        using var after = Render(Document(settings));
        var expected = FilterColorEffects.Apply(settings, 0x50, 0x60, 0x70);
        var actual = after.GetPixel(50, 50);
        Assert.InRange(Math.Abs(actual.Red - expected.R) + Math.Abs(actual.Green - expected.G) + Math.Abs(actual.Blue - expected.B), 0, 6);
        Assert.True(actual.Red > actual.Blue, $"{actual} is not warm");
    }

    [Fact]
    public void Zero_strength_and_a_neutral_filter_leave_the_map_unchanged()
    {
        var sepia = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset("sepia")!);
        using var before = Render(Document(null));
        using var zero = Render(Document(sepia with { ColorStrength = 0 }));
        using var neutral = Render(Document(new FilterSettings()));
        Assert.Equal(before.GetPixel(50, 50), zero.GetPixel(50, 50));
        Assert.Equal(before.GetPixel(50, 50), neutral.GetPixel(50, 50));
    }

    [Fact]
    public void Layer_opacity_mixes_the_recoloured_scene_with_the_original()
    {
        var grey = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset("grey")!);
        using var before = Render(Document(null));
        using var full = Render(Document(grey));
        using var half = Render(Document(grey, opacity: 0.5));
        var o = before.GetPixel(50, 50); var f = full.GetPixel(50, 50); var h = half.GetPixel(50, 50);
        Assert.InRange(h.Blue, Math.Min(o.Blue, f.Blue), Math.Max(o.Blue, f.Blue));
        Assert.NotEqual(o, h);
        Assert.NotEqual(f, h);
    }

    [Fact]
    public void Sharpness_strengthens_an_edge_and_the_export_path_matches_the_idle_canvas()
    {
        var sharp = FilterColorEffects.WithPreset(new FilterSettings(), FilterColorEffects.Preset("sharp")!);
        using var plain = Render(Document(null, edge: true));
        using var canvas = Render(Document(sharp, edge: true));
        using var export = Render(Document(sharp, edge: true), isExport: true);
        // Next to the edge the light side gets lighter and the dark side darker.
        Assert.True(canvas.GetPixel(99, 50).Red > plain.GetPixel(99, 50).Red);
        Assert.True(canvas.GetPixel(100, 50).Red < plain.GetPixel(100, 50).Red);
        for (var x = 90; x < 110; x++) Assert.Equal(canvas.GetPixel(x, 50), export.GetPixel(x, 50));
    }

    private static MapDocument Document(FilterSettings? filter, double opacity = 1, bool edge = false)
    {
        List<MapLayer> layers = [new BaseColorLayer(LayerId.New(), "background", true, false, 1, Background)];
        if (edge)
            layers.Add(new ObjectLayer(LayerId.New(), "edge", true, false, 1,
                [new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "dark", 100, 0, 100, 200, 0,
                    "#FF202830", "#00000000", 0, true, false)]));
        if (filter is not null) layers.Add(new FilterLayer(LayerId.New(), "filter", true, false, opacity, filter));
        return new MapDocument(DocumentId.New(), "filter colour", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(200, 200, "px", Background), layers);
    }

    private static SKBitmap Render(MapDocument document, bool isExport = false)
    {
        var info = new SKImageInfo(200, 200);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = isExport });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }
}
