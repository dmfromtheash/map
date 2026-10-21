using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Export;

namespace Map.App.Views;

public sealed partial class EditorView
{
    public Task ShowHexImportForSmokeAsync(string json) => Map.App.Services.TestMode.Enabled
        ? ShowHexImportAsync(json) : Task.CompletedTask;

    private async Task ShowHexImportAsync(string json)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var names = HexJsonExporter.ImportLayerNames(json);
        if (names.Count == 0) throw new System.IO.InvalidDataException("В файле нет слоёв гексов.");
        var layers = _editor.Session.Document.Layers.OfType<HexLayer>().Where(l => !l.Locked).ToArray();
        var source = new ComboBox { Name = "HexImportSourceComboBox", ItemsSource = names, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var target = new ComboBox { Name = "HexImportTargetComboBox", ItemsSource = layers.Select(l => l.Name).Append("Новый гексовый слой").ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(layers, l => l.Id == _editor.ActiveHexLayer?.Id)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var window = new Window { Name = "HexImportWindow", Title = "Импорт JSON гексов", Width = 500, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        {
            new TextBlock { Text = "Слой из JSON" }, source, new TextBlock { Text = "Куда импортировать" }, target,
            new TextBlock { Text = "Содержимое выбранного слоя будет заменено. Поле и холст увеличатся при необходимости. Можно отменить одним шагом.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            Ui.TextButton("HexImportApplyButton", "Импортировать", () =>
            {
                _editor.ImportHexJson(json, target.SelectedIndex < layers.Length ? layers[target.SelectedIndex].Id : null, source.SelectedIndex);
                window.Close(true);
            }, "primary"),
            Ui.TextButton("HexImportCancelButton", "Отмена", () => window.Close(false), "ghost"),
        } };
        if (await window.ShowDialog<bool>(owner)) await ShowHexImportResultAsync(_editor.StatusDetail);
    }

    private async Task ShowHexImportResultAsync(string text)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var window = new Window { Name = "HexImportResultWindow", Title = "Результат импорта", Width = 560, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
        {
            new ScrollViewer { MaxHeight = 230, Content = new TextBlock { Name = "HexImportResultText", Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } },
            Ui.TextButton("HexImportResultCloseButton", "Закрыть", window.Close, "primary"),
        } };
        await window.ShowDialog(owner);
    }
}
