using Map.Core.Resources;
using SkiaSharp;

namespace Map.Assets.Tests;

/// <summary>DEC-113 p.3 step 1: an adopted image never evicts one the view used in the last frames.</summary>
public sealed class NeededImageProtectionTests
{
    private const long ImageBytes = 64 * 64 * 4;

    private static SKImage Raster()
    {
        using var surface = SKSurface.Create(new SKImageInfo(64, 64))!;
        surface.Canvas.Clear(SKColors.SaddleBrown);
        return surface.Snapshot();
    }

    private static AssetImageCache Full(int protectedFrames)
    {
        var cache = new AssetImageCache(capacity: 1024, decodedByteCapacity: 4 * ImageBytes) { ProtectedFrames = protectedFrames, Frame = 1 };
        for (var i = 0; i < 4; i++)
            Assert.Equal(AssetImageCache.AdoptResult.Adopted, cache.TryAdoptPixels("a" + i, Raster(), out _));
        return cache;
    }

    [Fact]
    public void A_shelf_full_of_images_used_this_frame_refuses_a_new_one_and_evicts_nothing()
    {
        using var cache = Full(protectedFrames: 2);
        var result = cache.TryAdoptPixels("new", Raster(), out var adopted);
        Assert.Equal(AssetImageCache.AdoptResult.Refused, result);
        Assert.Null(adopted);
        Assert.Equal(1, cache.AdmissionRefused);
        Assert.Equal(0, cache.Evictions);
        for (var i = 0; i < 4; i++) Assert.True(cache.Contains("a" + i));
        Assert.False(cache.Contains("new"));
        Assert.Null(cache.AdoptPixels("again", Raster()));
    }

    [Fact]
    public void Images_not_used_for_the_protected_frames_are_evicted_oldest_first()
    {
        using var cache = Full(protectedFrames: 2);
        cache.Frame = 2;
        Assert.NotNull(cache.Get("a2", null)); // still used by the view
        Assert.NotNull(cache.Get("a3", null));
        cache.Frame = 3; // a0 and a1 were last used in frame 1: no longer protected
        Assert.Equal(AssetImageCache.AdoptResult.Adopted, cache.TryAdoptPixels("b0", Raster(), out _));
        Assert.Equal(AssetImageCache.AdoptResult.Adopted, cache.TryAdoptPixels("b1", Raster(), out _));
        Assert.False(cache.Contains("a0"));
        Assert.False(cache.Contains("a1"));
        Assert.True(cache.Contains("a2"));
        Assert.True(cache.Contains("a3"));
        // Now only images of frames 2-3 are left: the next one is refused, not a circle of evictions.
        Assert.Equal(AssetImageCache.AdoptResult.Refused, cache.TryAdoptPixels("b2", Raster(), out _));
        Assert.Equal(2, cache.Evictions);
    }

    [Fact]
    public void Without_protection_the_cache_stays_plain_lru()
    {
        using var cache = Full(protectedFrames: 0);
        Assert.Equal(AssetImageCache.AdoptResult.Adopted, cache.TryAdoptPixels("new", Raster(), out _));
        Assert.False(cache.Contains("a0"));
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(0, cache.AdmissionRefused);
    }

    [Fact]
    public void A_resident_key_is_reported_as_resident_not_refused()
    {
        using var cache = Full(protectedFrames: 2);
        Assert.Equal(AssetImageCache.AdoptResult.AlreadyResident, cache.TryAdoptPixels("a1", Raster(), out var adopted));
        Assert.NotNull(adopted);
        Assert.Equal(0, cache.AdmissionRefused);
    }

    [Fact]
    public void Refusal_does_not_evict_an_unprotected_prefix_or_keep_the_incoming_image_alive()
    {
        using var cache = Full(protectedFrames: 2);
        cache.Frame = 3;
        for (var i = 1; i < 4; i++) Assert.NotNull(cache.Get("a" + i, null));
        using var surface = SKSurface.Create(new SKImageInfo(128, 64))!;
        var incoming = surface.Snapshot();
        Assert.Equal(AssetImageCache.AdoptResult.Refused, cache.TryAdoptPixels("large", incoming, out var adopted));
        Assert.Null(adopted);
        Assert.Equal(IntPtr.Zero, incoming.Handle);
        Assert.True(cache.Contains("a0"));
        Assert.Equal(4 * ImageBytes, cache.ResidentBytes);
        Assert.Equal(0, cache.Evictions);
        Assert.Equal(1, cache.AdmissionRefused);
    }

