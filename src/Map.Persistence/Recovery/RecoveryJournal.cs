using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Map.Persistence.Recovery;

public enum RecoveryState
{
    /// <summary>A save started; nothing durable exists yet.</summary>
    Started,

    /// <summary>The temp file is fully written, flushed and validated. It is a usable recovery source.</summary>
    TempDurable,

    /// <summary>The atomic replace succeeded; the target file is the new truth.</summary>
    Replaced,
}

public sealed class RecoveryRecord
{
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("targetPath")] public string TargetPath { get; set; } = "";
    [JsonPropertyName("tempPath")] public string TempPath { get; set; } = "";
    [JsonPropertyName("baseSavedRevision")] public long BaseSavedRevision { get; set; }
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = nameof(RecoveryState.Started);

    /// <summary>SHA-256 of the target as it was before this save began, or empty for a new file.</summary>
    [JsonPropertyName("baseTargetSha256")] public string BaseTargetSha256 { get; set; } = "";

    /// <summary>
    /// The state as a member this version actually defines, or null when it does not recognise
    /// the recorded value (a journal written by a newer version, or a damaged one).
    ///
    /// TryParse alone is not enough: a numeric string such as "999" parses into an enum value no
    /// member defines, and treating it as a real state would invent a recovery decision from
    /// garbage - hence the additional IsDefined check. Unknown never throws: journals are read at
    /// startup, and startup must not die on a file a newer version left behind. (Stage 1 Fix 02.)
    /// </summary>
    [JsonIgnore]
    public RecoveryState? StateValue =>
        Enum.TryParse<RecoveryState>(State, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;

    /// <summary>
    /// True when an interrupted save left durable content the target does not have. Never inferred
    /// from the temp merely existing, and never true for a state this version does not recognise.
    /// </summary>
    [JsonIgnore]
    public bool HasRecoverableWork =>
        StateValue == RecoveryState.TempDurable && File.Exists(TempPath);
}

/// <summary>
/// Crash journal kept OUTSIDE the document directory (docs/04) so a read-only or removable
/// document location cannot make recovery impossible. One directory per document id.
/// </summary>
public sealed class RecoveryJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _rootDirectory;
    private readonly int _candidateBudget;

    public RecoveryJournal(string? rootDirectory = null, int candidateBudget = 12)
    {
        _rootDirectory = rootDirectory ?? DefaultRoot();
        _candidateBudget = Math.Clamp(candidateBudget, 1, 50);
    }

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
        Guid documentId, string targetPath, string tempPath, long baseSavedRevision, string baseTargetSha256)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var record = new RecoveryRecord
        {
            DocumentId = documentId.ToString("D"),
            TargetPath = targetPath,
            TempPath = tempPath,
            BaseSavedRevision = baseSavedRevision,
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
            // A torn journal carries no usable paths; null is the honest answer.
            return null;
        }
    }

    /// <summary>Every document id that currently has a journal directory.</summary>
    public IReadOnlyList<Guid> ListDocumentIds()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var directory in Directory.GetDirectories(_rootDirectory)
                     .OrderByDescending(Directory.GetLastWriteTimeUtc)
                     .Take(_candidateBudget))
        {
            if (Guid.TryParseExact(Path.GetFileName(directory), "D", out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>Removes the journal. Called only after the save is proven durable, or on explicit discard.</summary>
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
            // A stale journal only ever causes an extra offer to recover, which the user can
            // decline. Failing the save over cleanup would be the wrong trade.
        }
    }

    private void Write(Guid documentId, RecoveryRecord record)
    {
        var directory = DirectoryFor(documentId);
        Directory.CreateDirectory(directory);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);

        // The journal is written durably: a journal alive only in the OS cache would be useless
        // in exactly the crash it exists for.
        using var stream = new FileStream(
            PathFor(documentId), FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }
}
