using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public class Stage12ANamedCheckpointTests
{
    private static readonly CheckpointId FirstCheckpointId =
        CheckpointId.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly CheckpointId SecondCheckpointId =
        CheckpointId.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset CheckpointCreatedUtc =
        new(2026, 8, 31, 9, 10, 11, TimeSpan.Zero);

    [Fact]
    public void Checkpoint_round_trip_is_immutable_and_snapshot_has_no_recursive_catalog()
    {
        using var workspace = new TempWorkspace();
        var original = ProjectFixtures.Document("До эксперимента");
        var checkpoint = Checkpoint(
            FirstCheckpointId, "  До эксперимента  ", original, CheckpointCreatedUtc);
        var session = new EditingSession(original);

        MapProjectStore.ValidateCheckpointAddition(session.Document, checkpoint);
        session.Execute(new AddCheckpointCommand(checkpoint));
        var capturedBeforeEdit = session.Document.Checkpoints.Single();

        session.Execute(MoveRectangle(session.Document, 740));
        Assert.Equal(96.5, Rectangle(capturedBeforeEdit.Snapshot.Materialize(original.Id)).X);
        Assert.Equal(740, Rectangle(session.Document).X);

        var path = workspace.File("checkpoint-roundtrip.mapproj");
        MapProjectStore.Save(
            session.Document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path);

        Assert.Equal("До эксперимента", reopened.Document.Checkpoints.Single().Name);
        Assert.Equal(CheckpointCreatedUtc, reopened.Document.Checkpoints.Single().CreatedUtc);
        Assert.Equal(ProjectFixtures.PreviewPng(), reopened.Document.Checkpoints.Single().PreviewPng.ToArray());
        Assert.Equal(96.5, Rectangle(
            reopened.Document.Checkpoints.Single().Snapshot.Materialize(reopened.Document.Id)).X);
        Assert.Equal(740, Rectangle(reopened.Document).X);

        using var archive = ZipFile.OpenRead(path);
        Assert.NotNull(archive.GetEntry(MapProjectFormat.CheckpointCatalogEntry));
        var root = $"{MapProjectFormat.CheckpointPrefix}{FirstCheckpointId}/";
        var snapshotScene = Encoding.UTF8.GetString(
            MapProjectRoundTripTests.ReadEntry(archive, root + "scene.json"));
        Assert.DoesNotContain("checkpoint", snapshotScene, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith(
            root + MapProjectFormat.CheckpointPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_is_one_history_entry_and_undo_redo_preserve_identity_and_catalog()
    {
        var baseline = ProjectFixtures.Document("Карта");
        var first = Checkpoint(FirstCheckpointId, "До эксперимента", baseline, CheckpointCreatedUtc);
        var secondState = MoveRectangle(baseline, 222).Apply(baseline);
        var second = Checkpoint(
            SecondCheckpointId, "Другой снимок", secondState,
            CheckpointCreatedUtc.AddMinutes(1));
        var current = MoveRectangle(baseline, 740).Apply(baseline)
            .WithCheckpoints([first, second]);
        var session = new EditingSession(current);
        var beforeRestore = session.Document;

        session.Execute(RestoreCheckpointCommand.Create(session.Document, FirstCheckpointId));

        Assert.Equal(1, session.UndoDepth);
        Assert.Equal(baseline.Id, session.Document.Id);
        Assert.Equal([FirstCheckpointId, SecondCheckpointId],
            session.Document.Checkpoints.Select(item => item.Id).ToArray());
        Assert.Equal(96.5, Rectangle(session.Document).X);
        Assert.True(session.IsDirty);

        Assert.True(session.Undo());
        Assert.Equal(740, Rectangle(session.Document).X);
        Assert.Equal(beforeRestore.Id, session.Document.Id);
        Assert.Equal([FirstCheckpointId, SecondCheckpointId],
            session.Document.Checkpoints.Select(item => item.Id).ToArray());

        Assert.True(session.Redo());
        Assert.Equal(96.5, Rectangle(session.Document).X);
        Assert.Equal(beforeRestore.Id, session.Document.Id);
        Assert.Equal([FirstCheckpointId, SecondCheckpointId],
            session.Document.Checkpoints.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Count_and_total_size_limits_refuse_without_evicting_existing_checkpoints()
    {
        var source = ProjectFixtures.Document();
        var fullCatalog = source.WithCheckpoints(
            [.. Enumerable.Range(0, MapCheckpointLimits.MaxCount).Select(index =>
                Checkpoint(
                    new CheckpointId(new Guid(index + 1, 0, 0, new byte[8])),
                    $"Точка {index + 1}",
                    source,
                    CheckpointCreatedUtc.AddMinutes(index)))]);
        var originalIds = fullCatalog.Checkpoints.Select(item => item.Id).ToArray();

        var countRefusal = Assert.Throws<MapProjectResourceLimitException>(() =>
            MapProjectStore.ValidateCheckpointAddition(
                fullCatalog,
                Checkpoint(
                    CheckpointId.New(), "Лишняя", source,
                    CheckpointCreatedUtc.AddHours(1))));
        Assert.Contains(MapCheckpointLimits.MaxCount.ToString(), countRefusal.Message);
        Assert.Equal(originalIds, fullCatalog.Checkpoints.Select(item => item.Id).ToArray());

        var payload = new byte[4 * 1024 * 1024];
        ProjectFixtures.PreviewPng().CopyTo(payload, 0);
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var asset = new MapAssetReference(
            sha,
            "large.png",
            "image/png",
            "png",
            ProjectAssetStorage.Embedded,
            CheckpointCreatedUtc,
            "test",
            "test",
            embeddedContent: payload);
        var assetDocument = source.WithAssets([asset]);
        var existing = Enumerable.Range(0, 15).Select(index =>
            Checkpoint(
                new CheckpointId(new Guid(100 + index, 0, 0, new byte[8])),
                $"Большая {index + 1}",
                assetDocument,
                CheckpointCreatedUtc.AddMinutes(index))).ToList();
        var belowLimit = assetDocument.WithCheckpoints(existing);
        var existingIds = belowLimit.Checkpoints.Select(item => item.Id).ToArray();

        var sizeRefusal = Assert.Throws<MapProjectResourceLimitException>(() =>
            MapProjectStore.ValidateCheckpointAddition(
                belowLimit,
                Checkpoint(
                    CheckpointId.New(), "Превышение объёма", assetDocument,
                    CheckpointCreatedUtc.AddHours(2))));
        Assert.Contains(MapCheckpointLimits.MaxTotalStoredBytes.ToString(), sizeRefusal.Message);
        Assert.Equal(existingIds, belowLimit.Checkpoints.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Missing_checkpoint_entry_and_unknown_restore_leave_live_document_unchanged()
    {
        using var workspace = new TempWorkspace();
        var source = ProjectFixtures.Document();
        var checkpoint = Checkpoint(
            FirstCheckpointId, "До эксперимента", source, CheckpointCreatedUtc);
        var document = source.WithCheckpoints([checkpoint]);
        var path = workspace.File("missing-checkpoint-entry.mapproj");
        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());

        var session = new EditingSession(source);
        var liveBefore = session.Document;
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry(
                $"{MapProjectFormat.CheckpointPrefix}{FirstCheckpointId}/scene.json")!.Delete();
        }

        var corruption = Assert.Throws<MapProjectCorruptedException>(() => MapProjectStore.Open(path));
        Assert.Contains("missing", corruption.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(liveBefore, session.Document);

        Assert.Throws<KeyNotFoundException>(() =>
            RestoreCheckpointCommand.Create(session.Document, CheckpointId.New()));
        Assert.Same(liveBefore, session.Document);
        Assert.Equal(0, session.UndoDepth);
    }

    [Fact]
    public void Failed_checkpoint_write_preserves_the_previous_correct_mapproj()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("checkpoint-atomic.mapproj");
        var firstVersion = ProjectFixtures.Document("Старая версия");
        MapProjectStore.Save(
            firstVersion, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var firstBytes = File.ReadAllBytes(path);

        var secondVersion = ProjectFixtures.Document("Новая версия");
        var checkpoint = Checkpoint(
            FirstCheckpointId, "До эксперимента", secondVersion, CheckpointCreatedUtc);
        secondVersion = secondVersion.WithCheckpoints([checkpoint]);
        var faults = new SaveFaultInjection
        {
            AfterDurableTempBeforeReplace = () => throw new IOException("Injected Stage12A failure."),
        };

        Assert.Throws<MapProjectSaveException>(() => MapProjectStore.Save(
            secondVersion,
            path,
            ProjectFixtures.Metadata(),
            ProjectFixtures.PreviewPng(),
            journal: null,
            faults));

        Assert.Equal(firstBytes, File.ReadAllBytes(path));
        var reopened = MapProjectStore.Open(path);
        Assert.Equal("Старая версия", reopened.Document.Title);
        Assert.Empty(reopened.Document.Checkpoints);
    }

    [Fact]
    public void Deleting_one_checkpoint_changes_no_map_state_and_survives_reopen()
    {
        using var workspace = new TempWorkspace();
        var source = ProjectFixtures.Document();
        var first = Checkpoint(FirstCheckpointId, "Первая", source, CheckpointCreatedUtc);
        var second = Checkpoint(
            SecondCheckpointId, "Вторая", MoveRectangle(source, 222).Apply(source),
            CheckpointCreatedUtc.AddMinutes(1));
        var session = new EditingSession(source.WithCheckpoints([first, second]));
        var beforeDelete = session.Document;

        session.Execute(DeleteCheckpointCommand.Create(session.Document, FirstCheckpointId));
        ProjectFixtures.AssertSemanticallyEqual(beforeDelete, session.Document);
        Assert.Equal([SecondCheckpointId], session.Document.Checkpoints.Select(item => item.Id).ToArray());

        var path = workspace.File("checkpoint-delete.mapproj");
        MapProjectStore.Save(
            session.Document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path);
        ProjectFixtures.AssertSemanticallyEqual(source, reopened.Document);
        Assert.Equal([SecondCheckpointId], reopened.Document.Checkpoints.Select(item => item.Id).ToArray());
    }

    private static MapCheckpoint Checkpoint(
        CheckpointId id,
        string name,
        MapDocument document,
        DateTimeOffset createdUtc) => new(
            id,
            name,
            createdUtc,
            MapCheckpointSnapshot.Capture(document),
            ProjectFixtures.PreviewPng());

    private static TransformObjectCommand MoveRectangle(MapDocument document, double x)
    {
        var before = Rectangle(document);
        var after = new SceneObject(
            before.Id,
            before.Kind,
            before.Name,
            x,
            before.Y,
            before.Width,
            before.Height,
            before.RotationDegrees,
            before.FillColor,
            before.StrokeColor,
            before.StrokeWidth,
            before.Visible,
            before.Locked)
        {
            Sublayer = before.Sublayer,
            GroupId = before.GroupId,
            GroupName = before.GroupName,
            Stamp = before.Stamp,
            Line = before.Line,
            Shape = before.Shape,
            Text = before.Text,
            Composition = before.Composition,
            PlaceholderReason = before.PlaceholderReason,
            ObjectOpacity = before.ObjectOpacity,
        };
        return new TransformObjectCommand(before, after);
    }

    private static SceneObject Rectangle(MapDocument document) =>
        document.Layers.OfType<ObjectLayer>().Single().Objects
            .Single(item => item.Id == ProjectFixtures.FixedRectangleId);
}
