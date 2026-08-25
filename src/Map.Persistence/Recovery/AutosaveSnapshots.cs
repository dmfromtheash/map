using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Map.Core.Documents;

namespace Map.Persistence.Recovery;

/// <summary>
/// Durable pointer to the newest autosave and one previous generation. The pointer is replaced
/// atomically only after a complete .mapproj has passed the normal write/read-back pipeline.
/// Filenames, never arbitrary paths, are stored for snapshots so damaged metadata cannot make
/// cleanup or recovery escape the document's recovery directory.
/// </summary>
public sealed class AutosaveRecord
{
    [JsonPropertyName("documentId")] public string DocumentId { get; set; } = "";
    [JsonPropertyName("targetPath")] public string TargetPath { get; set; } = "";
    [JsonPropertyName("snapshotFileName")] public string SnapshotFileName { get; set; } = "";
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("updatedUtc")] public string UpdatedUtc { get; set; } = "";
    [JsonPropertyName("previousSnapshotFileName")] public string PreviousSnapshotFileName { get; set; } = "";
    [JsonPropertyName("previousRevision")] public long PreviousRevision { get; set; }
    [JsonPropertyName("previousUpdatedUtc")] public string PreviousUpdatedUtc { get; set; } = "";
}

/// <summary>
/// One bounded autosave stream per document. A generation is immutable; an atomic metadata
/// pointer makes it current. A crash before the pointer swap leaves the old generation current,
/// while a crash after it leaves the new generation current.
/// </summary>
public static class AutosaveSnapshots
{
    private const string RecordFileName = "autosave.json";
    private const string RecordBackupFileName = "autosave.json.bak";
    private const string SnapshotPrefix = "autosave-";
    private const string SnapshotSuffix = MapProjectFormat.Extension;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string RecordPathFor(RecoveryJournal journal, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return Path.Combine(journal.DirectoryFor(documentId), RecordFileName);
    }

    public static AutosaveRecord Save(
        RecoveryJournal journal,
        MapDocument document,
        string? targetPath,
        SaveMetadata metadata,
        byte[] previewPng,
        SaveFaultInjection? faults = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(previewPng);

        var documentId = document.Id.Value;
        var directory = journal.DirectoryFor(documentId);
        Directory.CreateDirectory(directory);

        var previous = TryRead(journal, documentId);
        var previousGeneration = SelectPreviousGeneration(directory, documentId, previous);
        var snapshotFileName = SnapshotPrefix + Guid.NewGuid().ToString("N") + SnapshotSuffix;
        var snapshotPath = Path.Combine(directory, snapshotFileName);

        try
        {
            MapProjectStore.Save(document, snapshotPath, metadata, previewPng, faults: faults);
        }
        catch (MapProjectSaveException ex) when (
            ex.TempPath is not null && TryValidateSnapshot(ex.TempPath, documentId))
        {
            // The normal save path can fail after producing a durable, validated temp. For an
            // autosave that temp is itself a valid immutable generation, so point to it instead
            // of throwing away the only new recovery copy.
            snapshotPath = Path.GetFullPath(ex.TempPath);
            snapshotFileName = Path.GetFileName(snapshotPath);
        }
        catch (MapProjectSaveException ex)
        {
            TryDeleteOwnedGeneration(directory, ex.TempPath);
            throw;
        }

        var now = metadata.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture);
        var record = new AutosaveRecord
        {
            DocumentId = documentId.ToString("D"),
            TargetPath = targetPath ?? "",
            SnapshotFileName = snapshotFileName,
            Revision = metadata.SavedRevision,
            UpdatedUtc = now,
            PreviousSnapshotFileName = previousGeneration.FileName,
            PreviousRevision = previousGeneration.Revision,
            PreviousUpdatedUtc = previousGeneration.UpdatedUtc,
        };

        try
        {
            WriteRecordAtomically(journal, documentId, record);
        }
        catch
        {
            // The old record still points to the old valid generation. Do not accumulate one
            // unreachable snapshot on every retry while metadata replacement is unavailable.
            TryDeleteOwnedGeneration(directory, snapshotPath);
            throw;
        }

