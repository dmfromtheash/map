using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>«Версии»: one-click snapshot, restore, delete, number reuse and the 20-version limit.</summary>
public sealed class VersionsPanelTests
{
    [Fact]
    public async Task Snapshot_restore_and_delete_work_as_single_undoable_steps()
    {
        var now = new DateTimeOffset(2026, 10, 16, 20, 30, 0, TimeSpan.Zero);
        var prompts = new FakePrompts { NextRestoreCheckpointConfirmation = true, NextDeleteCheckpointConfirmation = true };
        var editor = Editor(prompts, () => now);

        Assert.True(await editor.CreateVersionAsync());
        var first = Assert.Single(editor.Checkpoints);
        Assert.Equal($"Версия 01 · {now.ToLocalTime():dd.MM.yyyy HH:mm}", first.Name);
        Assert.True(first.PreviewPng.Length > 100);
        Assert.Single(first.Snapshot.Layers.OfType<ObjectLayer>().Single().Objects);

        // Change the map, then restore the snapshot: the map returns, and Ctrl+Z undoes the restore.
        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        editor.SelectObject(layer.Objects[0].Id);
        editor.DeleteSelection();
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects);
        Assert.True(await editor.RestoreCheckpointAsync(first.Id));
        Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects);
        editor.Undo();
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects);
        editor.Redo();

        Assert.True(await editor.CreateVersionAsync());
        Assert.StartsWith("Версия 02", editor.Checkpoints[^1].Name, StringComparison.Ordinal);
        Assert.True(await editor.DeleteCheckpointAsync(first.Id));
        Assert.Single(editor.Checkpoints);
        // The freed number is used again by the next snapshot.
        Assert.True(await editor.CreateVersionAsync());
        Assert.StartsWith("Версия 01", editor.Checkpoints[^1].Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_twenty_first_snapshot_is_refused_without_changing_the_map()
    {
        var editor = Editor(new FakePrompts(), () => DateTimeOffset.UnixEpoch);
        for (var i = 0; i < MapCheckpointLimits.MaxCount; i++) Assert.True(editor.CreateVersion());
        var before = editor.Session.Document;
        Assert.False(await editor.CreateVersionAsync());
        Assert.Same(before, editor.Session.Document);
        Assert.Contains("лимит", editor.StatusDetail);
    }

    [Fact]
    public async Task Cancelled_confirmations_change_nothing()
    {
        var editor = Editor(new FakePrompts(), () => DateTimeOffset.UnixEpoch);
        Assert.True(editor.CreateVersion());
        var version = editor.Checkpoints.Single();
        var before = editor.Session.Document;
        Assert.False(await editor.RestoreCheckpointAsync(version.Id));
        Assert.False(await editor.DeleteCheckpointAsync(version.Id));
        Assert.Same(before, editor.Session.Document);
    }

    private static EditorViewModel Editor(FakePrompts prompts, Func<DateTimeOffset> clock)
    {
        var rectangle = new SceneObject(ObjectId.New(), SceneObjectKind.Rectangle, "Башня", 100, 100, 80, 60, 0,
            "#FF8C6239", "#FF000000", 2, true, false);
        var document = new MapDocument(DocumentId.New(), "versions", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(640, 480, "px", "#FFEDE3CD"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFEDE3CD"),
             new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [rectangle])]);
        return new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            prompts, new FakeLog(), clock: clock);
    }
}
