using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// Map's own fill materials: every declared texture really is embedded, a chosen material changes
/// what the land and the water look like, an unknown id falls back to the plain fill instead of
/// failing, and the shallows lighten the water where it meets the bank.
/// </summary>
public sealed class FillTextureTests
{
    [Fact]
    public void Every_declared_material_is_embedded_and_seamless()
    {
        Assert.NotEmpty(MapFillTextures.All);
        foreach (var texture in MapFillTextures.All)
        {
            var image = MapTextureLibrary.Image(texture.Id);
            Assert.True(image is not null, $"texture {texture.Id} is declared but not embedded");
            Assert.Equal(image!.Width, image.Height);

            // Seamless means the wrap is no harsher than the inside of the tile: the difference
            // across the join must not exceed a typical neighbouring-pixel difference.
            using var bitmap = SKBitmap.FromImage(image);
            double wrap = 0, inside = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                wrap += Delta(bitmap.GetPixel(0, y), bitmap.GetPixel(bitmap.Width - 1, y));
                inside += Delta(bitmap.GetPixel(bitmap.Width / 2, y), bitmap.GetPixel(bitmap.Width / 2 - 1, y));
            }
            Assert.True(wrap <= inside * 1.5,
                $"texture {texture.Id} shows a seam: wrap {wrap / bitmap.Height:F2} vs inside {inside / bitmap.Height:F2}");
        }

        Assert.Null(MapTextureLibrary.Image("no-such-material"));
        Assert.Null(MapTextureLibrary.Shader("no-such-material", 128));
        Assert.True(MapFillTextures.IsValidId(null));
        Assert.False(MapFillTextures.IsValidId("no-such-material"));
    }

    private static double Delta(SKColor a, SKColor b) =>
        (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)) / 3.0;

    private static MapDocument Scene(LandEdgeStyle landStyle, RiverStyle riverStyle)
    {
        // Land over the left 200 columns only, so there is somewhere on the canvas that is not land.
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 0; y < TileMap.TileSize; y++)
            for (var x = 0; x < 200; x++) coverage[y * TileMap.TileSize + x] = 255;
        var land = new LandLayer(LayerId.New(), "Земля", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage), landStyle);
        var river = new River
        {
            Name = "Река",
            Style = riverStyle,
            Channels = [RiverEditor.NewChannel([new VectorPoint(10, 128), new VectorPoint(246, 128)], 60)],
        };
        river.Validate();
        var objects = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, []) { Rivers = [river] };
        return new MapDocument(DocumentId.New(), "Материалы", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), land, objects]);
    }

    private static SKBitmap Render(MapDocument document)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        SceneRenderer.Render(surface.Canvas, document, new RenderOptions());
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    [Fact]
    public void A_ground_material_varies_the_land_without_leaving_it()
    {
        var plainStyle = new LandEdgeStyle { FillColor = "#FF8CA36B", RippleCount = 0, OutlineWidth = 0 };
        var flatRiver = new RiverStyle { TextureId = null, ShallowWidth = 0, CutsLand = false, ClipToLand = false };
        using var plain = Render(Scene(plainStyle, flatRiver));
        using var textured = Render(Scene(
            plainStyle with { FillTextureId = "parchment", FillTextureOpacity = 0.6, FillTextureScale = 96 }, flatRiver));

        // Flat land is one colour; the material makes it vary from place to place.
        Assert.Equal(plain.GetPixel(40, 40), plain.GetPixel(150, 210));
        var spread = 0.0;
        var first = textured.GetPixel(40, 40);
        for (var i = 0; i < 6; i++) spread += Delta(first, textured.GetPixel(40 + i * 20, 40 + i * 24));
        Assert.True(spread > 4, $"the ground material has to vary the land, spread {spread:F2}");

        // And it stays inside the coverage: off the land nothing changed at all.
        Assert.Equal(plain.GetPixel(240, 240), textured.GetPixel(240, 240));
    }

    [Fact]
    public void Water_gets_a_material_and_lightens_towards_the_bank()
    {
        var landStyle = new LandEdgeStyle { FillColor = "#FF8CA36B", RippleCount = 0, OutlineWidth = 0 };
        var flat = new RiverStyle
        {
            TextureId = null, ShallowWidth = 0, HighlightOpacity = 0,
            CutsLand = false, ClipToLand = false, BankShadowOpacity = 0,
        };
        using var plain = Render(Scene(landStyle, flat));
        var middle = plain.GetPixel(128, 128);
        Assert.Equal(middle, plain.GetPixel(128, 118));

        using var shallows = Render(Scene(landStyle, flat with { ShallowWidth = 6 }));
        var nearBank = shallows.GetPixel(128, 128 - 25);
        var centre = shallows.GetPixel(128, 128);
        Assert.True(nearBank.Red > centre.Red && nearBank.Green > centre.Green && nearBank.Blue > centre.Blue,
            $"water has to lighten towards the bank, centre {centre} near bank {nearBank}");

        using var material = Render(Scene(landStyle, flat with { TextureId = "water-open", TextureOpacity = 0.7, TextureScale = 64 }));
        var spread = 0.0;
        var first = material.GetPixel(40, 128);
        for (var i = 0; i < 6; i++) spread += Delta(first, material.GetPixel(40 + i * 32, 128));
        Assert.True(spread > 2, $"the water material has to vary the surface, spread {spread:F2}");
        // The material never escapes the channel: the land above it is untouched.
        Assert.Equal(plain.GetPixel(128, 40), material.GetPixel(128, 40));
    }
}
