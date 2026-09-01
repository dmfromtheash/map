using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage15StampAppearanceRenderingTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Layer_shadow_is_below_the_whole_object_layer_while_object_shadow_is_local()
    {
        using var source = new SolidStampSource();
        using var objectShadow = Render(Document(StampShadowPlacement.Object), source);
        using var layerShadow = Render(Document(StampShadowPlacement.Layer), source);

        var localPixel = objectShadow.GetPixel(60, 60);
        var behindLayerPixel = layerShadow.GetPixel(60, 60);
        Assert.True(localPixel.Red > localPixel.Blue + 100);
        Assert.True(behindLayerPixel.Blue > behindLayerPixel.Red + 100);
    }

    [Fact]
    public void Preview_layer_shadow_uses_owning_layer_prepass_and_combines_alpha_once()
    {
        var layerId = LayerId.New();
        var document = new MapDocument(
            DocumentId.New(), "preview", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(120, 100, "px", "#FF000000"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FF000000"),
                new ObjectLayer(layerId, "objects", true, false, 0.5, []),
            ]);
        var preview = Stamp(StampShadowPlacement.Layer) with { ObjectOpacity = 0.5 };
        using var source = new SolidStampSource();
        using var bitmap = Render(document, source, new RenderOptions
        {
            PlacementPreviewObjects = [preview],
            PlacementPreviewLayerId = layerId,
            Stamps = source,
            ViewScale = 1,
        });

        // #FFFF0000 × shadow 1 × object .5 × layer .5 = about 25 % red.
        Assert.InRange(bitmap.GetPixel(60, 60).Red, 54, 74);
    }

    [Fact]
    public void Every_supported_blend_key_has_an_exact_renderer_mapping()
    {
        using var source = new SolidStampSource(new SKColor(210, 50, 130));
        SKColor? normalPixel = null;
        foreach (var blend in BlendModes.Supported)
        {
            var stamp = Stamp(StampShadowPlacement.Object) with
            {
                Stamp = Stamp(StampShadowPlacement.Object).Stamp! with
                {
                    Blend = blend,
                    ShadowOpacity = 0,
                },
            };
            var layerId = LayerId.New();
            var document = new MapDocument(
                DocumentId.New(), blend, DateTimeOffset.UnixEpoch,
                new CanvasDefinition(120, 100, "px", "#FF202020"),
                [
                    new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FF202020"),
                    new ObjectLayer(layerId, "objects", true, false, 1, [stamp]),
                ]);
            using var rendered = Render(document, source);
            var pixel = rendered.GetPixel(10, 10);
            Assert.NotEqual(SKColors.Transparent, pixel);
            if (blend == BlendModes.Normal) normalPixel = pixel;
            if (blend is "ColorBurn" or "ColorDodge" or "Exclusion" or "Hue"
                or "Saturation" or "Color" or "Luminosity")
            {
                Assert.NotEqual(normalPixel, pixel);
            }
        }
    }

    private static MapDocument Document(StampShadowPlacement placement)
    {
        var layerId = LayerId.New();
        var blue = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "blue", 50, 50, 30, 30, 0,
            "#FF0000FF", "#00000000", 0, true, false);
        return new MapDocument(
            DocumentId.New(), "shadow", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(120, 100, "px", "#FF000000"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FF000000"),
                new ObjectLayer(layerId, "objects", true, false, 1, [blue, Stamp(placement)]),
            ]);
    }

    private static SceneObject Stamp(StampShadowPlacement placement) => new(
        ObjectId.New(), SceneObjectKind.Stamp, "stamp", 0, 0, 20, 20, 0,
        "#FFFFFFFF", "#00000000", 0, true, false)
    {
        Stamp = new StampStyle(Sha)
        {
            ReferenceWidth = 20,
            ReferenceHeight = 20,
            ShadowOffsetX = 50,
            ShadowOffsetY = 50,
            ShadowBlur = 1,
            ShadowOpacity = 1,
            ShadowPlacement = placement,
            ShadowColor = "#FFFF0000",
        },
    };

    private static SKBitmap Render(
        MapDocument document, IStampImageSource source, RenderOptions? supplied = null)
    {
        var info = new SKImageInfo(120, 100);
        using var surface = SKSurface.Create(info)!;
        var options = supplied ?? new RenderOptions { Stamps = source, ViewScale = 1 };
        SceneRenderer.Render(surface.Canvas, document, options);
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    private sealed class SolidStampSource : IStampImageSource, IDisposable
    {
        private readonly SKImage _image;

        public SolidStampSource(SKColor? color = null)
        {
            using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
            surface.Canvas.Clear(color ?? SKColors.White);
            _image = surface.Snapshot();
        }

        public SKImage? TryGetImage(string sha256) => sha256 == Sha ? _image : null;

        public void Dispose() => _image.Dispose();
    }
}
