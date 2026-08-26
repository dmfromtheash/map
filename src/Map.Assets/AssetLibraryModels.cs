using System.Security.Cryptography;
using System.Text;

namespace Map.Assets;

/// <summary>How the local library document was loaded. The UI must show these apart.</summary>
public enum AssetLibraryStatus
{
    /// <summary>No index yet, or a healthy schema v2 document.</summary>
    Ready = 0,

    /// <summary>A flat schema v1 array was converted to v2 during this open.</summary>
    Migrated = 1,

    /// <summary>The index could not be parsed. Nothing is written until the user resolves it.</summary>
    Damaged = 2,
}

/// <summary>
/// A user-owned folder of the local library. Collections are our own information architecture:
/// they carry no meaning beyond the name, the optional parent and the manual order.
/// </summary>
public sealed record AssetCollection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? ParentId { get; init; }
    public int Order { get; init; }
}

/// <summary>
/// One concrete member of a logical set. The variant is the stable identity a tool selects; the
/// bytes stay shared through <see cref="Sha256"/>, so two variants may name the same content.
/// </summary>
public sealed record AssetVariant
{
    public required string Id { get; init; }
    public required string Sha256 { get; init; }
    public required string Label { get; init; }
    public double AnchorX { get; init; } = 0.5;
    public double AnchorY { get; init; } = 0.5;
    public double DefaultScale { get; init; } = 1.0;
}

/// <summary>
/// The logical library card. One import of a single file is a singleton set; one import of many
/// files is a single ordered set. Favorite and tags belong to the set, never to the bytes.
/// </summary>
public sealed record AssetSet
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> VariantIds { get; init; } = [];
    public required string CoverVariantId { get; init; }
    public string? CollectionId { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Favorite { get; init; }
    public required DateTimeOffset ImportedUtc { get; init; }
}

/// <summary>Deterministic identity for content-derived singleton sets and variants.</summary>
public static class AssetIdentity
{
    public const string SetPrefix = "set-";
    public const string VariantPrefix = "var-";

    /// <summary>
    /// A singleton set is fully determined by its content, so its id is derived rather than
    /// generated: reimporting the same bytes anywhere always lands on the same card.
    /// </summary>
    public static string SetIdForContent(string sha256) => SetPrefix + Digest("set:" + Normalize(sha256));

    public static string VariantIdForContent(string sha256) =>
        VariantPrefix + Digest("variant:" + Normalize(sha256));

    /// <summary>
    /// A multi-variant set has no single content hash, so it is derived once from the ordered
    /// hashes it was created from and then stored. Later reorder or rename keeps the stored id.
    /// </summary>
    public static string SetIdForVariants(IEnumerable<string> orderedSha256) =>
        SetPrefix + Digest("set:" + string.Join('/', orderedSha256.Select(Normalize)));

    public static string CollectionId(string name) => "col-" + Digest("collection:" + name.Trim().ToLowerInvariant());

    private static string Normalize(string sha256) => (sha256 ?? "").Trim().ToLowerInvariant();

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
}

/// <summary>One bounded recent entry: which set the user last worked with and in which variant.</summary>
public sealed record AssetRecentEntry
{
    public required string SetId { get; init; }
    public required string VariantId { get; init; }
    public required DateTimeOffset UsedUtc { get; init; }
}
