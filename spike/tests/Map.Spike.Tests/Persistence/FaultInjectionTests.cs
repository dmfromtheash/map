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
