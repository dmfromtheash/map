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
        public long LastFrame { get; set; } = long.MinValue / 2;
        public void Dispose()
        {
            FallbackImage?.Dispose();
            foreach (var image in Mips.Values) image.Dispose();
            Image.Dispose();
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The owner's frame number (set once per canvas frame). Entries record the frame they were last used in.</summary>
    public long Frame { get; set; }

    /// <summary>
    /// When above zero, <see cref="TryAdoptPixels"/> never evicts an entry used within this many frames of
    /// <see cref="Frame"/>: those are the images the current view still draws or waits to raster with. Like Chromium's
    /// decode cache, which evicts only entries no one references, an adoption that would need such an eviction is
    /// refused instead, so a needed set larger than the shelf no longer evicts itself in a circle. Zero (the default)
    /// keeps plain LRU.
    /// </summary>
    public int ProtectedFrames { get; set; }

    /// <summary>Adoptions refused because only recently used (protected) entries could have made room.</summary>
    public long AdmissionRefused { get; private set; }

    private bool IsProtected(Entry entry) => ProtectedFrames > 0 && Frame - entry.LastFrame < ProtectedFrames;

    private void Touch(string key, Entry entry)
    {
        entry.LastTick = Environment.TickCount64;
        entry.LastFrame = Frame;
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
        return pixels is null ? entry.Image : entry.FallbackImage = SKImage.FromPixels(pixels);
    }

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
            _retired[i].Dispose();
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
        else entry.Dispose();
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
        => TryAdoptPixels(key, image, out var adopted) == AdoptResult.Refused ? null : adopted;

    public enum AdoptResult { Adopted, AlreadyResident, TooLarge, Refused }

    /// <summary>
    /// <see cref="AdoptPixels"/> that tells why nothing was adopted. <see cref="AdoptResult.Refused"/> means the shelf is
    /// full of protected entries (<see cref="ProtectedFrames"/>): the image is disposed, nothing was evicted for it, and
    /// the caller may ask again once the view has finished with some of them. With
    /// <paramref name="retainOnRefusal"/> the caller instead retains ownership of the refused image
    /// (the out value is still null), for a temporary exact raster that must finish without canvas frames.
    /// </summary>
    public AdoptResult TryAdoptPixels(string key, SKImage image, out SKImage? adopted, bool retainOnRefusal = false)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            image.Dispose();
            Touch(key, existing);
            Hits++;
            adopted = existing.Image;
            return AdoptResult.AlreadyResident;
        }
        var bytes = checked((long)image.Width * image.Height * 4);
        if (bytes > _decodedByteCapacity) { image.Dispose(); adopted = null; return AdoptResult.TooLarge; }
        if (!TrimForIncoming(bytes, ProtectedFrames > 0))
        {
            if (!retainOnRefusal) image.Dispose();
            AdmissionRefused++;
            adopted = null;
            return AdoptResult.Refused;
        }
        _entries[key] = new Entry { Image = image, DecodedBytes = bytes, ResidentBytes = bytes };
        Touch(key, _entries[key]);
        DecodedBytes += bytes;
        ResidentBytes += bytes;
        Misses++;
        adopted = image;
        return AdoptResult.Adopted;
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
    private void TrimForIncoming(long incomingResidentBytes) => TrimForIncoming(incomingResidentBytes, false);

    /// <summary>
    /// Evicts least recently used entries until the incoming bytes fit. With <paramref name="protect"/> recently used
    /// entries are not candidates; returns false when room could not be made (over-budget with only pinned entries
    /// left is still allowed without <paramref name="protect"/>, as before).
    /// </summary>
    private bool TrimForIncoming(long incomingResidentBytes, bool protect)
    {
        // The shared budget can rebalance on another thread without the owner's cache gate.
        // Preflight and eviction must use the same limit or a refusal could follow partial eviction.
        var byteLimit = ByteLimit;
        // Refusal must be atomic: do not evict an old prefix only to discover that the remaining
        // protected/pinned entries still prevent admission. Scan just the eviction candidates first.
        if (protect)
        {
            var count = _entries.Count;
            var resident = ResidentBytes;
            for (var node = _recency.First;
                (count >= _capacity || resident > byteLimit - incomingResidentBytes) && node is not null;
                node = node.Next)
            {
                var entry = _entries[node.Value];
                if (IsProtected(entry)) break;
                if (IsPinned(entry)) continue;
                count--;
                resident -= entry.ResidentBytes + entry.MipBytes;
            }
            if (count >= _capacity || resident > byteLimit - incomingResidentBytes) return false;
        }
        while (_entries.Count >= _capacity
            || ResidentBytes > byteLimit - incomingResidentBytes)
        {
            if (OldestUnpinned(null, protect) is not { } oldest) return !protect;
            Remove(oldest);
            Evictions++;
        }
        return true;
    }

    private string? OldestUnpinned(string? except, bool protect = false)
    {
        for (var node = _recency.First; node is not null; node = node.Next)
        {
            var candidate = node.Value;
            var entry = _entries[candidate];
            // Recency order is use order: the first protected entry means every later one is protected too.
            if (protect && IsProtected(entry)) return null;
            if (except is not null && string.Equals(candidate, except, StringComparison.OrdinalIgnoreCase)
                || IsPinned(entry)) continue;
            return candidate;
        }
        return null;
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
        _recency.Clear();
        foreach (var entry in _retired) entry.Dispose();
        _retired.Clear();
        _pins.Clear();
        DecodedBytes = 0;
        ResidentBytes = 0;
        _account?.Dispose();
    }
}
