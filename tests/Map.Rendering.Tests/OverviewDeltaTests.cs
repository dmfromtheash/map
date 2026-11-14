using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class OverviewDeltaTests
{
    private const double Chunk = 8192;
    private const double Scale = 1d / 128;
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string Unused = new('c', 64);

    [Fact]
    public void Arrival_rebuilds_only_dependent_chunks_and_unchanged_frame_does_not_scan_assets()
    {
        using var source = new Colors();
        var document = Document([Stamp(A, 0, 0), Stamp(B, 1, 0), Stamp(A, 2, 0)]);
        var assets = new CountingAssets(document.Assets);
        document = document with { Assets = assets };
        using var cache = new ObjectOverviewCache();
        Draw(document, source, cache, false);
        Assert.Equal(2, source.Lookups); // Unused document references are never requested.
        source.Arrive(A, 1);
        var (_, arrival) = Draw(document, source, cache, false);
        Assert.Equal(2, arrival.OverviewVariantBuilds);
        Assert.Equal(2, arrival.ObjectCacheMisses);
        Assert.Equal(1, arrival.ObjectCacheHits);
        var lookups = source.Lookups;
        var preparations = source.Preparations;
        var assetReads = assets.Reads;
        var (_, unchanged) = Draw(document, source, cache, true);
        Assert.Equal(0, unchanged.OverviewVariantBuilds);
        Assert.Equal(0, unchanged.ObjectCacheMisses);
        Assert.Equal(lookups, source.Lookups);
        Assert.Equal(preparations, source.Preparations);
        Assert.Equal(assetReads, assets.Reads);
        source.Arrive(Unused, 2);
        var (_, unrelated) = Draw(document, source, cache, false);
        Assert.Equal(0, unrelated.OverviewVariantBuilds);
        Assert.Equal(0, unrelated.ObjectCacheMisses);
    }

    [Fact]
    public void Budget_refines_centre_first_keeps_every_other_old_image_and_settles_identically()
    {
        using var source = new Colors();
        source.Arrive(A, 1);
        var document = Document(Grid());
        using var cache = new ObjectOverviewCache(overviewBuildBudgetMilliseconds: 0);
        var (before, _) = Draw(document, source, cache, false);
        source.Arrive(A, 2);
        var (first, stats) = Draw(document, source, cache, true);
        Assert.Equal(1, stats.OverviewVariantBuilds);
        Assert.Equal(1, stats.ObjectCacheMisses);
        Assert.Equal(9, stats.OverviewChunks);
        Assert.True(stats.DetailTilesPending > 0);
        Assert.True(source.FramesRequested > 0);
        for (var y = 0; y < 3; y++)
        for (var x = 0; x < 3; x++)
        {
            var oldRegion = Region(before, x, y);
            var newRegion = Region(first, x, y);
            if (x == 1 && y == 1) Assert.NotEqual(oldRegion, newRegion);
            else Assert.Equal(oldRegion, newRegion);
        }
        byte[] settled = first;
        for (var frame = 0; frame < 9 && stats.DetailTilesPending > 0; frame++)
        {
            (settled, stats) = Draw(document, source, cache, true);
            Assert.InRange(stats.ObjectCacheMisses, 0, 1);
            Assert.InRange(stats.OverviewVariantBuilds, 0, 1);
            Assert.Equal(9, stats.OverviewChunks);
        }
        Assert.Equal(0, stats.DetailTilesPending);
        using var reference = new ObjectOverviewCache();
        Assert.Equal(Draw(document, source, reference, false).Pixels, settled);
    }

    [Fact]
    public void Budget_is_shared_by_all_overview_layers_in_a_frame()
    {
        using var source = new Colors();
        source.Arrive(A, 1);
        var original = Document(Grid());
        var document = new MapDocument(original.Id, "Two layers", DateTimeOffset.UnixEpoch, original.Canvas,
            original.Layers.Concat([new ObjectLayer(LayerId.New(), "Second", true, false, 1, Grid())]).ToArray(),
            assets: original.Assets);
        using var cache = new ObjectOverviewCache(overviewBuildBudgetMilliseconds: 0);
        Draw(document, source, cache, false);
        source.Arrive(A, 2);
        var (_, stats) = Draw(document, source, cache, true);
        Assert.Equal(1, stats.ObjectCacheMisses);
        Assert.Equal(1, stats.OverviewVariantBuilds);
        Assert.Equal(18, stats.OverviewChunks);
    }

    [Fact]
    public void Zoom_change_draws_resident_neighbour_bucket_for_every_deferred_chunk()
    {
        using var source = new Colors();
        source.Arrive(A, 1);
        var document = Document(Grid());
        using var cache = new ObjectOverviewCache(overviewBuildBudgetMilliseconds: 0);
        Draw(document, source, cache, false);
        var (pixels, stats) = Draw(document, source, cache, true, Scale / 2);
        Assert.Equal(1, stats.ObjectCacheMisses);
        Assert.Equal(9, stats.OverviewChunks);
        Assert.True(stats.DetailTilesPending > 0);
        for (var y = 0; y < 3; y++)
        for (var x = 0; x < 3; x++)
        {
            var offset = ((y * 32 + 16) * 192 + x * 32 + 16) * 4;
            Assert.True(pixels[offset] != 255 || pixels[offset + 1] != 255 || pixels[offset + 2] != 255);
        }
    }

    [Fact]
    public void Other_render_context_cannot_replace_the_variant_of_a_deferred_old_raster()
    {
        using var source = new Colors();
        source.Arrive(A, 1);
        // Painted stamps change their bounds when their colour becomes unavailable.
        var objects = Grid().Select(o => o with { Stamp = o.Stamp! with { ShadowOpacity = .7, ShadowBlur = 6 } }).ToArray();
        var document = Document(objects);
        using var slow = new ObjectOverviewCache(overviewBuildBudgetMilliseconds: 0);
        using var fast = new ObjectOverviewCache();
        var (before, _) = Draw(document, source, slow, false);
        // Evict the chunk plan's four variant slots while the first context retains its old image.
        for (var i = 0; i < 6; i++)
        {
            source.Arrive(A, i + 1);
            Draw(document, source, fast, false);
        }
        source.Remove(A);
        var (partial, stats) = Draw(document, source, slow, true);
        Assert.Equal(1, stats.ObjectCacheMisses);
        Assert.Equal(9, stats.OverviewChunks);
        Assert.Equal(Region(before, 0, 0), Region(partial, 0, 0));
    }

    [Fact]
    public void Removal_of_color_and_document_asset_invalidates_only_its_chunks()
    {
        using var source = new Colors();
        source.Arrive(A, 1); source.Arrive(B, 2);
        var document = Document([Stamp(A, 0, 0), Stamp(B, 1, 0)]);
        using var cache = new ObjectOverviewCache();
        Draw(document, source, cache, false);
        source.Remove(A);
        var (_, removed) = Draw(document, source, cache, false);
        Assert.Equal(1, removed.OverviewVariantBuilds);
        Assert.Equal(1, removed.ObjectCacheMisses);
        var (_, absent) = Draw(document.WithAssets(document.Assets.Where(a => a.Sha256 != B).ToArray()), source, cache, false);
        Assert.Equal(1, absent.OverviewVariantBuilds);
        Assert.Equal(1, absent.ObjectCacheMisses);
    }

    private static SceneObject[] Grid() => Enumerable.Range(0, 9).Select(i => Stamp(A, i % 3, i / 3)).ToArray();

    private static SceneObject Stamp(string sha, int x, int y) => new(ObjectId.New(), SceneObjectKind.Stamp,
        "Гора", x * Chunk + 4096, y * Chunk + 4096, 16, 16, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(sha) };

    private static MapDocument Document(SceneObject[] objects) => new(DocumentId.New(), "Overview delta",
        DateTimeOffset.UnixEpoch, new CanvasDefinition(24576, 24576, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF"),
         new ObjectLayer(LayerId.New(), "Горы", true, false, 1, objects)],
        assets: new[] { A, B, Unused }.Select(sha => new MapAssetReference(sha, "Гора владельца", "image/png", "png",
            ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "owner-fixture", "user-owned", catalogId: sha)).ToArray());

    private static (byte[] Pixels, RenderStats Stats) Draw(MapDocument document, Colors source, ObjectOverviewCache cache, bool refine,
        double scale = Scale)
    {
        using var surface = SKSurface.Create(new SKImageInfo(192, 192))!;
        surface.Canvas.Scale((float)scale);
        var stats = new RenderStats();
        cache.BeginFrame();
        try
        {
            SceneRenderer.Render(surface.Canvas, document, new RenderOptions
            {
                Stamps = source, ObjectCache = cache, Stats = stats, ViewScale = scale,
                LodTier = RenderLodTier.Overview, VisibleWorld = new(0, 0, 24575, 24575),
                RequestRefinedFrame = refine ? source.RequestFrame : null,
            });
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            return (bitmap.Bytes, stats);
        }
        finally { cache.EndFrame(); }
    }

    private static byte[] Region(byte[] pixels, int x, int y) => Enumerable.Range(y * 64, 64)
        .SelectMany(row => pixels.Skip((row * 192 + x * 64) * 4).Take(64 * 4)).ToArray();

    private sealed class CountingAssets(IReadOnlyList<MapAssetReference> assets) : IReadOnlyList<MapAssetReference>
    {
        public int Reads { get; private set; }
        public int Count => assets.Count;
        public MapAssetReference this[int index] { get { Reads++; return assets[index]; } }
        public IEnumerator<MapAssetReference> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Colors : IStampImageSource, IDisposable
    {
        private readonly SKImage[] _images;
        private readonly Dictionary<string, int> _ready = [];
        private long _revision;
        public long? OverviewColorRevision => _revision;
        public int Lookups { get; private set; }
        public int Preparations { get; private set; }
        public int FramesRequested { get; private set; }
        public void RequestFrame() => FramesRequested++;
        public Colors()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
            _images = Enumerable.Range(1, 6).Select(i => SKImage.FromEncodedData(Path.Combine(root!.FullName,
                "tests", "fixtures", "assets", $"owner-mountain-{i:00}.png"))!).ToArray();
        }
        public void Arrive(string sha, int fixture) { _ready[sha] = fixture - 1; _revision++; }
        public void Remove(string sha) { _ready.Remove(sha); _revision++; }
        public SKImage? TryGetImage(string sha256) => _ready.TryGetValue(sha256, out var i) ? _images[i] : null;
        public SKColor? TryGetOverviewColor(string sha256)
        {
            Lookups++;
            return TryGetImage(sha256) is { } image ? SceneRenderer.StampOverviewColor(image) : null;
        }
        public bool PrepareOverview(IEnumerable<string> assets, Action ready)
        { Preparations++; return assets.All(_ready.ContainsKey); }
        public void Dispose() { foreach (var image in _images) image.Dispose(); }
    }
}
