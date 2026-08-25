namespace Map.Persistence;

/// <summary>Constants of the .mapproj v1 container (docs/05, DEC-005).</summary>
public static class MapProjectFormat
{
    public const string FormatId = "map-project";
    public const int SchemaVersion = 1;
    public const string Extension = ".mapproj";

    public const string ManifestEntry = "manifest.json";
    public const string SceneEntry = "scene.json";
    public const string AssetManifestEntry = "asset-manifest.json";
    public const string PreviewEntry = "preview.png";

    /// <summary>Every entry a v1 container must contain.</summary>
    public static readonly IReadOnlyList<string> RequiredEntries =
    [
        ManifestEntry,
        SceneEntry,
        AssetManifestEntry,
        PreviewEntry,
    ];

    /// <summary>Fixed ZIP entry timestamp so identical snapshots produce identical entry metadata.</summary>
    public static readonly DateTimeOffset DeterministicEntryTime =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
