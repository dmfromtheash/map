using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence.Tests.Support;

namespace Map.Persistence.Tests;

public class Stage14VersionsAndAtomicAutosaveTests
{
    [Fact]
    public void Stage14_save_reopen_preserves_objects_version_order_names_previews_and_snapshots()
    {
        using var workspace = new TempWorkspace();
        var source = ProjectFixtures.Document("Stage 14");
        var first = Version(source, "Старое пользовательское имя", 1);
        var changed = DocumentEditor.ReplaceObject(
            source,
            source.Layers.OfType<ObjectLayer>().Single().Objects[0] with { Name = "Прямоугольник 1" });
        var second = Version(changed, "Версия 02 · 01.09.2026 14:05", 2);
        var document = changed.WithCheckpoints([first, second]);
        var path = workspace.File("stage14.mapproj");

        MapProjectStore.Save(document, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var reopened = MapProjectStore.Open(path).Document;

        Assert.Equal([first.Name, second.Name], reopened.Checkpoints.Select(item => item.Name));
        Assert.Equal(first.PreviewPng.ToArray(), reopened.Checkpoints[0].PreviewPng.ToArray());
        Assert.Equal(second.PreviewPng.ToArray(), reopened.Checkpoints[1].PreviewPng.ToArray());
        Assert.Equal("keep", reopened.Checkpoints[0].Snapshot
            .Materialize(reopened.Id).Layers.OfType<ObjectLayer>().Single().Objects[0].Name);
        Assert.Equal("Прямоугольник 1", reopened.Layers
            .OfType<ObjectLayer>().Single().Objects[0].Name);
    }

    [Fact]
    public void Stage14_atomic_primary_failure_keeps_previous_destination_byte_for_byte()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("primary-autosave.mapproj");
        var previous = ProjectFixtures.Document("До autosave");
        MapProjectStore.Save(previous, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng());
        var previousBytes = File.ReadAllBytes(path);
        var next = ProjectFixtures.Document("После autosave");
        var faults = new SaveFaultInjection
        {
            AfterDurableTempBeforeReplace = () => throw new IOException("Stage14 injected failure"),
        };

        Assert.Throws<MapProjectSaveException>(() => MapProjectStore.Save(
            next, path, ProjectFixtures.Metadata(), ProjectFixtures.PreviewPng(), journal: null, faults));

        Assert.Equal(previousBytes, File.ReadAllBytes(path));
        Assert.Equal("До autosave", MapProjectStore.Open(path).Document.Title);
    }

    [Fact]
    public void Stage14_version_limit_refuses_without_evicting_old_versions()
    {
        var source = ProjectFixtures.Document();
        var versions = Enumerable.Range(1, MapCheckpointLimits.MaxCount)
            .Select(index => Version(source, $"Версия {index:00}", index))
            .ToArray();
        var document = source.WithCheckpoints(versions);
        var ids = document.Checkpoints.Select(item => item.Id).ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            new AddCheckpointCommand(Version(source, "Лишняя", 99)).Apply(document));
        Assert.Equal(ids, document.Checkpoints.Select(item => item.Id));
    }

    private static MapCheckpoint Version(MapDocument document, string name, int minute) => new(
        CheckpointId.New(),
        name,
        new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero).AddMinutes(minute),
        MapCheckpointSnapshot.Capture(document),
        ProjectFixtures.PreviewPng());
}
