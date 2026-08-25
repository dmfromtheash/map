using System.Text.Json.Serialization;

namespace Map.Spike.Persistence.Container;

/// <summary>Property order is the serialization order; do not reorder without a format bump.</summary>
public sealed class ManifestDto
{
    [JsonPropertyName("format")] public string Format { get; set; } = SpikeFormat.FormatId;
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = SpikeFormat.SchemaVersion;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "0.0.1-spike";
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("canvasWidth")] public int CanvasWidth { get; set; }
    [JsonPropertyName("canvasHeight")] public int CanvasHeight { get; set; }
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("modifiedUtc")] public string ModifiedUtc { get; set; } = "";
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("sceneSha256")] public string SceneSha256 { get; set; } = "";
    [JsonPropertyName("tileManifestSha256")] public string TileManifestSha256 { get; set; } = "";
}
