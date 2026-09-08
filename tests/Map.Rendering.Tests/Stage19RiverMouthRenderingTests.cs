using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage19RiverMouthRenderingTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void An_estuary_has_open_water_without_river_ink_or_wave_tails(bool pen, double scale)
    {
        var document = Scene(RiverStyle.Illustrated(pen));
        using var frame = Render(document, scale);
        using var plain = Render(Scene(RiverStyle.Illustrated(pen) with { BankWidth = 0,
            BankReliefWidth = 0, BankShadowOpacity = 0, ShallowWidth = 0, HighlightOpacity = 0 }), scale);
        // Both banks and the centre continue into the sea here. Coast waves are less than
        // 12 units from land; this open-water rectangle must contain neither ink nor highlights.
        for (var y = 92; y < 164; y++) for (var x = 280; x < 316; x++)
            Assert.Equal(plain.GetPixel((int)(x * scale), (int)(y * scale)),
                frame.GetPixel((int)(x * scale), (int)(y * scale)));
        var bank = frame.GetPixel((int)(150 * scale), (int)(105 * scale));
        Assert.NotEqual(new SKColor(212, 200, 151), bank); // The upstream drawn bank still exists.
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Mouth_rounding_removes_the_land_corner_without_a_translucent_wash(double scale)
    {
        var document = Scene(RiverStyle.Illustrated() with { Opacity = 0 });
        var land = document.Layers.OfType<LandLayer>().Single();
        document = document.WithLayer(land.Id, _ => land with { EdgeStyle = land.EdgeStyle with
            { OutlineWidth = 0, RippleCount = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0 } });
        using var frame = Render(document, scale);
        // With the river paint hidden, these pixels prove the actual land cut, not a painted
        // cover-up: the outer corner opens into the sea, nearby earth keeps its solid colour.
        Assert.Equal(new SKColor(116, 167, 161), frame.GetPixel((int)(254 * scale), (int)(106 * scale)));
        Assert.Equal(new SKColor(212, 200, 151), frame.GetPixel((int)(244 * scale), (int)(100 * scale)));
    }

    private static MapDocument Scene(RiverStyle style)
    {
        var land = new LandLayer(LayerId.New(), "land", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray()),
            new LandEdgeStyle { FillColor = "#FFD4C897", OutlineColor = "#FF514936", OutlineWidth = 1.2,
                RippleColor = "#90C5DBCA", RippleCount = 2, RippleSpacing = 5, RippleWidth = .65,
                InnerShadowOpacity = .06, OuterShadowOpacity = .08, ShadowBlur = 2 });
        var river = new River { Style = style with { EdgeRoughness = 0, TaperSource = false },
            Channels = [RiverEditor.NewChannel([new(40, 128), new(200, 128), new(400, 128)], 40)] };
        return new MapDocument(DocumentId.New(), "mouth", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 256, "px", style.WaterColor),
            [new BaseColorLayer(LayerId.New(), "sea", true, false, 1, style.WaterColor), land,
                new ObjectLayer(LayerId.New(), "river", true, false, 1, []) { Rivers = [river] }]);
    }

    private static SKBitmap Render(MapDocument document, double scale)
    {
        var info = new SKImageInfo((int)(512 * scale), (int)(256 * scale));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true, ViewScale = scale });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }
}
