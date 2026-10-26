using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R12: the sea style draws the open water by its distance from the shore; the land stays as it was.</summary>
public sealed class SeaStyleRenderingTests
{
    private const int Size = 512;
    private const string Page = "#FF9DBBC4";

    private static MapDocument Island(SeaStyle? sea)
    {
        var tiles = TileMap.Empty(1);
        for (var ty = 0; ty < 2; ty++)
            for (var tx = 0; tx < 2; tx++)
            {
                var bytes = new byte[TileMap.TileSize * TileMap.TileSize];
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var wx = (tx * TileMap.TileSize) + x; var wy = (ty * TileMap.TileSize) + y;
                        var r = Math.Sqrt(Math.Pow(wx - 256, 2) + Math.Pow(wy - 256, 2));
                        bytes[(y * TileMap.TileSize) + x] = (byte)Math.Clamp((120 - r) * 128 + 128, 0, 255);
                    }
                tiles = tiles.With(new TileKey(tx, ty), bytes);
            }
        var land = new LandLayer(LayerId.New(), "Остров", true, false, 1, tiles,
            new LandEdgeStyle { FillColor = "#FFD6CFA8", FillTextureId = null, RippleCount = 2, InnerShadowOpacity = 0 });
        return new MapDocument(DocumentId.New(), "Море", DateTimeOffset.UnixEpoch, new CanvasDefinition(Size, Size, "px", Page),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, Page) { Sea = sea }, land]);
    }

    [Theory]
    [InlineData(SeaManner.PenLines)]
    [InlineData(SeaManner.Isobaths)]
    [InlineData(SeaManner.Engraved)]
    [InlineData(SeaManner.DepthBands)]
    [InlineData(SeaManner.BandsAndLines)]
    public void Each_manner_changes_the_sea_and_leaves_the_land(SeaManner manner)
    {
        Assert.True(SeaShader.Available, SeaShader.LastError);
        using var plain = Render(Island(null));
        using var styled = Render(Island(SeaStyle.For(manner)));
        var sea = 0;
        for (var y = 0; y < Size; y += 2)
            for (var x = 0; x < Size; x += 2)
            {
                var r = Math.Sqrt(Math.Pow(x - 256, 2) + Math.Pow(y - 256, 2));
                if (r < 100) Assert.Equal(plain.GetPixel(x, y), styled.GetPixel(x, y));
                else if (r > 140 && plain.GetPixel(x, y) != styled.GetPixel(x, y)) sea++;
            }
        Assert.True(sea > 3000, $"{manner}: the sea must change around the island, {sea} probes changed");
    }

    [Fact]
    public void Zero_strength_draws_the_plain_sea_and_the_style_is_bounded()
    {
        using var plain = Render(Island(null));
        using var none = Render(Island(SeaStyle.For(SeaManner.DepthBands) with { Strength = 0 }));
        for (var y = 0; y < Size; y += 4)
            for (var x = 0; x < Size; x += 4)
                Assert.Equal(plain.GetPixel(x, y), none.GetPixel(x, y));
        var wild = new SeaStyle { Manner = (SeaManner)42, Strength = 7, Scale = double.NaN, Ink = "nonsense" }.Bounded();
        Assert.Equal(SeaManner.PenLines, wild.Manner);
        Assert.Equal(1, wild.Strength);
        Assert.Equal(1, wild.Scale);
        Assert.Equal(SeaStyle.For(SeaManner.PenLines).Ink, wild.Ink);
    }

    /// <summary>MAP_SEA_PNG=&lt;folder&gt; writes the island in every manner for review.</summary>
    [Fact]
    public void Sea_sheet_for_review()
    {
        if (Environment.GetEnvironmentVariable("MAP_SEA_PNG") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        foreach (var (manner, _, _) in SeaStyle.Manners)
        {
            using var image = Render(Island(SeaStyle.For(manner)));
            LandCliffRenderingTests.Save(image, Path.Combine(folder, $"sea-{(int)manner}.png"));
        }
    }

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(Size, Size);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }
}
