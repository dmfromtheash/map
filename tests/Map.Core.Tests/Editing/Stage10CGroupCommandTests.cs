using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>
/// Stage 10C: creating, renaming and dissolving a named group is one reversible command each,
/// redo restores the very same identity, and deletion across layers dissolves a singleton
/// remainder inside the same history entry.
/// </summary>
public class Stage10CGroupCommandTests
{
    private static SceneObject Rectangle(string name, double x = 10) => new(
        ObjectId.New(), SceneObjectKind.Rectangle, name,
        x, 10, 80, 60, 0, "#FF3E5C76", "#FF1B263B", 2, visible: true, locked: false);

    private static (EditingSession Session, LayerId Lower, LayerId Upper,
        SceneObject A, SceneObject B, SceneObject C, SceneObject D) NewSession()
    {
        var lower = LayerId.New();
        var upper = LayerId.New();
        var a = Rectangle("A", 10);
        var b = Rectangle("B", 200);
        var c = Rectangle("C", 400);
        var d = Rectangle("D", 600);
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF"),
                new ObjectLayer(lower, "lower", true, false, 1.0, [a, b]),
                new ObjectLayer(upper, "upper", true, false, 1.0, [c, d]),
            ]);

        return (new EditingSession(document), lower, upper, a, b, c, d);
    }

    private static SceneObject Find(EditingSession session, ObjectId id) =>
        DocumentEditor.FindObject(session.Document, id)!;

    // ---------------------------------------------------------------- creation

    [Fact]
    public void Group_requires_at_least_two_objects()
    {
        var (session, _, _, a, _, _, _) = NewSession();

        Assert.ThrowsAny<Exception>(() => SetGroupCommand.Group(session.Document, [a.Id], "Лес"));
    }

    [Fact]
    public void Group_requires_a_non_empty_name()
    {
        var (session, _, _, a, b, _, _) = NewSession();

        Assert.Throws<ArgumentException>(() => SetGroupCommand.Group(session.Document, [a.Id, b.Id], "   "));
    }

    [Fact]
    public void Group_name_is_trimmed_and_capped_at_80_characters()
    {
        var (session, _, _, a, b, _, _) = NewSession();

        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], "  Лес  "));
        Assert.Equal("Лес", Find(session, a.Id).GroupName);

        Assert.Throws<ArgumentException>(() =>
            SetGroupCommand.Group(session.Document, [a.Id, b.Id], new string('x', 81)));
    }

    [Fact]
    public void Partial_regrouping_of_grouped_objects_is_refused()
    {
        var (session, _, _, a, b, c, _) = NewSession();
        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], "Лес"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SetGroupCommand.Group(session.Document, [b.Id, c.Id], "Горы"));
        Assert.Contains("групп", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redo_restores_the_same_group_id_and_name()
    {
        var (session, _, _, a, b, _, _) = NewSession();
        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], "Лес"));

        var groupId = Find(session, a.Id).GroupId;
        Assert.NotNull(groupId);
        Assert.Equal(groupId, Find(session, b.Id).GroupId);

        Assert.True(session.Undo());
        Assert.Null(Find(session, a.Id).GroupId);
        Assert.Null(Find(session, a.Id).GroupName);

        Assert.True(session.Redo());
        Assert.Equal(groupId, Find(session, a.Id).GroupId);
        Assert.Equal("Лес", Find(session, a.Id).GroupName);
    }

    [Fact]
    public void A_name_of_exactly_eighty_characters_is_accepted()
    {
        var (session, _, _, a, b, _, _) = NewSession();
        var name = new string('и', 80);

        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], name));

        Assert.Equal(name, Find(session, a.Id).GroupName);
    }

    [Fact]
    public void Ungroup_accepts_a_single_id()
    {
        var (session, _, _, a, b, _, _) = NewSession();
        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], "Лес"));

        // Clearing one member is a legal low-level operation even though the editor always
        // dissolves whole groups; the guard only refuses an empty id list.
        session.Execute(SetGroupCommand.Ungroup(session.Document, [a.Id]));

        Assert.Null(Find(session, a.Id).GroupId);
        Assert.NotNull(Find(session, b.Id).GroupId);
    }

    [Fact]
    public void Renaming_an_unknown_group_is_refused()
    {
        var (session, _, _, _, _, _, _) = NewSession();

        Assert.Throws<InvalidOperationException>(() =>
            SetGroupCommand.Rename(session.Document, Guid.NewGuid(), "Лес"));
    }

    // ---------------------------------------------------------------- ungroup and rename

    [Fact]
    public void Ungroup_clears_id_and_name_together()
    {
        var (session, _, _, a, b, _, _) = NewSession();
        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], "Лес"));

        session.Execute(SetGroupCommand.Ungroup(session.Document, [a.Id, b.Id]));

        Assert.Null(Find(session, a.Id).GroupId);
        Assert.Null(Find(session, a.Id).GroupName);
        Assert.Null(Find(session, b.Id).GroupId);
        Assert.Null(Find(session, b.Id).GroupName);

        Assert.True(session.Undo());
        Assert.Equal("Лес", Find(session, a.Id).GroupName);
    }

    [Fact]
    public void Rename_changes_every_member_and_undo_restores_the_old_name()
    {
        var (session, _, _, a, b, _, _) = NewSession();
        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], "Лес"));
        var groupId = Find(session, a.Id).GroupId!.Value;

        session.Execute(SetGroupCommand.Rename(session.Document, groupId, "Тёмный лес"));

        Assert.Equal("Тёмный лес", Find(session, a.Id).GroupName);
        Assert.Equal("Тёмный лес", Find(session, b.Id).GroupName);

        Assert.True(session.Undo());
        Assert.Equal("Лес", Find(session, a.Id).GroupName);
    }

    // ---------------------------------------------------------------- delete across layers

    [Fact]
    public void Delete_across_layers_is_one_entry_and_undo_restores_order()
    {
        var (session, lower, upper, a, b, c, d) = NewSession();

        session.Execute(new DeleteObjectsCommand(session.Document, [a.Id, c.Id]));

        Assert.Null(DocumentEditor.FindObject(session.Document, a.Id));
        Assert.Null(DocumentEditor.FindObject(session.Document, c.Id));
        Assert.NotNull(DocumentEditor.FindObject(session.Document, b.Id));
        Assert.Equal(1, session.UndoDepth);

        Assert.True(session.Undo());
        Assert.Equal(["A", "B"], ((ObjectLayer)session.Document.FindLayer(lower)!).Objects.Select(o => o.Name));
        Assert.Equal(["C", "D"], ((ObjectLayer)session.Document.FindLayer(upper)!).Objects.Select(o => o.Name));
    }

    [Fact]
    public void A_singleton_remainder_loses_group_id_and_name_in_the_same_entry()
    {
        var (session, _, _, a, b, c, _) = NewSession();
        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id, c.Id], "Лес"));

        session.Execute(new DeleteObjectsCommand(session.Document, [a.Id, b.Id]));

        var remainder = Find(session, c.Id);
        Assert.Null(remainder.GroupId);
        Assert.Null(remainder.GroupName);

        // One undo restores both the deleted members and the remainder's membership.
        Assert.True(session.Undo());
        Assert.Equal("Лес", Find(session, a.Id).GroupName);
        Assert.Equal("Лес", Find(session, c.Id).GroupName);
        Assert.NotNull(Find(session, c.Id).GroupId);

        Assert.True(session.Redo());
        Assert.Null(Find(session, c.Id).GroupId);
        Assert.Null(Find(session, c.Id).GroupName);
    }

    [Fact]
    public void Deleting_a_whole_group_leaves_no_orphan_rewrites()
    {
        var (session, _, _, a, b, c, _) = NewSession();
        session.Execute(SetGroupCommand.Group(session.Document, [a.Id, b.Id], "Лес"));

        session.Execute(new DeleteObjectsCommand(session.Document, [a.Id, b.Id]));

        Assert.Null(DocumentEditor.FindObject(session.Document, a.Id));
        Assert.Null(DocumentEditor.FindObject(session.Document, b.Id));
        Assert.Null(Find(session, c.Id).GroupId);

        Assert.True(session.Undo());
        Assert.Equal("Лес", Find(session, a.Id).GroupName);
    }
}
