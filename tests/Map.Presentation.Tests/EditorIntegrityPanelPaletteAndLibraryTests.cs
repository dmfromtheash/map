using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// Regressions for the three remaining reported defects: a selected object whose properties the
/// panel refused to show, a colour edit that had no visual control and no preview/commit split,
/// and a recent card whose file was gone and which therefore could never be removed.
/// </summary>
public sealed class EditorIntegrityPanelPaletteAndLibraryTests
{
    // ------------------------------------------------------- context panel scope

    [Theory]
    [InlineData(EditorTool.Shape)]
    [InlineData(EditorTool.Line)]
    [InlineData(EditorTool.Text)]
    [InlineData(EditorTool.Brush)]
    [InlineData(EditorTool.Stamp)]
    [InlineData(EditorTool.Select)]
    public void A_selected_object_shows_its_inspector_whatever_tool_is_active(EditorTool tool)
    {
        // The panel used to require ActiveTool == Select, so drawing a shape left the user looking
        // at the Shape tool while the shape was already selected and its properties unreachable.
        Assert.Equal(
            EditorContextPanelKind.ObjectInspector,
            EditorContextPanel.Resolve(tool, selectionCount: 1, hasSelectionKind: true, preferToolPanel: false));
    }

    [Fact]
    public void The_tool_settings_stay_one_click_away_from_a_selection()
    {
        Assert.True(EditorContextPanel.ToggleApplies(1));
        Assert.Equal(
            EditorContextPanelKind.ToolPanel,
            EditorContextPanel.Resolve(EditorTool.Shape, 1, true, preferToolPanel: true));

        // With nothing selected there is no second half to switch to.
        Assert.False(EditorContextPanel.ToggleApplies(0));
        Assert.Equal(
            EditorContextPanelKind.SelectIdle,
            EditorContextPanel.Resolve(EditorTool.Select, 0, false, preferToolPanel: false));
        Assert.Equal(
            EditorContextPanelKind.ToolPanel,
            EditorContextPanel.Resolve(EditorTool.Shape, 0, false, preferToolPanel: false));
    }

    [Fact]
    public void Several_selected_objects_show_the_shared_inspector_whatever_tool_is_active()
    {
        Assert.Equal(
            EditorContextPanelKind.MultiSelectionInspector,
            EditorContextPanel.Resolve(EditorTool.Brush, selectionCount: 3, hasSelectionKind: false,
                preferToolPanel: false));
    }

    // ------------------------------------------------------------------ palette

    [Fact]
    public void A_colour_gesture_previews_without_history_and_commits_exactly_once()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        var revision = editor.Session.Revision;

        // Dragging inside the picker: the canvas shows the value, the document keeps its own.
        editor.SetShapeFillColor("#FF884422", commit: false);
        editor.SetShapeFillColor("#FF224488", commit: false);
        Assert.NotNull(editor.DragPreview);
        Assert.Equal(revision, editor.Session.Revision);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(shape.Shape!.Fill.Color, Current(editor).Shape!.Fill.Color);

        editor.SetShapeFillColor("#FF224488", commit: true);
        Assert.Null(editor.DragPreview);
        Assert.Equal(1, editor.Session.UndoDepth);
        Assert.Equal("#FF224488", Current(editor).Shape!.Fill.Color);

