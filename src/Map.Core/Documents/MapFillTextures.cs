namespace Map.Core.Documents;

/// <summary>What a fill texture is for. A ground texture belongs on land, a water one on rivers
/// and lakes; the pickers never offer a texture for the wrong kind of data.</summary>
public enum MapFillTextureKind
{
    Ground,
    Water,
}

/// <summary>One fill texture: an identity the document stores and a name the user sees.</summary>
public sealed record MapFillTexture(string Id, string Title, MapFillTextureKind Kind, string Group = "", bool MirrorRepeat = false);

/// <summary>
/// Known fill texture identities, including retired materials still referenced by saved maps.
/// The owner removed the earlier artwork on 2026-09-09. Those metadata identities preserve
/// document validation and round trips; they do not imply available pixels. The UI offers only
/// materials that the renderer can actually load. Missing pixels fall back to a plain fill.
/// </summary>
public static class MapFillTextures
{
    public const string Foothill = "watercolour-foothill";
    public const string SoftMeadow = "watercolor-soft-meadow";
    public const string SoftSand = "watercolor-soft-sand";
    public const string SoftMoss = "watercolor-soft-moss";
    /// <summary>The owner's own painted ground (2026-10-18), redrawn at four times the source size. Since
    /// 2026-10-18 only these four owner grounds have pixels; the foothill and the first ice are retired ids.</summary>
    public const string OwnerIce = "owner-ice-strokes";
    public const string OwnerSnow = "owner-snow-drifts";
    public const string OwnerSnowSparkle = "owner-snow-sparkle";
    public const string OwnerSnowSoft = "owner-snow-soft";
    public const string OwnerIceWash = "owner-ice-wash";

    public static IReadOnlyList<MapFillTexture> All { get; } =
    [
        new(SoftMeadow, "Мягкая акварель · луг", MapFillTextureKind.Ground, "Мягкая акварель", true),
        new(SoftSand, "Мягкая акварель · песок", MapFillTextureKind.Ground, "Мягкая акварель", true),
        new(SoftMoss, "Мягкая акварель · лесная почва", MapFillTextureKind.Ground, "Мягкая акварель", true),
        new("regional-meadow", "Тихий атлас · луг", MapFillTextureKind.Ground, "Тихий атлас"),
        new("regional-forest", "Тихий атлас · лесная почва", MapFillTextureKind.Ground, "Тихий атлас"),
        new("regional-ochre", "Тихий атлас · сухая земля", MapFillTextureKind.Ground, "Тихий атлас"),
        new("regional-water", "Тихий атлас · вода", MapFillTextureKind.Water, "Тихий атлас"),
        new("watercolour-grass", "Акварель · трава", MapFillTextureKind.Ground, "Акварель"),
        new("watercolour-water", "Акварель · вода", MapFillTextureKind.Water, "Акварель"),
        new(Foothill, "Акварель · подножие гор", MapFillTextureKind.Ground, "Акварель"),
        new(OwnerIce, "Лёд · мазки", MapFillTextureKind.Ground, "Снег и лёд"),
        new(OwnerSnow, "Снег · наст со штриховкой", MapFillTextureKind.Ground, "Снег и лёд"),
        new(OwnerSnowSparkle, "Снег · искристая пыль", MapFillTextureKind.Ground, "Снег и лёд"),
        new(OwnerSnowSoft, "Снег · мягкие надувы", MapFillTextureKind.Ground, "Снег и лёд"),
        new(OwnerIceWash, "Лёд · голубая размывка", MapFillTextureKind.Ground, "Снег и лёд"),
        new("meadow", "Луг", MapFillTextureKind.Ground, "Трава и лес"),
        new("sand", "Тёплый песок", MapFillTextureKind.Ground, "Почва и камень"),
        new("stone", "Выветренный камень", MapFillTextureKind.Ground, "Почва и камень"),
        new("parchment", "Атласный пергамент", MapFillTextureKind.Ground, "Бумага"),
        new("forest-moss", "Лесной мох", MapFillTextureKind.Ground, "Трава и лес"),
        new("dry-grass", "Сухая степь", MapFillTextureKind.Ground, "Трава и лес"),
        new("red-earth", "Красная глина", MapFillTextureKind.Ground, "Почва и камень"),
        new("snow", "Снежный покров", MapFillTextureKind.Ground, "Снег и лёд"),
        new("marsh", "Болотистая низина", MapFillTextureKind.Ground, "Трава и лес"),
        new("water-still", "Тихая вода", MapFillTextureKind.Water, "Вода"),
        new("water-open", "Глубокая вода", MapFillTextureKind.Water, "Вода"),
        new("water-shallow", "Светлая отмель", MapFillTextureKind.Water, "Вода"),
    ];

    public static IReadOnlyList<MapFillTexture> Of(MapFillTextureKind kind) =>
        [.. All.Where(texture => texture.Kind == kind)];

    public static MapFillTexture? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null
        : IsAssetId(id) ? new MapFillTexture(id, LibraryTitle, MapFillTextureKind.Ground, LibraryGroup)
        : All.FirstOrDefault(texture => texture.Id == id);

    /// <summary>
    /// A tiling texture from the user's asset library: <c>asset:</c> and the content SHA-256 in
    /// lower-case hex. The document stores only this id - never a path or a drive letter - and
    /// carries the bytes as an embedded <see cref="MapAssetReference"/> of the same hash, so the map
    /// still draws when the library (or the drive it lives on) is not there.
    /// </summary>
    public const string AssetPrefix = "asset:";
    public const string LibraryTitle = "Текстура из библиотеки";
    public const string LibraryGroup = "Мои текстуры";

    public static string AssetId(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var id = AssetPrefix + sha256.ToLowerInvariant();
        return IsAssetId(id) ? id : throw new ArgumentException("A library texture is named by a SHA-256 hex string.", nameof(sha256));
    }

    public static bool IsAssetId(string? id) =>
        id is { Length: 70 } && id.StartsWith(AssetPrefix, StringComparison.Ordinal)
        && id.AsSpan(AssetPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>The content hash of a library texture id, or null for a built-in or absent one.</summary>
    public static string? AssetSha256(string? id) => IsAssetId(id) ? id![AssetPrefix.Length..] : null;

    /// <summary>
    /// Whether a picker of this kind may store the id: a built-in texture of the same kind, or any
    /// library texture (its kind comes from the library tags, which the document does not keep).
    /// </summary>
    public static bool Accepts(string? id, MapFillTextureKind kind) =>
        IsAssetId(id) || Find(id) is { } texture && texture.Kind == kind;

    /// <summary>Smallest and largest world size of one texture tile, in canvas units.</summary>
    public const double MinimumScale = 16;
    public const double MaximumScale = 4096;
    public const double DefaultGroundScale = 320;
    public const double DefaultWaterScale = 160;

    /// <summary>An id is valid when it is absent (plain fill), names a built-in texture or a library one.</summary>
    public static bool IsValidId(string? id) => string.IsNullOrWhiteSpace(id) || Find(id) is not null;

    public static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale >= MinimumScale && scale <= MaximumScale;

    public static bool IsValidOpacity(double opacity) => double.IsFinite(opacity) && opacity is >= 0 and <= 1;
}
