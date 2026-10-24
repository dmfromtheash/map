using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class ColdPanTileTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unfinished_view_survives_pressure_until_completed_or_abandoned(bool complete)
    {
        const long mib = 1024 * 1024;
        var memory = new Map.Core.Resources.WorkingMemoryBudget(() => new(16 * 1024 * mib, 512 * mib, 128 * mib));
        Assert.False(memory.TryReserve(16 * mib, out _, out _));
        Assert.True(memory.IsUnderPressure);
        using var cache = new ObjectOverviewCache(memoryBudget: memory);
        var layer = LayerId.New();
        var view = new WorldBounds(0, 0, 2499, 499);
        SKImage Tile()
        {
            using var surface = SKSurface.Create(new SKImageInfo(1024, 1024))!;
            return surface.Snapshot();
        }
        cache.BeginFrame();
        Assert.True(cache.BeginDetailView(layer, 1, view, 500, 4 * mib));
        for (var x = 0; x < 3; x++) cache.GetOrCreate(layer, x, 0, 1, 1, Tile);
        cache.EndFrame();
        cache.BeginFrame();
        Assert.True(cache.BeginDetailView(layer, 1, view, 500, 4 * mib));
        for (var x = 3; x < 5; x++) cache.GetOrCreate(layer, x, 0, 1, 1, Tile);
        cache.EndFrame();
        // 20 MiB of one unfinished view survive the 16 MiB pressure target, within the ordinary
        // 64 MiB limit; otherwise the first tiles disappear before the last ones are available.
        Assert.Equal(5, cache.Count);
        Assert.Equal(20 * mib, cache.Bytes);
        for (var x = 0; x < 5; x++) Assert.True(cache.TryGet(layer, x, 0, 1, 1, out _));
        cache.BeginFrame();
        if (complete)
        {
            cache.BeginDetailView(layer, 1, view, 500, 4 * mib);
            cache.CompleteDetailView(layer);
        }
        cache.EndFrame();
        Assert.InRange(cache.Bytes, 0, 16 * mib);
    }

    [Fact]
    public void Detail_churn_keeps_the_fallback_but_does_not_raise_the_cache_limits()
    {
        using var cache = new ObjectOverviewCache(itemBudget: 2, byteBudget: 2048);
        var layer = LayerId.New();
        SKImage Tile()
        {
            using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
            return surface.Snapshot();
        }
        var overview = cache.GetOrCreate(layer, 0, 0, 0, 1, Tile, fallback: true);
        cache.BeginFrame();
        var oldDetail = cache.GetOrCreate(layer, 0, 0, 1, 1, Tile);
        cache.GetOrCreate(layer, 1, 0, 1, 1, Tile);
        Assert.NotEqual(IntPtr.Zero, overview.Image.Handle);
        cache.EndFrame();
        Assert.True(cache.TryGet(layer, 0, 0, 0, 1, out _));
        Assert.False(cache.TryGet(layer, 0, 0, 1, 1, out _));
        Assert.Equal(IntPtr.Zero, oldDetail.Image.Handle);
        Assert.Equal(2, cache.Count);
        Assert.Equal(2048, cache.Bytes);
        // Even fallback images must be invalidated by content, and released on context change.
        cache.GetOrCreate(layer, 0, 0, 0, 2, Tile, fallback: true);
        Assert.Equal(IntPtr.Zero, overview.Image.Handle);
        cache.BindGpuContext(new IntPtr(1));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cold_pan_keeps_every_unready_tile_covered_and_eventually_matches_exact_tiles(bool moving)
    {
        using var sprite = SKSurface.Create(new SKImageInfo(16, 16))!;
        sprite.Canvas.Clear(new SKColor(20, 100, 180, 191));
        using var image = sprite.Snapshot();
        var source = new DelayedImages(image);
        var objects = Enumerable.Range(0, 5000).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Stamp", i % 100 * 16, i / 100 * 16, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(new string('a', 64)) }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Cold pan", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(2048, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Background", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Stamps", true, false, .7, objects)]);
        // Smaller than this view plus its overview: refinement must still finish, rather than
        // endlessly evicting the first tiles before the last ones are ready.
        using var tiles = new ObjectOverviewCache(byteBudget: 5L * 514 * 514 * 4);
        using var referenceTiles = new ObjectOverviewCache();
        using var layers = new LayerImageCache();
        var requests = 0;

        SKBitmap Draw(double left, bool fast, RenderStats stats, bool refine = true)
        {
            using var surface = SKSurface.Create(new SKImageInfo(768, 512))!;
            surface.Canvas.Translate((float)-left, 0);
            var tileCache = refine ? tiles : referenceTiles;
            tileCache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = tileCache, LayerImages = refine ? layers : null,
                    Stats = stats, ViewScale = 1, FastTerrain = fast,
                    VisibleWorld = new(left, 0, left + 768, 512),
                    RequestRefinedFrame = refine ? () => requests++ : null,
                });
                using var snapshot = surface.Snapshot();
                return SKBitmap.FromImage(snapshot);
            }
            finally { tileCache.EndFrame(); }
        }

        void Settle(double left)
        {
            for (var frame = 0; frame < 100; frame++)
            {
                var stats = new RenderStats();
                using var bitmap = Draw(left, false, stats);
                if (stats.DetailTilesPending == 0) return;
            }
            Assert.Fail("The exact tiles did not finish within 100 frames.");
        }

        Settle(0);
        source.Ready = false;
        var pan = new RenderStats();
        using var fallback = Draw(768, moving, pan);
        Assert.True(pan.DetailTilesPending > 0 || pan.TilesReused > 0);
        // Every cell is a translucent blue stamp. A skipped tile would show the white background;
        // drawing the overview under an exact tile would also change these composite colours.
        using var expected = Draw(768, false, new(), refine: false);
        for (var y = 8; y < 512; y += 16)
        for (var x = 8; x < 768; x += 16)
            Same(expected.GetPixel(x, y), fallback.GetPixel(x, y), tolerance: 3);

        // Repeated still frames must keep requesting work, rather than retaining the stand-in
        // as a finished layer image. The source gate makes this independent of machine speed.
        for (var frame = 0; frame < 2; frame++)
        {
            var waiting = new RenderStats();
            using var bitmap = Draw(768, false, waiting);
            Assert.True(waiting.DetailTilesPending > 0);
            Assert.Equal(0, waiting.LayerImagesKept);
        }
        Assert.True(requests > 0);
        source.Ready = true;
        source.DelayReady = true;
        var completed = new RenderStats();
        using var finished = Draw(768, false, completed);
        Assert.Equal(0, completed.DetailTilesPending);
        Assert.All(expected.Pixels.Zip(finished.Pixels), pair => Same(pair.First, pair.Second));
    }

    private static void Same(SKColor expected, SKColor actual, int tolerance = 1)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, tolerance);
        Assert.Equal(expected.Alpha, actual.Alpha);
    }

    private sealed class DelayedImages(SKImage image) : IStampImageSource
    {
        public bool Ready { get; set; } = true;
        public bool DelayReady { get; set; }
        public SKImage? TryGetImage(string sha256) => image;
        public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
        {
            if (Ready && DelayReady) Thread.Sleep(10);
            return Ready;
        }
    }
}
