namespace Map.Core.Documents;

/// <summary>What a fill texture is for. A ground texture belongs on land, a water one on rivers
/// and lakes; the pickers never offer a texture for the wrong kind of data.</summary>
public enum MapFillTextureKind
{
    Ground,
    Water,
}

/// <summary>One built-in fill texture: an identity the document stores and a name the user sees.</summary>
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
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(texture => texture.Id == id);

    /// <summary>Smallest and largest world size of one texture tile, in canvas units.</summary>
    public const double MinimumScale = 16;
    public const double MaximumScale = 4096;
    public const double DefaultGroundScale = 320;
    public const double DefaultWaterScale = 160;

    /// <summary>An id is valid when it is absent (plain fill) or names a built-in texture.</summary>
    public static bool IsValidId(string? id) => string.IsNullOrWhiteSpace(id) || Find(id) is not null;

    public static bool IsValidScale(double scale) =>
        double.IsFinite(scale) && scale >= MinimumScale && scale <= MaximumScale;

    public static bool IsValidOpacity(double opacity) => double.IsFinite(opacity) && opacity is >= 0 and <= 1;
}
