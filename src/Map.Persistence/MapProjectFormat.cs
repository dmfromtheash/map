namespace Map.Persistence;

/// <summary>Constants of the current .mapproj container (docs/05, DEC-005).</summary>
public static class MapProjectFormat
{
    public const string FormatId = "map-project";
    public const int LegacySchemaVersion = 1;
    public const int SchemaVersion = 4;
    public const int EditablePathsSchemaVersion = 5;
    public const int EditableAreasSchemaVersion = 6;
    public const int RiversSchemaVersion = 7;
    public const int RoadsSchemaVersion = 8;
    public const int LandPaintSchemaVersion = 9;
    public const int BaseMaterialSchemaVersion = 10;
    /// <summary>A map whose category is not the standard constructor (manifest <c>category</c>).</summary>
    public const int CategoriesSchemaVersion = 11;
    /// <summary>A map that carries a hex layer (layer type <c>Hex</c>, the hex category's surface).</summary>
    public const int HexLayersSchemaVersion = 12;
    /// <summary>
    /// A hex layer that carries the workshop constructs of DEC-049: turned tiles, regions, fog,
    /// paths, the hex key, levels or a travel scale (block <c>hex</c> with <c>stride</c> 9 and its
    /// optional lists).
    /// </summary>
    public const int HexWorkshopSchemaVersion = 13;
    public const string Extension = ".mapproj";

    public const int HexPathBlockingSchemaVersion = 14;

    /// <summary>
    /// A road with a crossing kind chosen by hand
    /// (block <c>crossings</c> on the road; R06). Roads without it stay in their older schema.
    /// </summary>
    public const int RoadCrossingsSchemaVersion = 15;

    /// <summary>
    /// The newest schema this build writes and reads: a text label bound to a river, road or hex
    /// path (block <c>path</c> on the text; R07). Maps without such labels stay in their older schema.
    /// </summary>
    public const int TextPathsSchemaVersion = 16;

    /// <summary>
    /// Cartographic decorations - scale bar, compass rose, cartouche, live legend - and the map scale
    /// on the canvas (R08). Maps without them stay older.
    /// </summary>
    public const int DecorationsSchemaVersion = 17;

    /// <summary>
    /// The newest schema this build writes and reads: several points of interest per hex and nested
    /// regions (R16). Maps without them stay older.
    /// </summary>
    public const int HexStructureSchemaVersion = 18;

    /// <summary>
    /// Rules of an editable area - not on water, only
    /// on water, not on roads, only on land - and the places they keep empty (R10). Areas without rules stay older.
    /// </summary>
    public const int AreaRulesSchemaVersion = 19;


    /// <summary>
    /// The newest schema this build writes and reads: the hex game layer - note reveal rules, the party
    /// marker with its route, rumours and approximate labels (R17). Maps without them stay older.
    /// </summary>
    public const int GameplaySchemaVersion = 20;

    public const int TravelSchemaVersion = 21;

    /// <summary>
    /// The newest schema this build writes and reads: painted brush strokes inside river water
    /// (`PaintStrokes` of the river style, the owner's painted look). Rivers without them stay older.
    /// Number 21 is R18 travel.
    /// </summary>
    public const int PaintedRiversSchemaVersion = 22;

    /// <summary>
    /// Painted roads (`PaintStrokes` of a road or branch style, the owner's painted look). Roads
    /// without them keep the older schema.
    /// </summary>
    public const int PaintedRoadsSchemaVersion = 23;

    public const int TileSetsSchemaVersion = 24;

    /// <summary>
    /// «Живая река»: the natural bank manner of a river (`BankManner` of the river style). Rivers
    /// without it keep the older schema.
    /// </summary>
    public const int NaturalRiversSchemaVersion = 25;

    public const int AtlasSchemaVersion = 26;

    /// <summary>
    /// Light and time of day of the map (DEC-057, R09): one manifest block. Documents without it
    /// keep the older schema. v27 was reserved by R10 and stays unused: R10 took v30 when merged.
    /// </summary>
    public const int LightingSchemaVersion = 28;

    /// <summary>The style of the map (DEC-059, R09): one manifest block. Documents without it keep the older schema.</summary>
    public const int StyleSchemaVersion = 29;

    /// <summary>
    /// R10 «Красота»: the beauty settings and the density brush of an editable area, «ближние поверх
    /// дальних» of a whole object layer, and the tint and ground of a placed asset. Maps without them
    /// keep the older schema. Written in the R10 branch as v27, renumbered to v30 on merge (after R09's v28/v29).
    /// </summary>
    public const int AreaBeautySchemaVersion = 30;

    public const int GenerationSchemaVersion = 31;

    /// <summary>
    /// R12 «Вода и обрывы»: the light angle and the mask of the cliffs, river node features
    /// (waterfall, rapids, ford) and the water style of the sea. Maps without them keep the older schema.
    /// </summary>
    public const int WaterCliffsSchemaVersion = 32;

