using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public class KeyboardShortcutBehaviorTests
{
    private static EditorViewModel NewEditor() => new(
        TestData.Document(), documentPath: null, startDirty: false,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(),
        new FakePrompts(), new FakeLog());

    [Fact]
    public void Default_bindings_match_the_observed_compatibility_keys()
    {
        var shortcuts = new EditorShortcutSettings();

        Assert.Equal("V", shortcuts.Get("select"));
        Assert.Equal("M", shortcuts.Get("mask"));
        Assert.Equal("B", shortcuts.Get("brush"));
        Assert.Equal("O", shortcuts.Get("stamp"));
        Assert.Equal("R", shortcuts.Get("shape"));
        Assert.Equal("L", shortcuts.Get("line"));
        Assert.Equal("T", shortcuts.Get("text"));
        Assert.Equal("N", shortcuts.Get("notes"));
        Assert.Equal("G", shortcuts.Get("grid-snap"));
        Assert.Equal("Ctrl+Z", shortcuts.Get("undo"));
        Assert.Equal("Ctrl+Shift+Z", shortcuts.Get("redo"));
        Assert.Equal("Tab", shortcuts.Get("previous-tool"));
    }

    [Fact]
    public void Previous_tool_toggles_the_last_two_canvas_tools()
    {
        var editor = NewEditor();
        editor.ActiveTool = EditorTool.Brush;
        editor.ActiveTool = EditorTool.Text;

        editor.SelectPreviousTool();
        Assert.Equal(EditorTool.Brush, editor.ActiveTool);

        editor.SelectPreviousTool();
        Assert.Equal(EditorTool.Text, editor.ActiveTool);
    }

    [Fact]
    public void Selection_shortcuts_are_undoable_and_restore_the_last_selection()
    {
        var editor = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        var original = Assert.IsType<SceneObject>(editor.Session.SelectedObject);
        var centerX = original.X + original.Width / 2;
        var centerY = original.Y + original.Height / 2;

        editor.NudgeSelectedObject(10, -10);
        Assert.Equal(original.X + 10, editor.Session.SelectedObject!.X);
        Assert.Equal(original.Y - 10, editor.Session.SelectedObject.Y);

        editor.ResizeSelectedObject(1, largerStep: true);
        var resized = editor.Session.SelectedObject!;
        Assert.Equal(centerX + 10, resized.X + resized.Width / 2, 6);
        Assert.Equal(centerY - 10, resized.Y + resized.Height / 2, 6);

        editor.RotateSelectedObject(1, largerStep: true);
        editor.AdjustSelectedSublayer(1);
        editor.AdjustSelectedOpacity(-1);
        Assert.Equal(15, editor.Session.SelectedObject!.RotationDegrees);
        Assert.Equal(1, editor.Session.SelectedObject.Sublayer);
        Assert.Equal(0.95, editor.Session.SelectedObject.ObjectOpacity, 6);

        editor.DeselectObject();
        Assert.Null(editor.Session.SelectedObject);
        editor.RestoreLastSelection();
        Assert.Equal(original.Id, editor.Session.SelectedObjectId);
    }

    [Fact]
    public void Brush_text_mask_and_grid_adjustments_change_real_editor_state()
    {
        var editor = NewEditor();

        editor.AdjustBrushSize(1);
        editor.AdjustBrushOpacity(-1);
        editor.AdjustBrushSoftness(1);
        Assert.Equal(25, editor.BrushSettings.Radius);
        Assert.Equal(0.95, editor.BrushSettings.Opacity, 6);
        Assert.Equal(0.55, editor.BrushSettings.Softness, 6);

        editor.ToggleMaskPaintMode();
        Assert.Equal(Map.Core.Editing.MaskPaintMode.Reveal, editor.MaskMode);

        editor.AddText();
        var textBefore = editor.Session.SelectedObject!.Text!;
        editor.AdjustSelectedTextFontSize(1);
        editor.AdjustSelectedTextCurve(1, largerStep: true);
        Assert.Equal(textBefore.FontSize + 1, editor.Session.SelectedObject!.Text!.FontSize);
        Assert.Equal(textBefore.Curve + 10, editor.Session.SelectedObject.Text.Curve);

        editor.AddGridLayer();
        var gridLayer = Assert.IsType<GridLayer>(editor.ActiveLayer);
        Assert.True(gridLayer.Grids[0].SnapEnabled);
        editor.ToggleGridSnap();
        Assert.False(Assert.IsType<GridLayer>(editor.ActiveLayer).Grids[0].SnapEnabled);
        editor.Undo();
        Assert.True(Assert.IsType<GridLayer>(editor.ActiveLayer).Grids[0].SnapEnabled);
    }
}
