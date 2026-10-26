using Avalonia.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildGenerationControls(bool hex = true)
    {
        var panel = new StackPanel { Spacing = 6, Tag = "Reveal:HexTabWorld" };
        var apply = Ui.TextButton("GenerationApplyButton", "Применить", () => _editor.ApplyGeneration(), "compact");
        var cancel = Ui.TextButton("GenerationCancelButton", "Отмена расчёта / превью", _editor.CancelGeneration, "compact");
        var recalc = Ui.TextButton("GenerationRecalculateButton", "Пересчитать", _editor.RecalculateGeneration, "compact");
        void Sync() { apply.IsEnabled = _editor.GenerationHasPreview; cancel.IsEnabled = _editor.GenerationBusy || _editor.GenerationHasPreview; }
        _editor.PropertyChanged += (_, _) => Sync();
        Sync();
        var selection = new CheckBox { Name = "GenerationSelectionOnlyToggle", Content = "Только выделенные гексы" };
        selection.IsCheckedChanged += (_, _) => _editor.GenerationSelectionOnly = selection.IsChecked == true;
        var erase = new CheckBox { Name = "GenerationProtectionEraseToggle", Content = "Кисть снимает защиту" };
        erase.IsCheckedChanged += (_, _) => _editor.GenerationProtectionErase = erase.IsChecked == true;
        if (hex) panel.Children.Add(selection);
        panel.Children.Add(Ui.TextButton("GenerationProtectBrushButton", "Кисть «Защитить»", () =>
        { if (hex) _editor.HexMode = HexToolMode.Protect; else _editor.SetAreaProtectionBrush(true); }, "compact"));
        panel.Children.Add(erase);
        if (hex)
        {
            panel.Children.Add(Ui.TextButton("GenerationProtectSelectionButton", "Защитить выделение", () => _editor.ProtectHexSelection(true), "compact"));
            panel.Children.Add(Ui.TextButton("GenerationUnprotectSelectionButton", "Снять защиту выделения", () => _editor.ProtectHexSelection(false), "compact"));
        }
        else
        {
            panel.Tag = "Reveal:EditableAreaPicker";
            panel.Children.Add(Ui.TextButton("GenerationAreaStartButton", "Рассчитать выбранную область", _editor.StartAreaGeneration, "compact"));
            panel.Children.Add(Ui.TextButton("GenerationProtectBrushStopButton", "Закончить защиту", () => _editor.SetAreaProtectionBrush(false), "compact"));
        }
        var status = new TextBlock { Name = "GenerationStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        void Status() => status.Text = _editor.GenerationConflict ? "Карта изменилась. Требуется пересчёт."
            : _editor.GenerationBusy ? "Расчёт в фоне…" : _editor.GenerationHasPreview ? "Предпросмотр. Документ ещё не изменён." : "";
        _editor.PropertyChanged += (_, _) => Status(); Status(); panel.Children.Add(status);
        panel.Children.Add(apply); panel.Children.Add(cancel); panel.Children.Add(recalc);
        return panel;
    }
}
