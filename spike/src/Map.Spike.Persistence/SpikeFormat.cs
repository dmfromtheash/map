namespace Map.Spike.Persistence;

/// <summary>
/// Spike container constants. Deliberately a DIFFERENT extension from the accepted .mapproj v1
/// (DEC-005) so a spike artefact can never be mistaken for a real project file.
/// </summary>
public static class SpikeFormat
{
    public const string FormatId = "map-spike";
    public const int SchemaVersion = 1;
    public const string Extension = ".mapspike";

    public const string ManifestEntry = "manifest.json";
    public const string SceneEntry = "scene.json";
    public const string TileManifestEntry = "tile-manifest.json";
    public const string RasterPrefix = "rasters/";
    public const string MaskPrefix = "masks/";

    /// <summary>Fixed ZIP entry timestamp so identical documents produce identical bytes.</summary>
    public static readonly DateTimeOffset DeterministicEntryTime =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
