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
        public LinkedListNode<string>? Recency { get; set; }
        public Dictionary<int, SKImage> Mips { get; } = [];
        public SKImage? FallbackImage { get; set; }
        public long MipBytes { get; set; }
        public long LastTick { get; set; }
        public void Dispose()
        {
            FallbackImage?.Dispose();
            foreach (var image in Mips.Values) image.Dispose();
            Image.Dispose();
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SKImage, Entry> _imageOwners = new(ReferenceEqualityComparer.Instance);
    // Images another thread is using (a lease of the stamp source): their entry is never evicted, and one removed
    // while in use waits in _retired. Eviction itself may also be asked to wait (DeferDisposal) — see below.
    private readonly Dictionary<SKImage, int> _pins = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _retired = [];
    private readonly int _capacity;
    private readonly long _decodedByteCapacity;
    private readonly Func<byte[], SKImage?> _decode;
    private readonly LinkedList<string> _recency = new();

    private Map.Core.Resources.CacheMemoryAccount? _account;

    /// <summary>Under memory pressure an image used within this time is never trimmed.</summary>
    public long PressureProtectedMilliseconds { get; set; } = 2000;
    public long PressureTrims { get; private set; }

    /// <summary>Reports this cache's bytes to the shared working-memory budget and follows its pressure target.</summary>
    public void TrackMemory(Map.Core.Resources.WorkingMemoryBudget budget, string name,
        Map.Core.Resources.CacheRebuildCost cost)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (_account is not null) throw new InvalidOperationException("The cache already reports to a budget.");
        _account = budget.RegisterCache(name, cost, _decodedByteCapacity);
    }

    /// <summary>The own byte limit, or this cache's share of the working-memory budget when that is smaller.</summary>
    private long ByteLimit => _account is null ? _decodedByteCapacity : Math.Min(_decodedByteCapacity, _account.LimitBytes);

    /// <summary>
    /// Called by the owner between frames, under whatever guards this cache. While Windows is short of memory it
    /// releases the least recently used images nobody touched within <see cref="PressureProtectedMilliseconds"/>,
    /// down to the budget's watermark, for at most <paramref name="sliceMilliseconds"/>. Pinned images stay; with
    /// <see cref="DeferDisposal"/> the released ones wait for <see cref="DisposeRetired"/> as any eviction does.
    /// Returns the bytes released.
    /// </summary>
    public long TrimForMemoryPressure(double sliceMilliseconds = 2)
    {
        if (_account is null) return 0;
        _account.Report(ResidentBytes);
        var target = _account.TargetBytes;
        if (ResidentBytes <= target) return 0;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = Environment.TickCount64;
        var before = ResidentBytes;
        var items = 0;
        for (var node = _recency.First; node is not null && ResidentBytes > target;)
        {
            var next = node.Next;
            var entry = _entries[node.Value];
            // The recency list is oldest first: the first protected image means every later one is protected too.
            if (now - entry.LastTick < PressureProtectedMilliseconds) break;
            if (!IsPinned(entry)) { Remove(node.Value); Evictions++; items++; }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= sliceMilliseconds) break;
            node = next;
        }
        if (items > 0) { PressureTrims++; _account.RecordTrim(before - ResidentBytes, items); }
        _account.Report(ResidentBytes);
        return before - ResidentBytes;
    }

    private void Touch(string key, Entry entry)
    {
        entry.LastTick = Environment.TickCount64;
        if (entry.Recency is { } node)
        {
            _recency.Remove(node);
            _recency.AddLast(node);
        }
        else entry.Recency = _recency.AddLast(key);
    }

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

    /// <summary>
    /// While true, an evicted or removed entry is set aside instead of disposed, until <see cref="DisposeRetired"/>:
    /// the caller is not the thread drawing the cache's images, and that thread may hold the evicted one right now.
    /// </summary>
    public bool DeferDisposal { get; set; }

    /// <summary>Entries out of the cache whose images are not yet disposed (pinned or deferred).</summary>
    public int RetiredCount => _retired.Count;

    /// <summary>A stable separate raster identity for provisional draws; shares this entry's immutable pixels.</summary>
    public SKImage? GetFallbackImage(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        Touch(key, entry);
        if (entry.FallbackImage is not null) return entry.FallbackImage;
        using var pixels = entry.Image.PeekPixels();
        if (pixels is null) return entry.Image;
        entry.FallbackImage = SKImage.FromPixels(pixels);
        _imageOwners[entry.FallbackImage] = entry;
        return entry.FallbackImage;
    }

    /// <summary>Conservative weight of retaining this image's whole entry, including future mips/fallback.
    /// Zero for an image owned by another cache. Constant-time; caller supplies synchronization.</summary>
    public long RetentionBytes(SKImage image) => _imageOwners.TryGetValue(image, out var entry)
        ? entry.ResidentBytes + entry.DecodedBytes * 2 : 0;

    /// <summary>Keeps <paramref name="image"/> (an image or mip this cache returned) alive and in the cache until unpinned.</summary>
    public void Pin(SKImage image) => _pins[image] = _pins.GetValueOrDefault(image) + 1;

    public void Unpin(SKImage image)
    {
        if (!_pins.TryGetValue(image, out var count)) return;
        if (count <= 1) _pins.Remove(image); else _pins[image] = count - 1;
    }

    /// <summary>Disposes the set-aside entries no one pins any more.</summary>
    public void DisposeRetired()
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (IsPinned(_retired[i])) continue;
            DisposeEntry(_retired[i]);
            _retired.RemoveAt(i);
        }
    }

    private bool IsPinned(Entry entry)
    {
        if (_pins.Count == 0) return false;
        if (_pins.ContainsKey(entry.Image)) return true;
        if (entry.FallbackImage is { } fallback && _pins.ContainsKey(fallback)) return true;
        foreach (var mip in entry.Mips.Values) if (_pins.ContainsKey(mip)) return true;
        return false;
    }

    private void Release(Entry entry)
    {
        if (DeferDisposal || IsPinned(entry)) _retired.Add(entry);
        else DisposeEntry(entry);
    }

    private void DisposeEntry(Entry entry)
    {
        _imageOwners.Remove(entry.Image);
        if (entry.FallbackImage is { } fallback) _imageOwners.Remove(fallback);
        foreach (var mip in entry.Mips.Values) _imageOwners.Remove(mip);
        entry.Dispose();
    }

    public SKImage? Get(string key, string? path)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            Touch(key, existing);
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
            Touch(key, existing);
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
            Touch(key, existing);
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
        };
        Touch(key, _entries[key]);
        _imageOwners[image] = _entries[key];
        DecodedBytes += metadata.DecodedBytes;
        ResidentBytes += incomingResidentBytes;
        return image;
    }

    public bool Contains(string key) => _entries.ContainsKey(key);

    /// <summary>Explicit diagnostic snapshot of primary images. Caller supplies synchronization;
    /// unlike Get, this does not change hit counts, recency, decoding or ownership.</summary>
    public IReadOnlyList<(string Key, int Width, int Height)> ReadResidentSizes()
        => _entries.Select(pair => (pair.Key, pair.Value.Image.Width, pair.Value.Image.Height)).ToArray();

    /// <summary>Takes ownership of already verified, derived raster pixels. They have their own
    /// LRU lifetime, so evicting a large decoded source need not discard a small sampled image.</summary>
    public SKImage? AdoptPixels(string key, SKImage image)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            image.Dispose();
            Touch(key, existing);
            Hits++;
            return existing.Image;
        }
        var bytes = checked((long)image.Width * image.Height * 4);
        if (bytes > _decodedByteCapacity) { image.Dispose(); return null; }
        TrimForIncoming(bytes);
        _entries[key] = new Entry { Image = image, DecodedBytes = bytes, ResidentBytes = bytes };
        Touch(key, _entries[key]);
        _imageOwners[image] = _entries[key];
        DecodedBytes += bytes;
        ResidentBytes += bytes;
        Misses++;
        return image;
    }

    /// <summary>
    /// Takes an image decoded elsewhere (off the render thread, by <see cref="DecodeOwned"/>) from
    /// these exact bytes. Admission and eviction are those of <see cref="GetEncoded"/>; an image
    /// the budget cannot hold, or a key that arrived meanwhile, is disposed and null returned.
    /// </summary>
    public SKImage? Adopt(string key, byte[] encoded, SKImage image,
        string? expectedMediaType = null, string? expectedExtension = null)
    {
        if (_entries.ContainsKey(key)) { image.Dispose(); return null; }
        AssetImageMetadata metadata;
        try
        {
            metadata = expectedMediaType is null || expectedExtension is null
                ? AssetImagePolicy.Inspect(encoded)
                : AssetImagePolicy.Inspect(encoded, expectedMediaType, expectedExtension);
        }
        catch (InvalidDataException) { image.Dispose(); return null; }
        var incomingResidentBytes = checked(metadata.DecodedBytes + metadata.EncodedBytes);
        if (incomingResidentBytes > _decodedByteCapacity) { image.Dispose(); return null; }
        TrimForIncoming(incomingResidentBytes);
        Misses++;
        _entries[key] = new Entry
        {
            Image = image,
            DecodedBytes = metadata.DecodedBytes,
            ResidentBytes = incomingResidentBytes,
        };
        Touch(key, _entries[key]);
        _imageOwners[image] = _entries[key];
        DecodedBytes += metadata.DecodedBytes;
        ResidentBytes += incomingResidentBytes;
        return image;
    }

    /// <summary>The decode this cache uses, for a caller that decodes on another thread.</summary>
    public static SKImage? DecodeOwned(byte[] encoded) => Decode(encoded);

    public void Remove(string key)
    {
        if (_entries.Remove(key, out var entry))
        {
            if (entry.Recency is { } node) _recency.Remove(node);
            entry.Recency = null;
            DecodedBytes -= entry.DecodedBytes;
            ResidentBytes -= entry.ResidentBytes + entry.MipBytes;
            Release(entry);
        }
    }

    /// <summary>
    /// Make room before native decode so the configured aggregate is also a peak gate. Pinned entries stay; when only
    /// they are left the cache holds more than its budget until they are released.
    /// </summary>
    private void TrimForIncoming(long incomingResidentBytes)
    {
        while (_entries.Count >= _capacity
            || ResidentBytes > ByteLimit - incomingResidentBytes)
        {
            if (OldestUnpinned(null) is not { } oldest) return;
            Remove(oldest);
            Evictions++;
        }
    }

    private string? OldestUnpinned(string? except)
    {
        for (var node = _recency.First; node is not null; node = node.Next)
        {
            var candidate = node.Value;
            if (except is not null && string.Equals(candidate, except, StringComparison.OrdinalIgnoreCase)
                || IsPinned(_entries[candidate])) continue;
            return candidate;
        }
        return null;
    }

    /// <summary>Only an already resident level; never generates pixels or reads a file.</summary>
    public SKImage? GetReadyForPixelSize(string key, double pixelWidth, double pixelHeight)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        Touch(key, entry);
        var ratio = Math.Min(entry.Image.Width / Math.Max(1, pixelWidth), entry.Image.Height / Math.Max(1, pixelHeight));
        var level = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, ratio))), 0, 15);
        return level == 0 ? entry.Image : entry.Mips.GetValueOrDefault(level);
    }

    /// <summary>Power-of-two minification of an already verified source, charged to the same LRU
    /// byte budget. Close zoom always receives the original. No source pixels are changed.</summary>
    public SKImage? GetForPixelSize(string key, double pixelWidth, double pixelHeight)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        Touch(key, entry);
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
        while (ResidentBytes + bytes * 2 > ByteLimit)
        {
            if (OldestUnpinned(key) is not { } oldest) return image;
            Remove(oldest); Evictions++;
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
            _imageOwners[source] = entry;
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
            DisposeEntry(entry);
        }

        _entries.Clear();
        _recency.Clear();
        foreach (var entry in _retired) DisposeEntry(entry);
        _retired.Clear();
        _pins.Clear();
        DecodedBytes = 0;
        ResidentBytes = 0;
        _account?.Dispose();
    }
}
