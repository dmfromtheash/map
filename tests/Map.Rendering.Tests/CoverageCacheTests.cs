using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Resources;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class CoverageCacheTests
{
    private static WorkingMemoryBudget Memory() => new(() => new(16L * 1024 * WorkingMemoryBudget.MiB,
        8L * 1024 * WorkingMemoryBudget.MiB, 512 * WorkingMemoryBudget.MiB));

    private static TerrainFieldCache.Key Key(int x, object source, double scale = .25) => new("test", default,
        new WorldBounds(x, 0, x + 256, 256), scale, (256, 256), [TerrainFieldCache.Revision(source)]);

    [Fact]
    public void Fields_obey_byte_limit_pressure_and_lru_and_rebuild_only_evicted_values()
    {
        var memory = Memory();
        using var cache = new TerrainFieldCache(8192, memory);
        var source = new object();
        var hot = Key(0, source);
        var image = cache.Get<byte[]>(hot, 1024, () => new byte[1024]);
        for (var i = 1; i < 20; i++)
        {
            cache.Get<byte[]>(Key(i, source), 1024, () => new byte[1024]);
            Assert.Same(image, cache.Get<byte[]>(hot, 1024, () => throw new InvalidOperationException()));
            Assert.InRange(cache.Bytes, 1, 8192);
        }
        var builds = cache.Builds;
        memory.SimulatedCachePressure = true;
        for (var i = 0; i < 3; i++) memory.PollCachePressure();
        Assert.Same(image, cache.Get<byte[]>(hot, 1024, () => throw new InvalidOperationException()));
        Assert.InRange(cache.Bytes, 1, 4096);
        cache.Get<byte[]>(Key(1, source), 1024, () => new byte[1024]);
        Assert.Equal(builds + 1, cache.Builds);
        Assert.Equal(cache.Bytes, Assert.Single(memory.ReadCacheStatistics()).Bytes);
        cache.Clear();
        Assert.Equal(0, Assert.Single(memory.ReadCacheStatistics()).Bytes);
    }

    [Fact]
    public void Exact_scale_origin_revision_and_field_kind_are_distinct_and_large_fields_are_transient()
    {
        using var cache = new TerrainFieldCache(4096, Memory());
        var source = new object();
        var key = Key(0, source);
        var bytes = cache.Get<byte[]>(key, 1024, () => new byte[1024]);
        Assert.Same(bytes, cache.Get<byte[]>(Key(0, source), 1024, () => throw new InvalidOperationException()));
        Assert.NotSame(bytes, cache.Get<byte[]>(Key(0, source, .251), 1024, () => new byte[1024]));
        Assert.NotSame(bytes, cache.Get<byte[]>(Key(1, source), 1024, () => new byte[1024]));
        Assert.NotSame(bytes, cache.Get<byte[]>(key with { Kind = "other" }, 1024, () => new byte[1024]));
        var before = cache.Bytes;
        cache.Get<byte[]>(Key(999, source), 16384, () => new byte[16384]);
        Assert.Equal(before, cache.Bytes);
        Assert.NotEqual(Key(0, source), Key(0, new object()));
    }

    [Fact]
    public void Concurrent_requests_compute_one_field_and_evicted_values_remain_usable()
    {
        using var cache = new TerrainFieldCache(4096, Memory());
        var key = Key(0, new object());
        var builds = 0;
        Parallel.For(0, 32, _ => cache.Get<byte[]>(key, 1024, () =>
        {
            Interlocked.Increment(ref builds);
            return new byte[1024];
        }));
        Assert.Equal(1, builds);
        var value = cache.Get<byte[]>(key, 1024, () => throw new InvalidOperationException());
        cache.Clear();
        Assert.Equal(1024, value!.Length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Temporary(TerrainFieldCache cache)
    {
        var source = TileMap.Empty(1).With(new TileKey(0, 0), new byte[65536]);
        cache.Get<byte[]>(Key(0, source), 1024, () => new byte[1024]);
        return new WeakReference(source);
    }

    [Fact]
    public void Field_keys_do_not_keep_discarded_document_snapshots_alive()
    {
        using var cache = new TerrainFieldCache(4096, Memory());
        var weak = Temporary(cache);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
        Assert.True(cache.Bytes > 0);
    }

    [Fact]
    public void Field_revisions_ignore_edits_outside_the_padded_block_and_track_removal_inside()
    {
        var pixels = new byte[65536];
        var map = TileMap.Empty(1).With(new TileKey(0, 0), pixels).With(new TileKey(32, 0), pixels);
        var bounds = new WorldBounds(0, 0, 512, 512);
        var first = TerrainFieldCache.TileRevisions(map, bounds);
        var far = map.With(new TileKey(32, 0), new byte[65536]);
        Assert.Equal(first, TerrainFieldCache.TileRevisions(far, bounds));
        var near = far.With(new TileKey(0, 0), new byte[65536]);
        Assert.NotEqual(first, TerrainFieldCache.TileRevisions(near, bounds));
        Assert.Empty(TerrainFieldCache.TileRevisions(near.With(new TileKey(0, 0), null), bounds));
    }

    [Fact]
    public void Three_land_layers_keep_more_than_256_blocks_and_one_edit_reduces_one_block()
    {
        CoarseCoverage.Clear();
        try
        {
            var pixels = Enumerable.Repeat((byte)173, 65536).ToArray();
            var tiles = TileMap.Empty(1).WithMany(from y in Enumerable.Range(0, 10)
                from x in Enumerable.Range(0, 10)
                select new KeyValuePair<TileKey, byte[]?>(new TileKey(x * 16, y * 16), pixels));
            var lands = Enumerable.Range(0, 3).Select(_ => new LandLayer(LayerId.New(), "land", true, false, 1, tiles, new LandEdgeStyle())).ToArray();
            var document = new MapDocument(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
                new CanvasDefinition(131072, 131072, "px", "#FFFFFFFF"),
                [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"), .. lands]);
            var view = new WorldBounds(0, 0, 40959, 40959);
            CoarseCoverage.Prepare(document, view);
            using var surface = SKSurface.Create(new SKImageInfo(16, 16))!;
            surface.Canvas.Scale(1f / 4096);
            SKImage Image(byte[] coverage, TileImageKind kind) => SKImage.FromPixelCopy(
                new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul), new byte[256 * 256 * 4]);
            void Draw(LandLayer land) => CoarseCoverage.Draw(surface.Canvas, land.Id, land.GeometryTiles, view,
                new SKSamplingOptions(SKFilterMode.Linear), null, Image);
            var before = CoarseCoverage.Builds;
            foreach (var land in lands) Draw(land);
            Assert.Equal(before + 300, CoarseCoverage.Builds);
            foreach (var land in lands) Draw(land);
            Assert.Equal(before + 300, CoarseCoverage.Builds);
            Draw(lands[0] with { GeometryTiles = tiles.With(new TileKey(0, 0), new byte[65536]) });
            Assert.Equal(before + 301, CoarseCoverage.Builds);
            Draw(lands[0]); // Undo must restore the old bytes, not a stale derived image.
            Assert.Equal(before + 302, CoarseCoverage.Builds);
        }
        finally { CoarseCoverage.Clear(); }
    }

    [Theory]
    [InlineData(.0625)]
    [InlineData(.25)]
    [InlineData(.251)]
    [InlineData(1.0)]
    [InlineData(1.75)]
    public void Cached_sea_relief_and_coast_are_byte_identical_to_uncached_renders(double scale)
    {
        var document = CoverageCacheBenchmark.Fixture(huge: false);
        using var tiles = new TileImageCache();
        var view = new WorldBounds(17.25, 23.5, 512, 512);
        byte[] direct;
        TerrainFieldCache.Bypass = true;
        try { direct = CoverageCacheBenchmark.Render(document, view, scale, tiles); }
        finally { TerrainFieldCache.Bypass = false; }
        Assert.Equal(direct, CoverageCacheBenchmark.Render(document, view, scale, tiles));
        Assert.Equal(direct, CoverageCacheBenchmark.Render(document, view, scale, tiles));
    }

    [Fact]
    public void Tiled_coast_cache_matches_the_uncached_tiled_renderer_byte_for_byte()
    {
        var (budget, tile) = (CoastDistanceField.Budget, CoastDistanceField.Tile);
        try
        {
            CoastDistanceField.Budget = 10000; CoastDistanceField.Tile = 300;
            Cached_sea_relief_and_coast_are_byte_identical_to_uncached_renders(1);
        }
        finally { (CoastDistanceField.Budget, CoastDistanceField.Tile) = (budget, tile); }
    }
}
