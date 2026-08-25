using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class StampRenderingTests
{
    [Fact]
    public void Missing_stamp_draws_visible_placeholder_and_reports_it()
    {
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "missing", 20, 20, 100, 100, 0,
            "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(new string('a', 64)),
        };
        var document = new MapDocument(
            DocumentId.New(), "missing", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(200, 200, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "objects", true, false, 1, [stamp]),
            ]);
        var stats = new RenderStats();
        using var surface = SKSurface.Create(new SKImageInfo(200, 200))!;

        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stats = stats });
        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);

        Assert.Equal(1, stats.MissingAssets);
        var centre = bitmap.GetPixel(70, 70);
        Assert.True(centre.Red > centre.Green, "Placeholder is not visibly red.");
    }
}
