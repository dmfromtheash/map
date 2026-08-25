using Map.Spike.Core.Documents;

namespace Map.Spike.Core.Commands;

/// <summary>
/// Every edit goes through one of these (AGENTS.md invariant). Apply/Revert must be exact
/// inverses on the same document instance.
/// </summary>
public interface IEditCommand
{
    string Name { get; }

    /// <summary>Approximate retained bytes, used for the undo memory budget report.</summary>
    long ApproximateMemoryBytes { get; }

    void Apply(SpikeDocument document);

    void Revert(SpikeDocument document);
}
