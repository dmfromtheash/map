using Map.Core.Documents;
using SkiaSharp;

namespace Map.Assets;

public readonly record struct AssetImageMetadata(
    int Width, int Height, long PixelCount, long DecodedBytes, long EncodedBytes);

/// <summary>
/// One allocation policy for every user-provided image. Metadata is checked before pixel decode;
/// the arithmetic is 64-bit and checked so dimensions cannot wrap into a small allocation.
/// </summary>
public static class AssetImagePolicy
{
    public const int MaxDimension = 32_768;
    public const long MaxPixels = 32L * 1024 * 1024;
    public const long MaxDecodedBytes = 128L * 1024 * 1024;
    public const long MaxEncodedBytes = 64L * 1024 * 1024;
    public const long DefaultCacheDecodedBytes = 256L * 1024 * 1024;
    public const int MaxThumbnailDimension = 2_048;
    public const long MaxThumbnailPixels = 4L * 1024 * 1024;
    public const long MaxThumbnailEncodedBytes = 8L * 1024 * 1024;

    public static AssetImageMetadata Validate(int width, int height, long encodedBytes)
    {
        if (encodedBytes is <= 0 or > MaxEncodedBytes)
        {
            throw new InvalidDataException(
                $"Encoded image size {encodedBytes} exceeds the {MaxEncodedBytes}-byte image budget.");
        }

        long pixels;
        long decodedBytes;
        try
        {
            pixels = checked((long)width * height);
            decodedBytes = checked(pixels * 4L);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Image dimensions overflow the decode budget.", ex);
        }

        if (width is <= 0 or > MaxDimension
            || height is <= 0 or > MaxDimension
            || pixels > MaxPixels
            || decodedBytes > MaxDecodedBytes)
        {
            throw new InvalidDataException(
                $"Image {width}x{height} needs {decodedBytes} decoded bytes and exceeds the image budget.");
        }

        return new AssetImageMetadata(width, height, pixels, decodedBytes, encodedBytes);
    }

    public static AssetImageMetadata Inspect(SKCodec codec, long encodedBytes)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _ = ActualFormat(codec);
        return Validate(codec.Info.Width, codec.Info.Height, encodedBytes);
    }

    public static AssetImageMetadata Inspect(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.LongLength is <= 0 or > MaxEncodedBytes)
        {
            return Validate(1, 1, encoded.LongLength);
        }

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The file is not a supported PNG, JPEG or WebP image.");
        return Inspect(codec, encoded.LongLength);
    }

    public static AssetImageMetadata Inspect(
        byte[] encoded, string mediaType, string extension)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (!ProjectAssetFormatPolicy.TryGetDeclaredFormat(mediaType, extension, out var expected))
        {
            throw new InvalidDataException("Image metadata does not declare a supported format pair.");
        }

        if (encoded.LongLength is <= 0 or > MaxEncodedBytes)
        {
            return Validate(1, 1, encoded.LongLength);
        }

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The file is not a supported PNG, JPEG or WebP image.");
        var actual = ActualFormat(codec);
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Encoded image format {actual} does not match its declared {mediaType}/{extension} metadata.");
        }

        return Validate(codec.Info.Width, codec.Info.Height, encoded.LongLength);
    }

    /// <summary>Read at most the encoded-image budget from one stable file handle.</summary>
    public static byte[] ReadEncodedFile(string path, long maxEncodedBytes = MaxEncodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncodedBytes);
        if (maxEncodedBytes > MaxEncodedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEncodedBytes), $"The read limit cannot exceed {MaxEncodedBytes} bytes.");
        }

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maxEncodedBytes)
        {
            throw new InvalidDataException(
                $"Encoded image size {stream.Length} exceeds the {maxEncodedBytes}-byte caller budget.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The image grew beyond its declared encoded-size budget.");
        }

        return bytes;
    }

    /// <summary>
    /// Reads and fully decodes a convenience thumbnail under its own smaller budget. Failure is
    /// intentionally represented as absence: an optional cover never invalidates healthy content.
    /// </summary>
    public static byte[]? TryReadOptionalThumbnail(string path)
    {
        try
        {
            var bytes = ReadEncodedFile(path, MaxThumbnailEncodedBytes);
            var metadata = Inspect(bytes);
            if (metadata.Width > MaxThumbnailDimension
                || metadata.Height > MaxThumbnailDimension
                || metadata.PixelCount > MaxThumbnailPixels)
            {
                return null;
            }

            using var decoded = DecodeBitmap(bytes, MaxThumbnailDimension, MaxThumbnailDimension);
            return bytes;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Decode an already bounded and identity-verified payload without reopening a path.</summary>
    public static SKBitmap DecodeBitmap(
        byte[] encoded, int? maximumWidth = null, int? maximumHeight = null)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.LongLength is <= 0 or > MaxEncodedBytes)
        {
            _ = Validate(1, 1, encoded.LongLength);
        }

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The file is not a supported PNG, JPEG or WebP image.");
        var metadata = Inspect(codec, encoded.LongLength);
        if (maximumWidth is { } maxWidth && metadata.Width > maxWidth
            || maximumHeight is { } maxHeight && metadata.Height > maxHeight)
        {
            throw new InvalidDataException(
                $"Image dimensions {metadata.Width}x{metadata.Height} exceed the caller decode budget.");
        }

        var info = new SKImageInfo(
            metadata.Width, metadata.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Image pixels cannot be decoded ({result}).");
        }

        return bitmap;
    }

    /// <summary>
    /// Decode at the codec's nearest supported scale for a thumbnail. Formats without native
    /// subsampling remain bounded by the global 128 MiB policy rather than allocating arbitrarily.
    /// </summary>
    public static SKBitmap DecodeThumbnailSource(byte[] encoded, int targetDimension)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetDimension);
        if (encoded.LongLength is <= 0 or > MaxEncodedBytes)
        {
            _ = Validate(1, 1, encoded.LongLength);
        }

        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("The file is not a supported PNG, JPEG or WebP image.");
        var metadata = Inspect(codec, encoded.LongLength);
        var scale = Math.Min(1f, targetDimension / (float)Math.Max(metadata.Width, metadata.Height));
        var scaled = codec.GetScaledDimensions(scale);
        if (scaled.Width <= 0 || scaled.Height <= 0
            || scaled.Width > metadata.Width || scaled.Height > metadata.Height)
        {
            scaled = new SKSizeI(metadata.Width, metadata.Height);
        }

        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"Image pixels cannot be decoded ({result}).");
        }

        return bitmap;
    }

    private static ProjectAssetImageFormat ActualFormat(SKCodec codec) => codec.EncodedFormat switch
    {
        SKEncodedImageFormat.Png => ProjectAssetImageFormat.Png,
        SKEncodedImageFormat.Jpeg => ProjectAssetImageFormat.Jpeg,
        SKEncodedImageFormat.Webp => ProjectAssetImageFormat.Webp,
        _ => throw new InvalidDataException(
            $"Image format {codec.EncodedFormat} is outside the PNG, JPEG and WebP allowlist."),
    };
}
