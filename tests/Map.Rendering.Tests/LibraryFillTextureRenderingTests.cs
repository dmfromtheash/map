using System.Security.Cryptography;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>Library textures are drawn from the bytes a source supplies, checked against their hash.</summary>
public sealed class LibraryFillTextureRenderingTests
{
    [Fact]
    public void A_library_texture_becomes_a_repeating_shader_and_a_brush_sampler()
    {
        var (png, id) = Tile(new SKColor(200, 40, 40), 64);
        using (MapTextureLibrary.AddAssetSource(sha => sha == MapFillTextures.AssetSha256(id) ? png : null))
        {
            var image = Assert.IsAssignableFrom<SKImage>(MapTextureLibrary.Image(id));
            Assert.Equal(64, image.Width);
            using var shader = MapTextureLibrary.Shader(id, 128);
            Assert.NotNull(shader);
            var brush = Assert.IsType<BrushTexture>(MapTextureLibrary.BrushTexture(id));
            Assert.Equal((64, 64), (brush.Width, brush.Height));

            using var surface = SKSurface.Create(new SKImageInfo(32, 32))!;
            surface.Canvas.Clear(SKColors.White);
            Assert.True(MapTextureLibrary.DrawFill(surface.Canvas, id, 128, 1, new SKRect(-500, -500, 500, 500)));
            using var bitmap = new SKBitmap(new SKImageInfo(32, 32));
            surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
            var pixel = bitmap.GetPixel(16, 16);
            Assert.InRange(pixel.Red, 190, 210);
            Assert.InRange(pixel.Green, 30, 50);
        }
    }

    [Fact]
    public void A_texture_no_source_can_supply_or_whose_bytes_do_not_match_has_no_shader()
    {
        var (png, id) = Tile(new SKColor(10, 120, 200), 16);
        var (other, _) = Tile(new SKColor(11, 121, 201), 16);
        using (MapTextureLibrary.AddAssetSource(_ => other))
        {
            Assert.Null(MapTextureLibrary.Image(id));
            Assert.Null(MapTextureLibrary.Shader(id, 64));
            Assert.False(MapTextureLibrary.DrawFill(SKSurface.Create(new SKImageInfo(4, 4))!.Canvas, id, 64, 1, new SKRect(0, 0, 4, 4)));
        }
        // Once a source that has the bytes appears (a drive attached, a map opened), it is asked again.
        using (MapTextureLibrary.AddAssetSource(_ => png))
            Assert.NotNull(MapTextureLibrary.Image(id));
    }

    [Fact]
    public void A_large_library_texture_is_decoded_at_a_bounded_size()
    {
        var (png, id) = Tile(new SKColor(90, 90, 30), MapTextureLibrary.MaxAssetTextureSide * 2);
        using (MapTextureLibrary.AddAssetSource(_ => png))
            Assert.Equal(MapTextureLibrary.MaxAssetTextureSide, MapTextureLibrary.Image(id)!.Width);
    }

    private static (byte[] Png, string Id) Tile(SKColor color, int side)
    {
        using var surface = SKSurface.Create(new SKImageInfo(side, side))!;
        // Each test has its own colour, so its bytes (and its cache entry) are its own.
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        return (png, MapFillTextures.AssetId(Convert.ToHexStringLower(SHA256.HashData(png))));
    }
}
