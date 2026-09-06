using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class StampSamplingRegressionTests
{
    [Fact]
    public void A_larger_neighbour_does_not_change_an_earlier_stamps_sampled_image()
    {
        using var source = SKSurface.Create(new SKImageInfo(512, 512))!;
        source.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint();
        for (var x = 0; x < 512; x++)
        {
            paint.Color = x % 3 == 0 ? SKColors.DarkRed : SKColors.LimeGreen;
            source.Canvas.DrawRect(x, 0, 1, 512, paint);
        }
        using var image = source.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        SceneObject Stamp(double x, double width) => new(ObjectId.New(), SceneObjectKind.Stamp,
            "Sample", x, 0, width, width, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(sha) };
        var first = Stamp(0, 32);
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [first]);
        var document = new MapDocument(DocumentId.New(), "Sampling", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF"), layer],
            [new MapAssetReference(sha, "Sample", "image/png", ".png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "test", "test", bytes)]);
        var withNeighbour = document.WithLayers([document.BaseColor,
            new ObjectLayer(layer.Id, layer.Name, true, false, 1, [first, Stamp(128, 96)])]);
        using var originals = new ExportStampImageSource(document, null, null);
        using var before = SKSurface.Create(new SKImageInfo(256, 256))!;
        using var after = SKSurface.Create(new SKImageInfo(256, 256))!;
        SceneRenderer.Render(before.Canvas, document, new RenderOptions { Stamps = originals });
        SceneRenderer.Render(after.Canvas, withNeighbour, new RenderOptions { Stamps = originals });
        using var beforeImage = before.Snapshot();
        using var afterImage = after.Snapshot();
        using var a = SKBitmap.FromImage(beforeImage);
        using var b = SKBitmap.FromImage(afterImage);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
            Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
    }
}
