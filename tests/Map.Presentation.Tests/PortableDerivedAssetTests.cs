using System.Text.Json;
using Map.Assets;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PortableDerivedAssetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-portable-tests", Guid.NewGuid().ToString("N"));

    private static AssetRecord ImportOwner(AssetCatalog catalog)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        return catalog.Import(Path.Combine(root!.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"), new(), DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Moving_a_catalog_keeps_portable_pixels_exact_with_an_empty_machine_cache()
    {
        var original = Path.Combine(_root, "original");
        AssetRecord record;
        await using (var catalog = new AssetCatalog(original))
        {
            record = ImportOwner(catalog);
            await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache"));
            await store.BuildPortableAsync(record.Sha256);
            await catalog.FlushMetadataAsync();
        }
        var moved = Path.Combine(_root, "moved");
        Directory.Move(original, moved);
        await using var reopened = new AssetCatalog(moved);
        await using var reader = new StampDerivedAssetStore(reopened, Path.Combine(_root, "empty-cache"));
        var header = (await reader.Request(record.Sha256))!;
        Assert.NotNull(header.PortablePath);
        Assert.StartsWith(moved, header.Original.Path, StringComparison.OrdinalIgnoreCase);
        var before = File.GetLastWriteTimeUtc(header.PortablePath);
        using var expected = new AssetImageCache();
        expected.Get(record.Sha256, reopened.SourcePath(record));
        foreach (var level in new[] { 0, 1, 2, 3, 5, 8 })
        {
            await reader.RequestPixels(header, 0, level).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reader.TryTakePixels(new(record.Sha256, 0, level), out var ready));
            using var image = ready!.Image;
            Assert.NotNull(image);
            using var actual = SKBitmap.FromImage(image);
            using var baseline = SKBitmap.FromImage(expected.GetForPixelSize(record.Sha256,
                record.Width / Math.Pow(2, level), record.Height / Math.Pow(2, level))!);
            Assert.Equal(baseline.Bytes, actual.Bytes);
        }
        Assert.Equal(0, reader.Builds);
        Assert.Equal(0, reader.Failures);
        Assert.Equal(before, File.GetLastWriteTimeUtc(header.PortablePath));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("pixels")]
    [InlineData("truncated")]
    public async Task Corrupt_portable_data_recovers_without_writing_the_library(string damage)
    {
        using var errors = new StringWriter();
        using var listener = new System.Diagnostics.TextWriterTraceListener(errors);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog);
        string path;
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache")))
            path = await builder.BuildPortableAsync(record.Sha256);
        var corrupted = File.ReadAllBytes(path);
        if (damage == "manifest") corrupted[44] ^= 1;
        else if (damage == "truncated") corrupted = corrupted[..^3];
        else
        {
            var headerLength = BitConverter.ToInt32(corrupted, 8);
            using var json = JsonDocument.Parse(corrupted.AsMemory(44, headerLength));
            var entry = json.RootElement.GetProperty("Entries")[0];
            corrupted[44 + headerLength + entry.GetProperty("Offset").GetInt32()] ^= 0x7f;
        }
        File.WriteAllBytes(path, corrupted);
        await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(_root, "empty-cache"));
        var header = (await reader.Request(record.Sha256))!;
        Assert.NotNull(header);
        await reader.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reader.TryTakePixels(new(record.Sha256, 0, 0), out var ready));
        using var image = ready!.Image;
        Assert.True(image is not null, errors.ToString());
        using var baselineCache = new AssetImageCache();
        using var actual = SKBitmap.FromImage(image);
        using var baseline = SKBitmap.FromImage(baselineCache.Get(record.Sha256, catalog.SourcePath(record))!);
        Assert.Equal(baseline.Bytes, actual.Bytes);
        Assert.Equal(corrupted, File.ReadAllBytes(path));
        }
        finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task Portable_ladder_keeps_each_step_pixels_and_invalidates_when_a_later_step_changes()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var owner = ImportOwner(catalog);
        using var original = SKBitmap.Decode(catalog.SourcePath(owner));
        using var surface = SKSurface.Create(new SKImageInfo(2049, 1025, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var image = SKImage.FromBitmap(original)) surface.Canvas.DrawImage(image, new SKRect(0, 0, 2049, 1025));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        var scaledPath = Path.Combine(_root, "owner-scaled.png");
        File.WriteAllBytes(scaledPath, encoded.ToArray());
        var record = catalog.Import(scaledPath, new(), DateTimeOffset.UnixEpoch);
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache")))
            await builder.BuildPortableAsync(record.Sha256);
        await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(_root, "empty-cache"));
        var header = (await reader.Request(record.Sha256))!;
        Assert.NotNull(header.PortablePath);
        foreach (var (step, level) in new[] { (1, 0), (2, 0), (2, 3) })
        {
            Assert.True(catalog.TryDetailLevelPath(record, step, out var ladder));
            using var expected = new AssetImageCache();
            var source = expected.Get("step", ladder)!;
            await reader.RequestPixels(header, step, level).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reader.TryTakePixels(new(record.Sha256, step, level), out var ready));
            using var actual = ready!.Image!;
            using var actualBitmap = SKBitmap.FromImage(actual);
            using var baseline = SKBitmap.FromImage(expected.GetForPixelSize("step", source.Width / Math.Pow(2, level), source.Height / Math.Pow(2, level))!);
            Assert.Equal(baseline.Bytes, actualBitmap.Bytes);
        }
        Assert.Equal(0, reader.Builds);
        Assert.Equal(0, reader.Failures);
        Assert.True(catalog.TryDetailLevelPath(record, 2, out var changed));
        var generation = reader.Generation;
        File.SetLastWriteTimeUtc(changed!, File.GetLastWriteTimeUtc(changed!).AddSeconds(1));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (reader.Generation == generation && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(reader.Generation > generation);
        Assert.Null((await reader.Request(record.Sha256))!.PortablePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
