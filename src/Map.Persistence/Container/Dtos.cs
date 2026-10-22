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
    /// <summary>
    /// The map category (workshop) token; absent for the standard constructor, so every file
    /// written before categories keeps its bytes and its meaning. Any other value needs v11.
    /// </summary>
    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }
    [JsonPropertyName("sceneSha256")] public string SceneSha256 { get; set; } = "";
    [JsonPropertyName("assetManifestSha256")] public string AssetManifestSha256 { get; set; } = "";
    [JsonPropertyName("checkpointCatalogSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CheckpointCatalogSha256 { get; set; }
    [JsonPropertyName("atlas")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Map.Core.Documents.AtlasLocator? Atlas { get; set; }
    [JsonPropertyName("lighting")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LightingDto? Lighting { get; set; }
    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapStyleDto? Style { get; set; }
}

/// <summary>The style of the map (schema v29, DEC-059).</summary>
internal sealed class MapStyleDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("preset")] public string Preset { get; set; } = "";
    [JsonPropertyName("strength")] public double Strength { get; set; }
    [JsonPropertyName("strokes")] public LightingEffectDto? Strokes { get; set; }
    [JsonPropertyName("bleed")] public LightingEffectDto? Bleed { get; set; }
    [JsonPropertyName("edges")] public LightingEffectDto? Edges { get; set; }
    [JsonPropertyName("dryBrush")] public LightingEffectDto? DryBrush { get; set; }
    [JsonPropertyName("mist")] public LightingEffectDto? Mist { get; set; }
    [JsonPropertyName("pen")] public LightingEffectDto? Pen { get; set; }
    [JsonPropertyName("paper")] public LightingEffectDto? Paper { get; set; }
    [JsonPropertyName("contour")] public LightingEffectDto? Contour { get; set; }
    [JsonPropertyName("sea")] public LightingEffectDto? Sea { get; set; }
    [JsonPropertyName("coast")] public LightingEffectDto? Coast { get; set; }
    [JsonPropertyName("colourWash")] public LightingEffectDto? ColourWash { get; set; }
    [JsonPropertyName("aquatint")] public LightingEffectDto? Aquatint { get; set; }
    [JsonPropertyName("granulation")] public LightingEffectDto? Granulation { get; set; }
    [JsonPropertyName("crest")] public LightingEffectDto? Crest { get; set; }
    [JsonPropertyName("gouge")] public LightingEffectDto? Gouge { get; set; }
    [JsonPropertyName("seaCuts")] public LightingEffectDto? SeaCuts { get; set; }
    [JsonPropertyName("woodGrain")] public LightingEffectDto? WoodGrain { get; set; }
    [JsonPropertyName("inkSqueeze")] public LightingEffectDto? InkSqueeze { get; set; }
    [JsonPropertyName("starved")] public LightingEffectDto? Starved { get; set; }
    [JsonPropertyName("waterLines")] public LightingEffectDto? WaterLines { get; set; }
    [JsonPropertyName("stoneCrayon")] public LightingEffectDto? StoneCrayon { get; set; }
    [JsonPropertyName("tintPlate")] public LightingEffectDto? TintPlate { get; set; }
    [JsonPropertyName("scraped")] public LightingEffectDto? Scraped { get; set; }
    [JsonPropertyName("impasto")] public LightingEffectDto? Impasto { get; set; }
    [JsonPropertyName("imprimatura")] public LightingEffectDto? Imprimatura { get; set; }
    [JsonPropertyName("gloss")] public LightingEffectDto? Gloss { get; set; }
    [JsonPropertyName("smudge")] public LightingEffectDto? Smudge { get; set; }
    [JsonPropertyName("crossHatch")] public LightingEffectDto? CrossHatch { get; set; }
    [JsonPropertyName("grid")] public LightingEffectDto? Grid { get; set; }
    [JsonPropertyName("formLines")] public LightingEffectDto? FormLines { get; set; }
    [JsonPropertyName("plate")] public LightingEffectDto? Plate { get; set; }
    [JsonPropertyName("border")] public LightingEffectDto? Border { get; set; }
}

/// <summary>Light and time of day of the map (schema v28, DEC-057).</summary>
internal sealed class LightingDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("timeOfDay")] public double TimeOfDay { get; set; }
    [JsonPropertyName("relief")] public LightingEffectDto? Relief { get; set; }
    [JsonPropertyName("shadows")] public LightingEffectDto? Shadows { get; set; }
    [JsonPropertyName("fog")] public LightingEffectDto? Fog { get; set; }
    [JsonPropertyName("lights")] public LightingEffectDto? Lights { get; set; }
    [JsonPropertyName("color")] public LightingEffectDto? Color { get; set; }
    [JsonPropertyName("sources")] public List<LightSourceDto>? Sources { get; set; }
}

