using System.Text.Json.Serialization;

namespace Map.Export;

public sealed record TilePackageRegion(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record TilePackageEntry(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record TilePackageManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("sourceRegion")] TilePackageRegion SourceRegion,
    [property: JsonPropertyName("outputWidth")] int OutputWidth,
    [property: JsonPropertyName("outputHeight")] int OutputHeight,
    [property: JsonPropertyName("tileWidth")] int TileWidth,
    [property: JsonPropertyName("tileHeight")] int TileHeight,
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("rows")] int Rows,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("quality")] int Quality,
    [property: JsonPropertyName("transparentBackground")] bool TransparentBackground,
    [property: JsonPropertyName("includeBackground")] bool IncludeBackground,
    [property: JsonPropertyName("includeGrid")] bool IncludeGrid,
    [property: JsonPropertyName("tiles")] IReadOnlyList<TilePackageEntry> Tiles);
