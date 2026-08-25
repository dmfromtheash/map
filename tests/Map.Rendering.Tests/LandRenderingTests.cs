using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class LandRenderingTests
{
    [Fact]
    public void Exact_land_pass_draws_style_while_fast_preview_draws_same_source_coverage()
    {
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 40; y < 210; y++)
        {
            for (var x = 40; x < 210; x++)
            {
                coverage[(y * TileMap.TileSize) + x] = 255;
            }
        }

        var land = new LandLayer(
            LayerId.New(), "land", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage),
            new LandEdgeStyle { FillColor = "#FF3C8C4C", RippleCount = 2, RippleSpacing = 8 });
        var document = new MapDocument(
            DocumentId.New(), "land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                land,
            ]);
        using var exact = Render(document, fast: false);
        using var preview = Render(document, fast: true);

        var centre = exact.GetPixel(128, 128);
        Assert.True(centre.Green > centre.Red);
        Assert.NotEqual(exact.GetPixel(34, 128), preview.GetPixel(34, 128));
    }

    private static SKBitmap Render(MapDocument document, bool fast)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { FastPreview = fast });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }
}
