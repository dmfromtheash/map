using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Map.Platform.Windows;

namespace Map.App.Views;

/// <summary>One local settings surface. Values are sanitized and atomically persisted by the store.</summary>
public sealed class AppSettingsWindow : Window
{
    private readonly AppSettingsStore _store;
    private readonly Action<AppSettings> _apply;
    private readonly Func<string> _diagnostics;
    private readonly ComboBox _theme = Choice("SettingsTheme", ["Системная", "Тёмная", "Светлая"]);
    private readonly NumericUpDown _scale = Number("SettingsUiScale", 80, 200, 5);
    private readonly NumericUpDown _autosave = Number("SettingsAutosave", 10, 3600, 10);
    private readonly NumericUpDown _recovery = Number("SettingsRecoveryBudget", 1, 50, 1);
    private readonly ComboBox _performance = Choice("SettingsPerformance", ["Баланс", "Качество", "Скорость"]);
    private readonly TextBox _libraryRoot = new() { Name = "SettingsLibraryRoot", PlaceholderText = "Стандартная папка профиля" };
    private readonly TextBox _cacheRoot = new() { Name = "SettingsCacheRoot", PlaceholderText = "Стандартная папка профиля" };
    private readonly ComboBox _exportFormat = Choice("SettingsExportFormat", ["PNG", "JPEG", "WebP"]);
    private readonly NumericUpDown _exportScale = Number("SettingsExportScale", 10, 800, 10);
    private readonly CheckBox _exportGrid = new() { Name = "SettingsExportGrid", Content = "Включать сетку по умолчанию" };
    private readonly TextBlock _diagnosticsText = new() { Name = "SettingsDiagnostics", TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public AppSettingsWindow(
        AppSettingsStore store,
        AppSettings current,
        Action<AppSettings> apply,
        Func<string> diagnostics)
    {
        _store = store;
        _apply = apply;
        _diagnostics = diagnostics;
        Title = "Настройки Map";
        Name = "AppSettingsWindow";
        Width = 720;
        Height = 760;
        MinWidth = 560;
        MinHeight = 480;

        var save = Ui.TextButton("SaveAppSettingsButton", "Сохранить", Save, "primary");
        var reset = Ui.TextButton("ResetAppSettingsButton", "Сбросить", Reset);
        var close = Ui.TextButton("CloseAppSettingsButton", "Закрыть", Close);
        var libraryPick = Ui.AsyncButton("PickLibraryRootButton", "Выбрать…", () => PickRootAsync(_libraryRoot));
        var cachePick = Ui.AsyncButton("PickCacheRootButton", "Выбрать…", () => PickRootAsync(_cacheRoot));
        var shortcuts = new TextBlock
        {
            Text = "Сочетания клавиш настраиваются отдельной кнопкой в редакторе; там же видны конфликты и сброс.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        }.Classed("muted", "role-caption");

        Content = new DockPanel
        {
            Children =
            {
                new Border
                {
                    Padding = new Thickness(18, 12),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { reset, close, save },
                    },
                }.Classed("app-bar").Docked(Dock.Bottom),
                new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Thickness(24),
                        Spacing = 16,
                        Children =
                        {
                            Heading("Интерфейс"),
                            Ui.PropertyRow("Тема", _theme),
                            Ui.PropertyRow("Масштаб интерфейса", PercentRow(_scale)),
                            new TextBlock { Text = "Язык интерфейса: русский", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            Heading("Сохранение и recovery"),
                            Ui.PropertyRow("Автосохранение, секунд", _autosave),
                            Ui.PropertyRow("Кандидатов recovery", _recovery),
                            Heading("Холст"),
                            Ui.PropertyRow("Режим производительности", _performance),
                            shortcuts,
                            Heading("Локальные папки"),
                            RootRow(_libraryRoot, libraryPick),
                            RootRow(_cacheRoot, cachePick),
                            new TextBlock
                            {
                                Text = "Новые папки применяются после перезапуска. Старые данные не удаляются.",
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            }.Classed("muted", "role-caption"),
                            Heading("Экспорт по умолчанию"),
                            Ui.PropertyRow("Формат", _exportFormat),
                            Ui.PropertyRow("Масштаб", PercentRow(_exportScale)),
                            _exportGrid,
                            Heading("Локальная диагностика"),
                            _diagnosticsText.Classed("muted", "role-caption"),
                            new TextBlock
                            {
                                Text = "Диагностика не отправляется наружу и не показывает содержимое карты или полные пути.",
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            }.Classed("muted", "role-caption"),
                        },
                    },
                },
            },
        };

        Load(current);
        RefreshDiagnostics();
    }

    private void Load(AppSettings settings)
    {
        settings = AppSettings.Sanitize(settings);
        _theme.SelectedIndex = (int)settings.Theme;
        _scale.Value = (decimal)(settings.UiScale * 100);
        _autosave.Value = settings.AutosaveIntervalSeconds;
        _recovery.Value = settings.RecoveryItemBudget;
        _performance.SelectedIndex = (int)settings.PerformanceMode;
        _libraryRoot.Text = settings.LibraryRoot ?? "";
        _cacheRoot.Text = settings.CacheRoot ?? "";
        _exportFormat.SelectedIndex = settings.DefaultExportFormat switch { "Jpeg" => 1, "Webp" => 2, _ => 0 };
        _exportScale.Value = settings.DefaultExportScalePercent;
        _exportGrid.IsChecked = settings.DefaultExportIncludeGrid;
    }

    private void Save()
    {
        var saved = _store.Save(new AppSettings
        {
            Theme = (LocalThemeMode)Math.Max(0, _theme.SelectedIndex),
            UiScale = (double)(_scale.Value ?? 100) / 100,
            AutosaveIntervalSeconds = (int)(_autosave.Value ?? 60),
            RecoveryItemBudget = (int)(_recovery.Value ?? 12),
            PerformanceMode = (CanvasPerformanceMode)Math.Max(0, _performance.SelectedIndex),
            LibraryRoot = EmptyToNull(_libraryRoot.Text),
            CacheRoot = EmptyToNull(_cacheRoot.Text),
            DefaultExportFormat = _exportFormat.SelectedIndex switch { 1 => "Jpeg", 2 => "Webp", _ => "Png" },
            DefaultExportScalePercent = (int)(_exportScale.Value ?? 100),
            DefaultExportIncludeGrid = _exportGrid.IsChecked == true,
        });
        _apply(saved);
        RefreshDiagnostics();
    }

    private void Reset()
    {
        var defaults = _store.Reset();
        Load(defaults);
        _apply(defaults);
        RefreshDiagnostics();
    }

    private async Task PickRootAsync(TextBox target)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите локальную папку",
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
        {
            target.Text = Path.GetFullPath(path);
        }
    }

    private void RefreshDiagnostics() => _diagnosticsText.Text = _diagnostics();
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static TextBlock Heading(string text) => new TextBlock { Text = text }.Classed("role-section");
    private static Control PercentRow(NumericUpDown input) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children = { input, new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center } },
    };
    private static Control RootRow(TextBox input, Button button) => new StackPanel
    {
        Spacing = 6,
        Children = { input, button },
    };
    private static ComboBox Choice(string name, string[] values) => new()
    {
        Name = name,
        MinWidth = 180,
        ItemsSource = values,
    };
    private static NumericUpDown Number(string name, decimal min, decimal max, decimal increment) => new()
    {
        Name = name,
        Minimum = min,
        Maximum = max,
        Increment = increment,
        MinWidth = 120,
    };
}
