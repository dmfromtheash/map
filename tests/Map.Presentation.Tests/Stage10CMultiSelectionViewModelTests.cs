using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Stage 10C: the editor's multi-selection behaviour - click/toggle/marquee, Ctrl+A over search
/// and type filter, the group lifecycle, whole-selection transforms as one atomic history entry
/// and the honest multi-state the properties panel reads.
/// </summary>
public class Stage10CMultiSelectionViewModelTests
{
    private static SceneObject Object(
        string name, SceneObjectKind kind, double x, double y,
        double w = 100, double h = 50, bool visible = true, bool locked = false) => new(
        ObjectId.New(), kind, name, x, y, w, h, 0,
        "#FF3E5C76", "#FF1B263B", 0, visible, locked);

    private static (EditorViewModel Editor, LayerId Lower, LayerId Upper) NewEditor(
        IReadOnlyList<SceneObject> lowerObjects,
        IReadOnlyList<SceneObject>? upperObjects = null,
        bool lowerVisible = true,
        bool lowerLocked = false,
        bool upperVisible = true,
        bool upperLocked = false)
    {
        var lower = LayerId.New();
        var upper = LayerId.New();
        var document = new MapDocument(
            DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1920, 1080, "px", "#FF1E2430"),
            [
                new BaseColorLayer(LayerId.New(), "Фон", true, false, 1.0, "#FF1E2430"),
                new ObjectLayer(lower, "Нижний", lowerVisible, lowerLocked, 1.0, lowerObjects),
                new ObjectLayer(upper, "Верхний", upperVisible, upperLocked, 1.0, upperObjects ?? []),
            ]);
        var editor = new EditorViewModel(
            document, documentPath: null, startDirty: false,
            new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
            new FakePrompts(), new FakeLog());
        return (editor, lower, upper);
    }

    private static SceneObject Find(EditorViewModel editor, ObjectId id) =>
        DocumentEditor.FindObject(editor.Session.Document, id)!;

    // ---------------------------------------------------------------- click behaviour

    [Fact]
    public void Clicking_a_grouped_object_selects_the_whole_group()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Тестовая группа"));

        editor.SelectAt(50, 25);

        Assert.Equal(2, editor.SelectionCount);
        Assert.Contains(a.Id, editor.Session.SelectedObjectIds);
        Assert.Contains(b.Id, editor.Session.SelectedObjectIds);
    }

    [Fact]
    public void Inside_an_open_group_a_click_selects_the_child_only()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Тестовая группа"));
        editor.EnterSelectedGroup();
        Assert.NotNull(editor.OpenGroupId);

        editor.SelectAt(50, 25);

        Assert.Equal([a.Id], editor.Session.SelectedObjectIds);
    }

    [Fact]
    public void Ctrl_click_toggles_an_ungrouped_object()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.SelectAt(50, 25);

        editor.SelectAt(350, 25, additive: true);
        Assert.Equal(2, editor.SelectionCount);

        editor.SelectAt(350, 25, additive: true);
        Assert.Equal([a.Id], editor.Session.SelectedObjectIds);
    }

    [Fact]
    public void Escape_first_leaves_the_open_group_then_clears()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Тестовая группа"));
        editor.EnterSelectedGroup();
        editor.SelectAt(50, 25);

        Assert.True(editor.HandleEscape());
        Assert.Null(editor.OpenGroupId);
        Assert.Equal(2, editor.SelectionCount); // the whole group is selected again

        Assert.True(editor.HandleEscape());
        Assert.Equal(0, editor.SelectionCount);
    }

    // ---------------------------------------------------------------- Ctrl+A

    [Fact]
    public void Select_all_honours_search_and_type_filter_and_skips_hidden_and_locked()
    {
        var stamp = Object("Дуб", SceneObjectKind.Stamp, 0, 0);
        var stampOther = Object("Ель", SceneObjectKind.Stamp, 200, 0);
        var text = Object("Дубрава подпись", SceneObjectKind.Text, 400, 0);
        var hidden = Object("Дуб скрытый", SceneObjectKind.Stamp, 600, 0, visible: false);
        var locked = Object("Дуб заблокированный", SceneObjectKind.Stamp, 800, 0, locked: true);
        var (editor, _, _) = NewEditor([stamp, stampOther, text, hidden, locked]);

        editor.ObjectSearchText = "дуб";
        editor.ObjectTypeFilter = ObjectTypeFilter.Stamps;
        editor.SelectAllByFilter();

        Assert.Equal([stamp.Id], editor.Session.SelectedObjectIds);

        editor.ObjectSearchText = "";
        editor.SelectAllByFilter();
        Assert.Equal([stamp.Id, stampOther.Id], editor.Session.SelectedObjectIds);

        editor.ObjectTypeFilter = ObjectTypeFilter.All;
        editor.SelectAllByFilter();
        Assert.Equal(3, editor.SelectionCount); // hidden and locked never enter Ctrl+A
    }

    [Fact]
    public void Select_all_by_group_name_selects_the_closed_group_as_one_unit()
    {
        var groupId = Guid.NewGuid();
        var a = Object("Дуб", SceneObjectKind.Stamp, 0, 0) with
        {
            GroupId = groupId,
            GroupName = "Тёмный лес",
        };
        var b = Object("Ель", SceneObjectKind.Stamp, 300, 0) with
        {
            GroupId = groupId,
            GroupName = "Тёмный лес",
        };
        var outsider = Object("Камень", SceneObjectKind.Rectangle, 600, 0);
        var (editor, _, _) = NewEditor([a, b, outsider]);

        editor.ObjectSearchText = "тёмный лес";
        editor.SelectAllByFilter();

        Assert.Equal([a.Id, b.Id], editor.Session.SelectedObjectIds);
        Assert.Equal(groupId, editor.SelectedGroupId);
    }

    [Fact]
    public void Select_all_keeps_a_closed_group_atomic_when_only_one_member_matches_the_filter()
    {
        var groupId = Guid.NewGuid();
        var stamp = Object("Дуб", SceneObjectKind.Stamp, 0, 0) with
        {
            GroupId = groupId,
            GroupName = "Лес",
        };
        var text = Object("Подпись", SceneObjectKind.Text, 300, 0) with
        {
            GroupId = groupId,
            GroupName = "Лес",
        };
        var (editor, _, _) = NewEditor([stamp, text]);

        editor.ObjectTypeFilter = ObjectTypeFilter.Stamps;
        editor.SelectAllByFilter();

        Assert.Equal([stamp.Id, text.Id], editor.Session.SelectedObjectIds);
        Assert.Equal(groupId, editor.SelectedGroupId);
    }

    [Fact]
    public void Select_all_skips_a_closed_group_when_any_member_is_unavailable()
    {
        var groupId = Guid.NewGuid();
        var available = Object("Дуб", SceneObjectKind.Stamp, 0, 0) with
        {
            GroupId = groupId,
            GroupName = "Лес",
        };
        var locked = Object("Ель", SceneObjectKind.Stamp, 300, 0, locked: true) with
        {
            GroupId = groupId,
            GroupName = "Лес",
        };
        var (editor, _, _) = NewEditor([available, locked]);

        editor.ObjectSearchText = "дуб";
        editor.SelectAllByFilter();

        Assert.Empty(editor.Session.SelectedObjectIds);
    }

    [Fact]
    public void Select_all_inside_an_open_group_never_selects_outsiders()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var outsider = Object("C", SceneObjectKind.Rectangle, 600, 0);
        var (editor, _, _) = NewEditor([a, b, outsider]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Лес"));
        editor.EnterSelectedGroup();
        editor.Session.ClearSelection();

        editor.SelectAllByFilter();

        Assert.Equal([a.Id, b.Id], editor.Session.SelectedObjectIds);
    }

    // ---------------------------------------------------------------- groups

    [Fact]
    public void Group_needs_two_objects_and_uses_an_automatic_name_when_blank()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);

        editor.Session.SetSelection([a.Id]);
        Assert.False(editor.CanGroupSelection);
        Assert.False(editor.GroupSelection("Лес"));

        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.CanGroupSelection);
        Assert.True(editor.GroupSelection("   "));
        Assert.Equal("Группа · 2 объектов", Find(editor, a.Id).GroupName);
        Assert.Equal(1, editor.Session.UndoDepth);

        editor.Undo();
        editor.Session.SetSelection([a.Id, b.Id]);

        Assert.True(editor.GroupSelection("Лес"));
        Assert.NotNull(Find(editor, a.Id).GroupId);
        Assert.Equal("Лес", Find(editor, a.Id).GroupName);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Partial_regrouping_is_refused_with_a_message_and_no_history_entry()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var c = Object("C", SceneObjectKind.Rectangle, 600, 0);
        var (editor, _, _) = NewEditor([a, b, c]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Лес"));

        editor.Session.SetSelection([b.Id, c.Id]);
        Assert.False(editor.GroupSelection("Горы"));

        Assert.Contains("групп", editor.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Null(Find(editor, c.Id).GroupId);
    }

    [Fact]
    public void Selecting_a_full_group_exposes_its_identity_and_ungroups_in_one_action()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Лес"));

        Assert.NotNull(editor.SelectedGroupId);
        Assert.Equal("Лес", editor.SelectedGroupName);

        Assert.True(editor.UngroupSelection());
        Assert.Null(Find(editor, a.Id).GroupId);
        Assert.Null(Find(editor, a.Id).GroupName);
        Assert.Null(editor.SelectedGroupId);
    }

    [Fact]
    public void Ungroup_refuses_anything_but_one_fully_selected_group()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var c = Object("C", SceneObjectKind.Rectangle, 600, 0);
        var (editor, _, _) = NewEditor([a, b, c]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Лес"));

        // Group member plus an outsider is not "one whole group".
        editor.Session.SetSelection([a.Id, b.Id, c.Id]);
        Assert.False(editor.UngroupSelection());
        Assert.NotNull(Find(editor, a.Id).GroupId);
    }

    [Fact]
    public void Rename_group_applies_to_every_member_and_is_undoable()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Лес"));

        Assert.True(editor.RenameSelectedGroup("Тёмный лес"));
        Assert.Equal("Тёмный лес", Find(editor, a.Id).GroupName);
        Assert.Equal("Тёмный лес", Find(editor, b.Id).GroupName);

        editor.Undo();
        Assert.Equal("Лес", Find(editor, a.Id).GroupName);
    }

    // ---------------------------------------------------------------- multi transforms

    [Fact]
    public void Multi_nudge_moves_every_member_as_one_history_entry()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 100);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);

        editor.NudgeSelectedObject(10, -5);

        Assert.Equal(10, Find(editor, a.Id).X);
        Assert.Equal(-5, Find(editor, a.Id).Y);
        Assert.Equal(310, Find(editor, b.Id).X);
        Assert.Equal(95, Find(editor, b.Id).Y);
        Assert.Equal(1, editor.Session.UndoDepth);

        editor.Undo();
        Assert.Equal(0, Find(editor, a.Id).X);
        Assert.Equal(300, Find(editor, b.Id).X);
    }

    [Fact]
    public void Multi_resize_scales_around_the_combined_centre()
    {
        // Combined bounds: 0..400 x 0..150; centre (200, 75).
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 100);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);

        editor.ResizeSelectedObject(1, largerStep: true); // factor 1.1

        var movedA = Find(editor, a.Id);
        Assert.Equal(110, movedA.Width, 6);
        Assert.Equal(55, movedA.Height, 6);
        // A's centre (50,25) -> (200 + 1.1*(50-200), 75 + 1.1*(25-75)) = (35, 20).
        Assert.Equal(35, movedA.X + (movedA.Width / 2), 6);
        Assert.Equal(20, movedA.Y + (movedA.Height / 2), 6);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void Multi_rotate_turns_member_centres_and_their_own_angles()
    {
        // Combined bounds 0..400 x 0..50; centre (200, 25).
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);

        editor.RotateSelectedObject(1, largerStep: true); // +15 degrees

        Assert.Equal(15, Find(editor, a.Id).RotationDegrees, 6);
        Assert.Equal(15, Find(editor, b.Id).RotationDegrees, 6);
        // Centres moved around (200,25): A's centre (50,25) is 150 left of the pivot.
        var movedA = Find(editor, a.Id);
        Assert.Equal(200 - (150 * Math.Cos(15 * Math.PI / 180)), movedA.X + (movedA.Width / 2), 6);
        Assert.Equal(25 - (150 * Math.Sin(15 * Math.PI / 180)), movedA.Y + (movedA.Height / 2), 6);
        Assert.Equal(1, editor.Session.UndoDepth);
    }

    [Fact]
    public void A_no_move_drag_creates_no_history_entry()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);

        Assert.True(editor.BeginDrag(10, 10));
        editor.EndDrag();

        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public void A_locked_or_hidden_member_blocks_the_whole_geometric_change()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var locked = Object("L", SceneObjectKind.Rectangle, 300, 0, locked: true);
        var (editor, _, _) = NewEditor([a, locked]);
        editor.Session.SetSelection([a.Id, locked.Id]);

        editor.NudgeSelectedObject(10, 0);
        editor.ResizeSelectedObject(1, largerStep: true);
        editor.RotateSelectedObject(1, largerStep: true);

        Assert.Equal(0, Find(editor, a.Id).X);
        Assert.Equal(100, Find(editor, a.Id).Width);
        Assert.Equal(0, Find(editor, a.Id).RotationDegrees);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.False(string.IsNullOrEmpty(editor.StatusDetail));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_hidden_or_locked_layer_blocks_the_whole_geometric_change(
        bool upperVisible, bool upperLocked)
    {
        var groupId = Guid.NewGuid();
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0) with
        {
            GroupId = groupId,
            GroupName = "Межслойная группа",
        };
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0) with
        {
            GroupId = groupId,
            GroupName = "Межслойная группа",
        };
        var (editor, _, _) = NewEditor(
            [a], [b], upperVisible: upperVisible, upperLocked: upperLocked);
        editor.Session.SetSelection([a.Id, b.Id]);

        editor.NudgeSelectedObject(10, 0);

        Assert.Equal(0, Find(editor, a.Id).X);
        Assert.Equal(300, Find(editor, b.Id).X);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Contains("сло", editor.StatusDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sublayer_and_opacity_apply_to_the_whole_selection_at_once()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);

        editor.AdjustSelectedSublayer(1);
        editor.AdjustSelectedOpacity(-1);

        Assert.Equal(1, Find(editor, a.Id).Sublayer);
        Assert.Equal(1, Find(editor, b.Id).Sublayer);
        Assert.Equal(0.95, Find(editor, a.Id).ObjectOpacity, 6);
        Assert.Equal(0.95, Find(editor, b.Id).ObjectOpacity, 6);
        Assert.Equal(2, editor.Session.UndoDepth);
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public void Delete_spans_layers_and_undo_restores_both()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var c = Object("C", SceneObjectKind.Rectangle, 600, 0);
        var (editor, lower, upper) = NewEditor([a], [c]);
        editor.Session.SetSelection([a.Id, c.Id]);

        editor.DeleteSelectedObject();

        Assert.Null(DocumentEditor.FindObject(editor.Session.Document, a.Id));
        Assert.Null(DocumentEditor.FindObject(editor.Session.Document, c.Id));
        Assert.Equal(0, editor.SelectionCount);
        Assert.Equal(1, editor.Session.UndoDepth);

        editor.Undo();
        Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(lower)!).Objects);
        Assert.Single(((ObjectLayer)editor.Session.Document.FindLayer(upper)!).Objects);
    }

    [Fact]
    public void Deleting_group_members_dissolves_a_singleton_remainder()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var c = Object("C", SceneObjectKind.Rectangle, 600, 0);
        var (editor, _, _) = NewEditor([a, b, c]);
        editor.Session.SetSelection([a.Id, b.Id, c.Id]);
        Assert.True(editor.GroupSelection("Лес"));

        editor.Session.SetSelection([a.Id, b.Id]);
        editor.DeleteSelectedObject();

        Assert.Null(Find(editor, c.Id).GroupId);
        Assert.Null(Find(editor, c.Id).GroupName);

        editor.Undo();
        Assert.Equal("Лес", Find(editor, c.Id).GroupName);
    }

    // ---------------------------------------------------------------- marquee

    [Fact]
    public void Marquee_selects_intersecting_objects_and_ctrl_marquee_adds()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var far = Object("F", SceneObjectKind.Rectangle, 5000, 5000);
        var (editor, _, _) = NewEditor([a, b, far]);

        Assert.True(editor.BeginMarquee(-50, -50, additive: false));
        editor.UpdateMarquee(150, 100);
        Assert.NotNull(editor.MarqueeWorld);
        editor.EndMarquee();

        Assert.Equal([a.Id], editor.Session.SelectedObjectIds);
        Assert.Null(editor.MarqueeWorld);

        Assert.True(editor.BeginMarquee(250, -50, additive: true));
        editor.UpdateMarquee(450, 100);
        editor.EndMarquee();

        Assert.Equal([a.Id, b.Id], editor.Session.SelectedObjectIds);
    }

    [Fact]
    public void Marquee_catching_a_group_member_selects_the_whole_group()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 3000, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Лес"));
        editor.Session.ClearSelection();

        Assert.True(editor.BeginMarquee(-50, -50, additive: false));
        editor.UpdateMarquee(150, 100); // touches only A
        editor.EndMarquee();

        Assert.Equal(2, editor.SelectionCount);
    }

    // ---------------------------------------------------------------- panel state

    [Fact]
    public void Multi_selection_hides_the_single_inspector_and_reports_a_count()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);

        editor.Session.SetSelection([a.Id]);
        Assert.True(editor.ShowSingleInspector);

        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.False(editor.ShowSingleInspector);
        Assert.Contains("2", editor.SelectionStatus);
    }

    [Fact]
    public void Mixed_visibility_and_lock_report_different_values_not_a_fake_one()
    {
        var visible = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var hidden = Object("B", SceneObjectKind.Rectangle, 300, 0, visible: false);
        var (editor, _, _) = NewEditor([visible, hidden]);
        editor.Session.SetSelection([visible.Id, hidden.Id]);

        Assert.Equal("Разные значения", editor.SelectionVisibilitySummary);
        Assert.Equal("Свободен", editor.SelectionLockSummary);
    }

    [Fact]
    public void Visibility_toggle_over_a_mixed_selection_shows_everything_first()
    {
        var visible = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var hidden = Object("B", SceneObjectKind.Rectangle, 300, 0, visible: false);
        var (editor, _, _) = NewEditor([visible, hidden]);
        editor.Session.SetSelection([visible.Id, hidden.Id]);

        editor.ToggleSelectedObjectVisibility();
        Assert.True(Find(editor, visible.Id).Visible);
        Assert.True(Find(editor, hidden.Id).Visible);
        Assert.Equal(1, editor.Session.UndoDepth);

        editor.ToggleSelectedObjectVisibility();
        Assert.False(Find(editor, visible.Id).Visible);
        Assert.False(Find(editor, hidden.Id).Visible);
    }

    [Fact]
    public void Lock_toggle_covers_the_whole_selection()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var locked = Object("B", SceneObjectKind.Rectangle, 300, 0, locked: true);
        var (editor, _, _) = NewEditor([a, locked]);
        editor.Session.SetSelection([a.Id, locked.Id]);

        editor.ToggleSelectedObjectLock();
        Assert.True(Find(editor, a.Id).Locked);
        Assert.True(Find(editor, locked.Id).Locked);

        editor.ToggleSelectedObjectLock();
        Assert.False(Find(editor, a.Id).Locked);
        Assert.False(Find(editor, locked.Id).Locked);
    }

    [Fact]
    public void Edits_over_grouped_members_keep_the_group_name()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);
        Assert.True(editor.GroupSelection("Лес"));

        // Every copy path of the view-model must carry GroupName, or the model invariant
        // (same GroupId - same GroupName) throws in the middle of an ordinary edit.
        editor.NudgeSelectedObject(5, 5);
        Assert.Equal("Лес", Find(editor, a.Id).GroupName);

        editor.ToggleSelectedObjectVisibility();
        Assert.Equal("Лес", Find(editor, a.Id).GroupName);
        editor.ToggleSelectedObjectVisibility(); // shown again for the edits below

        editor.ToggleSelectedObjectLock();
        Assert.Equal("Лес", Find(editor, a.Id).GroupName);
        editor.ToggleSelectedObjectLock();

        editor.AdjustSelectedSublayer(1);
        editor.AdjustSelectedOpacity(-1);
        Assert.Equal("Лес", Find(editor, b.Id).GroupName);

        // The single-object inspector path over one child inside the open group.
        editor.EnterSelectedGroup();
        editor.SelectObject(a.Id);
        editor.RefreshInspector();
        editor.XText = "42";
        Assert.True(editor.CommitInspector());
        Assert.Equal("Лес", Find(editor, a.Id).GroupName);
        Assert.Equal(42, Find(editor, a.Id).X);
    }

    // ---------------------------------------------------------------- restore

    [Fact]
    public void Restore_shortcut_brings_back_the_whole_previous_set()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 0);
        var (editor, _, _) = NewEditor([a, b]);
        editor.Session.SetSelection([a.Id, b.Id]);

        editor.DeselectObject();
        Assert.Equal(0, editor.SelectionCount);

        editor.RestoreLastSelection();
        Assert.Equal(2, editor.SelectionCount);
    }

    // ---------------------------------------------------------------- selection frame

    [Fact]
    public void Selection_frame_exists_only_for_a_multi_selection()
    {
        var a = Object("A", SceneObjectKind.Rectangle, 0, 0);
        var b = Object("B", SceneObjectKind.Rectangle, 300, 100);
        var (editor, _, _) = NewEditor([a, b]);

        editor.Session.SetSelection([a.Id]);
        Assert.Null(editor.SelectionFrame);

        editor.Session.SetSelection([a.Id, b.Id]);
        var frame = editor.SelectionFrame;
        Assert.NotNull(frame);
        Assert.Equal(0, frame!.Value.Left);
        Assert.Equal(400, frame.Value.Right);
        Assert.Equal(150, frame.Value.Bottom);
    }
}
