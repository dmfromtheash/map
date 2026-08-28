using System.Collections.ObjectModel;

namespace Map.Core.Documents;

public enum ProjectAssetStorage
{
    Embedded = 0,
    Linked = 1,
}

/// <summary>
/// Stable document reference to user-owned image content. Identity is the content SHA-256, never
/// the filename. Embedded bytes make a project portable; linked references retain both a catalog
/// id and a relocatable path plus the expected hash, so a same-name replacement is never silent.
/// </summary>
public sealed record MapAssetReference
{
    public MapAssetReference(
        string sha256,
        string displayName,
        string mediaType,
        string extension,
        ProjectAssetStorage storage,
        DateTimeOffset importedUtc,
        string provenance,
        string license,
        byte[]? embeddedContent = null,
        string? catalogId = null,
        string? relativePath = null,
        byte[]? fallbackThumbnailPng = null)
        : this(
            sha256, displayName, mediaType, extension, storage, importedUtc, provenance, license,
            embeddedContent, catalogId, relativePath, fallbackThumbnailPng, clonePayloads: true)
    {
    }

    /// <summary>
    /// Persistence has already allocated, bounded and verified these arrays. Transferring them
    /// avoids a second full archive-payload copy while the public constructor remains defensive.
    /// </summary>
    internal static MapAssetReference FromValidatedPayloads(
        string sha256,
        string displayName,
        string mediaType,
        string extension,
        ProjectAssetStorage storage,
        DateTimeOffset importedUtc,
        string provenance,
        string license,
        byte[]? embeddedContent,
        string? catalogId,
        string? relativePath,
        byte[]? fallbackThumbnailPng) => new(
            sha256, displayName, mediaType, extension, storage, importedUtc, provenance, license,
            embeddedContent, catalogId, relativePath, fallbackThumbnailPng, clonePayloads: false);

    private MapAssetReference(
        string sha256,
        string displayName,
        string mediaType,
        string extension,
        ProjectAssetStorage storage,
        DateTimeOffset importedUtc,
        string provenance,
        string license,
        byte[]? embeddedContent,
        string? catalogId,
        string? relativePath,
        byte[]? fallbackThumbnailPng,
        bool clonePayloads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("Asset identity must be a SHA-256 hex string.", nameof(sha256));
        }

        if (!ProjectAssetFormatPolicy.TryGetDeclaredFormat(mediaType, extension, out _))
        {
            throw new ArgumentException(
                "Asset media type and extension must name the same PNG, JPEG or WebP format.");
        }

        if (storage == ProjectAssetStorage.Embedded && embeddedContent is null)
        {
            throw new ArgumentException("An embedded asset carries its content bytes.", nameof(embeddedContent));
        }

        if (storage == ProjectAssetStorage.Linked
            && string.IsNullOrWhiteSpace(catalogId)
            && string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A linked asset needs a catalog id or relative path.");
        }

        if (relativePath is not null && !ProjectLinkedAssetPath.IsSafeRelative(relativePath))
        {
            throw new ArgumentException(
                "A project linked-asset path must be local and relative.", nameof(relativePath));
        }

        Sha256 = sha256.ToLowerInvariant();
        DisplayName = displayName;
        MediaType = mediaType;
        Extension = extension.TrimStart('.').ToLowerInvariant();
        Storage = storage;
        ImportedUtc = importedUtc;
        Provenance = provenance;
        License = license;
        EmbeddedContent = embeddedContent is null || !clonePayloads
            ? embeddedContent
            : (byte[])embeddedContent.Clone();
        CatalogId = catalogId;
        RelativePath = relativePath;
        FallbackThumbnailPng = fallbackThumbnailPng is null || !clonePayloads
            ? fallbackThumbnailPng
            : (byte[])fallbackThumbnailPng.Clone();
    }

    public string Sha256 { get; init; }
    public string DisplayName { get; init; }
    public string MediaType { get; init; }
    public string Extension { get; init; }
    public ProjectAssetStorage Storage { get; init; }
    public DateTimeOffset ImportedUtc { get; init; }
    public string Provenance { get; init; }
    public string License { get; init; }
    public byte[]? EmbeddedContent { get; init; }
    public string? CatalogId { get; init; }
    public string? RelativePath { get; init; }
    public byte[]? FallbackThumbnailPng { get; init; }
}
