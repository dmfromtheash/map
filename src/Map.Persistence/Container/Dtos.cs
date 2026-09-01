using System.Text.Json;
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
    [JsonPropertyName("checkpointCatalogSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CheckpointCatalogSha256 { get; set; }
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
    [JsonPropertyName("notes")] public List<NoteDto> Notes { get; set; } = [];
    [JsonPropertyName("stampForeground")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StampForegroundDto? StampForeground { get; set; }
}

internal sealed class StampForegroundDto
{
    [JsonPropertyName("tiles")] public List<TileDto> Tiles { get; set; } = [];
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
    [JsonPropertyName("grids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GridDefinitionDto>? Grids { get; set; }
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FilterSettingsDto? Filter { get; set; }
}

internal sealed class FilterSettingsDto
{
    [JsonPropertyName("brightness")] public double Brightness { get; set; }
    [JsonPropertyName("contrast")] public double Contrast { get; set; }
    [JsonPropertyName("saturation")] public double Saturation { get; set; }
    [JsonPropertyName("hueDegrees")] public double HueDegrees { get; set; }
    [JsonPropertyName("temperature")] public double Temperature { get; set; }
    [JsonPropertyName("textureAssetSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextureAssetSha256 { get; set; }
    [JsonPropertyName("textureOpacity")] public double TextureOpacity { get; set; }
    [JsonPropertyName("textureScale")] public double TextureScale { get; set; }
}

internal sealed class GridDefinitionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("geometry")] public string Geometry { get; set; } = "Square";
    [JsonPropertyName("cellSize")] public double CellSize { get; set; }
    [JsonPropertyName("subdivisions")] public int Subdivisions { get; set; } = 1;
    [JsonPropertyName("lineWidth")] public double LineWidth { get; set; } = 1;
    [JsonPropertyName("color")] public string Color { get; set; } = "";
    [JsonPropertyName("opacity")] public double Opacity { get; set; }
    [JsonPropertyName("offsetX")] public double OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public double OffsetY { get; set; }
    [JsonPropertyName("rotationDegrees")] public double RotationDegrees { get; set; }
    [JsonPropertyName("snapEnabled")] public bool SnapEnabled { get; set; }
    [JsonPropertyName("includeInExport")] public bool IncludeInExport { get; set; }
}

internal sealed class NoteDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("markdown")] public string Markdown { get; set; } = "";
    [JsonPropertyName("anchorKind")] public string AnchorKind { get; set; } = "World";
    [JsonPropertyName("worldX")] public double WorldX { get; set; }
    [JsonPropertyName("worldY")] public double WorldY { get; set; }
    [JsonPropertyName("objectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectId { get; set; }
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
    [JsonPropertyName("groupName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupName { get; set; }
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
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextStyleDto? Text { get; set; }
    [JsonPropertyName("composition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditableCompositionDto? Composition { get; set; }
    [JsonPropertyName("placeholderReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlaceholderReason { get; set; }
    [JsonPropertyName("opaquePayload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpaqueSceneObjectPayloadDto? OpaquePayload { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class OpaqueSceneObjectPayloadDto
{
    [JsonPropertyName("originalKind")] public string OriginalKind { get; set; } = "";
    [JsonPropertyName("rawJson")] public string RawJson { get; set; } = "";
}

internal sealed class EditableCompositionDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sourceDocumentId")] public string SourceDocumentId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sourceWidth")] public double SourceWidth { get; set; }
    [JsonPropertyName("sourceHeight")] public double SourceHeight { get; set; }
    [JsonPropertyName("anchorX")] public double AnchorX { get; set; }
    [JsonPropertyName("anchorY")] public double AnchorY { get; set; }
    [JsonPropertyName("referenceGridScale")] public double ReferenceGridScale { get; set; }
    [JsonPropertyName("items")] public List<SceneObjectDto> Items { get; set; } = [];
    [JsonPropertyName("assets")] public List<CompositionAssetDto> Assets { get; set; } = [];
    [JsonPropertyName("includeBackground")] public bool IncludeBackground { get; set; }
    [JsonPropertyName("backgroundColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundColor { get; set; }
    [JsonPropertyName("includeGrids")] public bool IncludeGrids { get; set; }
    [JsonPropertyName("grids")] public List<GridDefinitionDto> Grids { get; set; } = [];
    [JsonPropertyName("includeFilters")] public bool IncludeFilters { get; set; }
    [JsonPropertyName("filters")] public List<FilterSettingsDto> Filters { get; set; } = [];
}

internal sealed class CompositionAssetDto
{
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("provenance")] public string Provenance { get; set; } = "";
    [JsonPropertyName("license")] public string License { get; set; } = "";
}

internal sealed class TextStyleDto
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("requestedFontFamily")] public string RequestedFontFamily { get; set; } = "";
    [JsonPropertyName("fallbackFontFamily")] public string FallbackFontFamily { get; set; } = "";
    [JsonPropertyName("fontWeight")] public int FontWeight { get; set; }
    [JsonPropertyName("fontSize")] public double FontSize { get; set; }
    [JsonPropertyName("fillColor")] public string FillColor { get; set; } = "";
    [JsonPropertyName("outlineColor")] public string OutlineColor { get; set; } = "";
    [JsonPropertyName("outlineWidth")] public double OutlineWidth { get; set; }
    [JsonPropertyName("shadowOffsetX")] public double ShadowOffsetX { get; set; }
    [JsonPropertyName("shadowOffsetY")] public double ShadowOffsetY { get; set; }
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; }
    [JsonPropertyName("shadowOpacity")] public double ShadowOpacity { get; set; }
    [JsonPropertyName("curve")] public double Curve { get; set; }
    [JsonPropertyName("letterSpacing")] public double LetterSpacing { get; set; }
    [JsonPropertyName("lineHeight")] public double LineHeight { get; set; }
    [JsonPropertyName("alignment")] public string Alignment { get; set; } = "Left";
    [JsonPropertyName("flipHorizontal")] public bool FlipHorizontal { get; set; }
    [JsonPropertyName("flipVertical")] public bool FlipVertical { get; set; }
    [JsonPropertyName("blur")] public double Blur { get; set; }
    [JsonPropertyName("blend")] public string Blend { get; set; } = "Normal";
}

internal sealed class StampStyleDto
{
    [JsonPropertyName("assetSha256")] public string AssetSha256 { get; set; } = "";
    [JsonPropertyName("referenceWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ReferenceWidth { get; set; }
    [JsonPropertyName("referenceHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ReferenceHeight { get; set; }
    [JsonPropertyName("flipHorizontal")] public bool FlipHorizontal { get; set; }
    [JsonPropertyName("flipVertical")] public bool FlipVertical { get; set; }
    [JsonPropertyName("maskAssetSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaskAssetSha256 { get; set; }
    [JsonPropertyName("clipMaskEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ClipMaskEnabled { get; set; }
    [JsonPropertyName("blend")] public string Blend { get; set; } = "Normal";
    [JsonPropertyName("blur")] public double Blur { get; set; }
    [JsonPropertyName("shadowOffsetX")] public double ShadowOffsetX { get; set; } = 4;
    [JsonPropertyName("shadowOffsetY")] public double ShadowOffsetY { get; set; } = 4;
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; } = 6;
    [JsonPropertyName("shadowOpacity")] public double ShadowOpacity { get; set; }
    [JsonPropertyName("shadowPlacement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShadowPlacement { get; set; }
    [JsonPropertyName("shadowColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShadowColor { get; set; }
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
    [JsonPropertyName("blur")] public double Blur { get; set; }
    [JsonPropertyName("pattern")] public string Pattern { get; set; } = "Solid";
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

internal sealed class CheckpointCatalogDto
{
    [JsonPropertyName("checkpoints")]
    public List<CheckpointEntryDto> Checkpoints { get; set; } = [];
}

internal sealed class CheckpointEntryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("snapshotManifestSha256")]
    public string SnapshotManifestSha256 { get; set; } = "";
}

internal sealed class CheckpointSnapshotManifestDto
{
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("canvas")] public CanvasDto Canvas { get; set; } = new();
    [JsonPropertyName("sceneSha256")] public string SceneSha256 { get; set; } = "";
    [JsonPropertyName("assetManifestSha256")] public string AssetManifestSha256 { get; set; } = "";
    [JsonPropertyName("previewSha256")] public string PreviewSha256 { get; set; } = "";
}
