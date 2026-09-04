using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The user-visible object lifecycle: placing without preparing a layer, the owner layer and the
/// object's own level, and Ctrl+C / Ctrl+V over a whole multi-selection.
/// All tests in one class on purpose - the object buffer is process-local, and xUnit runs the
/// tests of a single class one after another.
/// </summary>
public sealed class ObjectLifecycleAndClipboardTests
{
    private static EditorViewModel NewEditor(MapDocument? document = null) => new(
        document ?? TestData.Document(), documentPath: null, startDirty: false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
        clock: () => new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));

    /// <summary>A map that has no object layer at all, the state the user starts from.</summary>
    private static MapDocument WithoutObjectLayer() => new(
        DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
        [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1.0, "#FF1E2430")]);

    [Fact]
    public void An_object_can_be_added_without_preparing_a_layer_first_and_one_undo_clears_both()
    {
        var editor = NewEditor(WithoutObjectLayer());
        editor.Session.Viewport.Resize(1000, 700);
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>());

        editor.AddObject(SceneObjectKind.Rectangle);

        var layer = Assert.Single(editor.Session.Document.Layers.OfType<ObjectLayer>());
        Assert.Single(layer.Objects);
        Assert.NotNull(editor.Session.SelectedObject);

        editor.Undo();
        Assert.Empty(editor.Session.Document.Layers.OfType<ObjectLayer>().SelectMany(item => item.Objects));
    }

    [Fact]
    public void Copy_takes_the_whole_selection_and_paste_restores_all_of_it_as_new_objects()
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);

        var ids = new List<ObjectId>();
        for (var index = 0; index < 3; index++)
        {
            editor.AddObject(SceneObjectKind.Rectangle);
            ids.Add(editor.Session.SelectedObject!.Id);
            editor.NudgeSelectedObject(index * 40, index * 25);
        }

        editor.SelectObject(ids[0], additive: false);
        editor.SelectObject(ids[1], additive: true);
        editor.SelectObject(ids[2], additive: true);
        Assert.Equal(3, editor.SelectionCount);

        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        var before = layer.Objects.Where(item => ids.Contains(item.Id))
            .OrderBy(item => ids.IndexOf(item.Id)).ToList();
        var historyBeforeCopy = editor.Session.UndoDepth;

        editor.CopySelectedObjects();

        // Copying is not an edit: nothing is added to the canvas and nothing lands in history.
        Assert.Equal(3, editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Count);
        Assert.Equal(historyBeforeCopy, editor.Session.UndoDepth);
        Assert.True(editor.CanPasteObjects);

        editor.PasteObjects();

        var after = editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects;
        Assert.Equal(6, after.Count);

        // The whole new group is the selection, not one arbitrary member of it.
        Assert.Equal(3, editor.SelectionCount);
        var pasted = editor.Session.SelectedObjects;
        Assert.All(pasted, item => Assert.DoesNotContain(item.Id, ids));

        // Mutual layout survived: every gap between the copies equals the original gap.
        for (var index = 1; index < 3; index++)
        {
            Assert.Equal(before[index].X - before[0].X, pasted[index].X - pasted[0].X, 6);
            Assert.Equal(before[index].Y - before[0].Y, pasted[index].Y - pasted[0].Y, 6);
            Assert.Equal(before[index].Width, pasted[index].Width, 6);
            Assert.Equal(before[index].Height, pasted[index].Height, 6);
        }

        // ...and the group did not land exactly on top of the original.
        Assert.NotEqual(before[0].X, pasted[0].X);

        // The whole paste is one history entry in both directions.
        editor.Undo();
        Assert.Equal(3, editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Count);
        editor.Redo();
        Assert.Equal(6, editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Count);
    }

    [Fact]
    public void A_repeated_paste_cascades_instead_of_stacking_on_one_spot()
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);
        editor.AddObject(SceneObjectKind.Ellipse);

        editor.CopySelectedObjects();
        editor.PasteObjects();
        var first = editor.Session.SelectedObjects.Single();
        editor.PasteObjects();
        var second = editor.Session.SelectedObjects.Single();

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.X, second.X);
    }

    [Fact]
    public void The_owner_layer_and_the_object_level_are_named_values_the_user_can_change()
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);
        editor.AddObject(SceneObjectKind.Rectangle);
        var id = editor.Session.SelectedObject!.Id;

        var first = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        Assert.Equal(first.Name, editor.SelectedOwnerLayerName);
        Assert.Equal("0", editor.SelectedSublayerSummary);

        // The level is the object's own order inside its layer, and it is undoable.
        editor.SetSelectedSublayer(4);
        Assert.Equal(4, editor.Session.SelectedObject!.Sublayer);
        Assert.Equal("4", editor.SelectedSublayerSummary);
        editor.Undo();
        Assert.Equal(0, editor.Session.SelectedObject!.Sublayer);
        editor.Redo();
        Assert.Equal(4, editor.Session.SelectedObject!.Sublayer);

        // A second object layer becomes a destination, offered by name and never by identifier.
        editor.AddObjectLayer();
        editor.SelectObject(id, additive: false);
        var targets = editor.ObjectLayerTargets;
        Assert.Equal(2, targets.Count);
        Assert.All(targets, target => Assert.False(string.IsNullOrWhiteSpace(target.Name)));

        var destination = targets.Single(target => !target.IsCurrent);
        Assert.True(editor.MoveSelectionToLayer(destination.Id));
        Assert.Equal(destination.Name, editor.SelectedOwnerLayerName);
        Assert.Equal(4, editor.Session.SelectedObject!.Sublayer);
        Assert.Empty(((ObjectLayer)editor.Session.Document.FindLayer(first.Id)!).Objects);

        editor.Undo();
        Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(first.Id)!).Objects);
    }

    [Fact]
    public void The_selection_can_be_raised_and_lowered_among_its_neighbours()
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);
        for (var index = 0; index < 3; index++)
        {
            editor.AddObject(SceneObjectKind.Rectangle);
        }

        var layer = editor.Session.Document.Layers.OfType<ObjectLayer>().Single();
        var bottom = layer.Objects[0].Id;
        editor.SelectObject(bottom, additive: false);

        Assert.True(editor.ReorderSelection(1));
        Assert.Equal(1, Order(editor).IndexOf(bottom));

        Assert.True(editor.ReorderSelection(1, toExtreme: true));
        Assert.Equal(2, Order(editor).IndexOf(bottom));

        // Already at the very top, so the command reports it instead of pretending to work.
        Assert.False(editor.ReorderSelection(1));

        Assert.True(editor.ReorderSelection(-1, toExtreme: true));
        Assert.Equal(0, Order(editor).IndexOf(bottom));

        editor.Undo();
        Assert.Equal(2, Order(editor).IndexOf(bottom));
    }

    private static List<ObjectId> Order(EditorViewModel editor) =>
        [.. editor.Session.Document.Layers.OfType<ObjectLayer>().Single().Objects.Select(item => item.Id)];
}
