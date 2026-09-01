using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage16BLayerRenderingTests
{
    [Fact]
    public void Mask_edit_overlay_is_editor_only_and_export_stays_exact()
    {
        var layerId = LayerId.New();
        var maskTile = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        for (var y = 25; y < 75; y++) for (var x = 25; x < 75; x++)
            maskTile[(y * TileMap.TileSize) + x] = 0;
        var layer = new ObjectLayer(layerId, "objects", true, false, 1,
            [Rectangle(10, 10, 100, 100, 0, "#FF2377BB")],
            new LayerMask(TileMap.Empty(1).With(new TileKey(0, 0), maskTile), true));
        var document = Document(layer);

        var ordinary = Render(document, new RenderOptions());
        var export = Render(document, new RenderOptions { IsExport = true });
        var edit = Render(document, new RenderOptions { MaskEditLayerId = layerId });

        Assert.Equal(ordinary, export);
        Assert.NotEqual(ordinary, edit);
    }

    [Fact]
    public void Sublayer_visibility_and_clipping_are_evaluated_in_renderer()
    {
        var lower = new ObjectLayer(LayerId.New(), "lower", true, false, 1,
            [Rectangle(30, 30, 40, 40, 0, "#FF222222")]);
        var upper = new ObjectLayer(LayerId.New(), "upper", true, false, 1,
            [Rectangle(0, 0, 120, 120, 2, "#FFFF3333")])
        {
            Effects = new LayerEffects { ClippingEnabled = true },
        };
        var clipped = Render(Document(lower, upper), new RenderOptions { IsExport = true });
        using var bitmap = SKBitmap.Decode(clipped);
        Assert.Equal(new SKColor(255, 255, 255, 255), bitmap.GetPixel(10, 10));
        Assert.NotEqual(new SKColor(255, 255, 255, 255), bitmap.GetPixel(45, 45));

        var hidden = upper with
        {
            Effects = new LayerEffects(),
            SublayerStates = [new ObjectSublayerState(2, Visible: false)],
        };
        Assert.Equal(Render(Document(), new RenderOptions { IsExport = true }),
            Render(Document(hidden), new RenderOptions { IsExport = true }));
    }

    private static SceneObject Rectangle(double x, double y, double width, double height, int sublayer, string color) =>
        new(ObjectId.New(), SceneObjectKind.Rectangle, "rect", x, y, width, height, 0,
            color, "#00000000", 0, true, false) { Sublayer = sublayer };

    private static MapDocument Document(params MapLayer[] layers) => new(
        DocumentId.New(), "render", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(128, 128, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "bg", true, false, 1, "#FFFFFFFF"), .. layers]);

    private static byte[] Render(MapDocument document, RenderOptions options)
    {
        using var surface = SKSurface.Create(new SKImageInfo(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        SceneRenderer.Render(surface.Canvas, document, options);
        using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