    [Fact]
    public void Admission_uses_one_budget_snapshot_when_the_share_changes_during_eviction()
    {
        var budget = new WorkingMemoryBudget(() => new(16L * 1024 * WorkingMemoryBudget.MiB,
            8L * 1024 * WorkingMemoryBudget.MiB, 512 * WorkingMemoryBudget.MiB));
        CacheMemoryAccount? competing = null;
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64));
        using var pixels = bitmap.PeekPixels();
        using var cache = new AssetImageCache(capacity: 1024, decodedByteCapacity: 4 * ImageBytes)
            { ProtectedFrames = 2, Frame = 1 };
        cache.TrackMemory(budget, "images", CacheRebuildCost.Moderate);
        try
        {
            // The release callback deterministically places a background-style rebalance between
            // the preflight and the remaining evictions, without a timing-dependent thread race.
            cache.AdoptPixels("a0", SKImage.FromPixels(pixels, (_, _) =>
                competing = budget.RegisterCache("competitor", CacheRebuildCost.Cheap, budget.CacheBudgetBytes * 2))!);
            for (var i = 1; i < 4; i++) Assert.NotNull(cache.AdoptPixels("a" + i, Raster()));
            cache.Frame = 3;
            Assert.NotNull(cache.Get("a2", null));
            Assert.NotNull(cache.Get("a3", null));
            using var surface = SKSurface.Create(new SKImageInfo(128, 64))!;
            var result = cache.TryAdoptPixels("large", surface.Snapshot(), out var adopted);

            Assert.NotNull(competing);
            Assert.True(budget.ReadCacheStatistics().Single(s => s.Name == "images").LimitBytes < 4 * ImageBytes);
            Assert.Equal(AssetImageCache.AdoptResult.Adopted, result);
            Assert.NotNull(adopted);
            Assert.Equal(2, cache.Evictions);
            Assert.Equal(0, cache.AdmissionRefused);
            Assert.True(cache.Contains("a2"));
            Assert.True(cache.Contains("a3"));
        }
        finally { competing?.Dispose(); }
    }

    [Fact]
    public void A_larger_working_set_progresses_when_finished_tiles_stop_touching_their_images()
    {
        using var cache = Full(protectedFrames: 2);
        Assert.Equal(AssetImageCache.AdoptResult.Refused, cache.TryAdoptPixels("next-tile", Raster(), out _));
        cache.Frame = 2;
        Assert.Equal(AssetImageCache.AdoptResult.Refused, cache.TryAdoptPixels("next-tile", Raster(), out _));
        cache.Frame = 3; // completed tile draws its cached raster; it no longer reads these images
        Assert.Equal(AssetImageCache.AdoptResult.Adopted, cache.TryAdoptPixels("next-tile", Raster(), out _));
        Assert.Equal(1, cache.Evictions);
        Assert.Equal(2, cache.AdmissionRefused);
    }

    [Fact]
    public void Protection_alone_cannot_finish_one_plan_larger_than_the_shelf()
    {
        using var cache = Full(protectedFrames: 2);
        for (var frame = 2; frame <= 20; frame++)
        {
            cache.Frame = frame;
            // A still waiting plan (or another exact consumer) keeps reading its resident subset.
            for (var i = 0; i < 4; i++) Assert.NotNull(cache.Get("a" + i, null));
            Assert.Equal(AssetImageCache.AdoptResult.Refused, cache.TryAdoptPixels("missing", Raster(), out _));
        }
        Assert.False(cache.Contains("missing"));
        Assert.Equal(0, cache.Evictions);
        // This documented limitation requires the future budget fit/bias adapter, not an eviction of needed pixels.
    }
}
