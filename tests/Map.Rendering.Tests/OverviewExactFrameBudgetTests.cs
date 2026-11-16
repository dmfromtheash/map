using System.Diagnostics;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class OverviewExactFrameBudgetTests
{
    [Fact]
    public void Every_provisional_frame_bounds_expensive_exact_draws_and_the_stopped_view_is_unchanged()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var bitmap = SKBitmap.Decode(Path.Combine(root.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        using var image = SKImage.FromBitmap(bitmap);
        var source = new SlowResidentMountain(image);
        var sha = new string('a', 64);
        var objects = Enumerable.Range(0, 1024).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "owner mountain", 100 + i % 32 * 220, 100 + i / 32 * 220, 512, 512, 0,
            "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(sha) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "exact frame budget", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "mountains", true, false, 1, objects)],
            [new MapAssetReference(sha, "owner", "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "fixture", "owner", catalogId: "fixture")]);
        using var cache = new ObjectOverviewCache();
        using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
        void Frame(bool moving, bool refine)
        {
            surface.Canvas.ResetMatrix(); surface.Canvas.Clear(SKColors.White); surface.Canvas.Scale(.025f);
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, ViewScale = .025, CameraMoving = moving,
                    LodTier = RenderLodTier.Overview, VisibleWorld = new(0, 0, 10240, 10240),
                    RequestRefinedFrame = refine ? static () => { } : null,
                });
                surface.Canvas.Flush();
            }
            finally { cache.EndFrame(); }
        }
        byte[] Pixels()
        {
            using var snapshot = surface.Snapshot(); using var pixels = SKBitmap.FromImage(snapshot);
            return pixels.Bytes;
        }
        Frame(true, true); // native/JIT start-up and the immutable overview plan
        source.Slow = true;
        var frames = new List<(double Ms, int Calls)>();
        for (var i = 0; i < 3; i++)
        {
            source.Calls = 0;
            var start = Stopwatch.GetTimestamp(); Frame(true, true);
            frames.Add((Stopwatch.GetElapsedTime(start).TotalMilliseconds, source.Calls));
        }
        Console.WriteLine($"EXACT-FRAME-GUARD worst={frames.Max(f => f.Ms):F3}ms calls={string.Join(',', frames.Select(f => f.Calls))}");
        // Every source lookup costs 4 ms, modelling an expensive first draw. An object-count cap of 512
        // allows >2 seconds here. A time budget permits just the draw that crosses it, in every frame.
        Assert.All(frames, f => Assert.InRange(f.Calls, 1, 8));
        Assert.True(frames.Max(f => f.Ms) < 150, "one provisional frame consumed an unbounded exact draw batch");
        source.Slow = false;
        Frame(false, true);
        var stopped = Pixels();
        Frame(false, false);
        Assert.Equal(Pixels(), stopped);
    }

    private sealed class SlowResidentMountain(SKImage image) : IStampImageSource
    {
        public bool Slow;
        public int Calls;
        public SKImage? TryGetImage(string sha256) => image;
        public (int Width, int Height)? TryGetImageSize(string sha256) => (image.Width, image.Height);
        public SKColor? TryGetOverviewColor(string sha256) => SceneRenderer.StampOverviewColor(image);
        public SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
        {
            Calls++;
            if (Slow) Thread.Sleep(4);
            return image;
        }
    }
}
