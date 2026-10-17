using System.Security.Cryptography;
using Map.Assets;
using Map.Core.Documents;

namespace Map.Presentation.Services;

/// <summary>Copies available linked image bytes before moving a document to another directory.
/// Missing images keep their identity and honest missing-image preview; the source is immutable.</summary>
public static class IndependentProjectImages
{
    public static MapDocument Prepare(MapDocument document, string? sourcePath, AssetCatalog? catalog = null)
    {
        var changed = false;
        var assets = document.Assets.Select(reference =>
        {
            if (reference.Storage != ProjectAssetStorage.Linked) return reference;
            byte[]? bytes = null;
            if (catalog?.Find(reference.Sha256) is { } record)
            {
                var resolved = catalog.ReadVerified(record);
                if (resolved.Status == AssetResolutionStatus.Available) bytes = resolved.Content;
            }
            if (bytes is null && sourcePath is not null && reference.RelativePath is { } relative
                && ProjectLinkedAssetPath.TryResolve(sourcePath, relative, out var path)
                && ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(sourcePath, path))
            {
                try { bytes = AssetImagePolicy.ReadEncodedFile(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or InvalidDataException) { }
            }
            if (bytes is null || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    reference.Sha256, StringComparison.OrdinalIgnoreCase)) return reference;
            changed = true;
            return reference with { Storage = ProjectAssetStorage.Embedded, EmbeddedContent = bytes,
                CatalogId = null, RelativePath = null };
        }).ToArray();
        return changed ? document with { Assets = assets } : document;
    }
}
