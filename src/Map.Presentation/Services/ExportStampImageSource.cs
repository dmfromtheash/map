using Map.Assets;
using Map.Core.Documents;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>
/// Operation-owned export resolver. It freezes catalog metadata and uses its own bounded decoded
/// cache, so a worker never touches the editor's disposable image cache or mutable catalog index.
/// </summary>
public sealed class ExportStampImageSource : IStampImageSource, IDisposable
{
    private readonly MapDocument _snapshot;
    private readonly string? _projectPath;
    private readonly string? _catalogRoot;
    private readonly IReadOnlyDictionary<string, AssetRecord> _catalogRecords;
    private readonly AssetImageCache _cache = new(64);

    public ExportStampImageSource(MapDocument snapshot, string? projectPath, AssetCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _projectPath = projectPath;
        _catalogRoot = catalog?.Root;
        _catalogRecords = catalog?.Records
            .GroupBy(record => record.Sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
    }

    public SKImage? TryGetImage(string sha256)
    {
        var reference = _snapshot.Assets.FirstOrDefault(
            asset => string.Equals(asset.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (reference?.EmbeddedContent is { } embedded)
        {
            return _cache.GetEncoded(sha256, embedded, reference.MediaType, reference.Extension);
        }

        if (_catalogRoot is not null && _catalogRecords.TryGetValue(sha256, out var record))
        {
            string? path = null;
            if (record.StoredRelativePath is { } stored)
            {
                TryResolveCatalogOriginal(_catalogRoot, stored, out path);
            }
            else
            {
                path = record.LinkedPath;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    return _cache.GetVerified(
                        sha256, path, record.Sha256, record.MediaType, record.Extension);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidDataException)
                {
                    return null;
                }
            }
        }

        if (reference is { Storage: ProjectAssetStorage.Linked, RelativePath: { } relative }
            && _projectPath is { } mapPath
            && ProjectLinkedAssetPath.TryResolve(mapPath, relative, out var candidate)
            && ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(mapPath, candidate))
        {
            try
            {
                return _cache.GetVerified(
                    sha256, candidate, sha256, reference.MediaType, reference.Extension);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }

    public SKImage? TryGetImageForPixelSize(string sha256, double width, double height) =>
        _cache.GetForPixelSize(sha256, width, height) ?? TryGetImage(sha256);

    public void Dispose() => _cache.Dispose();

    private static bool TryResolveCatalogOriginal(string root, string relative, out string? path)
    {
        path = null;
        if (Path.IsPathRooted(relative) || relative.Contains('\0')) return false;
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        var originals = Path.GetFullPath(Path.Combine(root, "originals"));
        var candidate = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = originals.EndsWith(Path.DirectorySeparatorChar)
            ? originals
            : originals + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        path = candidate;
        return true;
    }
}
