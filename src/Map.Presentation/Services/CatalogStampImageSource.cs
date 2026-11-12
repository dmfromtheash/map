using Map.Assets;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>
/// Resolves project assets lazily and makes missing/hash-mismatched links visible. The canvas draws from it on the
/// render thread; style preparation, the pictures of «Вид» and the panels take images through a <see cref="Lease"/>.
/// </summary>
public sealed class CatalogStampImageSource(
    AssetCatalog catalog, Func<MapDocument> document, Func<string?> projectPath) : IStampImageSource, IDisposable
{
    // Large owner art keeps its master plus a ladder of halved steps; the budget holds the
    // working steps of a whole set and the masters of the few objects a close view shows.
    private readonly AssetImageCache _cache = new(64, 512L * 1024 * 1024);
    private readonly Dictionary<string, (string Path, long Length, DateTime LastWriteUtc)> _resolved =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingSteps = new(StringComparer.OrdinalIgnoreCase);

    // One lock guards all the state of this source. The audit of 2026-11-01 found style preparation reading this
    // cache on the thread pool while the canvas drew from it: no lock, and an eviction disposed an image that another
    // thread was still drawing (more than 64 different pictures were enough).
    private readonly object _gate = new();
    // The thread inside a canvas frame (0 between frames): what other threads evict meanwhile is freed when it ends.
    private int _frameThread;
    private int _leases;
    private bool _disposed;
    // While a lease plans its fetch, the first miss that needs a file read and decode is noted here instead of done:
    // the lease decodes outside the lock, so the canvas never waits for a background decode.
    private bool _planning;
    private Fetch? _plan;
    private HashSet<string>? _planFailed;
    private sealed record Fetch(string Key, string? Path, byte[]? Embedded, string? Sha256, string? MediaType, string? Extension);

    /// <summary>
    /// The working image of an asset. For an original with a detail ladder this is its first
    /// halved step: the same picture and proportions, at a size that keeps every object of a set
    /// resident. The full master is decoded only by <see cref="TryGetImageForPixelSize"/> when a
    /// close view needs more pixels than that step holds.
    /// </summary>
    /// <summary>
    /// A drive library was attached or removed: pictures that were missing while the drive was
    /// away are looked for again, and paths resolved on a previous drive are not reused.
    /// </summary>
    public void ForgetMissing()
    {
        lock (_gate)
        {
            _missingSteps.Clear();
            _decodeFailed.Clear();
            _resolved.Clear();
        }
    }

    public SKImage? TryGetImage(string sha256)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            Enter();
            return WorkingImage(sha256);
        }
    }

    private SKImage? WorkingImage(string sha256) =>
        TryGetLadderStep(sha256, 1) ?? (_plan is null ? TryGetOriginal(sha256) : null);

    /// <summary>Called under the lock by every entry: only a frame's own thread may free what it evicts at once.</summary>
    private void Enter() =>
        _cache.DeferDisposal = _frameThread != 0 && _frameThread != Environment.CurrentManagedThreadId;

    private SKImage? TryGetOriginal(string sha256)
    {
        var reference = document().Assets.FirstOrDefault(asset => asset.Sha256 == sha256);
        if (reference?.EmbeddedContent is { } embedded)
        {
            return Load(sha256, null, embedded, null, reference.MediaType, reference.Extension);
        }

        var record = catalog.Find(sha256);
        if (record is not null)
        {
            var resolution = ResolveCatalog(record);
            return resolution is null ? null : Load(
                sha256, resolution, null, sha256, record.MediaType, record.Extension);
        }

        if (reference is { Storage: ProjectAssetStorage.Linked, RelativePath: { } relative }
            && projectPath() is { } mapPath)
        {
            if (ProjectLinkedAssetPath.TryResolve(mapPath, relative, out var candidate)
                && ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(mapPath, candidate))
            {
                return Load(sha256, candidate, null, sha256, reference.MediaType, reference.Extension);
            }
        }

        return null;
    }

    /// <summary>
    /// A cached image, or — on the render thread — the image read and decoded now. While a lease plans, the miss is
    /// noted as its fetch and null returned.
    /// </summary>
    private SKImage? Load(string key, string? path, byte[]? embedded, string? sha256, string? mediaType, string? extension)
    {
        if (!_planning)
            return embedded is not null ? _cache.GetEncoded(key, embedded, mediaType, extension)
                : sha256 is not null ? _cache.GetVerified(key, path, sha256, mediaType, extension)
                : _cache.Get(key, path);
        if (_cache.Get(key, null) is { } held) return held;
        if (_plan is null && _planFailed?.Contains(key) != true) _plan = new(key, path, embedded, sha256, mediaType, extension);
        return null;
    }

    /// <summary>
    /// A large original is read through its detail ladder: a far view decodes only the halved
    /// step just above the size it draws, and the full master only when the view is close enough
    /// to need more pixels than the first step holds. Below the deepest step the cache's own mip
    /// pyramid of that step takes over. Assets without a ladder keep the original path.
    /// </summary>
    public SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            Enter();
            return ForPixelSize(sha256, width, height);
        }
    }

    private SKImage? ForPixelSize(string sha256, double width, double height)
    {
        if (catalog.Find(sha256) is { } record && AssetCatalog.DetailLadderDepth(record.Width, record.Height) is > 0 and var depth)
        {
            var ratio = Math.Min(record.Width / Math.Max(1, width), record.Height / Math.Max(1, height));
            var level = Math.Min(depth, (int)Math.Floor(Math.Log2(Math.Max(1, ratio))));
            if (level == 0) return TryGetOriginal(sha256) ?? (_plan is null ? TryGetLadderStep(sha256, 1) : null);
            if (TryGetLadderStep(sha256, level) is { } step)
                return _cache.GetForPixelSize(sha256 + "@" + level, width, height) ?? step;
            if (_plan is not null) return null;
        }

        return _cache.GetForPixelSize(sha256, width, height) ?? TryGetOriginal(sha256);
    }

    /// <summary>
    /// A lease for work off the render thread or on the UI thread. It takes the lock only to look up and adopt; its
    /// decodes run on its own thread. What it hands out is pinned in the shared cache until it is disposed — the
    /// prepared form maps of a style belong to these very images, so preparation must use them, not copies.
    /// </summary>
    public IStampImageLease Lease()
    {
        lock (_gate)
        {
            _leases++;
            return new ImageLease(this);
        }
    }

    /// <summary>The canvas frame on the render thread: evictions by other threads wait until it ends.</summary>
    public IDisposable? BeginFrame()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            _frameThread = Environment.CurrentManagedThreadId;
            return new FrameScope(this);
        }
    }

    private void EndFrame()
    {
        lock (_gate)
        {
            _frameThread = 0;
            if (_disposed) { DisposeIfUnused(); return; }
            _cache.DeferDisposal = false;
            _cache.DisposeRetired();
        }
    }

    private SKImage? LeaseGet(ImageLease lease, Func<SKImage?> resolve)
    {
        // A few rounds at most: the working step, then the original if the step is missing, then its mip.
        for (var round = 0; round < 6; round++)
        {
            Fetch? fetch;
            lock (_gate)
            {
                if (_disposed || lease.Closed) return null;
                Enter();
                _planning = true; _plan = null; _planFailed = lease.Failed;
                SKImage? image;
                try { image = resolve(); }
                finally { fetch = _plan; _planning = false; _plan = null; _planFailed = null; }
                if (image is not null)
                {
                    _cache.Pin(image);
                    lease.Held.Add(image);
                    return image;
                }
                if (fetch is null) return null;
            }
            var (encoded, decoded) = Decode(fetch);
            lock (_gate)
            {
                if (_disposed || lease.Closed) { decoded?.Dispose(); return null; }
                Enter();
                if (decoded is null || encoded is null
                    || _cache.Adopt(fetch.Key, encoded, decoded, fetch.MediaType, fetch.Extension) is null && !_cache.Contains(fetch.Key))
                    lease.Failed.Add(fetch.Key);
            }
        }
        return null;
    }

    /// <summary>The same checks as the cache's own read: size, content hash, format, then the decode.</summary>
    private static (byte[]? Encoded, SKImage? Image) Decode(Fetch fetch)
    {
        try
        {
            var encoded = fetch.Embedded;
            if (encoded is null)
            {
                if (string.IsNullOrWhiteSpace(fetch.Path)) return (null, null);
                var file = new FileInfo(fetch.Path);
                if (!file.Exists || file.Length is <= 0 or > AssetImagePolicy.MaxEncodedBytes) return (null, null);
                encoded = AssetImagePolicy.ReadEncodedFile(file.FullName);
            }
            if (fetch.Sha256 is not null && !string.Equals(Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(encoded)), fetch.Sha256, StringComparison.OrdinalIgnoreCase))
                return (null, null);
            _ = fetch.MediaType is null || fetch.Extension is null
                ? AssetImagePolicy.Inspect(encoded)
                : AssetImagePolicy.Inspect(encoded, fetch.MediaType, fetch.Extension);
            return (encoded, AssetImageCache.DecodeOwned(encoded));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, null);
        }
    }

    private void EndLease(ImageLease lease)
    {
        lock (_gate)
        {
            if (lease.Closed) return;
            lease.Closed = true;
            _leases--;
            if (_disposed) { DisposeIfUnused(); return; }
            foreach (var image in lease.Held) _cache.Unpin(image);
            lease.Held.Clear();
            Enter();
            if (!_cache.DeferDisposal) _cache.DisposeRetired();
        }
    }

    private sealed class ImageLease(CatalogStampImageSource source) : IStampImageLease
    {
        internal readonly List<SKImage> Held = [];
        internal readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
        internal bool Closed;
        public SKImage? TryGetImage(string sha256) => source.LeaseGet(this, () => source.WorkingImage(sha256));
        public SKImage? TryGetImageForPixelSize(string sha256, double width, double height) =>
            source.LeaseGet(this, () => source.ForPixelSize(sha256, width, height));
        // A lease draws what it asks for on its own thread, so every image is ready when asked.
        public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready) => true;
        public IStampImageLease Lease() => source.Lease();
        public void Dispose() => source.EndLease(this);
    }

    private sealed class FrameScope(CatalogStampImageSource source) : IDisposable
    {
        private bool _ended;
        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            source.EndFrame();
        }
    }

    // Images decoded off the render thread wait here until the render thread takes them in:
    // the cache itself is used by one thread only.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Decoded> _decoded = new();
    private readonly HashSet<string> _decoding = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _decodeFailed = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Decoded(string Key, byte[]? Encoded, SKImage? Image, string? MediaType, string? Extension);

    /// <summary>
    /// The first frame of a closer zoom used to decode the larger ladder steps of every mountain
    /// in view on the render thread (~100 ms for six 4096 px originals). Each missing step or
    /// original is now read, verified and decoded on the thread pool, and the tile that needs it
    /// keeps showing the neighbouring zoom level until it arrives (R05, 2026-10-23).
    /// </summary>
    public bool PrepareForPixelSizes(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
    {
        lock (_gate)
        {
            if (_disposed) return true;
            Enter();
            return PrepareLocked(stamps, ready);
        }
    }

    private bool PrepareLocked(IEnumerable<(string Sha256, double Width, double Height)> stamps, Action ready)
    {
        AdoptDecoded();
        var resident = true;
        var seen = new HashSet<(string, int)>();
        AssetRecord? record = null;
        foreach (var (sha256, width, height) in stamps)
        {
            if (record?.Sha256 != sha256) record = catalog.Find(sha256);
            if (record is null || AssetCatalog.DetailLadderDepth(record.Width, record.Height) is not (> 0 and var depth)) continue;
            var ratio = Math.Min(record.Width / Math.Max(1, width), record.Height / Math.Max(1, height));
            var level = Math.Min(depth, (int)Math.Floor(Math.Log2(Math.Max(1, ratio))));
            if (!seen.Add((sha256, level))) continue;
            var key = level == 0 ? sha256 : sha256 + "@" + level;
            if (_cache.Contains(key) || _decodeFailed.Contains(key) || _missingSteps.Contains(key)) continue;
            resident = false;
            if (_decoding.Contains(key)) continue;
            if (level == 0)
            {
                // The original is hashed against its content id, as the synchronous path does.
                if (document().Assets.Any(asset => asset.Sha256 == sha256 && asset.EmbeddedContent is not null)
                    || ResolveCatalog(record) is not { } original) { resident = true; continue; }
                StartDecode(key, original, sha256, record.MediaType, record.Extension, ready);
            }
            else if (catalog.TryDetailLevelPath(record, level, out var step)) StartDecode(key, step, null, null, null, ready);
            else { _missingSteps.Add(key); resident = true; }
        }
        return resident;
    }

    private void StartDecode(string key, string path, string? sha256, string? mediaType, string? extension, Action ready)
    {
        _decoding.Add(key);
        _ = Task.Run(() =>
        {
            byte[]? encoded = null; SKImage? image = null;
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length is > 0 and <= AssetImagePolicy.MaxEncodedBytes)
                {
                    encoded = AssetImagePolicy.ReadEncodedFile(file.FullName);
                    if (sha256 is null || string.Equals(Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(encoded)), sha256, StringComparison.OrdinalIgnoreCase))
                        image = AssetImageCache.DecodeOwned(encoded);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            _decoded.Enqueue(new(key, encoded, image, mediaType, extension));
            ready();
        });
    }

    private void AdoptDecoded()
    {
        while (_decoded.TryDequeue(out var item))
        {
            _decoding.Remove(item.Key);
            if (item.Image is null || item.Encoded is null
                || _cache.Adopt(item.Key, item.Encoded, item.Image, item.MediaType, item.Extension) is null && !_cache.Contains(item.Key))
                _decodeFailed.Add(item.Key);
        }
    }

    private SKImage? TryGetLadderStep(string sha256, int level)
    {
        var key = sha256 + "@" + level;
        if (_cache.Get(key, null) is { } held) return held;
        if (_missingSteps.Contains(key) || catalog.Find(sha256) is not { } record) return null;
        if (catalog.TryDetailLevelPath(record, level, out var path) && Load(key, path, null, null, null, null) is { } image)
            return image;
        // A lease that is about to read the step has not found it missing.
        if (_plan is not null) return null;
        _missingSteps.Add(key);
        return null;
    }

    private string? ResolveCatalog(AssetRecord record)
    {
        if (_resolved.TryGetValue(record.Sha256, out var cached) && File.Exists(cached.Path))
        {
            var info = new FileInfo(cached.Path);
            if (info.Length == cached.Length && info.LastWriteTimeUtc == cached.LastWriteUtc)
            {
                return cached.Path;
            }

            _cache.Remove(record.Sha256);
            _resolved.Remove(record.Sha256);
        }

        AssetResolution resolution;
        try
        {
            resolution = catalog.Resolve(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
        if (resolution.Status != AssetResolutionStatus.Available || resolution.Path is null)
        {
            return null;
        }

        var resolvedInfo = new FileInfo(resolution.Path);
        _resolved[record.Sha256] =
            (resolution.Path, resolvedInfo.Length, resolvedInfo.LastWriteTimeUtc);
        return resolution.Path;
    }

    /// <summary>The editor closed: images still held by a lease or a frame in progress are freed when those end.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            DisposeIfUnused();
        }
    }

    private void DisposeIfUnused()
    {
        if (_leases > 0 || _frameThread != 0) return;
        while (_decoded.TryDequeue(out var item)) item.Image?.Dispose();
        _cache.Dispose();
    }
}
