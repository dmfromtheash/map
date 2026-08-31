using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class Stage13CLodRenderingTests
{
    [Fact]
    public void Overview_aggregates_subpixel_objects_but_keeps_selection_exact()
    {
        var first = Rectangle("first", 100, 100);
        var selected = Rectangle("selected", 20_000, 20_000);
        var document = Document(65_536, [first, selected]);
        using var cache = new ObjectOverviewCache();
        var stats = new RenderStats();
        using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
        surface.Canvas.Scale(1f / 256);

        SceneRenderer.Render(surface.Canvas, document, new RenderOptions
        {
            VisibleWorld = new WorldBounds(0, 0, 65_536, 65_536),
            ViewScale = 1.0 / 256,
            LodTier = RenderLodTier.Overview,
            ObjectCache = cache,
            SelectedObjectId = selected.Id,
            Stats = stats,
        });

        Assert.Equal(2, stats.OverviewObjects);
        Assert.Equal(1, stats.ExactObjects);
        Assert.InRange(stats.OverviewChunks, 1, 2);
        Assert.Equal(stats.OverviewChunks, cache.Misses);
    }

    [Fact]
    public void Exact_and_export_ignore_overview_cache_and_match_the_reference_pixels()
    {
        var rectangle = new SceneObject(
            ObjectId.New(), SceneObjectKind.Rectangle, "exact",
            40, 40, 32, 32, 17,
            "#CC557799", "#FF112233", 3, true, false);
        var document = Document(200, [rectangle], includeFilter: true);
        using var cache = new ObjectOverviewCache();
        using var reference = Render(document, new RenderOptions());
        using var close = Render(document, new RenderOptions
        {
            LodTier = RenderLodTier.Exact,
            ObjectCache = cache,
        });
        using var export = Render(document, new RenderOptions
        {
            IsExport = true,
            LodTier = RenderLodTier.Overview,
            ObjectCache = cache,
        });

        Assert.Equal(Pixels(reference), Pixels(close));
        Assert.Equal(Pixels(reference), Pixels(export));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    [Fact]
    public void Cache_invalidates_one_logical_chunk_and_disposes_deterministically()
    {
        var cache = new ObjectOverviewCache(itemBudget: 2, byteBudget: 1_000_000);
        var layer = LayerId.New();

        cache.GetOrCreate(layer, 0, 0, -8, 1, Image);
        cache.GetOrCreate(layer, 0, 0, -8, 2, Image);
        Assert.Equal(1, cache.Invalidations);
        Assert.Equal(1, cache.Disposals);
        Assert.Equal(1, cache.Count);

        cache.GetOrCreate(layer, 1, 0, -8, 1, Image);
        cache.GetOrCreate(layer, 2, 0, -8, 1, Image);
        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(2, cache.Disposals);

        cache.Dispose();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
        Assert.Equal(4, cache.Disposals);
    }

    [Fact]
    public void Fully_visible_mask_tile_is_a_no_op_without_an_offscreen_group()
    {
        var sceneObject = Rectangle("masked", 40, 40);
        var pixels = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var fullyVisibleMask = new LayerMask(
            TileMap.Empty(1).With(new TileKey(0, 0), pixels), enabled: true);
        var masked = Document(200, [sceneObject], fullyVisibleMask);
        var unmasked = Document(200, [sceneObject]);
        var stats = new RenderStats();
        var options = new RenderOptions
        {
            VisibleWorld = new WorldBounds(0, 0, 200, 200),
            Stats = stats,
        };

        using var actual = Render(masked, options);
        using var expected = Render(unmasked, new RenderOptions
        {
            VisibleWorld = new WorldBounds(0, 0, 200, 200),
        });

        Assert.Equal(Pixels(expected), Pixels(actual));
        Assert.Equal(0, stats.MaskTilesDrawn);
    }

    private static SceneObject Rectangle(string name, double x, double y) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, y, 32, 32, 0, "#FF557799", "#FF112233", 1, true, false);

    private static MapDocument Document(
        int size,
        IReadOnlyList<SceneObject> objects,
        LayerMask? mask = null,
        bool includeFilter = false)
    {
        var layers = new List<MapLayer>
        {
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1, objects, mask),
        };
        if (includeFilter)
        {
            layers.Add(new FilterLayer(
                LayerId.New(), "filter", true, false, 1,
                new FilterSettings { Saturation = 0.2, Temperature = 0.1 }));
        }

        return new MapDocument(
            DocumentId.New(), "Stage 13C", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(size, size, "px", "#FFFFFFFF"),
            layers);
    }

    private static SKImage Image()
    {
        using var surface = SKSurface.Create(new SKImageInfo(10, 10))!;
        surface.Canvas.Clear(SKColors.Red);
        return surface.Snapshot();
    }

    private static SKBitmap Render(MapDocument document, RenderOptions options)
    {
        var info = new SKImageInfo(200, 200);
        using var surface = SKSurface.Create(info)!;
        surface.Canvas.Clear(SKColors.Transparent);
        SceneRenderer.Render(surface.Canvas, document, options);
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
}