    /// <summary>R14: the dim zone of a light source (manifest lighting). Maps without it keep the older schema.</summary>
    public const int LightZonesSchemaVersion = 33;

    /// <summary>R22: the heights of a land (relief tiles). Maps without them keep the older schema.</summary>
    public const int ReliefSchemaVersion = 34;

    /// <summary>R24: a river, road or object marked «намеренно» for the geography check.</summary>
    public const int IntentionalSchemaVersion = 35;

    /// <summary>R26: the cards of the world and the link, category and visibility of notes.</summary>
    public const int WikiSchemaVersion = 36;

    /// <summary>R07b: region labels, label importance and the map's label settings.</summary>
    public const int RegionLabelsSchemaVersion = 37;

    /// <summary>R27: the fog of war of a painted map.</summary>
    public const int FogSchemaVersion = 38;

    /// <summary>R29: transitions between levels and maps.</summary>
    public const int TransitionsSchemaVersion = 39;

    /// <summary>R31: eras of objects and roads, morph keys, political roles and the timeline.</summary>
    public const int TimelineSchemaVersion = 40;

    /// <summary>R32: the pointcrawl over the map.</summary>
    public const int PointcrawlSchemaVersion = 41;

    /// <summary>R39: the planet flag (two to one, the seamless meridian).</summary>
    public const int PlanetSchemaVersion = 42;

    /// <summary>R33: star systems in the hexes of a sector.</summary>
    public const int StarSectorSchemaVersion = 43;
    public const int MaxSupportedSchemaVersion = StarSectorSchemaVersion;

    /// <summary>
    /// Schemas v5–v13 share the v4 container and differ only by the constructs they may carry
    /// (paths, areas, rivers, roads, land painting, a material on the page colour, a map category
    /// other than the standard one, hex layers, the hex workshop). This reader accepts all of them.
    /// </summary>
    public static bool IsExtendedV4Schema(int schemaVersion) =>
        schemaVersion is >= EditablePathsSchemaVersion and <= MaxSupportedSchemaVersion;

    public const string ManifestEntry = "manifest.json";
    public const string SceneEntry = "scene.json";
    public const string AssetManifestEntry = "asset-manifest.json";
    public const string PreviewEntry = "preview.png";
    public const string RasterPrefix = "rasters/";
    public const string LandPaintPrefix = "rasters/land-paint/";
    public const string CliffWallPrefix = "rasters/cliff-wall/";
    public const string CliffBeachPrefix = "rasters/cliff-beach/";
    public const string ReliefPrefix = "rasters/relief/";
    public const string MaskPrefix = "masks/";
    public const string AssetPrefix = "assets/";
    public const string ObjectPrefix = "objects/";
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
    public const long MaxContainerFileBytes = 1_280L * 1024 * 1024;

    public const long MaxCentralDirectoryBytes = 16L * 1024 * 1024;

    public const int MaxEntryNameBytes = 512;

    public const long MaxEntryNamePayloadBytes = 2L * 1024 * 1024;

    /// <summary>Structural JSON only; object data has its own streaming envelope.</summary>
    public const long MaxJsonEntryBytes = 32L * 1024 * 1024;
    public const long MaxStructuralJsonPayloadBytes = 64L * 1024 * 1024;
    public const long MaxObjectChunkPayloadBytes = 960L * 1024 * 1024;
    /// <summary>Read-only legacy scene admission, never a canonical writer allowance.</summary>
    public const long MaxLegacySceneEntryBytes = 512L * 1024 * 1024;
    public const long MaxMigrationPayloadBytes = 768L * 1024 * 1024;

    /// <summary>Canonical v4 object chunks are bounded independently of legacy JSON entries.</summary>
    public const int ObjectChunkTargetObjects = 8_192;

    public const long ObjectChunkTargetBytes = 8L * 1024 * 1024;

    public const int ObjectChunkRepresentationVersion = 1;

    /// <summary>One raw tile is fixed-size and bounded (256x256 RGBA at most).</summary>
    public const long MaxTileEntryBytes = 256L * 256 * 4;

    public const long MaxAssetEntryBytes = 64L * 1024 * 1024;

    /// <summary>Upper bound of the stored preview image bytes.</summary>
    public const long MaxPreviewBytes = 8L * 1024 * 1024;

    /// <summary>Upper bound of the whole container once decompressed.</summary>
    public const long MaxAssetPayloadBytes = 512L * 1024 * 1024;

    public const long MaxTilePayloadBytes = 1_024L * 1024 * 1024;

    public const long MaxJsonPayloadBytes = 1_024L * 1024 * 1024;

    public const long MaxTotalUncompressedBytes = 2_048L * 1024 * 1024;

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
