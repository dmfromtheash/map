using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// Stage 10C: the editing session holds an ordered multi-selection with a primary object,
/// stays compatible with the old single-selection property, restores the previous whole set
/// and never keeps an id that is no longer part of the document.
/// </summary>
public class Stage10CMultiSelectionSessionTests
{
    private static SceneObject Rectangle(string name, double x = 100, double y = 100) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, y, 80, 60, 0, "#FF3E5C76", "#FF1B263B", 2, visible: true, locked: false);

    private static (EditingSession Session, LayerId LayerId, SceneObject A, SceneObject B, SceneObject C) NewSession()
    {
        var layerId = LayerId.New();
        var a = Rectangle("A", 10, 10);
        var b = Rectangle("B", 200, 10);
        var c = Rectangle("C", 400, 10);
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF"),
                new ObjectLayer(layerId, "objects", true, false, 1.0, [a, b, c]),
            ]);

        return (new EditingSession(document), layerId, a, b, c);
    }

    [Fact]
    public void Selection_holds_no_duplicates_and_keeps_the_requested_primary()
    {
        var (session, _, a, b, _) = NewSession();

        session.SetSelection([a.Id, b.Id, a.Id], primary: a.Id);

        Assert.Equal([a.Id, b.Id], session.SelectedObjectIds);
        Assert.Equal(a.Id, session.SelectedObjectId);
    }

    [Fact]
    public void Set_selection_defaults_the_primary_to_the_last_id()
    {
        var (session, _, a, b, c) = NewSession();

        session.SetSelection([a.Id, b.Id, c.Id]);

        Assert.Equal(c.Id, session.SelectedObjectId);
    }

    [Fact]
    public void Toggle_adds_and_removes_an_object_deterministically()
    {
        var (session, _, a, b, _) = NewSession();
        session.SetSelection([a.Id]);

        session.ToggleSelection(b.Id);
        Assert.Equal([a.Id, b.Id], session.SelectedObjectIds);
        Assert.Equal(b.Id, session.SelectedObjectId);

        session.ToggleSelection(b.Id);
        Assert.Equal([a.Id], session.SelectedObjectIds);
        Assert.Equal(a.Id, session.SelectedObjectId);

        session.ToggleSelection(a.Id);
        Assert.Empty(session.SelectedObjectIds);
        Assert.Null(session.SelectedObjectId);
    }

    [Fact]
    public void Single_selection_property_still_replaces_the_whole_set()
    {
        var (session, _, a, b, c) = NewSession();
        session.SetSelection([a.Id, b.Id]);

        session.SelectedObjectId = c.Id;

        Assert.Equal([c.Id], session.SelectedObjectIds);
        Assert.Equal(c.Id, session.SelectedObjectId);

        session.SelectedObjectId = null;
        Assert.Empty(session.SelectedObjectIds);
    }

    [Fact]
    public void Restore_returns_the_whole_previous_set_not_one_object()
    {
        var (session, _, a, b, c) = NewSession();
        session.SetSelection([a.Id, b.Id, c.Id]);

        session.ClearSelection();
        Assert.Empty(session.SelectedObjectIds);

        Assert.True(session.RestorePreviousSelection());
        Assert.Equal([a.Id, b.Id, c.Id], session.SelectedObjectIds);
        Assert.Equal(c.Id, session.SelectedObjectId);
    }

    [Fact]
    public void Restore_skips_ids_that_left_the_document()
    {
        var (session, layerId, a, b, c) = NewSession();
        session.SetSelection([a.Id, b.Id, c.Id]);
        session.ClearSelection();

        session.Execute(new RemoveObjectsCommand(session.Document, layerId, [b.Id]));

        Assert.True(session.RestorePreviousSelection());
        Assert.Equal([a.Id, c.Id], session.SelectedObjectIds);
    }

    [Fact]
    public void Removed_ids_are_pruned_after_execute_undo_and_redo()
    {
        var (session, layerId, a, b, c) = NewSession();
        session.SetSelection([a.Id, b.Id, c.Id], primary: b.Id);

        session.Execute(new RemoveObjectsCommand(session.Document, layerId, [b.Id]));
        Assert.Equal([a.Id, c.Id], session.SelectedObjectIds);
        Assert.NotNull(session.SelectedObjectId);
        Assert.NotEqual(b.Id, session.SelectedObjectId);

        // Undo brings the object back into the document, not back into the selection.
        Assert.True(session.Undo());
        Assert.Equal([a.Id, c.Id], session.SelectedObjectIds);

        Assert.True(session.Redo());
        Assert.Equal([a.Id, c.Id], session.SelectedObjectIds);
    }

    [Fact]
    public void Add_to_selection_appends_only_new_ids()
    {
        var (session, _, a, b, c) = NewSession();
        session.SetSelection([a.Id]);

        session.AddToSelection([a.Id, b.Id, c.Id]);

        Assert.Equal([a.Id, b.Id, c.Id], session.SelectedObjectIds);
        Assert.Equal(c.Id, session.SelectedObjectId);
    }

    [Fact]
    public void Selected_objects_resolve_in_selection_order()
    {
        var (session, _, a, b, _) = NewSession();
        session.SetSelection([b.Id, a.Id]);

        Assert.Equal(["B", "A"], session.SelectedObjects.Select(o => o.Name));
    }

    [Fact]
    public void Toggling_off_a_non_primary_member_keeps_the_primary()
    {
        // The primary is deliberately NOT the last member: falling back to "the last one" by
        // accident would go unnoticed otherwise.
        var (session, _, a, b, c) = NewSession();
        session.SetSelection([a.Id, b.Id, c.Id], primary: b.Id);

        session.ToggleSelection(a.Id);

        Assert.Equal([b.Id, c.Id], session.SelectedObjectIds);
        Assert.Equal(b.Id, session.SelectedObjectId);
    }

    [Fact]
    public void Restore_reports_honestly_when_there_is_nothing_to_bring_back()
    {
        var (session, layerId, a, _, _) = NewSession();

        // Nothing was ever selected: nothing to restore.
        Assert.False(session.RestorePreviousSelection());

        // The whole remembered set has left the document: restore does nothing and says so.
        session.SetSelection([a.Id]);
        session.ClearSelection();
        session.Execute(new RemoveObjectsCommand(session.Document, layerId, [a.Id]));
        Assert.False(session.RestorePreviousSelection());
        Assert.Empty(session.SelectedObjectIds);
    }

    [Fact]
    public void Undo_and_redo_each_prune_ids_their_snapshot_no_longer_contains()
    {
        var (session, layerId, a, b, _) = NewSession();

        // Undo removes a freshly added object out from under its own selection.
        var added = Rectangle("D", 600, 10);
        session.Execute(new AddObjectCommand(layerId, added));
        session.SetSelection([added.Id]);
        Assert.True(session.Undo());
        Assert.Empty(session.SelectedObjectIds);

        // Redo removes an object that was re-selected while the removal was undone.
        session.Execute(new RemoveObjectsCommand(session.Document, layerId, [b.Id]));
        Assert.True(session.Undo());
        session.SetSelection([a.Id, b.Id]);
        Assert.True(session.Redo());
        Assert.Equal([a.Id], session.SelectedObjectIds);
    }

    [Fact]
    public void Removing_the_only_selected_object_empties_selection_and_primary()
    {
        var (session, layerId, a, _, _) = NewSession();
        session.SetSelection([a.Id]);

        session.Execute(new RemoveObjectsCommand(session.Document, layerId, [a.Id]));

        Assert.Empty(session.SelectedObjectIds);
        Assert.Null(session.SelectedObjectId);
    }

    [Fact]
    public void Selecting_after_a_clear_keeps_the_remembered_set_intact()
    {
        var (session, _, a, b, c) = NewSession();
        session.SetSelection([a.Id, b.Id]);
        session.ClearSelection();

        // The empty set must never overwrite the remembered one: the user cleared, clicked one
        // object, and still expects ` to bring the pair back.
        session.SetSelection([c.Id]);
        Assert.True(session.RestorePreviousSelection());

        Assert.Equal([a.Id, b.Id], session.SelectedObjectIds);
    }

    [Fact]
    public void Ids_absent_from_the_document_are_never_admitted()
    {
        var (session, _, a, _, _) = NewSession();

        session.SetSelection([a.Id, ObjectId.New()]);

        Assert.Equal([a.Id], session.SelectedObjectIds);
    }
}
