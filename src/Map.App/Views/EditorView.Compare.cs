using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Core.Editing;

namespace Map.App.Views;

/// <summary>
/// R30: the comparison block at the top of «Версии» - what changed since the chosen version in
/// words, the highlight on the canvas, «До / После» and the list of changes to show one by one.
/// </summary>
public sealed partial class EditorView
{
    private StackPanel? _compareBlock;

    private Control BuildCompareBlock()
    {
        _compareBlock = new StackPanel { Name = "CompareBlock", Spacing = 6, IsVisible = false };
        return new Border { Padding = new Avalonia.Thickness(8), Child = _compareBlock, IsVisible = false }.Classed("group");
    }

    private void SyncCompareBlock()
    {
        if (_compareBlock is null) return;
        _compareBlock.Children.Clear();
        var parent = _compareBlock.Parent as Control;
        if (_editor.Comparison is not { } diff)
        {
            _compareBlock.IsVisible = false;
            if (parent is not null) parent.IsVisible = false;
            return;
        }

        _compareBlock.IsVisible = true;
        if (parent is not null) parent.IsVisible = true;
        _compareBlock.Children.Add(new TextBlock { Text = $"Сравнение с «{_editor.ComparedVersionName}»", TextWrapping = TextWrapping.Wrap }.Classed("role-panel"));
        var summary = new StackPanel { Name = "CompareSummary", Spacing = 2 };
        foreach (var line in diff.Summary()) summary.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        _compareBlock.Children.Add(summary);

        var (sides, sideButtons) = Ui.Segmented("CompareSide", [("Before", "До", null), ("After", "После", null)], key =>
        {
            _editor.CompareShowsBefore = key == "Before";
            SyncCompareBlock();
        });
        foreach (var button in sideButtons)
            Ui.SetClass(button, "active", button.Name == (_editor.CompareShowsBefore ? "CompareSideBefore" : "CompareSideAfter"));
        ToolTip.SetTip(sides, "«До» показывает версию только для просмотра; «После» — текущую карту.");
        _compareBlock.Children.Add(sides);

        var highlight = Ui.PropertyToggle("CompareHighlightToggle", "Подсветка на карте");
        highlight.IsChecked = _editor.CompareHighlight;
        highlight.IsCheckedChanged += (_, _) => _editor.CompareHighlight = highlight.IsChecked == true;
        ToolTip.SetTip(highlight, "Зелёное — добавлено, красное — удалено, жёлтое — изменено.");
        _compareBlock.Children.Add(highlight);

        var list = new StackPanel { Name = "CompareChanges", Spacing = 2 };
        foreach (var change in diff.Changes.Where(c => c.Bounds.Right > c.Bounds.Left).Take(60))
        {
            var captured = change;
            var verb = change.Kind switch { MapChangeKind.Added => "добавлено", MapChangeKind.Removed => "удалено", _ => "изменено" };
            list.Children.Add(Ui.TextButton("CompareChange_" + list.Children.Count, $"{change.Group}: {change.Name} — {verb}", () => _editor.ShowChange(captured), "compact"));
        }
        if (list.Children.Count > 0) _compareBlock.Children.Add(new Expander { Header = $"Изменения ({diff.Changes.Count})", Content = list });

        _compareBlock.Children.Add(Ui.TextButton("CompareCloseButton", "Закрыть сравнение", () => { _editor.CloseComparison(); SyncCompareBlock(); }, "compact"));
    }
}
