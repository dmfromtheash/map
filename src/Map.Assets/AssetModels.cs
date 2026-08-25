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
