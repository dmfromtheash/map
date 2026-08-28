namespace Map.Persistence;

/// <summary>Base of every persistence failure this library reports deliberately.</summary>
public abstract class MapProjectException(string message, Exception? inner = null) : Exception(message, inner)
{
}

/// <summary>
/// The container is damaged or is not a map project: a required entry is missing, a checksum does
/// not match, JSON does not parse, or the identifiers inside disagree with each other. The message
/// names what exactly failed; the raw ZIP/JSON exception, when there was one, is the inner one.
/// </summary>
public sealed class MapProjectCorruptedException(string message, Exception? inner = null) : MapProjectException(message, inner)
{
}

/// <summary>
/// The container may be structurally valid, but exceeds this build's bounded interactive read
/// envelope. It is never rewritten and must not be reported to the user as damaged data.
/// </summary>
public sealed class MapProjectResourceLimitException(string message) : MapProjectException(message)
{
}

/// <summary>A materialized raster or mask tile is missing, malformed or fails its checksum.</summary>
public sealed class MapProjectTileCorruptedException(string entryName, string layerId, int tileX, int tileY, string reason) : MapProjectException($"Tile {tileX}_{tileY} of layer {layerId} ({entryName}) {reason}.")
{
    public string EntryName { get; } = entryName;
    public string LayerId { get; } = layerId;
    public int TileX { get; } = tileX;
    public int TileY { get; } = tileY;
}

/// <summary>
/// The container is a well-formed map project written by a schema this build does not support.
/// Opening never migrates and never rewrites the source file (docs/05).
/// </summary>
public sealed class UnsupportedSchemaVersionException(int schemaVersion) : MapProjectException($"Schema version {schemaVersion} is not supported by this build " +
               $"(expected {MapProjectFormat.SchemaVersion}). The file was not modified.")
{
    public int SchemaVersion { get; } = schemaVersion;
}

/// <summary>
/// A save did not complete. The target file was not replaced, so the last correct version is
/// still on disk; a durable temp, when one was written, is named so nothing is silently lost.
/// </summary>
public sealed class MapProjectSaveException(string message, string targetPath, string? tempPath, Exception? inner = null) : MapProjectException(message, inner)
{
    public string TargetPath { get; } = targetPath;

    /// <summary>The sibling temp file left on disk, or null when none survived.</summary>
    public string? TempPath { get; } = tempPath;
}
