using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>R22: heights shade and contour the land inside its coast and nowhere else; the default look of a flat land is unchanged.</summary>
public sealed class ReliefRenderingTests
{
    private const int Size = 512;

    private static LandLayer Island(TileMap? relief = null, double steep = 0, double rise = 1.2)
    {
        var tiles = TileMap.Empty(1);
        var heights = TileMap.Empty(1);
        for (var ty = 0; ty < 2; ty++)
            for (var tx = 0; tx < 2; tx++)
            {
                var cover = new byte[TileMap.TileSize * TileMap.TileSize];
                var hill = new byte[TileMap.TileSize * TileMap.TileSize];
                for (var y = 0; y < TileMap.TileSize; y++)
                    for (var x = 0; x < TileMap.TileSize; x++)
                    {
                        var wx = (tx * TileMap.TileSize) + x; var wy = (ty * TileMap.TileSize) + y;
                        var r = Math.Sqrt(Math.Pow(wx - 256, 2) + Math.Pow(wy - 256, 2));
                        cover[(y * TileMap.TileSize) + x] = (byte)Math.Clamp((200 - r) * 128 + 128, 0, 255);
                        // A hill reaching past the coast: the part over the sea must not show.
                        hill[(y * TileMap.TileSize) + x] = (byte)Math.Clamp((240 - r) * rise, 0, 255);
                    }
                tiles = tiles.With(new TileKey(tx, ty), cover);
                heights = heights.With(new TileKey(tx, ty), hill);
            }
        return new LandLayer(LayerId.New(), "Остров", true, false, 1, tiles,
            new LandEdgeStyle { FillColor = "#FFD6CFA8", FillTextureId = null, RippleCount = 0, InnerShadowOpacity = 0, OuterShadowOpacity = 0, ReliefSteepInk = steep })
        { ReliefTiles = relief ?? heights };
    }

    private static SKBitmap Render(LandLayer land)
    {
        var document = new MapDocument(DocumentId.New(), "Рельеф", DateTimeOffset.UnixEpoch, new CanvasDefinition(Size, Size, "px", "#FF9DBBC4"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FF9DBBC4"), land]);
        var info = new SKImageInfo(Size, Size);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions { IsExport = true });
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    [Fact]
    public void Heights_shade_the_land_and_stay_inside_the_coast()
    {
        Assert.True(ReliefShader.Available, ReliefShader.LastError);
        using var flat = Render(Island(TileMap.Empty(1)));
        using var hill = Render(Island());
        var inside = 0;
        for (var y = 80; y < 440; y += 3)
            for (var x = 80; x < 440; x += 3)
            {
                var r = Math.Sqrt(Math.Pow(x - 256, 2) + Math.Pow(y - 256, 2));
                if (r > 206) Assert.Equal(flat.GetPixel(x, y), hill.GetPixel(x, y));
                else if (r < 190 && flat.GetPixel(x, y) != hill.GetPixel(x, y)) inside++;
            }
        Assert.True(inside > 2000, $"the heights must show on the land: {inside} probes changed");
        // The light comes from the north-west: the south-east slope is darker than the north-west one.
        double Luma(SKColor c) => (0.3 * c.Red) + (0.59 * c.Green) + (0.11 * c.Blue);
        Assert.True(Luma(hill.GetPixel(330, 330)) < Luma(hill.GetPixel(182, 182)) - 6,
            $"shade {Luma(hill.GetPixel(330, 330)):0} vs light {Luma(hill.GetPixel(182, 182)):0}");
    }

    [Fact]
    public void Steep_hachures_add_ink_only_when_asked()
    {
        // A steep cone: three height steps per world unit.
        using var plain = Render(Island(rise: 3));
        using var hatched = Render(Island(steep: 1, rise: 3));
        var changed = 0;
        for (var y = 60; y < 460; y += 2)
            for (var x = 60; x < 460; x += 2)
                if (plain.GetPixel(x, y) != hatched.GetPixel(x, y)) changed++;
        Assert.True(changed > 200, $"hachures on the steep slopes: {changed}");
    }

    /// <summary>MAP_RELIEF_PNG=&lt;folder&gt; writes the hill with and without hachures for review.</summary>
    [Fact]
    public void Relief_sheet_for_review()
    {
        if (Environment.GetEnvironmentVariable("MAP_RELIEF_PNG") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        using var hill = Render(Island());
        LandCliffRenderingTests.Save(hill, Path.Combine(folder, "relief-hill.png"));
        using var hatched = Render(Island(steep: 1, rise: 3));
        LandCliffRenderingTests.Save(hatched, Path.Combine(folder, "relief-steep.png"));
    }
}
