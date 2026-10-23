using Map.Assets;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>Resolves project assets lazily and makes missing/hash-mismatched links visible.</summary>
public sealed class CatalogStampImageSource(
    AssetCatalog catalog, Func<MapDocument> document, Func<string?> projectPath) : IStampImageSource, IDisposable
{
    // Large owner art keeps its master plus a ladder of halved steps; the budget holds the
    // working steps of a whole set and the masters of the few objects a close view shows.
    private readonly AssetImageCache _cache = new(64, 512L * 1024 * 1024);
    private readonly Dictionary<string, (string Path, long Length, DateTime LastWriteUtc)> _resolved =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingSteps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The working image of an asset. For an original with a detail ladder this is its first
    /// halved step: the same picture and proportions, at a size that keeps every object of a set
    /// resident. The full master is decoded only by <see cref="TryGetImageForPixelSize"/> when a
    /// close view needs more pixels than that step holds.
    /// </summary>
    public SKImage? TryGetImage(string sha256) =>
        TryGetLadderStep(sha256, 1) ?? TryGetOriginal(sha256);

    private SKImage? TryGetOriginal(string sha256)
    {
        var reference = document().Assets.FirstOrDefault(asset => asset.Sha256 == sha256);
        if (reference?.EmbeddedContent is { } embedded)
        {
            return _cache.GetEncoded(
                sha256, embedded, reference.MediaType, reference.Extension);
        }

        var record = catalog.Find(sha256);
        if (record is not null)
        {
            var resolution = ResolveCatalog(record);
            return resolution is null ? null : _cache.GetVerified(
                sha256, resolution, sha256, record.MediaType, record.Extension);
        }

        if (reference is { Storage: ProjectAssetStorage.Linked, RelativePath: { } relative }
            && projectPath() is { } mapPath)
        {
            if (ProjectLinkedAssetPath.TryResolve(mapPath, relative, out var candidate)
                && ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(mapPath, candidate))
            {
                return _cache.GetVerified(
                    sha256, candidate, sha256, reference.MediaType, reference.Extension);
            }
        }

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
        if (catalog.Find(sha256) is { } record && AssetCatalog.DetailLadderDepth(record.Width, record.Height) is > 0 and var depth)
        {
            var ratio = Math.Min(record.Width / Math.Max(1, width), record.Height / Math.Max(1, height));
            var level = Math.Min(depth, (int)Math.Floor(Math.Log2(Math.Max(1, ratio))));
            if (level == 0) return TryGetOriginal(sha256) ?? TryGetLadderStep(sha256, 1);
            if (TryGetLadderStep(sha256, level) is { } step)
                return _cache.GetForPixelSize(sha256 + "@" + level, width, height) ?? step;
        }

        return _cache.GetForPixelSize(sha256, width, height) ?? TryGetOriginal(sha256);
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
        if (catalog.TryDetailLevelPath(record, level, out var path) && _cache.Get(key, path) is { } image)
            return image;
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

    public void Dispose()
    {
        while (_decoded.TryDequeue(out var item)) item.Image?.Dispose();
        _cache.Dispose();
    }
}
