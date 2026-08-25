namespace Map.Persistence;

/// <summary>
/// Test seam of the atomic save. Production code never passes one. The single supported boundary
/// is the docs/08 case that matters most for this slice: the temp file is fully written, flushed
/// and validated - and the target has not been replaced yet.
/// </summary>
public sealed class SaveFaultInjection
{
    /// <summary>Invoked after the temp is durable and validated, immediately before the replace.</summary>
    public Action? AfterDurableTempBeforeReplace { get; init; }
}
