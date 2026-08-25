using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Map.Spike.Persistence.Recovery;

public enum RecoveryState
{
    /// <summary>A save started; nothing durable exists yet.</summary>
    Started,

    /// <summary>The temp file is fully written, flushed and verified. It is a usable recovery source.</summary>
    TempDurable,

    /// <summary>The atomic replace succeeded; the target file is the new truth.</summary>
    Replaced,

    /// <summary>Cleanup finished; nothing needs recovery.</summary>
    Completed,
}

public sealed class RecoveryRecord
{
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("targetPath")] public string TargetPath { get; set; } = "";
    [JsonPropertyName("tempPath")] public string TempPath { get; set; } = "";
    [JsonPropertyName("backupPath")] public string BackupPath { get; set; } = "";
    [JsonPropertyName("baseSavedRevision")] public long BaseSavedRevision { get; set; }
    [JsonPropertyName("pendingRevision")] public long PendingRevision { get; set; }
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = nameof(RecoveryState.Started);

    /// <summary>Hash of the target file as it was before this save began, or empty for a new file.</summary>
    [JsonPropertyName("baseTargetSha256")] public string BaseTargetSha256 { get; set; } = "";

    [JsonIgnore]
    public RecoveryState StateValue => Enum.Parse<RecoveryState>(State);

    /// <summary>
    /// True when a crash left durable content that the target file does not have yet.
    /// This is what the UI must offer the user - it must never be inferred from the temp
    /// file merely existing, because a temp file may also be a half-written fragment.
    /// </summary>
    [JsonIgnore]
    public bool HasRecoverableWork =>
        StateValue == RecoveryState.TempDurable && File.Exists(TempPath);
}

/// <summary>
/// Crash journal kept OUTSIDE the document directory, in %LOCALAPPDATA%\Map\Recovery (docs/04),
/// so a read-only or removable document location cannot make recovery impossible.
/// </summary>
public sealed class RecoveryJournal(string? rootDirectory = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _rootDirectory = rootDirectory ?? DefaultRoot();

    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Map",
        "Recovery");

    public string RootDirectory => _rootDirectory;

    public string DirectoryFor(Guid documentId) =>
        Path.Combine(_rootDirectory, documentId.ToString("D"));

    public string PathFor(Guid documentId) =>
        Path.Combine(DirectoryFor(documentId), "journal.json");

    public RecoveryRecord Begin(
        Guid documentId,
        string targetPath,
        string tempPath,
        string backupPath,
        long baseSavedRevision,
        long pendingRevision,
        string baseTargetSha256)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var record = new RecoveryRecord
        {
            DocumentId = documentId.ToString("D"),
            TargetPath = targetPath,
            TempPath = tempPath,
            BackupPath = backupPath,
            BaseSavedRevision = baseSavedRevision,
            PendingRevision = pendingRevision,
            StartedUtc = now,
            UpdatedUtc = now,
            State = nameof(RecoveryState.Started),
            BaseTargetSha256 = baseTargetSha256,
        };

        Write(documentId, record);
        return record;
    }

    public void Advance(Guid documentId, RecoveryRecord record, RecoveryState state)
    {
        record.State = state.ToString();
        record.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Write(documentId, record);
    }

    public RecoveryRecord? TryRead(Guid documentId)
    {
        var path = PathFor(documentId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (JsonException)
        {
            // A torn journal is itself a recovery signal, but it carries no usable paths.
            // Surfacing null here is honest: there is nothing we can safely restore from it.
            return null;
        }
    }

    /// <summary>Removes the journal. Called only after the save is proven durable.</summary>
    public void Clear(Guid documentId)
    {
        var directory = DirectoryFor(documentId);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Leaving a stale journal behind is safe: it only ever causes an extra offer to
            // recover, which the user can decline. Deleting user data on a guess is not safe.
        }
    }

    private void Write(Guid documentId, RecoveryRecord record)
    {
        var directory = DirectoryFor(documentId);
        Directory.CreateDirectory(directory);

        var path = PathFor(documentId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);

        // The journal itself is written durably: a journal that survives only in the OS cache
        // would be useless in exactly the crash it exists for.
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
