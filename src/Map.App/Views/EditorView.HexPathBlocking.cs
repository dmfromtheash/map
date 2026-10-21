using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.App.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildHexPathBlockingSection()
    {
        var title = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }.Classed("role-caption");
        CheckBox Flag(string name, string label, Action<bool> apply)
        {
            var check = new CheckBox { Name = name, Content = label, Tag = "Reveal:HexPathCategory", MinHeight = 28 };
            check.IsCheckedChanged += (_, _) => { if (!_syncingHex) apply(check.IsChecked == true); };
            return check;
        }
        var sight = Flag("HexPathBlocksSightCheckBox", "Блокирует обзор", value => _editor.SetSelectedHexPathBlocking(sight: value));
        var move = Flag("HexPathBlocksMoveCheckBox", "Блокирует движение", value => _editor.SetSelectedHexPathBlocking(move: value));
        var light = Flag("HexPathBlocksLightCheckBox", "Блокирует свет", value => _editor.SetSelectedHexPathBlocking(light: value));
        var label = Ui.TextButton("HexPathAddLabelButton", "Подпись вдоль пути", () => _editor.AddLabelAlongSelectedPath(Map.Core.Documents.TextPathTarget.HexPath), "compact");
        label.Tag = "Reveal:HexPathCategory";
        ToolTip.SetTip(sight, "Включает путь в стены обзора при экспорте dd2vtt выбранного уровня.");
        ToolTip.SetTip(move, "Сохраняется в карте и JSON. В текущем экспорте dd2vtt этот флаг отдельно не записывается.");
        ToolTip.SetTip(light, "Сохраняется в карте и JSON. Для стены dd2vtt включите «Блокирует обзор».");
        HexSync(() =>
        {
            var path = _editor.SelectedHexPath;
            title.Text = path is null ? "Выберите путь в списке ниже." : "Выбран: "
                + (path.Name.Length > 0 ? path.Name : EditorViewModel.PathKindName(path.Kind));
            sight.IsEnabled = move.IsEnabled = light.IsEnabled = label.IsEnabled = _editor.CanEditSelectedHexPath;
            sight.IsChecked = path?.BlocksSight ?? false;
            move.IsChecked = path?.BlocksMove ?? false;
            light.IsChecked = path?.BlocksLight ?? false;
        }, [.. DocumentChanged, nameof(EditorViewModel.HexPaths), nameof(EditorViewModel.SelectedHexPath)]);
        return Ui.Subgroup("Свойства выбранного пути", new StackPanel
        {
            Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { title, sight, move, light, label },
        });
    }
}
