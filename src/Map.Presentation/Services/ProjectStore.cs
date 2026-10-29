using Map.Core.Documents;
using Map.Persistence;
using Map.Persistence.Recovery;

namespace Map.Presentation.Services;

/// <summary>Production store: the real .mapproj pipeline with the production recovery journal.</summary>
public sealed class ProjectStore(RecoveryJournal journal) : IProjectStore
{
    public ProjectFileInspection Inspect(string path)
    {
        try { AtlasStore.RecoverForMap(path); }
        catch (Exception ex) when (ex is MapProjectException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ProjectFileInspection
            {
                Status = ex is MapProjectException or InvalidDataException ? ProjectFileStatus.Corrupted : ProjectFileStatus.IoFailure,
                Code = "ATLAS_RECOVERY_FAILED", Title = "Не удалось восстановить атлас",
                Message = "Операция атласа не завершена. Проверьте доступ к папке и сохранённые копии журнала; не удаляйте их до восстановления.",
            };
        }
        return MapProjectStore.Inspect(path);
    }

    public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
        MapProjectStore.MigrateCopy(sourcePath, destinationPath);

    public OpenedProject Open(string path)
    {
        return MapProjectStore.Open(path);
    }

    public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng)
    {
        if (document.Atlas is not null) AtlasStore.SaveMap(new(document, path, metadata, previewPng));
        else MapProjectStore.Save(document, path, metadata, previewPng, journal);
    }

    public void SaveAutosave(
        MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) =>
        AutosaveSnapshots.Save(journal, document, targetPath, metadata, previewPng);

    public void ClearRecovery(Guid documentId) => journal.Clear(documentId);

    public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => RecoveryCandidates.Find(journal);

    public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => RecoveryCandidates.Restore(candidate);

    public void DiscardRecovery(RecoveryCandidate candidate) => RecoveryCandidates.Discard(journal, candidate);
}