internal sealed class LightingEffectDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("strength")] public double Strength { get; set; }
}

internal sealed class LightSourceDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("color")] public string Color { get; set; } = "#ffb866";
    [JsonPropertyName("radius")] public double Radius { get; set; }
    [JsonPropertyName("intensity")] public double Intensity { get; set; }
    [JsonPropertyName("flicker")] public bool Flicker { get; set; }
}

internal sealed class CanvasDto
{
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("background")] public string Background { get; set; } = "";

    /// <summary>Real-world size of one pixel (R08, schema v17); absent = pixels only.</summary>
    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapScaleDto? Scale { get; set; }
}

internal sealed class MapScaleDto
{
    [JsonPropertyName("distancePerPixel")] public double DistancePerPixel { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
}

/// <summary>Cartographic decoration (R08, schema v17): scale bar, compass, cartouche, legend.</summary>
internal sealed class DecorationStyleDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("variant")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Variant { get; set; }
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Title { get; set; }
    [JsonPropertyName("subtitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Subtitle { get; set; }
    [JsonPropertyName("inkColor")] public string InkColor { get; set; } = "";
    [JsonPropertyName("paperColor")] public string PaperColor { get; set; } = "";
    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapScaleDto? Scale { get; set; }
    [JsonPropertyName("entries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegendEntryDto>? Entries { get; set; }
}

internal sealed class LegendEntryDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "";
    [JsonPropertyName("asset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssetSha256 { get; set; }
}

/// <summary>scene.json — layer metadata and v4 object-chunk descriptors.</summary>
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

internal sealed class BaseMaterialDto
{
    [JsonPropertyName("textureId")]
    public string? TextureId { get; set; }
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; }
    [JsonPropertyName("scale")]
    public double Scale { get; set; }
}

internal sealed class LayerDto
{
    [JsonPropertyName("hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HexBoardDto? Hex { get; set; }
    [JsonPropertyName("baseMaterial")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BaseMaterialDto? BaseMaterial { get; set; }
    [JsonPropertyName("placementPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Map.Core.Documents.PlacementPath>? PlacementPaths { get; set; }
    [JsonPropertyName("placementAreas")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Map.Core.Documents.PlacementArea>? PlacementAreas { get; set; }
    [JsonPropertyName("rivers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Map.Core.Documents.River>? Rivers { get; set; }
    [JsonPropertyName("roads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Map.Core.Documents.Road>? Roads { get; set; }
    [JsonPropertyName("surfaceOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Guid>? SurfaceOrder { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; }
    [JsonPropertyName("blend")] public string Blend { get; set; } = "Normal";
    [JsonPropertyName("level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Level { get; set; }
    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }
    [JsonPropertyName("objects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SceneObjectDto>? Objects { get; set; }
    [JsonPropertyName("objectChunks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ObjectChunkDescriptorDto>? ObjectChunks { get; set; }
    [JsonPropertyName("tiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TileDto>? Tiles { get; set; }
    [JsonPropertyName("landPaintTiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TileDto>? LandPaintTiles { get; set; }
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
    [JsonPropertyName("effects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerEffectsDto? Effects { get; set; }
    [JsonPropertyName("sublayers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ObjectSublayerStateDto>? Sublayers { get; set; }
}

/// <summary>
/// The hexes of a hex layer (schema v12). Cells are stored in board offset coordinates as one flat
/// array of six integers per painted hex - column, row, terrain index, variant, feature index
/// (-1 for none), feature variant - with tile ids in a palette, so a 100 000-hex board stays compact.
/// </summary>
internal sealed class HexBoardDto
{
    [JsonPropertyName("orientation")] public string Orientation { get; set; } = "flat";
    [JsonPropertyName("size")] public double Size { get; set; }
    [JsonPropertyName("originX")] public double OriginX { get; set; }
    [JsonPropertyName("originY")] public double OriginY { get; set; }
    [JsonPropertyName("columns")] public int Columns { get; set; }
    [JsonPropertyName("rows")] public int Rows { get; set; }
    [JsonPropertyName("tiles")] public List<string> Tiles { get; set; } = [];
    /// <summary>Ints per hex in <see cref="Cells"/>: 6 (v12), or 9 with rotation, region and revealed (v13).</summary>
    [JsonPropertyName("stride")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Stride { get; set; }
    [JsonPropertyName("cells")] public List<int> Cells { get; set; } = [];
    [JsonPropertyName("gameplay")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GameplayDto? Gameplay { get; set; }
    [JsonPropertyName("journey")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TravelV21Dto? Journey { get; set; }
    [JsonPropertyName("tileSets")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? TileSets { get; set; }
    [JsonPropertyName("pointsOfInterest")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<HexPoiCellDto>? Points { get; set; }
    [JsonPropertyName("grid")] public HexGridDto? Grid { get; set; }
    [JsonPropertyName("paths")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<HexPathDto>? Paths { get; set; }
    [JsonPropertyName("hexRegions")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<HexRegionDto>? Regions { get; set; }
    [JsonPropertyName("hexKey")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<HexNoteDto>? Notes { get; set; }
    [JsonPropertyName("level")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Level { get; set; }
    [JsonPropertyName("fog")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Fog { get; set; }
    [JsonPropertyName("travel")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public HexTravelDto? Travel { get; set; }
}

internal sealed class HexPathDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("style")] public string Style { get; set; } = "";
    /// <summary>Offset column, row per hex - or column, row, corner per corner of an edge path.</summary>
    [JsonPropertyName("nodes")] public List<int>? Nodes { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; } = 1;
    [JsonPropertyName("color")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Color { get; set; }
    [JsonPropertyName("name")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("seed")] public int Seed { get; set; }
    [JsonPropertyName("blocksSight")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BlocksSight { get; set; }
    [JsonPropertyName("blocksMove")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BlocksMove { get; set; }
    [JsonPropertyName("blocksLight")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BlocksLight { get; set; }
}

internal sealed class HexPoiCellDto
{
    [JsonPropertyName("column")] public int Column { get; set; }
    [JsonPropertyName("row")] public int Row { get; set; }
    [JsonPropertyName("points")] public List<HexPoiDto> Points { get; set; } = [];
}

internal sealed class HexPoiDto
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("playerVisible")] public bool PlayerVisible { get; set; } = true;
    [JsonPropertyName("variant")] public int Variant { get; set; }
}

internal sealed class HexRegionDto
{
    [JsonPropertyName("parentRegion")] public int ParentRegion { get; set; }
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("color")] public string Color { get; set; } = "";
}

internal sealed class HexNoteDto
{
    [JsonPropertyName("column")] public int Column { get; set; }
    [JsonPropertyName("row")] public int Row { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("gm")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? GmText { get; set; }
    [JsonPropertyName("player")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PlayerText { get; set; }
    [JsonPropertyName("childMap")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ChildMap { get; set; }
}

internal sealed class HexTravelDto
{
    [JsonPropertyName("perHex")] public double DistancePerHex { get; set; } = 6;
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    [JsonPropertyName("perDay")] public double DistancePerDay { get; set; } = 24;
}

internal sealed class HexGridDto
{
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("color")] public string Color { get; set; } = "#66201810";
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("numbers")] public bool Numbers { get; set; }
    [JsonPropertyName("showEmpty")] public bool ShowEmpty { get; set; }
}

internal sealed class LayerEffectsDto
{
    [JsonPropertyName("clippingEnabled")] public bool ClippingEnabled { get; set; }
    [JsonPropertyName("shadowColor")] public string ShadowColor { get; set; } = "#FF000000";
    [JsonPropertyName("outerShadowOpacity")] public double OuterShadowOpacity { get; set; }
    [JsonPropertyName("innerShadowOpacity")] public double InnerShadowOpacity { get; set; }
    [JsonPropertyName("shadowOffsetX")] public double ShadowOffsetX { get; set; }
    [JsonPropertyName("shadowOffsetY")] public double ShadowOffsetY { get; set; }
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; }
    [JsonPropertyName("depthSort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DepthSort { get; set; }
}

internal sealed class ObjectSublayerStateDto
{
    [JsonPropertyName("sublayer")] public int Sublayer { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
    [JsonPropertyName("locked")] public bool Locked { get; set; }
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
    // Colour pass (2026-10-16). Absent in older files; written only when it changes pixels.
    [JsonPropertyName("gradientMap")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FilterGradientMapDto? GradientMap { get; set; }
    [JsonPropertyName("postProcess")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FilterPostProcessDto? PostProcess { get; set; }
    [JsonPropertyName("colorStrength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ColorStrength { get; set; }
    [JsonPropertyName("colorPresetId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorPresetId { get; set; }
}

internal sealed class FilterGradientMapDto
{
    [JsonPropertyName("shadow")] public string Shadow { get; set; } = "";
    [JsonPropertyName("midtone")] public string Midtone { get; set; } = "";
    [JsonPropertyName("highlight")] public string Highlight { get; set; } = "";
    [JsonPropertyName("strength")] public double Strength { get; set; }
}

internal sealed class FilterPostProcessDto
{
    [JsonPropertyName("saturation")] public double Saturation { get; set; }
    [JsonPropertyName("brightness")] public double Brightness { get; set; }
    [JsonPropertyName("contrast")] public double Contrast { get; set; }
    [JsonPropertyName("sharpness")] public double Sharpness { get; set; }
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
    [JsonPropertyName("shadowOffsetX")] public double ShadowOffsetX { get; set; }
    [JsonPropertyName("shadowOffsetY")] public double ShadowOffsetY { get; set; }
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; }
    [JsonPropertyName("rippleColor")] public string RippleColor { get; set; } = "";
    [JsonPropertyName("rippleWidth")] public double RippleWidth { get; set; }
    [JsonPropertyName("rippleSpacing")] public double RippleSpacing { get; set; }
    [JsonPropertyName("rippleCount")] public int RippleCount { get; set; }

    // Absent in every file written before ground materials existed, and absent again whenever no
    // material is chosen, so a map that does not use one is byte-identical to what it always was.
    [JsonPropertyName("fillTextureId")] public string? FillTextureId { get; set; }
    [JsonPropertyName("fillTextureOpacity")] public double? FillTextureOpacity { get; set; }
    [JsonPropertyName("fillTextureScale")] public double? FillTextureScale { get; set; }

    // Written only when the cliff look differs from the default, so an untouched land keeps the
    // bytes it had before cliffs existed; an absent object reads back as the default look.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("cliff")] public LandCliffDto? Cliff { get; set; }
}

internal sealed class LandCliffDto
{
    // Every value may be absent: a file from an older Map, or one that predates a setting,
    // reads the missing ones as the default look.
    [JsonPropertyName("height")] public double? Height { get; set; }
    [JsonPropertyName("heightVariation")] public double? HeightVariation { get; set; }
    [JsonPropertyName("variationLength")] public double? VariationLength { get; set; }
    [JsonPropertyName("gaps")] public double? Gaps { get; set; }
    [JsonPropertyName("gapLength")] public double? GapLength { get; set; }
    [JsonPropertyName("lean")] public double? Lean { get; set; }
    [JsonPropertyName("facets")] public double? Facets { get; set; }
    [JsonPropertyName("facetWidth")] public double? FacetWidth { get; set; }
    [JsonPropertyName("facetLines")] public double? FacetLines { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("shadeColor")] public string? ShadeColor { get; set; }
    [JsonPropertyName("contrast")] public double? Contrast { get; set; }
    [JsonPropertyName("wash")] public double? Wash { get; set; }
    [JsonPropertyName("rimShadow")] public double? RimShadow { get; set; }
    [JsonPropertyName("hatching")] public double? Hatching { get; set; }
    [JsonPropertyName("hatchSpacing")] public double? HatchSpacing { get; set; }
    [JsonPropertyName("hatchLength")] public double? HatchLength { get; set; }
    [JsonPropertyName("hatchSlant")] public double? HatchSlant { get; set; }
    [JsonPropertyName("stipple")] public double? Stipple { get; set; }
    [JsonPropertyName("stippleSize")] public double? StippleSize { get; set; }
    [JsonPropertyName("strata")] public int? Strata { get; set; }
    [JsonPropertyName("inkColor")] public string? InkColor { get; set; }
    [JsonPropertyName("inkWidth")] public double? InkWidth { get; set; }
    [JsonPropertyName("foot")] public int? Foot { get; set; }
    [JsonPropertyName("footRoughness")] public double? FootRoughness { get; set; }
    [JsonPropertyName("footTicks")] public double? FootTicks { get; set; }
    [JsonPropertyName("baseShadow")] public double? BaseShadow { get; set; }
    [JsonPropertyName("baseShadowLength")] public double? BaseShadowLength { get; set; }
    [JsonPropertyName("baseShadowColor")] public string? BaseShadowColor { get; set; }
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
    [JsonPropertyName("rotationDegrees")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double RotationDegrees { get; set; }
    [JsonPropertyName("fillColor")] public string FillColor { get; set; } = "";
    [JsonPropertyName("fillGradient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradientDto? FillGradient { get; set; }
    [JsonPropertyName("strokeColor")] public string StrokeColor { get; set; } = "";
    [JsonPropertyName("strokeWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double StrokeWidth { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; }
    [JsonPropertyName("locked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }
    [JsonPropertyName("sublayer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Sublayer { get; set; }
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
    [JsonPropertyName("room")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RoomStyleDto? Room { get; set; }
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextStyleDto? Text { get; set; }
    [JsonPropertyName("composition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EditableCompositionDto? Composition { get; set; }
    [JsonPropertyName("decoration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecorationStyleDto? Decoration { get; set; }
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
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextPathDto? Path { get; set; }
}

/// <summary>A label bound to a river, road or hex path (schema v16, R07).</summary>
internal sealed class TextPathDto
{
    [JsonPropertyName("target")] public string Target { get; set; } = "River";
    [JsonPropertyName("targetId")] public string TargetId { get; set; } = "";
    [JsonPropertyName("partId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartId { get; set; }
    [JsonPropertyName("position")] public double Position { get; set; } = 0.5;
    [JsonPropertyName("side")] public string Side { get; set; } = "Above";
    [JsonPropertyName("gap")] public double Gap { get; set; }
    [JsonPropertyName("keepUpright")] public bool KeepUpright { get; set; } = true;
    [JsonPropertyName("reverse")] public bool Reverse { get; set; }
    [JsonPropertyName("detached")] public bool Detached { get; set; }
    /// <summary>Derived baseline, x/y pairs relative to the label's top-left.</summary>
    [JsonPropertyName("baseline")] public double[] Baseline { get; set; } = [];
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
    [JsonPropertyName("flipHorizontal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FlipHorizontal { get; set; }
    [JsonPropertyName("flipVertical")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FlipVertical { get; set; }
    [JsonPropertyName("maskAssetSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaskAssetSha256 { get; set; }
    [JsonPropertyName("clipMaskEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ClipMaskEnabled { get; set; }
    [JsonPropertyName("blend")] public string Blend { get; set; } = "Normal";
    [JsonPropertyName("blur")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Blur { get; set; }
    [JsonPropertyName("shadowOffsetX")] public double ShadowOffsetX { get; set; } = 4;
    [JsonPropertyName("shadowOffsetY")] public double ShadowOffsetY { get; set; } = 4;
    [JsonPropertyName("shadowBlur")] public double ShadowBlur { get; set; } = 6;
    [JsonPropertyName("shadowOpacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ShadowOpacity { get; set; }
    [JsonPropertyName("shadowPlacement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShadowPlacement { get; set; }
    [JsonPropertyName("shadowColor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ShadowColor { get; set; }
    [JsonPropertyName("hue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Hue { get; set; }
    [JsonPropertyName("saturation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Saturation { get; set; }
    [JsonPropertyName("brightness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Brightness { get; set; }
    [JsonPropertyName("contrast")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Contrast { get; set; }
    [JsonPropertyName("tint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tint { get; set; }
    [JsonPropertyName("groundShadow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroundShadowDto? GroundShadow { get; set; }
}

/// <summary>R10 ground of a placed asset (schema v27).</summary>
internal sealed class GroundShadowDto
{
    [JsonPropertyName("opacity")] public double Opacity { get; set; }
    [JsonPropertyName("footX")] public double FootX { get; set; }
    [JsonPropertyName("footY")] public double FootY { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("mass")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Mass { get; set; }
}

internal sealed class ObjectChunkDescriptorDto
{
    [JsonPropertyName("ordinal")] public int Ordinal { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("byteLength")] public long ByteLength { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("bounds")] public ObjectChunkBoundsDto? Bounds { get; set; }
    [JsonPropertyName("entry")] public string Entry { get; set; } = "";
    [JsonPropertyName("representationVersion")] public int RepresentationVersion { get; set; }
}

internal sealed class ObjectChunkBoundsDto
{
    [JsonPropertyName("left")] public double Left { get; set; }
    [JsonPropertyName("top")] public double Top { get; set; }
    [JsonPropertyName("right")] public double Right { get; set; }
    [JsonPropertyName("bottom")] public double Bottom { get; set; }
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
    [JsonPropertyName("blocksLight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BlocksLight { get; set; }
}

internal sealed class RoomStyleDto
{
    [JsonPropertyName("cornerRadius")] public double CornerRadius { get; set; }
    [JsonPropertyName("capAssetSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CapAssetSha256 { get; set; }
    [JsonPropertyName("capScale")] public double CapScale { get; set; }
    [JsonPropertyName("capAngle")] public double CapAngle { get; set; }
    [JsonPropertyName("capShadow")] public bool CapShadow { get; set; }
}

internal sealed class LineStyleDto
{
    [JsonPropertyName("nodes")] public List<VectorNodeDto> Nodes { get; set; } = [];
    [JsonPropertyName("stroke")] public VectorStrokeDto Stroke { get; set; } = new();
    [JsonPropertyName("portals")] public List<LinePortalDto> Portals { get; set; } = [];
    [JsonPropertyName("closed")] public bool Closed { get; set; }
}

/// <summary>One colour stop of a stored gradient.</summary>
internal sealed class GradientStopDto
{
    [JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonPropertyName("color")] public string Color { get; set; } = "";
}

/// <summary>
/// A stored gradient. It is written only when an object actually has one, so a project made
/// before gradients existed - and any project that still uses flat colour - keeps exactly the
/// bytes it had.
/// </summary>
internal sealed class GradientDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "Linear";
    [JsonPropertyName("stops")] public List<GradientStopDto> Stops { get; set; } = [];
    [JsonPropertyName("angleDegrees")] public double AngleDegrees { get; set; }
    [JsonPropertyName("centerX")] public double CenterX { get; set; }
    [JsonPropertyName("centerY")] public double CenterY { get; set; }
    [JsonPropertyName("radius")] public double Radius { get; set; }
}

internal sealed class ShapeFillDto
{
    [JsonPropertyName("color")] public string Color { get; set; } = "";
    [JsonPropertyName("gradient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradientDto? Gradient { get; set; }
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
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("createdUtc")] public string CreatedUtc { get; set; } = "";
    [JsonPropertyName("canvas")] public CanvasDto Canvas { get; set; } = new();
    [JsonPropertyName("sceneSha256")] public string SceneSha256 { get; set; } = "";
    [JsonPropertyName("assetManifestSha256")] public string AssetManifestSha256 { get; set; } = "";
    [JsonPropertyName("previewSha256")] public string PreviewSha256 { get; set; } = "";

    /// <summary>The version's light (schema v28, optional): a named version keeps its time of day.</summary>
    [JsonPropertyName("lighting")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LightingDto? Lighting { get; set; }

    /// <summary>The version's style (schema v29, optional).</summary>
    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapStyleDto? Style { get; set; }
}
