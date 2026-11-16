using System.Diagnostics;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class EditOverviewBudgetTests
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void Overview_edits_and_history_repair_local_chunks_with_identical_pixels(int count)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        using var image = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"))!;
        var images = new OwnerImage(image);
        var random = new Random(901);
        var objects = Enumerable.Range(0, count).Select(_ => Stamp(random.Next(131000), random.Next(131000))).ToArray();
        objects[^1] = Stamp(65520, 65520);
        var layer = new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects);
        var document = new MapDocument(DocumentId.New(), "overview edit budget", DateTimeOffset.UnixEpoch,
            new(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"), layer],
            [new MapAssetReference(new string('a', 64), "owner", "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner", "owner", catalogId: "owner")]);
        DocumentDerivedIndexes.Prepare(document);
        var session = new EditingSession(document);
        using var layers = new LayerImageCache();
        using var overview = new ObjectOverviewCache();
        using var surface = SKSurface.Create(new SKImageInfo(1024, 1024))!;
        var violations = new List<string>();
        Frame(layers); Frame(layers);
        foreach (var route in new[] { "place-10", "drag", "undo", "redo" })
        for (var step = 0; step < 10; step++)
        {
            if (route == "place-10") session.Execute(new AddObjectCommand(layer.Id, Stamp(65400 + step * 32, 65656)));
            if (route == "drag")
            {
                Assert.True(DocumentRevisionIndex.For(session.Document).TryFind(objects[^1].Id, out var location));
                var before = location.Object;
                session.Execute(new ReplaceObjectsCommand([before], [SceneObjectClone.At(before, before.X + 64, before.Y + 32)], "Drag"));
            }
            if (route == "undo") session.Undo();
            if (route == "redo") session.Redo();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var stats = Frame(layers);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine(FormattableString.Invariant($"EDIT-OVERVIEW count={count} route={route} step={step} ms={ms:F4} bytes={bytes} chunks={stats.OverviewChunks} builds={stats.OverviewVariantBuilds} planMs={stats.OverviewPlanMilliseconds:F4} rasterMs={stats.OverviewRasterMilliseconds:F4}"));
            // One changed chunk and its margin composite 3x3 chunks. Crossing both axes
            // repairs two chunks as three disjoint bands: 6 + 8 + 6 chunk blits, including
            // repeated neighbours clipped to different bands. The gap is never redrawn.
            if (stats.OverviewChunks > 20) violations.Add($"{route}/{step}: {stats.OverviewChunks} chunks > 20");
            if (stats.OverviewVariantBuilds > 2) violations.Add($"{route}/{step}: {stats.OverviewVariantBuilds} rebuilt variants > 2");
            var actual = Pixels();
            using (var fresh = new LayerImageCache()) { Frame(fresh); Assert.Equal(Pixels(), actual); }
            Thread.Sleep(16);
        }
        Console.WriteLine($"EDIT-OVERVIEW-PIXELS count={count} sha256={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Pixels()))}");
        Assert.Empty(violations);

        RenderStats Frame(LayerImageCache cache)
        {
            surface.Canvas.ResetMatrix(); surface.Canvas.Clear(SKColors.White); surface.Canvas.Scale(1f / 128);
            var stats = new RenderStats();
            overview.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, session.Document, new RenderOptions
                {
                    Stamps = images, LayerImages = cache, ObjectCache = overview, Stats = stats, ViewScale = 1d / 128,
                    LodTier = RenderLodTier.Overview, VisibleWorld = new(0, 0, 131072, 131072),
                });
            }
            finally { overview.EndFrame(); }
            surface.Canvas.Flush(); return stats;
        }
        byte[] Pixels()
        {
            using var snapshot = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(snapshot); return bitmap.Bytes;
        }
    }

    private static SceneObject Stamp(double x, double y) => new(ObjectId.New(), SceneObjectKind.Stamp, "owner mountain",
        x, y, 16, 12, 0, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new(new string('a', 64)) };
    private sealed class OwnerImage(SKImage image) : IStampImageSource
    {
        public long? OverviewColorRevision => 0;
        public SKImage? TryGetImage(string sha256) => image;
    }
}
