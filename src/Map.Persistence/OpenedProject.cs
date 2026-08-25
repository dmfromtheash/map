using Map.Core.Documents;

namespace Map.Persistence;

/// <summary>What a successful open returns: the document plus the container-level metadata.</summary>
public sealed record OpenedProject
{
    public required MapDocument Document { get; init; }
    public required string AppVersion { get; init; }
    public required DateTimeOffset ModifiedUtc { get; init; }
    public required byte[] PreviewPng { get; init; }
}
