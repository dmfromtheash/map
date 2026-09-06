using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class OverviewLifetimeTests
{
    [Fact]
    public void Dense_missing_stamps_keep_a_visible_overview_without_a_dashed_path_per_object()
    {
        var stamps = Enumerable.Range(0, 20000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "missing", (i % 200) * 32, (i / 200) * 32, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('a', 64)) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "missing", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
                new ObjectLayer(LayerId.New(), "missing", true, false, 1, stamps)]);
        using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
        using var cache = new ObjectOverviewCache();
        surface.Canvas.Scale(1f / 64);
        var options = new RenderOptions { ViewScale = 1d / 64, VisibleWorld = new(0, 0, 8192, 8192),
            LodTier = RenderLodTier.Overview, ObjectCache = cache, Stats = new(), SelectedObjectId = stamps[0].Id };
        SceneRenderer.Render(surface.Canvas, document, options);
        Assert.Equal(20000, options.Stats.MissingAssets);
        Assert.Equal(20000, options.Stats.OverviewObjects);
        Assert.Equal(1, options.Stats.ExactObjects);
        using var snapshot = surface.Snapshot();
        using var rendered = SKBitmap.FromImage(snapshot);
        Assert.NotEqual(SKColors.White, rendered.GetPixel(10, 10));
    }

    [Fact]
    public void Oversized_new_image_and_invalidated_frame_images_live_until_the_frame_finishes()
    {
        using var cache = new ObjectOverviewCache(1, 1024);
        var layer = LayerId.New();
        cache.BeginFrame();
        var first = cache.GetOrCreate(layer, 0, 0, 0, 1, Image);
        var second = cache.GetOrCreate(layer, 0, 0, 0, 2, Image);
        Assert.NotEqual(IntPtr.Zero, first.Image.Handle);
        Assert.NotEqual(IntPtr.Zero, second.Image.Handle);
        using (var surface = SKSurface.Create(new SKImageInfo(64, 64))!) surface.Canvas.DrawImage(second.Image, 0, 0);
        cache.EndFrame();
        Assert.Equal(IntPtr.Zero, first.Image.Handle);
        Assert.Equal(IntPtr.Zero, second.Image.Handle);
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void Append_invalidates_only_its_overview_region_and_moved_object_leaves_no_stale_point()
    {
        var a = Rectangle(100); var b = Rectangle(20000);
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [a, b]);
        var document = new MapDocument(DocumentId.New(), "Test", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer]);
        using var cache = new ObjectOverviewCache();
        using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
        var options = new RenderOptions { ViewScale = 1d / 128, VisibleWorld = new(0, 0, 32768, 32768),
            LodTier = RenderLodTier.Overview, ObjectCache = cache, Stats = new() };
        surface.Canvas.Scale(1f / 128);
        SceneRenderer.Render(surface.Canvas, document, options);
        var appended = new AddObjectCommand(layer.Id, Rectangle(120)).Apply(document);
        SceneRenderer.Render(surface.Canvas, appended, options);
        Assert.Equal(1, options.Stats.ObjectCacheHits);
        Assert.Equal(1, options.Stats.ObjectCacheMisses);
        Assert.Equal(3, options.Stats.ObjectsDrawn);
        var moved = DocumentEditor.ReplaceObject(appended, MultiSelectionGeometry.Translate([a], 10000, 0)[0]);
        SceneRenderer.Render(surface.Canvas, moved, options);
        Assert.Equal(3, options.Stats.ObjectsDrawn);
    }

    [Fact]
    public void Overview_uses_asset_color_and_invalidates_when_the_asset_becomes_missing()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(16, 16))!;
        sprite.Canvas.Clear(SKColors.ForestGreen);
        using var image = sprite.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var sha = new string('a', 64);
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Tree", 1024, 1024, 16, 16,
            0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new(sha) };
        var document = new MapDocument(DocumentId.New(), "Overview", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Objects", true, false, 1, [stamp])],
            assets: [new MapAssetReference(sha, "Tree", "image/png", "png", ProjectAssetStorage.Embedded,
                DateTimeOffset.UnixEpoch, "Own test", "Own", encoded.ToArray())]);
        using var cache = new ObjectOverviewCache();
        using var surface = SKSurface.Create(new SKImageInfo(64, 64))!;
        surface.Canvas.Scale(1f / 128);
        var source = new MutableImages { Image = image };
        var options = new RenderOptions { ViewScale = 1d / 128, VisibleWorld = new(0, 0, 8192, 8192),
            LodTier = RenderLodTier.Overview, ObjectCache = cache, Stamps = source, Stats = new() };
        SceneRenderer.Render(surface.Canvas, document, options);
        using var result = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(result);
        Assert.Contains(bitmap.Pixels, color => color.Green > color.Red + 20 && color.Alpha > 0);
        source.Image = null;
        SceneRenderer.Render(surface.Canvas, document, options);
        Assert.Equal(1, options.Stats.MissingAssets);
        Assert.Equal(1, options.Stats.ObjectCacheMisses);
    }

    private sealed class MutableImages : IStampImageSource
    {
        public SKImage? Image { get; set; }
        public SKImage? TryGetImage(string sha256) => Image;
    }

    private static SKImage Image()
    {
        using var surface = SKSurface.Create(new SKImageInfo(64, 64))!;
        return surface.Snapshot();
    }
    private static SceneObject Rectangle(double x) => new(ObjectId.New(), SceneObjectKind.Rectangle, "Item",
        x, 100, 10, 10, 0, "#FF008800", "#00000000", 0, true, false);
}
