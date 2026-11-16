using System.Reflection;
using Map.Assets;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class EdgePanMipPlanTests : FrameSourceFixture
{
    [Fact]
    public async Task A_float_geometry_mip_miss_extends_the_plan_and_retries_off_frame()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(OwnerImage(), new(), DateTimeOffset.UnixEpoch);
        await using var source = new CatalogStampImageSource(catalog, Document, () => null,
            derivedRoot: Path.Combine(_root, "derived"), startWatcher: _ => { });
        var samples = Field<AssetImageCache>(source, "_samples");
        using var original = SKBitmap.Decode(OwnerImage());
        foreach (var side in new[] { 16, 32 })
        {
            var level = (int)Math.Floor(Math.Log2(Math.Min((double)record.Width / side, (double)record.Height / side)));
            using var bitmap = original.Resize(new SKImageInfo(record.Width >> level, record.Height >> level),
                new SKSamplingOptions(SKFilterMode.Linear));
            bitmap.SetImmutable();
            samples.AdoptPixels(record.Sha256 + "#" + level, SKImage.FromBitmap(bitmap));
        }
        var plan = source.CreatePreparationPlan()!;
        plan.Add([(record.Sha256, 32, 32)]);
        Assert.True(plan.Prepare(() => { }));
        using (var pinned = plan.Pin())
        {
            Assert.NotNull(pinned);
            Assert.Null(pinned.TryGetImageForPixelSize(record.Sha256, 16, 16));
            Assert.True(pinned.UnresolvedRequestVersion > 0);
        }
        // Same cache epoch: adding the key must invalidate the plan's old ReadyAt shortcut.
        Assert.True(plan.Prepare(() => { }));
        using var retried = plan.Pin();
        Assert.NotNull(retried);
        Assert.NotNull(retried.TryGetImageForPixelSize(record.Sha256, 16, 16));
        Assert.Equal(0, retried.UnresolvedRequestVersion);
    }

    [Fact]
    public async Task Overlapping_tiles_keep_shared_requests_until_the_last_tile_leaves()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(OwnerImage(), new(), DateTimeOffset.UnixEpoch);
        await using var source = new CatalogStampImageSource(catalog, Document, () => null,
            derivedRoot: Path.Combine(_root, "derived"), startWatcher: _ => { });
        var first = source.CreatePreparationPlan()!;
        var second = source.CreatePreparationPlan()!;
        first.Add([(record.Sha256, 32, 32)]); second.Add([(record.Sha256, 32, 32)]);
        var wanted = Field<Dictionary<StampDerivedAssetStore.PixelKey, int>>(source, "_leaseWanted");
        using var a = first.KeepRequests();
        using var b = second.KeepRequests();
        Assert.Equal(2, Assert.Single(wanted).Value);
        a!.Dispose();
        Assert.Equal(1, Assert.Single(wanted).Value);
        a.Dispose(); // cancellation and completion can both dispose the demand
        b!.Dispose();
        Assert.Empty(wanted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Temporary_failures_wait_but_confirmed_missing_pixels_can_finish_a_tile(bool terminal)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = catalog.Import(OwnerImage(), new(), DateTimeOffset.UnixEpoch);
        await using var source = new CatalogStampImageSource(catalog, Document, () => null,
            derivedRoot: Path.Combine(_root, "derived"), startWatcher: _ => { });
        var plan = source.CreatePreparationPlan()!;
        plan.Add([(record.Sha256, 32, 32)]);
        var level = (int)Math.Floor(Math.Log2(Math.Min(record.Width / 32d, record.Height / 32d)));
        var key = new StampDerivedAssetStore.PixelKey(record.Sha256, 0, level);
        Field<HashSet<string>>(source, "_decodeFailed").Add(record.Sha256 + "#" + level);
        if (terminal) Field<HashSet<StampDerivedAssetStore.PixelKey>>(source, "_cacheableMissing").Add(key);
        using var pinned = plan.Pin();
        if (!terminal) { Assert.Null(pinned); return; }
        Assert.NotNull(pinned);
        Assert.Null(pinned.TryGetImageForPixelSize(record.Sha256, 32, 32));
        Assert.True(pinned.LastImageRequestCacheableMissing);
        Assert.Equal(0, pinned.UnresolvedRequestVersion);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
