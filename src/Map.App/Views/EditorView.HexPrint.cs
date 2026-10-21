using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.App.Controls;
using Map.Export;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private async Task ShowHexPrintAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_exportWindow?.IsVisible == true) owner = _exportWindow;
        var options = _editor.HexPrintOptions;
        var paper = new ComboBox { Name = "HexPrintPaperComboBox", ItemsSource = new[] { "A4 · 210 × 297 мм", "A3 · 297 × 420 мм", "Letter · 215,9 × 279,4 мм", "Свой размер", "A2 · 420 × 594 мм", "A1 · 594 × 841 мм", "A0 · 841 × 1189 мм" }, SelectedIndex = (int)(options.PaperFormat ?? (options.A3 ? HexPaperFormat.A3 : HexPaperFormat.A4)), HorizontalAlignment = HorizontalAlignment.Stretch };
        NumericUpDown Number(string name, double value, decimal min, decimal max) => new() { Name = name, Value = (decimal)value, Minimum = min, Maximum = max, Increment = 1, FormatString = "0.##", HorizontalAlignment = HorizontalAlignment.Stretch };
        var width = Number("HexPrintWidthBox", options.CustomWidthMillimetres, 60, 2000);
        var height = Number("HexPrintHeightBox", options.CustomHeightMillimetres, 60, 2000);
        var hex = Number("HexPrintHexSizeBox", options.HexMillimetres, 5, 200);
        hex.IsVisible = _editor.IsHexWorkshop;
        var mapWidth = Number("PosterPrintMapWidthBox", options.MapWidthMillimetres, 10, 20000);
        mapWidth.IsVisible = !_editor.IsHexWorkshop;
        var overlap = Number("HexPrintOverlapBox", options.OverlapMillimetres, 0, 30);
        var bleed = Number("HexPrintBleedBox", options.BleedMillimetres, 0, 5);
        var landscape = new CheckBox { Name = "HexPrintLandscapeToggle", Content = "Альбомные листы", IsChecked = options.Landscape };
        var marks = new CheckBox { Name = "HexPrintCropMarksToggle", Content = "Метки реза", IsChecked = options.CropMarks };
        var key = new CheckBox { Name = "HexPrintKeyToggle", Content = "Ключ гексов после карты", IsChecked = options.IncludeKey };
        key.IsVisible = _editor.IsHexWorkshop;
        var overview = new CheckBox { Name = "PosterPrintOverviewToggle", Content = "Лист-обзор со схемой склейки", IsChecked = options.IncludeOverview };
        var error = new TextBlock { Name = "HexPrintResultText", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var window = new Window { Name = "HexPrintWindow", Title = "Печать по листам — постер", Width = 550, Height = 800, CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var save = Ui.AsyncButton("HexPrintSaveButton", "Сохранить PDF…", async () =>
        {
            _editor.HexPrintOptions = options with { PaperFormat = (HexPaperFormat)paper.SelectedIndex,
                CustomWidthMillimetres = (double)(width.Value ?? 210), CustomHeightMillimetres = (double)(height.Value ?? 297),
                HexMillimetres = (double)(hex.Value ?? 25), BleedMillimetres = (double)(bleed.Value ?? 0),
                Landscape = landscape.IsChecked == true, CropMarks = marks.IsChecked == true, IncludeKey = key.IsChecked == true,
                OverlapMillimetres = (double)(overlap.Value ?? 8), MapWidthMillimetres = (double)(mapWidth.Value ?? 600), IncludeOverview = overview.IsChecked == true };
            await _editor.ExportPosterAsync();
            error.Text = _editor.StatusDetail;
        }, "primary");
        void Sync() { width.IsEnabled = height.IsEnabled = paper.SelectedIndex == 3; landscape.IsEnabled = paper.SelectedIndex != 3; }
        paper.SelectionChanged += (_, _) => Sync(); Sync();
        var footer = new StackPanel { Margin = new Thickness(24, 10), Spacing = 8, Children = { save, error, Ui.TextButton("HexPrintCloseButton", "Закрыть", window.Close, "ghost") } };
        DockPanel.SetDock(footer, Dock.Bottom);
        window.Content = new DockPanel { Children = { footer, new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24, 16), Spacing = 8, Children =
        {
            new TextBlock { Text = "Формат бумаги", FontSize = 20 }, paper,
            new TextBlock { Text = "Свой лист: ширина × высота, мм" },
            new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Children = { width, height } },
            landscape, new TextBlock { Text = _editor.IsHexWorkshop ? "Гекс на бумаге, мм (грань к грани)" : "Ширина всей карты на бумаге, мм" }, hex, mapWidth,
            new TextBlock { Text = "Перекрытие соседних листов, мм" }, overlap,
            marks, new TextBlock { Text = "Вылет за линию реза (bleed), мм" }, bleed,
            new TextBlock { Text = "Вылет располагается внутри листа вокруг карты. Метки показывают линию реза.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }.Classed("muted"),
            overview, key,
            new TextBlock { Text = "PDF печатайте в масштабе 100%, без подгонки под лист.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }.Classed("muted"),
        } } } } };
        Grid.SetColumn(height, 1);
        await window.ShowDialog(owner);
    }
}
