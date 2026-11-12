namespace Map.Assets;

public enum AssetLicenseStatus
{
    Unknown = 0,
    UserOwned = 1,
    Licensed = 2,
    PublicDomain = 3,
}

public sealed record AssetProvenance
{
    public string Source { get; init; } = "user-imported";
    public string Author { get; init; } = "";
    public string LicenseId { get; init; } = "";
    public string LicenseUrl { get; init; } = "";
    public AssetLicenseStatus LicenseStatus { get; init; } = AssetLicenseStatus.Unknown;
    public bool CommercialUseAllowed { get; init; }
}

public sealed record AssetRecord
{
    public required string Sha256 { get; init; }
    public required string DisplayName { get; init; }
    public required string OriginalFileName { get; init; }
    public required string MediaType { get; init; }
    public required string Extension { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool HasAlpha { get; init; }
    public required long ByteLength { get; init; }
    public required DateTimeOffset ImportedUtc { get; init; }
    public string Pack { get; init; } = "Мои ассеты";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Favorite { get; init; }
    public required AssetProvenance Provenance { get; init; }

    /// <summary>Catalog-relative original; null for a linked-only import.</summary>
    public string? StoredRelativePath { get; init; }

    /// <summary>User source for linked mode. Hash is still the identity.</summary>
    public string? LinkedPath { get; init; }

    public required string ThumbnailRelativePath { get; init; }
}

/// <summary>One logical set for <see cref="AssetCatalog.ImportSets"/>: files with their variant labels, in order.</summary>
public sealed record AssetSetImport
{
    public required string StableKey { get; init; }
    public required string Name { get; init; }
    public string? Detail { get; init; }

    /// <summary>The builder renames a set it created (names were reworked after looking at the pictures).</summary>
    public bool OverwriteNames { get; init; }
    public required IReadOnlyList<string> Paths { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public IReadOnlyList<string> CollectionPath { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public AssetProvenance Provenance { get; init; } = new();
    public AssetPlacementDefaults? Placement { get; init; }
}

public sealed record AssetImportOptions
{
    public bool Linked { get; init; }
    public string Pack { get; init; } = "Мои ассеты";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public AssetProvenance Provenance { get; init; } = new();
}

public enum AssetResolutionStatus
{
    Available,
    Missing,
    HashMismatch,
}

public sealed record AssetResolution(AssetResolutionStatus Status, string? Path);

/// <summary>
/// One catalog read whose status, hash verdict and payload all describe the same opened bytes.
/// Content is present only for an available, SHA-verified image.
/// </summary>
public sealed record AssetPayloadResolution(
    AssetResolutionStatus Status, string? Path, byte[]? Content);
