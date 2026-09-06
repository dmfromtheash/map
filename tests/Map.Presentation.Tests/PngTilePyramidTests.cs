using Map.Export;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PngTilePyramidTests
{
    [Fact]
    public async Task Opaque_rgb_png_from_standard_encoder_opens_without_changing_color()
    {
        using var temp = new Temp();
        using var bitmap = new SKBitmap(new SKImageInfo(19, 17, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        Assert.Equal(2, bytes[25]);
        var source = System.IO.Path.Combine(temp.Path, "rgb.png");
        File.WriteAllBytes(source, bytes);
        var pyramid = await PngTilePyramid.OpenAsync(source, System.IO.Path.Combine(temp.Path, "cache"));
        using var decoded = SKBitmap.Decode(pyramid.CheckTile(0, 0, 0));
        Assert.Equal(SKColors.OrangeRed, decoded.GetPixel(18, 16));
    }

    [Fact]
    public async Task Level_zero_preserves_standard_png_pixels_across_seams_and_odd_edges_and_cache_reopens()
    {
        using var temp = new Temp();
        var source = CreateSource(temp.Path);
        var originalBytes = File.ReadAllBytes(source);
        var result = await PngTilePyramid.OpenAsync(source, System.IO.Path.Combine(temp.Path, "cache"));
        using var expected = SKBitmap.Decode(source);
        Assert.Equal((2051, 1027, 3), (result.Width, result.Height, result.Levels));
        for (var y = 0; y < result.Height; y += 1024)
        for (var x = 0; x < result.Width; x += 1024)
        {
            using var tile = SKBitmap.Decode(result.CheckTile(0, x / 1024, y / 1024));
            for (var dy = 0; dy < tile.Height; dy++)
            for (var dx = 0; dx < tile.Width; dx++)
                Assert.Equal(expected.GetPixel(x + dx, y + dy), tile.GetPixel(dx, dy));
        }
        using var reduced = SKBitmap.Decode(result.CheckTile(1, 1, 0));
        // Last, odd source column is replicated, not dropped or read outside the row.
        var a = expected.GetPixel(2050, 0); var b = expected.GetPixel(2050, 1);
        var actual = reduced.GetPixel(1, 0);
        Assert.InRange(Math.Abs(actual.Red - (a.Red + b.Red + 1) / 2), 0, 1);
        using var alphaTile = SKBitmap.Decode(result.CheckTile(1, 0, 0));
        var alphaPixel = alphaTile.GetPixel(500, 500);
        Assert.Equal((byte)128, alphaPixel.Alpha);
        Assert.Equal((byte)255, alphaPixel.Blue);
        Assert.Equal((byte)0, alphaPixel.Red);
        Assert.Equal(originalBytes, File.ReadAllBytes(source));
        var manifest = System.IO.Path.Combine(result.DirectoryPath, "manifest.json");
        var written = File.GetLastWriteTimeUtc(manifest);
        var reused = await PngTilePyramid.OpenAsync(source, System.IO.Path.Combine(temp.Path, "cache"));
        Assert.Equal(result, reused);
        Assert.Equal(written, File.GetLastWriteTimeUtc(manifest));
        File.Delete(result.TilePath(0, 1, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => PngTilePyramid.OpenAsync(source, System.IO.Path.Combine(temp.Path, "cache")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_or_disk_exhaustion_never_publishes_partial_cache_or_changes_source(bool cancel)
    {
        using var temp = new Temp();
        var source = CreateSource(temp.Path);
        var original = File.ReadAllBytes(source);
        var cache = System.IO.Path.Combine(temp.Path, "cache");
        using var cts = new CancellationTokenSource();
        var report = new InlineProgress(p => { if (cancel && p.Fraction > 0) cts.Cancel(); });
        var diskReads = 0;
        var settings = new ExportEngineSettings { AvailableDiskBytes = _ => cancel || ++diskReads < 3 ? long.MaxValue : 0 };
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PngTilePyramid.OpenAsync(source, cache, report, cts.Token, settings));
        else
            await Assert.ThrowsAsync<IOException>(() => PngTilePyramid.OpenAsync(source, cache, report, cts.Token, settings));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task Corrupt_source_crc_is_rejected_without_publishing_cache()
    {
        using var temp = new Temp();
        var source = CreateSource(temp.Path);
        var data = File.ReadAllBytes(source);
        data[^1] ^= 1;
        File.WriteAllBytes(source, data);
        var cache = System.IO.Path.Combine(temp.Path, "cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => PngTilePyramid.OpenAsync(source, cache));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));
    }

    private static string CreateSource(string directory)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2051, 1027, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256), x < 400 ? (byte)128 : (byte)255));
        bitmap.SetPixel(1000, 1000, new SKColor(255, 0, 0, 0)); bitmap.SetPixel(1001, 1000, SKColors.Blue);
        bitmap.SetPixel(1000, 1001, new SKColor(255, 0, 0, 0)); bitmap.SetPixel(1001, 1001, SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = System.IO.Path.Combine(directory, "source.png");
        using var file = File.Create(path); bytes.SaveTo(file);
        return path;
    }

    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => report(value); }
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "map-viewer-test-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
