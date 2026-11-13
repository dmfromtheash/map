using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class LocalDetailPreparationTests
{
    private static readonly string Near = new('a', 64);
    private static readonly string Far = new('b', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_preparing_stamp_is_not_a_missing_file_and_its_tile_is_not_retained(bool shadow)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        using var image = SKImage.FromEncodedData(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        var source = new PreparingWithoutFallback(image);
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Owner mountain", 16, 16, 96, 96,
            0, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(Near) { ShadowOpacity = shadow ? .5 : 0 } };
        var layer = new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, [stamp]);
        var document = new MapDocument(DocumentId.New(), "Preparation", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(128, 128, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"), layer]);
        using var cache = new ObjectOverviewCache();
        cache.BeginFrame();
        var stats = new RenderStats();
        SKImage Build()
        {
            using var surface = SKSurface.Create(new SKImageInfo(128, 128));
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions { Stamps = source, Stats = stats });
            return surface.Snapshot();
        }
        var temporary = cache.GetOrCreate(layer.Id, 0, 0, 0, 1, Build, keep: () => stats.UnresolvedImages == 0);
        Assert.Equal(0, stats.MissingAssets);
        Assert.True(stats.DetailTilesPending > 0);
        Assert.False(cache.TryGet(layer.Id, 0, 0, 0, 1, out _));
        cache.EndFrame();
        Assert.Equal(IntPtr.Zero, temporary.Image.Handle);
        source.Ready = true;
        cache.BeginFrame();
        cache.GetOrCreate(layer.Id, 0, 0, 0, 1, Build, keep: () => stats.UnresolvedImages == 0);
        Assert.Equal(0, stats.DetailTilesPending);
        Assert.True(cache.TryGet(layer.Id, 0, 0, 0, 1, out _));
        cache.EndFrame();
    }

    private sealed class PreparingWithoutFallback(SKImage image) : IStampImageSource
    {
        public bool Ready { get; set; }
        public bool LastImageRequestPending => !Ready;
        public SKImage? TryGetImage(string sha256) => Ready ? image : null;
        public SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height) => null;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Overview_is_pending_until_its_exact_stamps_arrive_and_does_not_keep_a_partial_layer(bool lateRequest, bool masked)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        using var bitmap = SKBitmap.Decode(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        using var image = SKImage.FromBitmap(bitmap);
        using var small = bitmap.Resize(new SKImageInfo(8, 8), new SKSamplingOptions(SKFilterMode.Linear));
        using var fallback = SKImage.FromBitmap(small);
        var source = new DelayedExact(image, fallback) { LateRequest = lateRequest };
        var stamp = new SceneObject(ObjectId.New(), SceneObjectKind.Stamp, "Owner mountain", 2048, 2048, 1024, 1024,
            17, "#FFFFFFFF", "#00000000", 0, true, false) { Stamp = new StampStyle(Near) };
        var document = new MapDocument(DocumentId.New(), "Overview exact arrival", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(8192, 8192, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Mountains", true, false, 1, [stamp])])
            .WithAssets([new MapAssetReference(Near, Near, "image/png", "png", ProjectAssetStorage.Linked,
                DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: Near)]);
        using var cache = new ObjectOverviewCache();
        using var layers = new LayerImageCache();
        using var referenceLayers = new LayerImageCache();
        var requested = 0;
        byte[] Draw(RenderStats stats, bool progressive)
        {
            using var surface = SKSurface.Create(new SKImageInfo(256, 256))!;
            surface.Canvas.Scale(1f / 32);
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, LayerImages = progressive ? layers : referenceLayers,
                    Stats = stats, ViewScale = 1d / 32, LodTier = RenderLodTier.Overview,
                    MaskEditLayerId = masked ? document.Layers.OfType<ObjectLayer>().Single().Id : null,
                    VisibleWorld = new(0, 0, 8192, 8192), RequestRefinedFrame = progressive ? () => requested++ : null,
                });
                using var snapshot = surface.Snapshot();
                using var result = SKBitmap.FromImage(snapshot);
                return result.Bytes;
            }
            finally { cache.EndFrame(); }
        }
        var pending = new RenderStats();
        var partial = Draw(pending, true);
        Assert.True(pending.DetailTilesPending > 0);
        // Planned work requests refinement immediately; a late lookup relies on its worker
        // completion notification instead of continuously requesting frames while it waits.
        if (!lateRequest) Assert.True(requested > 0);
        Assert.Equal(0, layers.Count);
        Assert.Contains(source.Requested, request => request.Sha256 == Near && request.Width == 32 && request.Height == 32);
        source.Ready = true;
        var complete = new RenderStats();
        var actual = Draw(complete, true);
        Assert.Equal(0, complete.DetailTilesPending);
        Assert.Equal(Draw(new(), false), actual);
        Assert.NotEqual(partial, actual);
    }

    private sealed class DelayedExact(SKImage image, SKImage fallback) : IStampImageSource
    {
        public bool Ready { get; set; }
        public bool LateRequest { get; init; }
        private bool _asked;
        public bool ImageRequestsPending => LateRequest && _asked && !Ready;
        public List<(string Sha256, double Width, double Height)> Requested { get; } = [];
        public SKImage? TryGetImage(string sha256) { _asked = true; return Ready ? image : null; }
        public SKImage? TryGetReadyImageForPixelSize(string sha256, double width, double height) => fallback;
        public SKColor? TryGetOverviewColor(string sha256) => SceneRenderer.StampOverviewColor(image);
        public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
        { Requested.AddRange(stamps); return Ready || LateRequest; }
    }

    [Fact]
    public void Cold_fallback_prepares_only_used_references_and_tracks_append_delete_and_visibility()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        using var bitmap = SKBitmap.Decode(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        using var image = SKImage.FromBitmap(bitmap);
        var source = new WaitingImages(image);
        static SceneObject Mountain(ObjectId id, string sha, double x, bool visible) =>
            new(id, SceneObjectKind.Stamp, "Гора", x, 64, 16, 16, 0,
                "#FFFFFFFF", "#00000000", 0, visible, false) { Stamp = new StampStyle(sha) };
        var stamp = Mountain(ObjectId.New(), Near, 64, true);
        var hidden = Mountain(ObjectId.New(), Far, 64, false);
        var layer = new ObjectLayer(LayerId.New(), "Горы", true, false, 1, [stamp, hidden]);
        var document = new MapDocument(DocumentId.New(), "Unused references", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(512, 512, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"), layer])
            .WithAssets(new[] { Near, Far }.Select(sha => new MapAssetReference(sha, sha, "image/png", "png",
                ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)).ToArray());
        using var cache = new ObjectOverviewCache();
        byte[] Draw(MapDocument current)
        {
            source.Prepared.Clear(); source.Colors.Clear();
            using var surface = SKSurface.Create(new SKImageInfo(512, 512))!;
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, current, new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, Stats = new(), ViewScale = 1,
                    VisibleWorld = new(0, 0, 512, 512), RequestRefinedFrame = () => { },
                });
                using var snapshot = surface.Snapshot();
                using var result = SKBitmap.FromImage(snapshot);
                return result.Bytes;
            }
            finally { cache.EndFrame(); }
        }
        var initial = Draw(document);
        Assert.Equal(new[] { Near }, source.Prepared);
        Assert.Equal(new[] { Near }, source.Colors);
        // An unreferenced catalog entry must neither be fetched nor affect the picture.
        Assert.Equal(initial, Draw(document.WithAssets(document.Assets.Where(a => a.Sha256 == Near).ToArray())));
        var visible = Mountain(ObjectId.New(), Far, 128, true);
        var appended = new AddObjectCommand(layer.Id, visible).Apply(document);
        Draw(appended);
        Assert.Equal(new[] { Near, Far }, source.Prepared);
        Assert.Equal(new[] { Near, Far }, source.Colors);
        var removed = new RemoveObjectsCommand(appended, layer.Id, [visible.Id]).Apply(appended);
        Assert.Equal(initial, Draw(removed));
        Assert.Equal(new[] { Near }, source.Prepared);
        var revealed = DocumentEditor.ReplaceObject(removed, Mountain(hidden.Id, Far, 64, true));
        Draw(revealed);
        Assert.Equal(new[] { Near, Far }, source.Prepared);
        Assert.Equal(initial, Draw(DocumentEditor.ReplaceObject(revealed, hidden)));
        Assert.Equal(new[] { Near }, source.Prepared);
    }

    private sealed class WaitingImages(SKImage image) : IStampImageSource
    {
        public List<string> Prepared { get; } = [];
        public List<string> Colors { get; } = [];
        public SKImage? TryGetImage(string sha256) => image;
        public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready) => false;
        public bool PrepareOverview(IEnumerable<string> assets, Action ready) { Prepared.AddRange(assets); return true; }
        public SKColor? TryGetOverviewColor(string sha256) { Colors.Add(sha256); return SceneRenderer.StampOverviewColor(image); }
    }
    [Theory]
    [InlineData(512, 8)]
    [InlineData(2048, 0)]
    public void A_local_giant_view_does_not_decode_distant_stamps_and_settles_to_the_same_pixels(int extent, double budget)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        using var original = SKBitmap.Decode(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"));
        using var small = original.Resize(new SKImageInfo(32, 32), new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(small);
        var source = new Images(image);
        const double origin = 65536;
        var objects = Enumerable.Range(0, 4800).Select(i => new SceneObject(ObjectId.New(), SceneObjectKind.Stamp,
            "Гора", (i < 2400 ? origin : 1024) + i % 50 * 8, (i < 2400 ? origin : 1024) + i % 2400 / 50 * 8,
            12, 12, i % 3 * 15, "#FFFFFFFF", "#00000000", 0, true, false)
            { Stamp = new StampStyle(i < 2400 ? Near : Far), ObjectOpacity = .7 }).ToArray();
        var document = new MapDocument(DocumentId.New(), "Local detail", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "Горы", true, false, .8, objects)])
            .WithAssets(new[] { Near, Far }.Select(sha => new MapAssetReference(sha, sha, "image/png", "png",
                ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)).ToArray());
        using var exact = new ObjectOverviewCache();
        using var progressive = new ObjectOverviewCache();
        SKBitmap Draw(ObjectOverviewCache cache, bool refine, RenderStats stats)
        {
            using var surface = SKSurface.Create(new SKImageInfo(extent, extent))!;
            surface.Canvas.Translate((float)-origin, (float)-origin);
            cache.BeginFrame();
            try
            {
                SceneRenderer.Render(surface.Canvas, document, new RenderOptions
                {
                    Stamps = source, ObjectCache = cache, Stats = stats, ViewScale = 1,
                    VisibleWorld = new(origin, origin, origin + extent - 1, origin + extent - 1),
                    DetailTileBudgetMilliseconds = budget,
                    RequestRefinedFrame = refine ? () => { } : null,
                });
                using var snapshot = surface.Snapshot();
                return SKBitmap.FromImage(snapshot);
            }
            finally { cache.EndFrame(); }
        }
        using var expected = Draw(exact, false, new());
        var completed = false;
        var deferred = 0;
        for (var frame = 0; frame < 30; frame++)
        {
            var stats = new RenderStats();
            using var actual = Draw(progressive, true, stats);
            Assert.Equal(0, source.DistantFetches);
            if (stats.DetailTilesPending != 0) { deferred++; continue; }
            Assert.Equal(expected.Bytes, actual.Bytes);
            completed = true;
            break;
        }
        Assert.True(completed, "Local exact tiles must finish.");
        if (budget == 0) Assert.True(deferred > 0, "The fallback seed must also yield between tiles.");
        Assert.True(source.SizeRequests > 0, "Dense eligibility must use dimensions without fetching distant pixels.");
    }

    private sealed class Images(SKImage image) : IStampImageSource
    {
        public int DistantFetches { get; private set; }
        public int SizeRequests { get; private set; }
        public (int Width, int Height)? TryGetImageSize(string sha256)
        {
            SizeRequests++;
            return (image.Width, image.Height);
        }
        public SKImage? TryGetImage(string sha256)
        {
            if (sha256 == Far) DistantFetches++;
            return image;
        }
    }
}