        editor.Undo();
        Assert.Equal(shape.Shape!.Fill.Color, Current(editor).Shape!.Fill.Color);
    }

    [Fact]
    public void Cancelling_a_colour_gesture_restores_the_original_and_writes_no_history()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        var original = shape.Shape!.Fill.Color;

        editor.SetShapeFillColor("#FFAA3311", commit: false);
        editor.CancelAppearanceGesture();

        Assert.Null(editor.DragPreview);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Equal(original, Current(editor).Shape!.Fill.Color);
    }

    [Fact]
    public void An_invalid_colour_never_reaches_the_object()
    {
        var editor = EditorWithShape(out var shape);
        editor.SelectObject(shape.Id);
        var original = shape.Shape!.Fill.Color;

        editor.SetShapeFillColor("не цвет");

        Assert.Equal(original, Current(editor).Shape!.Fill.Color);
        Assert.Equal(0, editor.Session.UndoDepth);
        Assert.Contains("#RRGGBB", editor.InspectorError);
    }

    [Fact]
    public void Every_colour_the_picker_can_produce_round_trips_through_the_document_contract()
    {
        // The picker only ever emits the canonical "#AARRGGBB" form; the shorthand stays a valid
        // extra way in rather than the only one.
        Assert.Equal("#FF224488", AppearanceColor.Normalize("#224488"));
        Assert.Equal("#80224488", AppearanceColor.Normalize("#80224488"));
        Assert.Equal("#FF224488", AppearanceColor.Normalize("224488"));
        Assert.Null(AppearanceColor.Normalize("#12345"));
        Assert.Null(AppearanceColor.Normalize("zzzzzz"));
    }

    [Fact]
    public void The_map_background_colour_is_editable_and_undoable()
    {
        // Only the wizard could ever set this: the layer sheet hid itself for the base layer.
        var editor = EditorWithShape(out _);
        var original = editor.Session.Document.BaseColor.Color;

        editor.SetBaseColor("#FF102030");

        Assert.Equal("#FF102030", editor.Session.Document.BaseColor.Color);
        Assert.Equal(1, editor.Session.UndoDepth);
        editor.Undo();
        Assert.Equal(original, editor.Session.Document.BaseColor.Color);
    }

    // ---------------------------------------------------------- ghost map cards

    [Fact]
    public async Task A_recent_card_whose_file_is_gone_loses_its_entry_and_says_so()
    {
        var recent = Recent("C:\\synthetic\\ghost.mapproj", "C:\\synthetic\\real.mapproj");
        var trash = new FakeProjectTrash();
        trash.MissingPaths.Add("C:\\synthetic\\ghost.mapproj");
        var prompts = new FakePrompts { NextDeleteProjectConfirmation = true };
        var dashboard = Dashboard(recent, prompts, trash);
        var ghost = new RecentCard(
            "C:\\synthetic\\ghost.mapproj", "Призрак", DateTimeOffset.UnixEpoch, IsAvailable: false, null);

        Assert.True(await dashboard.DeleteAsync(ghost));

        // Exactly one path was named, the stale entry is gone, and the other map is untouched.
        Assert.Equal(["C:\\synthetic\\ghost.mapproj"], trash.Paths);
        Assert.DoesNotContain(recent.Items, item => item.Path == "C:\\synthetic\\ghost.mapproj");
        Assert.Contains(recent.Items, item => item.Path == "C:\\synthetic\\real.mapproj");
        Assert.Equal("", dashboard.LastError);
        Assert.Contains("Удалена только запись", dashboard.LastNotice);
        Assert.True(prompts.LastDeletePromptSaidFileMissing);

        // Refreshing must not resurrect it, and deleting again must stay harmless.
        dashboard.Refresh();
        Assert.DoesNotContain(dashboard.RecentCards, card => card.Path == ghost.Path);
        Assert.True(await dashboard.DeleteAsync(ghost));
        Assert.Single(recent.Items);
    }

    [Fact]
    public async Task An_existing_recent_card_still_goes_to_the_recycle_bin_by_its_exact_path()
    {
        var recent = Recent("C:\\synthetic\\real.mapproj", "C:\\synthetic\\other.mapproj");
        var trash = new FakeProjectTrash();
        var prompts = new FakePrompts { NextDeleteProjectConfirmation = true };
        var dashboard = Dashboard(recent, prompts, trash);
        var card = new RecentCard(
            "C:\\synthetic\\real.mapproj", "Карта", DateTimeOffset.UnixEpoch, IsAvailable: true, null);

        Assert.True(await dashboard.DeleteAsync(card));

        Assert.Equal(["C:\\synthetic\\real.mapproj"], trash.Paths);
        Assert.False(prompts.LastDeletePromptSaidFileMissing);
        Assert.Contains("перемещена в Корзину", dashboard.LastNotice);
        Assert.Equal(["C:\\synthetic\\other.mapproj"], recent.Items.Select(item => item.Path));
    }

    [Fact]
    public async Task A_file_that_vanishes_between_the_check_and_the_trash_call_is_a_stale_entry()
    {
        var recent = Recent("C:\\synthetic\\racing.mapproj");
        var trash = new FakeProjectTrash();
        trash.BeforeMove = path => trash.MissingPaths.Add(path);
        var prompts = new FakePrompts { NextDeleteProjectConfirmation = true };
        var dashboard = Dashboard(recent, prompts, trash);

        // The card still claims to be available, so the prompt promises a Recycle Bin move...
        var card = new RecentCard(
            "C:\\synthetic\\racing.mapproj", "Гонка", DateTimeOffset.UnixEpoch, IsAvailable: true, null);
        Assert.True(await dashboard.DeleteAsync(card));

        // ...and the result still tells the truth about what actually happened.
        Assert.False(prompts.LastDeletePromptSaidFileMissing);
        Assert.Contains("Удалена только запись", dashboard.LastNotice);
        Assert.Empty(recent.Items);
        Assert.Equal("", dashboard.LastError);
    }

    [Fact]
    public async Task A_real_io_failure_keeps_both_the_file_and_the_entry()
    {
        var recent = Recent("C:\\synthetic\\locked.mapproj");
        var trash = new FakeProjectTrash
        {
            NextFailure = new UnauthorizedAccessException("Отказано в доступе."),
        };
        var prompts = new FakePrompts { NextDeleteProjectConfirmation = true };
        var dashboard = Dashboard(recent, prompts, trash);
        var card = new RecentCard(
            "C:\\synthetic\\locked.mapproj", "Занятая", DateTimeOffset.UnixEpoch, IsAvailable: true, null);

        Assert.False(await dashboard.DeleteAsync(card));

        Assert.Single(recent.Items);
        Assert.Contains("Не удалось переместить", dashboard.LastError);
        Assert.Equal("", dashboard.LastNotice);
    }

    [Fact]
    public async Task A_declined_confirmation_changes_nothing_at_all()
    {
        var recent = Recent("C:\\synthetic\\ghost.mapproj");
        var trash = new FakeProjectTrash();
        trash.MissingPaths.Add("C:\\synthetic\\ghost.mapproj");
        var prompts = new FakePrompts { NextDeleteProjectConfirmation = false };
        var dashboard = Dashboard(recent, prompts, trash);
        var card = new RecentCard(
            "C:\\synthetic\\ghost.mapproj", "Призрак", DateTimeOffset.UnixEpoch, IsAvailable: false, null);

        Assert.False(await dashboard.DeleteAsync(card));

        Assert.Empty(trash.Paths);
        Assert.Single(recent.Items);
        Assert.Equal("", dashboard.LastNotice);
    }

    // ------------------------------------------------------------------- setup

    private static FakeRecentMaps Recent(params string[] paths)
    {
        var recent = new FakeRecentMaps();
        foreach (var path in paths)
        {
            recent.Touch(path, Guid.NewGuid(), Path.GetFileName(path), DateTimeOffset.UnixEpoch);
        }

        return recent;
    }

    private static DashboardViewModel Dashboard(
        FakeRecentMaps recent, FakePrompts prompts, FakeProjectTrash trash) =>
        new(new FakeProjectStore(), recent, new FakeLog(), new FakeFilePicker(), prompts, trash);

    private static SceneObject Current(EditorViewModel editor) => editor.Session.SelectedObject!;

    private static EditorViewModel EditorWithShape(out SceneObject shape)
    {
        shape = VectorGeometry.CreateShape(
            ObjectId.New(), "Фигура 1", ShapePrimitiveKind.Rectangle,
            new WorldBounds(100, 100, 300, 260));
        var layer = new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [shape]);
        var document = new MapDocument(DocumentId.New(), "integrity", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Фон", true, false, 1, "#FFFFFFFF"), layer]);
        return new EditorViewModel(document, null, true, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), availableFontFamilies: ["Arial"]);
    }
}
