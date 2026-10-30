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
    public async Task Opaque_rgb_tiles_written_on_several_cores_match_rgba_tiles_written_on_one()
    {
        using var temp = new Temp();
        var random = new Random(7);
        var rows = new byte[1027][];
        for (var y = 0; y < rows.Length; y++)
        {
            rows[y] = new byte[2051 * 4];
            random.NextBytes(rows[y]);
            for (var x = 3; x < rows[y].Length; x += 4) rows[y][x] = 255;
        }
        string Write(string name, bool opaque)
        {
            var path = System.IO.Path.Combine(temp.Path, name);
            using var file = File.Create(path);
            using var png = new StreamingPngWriter(file, 2051, rows.Length, opaque);
            foreach (var row in rows) png.WriteRow(row);
            png.Complete();
            return path;
        }
        var rgb = await PngTilePyramid.OpenAsync(Write("rgb.png", opaque: true), System.IO.Path.Combine(temp.Path, "rgb"),
            settings: new ExportEngineSettings { CompressionParallelism = 4 });
        var rgba = await PngTilePyramid.OpenAsync(Write("rgba.png", opaque: false), System.IO.Path.Combine(temp.Path, "rgba"),
            settings: new ExportEngineSettings { CompressionParallelism = 1 });
        Assert.Equal(3, rgb.Levels);
        for (var level = 0; level < rgb.Levels; level++)
        {
            var (width, height) = rgb.LevelSize(level);
            for (var row = 0; row * 1024 < height; row++)
            for (var column = 0; column * 1024 < width; column++)
            {
                var rgbTile = rgb.CheckTile(level, column, row);
                Assert.Equal(2, File.ReadAllBytes(rgbTile)[25]);
                using var a = SKBitmap.Decode(rgbTile);
                using var b = SKBitmap.Decode(rgba.CheckTile(level, column, row));
                Assert.Equal(b.Pixels, a.Pixels);
            }
        }
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

    [Fact]
    public async Task Idle_cache_evicts_oldest_tiles_and_an_oversized_entry_without_touching_sources()
    {
        using var temp = new Temp();
        var source = CreateSource(temp.Path);
        var original = File.ReadAllBytes(source);
        var cache = System.IO.Path.Combine(temp.Path, "cache");
        var first = await PngTilePyramid.OpenAsync(source, cache);
        var secondSource = System.IO.Path.Combine(temp.Path, "second.png");
        File.Copy(source, secondSource);
        var second = await PngTilePyramid.OpenAsync(secondSource, cache);
        Directory.SetLastWriteTimeUtc(first.DirectoryPath, DateTime.UtcNow.AddDays(-2));
        var bytes = Directory.EnumerateFiles(second.DirectoryPath, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        var result = PngViewCache.TrimUnused(cache, bytes);
        Assert.False(result.InUse); Assert.True(result.RemovedBytes > 0);
        Assert.Equal(bytes, result.RetainedBytes);
        Assert.False(Directory.Exists(first.DirectoryPath)); Assert.True(Directory.Exists(second.DirectoryPath));
        Assert.True(PngViewCache.TrimUnused(cache, 0).RemovedBytes > 0);
        Assert.False(Directory.Exists(second.DirectoryPath));
        Assert.Equal(original, File.ReadAllBytes(source)); Assert.Equal(original, File.ReadAllBytes(secondSource));
    }

    [Fact]
    public async Task Every_active_reader_pins_tiles_until_disposed_and_cleanup_preserves_unknown_content()
    {
        using var temp = new Temp();
        var source = CreateSource(temp.Path);
        var cache = System.IO.Path.Combine(temp.Path, "cache");
        var pyramid = await PngTilePyramid.OpenAsync(source, cache);
        using var first = await PngViewCache.AcquireAsync(cache);
        using var second = await PngViewCache.AcquireAsync(cache);
        Assert.True(PngViewCache.TrimUnused(cache, 0).InUse);
        first.Dispose();
        Assert.True(PngViewCache.TrimUnused(cache, 0).InUse);
        Assert.True(File.Exists(pyramid.CheckTile(0, 0, 0)));
        second.Dispose();
        var unexpected = System.IO.Path.Combine(pyramid.DirectoryPath, "owner.mapproj");
        File.WriteAllText(unexpected, "Keep this unknown file");
        var unknownDirectory = System.IO.Path.Combine(cache, "user-images");
        Directory.CreateDirectory(unknownDirectory);
        var unknownImage = System.IO.Path.Combine(unknownDirectory, "image.png");
        File.Copy(source, unknownImage);
        Assert.Equal(0, PngViewCache.TrimUnused(cache, 0).RemovedBytes);
        Assert.Equal("Keep this unknown file", File.ReadAllText(unexpected));
        Assert.True(File.Exists(pyramid.CheckTile(0, 0, 0))); Assert.True(File.Exists(unknownImage));
        File.Delete(unexpected);
        Assert.True(PngViewCache.TrimUnused(cache, 0).RemovedBytes > 0);
        Assert.True(File.Exists(unknownImage)); Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Interrupted_eviction_is_cleaned_even_under_quota_and_reopen_builds_a_complete_cache()
    {
        using var temp = new Temp();
        var source = CreateSource(temp.Path);
        var cache = System.IO.Path.Combine(temp.Path, "cache");
        var pyramid = await PngTilePyramid.OpenAsync(source, cache);
        // Simulate an interruption after atomic retirement and one tile removal.
        var retired = pyramid.DirectoryPath + ".evicting-" + Guid.NewGuid().ToString("N");
        Directory.Move(pyramid.DirectoryPath, retired);
        File.Delete(System.IO.Path.Combine(retired, "0", "0_0.png"));
        var reopened = await PngTilePyramid.OpenAsync(source, cache);
        Assert.True(File.Exists(reopened.CheckTile(0, 0, 0)));
        var result = PngViewCache.TrimUnused(cache); // Both tiny entries fit, but retired work must finish.
        Assert.True(result.RemovedBytes > 0); Assert.False(Directory.Exists(retired));
        Assert.True(File.Exists(reopened.CheckTile(0, 0, 0))); Assert.True(File.Exists(source));
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
