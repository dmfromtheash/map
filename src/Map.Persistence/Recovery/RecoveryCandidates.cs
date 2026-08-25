using Map.Core.Documents;

namespace Map.Persistence.Recovery;

public enum RecoveryCandidateKind
{
    InterruptedSave,
    Autosave,
}

/// <summary>An interrupted save whose durable temp validated as a complete .mapproj.</summary>
public sealed record RecoveryCandidate
{
    public required Guid DocumentId { get; init; }
    public required string TargetPath { get; init; }
    public required string CandidatePath { get; init; }
    public required DateTimeOffset UpdatedUtc { get; init; }
    public required string Title { get; init; }
    public RecoveryCandidateKind Kind { get; init; } = RecoveryCandidateKind.InterruptedSave;
    public long Revision { get; init; }
}

/// <summary>
/// Finds, restores and discards recovery candidates. Restoring only READS the candidate: the old
/// target file is never overwritten until the user explicitly saves.
/// </summary>
public static class RecoveryCandidates
{
    /// <summary>
    /// Journals in a recoverable state whose temp file still exists AND validates as a complete
    /// container through the full reader. A journal with an unknown state, a missing temp or an
    /// unreadable candidate is skipped silently here - it stays on disk for diagnostics, but it
    /// is never offered as the user's work.
    /// </summary>
    public static IReadOnlyList<RecoveryCandidate> Find(RecoveryJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var candidates = FindInterruptedSaves(journal)
            .Concat(AutosaveSnapshots.Find(journal))
            .GroupBy(candidate => candidate.DocumentId)
            .Select(group => group
                .OrderByDescending(candidate => candidate.UpdatedUtc)
                .ThenByDescending(candidate => candidate.Kind == RecoveryCandidateKind.InterruptedSave)
                .First())
            .OrderByDescending(candidate => candidate.UpdatedUtc)
            .ToList();

        return candidates;
    }

    private static IReadOnlyList<RecoveryCandidate> FindInterruptedSaves(RecoveryJournal journal)
    {
        var candidates = new List<RecoveryCandidate>();
        foreach (var documentId in journal.ListDocumentIds())
        {
            var record = journal.TryRead(documentId);
            if (record is null || !record.HasRecoverableWork)
            {
                continue;
            }

            OpenedProject validated;
            try
            {
                validated = MapProjectStore.Open(record.TempPath);
            }
            catch (MapProjectException)
            {
                continue; // a half-written or damaged temp is not offered
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (validated.Document.Id.Value != documentId)
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(record.UpdatedUtc, out var updatedUtc))
            {
                updatedUtc = DateTimeOffset.MinValue;
            }

            candidates.Add(new RecoveryCandidate
            {
                DocumentId = documentId,
                TargetPath = record.TargetPath,
                CandidatePath = record.TempPath,
                UpdatedUtc = updatedUtc,
                Title = validated.Document.Title,
                Kind = RecoveryCandidateKind.InterruptedSave,
                Revision = record.BaseSavedRevision,
            });
        }

        return candidates;
    }

    /// <summary>Opens the candidate content. The target file on disk is not touched.</summary>
    public static OpenedProject Restore(RecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return MapProjectStore.Open(candidate.CandidatePath);
    }

    /// <summary>
    /// Explicitly discards one candidate: deletes exactly the temp file the journal recorded and
    /// the journal directory of that document - nothing else.
    /// </summary>
    public static void Discard(RecoveryJournal journal, RecoveryCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(candidate);

        if (File.Exists(candidate.CandidatePath))
        {
            File.Delete(candidate.CandidatePath);
        }

        journal.Clear(candidate.DocumentId);
    }
}
