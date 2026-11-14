using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class FallbackImageTests
{
    [Fact]
    public void Recency_touch_and_pins_evict_the_oldest_eligible_entry()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png");
        using var cache = new AssetImageCache(capacity: 3);
        var a = cache.Get("a", path)!;
        cache.Get("b", path);
        cache.Get("c", path);
        cache.Pin(a);
        cache.Get("b", null); // c is now the oldest unpinned entry after pinned a.
        cache.Get("d", path);
        Assert.True(cache.Contains("a")); Assert.True(cache.Contains("b"));
        Assert.False(cache.Contains("c"));
        cache.Unpin(a);
        cache.Get("e", path);
        Assert.False(cache.Contains("a"));
        Assert.Equal(3, cache.Count);
        Assert.Equal(2, cache.Evictions);
    }

    [Fact]
    public void Fallback_identity_is_reused_and_shares_the_entry_retirement_lifetime()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        var path = Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png");
        using var cache = new AssetImageCache();
        var exact = cache.Get("mountain", path)!;
        var fallback = cache.GetFallbackImage("mountain")!;
        Assert.NotSame(exact, fallback);
        Assert.Same(fallback, cache.GetFallbackImage("mountain"));
        using var a = SKBitmap.FromImage(exact);
        using var b = SKBitmap.FromImage(fallback);
        Assert.Equal(a.Bytes, b.Bytes);
        cache.Pin(fallback);
        cache.Remove("mountain");
        cache.DisposeRetired();
        Assert.NotEqual(IntPtr.Zero, fallback.Handle);
        Assert.NotEqual(IntPtr.Zero, exact.Handle);
        cache.Unpin(fallback);
        cache.DisposeRetired();
        Assert.Equal(IntPtr.Zero, fallback.Handle);
        Assert.Equal(IntPtr.Zero, exact.Handle);
        Assert.Equal(0, cache.RetiredCount);
    }
}