        CleanupGenerations(directory, record);
        return record;
    }

    public static AutosaveRecord? TryRead(RecoveryJournal journal, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var primary = TryReadRecord(RecordPathFor(journal, documentId), documentId);
        if (primary is not null)
        {
            return primary;
        }

        return TryReadRecord(
            Path.Combine(journal.DirectoryFor(documentId), RecordBackupFileName), documentId);
    }

    public static IReadOnlyList<RecoveryCandidate> Find(RecoveryJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);

        var candidates = new List<RecoveryCandidate>();
        foreach (var documentId in journal.ListDocumentIds())
        {
            var record = TryRead(journal, documentId);
            if (record is null)
            {
                continue;
            }

            var candidate = TryCandidate(
                    journal, documentId, record.TargetPath, record.SnapshotFileName,
                    record.Revision, record.UpdatedUtc)
                ?? TryCandidate(
                    journal, documentId, record.TargetPath, record.PreviousSnapshotFileName,
                    record.PreviousRevision, record.PreviousUpdatedUtc);

            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static RecoveryCandidate? TryCandidate(
        RecoveryJournal journal,
        Guid documentId,
        string targetPath,
        string snapshotFileName,
        long revision,
        string updatedText)
    {
        if (!IsSafeSnapshotFileName(snapshotFileName))
        {
            return null;
        }

        var path = Path.Combine(journal.DirectoryFor(documentId), snapshotFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        OpenedProject opened;
        try
        {
            opened = MapProjectStore.Open(path);
        }
        catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (opened.Document.Id.Value != documentId)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                updatedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedUtc))
        {
            updatedUtc = opened.ModifiedUtc;
        }

        return new RecoveryCandidate
        {
            DocumentId = documentId,
            TargetPath = targetPath,
            CandidatePath = path,
            UpdatedUtc = updatedUtc,
            Title = opened.Document.Title,
            Kind = RecoveryCandidateKind.Autosave,
            Revision = revision,
        };
    }

    private static AutosaveRecord? TryReadRecord(string path, Guid expectedDocumentId)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<AutosaveRecord>(File.ReadAllBytes(path), JsonOptions);
            return record is not null
                && Guid.TryParseExact(record.DocumentId, "D", out var parsed)
                && parsed == expectedDocumentId
                ? record
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteRecordAtomically(
        RecoveryJournal journal, Guid documentId, AutosaveRecord record)
    {
        var directory = journal.DirectoryFor(documentId);
        var path = RecordPathFor(journal, documentId);
        var backup = Path.Combine(directory, RecordBackupFileName);
        var temp = Path.Combine(directory, $"autosave.json.tmp-{Guid.NewGuid():N}");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);

        try
        {
            using (var stream = new FileStream(
                       temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool TryValidateSnapshot(string path, Guid documentId)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path)) is not null
                && MapProjectStore.Open(path).Document.Id.Value == documentId;
        }
        catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string FileName, long Revision, string UpdatedUtc) SelectPreviousGeneration(
        string directory, Guid documentId, AutosaveRecord? record)
    {
        if (record is null)
        {
            return ("", 0, "");
        }

        if (IsUsableGeneration(directory, documentId, record.SnapshotFileName))
        {
            return (record.SnapshotFileName, record.Revision, record.UpdatedUtc);
        }

        return IsUsableGeneration(directory, documentId, record.PreviousSnapshotFileName)
            ? (record.PreviousSnapshotFileName, record.PreviousRevision, record.PreviousUpdatedUtc)
            : ("", 0, "");
    }

    private static bool IsUsableGeneration(string directory, Guid documentId, string fileName) =>
        IsSafeSnapshotFileName(fileName)
        && TryValidateSnapshot(Path.Combine(directory, fileName), documentId);

    private static bool IsSafeSnapshotFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
        && fileName.StartsWith(SnapshotPrefix, StringComparison.Ordinal)
        && fileName.Contains(SnapshotSuffix, StringComparison.Ordinal)
        && !fileName.Contains(Path.DirectorySeparatorChar)
        && !fileName.Contains(Path.AltDirectorySeparatorChar);

    private static void CleanupGenerations(string directory, AutosaveRecord record)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            record.SnapshotFileName,
            record.PreviousSnapshotFileName,
        };

        foreach (var path in Directory.GetFiles(directory, SnapshotPrefix + "*"))
        {
            if (!keep.Contains(Path.GetFileName(path)))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Cleanup is bounded best effort. A later successful autosave tries again.
                }
                catch (UnauthorizedAccessException)
                {
                    // The current pointer is already durable; stale files are not a save failure.
                }
            }
        }
    }

    private static void TryDeleteOwnedGeneration(string directory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(
                    Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar),
                    fullDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith(SnapshotPrefix, StringComparison.Ordinal))
            {
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A later successful autosave runs bounded cleanup again.
        }
    }
}
