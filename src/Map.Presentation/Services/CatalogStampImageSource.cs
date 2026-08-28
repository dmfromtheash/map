using Map.Assets;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>Resolves project assets lazily and makes missing/hash-mismatched links visible.</summary>
public sealed class CatalogStampImageSource(
    AssetCatalog catalog, Func<MapDocument> document, Func<string?> projectPath) : IStampImageSource, IDisposable
{
    private readonly AssetImageCache _cache = new(64);
    private readonly Dictionary<string, (string Path, long Length, DateTime LastWriteUtc)> _resolved =
        new(StringComparer.OrdinalIgnoreCase);

    public SKImage? TryGetImage(string sha256)
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
