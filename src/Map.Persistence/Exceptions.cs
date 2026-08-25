namespace Map.Persistence;

/// <summary>Base of every persistence failure this library reports deliberately.</summary>
public abstract class MapProjectException : Exception
{
    protected MapProjectException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The container is damaged or is not a map project: a required entry is missing, a checksum does
/// not match, JSON does not parse, or the identifiers inside disagree with each other. The message
/// names what exactly failed; the raw ZIP/JSON exception, when there was one, is the inner one.
/// </summary>
public sealed class MapProjectCorruptedException : MapProjectException
{
    public MapProjectCorruptedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The container is a well-formed map project written by a schema this build does not support.
/// Opening never migrates and never rewrites the source file (docs/05).
/// </summary>
public sealed class UnsupportedSchemaVersionException : MapProjectException
{
    public UnsupportedSchemaVersionException(int schemaVersion)
        : base($"Schema version {schemaVersion} is not supported by this build " +
               $"(expected {MapProjectFormat.SchemaVersion}). The file was not modified.")
    {
        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }
}

/// <summary>
/// A save did not complete. The target file was not replaced, so the last correct version is
/// still on disk; a durable temp, when one was written, is named so nothing is silently lost.
/// </summary>
public sealed class MapProjectSaveException : MapProjectException
{
    public MapProjectSaveException(string message, string targetPath, string? tempPath, Exception? inner = null)
        : base(message, inner)
    {
        TargetPath = targetPath;
        TempPath = tempPath;
    }

    public string TargetPath { get; }

    /// <summary>The sibling temp file left on disk, or null when none survived.</summary>
    public string? TempPath { get; }
}
