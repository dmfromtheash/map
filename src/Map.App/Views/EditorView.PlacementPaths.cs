using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private ComboBox? _pathPicker;
    private TextBlock? _pathSummary;
    private Control? _pathPanel;
    private Button? _pathFinish, _pathDetach;
    private bool _syncingPaths;

    private Control BuildEditablePathPanel()
    {
        _pathPicker = new ComboBox { Name = "EditablePathPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            PlaceholderText = "Пути этой карты" };
        _pathPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingPaths && _pathPicker.SelectedItem is PathChoice item) _editor.SelectEditablePath(item.Id);
        };
        _pathSummary = new TextBlock { Name = "EditablePathSummary", TextWrapping = TextWrapping.Wrap };
        _pathFinish = Ui.TextButton("FinishEditablePathButton", "Завершить путь", () => _editor.FinishEditablePath(), "compact");
        _pathDetach = Ui.TextButton("DetachPathObjectButton", "Отделить выбранный объект", _editor.DetachSelectedPathObject, "compact");
        _pathPanel = new StackPanel
        {
            Spacing = 6, Children =
            {
                _pathPicker, _pathSummary,
                Ui.TextButton("StartEditablePathButton", "Новый редактируемый путь", _editor.StartEditablePath, "compact"),
                _pathFinish,
                new TextBlock { Text = "Клики — узлы · Enter — завершить · Esc — отменить. Перетаскивайте круглый узел. Длинный ряд показан выборочно по всей длине. Все объекты появятся после завершения.", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
                AppearanceNumberBox("EditablePathSpacingBox", "Шаг", "ед.", () => _editor.DisplayedEditablePath?.Spacing, _editor.SetEditablePathSpacing),
                AppearanceNumberBox("EditablePathSeedBox", "Рисунок", null, () => _editor.DisplayedEditablePath?.Seed, _editor.SetEditablePathSeed),
                Ui.TextButton("InsertEditablePathNodeButton", "Добавить узел на сегменте", _editor.InsertEditablePathNode, "compact"),
                Ui.TextButton("DeleteEditablePathNodeButton", "Удалить узел (в черновике — последний)", _editor.DeleteEditablePathNode, "compact"),
                Ui.TextButton("SelectPathObjectButton", "Выбрать объект пути", _editor.PickEditablePathObject, "compact"),
                _pathDetach,
                Ui.TextButton("ApplyEditablePathSettingsButton", "Применить параметры ассета к пути", _editor.ApplyStampSettingsToEditablePath, "compact"),
                Ui.TextButton("BakeEditablePathButton", "Превратить путь в обычные объекты", _editor.BakeEditablePath, "compact"),
                Ui.TextButton("DeleteEditablePathButton", "Удалить путь и связанные объекты", _editor.DeleteEditablePath, "compact", "destructive"),
            },
        };
        return _pathPanel;
    }

    private void SyncEditablePathPanel()
    {
        if (_pathPanel is null || _pathPicker is null || _pathSummary is null) return;
        _syncingPaths = true;
        try
        {
            var choices = _editor.EditablePaths.Select(p => new PathChoice(p.Id, p.Name)).ToArray();
            if (_pathPicker.ItemsSource is not PathChoice[] previous || !previous.SequenceEqual(choices))
                _pathPicker.ItemsSource = choices;
            _pathPicker.SelectedItem = choices.FirstOrDefault(p => p.Id == _editor.SelectedEditablePath?.Id);
            _pathSummary.Text = _editor.EditablePathSummary;
            _stampGestureBlock.IsVisible = _editor.SelectedSet is not null || _editor.SelectedEditablePath is not null || _editor.CurrentAreaSource is not null;
            _pathFinish!.IsVisible = _editor.IsCreatingEditablePath;
            _pathDetach!.IsEnabled = _editor.CanDetachPathObject;
            _pathPanel.IsVisible = _editor.StampPlacementMode == Map.Presentation.ViewModels.StampPlacementMode.Path
                || _editor.IsCreatingEditablePath || _editor.SelectedEditablePath is not null;
            SyncAppearanceSections();
        }
        finally { _syncingPaths = false; }
    }

    private sealed record PathChoice(Guid Id, string Name)
    { public override string ToString() => Name; }
}
