using System.Text.Json;
using Map.Spike.Core.Commands;
using Map.Spike.Core.Documents;
using Map.Spike.Core.Fixtures;
using Map.Spike.Core.Geometry;
using Map.Spike.Persistence;
using Map.Spike.Persistence.Atomic;
using Map.Spike.Persistence.Recovery;
using Map.Spike.Tests.Support;

namespace Map.Spike.Tests.Persistence;

/// <summary>
/// Covers the docs/08 fault-injection matrix. For every interruption point the questions are the
/// same: which file is the last correct one, is the document still honestly marked unsaved, and
/// what can the user recover.
/// </summary>
public class FaultInjectionTests
{
    private static SpikeDocument Edited(SpikeDocument document, double x, double y)
    {
        var session = new BrushStrokeSession(document.Brush, BrushSettings.Default);
        session.AddPoint(new Vec2(x, y));
        session.AddPoint(new Vec2(x + 60, y + 40));
        new CommandHistory().Execute(document, session.Complete()!);
        return document;
    }

    private static (SpikeDocument Document, string Path, RecoveryJournal Journal, byte[] FirstBytes)
        SetUpSavedDocument(TempWorkspace workspace, string name)
    {
        var journal = new RecoveryJournal(workspace.JournalRoot);
        var document = Edited(SpikeFixtures.CreateObjectScene(200, canvasSize: 1024), 200, 200);
        var path = workspace.File(name + SpikeFormat.Extension);

        new AtomicDocumentStore(journal).Save(document, path);
        return (document, path, journal, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(SavePhase.BeforeTempCreate)]
    [InlineData(SavePhase.MidTempWrite)]
    [InlineData(SavePhase.AfterFlushBeforeValidate)]
    [InlineData(SavePhase.AfterValidateBeforeReplace)]
    [InlineData(SavePhase.DuringReplace)]
    public void A_fault_before_replace_leaves_the_previous_file_intact_and_the_document_unsaved(SavePhase phase)
    {
        using var workspace = new TempWorkspace();
        var (document, path, journal, firstBytes) = SetUpSavedDocument(workspace, "fault-" + phase);

        var savedRevisionBefore = document.SavedRevision;
        Edited(document, 500, 500);
        Assert.True(document.IsDirty);

        var store = new AtomicDocumentStore(journal, new SingleFaultInjector(phase));
        var failure = Assert.Throws<SaveFailedException>(() => store.Save(document, path));

        // 1. The last correct file is the previously saved one, byte for byte.
        Assert.True(File.Exists(path));
        Assert.Equal(firstBytes, File.ReadAllBytes(path));
        Assert.True(failure.TargetStillIntact);

        // 2. It still opens.
        var reopened = AtomicDocumentStore.Load(path);
        Assert.Equal(savedRevisionBefore, reopened.Revision);

        // 3. The document does NOT claim success. This is the false-success guard.
        Assert.True(document.IsDirty);
        Assert.Equal(savedRevisionBefore, document.SavedRevision);
        Assert.Equal(phase, failure.Phase);
    }

    [Fact]
    public void A_fault_after_validation_leaves_durable_recoverable_work()
    {
        using var workspace = new TempWorkspace();
        var (document, path, journal, _) = SetUpSavedDocument(workspace, "recoverable");

        Edited(document, 700, 700);
        var pendingRevision = document.Revision;

        var store = new AtomicDocumentStore(journal, new SingleFaultInjector(SavePhase.DuringReplace));
        var failure = Assert.Throws<SaveFailedException>(() => store.Save(document, path));

        var record = journal.TryRead(document.DocumentId);
        Assert.NotNull(record);
        Assert.Equal(RecoveryState.TempDurable, record!.StateValue);
        Assert.True(record.HasRecoverableWork);
        Assert.Equal(pendingRevision, record.PendingRevision);

        Assert.NotNull(failure.TempPath);
        Assert.True(File.Exists(failure.TempPath!));

        // The recovered temp is a complete, loadable document holding the unsaved work.
        var recovered = AtomicDocumentStore.Load(record.TempPath);
        Assert.Equal(pendingRevision, recovered.Revision);
        Assert.Equal(document.DocumentId, recovered.DocumentId);
        Assert.Equal(document.Brush.TileCount, recovered.Brush.TileCount);
    }

    [Fact]
    public void A_fault_before_the_temp_is_validated_is_not_offered_as_recoverable()
    {
        using var workspace = new TempWorkspace();
        var (document, path, journal, _) = SetUpSavedDocument(workspace, "unverified");

        Edited(document, 800, 800);

        var store = new AtomicDocumentStore(journal, new SingleFaultInjector(SavePhase.AfterFlushBeforeValidate));
        Assert.Throws<SaveFailedException>(() => store.Save(document, path));

        var record = journal.TryRead(document.DocumentId);
        Assert.NotNull(record);
        Assert.Equal(RecoveryState.Started, record!.StateValue);

        // A temp file exists, but it was never read back. Offering it as "your recovered work"
        // would be a guess, so HasRecoverableWork stays false.
        Assert.False(record.HasRecoverableWork);
    }

    [Fact]
    public void A_fault_after_replace_keeps_the_new_content_and_still_refuses_to_claim_success()
    {
        using var workspace = new TempWorkspace();
        var (document, path, journal, firstBytes) = SetUpSavedDocument(workspace, "after-replace");

        Edited(document, 900, 900);
        var pendingRevision = document.Revision;

        var store = new AtomicDocumentStore(journal, new SingleFaultInjector(SavePhase.AfterReplaceBeforeCleanup));
        Assert.Throws<SaveFailedException>(() => store.Save(document, path));

        // The replace already happened, so the target holds the new work.
        Assert.NotEqual(firstBytes, File.ReadAllBytes(path));
        var reopened = AtomicDocumentStore.Load(path);
        Assert.Equal(pendingRevision, reopened.Revision);

        // The in-memory document still reports unsaved. Erring toward "you may still need to save"
        // costs the user one redundant save; the opposite error costs them their work.
        Assert.True(document.IsDirty);

        var record = journal.TryRead(document.DocumentId);
        Assert.NotNull(record);
        Assert.Equal(RecoveryState.Replaced, record!.StateValue);
    }

    [Fact]
    public void A_first_ever_save_that_fails_creates_no_target_file()
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(workspace.JournalRoot);
        var document = Edited(SpikeFixtures.CreateObjectScene(50, canvasSize: 512), 100, 100);
        var path = workspace.File("never-created" + SpikeFormat.Extension);

        var store = new AtomicDocumentStore(journal, new SingleFaultInjector(SavePhase.MidTempWrite));
        var failure = Assert.Throws<SaveFailedException>(() => store.Save(document, path));

        Assert.False(File.Exists(path));
        Assert.True(failure.TargetStillIntact); // nothing existed to damage
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void A_locked_target_fails_the_save_without_losing_the_previous_file()
    {
        using var workspace = new TempWorkspace();
        var (document, path, journal, firstBytes) = SetUpSavedDocument(workspace, "locked");

        Edited(document, 300, 300);

        var pendingRevision = document.Revision;

        // Hold the target open with no sharing, as another process would.
        using (var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var store = new AtomicDocumentStore(journal);
            var failure = Assert.Throws<SaveFailedException>(() => store.Save(document, path));

            Assert.True(document.IsDirty);
            Assert.Equal(SavePhase.DuringReplace, failure.Phase);

            // The work still reached a durable, validated temp file, so nothing is lost.
            Assert.NotNull(failure.TempPath);
            var record = journal.TryRead(document.DocumentId);
            Assert.NotNull(record);
            Assert.True(record!.HasRecoverableWork);
            Assert.Equal(pendingRevision, AtomicDocumentStore.Load(record.TempPath).Revision);
        }

        Assert.Equal(firstBytes, File.ReadAllBytes(path));
    }

    /// <summary>
    /// Corrupts the durable temp file at a chosen phase instead of throwing. This models the case the
    /// read-back exists for: the bytes reached the device, the write reported success, and the file
    /// is still not a loadable document.
    /// </summary>
    private sealed class CorruptTempAtPhase(SavePhase corruptAt, string directory) : ISaveFaultInjector
    {
        private readonly SavePhase _corruptAt = corruptAt;
        private readonly string _directory = directory;

        public string? CorruptedPath { get; private set; }

        public void OnPhase(SavePhase phase)
        {
            if (phase != _corruptAt || CorruptedPath is not null)
            {
                return;
            }

            var temp = Directory.GetFiles(_directory, "*.tmp-*").Single();

            // Chop off the ZIP end-of-central-directory record: exactly what a torn write leaves.
            using var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(Math.Max(0, stream.Length - 96));
            stream.Flush(flushToDisk: true);

            CorruptedPath = temp;
        }
    }

    [Fact]
    public void A_temp_that_is_durable_but_unreadable_fails_the_save_and_never_reaches_the_target()
    {
        using var workspace = new TempWorkspace();
        var (document, path, journal, firstBytes) = SetUpSavedDocument(workspace, "corrupt-temp");

        var savedRevisionBefore = document.SavedRevision;
        Edited(document, 640, 640);
        Assert.True(document.IsDirty);

        var injector = new CorruptTempAtPhase(SavePhase.AfterFlushBeforeValidate, workspace.Root);
        var store = new AtomicDocumentStore(journal, injector);

        var failure = Assert.Throws<SaveFailedException>(() => store.Save(document, path));

        Assert.NotNull(injector.CorruptedPath);

        // 1. The corrupt bytes never replaced the last good file.
        Assert.Equal(firstBytes, File.ReadAllBytes(path));
        Assert.True(failure.TargetStillIntact);
        Assert.Equal(savedRevisionBefore, AtomicDocumentStore.Load(path).Revision);

        // 2. No false success: the document still knows it is unsaved.
        Assert.True(document.IsDirty);
        Assert.Equal(savedRevisionBefore, document.SavedRevision);

        // 3. The failure is attributed to the validation step, not to a later phase.
        Assert.Equal(SavePhase.AfterFlushBeforeValidate, failure.Phase);

        // 4. The journal must not advertise the broken temp as recovered work.
        var record = journal.TryRead(document.DocumentId);
        Assert.NotNull(record);
        Assert.Equal(RecoveryState.Started, record!.StateValue);
        Assert.False(record.HasRecoverableWork);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"documentId\": \"not-json")]
    [InlineData("\0\0\0\0\0\0\0\0")]
    [InlineData("null")]
    [InlineData("{\"documentId\": 12345}")]
    public void A_torn_recovery_journal_is_read_without_crashing_the_process(string content)
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(workspace.JournalRoot);
        var documentId = Guid.NewGuid();

