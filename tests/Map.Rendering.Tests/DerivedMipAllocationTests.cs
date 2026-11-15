using Map.Assets;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class DerivedMipAllocationTests
{
    [Fact]
    public async Task Canvas_mips_cross_the_handoff_as_native_images_with_identical_pixels()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-native-mip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var fixtures = new DirectoryInfo(AppContext.BaseDirectory);
            while (fixtures is not null && !File.Exists(Path.Combine(fixtures.FullName, "Map.slnx"))) fixtures = fixtures.Parent;
            Assert.NotNull(fixtures);
            var record = catalog.Import(Path.Combine(fixtures.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"),
                new(), DateTimeOffset.UnixEpoch);
            await using var store = new StampDerivedAssetStore(catalog, Path.Combine(root, "derived"),
                startWatcher: static _ => { }, nativeMipImages: true);
            var header = await store.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(header);
            var expected = store.ReadMip(header, header.FirstMip, out var width, out var height);
            Assert.NotNull(expected);
            Assert.True(expected.Length > 85_000, "fixture must exercise a mip that used to enter the LOH");
            var completed = await store.RequestMip(header, header.FirstMip).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(store.TryTakeMip(record.Sha256, header.FirstMip, out var taken));
            Assert.Same(completed, taken);
            using var image = completed.Image;
            Assert.Null(completed.Pixels);
            Assert.NotNull(image);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            Assert.True(image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0));
            Assert.Equal(expected, bitmap.Bytes);
            Console.WriteLine($"NATIVE mip={width}x{height} referenceBytes={expected.Length} handoffManagedPixels=0; pixels identical");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Reading_the_smallest_mip_does_not_allocate_discarded_larger_pixels()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-mip-alloc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var fixtures = new DirectoryInfo(AppContext.BaseDirectory);
            while (fixtures is not null && !File.Exists(Path.Combine(fixtures.FullName, "Map.slnx"))) fixtures = fixtures.Parent;
            Assert.NotNull(fixtures);
            var record = catalog.Import(Path.Combine(fixtures.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"),
                new(), DateTimeOffset.UnixEpoch);
            await using var store = new StampDerivedAssetStore(catalog, Path.Combine(root, "derived"), startWatcher: static _ => { });
            var header = await store.Request(record.Sha256).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(header);
            Assert.True(header.MipCount > 1);
            var level = header.FirstMip + header.MipCount - 1;
            var expected = store.ReadMip(header, level, out var width, out var height);
            Assert.NotNull(expected);
            Assert.Equal(width * height * 4, expected.Length);
            store.ReadMip(header, level, out _, out _); // warm the checksum and reader
            var before = GC.GetAllocatedBytesForCurrentThread();
            var actual = store.ReadMip(header, level, out var actualWidth, out var actualHeight);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"MIP smallest={width}x{height} levels={header.MipCount} allocated={bytes} bytes");
            Assert.Equal(width, actualWidth); Assert.Equal(height, actualHeight);
            Assert.Equal(expected, actual);
            Assert.True(bytes < 4096, $"smallest mip allocated {bytes} bytes (limit 4096)");
        }
        finally { Directory.Delete(root, true); }
    }
}
