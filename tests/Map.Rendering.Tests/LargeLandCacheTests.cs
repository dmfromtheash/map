using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class LargeLandCacheTests
{
    [Fact]
    public void Alpha8_land_cache_matches_uncached_land_pixels()
    {
        var coverage = new byte[TileMap.TileSize * TileMap.TileSize];
        for (var y = 48; y < 208; y++)
        for (var x = 32; x < 224; x++)
        {
            coverage[(y * TileMap.TileSize) + x] = 255;
        }

        var land = new LandLayer(
            LayerId.New(), "land", true, false, 1,
            TileMap.Empty(1).With(new TileKey(0, 0), coverage),
            new LandEdgeStyle { FillColor = "#FF3C8C4C", RippleCount = 0 });
        var document = new MapDocument(
            DocumentId.New(), "cached land", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(256, 256, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                land,
            ]);

        using var expected = Render(document, cache: null);
        using var cache = new TileImageCache();
        using var actual = Render(document, cache);

        Assert.InRange(MaxChannelDelta(Pixels(expected), Pixels(actual)), 0, 1);
        Assert.Equal(TileMap.TileSize * TileMap.TileSize, cache.Bytes);
    }

    [Fact]
    public void One_frame_can_exceed_the_steady_cache_without_disposing_an_image_used_by_that_frame()
    {
        using var cache = new TileImageCache(capacity: 1, byteCapacity: 65_536);
        var layer = LayerId.New();
        var first = Enumerable.Repeat((byte)255, 65_536).ToArray();
        var second = Enumerable.Repeat((byte)128, 65_536).ToArray();

        cache.BeginFrame();
        _ = cache.Get(layer, new TileKey(0, 0), TileImageKind.LandCoverage, first);
        _ = cache.Get(layer, new TileKey(1, 0), TileImageKind.LandCoverage, second);

        Assert.Equal(2, cache.Count);
        Assert.Equal(0, cache.Evictions);
        Assert.Equal(0, cache.Disposals);

        cache.EndFrame();

        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(1, cache.Disposals);
    }

    private static SKBitmap Render(MapDocument document, TileImageCache? cache)
    {
        var info = new SKImageInfo(256, 256);
        using var surface = SKSurface.Create(info)!;
        cache?.BeginFrame();
        try
        {
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions { TileCache = cache });
        }
        finally
        {
            cache?.EndFrame();
        }

        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private static SKColor[] Pixels(SKBitmap bitmap)
    {
        var pixels = new SKColor[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            pixels[(y * bitmap.Width) + x] = bitmap.GetPixel(x, y);
        }
        return pixels;
    }

    private static int MaxChannelDelta(IReadOnlyList<SKColor> expected, IReadOnlyList<SKColor> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        var maximum = 0;
        for (var index = 0; index < expected.Count; index++)
        {
            maximum = Math.Max(maximum, Math.Abs(expected[index].Red - actual[index].Red));
            maximum = Math.Max(maximum, Math.Abs(expected[index].Green - actual[index].Green));
            maximum = Math.Max(maximum, Math.Abs(expected[index].Blue - actual[index].Blue));
            maximum = Math.Max(maximum, Math.Abs(expected[index].Alpha - actual[index].Alpha));
        }
        return maximum;
    }
}
