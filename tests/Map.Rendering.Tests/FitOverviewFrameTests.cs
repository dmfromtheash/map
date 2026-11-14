using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// A giant page (131 072 px, perf host «giant» layout) seen whole: once the background overview
/// is prepared, every fit frame is drawn from the cached chunk rasters - no object one by one, no
/// variant built on the frame, no cache miss and no allocation in proportion to the objects.
/// </summary>
public sealed class FitOverviewFrameTests
{
    private const int Side = 131072;
    private const int ViewPixels = 700;
    private const int Count = 200_000;

    [Fact]
    public void A_settled_fit_view_of_a_giant_page_is_drawn_from_overview_chunks()
    {
        var shas = Enumerable.Range(0, 32).Select(i => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([(byte)i]))).ToArray();
        var random = new Random(7);
        var objects = new SceneObject[Count];
        for (var i = 0; i < Count; i++)
        {
            var width = 64 * (0.5 + random.NextDouble() * 1.5);
            objects[i] = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Ассет " + i,
                random.NextDouble() * (Side - width), random.NextDouble() * (Side - width), width, width,
                random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(shas[i % shas.Length]) };
        }
        var document = new MapDocument(DocumentId.New(), "giant fit", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(Side, Side, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"), new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, objects)])
            .WithAssets([.. shas.Select(sha => new MapAssetReference(sha, sha[..8], "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner fixture", "owner", catalogId: sha))]);
        // The editor prepares these right after open of a large map; edits then follow deltas.
        DocumentDerivedIndexes.Prepare(document);
        using var source = new Mountains();
        using var prep = new SceneRenderer.OverviewPreparation();
        using var cache = new ObjectOverviewCache();
        prep.UpdateDocument(document, source, source.Ready);
        var scale = (double)ViewPixels / Side;
        var timeout = Stopwatch.StartNew();
        RenderStats stats;
        do
        {
            stats = Draw(document, source, cache, prep, scale);
            Assert.Null(prep.Statistics.Error);
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(60), "the fit overview did not settle");
            Thread.Sleep(5);
        } while (stats.DetailTilesPending > 0 || !prep.Statistics.Idle);

        for (var frame = 0; frame < 5; frame++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            stats = Draw(document, source, cache, prep, scale);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(0, stats.DetailTilesPending);
            Assert.Equal(256, stats.OverviewChunks); // 16 × 16 chunks cover the page
            Assert.Equal(Count, stats.OverviewObjects);
            Assert.Equal(0, stats.ExactObjects);
            Assert.Equal(0, stats.OverviewVariantBuilds);
            Assert.Equal(0, stats.ObjectCacheMisses);
            // About 90 KiB on a dev PC; an object-proportional pass would allocate megabytes.
            Assert.True(bytes < 1024 * 1024, $"{bytes} bytes allocated by a settled fit frame");
        }
    }

    private static RenderStats Draw(MapDocument document, Mountains source, ObjectOverviewCache cache,
        SceneRenderer.OverviewPreparation prep, double scale)
    {
        using var surface = SKSurface.Create(new SKImageInfo(ViewPixels, ViewPixels, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Scale((float)scale);
        var stats = new RenderStats();
        cache.BeginFrame();
        try
        {
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                ObjectCache = cache, OverviewPreparation = prep, Stamps = source, Stats = stats,
                ViewScale = scale, LodTier = RenderLodTier.Overview, VisibleWorld = new(0, 0, Side - 1, Side - 1),
                RequestRefinedFrame = source.Ready,
            });
            return stats;
        }
        finally { cache.EndFrame(); }
    }

    private sealed class Mountains : IStampImageSource, IDisposable
    {
        private readonly SKImage _image;
        public void Ready() { }
        public long? OverviewColorRevision => 0;
        public SKImage? TryGetImage(string sha256) => _image;
        public SKColor? TryGetOverviewColor(string sha256) => SceneRenderer.StampOverviewColor(_image);
        public Mountains()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
            _image = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"))!;
        }
        public void Dispose() => _image.Dispose();
    }
}
