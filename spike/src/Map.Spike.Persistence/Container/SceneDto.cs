using System.Text.Json.Serialization;

namespace Map.Spike.Persistence.Container;

public sealed class SceneDto
{
    [JsonPropertyName("objectLayerId")] public string ObjectLayerId { get; set; } = "";
    [JsonPropertyName("brushLayerId")] public string BrushLayerId { get; set; } = "";
    [JsonPropertyName("decorLayerId")] public string DecorLayerId { get; set; } = "";
    [JsonPropertyName("maskLayerId")] public string MaskLayerId { get; set; } = "";
    [JsonPropertyName("maskTargetLayerId")] public string MaskTargetLayerId { get; set; } = "";
    [JsonPropertyName("tileSize")] public int TileSize { get; set; }
    [JsonPropertyName("objects")] public List<SceneObjectDto> Objects { get; set; } = [];
}

public sealed class SceneObjectDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("cx")] public double CenterX { get; set; }
    [JsonPropertyName("cy")] public double CenterY { get; set; }
    [JsonPropertyName("size")] public double Size { get; set; }
    [JsonPropertyName("rotation")] public double Rotation { get; set; }
    [JsonPropertyName("colorArgb")] public uint ColorArgb { get; set; }
}

public sealed class TileManifestDto
{
    [JsonPropertyName("tiles")] public List<TileEntryDto> Tiles { get; set; } = [];
}

public sealed class TileEntryDto
{
    [JsonPropertyName("entry")] public string Entry { get; set; } = "";
    [JsonPropertyName("length")] public int Length { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}
