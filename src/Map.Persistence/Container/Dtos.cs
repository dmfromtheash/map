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

/// <summary>asset-manifest.json — empty catalog in this slice, but a real versionable document.</summary>
internal sealed class AssetManifestDto
{
    [JsonPropertyName("assets")] public List<AssetEntryDto> Assets { get; set; } = [];
}

/// <summary>Reserved for later slices; declared so the empty catalog has a stable element shape.</summary>
internal sealed class AssetEntryDto
{
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}
