using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class SoftLandRiverMouthTests
{
    private const int Width = 512, Height = 256;

    private static byte Coverage(int x) => (byte)Math.Round(255 * Math.Clamp(Math.Min((x - 64) / 128d, (448 - x) / 128d), 0, 1));

    private static River River(RiverStyle style) => new()
    {
        Style = style,
        Channels = [RiverEditor.NewChannel([new(16, 128), new(256, 128), new(496, 128)], 32)],
    };

    private static RiverStyle PlainStyle => new()
    {
        WaterColor = "#00000000", BankWidth = 0, BankReliefWidth = 0,
        BankShadowOpacity = 0, ShallowWidth = 0, HighlightOpacity = 0,
        TaperSource = false, CutsLand = true, ClipToLand = true, TextureId = null,
    };

    private static MapDocument Scene(bool water)
    {
        var tiles = TileMap.Empty(1);
        for (var tx = 0; tx < 2; tx++)
        {
            var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
            for (var y = 0; y < TileMap.TileSize; y++)
                for (var x = 0; x < TileMap.TileSize; x++) bytes[y * TileMap.TileSize + x] = Coverage(tx * TileMap.TileSize + x);
            tiles = tiles.With(new TileKey(tx, 0), bytes);
        }
        var land = new LandLayer(LayerId.New(), "Мягкая земля", true, false, 1, tiles, new LandEdgeStyle
        {
            FillColor = "#FF557733", OutlineWidth = 0, RippleCount = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0,
        });
        return new MapDocument(DocumentId.New(), "Мягкие устья", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Width, Height, "px", "#FFF2E9DB"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFF2E9DB"), land,
                new ObjectLayer(LayerId.New(), "Река", true, false, 1, []) { Rivers = water ? [River(PlainStyle)] : [] }]);
    }

    private static SKBitmap Render(MapDocument document, double scale)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)(Width * scale), (int)(Height * scale)))!;
        surface.Canvas.Scale((float)scale);
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { ViewScale = scale });
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void A_river_does_not_punch_holes_beside_its_channel_at_half_opacity_of_a_soft_coast(double scale)
    {
        using var before = Render(Scene(water: false), scale);
        using var cut = Render(Scene(water: true), scale);
        // Both the entry and exit cross a 128-unit soft edge. These points are outside the
        // river body, but inside visible ground; rounding must not turn them into open sea.
        foreach (var start in new[] { 100, 340 })
            for (var x = start; x <= start + 70; x += 2)
                foreach (var y in new[] { 96, 160 })
                {
                    var p = before.GetPixel((int)(x * scale), (int)(y * scale));
                    var q = cut.GetPixel((int)(x * scale), (int)(y * scale));
                    var difference = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
                    Assert.True(difference <= 2, $"premature mouth cut at ({x},{y}), scale {scale}: {p} -> {q}");
                }
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.4, false)]
    [InlineData(1.0, true)]
    [InlineData(0.4, true)]
    public void Banks_follow_visible_soft_land_without_repeatedly_multiplying_its_opacity(double layerOpacity, bool relief)
    {
        using var mask = new SKBitmap(Width, Height);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                mask.SetPixel(x, y, SKColors.White.WithAlpha((byte)Math.Round(Coverage(x) * layerOpacity)));
        using var land = SKImage.FromBitmap(mask);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height))!;
        surface.Canvas.Clear(SKColors.Transparent);
        var style = PlainStyle with
        {
            BankColor = "#FF000000", BankWidth = relief ? 0 : 4,
            BankReliefColor = "#FFB89A6C", BankReliefWidth = relief ? 8 : 0, BankHatchOpacity = 0,
        };
        RiverRenderer.Draw(surface.Canvas, River(style), 1, false,
            (canvas, paint) => canvas.DrawImage(land, 0, 0, paint));
        using var image = surface.Snapshot();
        using var frame = SKBitmap.FromImage(image);
        // Inspect the cliff itself as well as its ink, including the outer quarter of the
        // feathered land. A width-dependent inset or a second alpha mask ends it too early.
        var bankY = relief ? 108 : 112;
        foreach (var x in new[] { 88, 96, 128, 384, 416, 424 })
        {
            var alpha = frame.GetPixel(x, bankY).Alpha;
            var landAlpha = mask.GetPixel(x, bankY).Alpha;
            Assert.True(Math.Abs(alpha - landAlpha) <= 2,
                $"bank must fade with the ground at x={x}: bank {alpha}, land {landAlpha}, relief {relief}");
        }
        Assert.Equal(0, frame.GetPixel(455, bankY).Alpha);
        Assert.Equal(0, frame.GetPixel(55, bankY).Alpha);
    }
}
