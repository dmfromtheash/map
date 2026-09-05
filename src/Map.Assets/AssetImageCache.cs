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
            ResidentBytes -= entry.ResidentBytes;
            entry.Image.Dispose();
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
            ResidentBytes -= oldest.Value.ResidentBytes;
            oldest.Value.Image.Dispose();
            _entries.Remove(oldest.Key);
            Evictions++;
        }
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
            entry.Image.Dispose();
        }

        _entries.Clear();
        DecodedBytes = 0;
        ResidentBytes = 0;
    }
}
