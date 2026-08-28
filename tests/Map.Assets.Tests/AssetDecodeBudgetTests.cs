using SkiaSharp;

namespace Map.Assets.Tests;

public sealed class AssetDecodeBudgetTests
{
    [Theory]
    [InlineData(32_768, 32_768, 1_024)]
    [InlineData(16_384, 4_096, 1_024)]
    [InlineData(4_096, 4_096, 70L * 1024 * 1024)]
    public void Oversized_metadata_is_rejected_without_decoding_pixels(
        int width, int height, long encodedBytes)
    {
        var failure = Assert.Throws<InvalidDataException>(() =>
            AssetImagePolicy.Validate(width, height, encodedBytes));

        Assert.Contains("budget", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normal_large_image_fits_the_documented_budget()
    {
        var metadata = AssetImagePolicy.Validate(4_096, 4_096, 16L * 1024 * 1024);

        Assert.Equal(4_096, metadata.Width);
        Assert.Equal(4_096, metadata.Height);
        Assert.Equal(64L * 1024 * 1024, metadata.DecodedBytes);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(32_769, 1, 1)]
    [InlineData(1, 32_769, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 67_108_865)]
    public void Every_individual_image_budget_boundary_rejects_its_invalid_side(
        int width, int height, long encodedBytes)
    {
        Assert.Throws<InvalidDataException>(() =>
            AssetImagePolicy.Validate(width, height, encodedBytes));
    }

    [Fact]
    public void Inspect_reports_metadata_for_the_exact_encoded_payload()
    {
        var encoded = Image(120, 80, SKColors.CadetBlue);

        var metadata = AssetImagePolicy.Inspect(encoded);

        Assert.Equal(120, metadata.Width);
        Assert.Equal(80, metadata.Height);
        Assert.Equal(9_600, metadata.PixelCount);
        Assert.Equal(38_400, metadata.DecodedBytes);
        Assert.Equal(encoded.LongLength, metadata.EncodedBytes);
        var empty = Assert.Throws<InvalidDataException>(() => AssetImagePolicy.Inspect([]));
        Assert.Contains("Encoded image size", empty.Message);
        Assert.Throws<InvalidDataException>(() => AssetImagePolicy.Inspect([1, 2, 3]));
        Assert.Throws<ArgumentNullException>(() => AssetImagePolicy.Inspect((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => AssetImagePolicy.Inspect((SKCodec)null!, 1));
    }

    [Fact]
    public void Codec_allowlist_and_declared_format_identity_are_enforced_before_decode()
    {
        var png = Image(8, 8, SKColors.CornflowerBlue);
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

        Assert.Throws<InvalidDataException>(() => AssetImagePolicy.Inspect(gif));
        Assert.Throws<InvalidDataException>(() =>
            AssetImagePolicy.Inspect(png, "image/jpeg", "jpg"));
        Assert.Equal(64, AssetImagePolicy.Inspect(png, "image/png", "png").PixelCount);
    }

    [Fact]
    public void Bounded_file_read_returns_exact_bytes_and_rejects_empty_or_oversized_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-image-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var validPath = Path.Combine(root, "valid.png");
        var emptyPath = Path.Combine(root, "empty.png");
        var oversizedPath = Path.Combine(root, "oversized.png");
        var encoded = Image(24, 16, SKColors.OrangeRed);
        File.WriteAllBytes(validPath, encoded);
        File.WriteAllBytes(emptyPath, []);
        using (var stream = File.Create(oversizedPath))
        {
            stream.SetLength(AssetImagePolicy.MaxEncodedBytes + 1);
        }

        try
        {
            Assert.Equal(encoded, AssetImagePolicy.ReadEncodedFile(validPath));
            Assert.Throws<InvalidDataException>(() => AssetImagePolicy.ReadEncodedFile(emptyPath));
            Assert.Throws<InvalidDataException>(() => AssetImagePolicy.ReadEncodedFile(oversizedPath));
            Assert.Throws<FileNotFoundException>(() =>
                AssetImagePolicy.ReadEncodedFile(Path.Combine(root, "missing.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Bitmap_decode_enforces_caller_dimensions_and_rejects_truncated_pixels()
    {
        var encoded = Image(96, 64, SKColors.MediumPurple);
        using var decoded = AssetImagePolicy.DecodeBitmap(encoded, 96, 64);
        Assert.Equal(96, decoded.Width);
        Assert.Equal(64, decoded.Height);
        Assert.Throws<InvalidDataException>(() => AssetImagePolicy.DecodeBitmap(encoded, 95, 64));
        Assert.Throws<InvalidDataException>(() => AssetImagePolicy.DecodeBitmap(encoded, 96, 63));
        var empty = Assert.Throws<InvalidDataException>(() => AssetImagePolicy.DecodeBitmap([]));
        Assert.Contains("Encoded image size", empty.Message);
        Assert.Throws<ArgumentNullException>(() => AssetImagePolicy.DecodeBitmap(null!));

        var truncated = encoded[..(encoded.Length / 2)];
        Assert.Throws<InvalidDataException>(() => AssetImagePolicy.DecodeBitmap(truncated));
    }

    [Fact]
    public void Thumbnail_decode_uses_a_positive_bounded_codec_scale()
    {
        var encoded = Image(400, 200, SKColors.SeaGreen, SKEncodedImageFormat.Jpeg);

        using var thumbnailSource = AssetImagePolicy.DecodeThumbnailSource(encoded, 100);

        Assert.Equal(100, thumbnailSource.Width);
        Assert.Equal(50, thumbnailSource.Height);
        using var fullSize = AssetImagePolicy.DecodeThumbnailSource(encoded, 800);
        Assert.Equal(400, fullSize.Width);
        Assert.Equal(200, fullSize.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AssetImagePolicy.DecodeThumbnailSource(encoded, 0));
        Assert.Throws<InvalidDataException>(() =>
            AssetImagePolicy.DecodeThumbnailSource([1, 2, 3], 100));
        var empty = Assert.Throws<InvalidDataException>(() =>
            AssetImagePolicy.DecodeThumbnailSource([], 100));
        Assert.Contains("Encoded image size", empty.Message);
        Assert.Throws<ArgumentNullException>(() =>
            AssetImagePolicy.DecodeThumbnailSource(null!, 100));
    }

    [Fact]
    public void Cache_evicts_by_decoded_bytes_even_below_its_item_count()
    {
        using var cache = new AssetImageCache(capacity: 64, decodedByteCapacity: 45_000);
        var first = Image(100, 100, SKColors.Red);
        var second = Image(100, 100, SKColors.Blue);

        Assert.NotNull(cache.GetEncoded("first", first));
        Assert.NotNull(cache.GetEncoded("second", second));

        Assert.Equal(1, cache.Count);
        Assert.InRange(cache.DecodedBytes, 40_000, 45_000);
        Assert.Equal(1, cache.Evictions);
    }

    [Fact]
    public void Cache_accounts_for_encoded_bytes_retained_by_lazy_native_images()
    {
        using var cache = new AssetImageCache(capacity: 64, decodedByteCapacity: 50_000);
        var first = PaddedImage(SKColors.Red, 30_000);
        var second = PaddedImage(SKColors.Blue, 30_000);

        Assert.NotNull(cache.GetEncoded("first", first));
        Assert.Equal(first.LongLength + 4, cache.ResidentBytes);
        Assert.NotNull(cache.GetEncoded("second", second));

        Assert.Equal(1, cache.Count);
        Assert.Equal(second.LongLength + 4, cache.ResidentBytes);
        Assert.Equal(1, cache.Evictions);
    }

    [Fact]
    public void Cache_refuses_an_image_over_its_total_byte_budget()
    {
        using var cache = new AssetImageCache(capacity: 64, decodedByteCapacity: 20_000);

        Assert.Null(cache.GetEncoded("too-large", Image(100, 100, SKColors.Green)));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.DecodedBytes);
    }

    [Fact]
    public void Cache_evicts_before_the_next_native_decode_starts()
    {
        AssetImageCache? cache = null;
        var countsAtDecode = new List<int>();
        cache = new AssetImageCache(64, 45_000, encoded =>
        {
            countsAtDecode.Add(cache!.Count);
            using var data = SKData.CreateCopy(encoded);
            return SKImage.FromEncodedData(data);
        });
        using (cache)
        {
            Assert.NotNull(cache.GetEncoded("first", Image(100, 100, SKColors.Red)));
            Assert.NotNull(cache.GetEncoded("second", Image(100, 100, SKColors.Blue)));

            Assert.Equal([0, 0], countsAtDecode);
            Assert.Equal(1, cache.Count);
        }
    }

    [Fact]
    public void Optional_thumbnail_is_fully_decoded_under_its_smaller_budget_or_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-thumbnail-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var validPath = Path.Combine(root, "valid.png");
        var corruptPath = Path.Combine(root, "corrupt.png");
        var tooWidePath = Path.Combine(root, "too-wide.png");
        var tooLargePath = Path.Combine(root, "too-large.png");
        var valid = Image(64, 48, SKColors.Goldenrod);
        File.WriteAllBytes(validPath, valid);
        File.WriteAllBytes(corruptPath, [1, 2, 3, 4]);
        File.WriteAllBytes(
            tooWidePath,
            Image(AssetImagePolicy.MaxThumbnailDimension + 1, 1, SKColors.Black));
        using (var stream = File.Create(tooLargePath))
        {
            stream.SetLength(AssetImagePolicy.MaxThumbnailEncodedBytes + 1);
        }

        try
        {
            Assert.Equal(valid, AssetImagePolicy.TryReadOptionalThumbnail(validPath));
            Assert.Null(AssetImagePolicy.TryReadOptionalThumbnail(corruptPath));
            Assert.Null(AssetImagePolicy.TryReadOptionalThumbnail(tooWidePath));
            Assert.Null(AssetImagePolicy.TryReadOptionalThumbnail(tooLargePath));
            Assert.Null(AssetImagePolicy.TryReadOptionalThumbnail(Path.Combine(root, "missing.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] PaddedImage(SKColor color, int totalLength)
    {
        var image = Image(1, 1, color);
        Assert.True(image.Length < totalLength);
        Array.Resize(ref image, totalLength);
        return image;
    }

    private static byte[] Image(
        int width, int height, SKColor color,
        SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(format, 100);
        return encoded.ToArray();
    }
}
