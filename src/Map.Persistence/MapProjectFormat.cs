namespace Map.Persistence;

/// <summary>Constants of the current .mapproj container (docs/05, DEC-005).</summary>
public static class MapProjectFormat
{
    public const string FormatId = "map-project";
    public const int LegacySchemaVersion = 1;
    public const int SchemaVersion = 3;
    public const string Extension = ".mapproj";

    public const string ManifestEntry = "manifest.json";
    public const string SceneEntry = "scene.json";
    public const string AssetManifestEntry = "asset-manifest.json";
    public const string PreviewEntry = "preview.png";
    public const string RasterPrefix = "rasters/";
    public const string MaskPrefix = "masks/";
    public const string AssetPrefix = "assets/";
    public const string CheckpointPrefix = "checkpoints/";
    public const string CheckpointCatalogEntry = "checkpoints/catalog.json";

    /// <summary>Every supported container must contain these canonical entries.</summary>
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
    // These limits are part of the untrusted-input boundary. A local .mapproj can still be
    // malformed or attacker-controlled, so the reader rejects excessive work before allocating
    // or inflating payloads and fails with a specific resource-limit error.

    /// <summary>Bounds sparse tiles/assets without tying the limit to the four Stage 2 entries.</summary>
    public const int MaxEntries = 16_384;

    /// <summary>Bounds compressed bytes before ZipArchive materializes central-directory state.</summary>
    public const long MaxContainerFileBytes = 544L * 1024 * 1024;

    public const long MaxCentralDirectoryBytes = 16L * 1024 * 1024;

    public const int MaxEntryNameBytes = 512;

    public const long MaxEntryNamePayloadBytes = 2L * 1024 * 1024;

    /// <summary>Upper bound of any single JSON entry once decompressed.</summary>
    public const long MaxJsonEntryBytes = 32L * 1024 * 1024;

    /// <summary>One raw tile is fixed-size and bounded (256x256 RGBA at most).</summary>
    public const long MaxTileEntryBytes = 256L * 256 * 4;

    public const long MaxAssetEntryBytes = 64L * 1024 * 1024;

    /// <summary>Upper bound of the stored preview image bytes.</summary>
    public const long MaxPreviewBytes = 8L * 1024 * 1024;

    /// <summary>Upper bound of the whole container once decompressed.</summary>
    public const long MaxAssetPayloadBytes = 256L * 1024 * 1024;

    public const long MaxTilePayloadBytes = 256L * 1024 * 1024;

    public const long MaxJsonPayloadBytes = 64L * 1024 * 1024;

    public const long MaxTotalUncompressedBytes = 512L * 1024 * 1024;

    public const double MaxCompressionRatio = 1_000;

    /// <summary>
    /// Small highly-compressible entries (notably empty raw tiles) are legitimate. Above this
    /// additive allowance, the ratio budget applies to every entry instead of having a bypassing
    /// size threshold.
    /// </summary>
    public const long CompressionRatioSlackBytes = 1L * 1024 * 1024;

    /// <summary>Largest preview dimension the reader accepts, read from the PNG IHDR.</summary>
    public const int MaxPreviewDimension = 8192;

    public const long MaxPreviewPixels = 16L * 1024 * 1024;

    public const long MaxCheckpointPayloadBytes = Map.Core.Documents.MapCheckpointLimits.MaxTotalStoredBytes;
    public const long MaxCheckpointPreviewBytes = Map.Core.Documents.MapCheckpointLimits.MaxPreviewBytes;
    public const int MaxCheckpointPreviewWidth = Map.Core.Documents.MapCheckpointLimits.PreviewWidth;
    public const int MaxCheckpointPreviewHeight = Map.Core.Documents.MapCheckpointLimits.PreviewHeight;
}
