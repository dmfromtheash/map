using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R12: a waterfall, rapids or a ford is drawn on the water at its node and nowhere else.</summary>
public sealed class RiverFeatureRenderingTests
{
    private const int Size = 400;

    // One course for every case: the natural banks wobble by the ids, so the ids must be the same.
    private static readonly River Plain = new()
    {
        Name = "Р", Style = RiverStyle.Natural() with { TextureId = null },
        Channels = [RiverEditor.NewChannel([new(20, 200), new(200, 200), new(380, 200)], 60)],
    };

    private static River Course(RiverNodeFeature feature) =>
        RiverEditor.SetNodeFeature(Plain, Plain.Channels[0].Id, 1, feature);

    [Theory]
    [InlineData(RiverNodeFeature.Waterfall)]
    [InlineData(RiverNodeFeature.Rapids)]
    [InlineData(RiverNodeFeature.Ford)]
    public void A_feature_changes_the_water_near_its_node_only(RiverNodeFeature feature)
    {
        using var plain = Render(Course(RiverNodeFeature.None));
        using var marked = Render(Course(feature));
        int Changed(int left, int right)
        {
            var count = 0;
            for (var y = 160; y < 240; y++)
                for (var x = left; x < right; x++)
                    if (plain.GetPixel(x, y) != marked.GetPixel(x, y)) count++;
            return count;
        }
        Assert.True(Changed(150, 260) > 300, $"{feature}: the feature must show at the node, {Changed(150, 260)} pixels");
        Assert.Equal(0, Changed(20, 100));
        // Nothing is drawn outside the water.
        for (var x = 150; x < 260; x += 2)
        {
            Assert.Equal(plain.GetPixel(x, 120), marked.GetPixel(x, 120));
            Assert.Equal(plain.GetPixel(x, 280), marked.GetPixel(x, 280));
        }
    }

    /// <summary>MAP_RIVER_FEATURES_PNG=&lt;folder&gt; writes the three features close up for review.</summary>
    [Fact]
    public void Feature_sheet_for_review()
    {
        if (Environment.GetEnvironmentVariable("MAP_RIVER_FEATURES_PNG") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        foreach (var feature in new[] { RiverNodeFeature.Waterfall, RiverNodeFeature.Rapids, RiverNodeFeature.Ford })
            foreach (var scale in new[] { 1.0, 2.5 })
            {
                using var bitmap = Render(Course(feature), scale);
                LandCliffRenderingTests.Save(bitmap, Path.Combine(folder, $"{feature.ToString().ToLowerInvariant()}-{scale * 100:0}.png"));
            }
    }

    private static SKBitmap Render(River river, double scale = 1)
    {
        var layer = new ObjectLayer(LayerId.New(), "Вода", true, false, 1, []) { Rivers = [river] };
        var document = new MapDocument(DocumentId.New(), "river", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Size, Size, "px", "#FFE9E1CC"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFE9E1CC"), layer]);
        var info = new SKImageInfo((int)(Size * scale), (int)(Size * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }
}
