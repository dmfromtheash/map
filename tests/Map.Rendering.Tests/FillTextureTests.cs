using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The owner removed the generated artwork. Saved material identifiers remain readable and fall
/// back to plain fill; geometric water shading is independent of the removed PNG files.
/// </summary>
public sealed class FillTextureTests
{
    [Fact]
    public void Removed_materials_and_unknown_ids_fall_back_without_a_shader()
    {
        Assert.NotEmpty(MapFillTextures.All);
        foreach (var texture in MapFillTextures.All)
        {
            Assert.Null(MapTextureLibrary.Image(texture.Id));
            Assert.Null(MapTextureLibrary.Shader(texture.Id, 128));
        }

        Assert.Null(MapTextureLibrary.Image("no-such-material"));
        Assert.Null(MapTextureLibrary.Shader("no-such-material", 128));
        Assert.True(MapFillTextures.IsValidId(null));
        Assert.False(MapFillTextures.IsValidId("no-such-material"));
    }

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
    public void A_removed_ground_material_preserves_the_plain_land()
    {
        var plainStyle = new LandEdgeStyle { FillColor = "#FF8CA36B", RippleCount = 0, OutlineWidth = 0 };
        var flatRiver = new RiverStyle { TextureId = null, ShallowWidth = 0, HighlightOpacity = 0, CutsLand = false, ClipToLand = false };
        using var plain = Render(Scene(plainStyle, flatRiver));
        using var textured = Render(Scene(
            plainStyle with { FillTextureId = "parchment", FillTextureOpacity = 0.6, FillTextureScale = 96 }, flatRiver));

        Assert.Equal(plain.Pixels, textured.Pixels);
    }

    [Fact]
    public void Water_keeps_its_bank_shading_without_the_removed_material()
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
        Assert.Equal(plain.Pixels, material.Pixels);
    }
}
