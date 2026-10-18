using System.Security.Cryptography;
using SkiaSharp;

namespace Map.Assets;

/// <summary>Bounded lazy decode cache shared by catalog thumbnails or full stamp images.</summary>
public sealed class AssetImageCache : IDisposable
{
    private sealed class Entry
    {
        public required SKImage Image { get; init; }
        public required long DecodedBytes { get; init; }
        public required long ResidentBytes { get; init; }
        public long LastUsed { get; set; }
        public Dictionary<int, SKImage> Mips { get; } = [];
        public long MipBytes { get; set; }
        public void Dispose()
        {
            foreach (var image in Mips.Values) image.Dispose();
            Image.Dispose();
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;
    private readonly long _decodedByteCapacity;
    private readonly Func<byte[], SKImage?> _decode;
    private long _clock;

    public AssetImageCache(
        int capacity = 64,
        long decodedByteCapacity = AssetImagePolicy.DefaultCacheDecodedBytes)
        : this(capacity, decodedByteCapacity, Decode)
    {
    }

    internal AssetImageCache(
        int capacity, long decodedByteCapacity, Func<byte[], SKImage?> decode)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodedByteCapacity);
        ArgumentNullException.ThrowIfNull(decode);
        _capacity = capacity;
        _decodedByteCapacity = decodedByteCapacity;
        _decode = decode;
    }

    public int Count => _entries.Count;
    public long DecodedBytes { get; private set; }
    /// <summary>
    /// Conservative native-cache weight. Encoded-backed Skia images may retain their encoded
    /// payload as well as decoded pixels, so admission and eviction use both parts.
    /// </summary>
    public long ResidentBytes { get; private set; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Evictions { get; private set; }

    public SKImage? Get(string key, string? path)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUsed = ++_clock;
            Hits++;
            return existing.Image;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > AssetImagePolicy.MaxEncodedBytes)
            {
                return null;
            }

            return GetEncoded(key, AssetImagePolicy.ReadEncodedFile(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Hash and decode the exact same bounded bytes, closing the path-validation TOCTOU.</summary>
    public SKImage? GetVerified(
        string key,
        string? path,
        string expectedSha256,
        string? expectedMediaType = null,
        string? expectedExtension = null)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUsed = ++_clock;
            Hits++;
            return existing.Image;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > AssetImagePolicy.MaxEncodedBytes)
            {
                return null;
            }

            var encoded = AssetImagePolicy.ReadEncodedFile(file.FullName);
            var actual = Convert.ToHexStringLower(SHA256.HashData(encoded));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)
                ? GetEncoded(key, encoded, expectedMediaType, expectedExtension)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public SKImage? GetEncoded(
        string key,
        byte[] encoded,
        string? expectedMediaType = null,
        string? expectedExtension = null)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUsed = ++_clock;
            Hits++;
            return existing.Image;
        }

        AssetImageMetadata metadata;
        try
        {
            metadata = expectedMediaType is null || expectedExtension is null
                ? AssetImagePolicy.Inspect(encoded)
                : AssetImagePolicy.Inspect(encoded, expectedMediaType, expectedExtension);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var incomingResidentBytes = checked(metadata.DecodedBytes + metadata.EncodedBytes);
        if (incomingResidentBytes > _decodedByteCapacity)
        {
            return null;
        }

        TrimForIncoming(incomingResidentBytes);
        var image = _decode(encoded);
        if (image is null) return null;

        Misses++;
        _entries[key] = new Entry
        {
            Image = image,
            DecodedBytes = metadata.DecodedBytes,
            ResidentBytes = incomingResidentBytes,
            LastUsed = ++_clock,
        };
        DecodedBytes += metadata.DecodedBytes;
        ResidentBytes += incomingResidentBytes;
        return image;
    }

    public void Remove(string key)
    {
        if (_entries.Remove(key, out var entry))
        {
            DecodedBytes -= entry.DecodedBytes;
            ResidentBytes -= entry.ResidentBytes + entry.MipBytes;
            entry.Dispose();
        }
    }

    /// <summary>Make room before native decode so the configured aggregate is also a peak gate.</summary>
    private void TrimForIncoming(long incomingResidentBytes)
    {
        while (_entries.Count >= _capacity
            || ResidentBytes > _decodedByteCapacity - incomingResidentBytes)
        {
            var oldest = _entries.MinBy(pair => pair.Value.LastUsed);
            DecodedBytes -= oldest.Value.DecodedBytes;
            ResidentBytes -= oldest.Value.ResidentBytes + oldest.Value.MipBytes;
            oldest.Value.Dispose();
            _entries.Remove(oldest.Key);
            Evictions++;
        }
    }

    /// <summary>Power-of-two minification of an already verified source, charged to the same LRU
    /// byte budget. Close zoom always receives the original. No source pixels are changed.</summary>
    public SKImage? GetForPixelSize(string key, double pixelWidth, double pixelHeight)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        entry.LastUsed = ++_clock;
        var image = entry.Image;
        var ratio = Math.Min(image.Width / Math.Max(1, pixelWidth), image.Height / Math.Max(1, pixelHeight));
        var level = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, ratio))), 0, 15);
        if (level == 0) return image;
        if (entry.Mips.TryGetValue(level, out var cached)) return cached;
        // Populate the intervening levels as well. A fit view normally asks for a small image
        // first; generating only that level forced each later zoom-in to resample the original
        // again on the frame thread. The pyramid costs at most one third of the source pixels.
        long bytes = 0;
        for (var n = 1; n <= level; n++)
            if (!entry.Mips.ContainsKey(n)) bytes += (long)Math.Max(1, image.Width >> n) * Math.Max(1, image.Height >> n) * 4;
        // The bitmap and immutable image may overlap during construction.
        while (ResidentBytes + bytes * 2 > _decodedByteCapacity)
        {
            var candidates = _entries.Where(pair => !string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0) return image;
            var oldest = candidates.MinBy(pair => pair.Value.LastUsed);
            Remove(oldest.Key); Evictions++;
        }
        var source = image;
        for (var n = 1; n <= level; n++)
        {
            if (entry.Mips.TryGetValue(n, out var held)) { source = held; continue; }
            var width = Math.Max(1, image.Width >> n); var height = Math.Max(1, image.Height >> n);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var pixels = bitmap.PeekPixels();
            if (!source.ScalePixels(pixels, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))) return image;
            bitmap.SetImmutable();
            source = SKImage.FromBitmap(bitmap);
            entry.Mips.Add(n, source);
            var added = (long)width * height * 4;
            entry.MipBytes += added; ResidentBytes += added;
        }
        return source;
    }

    private static SKImage? Decode(byte[] encoded)
    {
        // Own decoded pixels once. Encoded-backed images can repeatedly decode under native
        // raster-cache pressure while thousands of stamps are composited into different tiles.
        using var bitmap = SKBitmap.Decode(encoded);
        return bitmap is null ? null : SKImage.FromBitmap(bitmap);
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
        DecodedBytes = 0;
        ResidentBytes = 0;
    }
}
