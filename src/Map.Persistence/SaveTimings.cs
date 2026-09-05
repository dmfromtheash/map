namespace Map.Persistence;

/// <summary>Optional operation-owned timings. Write includes serialization and ZIP compression;
/// it is not an incremental-save metric or a durability claim by itself.</summary>
public sealed class SaveTimings
{
    public double PreviousFingerprintMilliseconds { get; internal set; }
    public double WriteContainerMilliseconds { get; internal set; }
    public double FlushMilliseconds { get; internal set; }
    public double ReadBackMilliseconds { get; internal set; }
    public double PublishMilliseconds { get; internal set; }
}