        Directory.CreateDirectory(journal.DirectoryFor(documentId));
        File.WriteAllText(journal.PathFor(documentId), content);

        // A journal we cannot parse carries no usable paths, so the honest answer is "nothing to
        // offer" - not an exception, and never a guess about which file holds the user's work.
        var record = journal.TryRead(documentId);

        if (record is not null)
        {
            Assert.False(record.HasRecoverableWork);
        }
    }

    /// <summary>
    /// Writes a syntactically valid journal whose "state" holds an arbitrary JSON fragment, next to
    /// a real durable-looking temp file - the strongest possible temptation to offer recovery.
    /// </summary>
    private static (RecoveryJournal Journal, Guid DocumentId, string JournalPath, string TempPath)
        WriteJournalWithRawState(TempWorkspace workspace, string stateJsonFragment)
    {
        var journal = new RecoveryJournal(workspace.JournalRoot);
        var documentId = Guid.NewGuid();

        var tempPath = workspace.File("recovered-work.tmp-deadbeef");
        File.WriteAllText(tempPath, "durable bytes left behind by an interrupted save");

        var journalPath = journal.PathFor(documentId);
        Directory.CreateDirectory(journal.DirectoryFor(documentId));
        File.WriteAllText(
            journalPath,
            "{\"documentId\":\"" + documentId.ToString("D") + "\"," +
            "\"tempPath\":" + JsonSerializer.Serialize(tempPath) + "," +
            "\"state\":" + stateJsonFragment + "}");

        return (journal, documentId, journalPath, tempPath);
    }

    [Fact]
    public void A_journal_from_a_future_version_with_an_unknown_state_is_not_offered_and_does_not_throw()
    {
        // The independently confirmed counterexample: valid JSON, state written by a newer version,
        // and an existing temp file. Reading HasRecoverableWork used to throw
        // ArgumentException: Requested value 'FutureVersionDurable' was not found.
        using var workspace = new TempWorkspace();
        var (journal, documentId, journalPath, tempPath) =
            WriteJournalWithRawState(workspace, "\"FutureVersionDurable\"");

        var record = journal.TryRead(documentId);

        // Valid JSON still yields a record, so diagnostics keep the raw state...
        Assert.NotNull(record);
        Assert.Equal("FutureVersionDurable", record!.State);

        // ...but a state this version does not understand is never offered as recoverable,
        // and deciding that must not throw.
        Assert.False(record.HasRecoverableWork);
        Assert.Null(record.StateValue);

        // Nothing is deleted on a guess: both the journal and the temp survive for a newer
        // version (or the user) to look at.
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(tempPath));
    }

    [Theory]
    [InlineData("\"999\"")]         // parses numerically, but 999 is not a defined member
    [InlineData("\"-1\"")]          // same, negative
    [InlineData("\"tempdurable\"")] // wrong case is not the state our writer records
    [InlineData("\"TEMPDURABLE\"")]
    [InlineData("\"\"")]            // empty
    [InlineData("\"   \"")]         // whitespace
    [InlineData("null")]            // JSON null
    public void A_state_this_version_does_not_recognise_is_never_recoverable(string stateJsonFragment)
    {
        using var workspace = new TempWorkspace();
        var (journal, documentId, journalPath, tempPath) =
            WriteJournalWithRawState(workspace, stateJsonFragment);

        var record = journal.TryRead(documentId);

        Assert.NotNull(record);
        Assert.False(record!.HasRecoverableWork);
        Assert.Null(record.StateValue);
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(tempPath));
    }

    [Fact]
    public void A_known_TempDurable_state_with_an_existing_temp_stays_recoverable()
    {
        // Positive control for the safe parse: the recognised durable state must keep working.
        using var workspace = new TempWorkspace();
        var (journal, documentId, _, _) = WriteJournalWithRawState(workspace, "\"TempDurable\"");

        var record = journal.TryRead(documentId);

        Assert.NotNull(record);
        Assert.Equal(RecoveryState.TempDurable, record!.StateValue);
        Assert.True(record.HasRecoverableWork);
    }

    [Fact]
    public void A_known_TempDurable_state_without_the_temp_file_is_not_recoverable()
    {
        using var workspace = new TempWorkspace();
        var (journal, documentId, _, tempPath) = WriteJournalWithRawState(workspace, "\"TempDurable\"");
        File.Delete(tempPath);

        var record = journal.TryRead(documentId);

        Assert.NotNull(record);
        Assert.Equal(RecoveryState.TempDurable, record!.StateValue);
        Assert.False(record.HasRecoverableWork);
    }

    [Theory]
    [InlineData("Started", RecoveryState.Started)]
    [InlineData("Replaced", RecoveryState.Replaced)]
    [InlineData("Completed", RecoveryState.Completed)]
    public void Known_non_durable_states_stay_non_recoverable_even_with_a_temp_present(
        string state, RecoveryState expected)
    {
        using var workspace = new TempWorkspace();
        var (journal, documentId, _, _) = WriteJournalWithRawState(workspace, "\"" + state + "\"");

        var record = journal.TryRead(documentId);

        Assert.NotNull(record);
        Assert.Equal(expected, record!.StateValue);
        Assert.False(record.HasRecoverableWork);
    }

    [Fact]
    public void A_torn_journal_does_not_stop_the_next_save_from_succeeding()
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(workspace.JournalRoot);
        var document = Edited(SpikeFixtures.CreateObjectScene(40, canvasSize: 512), 120, 120);
        var path = workspace.File("after-torn-journal" + SpikeFormat.Extension);

        Directory.CreateDirectory(journal.DirectoryFor(document.DocumentId));
        File.WriteAllText(journal.PathFor(document.DocumentId), "{ this is not json");

        new AtomicDocumentStore(journal).Save(document, path);

        Assert.False(document.IsDirty);
        Assert.Equal(document.Revision, AtomicDocumentStore.Load(path).Revision);
        Assert.Null(journal.TryRead(document.DocumentId));
    }

    [Fact]
    public void Recovery_journal_lives_outside_the_document_directory()
    {
        using var workspace = new TempWorkspace();
        var journal = new RecoveryJournal(workspace.JournalRoot);
        var document = Edited(SpikeFixtures.CreateObjectScene(20, canvasSize: 512), 100, 100);
        var path = workspace.File("journal-location" + SpikeFormat.Extension);

        var store = new AtomicDocumentStore(journal, new SingleFaultInjector(SavePhase.DuringReplace));
        Assert.Throws<SaveFailedException>(() => store.Save(document, path));

        var journalPath = journal.PathFor(document.DocumentId);
        Assert.True(File.Exists(journalPath));
        Assert.StartsWith(workspace.JournalRoot, journalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_journal_root_is_under_local_appdata()
    {
        var root = RecoveryJournal.DefaultRoot();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, root, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Map", "Recovery"), root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_successful_save_after_a_failed_one_clears_the_journal()
    {
        using var workspace = new TempWorkspace();
        var (document, path, journal, _) = SetUpSavedDocument(workspace, "retry");

        Edited(document, 400, 400);

        var failing = new AtomicDocumentStore(journal, new SingleFaultInjector(SavePhase.DuringReplace));
        Assert.Throws<SaveFailedException>(() => failing.Save(document, path));
        Assert.True(journal.TryRead(document.DocumentId)!.HasRecoverableWork);

        new AtomicDocumentStore(journal).Save(document, path);

        Assert.Null(journal.TryRead(document.DocumentId));
        Assert.False(document.IsDirty);
        Assert.Equal(document.Revision, AtomicDocumentStore.Load(path).Revision);
    }
}
