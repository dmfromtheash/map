using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// R10 in the picture: «ближние поверх дальних» of a layer draws the lower object over the upper
/// one whatever the list order; the contact shadow and the underlay darken the ground under a
/// stamp to the south-east in a cold tone; a tint darkens the stamp itself; a stamp without them
/// is drawn exactly as before.
/// </summary>
public sealed class AreaBeautyRenderingTests
{
    private static readonly string Sha = new('c', 64);

    [Fact]
    public void Layer_depth_sort_draws_the_lower_object_over_the_upper_one()
    {
        var upper = Rect(100, 100, "#FFFF0000"); // red, lower edge 300, listed last
        var lower = Rect(150, 150, "#FF0000FF"); // blue, lower edge 350, listed first
        var plain = new ObjectLayer(LayerId.New(), "L", true, false, 1, [lower, upper]);
        Assert.Equal(SKColors.Red, Pixel(Doc(plain), 220, 220));
        var sorted = plain with { Effects = new() { DepthSort = true } };
        Assert.Equal(SKColors.Blue, Pixel(Doc(sorted), 220, 220));
    }

    [Fact]
    public void Contact_shadow_and_underlay_darken_the_ground_coldly_and_a_tint_darkens_the_stamp()
    {
        using var image = Square();
        var neutral = Stamp(null, null);
        var shaded = Stamp(null, new StampGroundShadow(.8, .5, 1, .9, .6));
        var tinted = Stamp("#FF808080", null);
        var paper = Pixel(Doc(Layer(neutral)), 230, 280, image);
        var ground = Pixel(Doc(Layer(shaded)), 230, 280, image);
        Assert.True(ground.Red < paper.Red - 40, $"ground {ground} paper {paper}");
        Assert.True(ground.Blue >= ground.Red, $"the shadow is cold: {ground}");
        // The stamp itself stays as it was outside its tint.
        Assert.Equal(Pixel(Doc(Layer(neutral)), 200, 150, image), Pixel(Doc(Layer(shaded)), 200, 150, image));
        var plainStamp = Pixel(Doc(Layer(neutral)), 200, 150, image);
        var tintStamp = Pixel(Doc(Layer(tinted)), 200, 150, image);
        Assert.InRange(tintStamp.Green, plainStamp.Green / 2 - 3, plainStamp.Green / 2 + 3);
    }

    private static SceneObject Rect(double x, double y, string color) =>
        new(ObjectId.New(), SceneObjectKind.Rectangle, "r", x, y, 200, 200, 0, color, "#00000000", 0, true, false);

    /// <summary>A stamp whose picture is an opaque disc in the upper half: the lower half shows the ground.</summary>
    private static SceneObject Stamp(string? tint, StampGroundShadow? shadow) =>
        new(ObjectId.New(), SceneObjectKind.Stamp, "s", 100, 100, 200, 200, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(Sha) { Tint = tint, GroundShadow = shadow } };

    private static SKImage Square()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = new SKColor(40, 200, 60), IsAntialias = true };
            canvas.DrawCircle(32, 18, 16, paint);
        }
        return SKImage.FromBitmap(bitmap);
    }

    private static ObjectLayer Layer(SceneObject item) => new(LayerId.New(), "L", true, false, 1, [item]);

    private static MapDocument Doc(ObjectLayer layer) => new(DocumentId.New(), "D", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(400, 400, "px", "#FFFFFFFF"), [new BaseColorLayer(LayerId.New(), "B", true, false, 1, "#FFE6DCC0"), layer]);

    private static SKColor Pixel(MapDocument document, int x, int y, SKImage? image = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, Stamps = image is null ? null : new Images(image) });
        using var bitmap = new SKBitmap(new SKImageInfo(400, 400));
        surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap.GetPixel(x, y);
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
