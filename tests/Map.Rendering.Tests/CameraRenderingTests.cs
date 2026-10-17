using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class CameraRenderingTests
{
    [Fact]
    public void Moving_camera_composites_kept_translucent_stamp_tiles_once()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(16, 16))!;
        sprite.Canvas.Clear(SKColors.ForestGreen);
        using var image = sprite.Snapshot();
        var objects = Enumerable.Range(0, 2100).Select(i => new SceneObject(ObjectId.New(),
            SceneObjectKind.Stamp, "Tree", i % 70 * 16, i / 70 * 16, 16, 16, 0,
            "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('b', 64)), ObjectOpacity = .6 }).ToArray();
        var document = Document(2048, 2048,
            new ObjectLayer(LayerId.New(), "Objects", true, false, 1, objects));
        var view = new WorldBounds(0, 0, 1024, 512);
        using var cache = new ObjectOverviewCache();
        using var warm = Draw(document, view, new RenderOptions
            { Stamps = new Images(image), ObjectCache = cache, VisibleWorld = view });
        using var moving = Draw(document, view, new RenderOptions
            { Stamps = new Images(image), ObjectCache = cache, VisibleWorld = view, FastTerrain = true });
        SamePixel(warm.GetPixel(100, 100), moving.GetPixel(100, 100));
    }

    [Fact]
    public void Partial_terrain_reuse_never_composites_translucent_land_twice()
    {
        var bytes = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1).With(new TileKey(0, 0), bytes)
            .With(new TileKey(1, 0), bytes).With(new TileKey(2, 0), bytes);
        var land = new LandLayer(LayerId.New(), "Land", true, false, .5, tiles,
            new LandEdgeStyle { FillColor = "#FF7FA35A", FillTextureId = null,
                OutlineWidth = 0, RippleCount = 0, OuterShadowOpacity = 0, InnerShadowOpacity = 0 });
        var document = Document(768, 256, land);
        using var terrain = new RenderImageCache();
        var initial = new WorldBounds(0, 0, 256, 256);
        using var warm = Draw(document, initial, new RenderOptions { Terrain = terrain, VisibleWorld = initial });
        var next = new WorldBounds(128, 0, 384, 256);
        using var moving = Draw(document, next, new RenderOptions { Terrain = terrain, VisibleWorld = next, FastTerrain = true });
        using var direct = Draw(document, next, new RenderOptions { VisibleWorld = next });
        SamePixel(direct.GetPixel(64, 100), moving.GetPixel(64, 100));
        SamePixel(direct.GetPixel(220, 100), moving.GetPixel(220, 100));
    }

    [Fact]
    public void Very_small_scales_keep_distinct_pixel_dimensions_in_image_cache()
    {
        using var cache = new RenderImageCache();
        var view = new WorldBounds(0, 0, 5000, 5000);
        var part = new object();
        SKImage Build(int width, int height)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
            surface.Canvas.Clear(SKColors.Blue);
            return surface.Snapshot();
        }
        var first = cache.Get([part], view, .006, null, Build)!;
        var second = cache.Get([part], view, .007, null, Build)!;
        Assert.Equal(RenderImageCache.SizeOf(view, .007).Width, second.Width);
        Assert.NotEqual(first.UniqueId, second.UniqueId);
    }

    private static MapDocument Document(int width, int height, MapLayer content) => new(
        DocumentId.New(), "Camera", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(width, height, "px", "#FF6E9FBF"),
        [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FF6E9FBF"), content]);

    private static SKBitmap Draw(MapDocument document, WorldBounds view, RenderOptions options)
    {
        var info = new SKImageInfo((int)(view.Right - view.Left), (int)(view.Bottom - view.Top));
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(surface.Canvas, document, options);
        var bitmap = new SKBitmap(info);
        surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return bitmap;
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }

    private static void SamePixel(SKColor expected, SKColor actual)
    {
        // Cached premultiplied pixels and their sampling may round one channel once more.
        // The original double-composition defects differed by 19-37 levels at these probes.
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 1);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 1);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 1);
        Assert.Equal(expected.Alpha, actual.Alpha);
    }
}
