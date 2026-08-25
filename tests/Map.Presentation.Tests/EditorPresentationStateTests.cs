using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

/// <summary>
/// The wording and the derived state the rebuilt interface reads. These assertions exist because
/// the screen must never show a raw enum member, a GUID or a blend key, and because the status
/// line has to stay honest about how much unsaved work there is.
/// </summary>
public class EditorPresentationStateTests
{
    private static EditorViewModel NewEditor(bool startDirty = false) => new(
        TestData.Document(), documentPath: null, startDirty,
        new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog(),
        clock: () => new DateTimeOffset(2026, 8, 25, 15, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Selection_status_names_the_object_and_never_leaks_its_identifier()
    {
        var editor = NewEditor();
        Assert.Equal("Ничего не выбрано", editor.SelectionStatus);
        Assert.Null(editor.SelectionKind);

        editor.AddObject(SceneObjectKind.Rectangle);
        var selected = editor.Session.SelectedObject!;

        Assert.Equal(SceneObjectKind.Rectangle, editor.SelectionKind);
        Assert.Contains(selected.Name, editor.SelectionStatus);
        Assert.DoesNotContain(selected.Id.ToString(), editor.SelectionStatus);
        Assert.DoesNotContain("Rectangle", editor.SelectionStatus);

        // A default object is named after its kind, so the status must not say the word twice.
        Assert.Equal("Прямоугольник", editor.SelectionStatus);
    }

    [Fact]
    public void A_renamed_object_is_announced_with_both_its_name_and_its_kind()
    {
        var editor = NewEditor();
        editor.AddObject(SceneObjectKind.Ellipse);
        var selected = editor.Session.SelectedObject!;

        Assert.Equal("Эллипс", EditorLabels.DescribeObject(selected));
        Assert.Null(EditorLabels.ObjectSubtitle(selected));

        var renamed = selected with { Name = "Озеро Тишины" };
        Assert.Equal("Озеро Тишины · Эллипс", EditorLabels.DescribeObject(renamed));
        Assert.Equal("Эллипс", EditorLabels.ObjectSubtitle(renamed));
    }

    [Fact]
    public void Unsaved_state_reports_how_many_edits_are_at_risk()
    {
        var editor = NewEditor();
        Assert.Equal(0, editor.PendingChanges);
        Assert.Equal("Сохранено", editor.SaveSummary);

        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.Equal(1, editor.PendingChanges);
        Assert.Equal("Не сохранено: 1 изменение", editor.SaveSummary);

        editor.AddObject(SceneObjectKind.Ellipse);
        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.Equal(3, editor.PendingChanges);
        Assert.Equal("Не сохранено: 3 изменения", editor.SaveSummary);

        for (var index = 0; index < 3; index++)
        {
            editor.AddObject(SceneObjectKind.Ellipse);
        }

        Assert.Equal(6, editor.PendingChanges);
        Assert.Equal("Не сохранено: 6 изменений", editor.SaveSummary);
    }

    [Fact]
    public void Undo_and_redo_availability_is_published_for_the_toolbar()
    {
        var editor = NewEditor();
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);

        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);

        editor.Undo();
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void Undo_and_redo_restore_the_saved_summary_instead_of_counting_history_actions()
    {
        var editor = NewEditor();
        editor.AddObject(SceneObjectKind.Rectangle);
        Assert.Equal(1, editor.PendingChanges);
        Assert.Equal("Не сохранено: 1 изменение", editor.SaveSummary);

        editor.Undo();
        Assert.Equal(0, editor.PendingChanges);
        Assert.Equal(SaveState.Saved, editor.SaveState);
        Assert.Equal("Сохранено", editor.SaveSummary);

        editor.Redo();
        Assert.Equal(1, editor.PendingChanges);
        Assert.Equal(SaveState.Unsaved, editor.SaveState);
        Assert.Equal("Не сохранено: 1 изменение", editor.SaveSummary);
    }

    [Fact]
    public void Status_line_reports_the_active_layer_by_name_and_the_grid_by_state()
    {
        var editor = NewEditor();
        Assert.Equal("Сетка выключена", editor.GridSummary);

        editor.AddObjectLayer();
        var layer = editor.ActiveLayer!;
        Assert.Equal($"Слой: {layer.Name}", editor.ActiveLayerSummary);
        Assert.DoesNotContain(layer.Id.ToString(), editor.ActiveLayerSummary);

        editor.AddGridLayer();
        Assert.Equal("Сетка вкл · привязка вкл", editor.GridSummary);

        editor.ToggleActiveLayerVisibility();
        Assert.Equal("Сетка выключена", editor.GridSummary);
    }

    [Fact]
    public void Every_tool_has_a_name_a_single_key_and_a_first_time_hint()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tool in Enum.GetValues<EditorTool>())
        {
            var name = EditorLabels.Tool(tool);
            var shortcut = EditorLabels.ToolShortcut(tool);
            var hint = EditorLabels.ToolHint(tool);

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(hint));
            Assert.Single(shortcut);
            Assert.True(keys.Add(shortcut), $"Tool key '{shortcut}' is used twice.");
            Assert.DoesNotContain(tool.ToString(), name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Object_and_layer_kinds_are_translated_rather_than_printed_as_enum_members()
    {
        foreach (var kind in Enum.GetValues<SceneObjectKind>())
        {
            var label = EditorLabels.ObjectKind(kind);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotContain(kind.ToString(), label, StringComparison.Ordinal);
        }

        foreach (var geometry in Enum.GetValues<GridGeometry>())
        {
            Assert.DoesNotContain(geometry.ToString(), EditorLabels.GridGeometry(geometry), StringComparison.Ordinal);
        }

        foreach (var shape in Enum.GetValues<LandShapeKind>())
        {
            Assert.DoesNotContain(shape.ToString(), EditorLabels.LandShape(shape), StringComparison.Ordinal);
        }

        Assert.Equal("Обычное", EditorLabels.Blend("Normal"));
        Assert.Equal("Умножение", EditorLabels.Blend("Multiply"));
        Assert.Equal("Осветление", EditorLabels.Blend("Screen"));
    }

    [Fact]
    public void Layer_commands_are_named_after_what_they_will_do_next()
    {
        Assert.Equal("Скрыть слой", EditorLabels.VisibilityCommand(visible: true));
        Assert.Equal("Показать слой", EditorLabels.VisibilityCommand(visible: false));
        Assert.Equal("Снять блокировку", EditorLabels.LockCommand(locked: true));
        Assert.Equal("Заблокировать слой", EditorLabels.LockCommand(locked: false));
    }

    [Theory]
    [InlineData(1, "изменение")]
    [InlineData(2, "изменения")]
    [InlineData(4, "изменения")]
    [InlineData(5, "изменений")]
    [InlineData(11, "изменений")]
    [InlineData(12, "изменений")]
    [InlineData(14, "изменений")]
    [InlineData(21, "изменение")]
    [InlineData(22, "изменения")]
    [InlineData(25, "изменений")]
    [InlineData(101, "изменение")]
    [InlineData(111, "изменений")]
    public void Change_counts_agree_with_Russian_plural_rules(long count, string expectedWord)
    {
        Assert.Equal($"{count} {expectedWord}", EditorLabels.Changes(count));
    }

    [Fact]
    public void Selection_kind_switches_with_the_selection_so_the_inspector_can_follow_it()
    {
        var editor = NewEditor();
        editor.Session.Viewport.Resize(1000, 700);

        editor.AddLine();
        Assert.Equal(SceneObjectKind.Line, editor.SelectionKind);

        editor.AddText();
        Assert.Equal(SceneObjectKind.Text, editor.SelectionKind);

        editor.AddShape(ShapePrimitiveKind.Rectangle);
        Assert.Equal(SceneObjectKind.Shape, editor.SelectionKind);

        editor.DeleteSelectedObject();
        Assert.Null(editor.SelectionKind);
    }
}
