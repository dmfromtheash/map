using Map.Core.Resources;
using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class CacheMemoryPressureTests
{
    private const long ImageBytes = 64 * 64 * 4;

    private static WorkingMemoryBudget Budget(bool pressure)
    {
        var budget = new WorkingMemoryBudget(() => new(16L * 1024 * WorkingMemoryBudget.MiB,
            8L * 1024 * WorkingMemoryBudget.MiB, 512 * WorkingMemoryBudget.MiB))
        { SimulatedCachePressure = pressure };
        budget.PollCachePressure();
        budget.PollCachePressure(); // Stage 1 reaches the moderate (decoded) class.
        return budget;
    }

    private static SKImage Raster()
    {
        using var surface = SKSurface.Create(new SKImageInfo(64, 64))!;
        surface.Canvas.Clear(SKColors.SaddleBrown);
        return surface.Snapshot();
    }

    private static AssetImageCache Filled(WorkingMemoryBudget budget, int count, long protectMs)
    {
        var cache = new AssetImageCache(capacity: 1024, decodedByteCapacity: 32 * ImageBytes) { PressureProtectedMilliseconds = protectMs };
        cache.TrackMemory(budget, "asset-images", CacheRebuildCost.Moderate);
        for (var i = 0; i < count; i++) Assert.NotNull(cache.AdoptPixels("a" + i, Raster()));
        return cache;
    }

    [Fact]
    public void Under_pressure_a_full_cache_falls_to_the_watermark_oldest_first()
    {
        using var cache = Filled(Budget(pressure: true), 32, protectMs: 0);
        Assert.Equal(32 * ImageBytes, cache.ResidentBytes);
        var released = cache.TrimForMemoryPressure(sliceMilliseconds: 50);
        Assert.Equal(16 * ImageBytes, released);
        Assert.Equal(16 * ImageBytes, cache.ResidentBytes);
        Assert.False(cache.Contains("a0"));
        Assert.False(cache.Contains("a15"));
        Assert.True(cache.Contains("a16"));
        Assert.True(cache.Contains("a31"));
        Assert.Equal(1, cache.PressureTrims);
    }

    [Fact]
    public void Without_pressure_nothing_is_trimmed()
    {
        using var cache = Filled(Budget(pressure: false), 32, protectMs: 0);
        Assert.Equal(0, cache.TrimForMemoryPressure(sliceMilliseconds: 50));
        Assert.Equal(32, cache.Count);
        Assert.Equal(0, cache.PressureTrims);
    }

    [Fact]
    public void Recently_used_and_pinned_images_survive_pressure()
    {
        using var cache = Filled(Budget(pressure: true), 32, protectMs: 300);
        var pinned = cache.Get("a1", null)!;
        cache.Pin(pinned);
        Thread.Sleep(400);
        // The visible set of the next frames: four images drawn just now.
        foreach (var key in new[] { "a0", "a5", "a10", "a20" }) Assert.NotNull(cache.Get(key, null));
        cache.TrimForMemoryPressure(sliceMilliseconds: 50);
        foreach (var key in new[] { "a0", "a5", "a10", "a20", "a1" }) Assert.True(cache.Contains(key), key);
        Assert.Equal(16 * ImageBytes, cache.ResidentBytes);
        Assert.NotEqual(IntPtr.Zero, pinned.Handle);
        cache.Unpin(pinned);
    }

    [Fact]
    public void Nothing_is_trimmed_while_the_whole_cache_is_the_working_set()
    {
        using var cache = Filled(Budget(pressure: true), 32, protectMs: 60_000);
        Assert.Equal(0, cache.TrimForMemoryPressure(sliceMilliseconds: 50));
        Assert.Equal(32, cache.Count);
    }

    [Fact]
    public void A_trimmed_image_is_decoded_again_on_the_next_request()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png");
        var budget = Budget(pressure: true);
        long size;
        using (var measure = new AssetImageCache(capacity: 1))
        {
            Assert.NotNull(measure.Get("m", path));
            size = measure.ResidentBytes;
        }
        // Room for four copies of the owner's mountain; the watermark keeps two.
        using var cache = new AssetImageCache(capacity: 16, decodedByteCapacity: 4 * size) { PressureProtectedMilliseconds = 0 };
        cache.TrackMemory(budget, "asset-images", CacheRebuildCost.Moderate);
        var first = cache.Get("m", path)!;
        var width = first.Width;
        for (var i = 0; i < 3; i++) Assert.NotNull(cache.Get("copy" + i, path));
        Assert.Equal(4 * size, cache.ResidentBytes);
        Assert.Equal(2 * size, cache.TrimForMemoryPressure(sliceMilliseconds: 50));
        Assert.False(cache.Contains("m"));
        Assert.Equal(IntPtr.Zero, first.Handle);
        var again = cache.Get("m", path);
        Assert.NotNull(again);
        Assert.Equal(width, again!.Width);
        var statistics = Assert.Single(budget.ReadCacheStatistics());
        Assert.True(statistics.Trims >= 1);
        Assert.True(statistics.TrimmedBytes > 0);
    }
}
