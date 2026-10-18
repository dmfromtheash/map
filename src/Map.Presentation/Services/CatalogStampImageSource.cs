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

    public void Dispose() => _cache.Dispose();
}
