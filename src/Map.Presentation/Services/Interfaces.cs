using Map.Core.Documents;
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

    Task ShowErrorAsync(string title, string message);
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
    OpenedProject Open(string path);

    void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng);

    IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates();

    OpenedProject RestoreRecovery(RecoveryCandidate candidate);

    void DiscardRecovery(RecoveryCandidate candidate);
}

/// <summary>Production store: the real .mapproj pipeline with the production recovery journal.</summary>
public sealed class ProjectStore(RecoveryJournal journal) : IProjectStore
{
    public OpenedProject Open(string path) => MapProjectStore.Open(path);

    public void Save(MapDocument document, string path, SaveMetadata metadata, byte[] previewPng) =>
        MapProjectStore.Save(document, path, metadata, previewPng, journal);

    public IReadOnlyList<RecoveryCandidate> FindRecoveryCandidates() => RecoveryCandidates.Find(journal);

    public OpenedProject RestoreRecovery(RecoveryCandidate candidate) => RecoveryCandidates.Restore(candidate);

    public void DiscardRecovery(RecoveryCandidate candidate) => RecoveryCandidates.Discard(journal, candidate);
}
