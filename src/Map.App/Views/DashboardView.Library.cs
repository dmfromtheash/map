using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Map.App.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class DashboardView
{
    private Control BuildLibraryCategoryFilter(string suffix)
    {
        var filter = new ComboBox { Name = "LibraryCategoryFilter" + suffix, ItemsSource = new[] { "Все категории", "Стандартный", "Гексовый" }, MinWidth = 190 };
        var syncing = false;
        void Sync() { syncing = true; filter.SelectedIndex = _dashboard.LibraryCategoryFilter switch { "standard" => 1, "hex" => 2, _ => 0 }; syncing = false; }
        filter.SelectionChanged += (_, _) => { if (!syncing) _dashboard.LibraryCategoryFilter = filter.SelectedIndex switch { 1 => "standard", 2 => "hex", _ => "" }; };
        void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(DashboardViewModel.LibraryCategoryFilter)) Sync(); }
        filter.AttachedToVisualTree += (_, _) => { _dashboard.PropertyChanged += Changed; Sync(); };
        filter.DetachedFromVisualTree += (_, _) => _dashboard.PropertyChanged -= Changed;
        Sync();
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children =
            { new TextBlock { Text = "Категория", VerticalAlignment = VerticalAlignment.Center }, filter } };
    }
    private static Control BuildLibraryCategoryBadge(RecentCard card) => new Border
    {
        Name = "LibraryCardCategoryBadge", HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(2, 5, 2, 1), Padding = new Thickness(8, 3), CornerRadius = new CornerRadius(5),
        Background = Brush("MapPanelBrush"), BorderBrush = Brush("MapBorderBrush"), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = card.CategoryLabel, FontSize = 12 },
    };
    private Control BuildLibraryFolderLabel()
    {
        var label = new TextBlock { Name = "LibraryFolderLabel", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
        void Sync() => label.Text = "Папка библиотеки: " + _dashboard.Library?.Root;
        void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(DashboardViewModel.Library)) Sync(); }
        label.AttachedToVisualTree += (_, _) => { _dashboard.PropertyChanged += Changed; Sync(); };
        label.DetachedFromVisualTree += (_, _) => _dashboard.PropertyChanged -= Changed;
        Sync(); return label.Classed("muted", "role-caption");
    }
    private Button BuildLibraryFolderButton() => Ui.AsyncButton("LibraryFolderButton", "Сменить папку библиотеки…", ChangeLibraryFolderAsync, "ghost", "compact");
    private async Task ChangeLibraryFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Папка библиотеки карт", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) _dashboard.ChangeLibraryFolder(path);
    }
}
