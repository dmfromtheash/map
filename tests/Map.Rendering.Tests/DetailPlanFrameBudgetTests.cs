using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

[Collection("OverviewExactLayers")]
public sealed class DetailPlanFrameBudgetTests
{
    [Fact]
    public void Each_frame_starts_one_plan_across_the_view_and_its_seed()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var bitmap = SKBitmap.Decode(Path.Combine(root.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        using var image = SKImage.FromBitmap(bitmap);
        var source = new Plans(image, SceneRenderer.StampOverviewColor(image));
        var sha = new string('a', 64);
        var objects = Enumerable.Range(0, 6000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "owner mountain", i % 100 * 8, i / 100 * 8, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(sha) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "plan budget", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
            [new MapAssetReference(sha, "owner", "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "fixture", "owner", catalogId: "fixture")]);
        using var cache = new ObjectOverviewCache();
        using var preparation = new SceneRenderer.OverviewPreparation(static () => true);
        using var surface = SKSurface.Create(new SKImageInfo(1536, 864))!;
        var previous = 0;
        for (var frame = 0; frame < 3; frame++)
        {
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, OverviewPreparation = preparation,
                    ViewScale = 1, CameraMoving = true, FastTerrain = true,
                    VisibleWorld = new(0, 0, 1536, 864), RequestRefinedFrame = static () => { },
                });
            }
            finally { cache.EndFrame(); }
            var created = source.Created - previous;
            Console.WriteLine($"DETAIL-PLAN-GUARD frame={frame} created={created}");
            Assert.Equal(1, created);
            previous = source.Created;
        }
    }

    private sealed class Plans(SKImage image, SKColor color) : IStampImageSource
    {
        public int Created;
        public SKImage? TryGetImage(string sha256) => image;
        public SKColor? TryGetOverviewColor(string sha256) => color;
        public (int Width, int Height)? TryGetImageSize(string sha256) => (image.Width, image.Height);
        public IStampPreparationPlan CreatePreparationPlan() { Created++; return new Pending(); }
        private sealed class Pending : IStampPreparationPlan
        {
            public long SourceRevision => 0;
            public long Bytes => 1;
            public void Add(IEnumerable<(string Sha256, double Width, double Height)> stamps) { foreach (var _ in stamps) { } }
            public bool Prepare(Action ready) => false;
        }
    }
}
