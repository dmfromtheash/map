using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class StampBatchTests
{
    [Fact]
    public void Compatible_batches_preserve_overlap_rotation_opacity_and_fallback_order()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(32, 32))!;
        sprite.Canvas.Clear(SKColors.Transparent);
        using var ink = new SKPaint { Color = SKColors.ForestGreen, IsAntialias = false };
        sprite.Canvas.DrawRect(4, 2, 22, 28, ink);
        using var image = sprite.Snapshot();
        var sha = new string('a', 64);
        var objects = Enumerable.Range(0, 25).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Tree " + i, 8 + i * 3, 16 + i * 2, 32, 32, i % 2 * 90, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha), ObjectOpacity = .6 }).ToArray();
        objects[12] = objects[12] with { Stamp = objects[12].Stamp! with { FlipHorizontal = true, Hue = 25 } };
        var document = new MapDocument(DocumentId.New(), "Test", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(128, 128, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Objects", true, false, 1, objects)]);
        using var actual = SKSurface.Create(new SKImageInfo(128, 128))!;
        using var expected = SKSurface.Create(new SKImageInfo(128, 128))!;
        var stats = new RenderStats();
        SceneRenderer.Render(actual.Canvas, document, new RenderOptions { Stamps = new Images(image), Stats = stats });
        SceneRenderer.Render(expected.Canvas, document, new RenderOptions { Stamps = new Images(image), IsExport = true });
        Assert.Equal(2, stats.StampAtlasBatches);
        Assert.Equal(25, stats.ObjectsDrawn);
        using var a = SKBitmap.FromImage(actual.Snapshot());
        using var b = SKBitmap.FromImage(expected.Snapshot());
        var error = a.Pixels.Zip(b.Pixels).Sum(p => Math.Abs(p.First.Red - p.Second.Red)
            + Math.Abs(p.First.Green - p.Second.Green) + Math.Abs(p.First.Blue - p.Second.Blue) + Math.Abs(p.First.Alpha - p.Second.Alpha));
        Assert.InRange(error / (128d * 128 * 4), 0, 1);
    }
    [Fact]
    public void Dense_tile_reuse_matches_exact_scene_and_invalidates_move_across_tile_boundary()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(16, 16))!;
        sprite.Canvas.Clear(SKColors.ForestGreen);
        using var image = sprite.Snapshot();
        var objects = Enumerable.Range(0, 2100).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Tree " + i, i % 70 * 16, i / 70 * 16, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('b', 64)), ObjectOpacity = .6 }).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "Objects", true, false, 1, objects);
        var document = new MapDocument(DocumentId.New(), "Tiles", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2048, 2048, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"), layer]);
        using var cache = new ObjectOverviewCache();
        using var actual = SKSurface.Create(new SKImageInfo(1024, 512))!;
        using var expected = SKSurface.Create(new SKImageInfo(1024, 512))!;
        var options = new RenderOptions { Stamps = new Images(image), ObjectCache = cache,
            VisibleWorld = new(0, 0, 1024, 512), Stats = new() };
        void Match(MapDocument snapshot)
        {
            SceneRenderer.Render(actual.Canvas, snapshot, options);
            SceneRenderer.Render(expected.Canvas, snapshot, new RenderOptions { Stamps = new Images(image), IsExport = true });
            using var actualImage = actual.Snapshot(); using var expectedImage = expected.Snapshot();
            using var a = SKBitmap.FromImage(actualImage); using var b = SKBitmap.FromImage(expectedImage);
            // Compositing a translucent tile rounds premultiplied channels once more than direct
            // export. At most one channel unit is allowed; stale tiles or changed order fail.
            Assert.All(a.Pixels.Zip(b.Pixels), p =>
            {
                Assert.InRange(Math.Abs(p.First.Red - p.Second.Red), 0, 1);
                Assert.InRange(Math.Abs(p.First.Green - p.Second.Green), 0, 1);
                Assert.InRange(Math.Abs(p.First.Blue - p.Second.Blue), 0, 1);
                Assert.Equal(p.First.Alpha, p.Second.Alpha);
            });
        }
        Match(document);
        Match(document);
        Assert.True(options.Stats.ObjectCacheHits > 0);
        Assert.Equal(0, options.Stats.ObjectCacheMisses);
        var appended = new AddObjectCommand(layer.Id,
            MultiSelectionGeometry.Translate([objects[0] with { Id = ObjectId.New() }], 10, 12)[0]).Apply(document);
        Match(appended);
        Assert.True(options.Stats.ObjectCacheMisses > 0);
        var moved = DocumentEditor.ReplaceObject(document, MultiSelectionGeometry.Translate([objects[0]], 700, 100)[0]);
        Match(moved);
        Assert.True(options.Stats.ObjectCacheMisses > 0);
    }

    [Fact]
    public void Opaque_coverage_removes_hidden_raster_work_without_changing_transparent_edges_or_order()
    {
        using var sprite = SKSurface.Create(new SKImageInfo(64, 64))!;
        sprite.Canvas.Clear(SKColors.Transparent);
        using var ink = new SKPaint { Color = SKColors.ForestGreen };
        sprite.Canvas.DrawRect(4, 4, 56, 56, ink);
        ink.Color = SKColors.DarkRed;
        sprite.Canvas.DrawRect(32, 16, 14, 30, ink);
        using var image = sprite.Snapshot();
        var objects = Enumerable.Range(0, 2100).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Tree " + i, i % 3 - 256, i % 5 - 256, 1024, 1024, i % 4 * 90, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('c', 64)) { FlipHorizontal = i % 2 == 0 }, ObjectOpacity = i % 2 == 0 ? .6 : 1 }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Occlusion", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(128, 128, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Objects", true, false, 1, objects)]);
        using var cache = new ObjectOverviewCache();
        using var actual = SKSurface.Create(new SKImageInfo(64, 64))!;
        using var expected = SKSurface.Create(new SKImageInfo(64, 64))!;
        var stats = new RenderStats();
        SceneRenderer.Render(actual.Canvas, document, new RenderOptions { Stamps = new Images(image),
            ObjectCache = cache, VisibleWorld = new(0, 0, 64, 64), Stats = stats });
        SceneRenderer.Render(expected.Canvas, document, new RenderOptions { Stamps = new Images(image), IsExport = true });
        using var actualImage = actual.Snapshot(); using var expectedImage = expected.Snapshot();
        using var a = SKBitmap.FromImage(actualImage); using var b = SKBitmap.FromImage(expectedImage);
        Assert.Equal(b.Pixels, a.Pixels);
        Assert.True(stats.StampOccludedObjects > 1000);
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => image;
    }
}
