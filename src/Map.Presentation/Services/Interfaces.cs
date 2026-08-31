using Map.Core.Documents;
using Map.Export;
using Map.Persistence;
using Map.Persistence.Recovery;

namespace Map.Presentation.Services;

/// <summary>What the user chose when closing a dirty document.</summary>
public enum CloseChoice
{
    Save,
    DontSave,
    Cancel,
}

/// <summary>File pickers. Implemented over Avalonia StorageProvider in the app; faked in tests.</summary>
public interface IFilePicker
{
    Task<string?> PickOpenAsync();

    Task<string?> PickSaveAsync(string suggestedFileName);
}

/// <summary>Blocking user decisions. Implemented as dialogs in the app; faked in tests.</summary>
public interface IUserPrompts
{
    Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle);

    Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName);

    Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName);

    Task<bool> ConfirmMigrationCopyAsync();

    Task ShowErrorAsync(string title, string message);
}

/// <summary>Export destinations are separate from .mapproj save/open dialogs.</summary>
public interface IExportDestinationPicker
{
    Task<string?> PickImageAsync(string suggestedFileName, ExportImageFormat format);

    Task<string?> PickTilePackageAsync(string suggestedFolderName);
}

/// <summary>Opens the local result folder after a successful export.</summary>
public interface IFolderLauncher
{
    void OpenContainingFolder(string resultPath, ExportOutputMode outputMode);
}

/// <summary>Local diagnostics sink (docs/04: no telemetry, no document content).</summary>
public interface IAppLog
{
    void Info(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>One dashboard card of the local recent index.</summary>
public sealed record RecentMapItem(
    string Path, string Title, DateTimeOffset ModifiedUtc, bool IsAvailable);

/// <summary>Local bounded recent index. Implemented in Map.Platform.Windows; faked in tests.</summary>
public interface IRecentMaps
{
    IReadOnlyList<RecentMapItem> Load();

    void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc);

    void Remove(string path);
}

/// <summary>Persistence facade so view-models can be tested without touching the file system.</summary>
public interface IProjectStore
{
    ProjectFileInspection Inspect(string path);

    ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath);

    OpenedProject Open(string path);

    void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng);

    void SaveAutosave(
        MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng);

    void ClearRecovery(Guid documentId);

    IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates();

    OpenedProject RestoreRecovery(RecoveryCandidate candidate);

    void DiscardRecovery(RecoveryCandidate candidate);
}

/// <summary>Production store: the real .mapproj pipeline with the production recovery journal.</summary>
public sealed class ProjectStore(RecoveryJournal journal) : IProjectStore
{
    public ProjectFileInspection Inspect(string path) => MapProjectStore.Inspect(path);

    public ProjectMigrationResult MigrateCopy(string sourcePath, string destinationPath) =>
        MapProjectStore.MigrateCopy(sourcePath, destinationPath);

    public OpenedProject Open(string path) => MapProjectStore.Open(path);

    public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
        MapProjectStore.Save(document, path, metadata, previewPng, journal);

    public void SaveAutosave(
        MapDocument document, string? targetPath, SaveMetadata metadata, byte[] previewPng) =>
        AutosaveSnapshots.Save(journal, document, targetPath, metadata, previewPng);

    public void ClearRecovery(Guid documentId) => journal.Clear(documentId);

    public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => RecoveryCandidates.Find(journal);

    public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => RecoveryCandidates.Restore(candidate);

    public void DiscardRecovery(RecoveryCandidate candidate) => RecoveryCandidates.Discard(journal, candidate);
}
