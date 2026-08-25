using System.Text.Json.Serialization;

namespace Map.Persistence.Container;

// Property declaration order below IS the serialization order, and the wire names are pinned by
// attribute. Do not reorder or rename without a schema bump: canonical bytes depend on it.

/// <summary>manifest.json — docs/05 accepted baseline.</summary>
internal sealed class ManifestDto
{
    [JsonPropertyName("format")] public string Format { get; set; } = MapProjectFormat.FormatId;
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = MapProjectFormat.SchemaVersion;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("modifiedUtc")] public string ModifiedUtc { get; set; } = "";
    [JsonPropertyName("canvas")] public CanvasDto Canvas { get; set; } = new();
    [JsonPropertyName("preset")] public string Preset { get; set; } = "blank";
    [JsonPropertyName("sceneSha256")] public string SceneSha256 { get; set; } = "";
    [JsonPropertyName("assetManifestSha256")] public string AssetManifestSha256 { get; set; } = "";
}

internal sealed class CanvasDto
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("background")] public string Background { get; set; } = "";
}

/// <summary>scene.json — layers and objects; document identity repeated for a cross-check.</summary>
internal sealed class SceneDto
{
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("layers")] public List<LayerDto> Layers { get; set; } = [];
}

internal sealed class LayerDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; }
    [JsonPropertyName("blend")] public string Blend { get; set; } = "Normal";
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }
    [JsonPropertyName("objects")] public List<SceneObjectDto> Objects { get; set; } = [];
    [JsonPropertyName("tiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TileDto>? Tiles { get; set; }
    [JsonPropertyName("mask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MaskDto? Mask { get; set; }
    [JsonPropertyName("landStyle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LandStyleDto? LandStyle { get; set; }
}

internal sealed class LandStyleDto
{
    [JsonPropertyName("fillColor")] public string FillColor { get; set; } = "";
    [JsonPropertyName("outlineColor")] public string OutlineColor { get; set; } = "";
    [JsonPropertyName("outlineWidth")] public double OutlineWidth { get; set; }
    [JsonPropertyName("innerShadowOpacity")] public double InnerShadowOpacity { get; set; }
    [JsonPropertyName("outerShadowOpacity")] public double OuterShadowOpacity { get; set; }
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; }
    [JsonPropertyName("rippleColor")] public string RippleColor { get; set; } = "";
    [JsonPropertyName("rippleWidth")] public double RippleWidth { get; set; }
    [JsonPropertyName("rippleSpacing")] public double RippleSpacing { get; set; }
    [JsonPropertyName("rippleCount")] public int RippleCount { get; set; }
}

internal sealed class MaskDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("tiles")] public List<TileDto> Tiles { get; set; } = [];
}

internal sealed class TileDto
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

internal sealed class SceneObjectDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("rotationDegrees")] public double RotationDegrees { get; set; }
    [JsonPropertyName("fillColor")] public string FillColor { get; set; } = "";
    [JsonPropertyName("strokeColor")] public string StrokeColor { get; set; } = "";
    [JsonPropertyName("strokeWidth")] public double StrokeWidth { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("sublayer")] public int Sublayer { get; set; }
    [JsonPropertyName("groupId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; set; }
    [JsonPropertyName("objectOpacity")] public double ObjectOpacity { get; set; } = 1.0;
    [JsonPropertyName("stamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StampStyleDto? Stamp { get; set; }
    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LineStyleDto? Line { get; set; }
    [JsonPropertyName("shape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShapeStyleDto? Shape { get; set; }
}

internal sealed class StampStyleDto
{
    [JsonPropertyName("assetSha256")] public string AssetSha256 { get; set; } = "";
    [JsonPropertyName("flipHorizontal")] public bool FlipHorizontal { get; set; }
    [JsonPropertyName("flipVertical")] public bool FlipVertical { get; set; }
    [JsonPropertyName("blend")] public string Blend { get; set; } = "Normal";
    [JsonPropertyName("blur")] public double Blur { get; set; }
    [JsonPropertyName("shadowOffsetX")] public double ShadowOffsetX { get; set; } = 4;
    [JsonPropertyName("shadowOffsetY")] public double ShadowOffsetY { get; set; } = 4;
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; } = 6;
    [JsonPropertyName("shadowOpacity")] public double ShadowOpacity { get; set; }
    [JsonPropertyName("hue")] public double Hue { get; set; }
    [JsonPropertyName("saturation")] public double Saturation { get; set; }
    [JsonPropertyName("brightness")] public double Brightness { get; set; }
    [JsonPropertyName("contrast")] public double Contrast { get; set; }
}

internal sealed class VectorNodeDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "Corner";
    [JsonPropertyName("controlInX")] public double ControlInX { get; set; }
    [JsonPropertyName("controlInY")] public double ControlInY { get; set; }
    [JsonPropertyName("controlOutX")] public double ControlOutX { get; set; }
    [JsonPropertyName("controlOutY")] public double ControlOutY { get; set; }
}

internal sealed class LinePortalDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("position")] public double Position { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
}

internal sealed class VectorStrokeDto
{
    [JsonPropertyName("color")] public string Color { get; set; } = "";
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; }
    [JsonPropertyName("blend")] public string Blend { get; set; } = "Normal";
    [JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonPropertyName("endCap")] public string EndCap { get; set; } = "Round";
    [JsonPropertyName("nodeCap")] public string NodeCap { get; set; } = "Round";
    [JsonPropertyName("shadowOffsetX")] public double ShadowOffsetX { get; set; }
    [JsonPropertyName("shadowOffsetY")] public double ShadowOffsetY { get; set; }
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; }
    [JsonPropertyName("shadowOpacity")] public double ShadowOpacity { get; set; }
    [JsonPropertyName("assetSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssetSha256 { get; set; }
    [JsonPropertyName("assetSpacing")] public double AssetSpacing { get; set; }
    [JsonPropertyName("hue")] public double Hue { get; set; }
    [JsonPropertyName("saturation")] public double Saturation { get; set; }
}

internal sealed class LineStyleDto
{
    [JsonPropertyName("nodes")] public List<VectorNodeDto> Nodes { get; set; } = [];
    [JsonPropertyName("stroke")] public VectorStrokeDto Stroke { get; set; } = new();
    [JsonPropertyName("portals")] public List<LinePortalDto> Portals { get; set; } = [];
    [JsonPropertyName("closed")] public bool Closed { get; set; }
}

internal sealed class ShapeFillDto
{
    [JsonPropertyName("color")] public string Color { get; set; } = "";
    [JsonPropertyName("textureAssetSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextureAssetSha256 { get; set; }
    [JsonPropertyName("textureScale")] public double TextureScale { get; set; }
    [JsonPropertyName("textureRotation")] public double TextureRotation { get; set; }
    [JsonPropertyName("textureOffsetX")] public double TextureOffsetX { get; set; }
    [JsonPropertyName("textureOffsetY")] public double TextureOffsetY { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; }
}

internal sealed class ShapeRegionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "Add";
    [JsonPropertyName("primitive")] public string Primitive { get; set; } = "Polygon";
    [JsonPropertyName("nodes")] public List<VectorNodeDto> Nodes { get; set; } = [];
}

internal sealed class ShapeStyleDto
{
    [JsonPropertyName("regions")] public List<ShapeRegionDto> Regions { get; set; } = [];
    [JsonPropertyName("fill")] public ShapeFillDto Fill { get; set; } = new();
    [JsonPropertyName("stroke")] public VectorStrokeDto Stroke { get; set; } = new();
    [JsonPropertyName("portals")] public List<LinePortalDto> Portals { get; set; } = [];
}

/// <summary>asset-manifest.json — empty catalog in this slice, but a real versionable document.</summary>
internal sealed class AssetManifestDto
{
    [JsonPropertyName("assets")] public List<AssetEntryDto> Assets { get; set; } = [];
}

/// <summary>Reserved for later slices; declared so the empty catalog has a stable element shape.</summary>
internal sealed class AssetEntryDto
{
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("mediaType")] public string MediaType { get; set; } = "";
    [JsonPropertyName("extension")] public string Extension { get; set; } = "";
    [JsonPropertyName("storage")] public string Storage { get; set; } = "";
    [JsonPropertyName("importedUtc")] public string ImportedUtc { get; set; } = "";
    [JsonPropertyName("provenance")] public string Provenance { get; set; } = "";
    [JsonPropertyName("license")] public string License { get; set; } = "";
    [JsonPropertyName("catalogId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogId { get; set; }
    [JsonPropertyName("relativePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RelativePath { get; set; }
    [JsonPropertyName("contentEntry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentEntry { get; set; }
    [JsonPropertyName("thumbnailEntry")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThumbnailEntry { get; set; }
    [JsonPropertyName("thumbnailSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThumbnailSha256 { get; set; }
}
