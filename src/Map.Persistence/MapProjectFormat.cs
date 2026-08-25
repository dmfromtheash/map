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
    public const string RasterPrefix = "rasters/";
    public const string MaskPrefix = "masks/";
    public const string AssetPrefix = "assets/";

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

    // ---------------------------------------------------------------- read limits
    //
    // Not a defence against attackers - the program is offline and opens the owner's own files.
    // These bound what a truncated, mis-copied or mislabeled file can cost before the reader
    // commits memory to it, so a damaged container fails with a clear error instead of a hang.

    /// <summary>Bounds sparse tiles/assets without tying the limit to the four Stage 2 entries.</summary>
    public const int MaxEntries = 16_384;

    /// <summary>Upper bound of any single JSON entry once decompressed.</summary>
    public const long MaxJsonEntryBytes = 32L * 1024 * 1024;

    /// <summary>One raw tile is fixed-size and bounded (256x256 RGBA at most).</summary>
    public const long MaxTileEntryBytes = 256L * 256 * 4;

    public const long MaxAssetEntryBytes = 512L * 1024 * 1024;

    /// <summary>Upper bound of the stored preview image bytes.</summary>
    public const long MaxPreviewBytes = 8L * 1024 * 1024;

    /// <summary>Upper bound of the whole container once decompressed.</summary>
    public const long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Largest preview dimension the reader accepts, read from the PNG IHDR.</summary>
    public const int MaxPreviewDimension = 8192;
}
