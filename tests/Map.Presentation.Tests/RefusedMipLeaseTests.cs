using System.Reflection;
using Map.Assets;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class RefusedMipLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-refused-lease-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Refused_late_pixels_keep_the_canvas_request_pending_until_admitted()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        using var source = new CatalogStampImageSource(catalog, () => null!, () => null, cacheBytes: 4L * 64 * 64 * 4);
        var samples = ImageProtectionFrameTests.Field<AssetImageCache>(source, "_samples");
        var late = ImageProtectionFrameTests.Field<HashSet<StampDerivedAssetStore.PixelKey>>(source, "_latePixels");
        var key = new StampDerivedAssetStore.PixelKey("late", 0, 0);
        var method = typeof(CatalogStampImageSource).GetMethod("AdoptDerivedMip", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var surface = SKSurface.Create(new SKImageInfo(64, 64))!;
        // The pixels themselves do not affect this lifetime test; no asset is added to a document.
        samples.AdoptPixels("held", surface.Snapshot());
        late.Add(key);
        method.Invoke(source, [key, new StampDerivedAssetStore.Mip(null, 64, 64, false, Image: surface.Snapshot()), null]);
        Assert.Equal(1, source.AdmissionRefused);
        Assert.True(source.ImageRequestsPending);
        samples.Frame += 2;
        method.Invoke(source, [key, new StampDerivedAssetStore.Mip(null, 64, 64, false, Image: surface.Snapshot()), null]);
        Assert.False(source.ImageRequestsPending);
    }

    [Fact]
    public async Task Exact_lease_finishes_without_canvas_frames_when_the_shelf_is_full_of_pinned_pixels()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var fixtureRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixtureRoot is not null && !File.Exists(Path.Combine(fixtureRoot.FullName, "Map.slnx"))) fixtureRoot = fixtureRoot.Parent;
        var records = Enumerable.Range(1, 2).Select(i => catalog.Import(Path.Combine(fixtureRoot!.FullName,
            "tests", "fixtures", "assets", $"owner-mountain-{i:00}.png"), new(), DateTimeOffset.UnixEpoch)).ToArray();
        var keys = records.Select(record => (StampDerivedAssetStore.PixelKey)typeof(CatalogStampImageSource)
            .GetMethod("PixelKey", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [record, 32d, 32d])!).ToArray();
        var sizes = records.Select((r, i) => (Width: Math.Max(1, r.Width >> (keys[i].Step + keys[i].Level)),
            Height: Math.Max(1, r.Height >> (keys[i].Step + keys[i].Level)))).ToArray();
        var shelf = sizes.Max(s => (long)s.Width * s.Height * 4);
        using var source = new CatalogStampImageSource(catalog, () => null!, () => null, cacheBytes: shelf * 4,
            derivedRoot: Path.Combine(_root, "derived"));
        // Inject already finished worker results: this exercises public lease ownership and refusal without
        // depending on Mac memory-mapped derived storage or on the worker winning a scheduling race.
        var store = ImageProtectionFrameTests.Field<StampDerivedAssetStore>(source, "_derived");
        var pending = ImageProtectionFrameTests.Field<object>(store, "_mips");
        var requestType = typeof(StampDerivedAssetStore).GetNestedType("MipRequest", BindingFlags.NonPublic)!;
        for (var i = 0; i < keys.Length; i++)
        {
            using var original = SKImage.FromEncodedData(Path.Combine(fixtureRoot!.FullName,
                "tests", "fixtures", "assets", $"owner-mountain-{i + 1:00}.png"));
            using var surface = SKSurface.Create(new SKImageInfo(sizes[i].Width, sizes[i].Height))!;
            surface.Canvas.DrawImage(original, new SKRect(0, 0, sizes[i].Width, sizes[i].Height));
            var request = Activator.CreateInstance(requestType, new CancellationTokenSource())!;
            requestType.GetProperty("Task")!.SetValue(request, Task.FromResult(new StampDerivedAssetStore.Mip(null,
                sizes[i].Width, sizes[i].Height, false, Image: surface.Snapshot())));
            pending.GetType().GetProperty("Item")!.SetValue(pending, request, [keys[i]]);
        }
        var oldLimit = CatalogStampImageSource.ExactWaitLimit;
        CatalogStampImageSource.ExactWaitLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            var lease = source.ExactLease();
            using (lease)
            {
                Assert.NotNull(lease.TryGetImageForPixelSize(records[0].Sha256, 32, 32));
                var second = lease.TryGetImageForPixelSize(records[1].Sha256, 32, 32);
                Assert.NotNull(second);
                Assert.Equal(0, lease.PreparingLookups);
                Assert.Same(second, lease.TryGetImageForPixelSize(records[1].Sha256, 32, 32));
                Assert.NotEqual(IntPtr.Zero, second.Handle);
                Assert.Equal(1, source.AdmissionRefused);
                lease.Dispose();
                Assert.Equal(IntPtr.Zero, second.Handle);
            }
        }
        finally { CatalogStampImageSource.ExactWaitLimit = oldLimit; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
