using Avalonia.Controls;
using Map.App.Controls;
using Map.Export;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildExportUnits()
    {
        var units = new ComboBox { Name = "ExportSizeUnitComboBox", ItemsSource = new[] { "px", "мм", "дюймы" }, MinWidth = 140 };
        var dpi = new TextBox { Name = "ExportDpiBox", MinWidth = 100 };
        var syncing = false;
        void Sync()
        {
            syncing = true;
            units.SelectedIndex = (int)_editor.ExportSizeUnit;
            if (dpi.Text != _editor.ExportDpiText) dpi.Text = _editor.ExportDpiText;
            syncing = false;
        }
        units.SelectionChanged += (_, _) => { if (!syncing && units.SelectedIndex >= 0) { _editor.ExportSizeUnit = (ExportSizeUnit)units.SelectedIndex; SyncExport(); } };
        dpi.TextChanged += (_, _) => { if (!syncing) { _editor.ExportDpiText = dpi.Text ?? ""; SyncExport(); } };
        _editor.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(EditorViewModel.ExportSizeUnit) or nameof(EditorViewModel.ExportDpiText) or nameof(EditorViewModel.ExportOpen)) Sync(); };
        Sync();
        return new StackPanel { Spacing = 4, Children =
        {
            new TextBlock { Text = "Размер изображения · единицы и DPI" }.Classed("role-caption"),
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children =
                { units, new TextBlock { Text = "DPI", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, dpi } },
            new TextBlock { Text = "Физический размер × DPI → ближайший целый пиксель.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }.Classed("muted", "role-caption"),
        } };
    }
}
