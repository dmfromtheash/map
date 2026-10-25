using System.Security.Cryptography;
using System.Text;

namespace Map.Assets;

/// <summary>How the local library document was loaded. The UI must show these apart.</summary>
public enum AssetLibraryStatus
{
    /// <summary>No index yet, or a healthy current document.</summary>
    Ready = 0,

    /// <summary>An older document was converted during this open.</summary>
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
    /// <summary>
    /// Optional asset-owned clipping coverage. It is a separate content-addressed image, never
    /// bytes duplicated into each variant or placed stamp.
    /// </summary>
    public string? MaskSha256 { get; init; }
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

    /// <summary>
    /// An archived card is kept with all its content and maps that use it still resolve it, but
    /// the catalog hides it unless «Архивные» is switched on. Older catalogs read as not archived.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Archived { get; init; }

    /// <summary>Placement passport of the card; null keeps the built-in defaults (older catalogs).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AssetPlacementDefaults? Placement { get; init; }
}

/// <summary>
/// How an asset prefers to be placed. Values are starting points of the tool; the user can
/// always override them for a gesture.
/// </summary>
public sealed record AssetPlacementDefaults
{
    /// <summary>Null = not assigned; None = explicitly cleared, including built-in defaults.</summary>
    public AssetRole? Role { get; init; }

    /// <summary>Degrees in [-180,180]; an empty list allows any rotation. Catalog metadata only.</summary>
    public IReadOnlyList<double> AllowedRotations { get; init; } = [];

    /// <summary>New areas draw nearer objects over farther ones.</summary>
    public bool DepthSort { get; init; }

    /// <summary>Empty = no hex contract; otherwise six clockwise edge labels starting at edge 0.</summary>
    public IReadOnlyList<string> HexEdges { get; init; } = [];

    /// <summary>
    /// Side of the no-overlap box as a share of the stamp side (0.05..1); null = 30 %. Smaller
    /// boxes let a scatter pack tighter, larger ones keep pictures further apart.
    /// </summary>
    public double? CollisionFraction { get; init; }

    /// <summary>«Случайный поворот»: the tool starts with a full ±180° rotation spread.</summary>
    public bool RandomRotation { get; init; }

    /// <summary>«Случайный размер ±%» (0..100): the tool starts with this size spread.</summary>
    public double RandomSizePercent { get; init; }

    /// <summary>«Можно отражать»: false keeps the picture unmirrored (text, a compass, a coat of arms).</summary>
    public bool CanFlip { get; init; } = true;

    /// <summary>«Без тени»: the tool starts with the shadow off for this asset.</summary>
    public bool NoShadow { get; init; }

    // JSON materializes IReadOnlyList as List. Passports must still compare by value after a
    // restart, including the empty default, rather than by the identity of their list objects.
    public bool Equals(AssetPlacementDefaults? other) => other is not null
        && Role == other.Role && DepthSort == other.DepthSort && CanFlip == other.CanFlip
        && RandomRotation == other.RandomRotation && NoShadow == other.NoShadow
        && CollisionFraction == other.CollisionFraction && RandomSizePercent == other.RandomSizePercent
        && Same(AllowedRotations, other.AllowedRotations) && Same(HexEdges, other.HexEdges);

    private static bool Same<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        ReferenceEquals(a, b) || a is not null && b is not null && a.SequenceEqual(b);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Role); hash.Add(DepthSort); hash.Add(CanFlip); hash.Add(RandomRotation);
        hash.Add(NoShadow); hash.Add(CollisionFraction); hash.Add(RandomSizePercent);
        if (AllowedRotations is not null) foreach (var angle in AllowedRotations) hash.Add(angle);
        if (HexEdges is not null) foreach (var edge in HexEdges) hash.Add(edge, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public enum AssetRole { None, Tree, Spruce, Bush, Peak, Slope, House, Tower, Wall, Door, Sign }

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

    public static string VariantIdForSet(string setId, string sha256) =>
        VariantPrefix + Digest("variant:" + setId + ":" + Normalize(sha256));

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
