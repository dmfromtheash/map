using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class DenseEditImageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_edit_of_a_dense_layer_keeps_the_same_tile_pixels(bool still)
    {
        using var images = new EditFrameBudgetTests.OwnerImage();
        var objects = Enumerable.Range(0, 10000).Select(i => EditFrameBudgetTests.Stamp(
            i < 3000 ? 15632 + i % 60 * 24 : i % 100 * 64,
            i < 3000 ? 15960 + i / 60 * 16 : i / 100 * 48)).ToArray();
        var layer = new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects);
        var document = new MapDocument(DocumentId.New(), "dense edit pixels", DateTimeOffset.UnixEpoch,
            new(32768, 32768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"), layer]);
        DocumentDerivedIndexes.Prepare(document); ObjectSpatialIndex.For(layer);
        using var layers = new LayerImageCache();
        using var tiles = new ObjectOverviewCache();
        using var surface = SKSurface.Create(new SKImageInfo(1536, 864))!;
        Frame(layers);
        if (still) layers.ClearPanned();
        Frame(layers);
        foreach (var route in new[] { "place", "drag" })
        for (var step = 0; step < 10; step++)
        {
            if (route == "place") document = new AddObjectCommand(layer.Id, EditFrameBudgetTests.Stamp(16384 + step * 3, 16504)).Apply(document);
            else
            {
                Assert.True(DocumentRevisionIndex.For(document).TryFind(objects[1500].Id, out var location));
                var before = location.Object;
                document = new ReplaceObjectsCommand([before], [SceneObjectClone.At(before, before.X + 3, before.Y + 2)], "Drag").Apply(document);
            }
            var start = Stopwatch.GetTimestamp();
            var stats = Frame(layers);
            Console.WriteLine(FormattableString.Invariant($"EDIT-DENSE still={still} route={route} step={step} ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F4} tiles={stats.ObjectTilesDrawn} candidates={stats.ObjectCandidates}"));
            var actual = Pixels();
            using var fresh = new LayerImageCache();
            Frame(fresh);
            if (still) { fresh.ClearPanned(); Frame(fresh); }
            Assert.Equal(Pixels(), actual);
            // One changed 64x48 stamp intersects at most two detail tiles. A drag's
            // disjoint top/middle/bottom bands copy at most 1 + 2 + 1 tiles.
            Assert.InRange(stats.ObjectTilesDrawn, 1, 4);
        }

        RenderStats Frame(LayerImageCache cache)
        {
            surface.Canvas.ResetMatrix(); surface.Canvas.Clear(SKColors.White); surface.Canvas.Translate(-15616, -15952);
            var stats = new RenderStats(); tiles.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = images, LayerImages = cache, ObjectCache = tiles, Stats = stats, ViewScale = 1,
                    LodTier = RenderLodTier.Exact, VisibleWorld = new(15616, 15952, 17152, 16816),
                });
            }
            finally { tiles.EndFrame(); }
            return stats;
        }
        byte[] Pixels() { using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image); return bitmap.Bytes; }
    }
}
