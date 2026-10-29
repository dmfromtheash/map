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
    // The export draws every copy of an asset across the whole map, tile after tile: when the
    // decoded originals do not fit, each tile decodes them again (a 131072² map of the owner's
    // 4096 px mountains spent more than half its time in SKCodec.GetPixels with the editor's
    // 256 MiB budget). The export owns this cache for one operation, so it may take a quarter of
    // the free memory, between the editor's budget and 3 GiB.
    private readonly AssetImageCache _cache = new(256, ExportDecodedBudget());

    internal static long ExportDecodedBudget()
    {
        var available = new Map.Export.ExportEngineSettings().AvailableMemoryBytes();
        return Math.Clamp(available / 4, AssetImagePolicy.DefaultCacheDecodedBytes, 3L * 1024 * 1024 * 1024);
    }
    private readonly HashSet<string> _missingSteps = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// The working image of an asset, as in the editor: for an original with a detail ladder its
    /// first halved step (shadows, foreground masks and presence checks read it); the drawn
    /// picture itself comes from <see cref="TryGetImageForPixelSize"/> at the output's size.
    /// </summary>
    public SKImage? TryGetImage(string sha256) =>
        (_catalogRoot is not null && _catalogRecords.TryGetValue(sha256, out var record)
            && AssetCatalog.DetailLadderDepth(record.Width, record.Height) > 0 ? LadderStep(record, 1) : null)
        ?? TryGetOriginal(sha256);

    private SKImage? TryGetOriginal(string sha256)
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

    /// <summary>
    /// A large catalog original is read through its detail ladder, as the editor reads it
    /// (CatalogStampImageSource): the halved step just above the drawn size, the master only when
    /// the output needs more pixels than the first step holds. A save's 320×180 preview of a map
    /// with 1 500 of the owner's 4096 px mountains decoded a master for every object (the six
    /// masters do not fit the cache together): over two minutes of CPU behind every save and autosave.
    /// </summary>
    public SKImage? TryGetImageForPixelSize(string sha256, double width, double height)
    {
        if (_catalogRoot is not null && _catalogRecords.TryGetValue(sha256, out var record)
            && AssetCatalog.DetailLadderDepth(record.Width, record.Height) is > 0 and var depth)
        {
            var ratio = Math.Min(record.Width / Math.Max(1, width), record.Height / Math.Max(1, height));
            var level = Math.Min(depth, (int)Math.Floor(Math.Log2(Math.Max(1, ratio))));
            if (level == 0) return TryGetOriginal(sha256) ?? LadderStep(record, 1);
            if (LadderStep(record, level) is { } step)
                return _cache.GetForPixelSize(sha256 + "@" + level, width, height) ?? step;
        }

        if (_cache.GetForPixelSize(sha256, width, height) is { } scaled) return scaled;
        // The original was evicted or never read: read it, then take the same step a warm cache
        // gives. The drawn image never depends on what was evicted before, so an export's tiles
        // look the same in any order (parallel export, owner 2026-10-29: "the pixels must not change").
        var original = TryGetOriginal(sha256);
        return original is null ? null : _cache.GetForPixelSize(sha256, width, height) ?? original;
    }

    private SKImage? LadderStep(AssetRecord record, int level)
    {
        var key = record.Sha256 + "@" + level;
        if (_cache.Get(key, null) is { } held) return held;
        if (_missingSteps.Contains(key)) return null;
        if (AssetCatalog.TryDetailLevelPath(_catalogRoot!, record, level, out var path) && _cache.Get(key, path) is { } image)
            return image;
        _missingSteps.Add(key);
        return null;
    }

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
