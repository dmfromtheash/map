namespace Map.Persistence;

/// <summary>
/// Test seams of the atomic save. Production code never passes one.
/// </summary>
public sealed class SaveFaultInjection
{
    /// <summary>Invoked on the closed, flushed temp before checksum verification.</summary>
    public Action<string>? AfterFlushBeforeReadBack { get; init; }

    /// <summary>Invoked after the temp is durable and validated, immediately before the replace.</summary>
    public Action? AfterDurableTempBeforeReplace { get; init; }
}
