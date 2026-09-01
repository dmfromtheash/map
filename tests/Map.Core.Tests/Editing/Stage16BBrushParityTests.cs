using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage16BBrushParityTests
{
    [Fact]
    public void Live_preview_pixels_equal_commit_over_transparent_tiles()
    {
        var texture = new BrushTexture(2, 2,
        [
            255, 0, 0, 255, 0, 128, 0, 128,
            0, 0, 255, 255, 128, 128, 0, 128,
        ]);
        var settings = new BrushSettings
        {
            Radius = 18, Softness = .35, Opacity = .72, Texture = texture,
            TextureScale = 1.5, TextureRotationDegrees = 90,
            TextureOffsetX = 3, TextureOffsetY = -2, TextureOpacity = .65,
            HueDegrees = 24, Saturation = .3, Brightness = .08, Contrast = .2,
            Scatter = .2, SizeJitter = .15, OpacityJitter = .1, Seed = 481,
        };
        var stroke = new RasterStrokeSession(TileMap.Empty(4), settings);
        stroke.AddPoint(120, 110, .7);
        stroke.AddPoint(175, 145, .85);

        var (_, after) = stroke.Commit();

        foreach (var (key, coverage) in stroke.Coverage)
        {
            var preview = RasterStrokeSession.RenderPreviewTile(settings, key, coverage);
            Assert.Equal(after[key], preview);
        }
    }

    [Fact]
    public void Pressure_softness_is_separate_and_seeded_randomness_is_stable()
    {
        var settings = new BrushSettings
        {
            Radius = 20, Softness = 1, PressureAffectsSoftness = true,
            Scatter = .4, SizeJitter = .3, OpacityJitter = .2, Seed = 77,
        };
        var first = Paint(settings, .25);
        var second = Paint(settings, .25);
        var hardPressure = Paint(settings, 1);

        Assert.All(first.Coverage, pair => Assert.Equal(pair.Value, second.Coverage[pair.Key]));
        Assert.True(first.Coverage.Values.SelectMany(value => value).Sum(value => (long)value)
                    > hardPressure.Coverage.Values.SelectMany(value => value).Sum(value => (long)value));
    }

    private static RasterStrokeSession Paint(BrushSettings settings, double pressure)
    {
        var stroke = new RasterStrokeSession(TileMap.Empty(4), settings);
        stroke.AddPoint(200, 200, pressure); stroke.AddPoint(260, 220, pressure);
        return stroke;
    }
}
