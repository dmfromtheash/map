using Map.Spike.Core.Documents;
using Map.Spike.Persistence.Container;
using Map.Spike.Persistence.Recovery;

namespace Map.Spike.Persistence.Atomic;

public sealed record SaveResult(
    string TargetPath,
    string? BackupPath,
    long SavedRevision,
    long BytesWritten,
    TimeSpan Duration);

/// <summary>
/// Raised when a save did not complete. Carries the paths the user must be shown instead of a
/// false success message (docs/05).
/// </summary>
public sealed class SaveFailedException(
    string message,
    SavePhase phase,
    string targetPath,
    string? tempPath,
    bool targetStillIntact,
    Exception? inner) : Exception(message, inner)
{
    public SavePhase Phase { get; } = phase;
    public string TargetPath { get; } = targetPath;
    public string? TempPath { get; } = tempPath;

    /// <summary>True when the previously saved file on disk is untouched and still openable.</summary>
    public bool TargetStillIntact { get; } = targetStillIntact;
}

/// <summary>
/// Implements the docs/05 save contract:
/// serialize snapshot -> write sibling temp -> validate read-back -> flush -> atomic replace ->
/// bounded .bak -> only then mark the revision saved.
/// </summary>
public sealed class AtomicDocumentStore(RecoveryJournal? journal = null, ISaveFaultInjector? faults = null)
{
    private readonly RecoveryJournal _journal = journal ?? new RecoveryJournal();
    private readonly ISaveFaultInjector? _faults = faults;

    public RecoveryJournal Journal => _journal;

    public SaveResult Save(SpikeDocument document, string targetPath)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        var fullTarget = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTarget)
            ?? throw new ArgumentException("Target path has no directory.", nameof(targetPath));

        // Sibling temp so File.Replace stays on one volume, which is what makes it atomic.
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(fullTarget)}.tmp-{Guid.NewGuid():N}");
        var backupPath = fullTarget + ".bak";

        var targetExisted = File.Exists(fullTarget);
        var pendingRevision = document.Revision;

        // Tracks how far the sequence actually got, so a failure reports the real interruption
        // point instead of a guess.
        var reached = SavePhase.BeforeTempCreate;

        try
        {
            // Everything from here on is inside the guard, including the prologue: creating the
            // directory, fingerprinting the existing target and writing the journal can all fail
            // (locked file, denied path, full disk), and those must surface as SaveFailedException
            // like any other save failure rather than as a raw IOException the caller cannot
            // interpret.
            Directory.CreateDirectory(directory);

            // Best effort: the fingerprint only helps a later recovery decide whether the target
            // changed underneath us. If the target cannot be read right now (another process holds
            // it), that is not a reason to abandon the save before writing anything - the user's
            // work should still reach a durable temp file, and the real failure will surface at
            // the replace, with something recoverable on disk.
            var baseHash = targetExisted ? TryFingerprint(fullTarget) : "";

            var record = _journal.Begin(
                document.DocumentId, fullTarget, tempPath, backupPath,
                document.SavedRevision, pendingRevision, baseHash);

            _faults?.OnPhase(SavePhase.BeforeTempCreate);

            long bytesWritten;
            using (var stream = new FileStream(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.None))
            {
                reached = SavePhase.MidTempWrite;
                SpikeDocumentSerializer.Write(document, stream);
                _faults?.OnPhase(SavePhase.MidTempWrite);
                stream.Flush(flushToDisk: true);
                bytesWritten = stream.Length;
            }

            reached = SavePhase.AfterFlushBeforeValidate;
            _faults?.OnPhase(SavePhase.AfterFlushBeforeValidate);

            ValidateReadBack(tempPath, document);
            _journal.Advance(document.DocumentId, record, RecoveryState.TempDurable);

            reached = SavePhase.AfterValidateBeforeReplace;
            _faults?.OnPhase(SavePhase.AfterValidateBeforeReplace);

            reached = SavePhase.DuringReplace;
            _faults?.OnPhase(SavePhase.DuringReplace);

            if (targetExisted)
            {
                // File.Replace is the Win32 ReplaceFile transactional swap: on return the target
                // is either wholly old or wholly new. We cannot split it further from managed code.
                File.Replace(tempPath, fullTarget, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullTarget);
            }

            _journal.Advance(document.DocumentId, record, RecoveryState.Replaced);

            reached = SavePhase.AfterReplaceBeforeCleanup;
            _faults?.OnPhase(SavePhase.AfterReplaceBeforeCleanup);

            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            _journal.Advance(document.DocumentId, record, RecoveryState.Completed);
            _journal.Clear(document.DocumentId);

            // Only now is the work durable, so only now may the document call itself saved.
            document.MarkSaved(pendingRevision);

            return new SaveResult(
                fullTarget,
                targetExisted ? backupPath : null,
                pendingRevision,
                bytesWritten,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
        }
        catch (Exception ex) when (ex is not SaveFailedException)
        {
            // The document is deliberately NOT marked saved here. The journal is deliberately NOT
            // cleared: it is the only thing that tells the next launch whether the temp file on
            // disk is durable work or a half-written fragment.
            var targetIntact = !targetExisted || IsReadable(fullTarget);

            throw new SaveFailedException(
                $"Save of {fullTarget} did not complete. The document is still unsaved.",
                reached,
                fullTarget,
                File.Exists(tempPath) ? tempPath : null,
                targetIntact,
                ex);
        }
    }

    public static SpikeDocument Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SpikeDocumentSerializer.Read(stream);
    }

    /// <summary>
    /// Reads the temp file back through the normal reader and compares identity and checksums.
    /// A save that cannot be re-read is a failed save, not a successful one.
    /// </summary>
    private static void ValidateReadBack(string tempPath, SpikeDocument expected)
    {
        using var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var reloaded = SpikeDocumentSerializer.Read(stream);

        if (reloaded.DocumentId != expected.DocumentId)
        {
            throw new InvalidDataException("Read-back document id does not match.");
        }

        if (reloaded.Revision != expected.Revision)
        {
            throw new InvalidDataException("Read-back revision does not match.");
        }

        if (reloaded.Objects.Count != expected.Objects.Count)
        {
            throw new InvalidDataException("Read-back object count does not match.");
        }
    }

    private static string TryFingerprint(string path)
    {
        try
        {
            return SpikeDocumentSerializer.Sha256HexOfFile(path);
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            SpikeDocumentSerializer.Read(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
