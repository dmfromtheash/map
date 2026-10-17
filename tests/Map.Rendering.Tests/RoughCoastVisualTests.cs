using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The rough land brush through the real renderer: a stroke of rough dabs makes one jagged
/// coast with the usual coast styling, and a review image is written next to the test output
/// (MAP_ROUGH_COAST_PNG) for comparison with a drawn reference.
/// </summary>
public sealed class RoughCoastVisualTests
{
    [Theory]
    [InlineData(0, 0, "plain")]
    [InlineData(48, 0, "rough")]
    [InlineData(48, .6, "rough-smooth")]
    public void Rough_stroke_renders_one_jagged_landmass_with_a_styled_coast(double roughness, double smoothing, string name)
    {
        var land = LandLayer.CreateEmpty(LayerId.New(), "Земля");
        var settings = new BrushSettings
        {
            Radius = 90, Softness = roughness > 0 ? 0 : .3, Spacing = .15,
            RoughCellSize = roughness, RoughSmoothing = smoothing,
        };
        var stroke = new RasterStrokeSession(land.GeometryTiles, settings);
        foreach (var (x, y) in new[] { (180.0, 260.0), (300.0, 200.0), (430.0, 230.0), (560.0, 330.0), (640.0, 460.0), (520.0, 560.0), (360.0, 520.0) })
            stroke.AddPoint(x, y);
        var (_, after) = stroke.CommitCoverage(missingTileValue: 0, subtract: false);
        land = land with { GeometryTiles = land.GeometryTiles.WithMany(after) };

        var document = new MapDocument(DocumentId.New(), "Берег", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(800, 700, "px", "#FF9FB7C4"),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, "#FF9FB7C4"), land,
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);

        var info = new SKImageInfo(800, 700);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
        using var image = surface.Snapshot();

        var output = Environment.GetEnvironmentVariable("MAP_ROUGH_COAST_PNG");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(output, $"coast-{name}.png"));
            data.SaveTo(file);
        }

        using var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        var landPixels = 0;
        for (var y = 0; y < info.Height; y += 2)
            for (var x = 0; x < info.Width; x += 2)
                if (bitmap.GetPixel(x, y) != new SKColor(0x9F, 0xB7, 0xC4)) landPixels++;
        Assert.InRange(landPixels, 20_000, 120_000);
    }
}
