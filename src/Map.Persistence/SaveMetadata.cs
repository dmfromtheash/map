namespace Map.Persistence;

/// <summary>
/// Everything time- and version-shaped a save needs, passed in explicitly. Persistence has no
/// clock of its own, which is what makes serialization of the same snapshot deterministic.
/// </summary>
public sealed record SaveMetadata
{
    public SaveMetadata(string appVersion, DateTimeOffset modifiedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        AppVersion = appVersion;
        ModifiedUtc = modifiedUtc;
    }

    /// <summary>Version of the application that wrote the file, e.g. "0.1.0".</summary>
    public string AppVersion { get; }

    /// <summary>Moment this revision was saved. Recorded verbatim in the manifest.</summary>
    public DateTimeOffset ModifiedUtc { get; }
}
