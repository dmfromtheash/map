using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Assets;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The create-map dialog. Presets carry the decision so a first-time user never has to know that
/// the canvas is measured in pixels; the raw width and height only appear once "Свой размер" is
/// chosen, which is also the only way to reach the very large canvases.
/// </summary>
public sealed class WizardWindow : Window
{
    private sealed record SizePreset(string Key, string Title, string Detail, int Width, int Height);

    private static readonly IReadOnlyList<SizePreset> Presets =
    [
        new("Standard", "Обычная", "1920 × 1080 · один экран", 1920, 1080),
        new("Large", "Крупная", "4096 × 4096 · 8 экранов", 4096, 4096),
        new("Huge", "Очень крупная", "16384 × 16384 · 130 экранов", 16384, 16384),
        new("Max", "Максимальная", "131072 × 131072 · предел формата", 131072, 131072),
    ];

    private static readonly IReadOnlyList<(string Key, string Title)> Backgrounds =
    [
        ("#FF1E2430", "Ночная синь"),
        ("#FFF2E9DC", "Пергамент"),
        ("#FF223322", "Тёмная зелень"),
        ("#FFFFFFFF", "Белый"),
    ];

    private readonly NewMapWizardViewModel _wizard = new();
    private readonly List<ToggleButton> _presetButtons = [];
    private readonly List<ToggleButton> _backgroundButtons = [];
    private readonly TextBox _titleBox = new() { Name = "WizardTitleBox" };
    private readonly TextBox _widthBox = new() { Name = "WizardWidthBox" };
    private readonly TextBox _heightBox = new() { Name = "WizardHeightBox" };
    private readonly ComboBox _backgroundCombo;
    private readonly TextBlock _error = new() { Name = "WizardErrorText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sizeHint = new() { Name = "WizardSizeHint", TextWrapping = TextWrapping.Wrap };
    private Control _customBlock = null!;
    private Control _backgroundBlock = null!;
    private string _presetKey = "Standard";
    private MapDocument? _created;
    private readonly AssetCatalog? _catalog;
    private readonly ComboBox _startingPoint = new() { Name = "WizardStartingPoint", ItemsSource = new[]
        { "Чистый холст", "Тихий атлас · пустой регион", "Тихий атлас · долина Листвы" }, SelectedIndex = 0 };
    public MapDocument? CreatedDocument => _created;

    public WizardWindow(AssetCatalog? catalog = null)
    {
        _catalog = catalog;
        Title = "Новая карта";
        Name = "WizardWindow";
        Width = 1080;
        Height = 820;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        _titleBox.Text = _wizard.Title;
        _widthBox.Text = _wizard.WidthText;
        _heightBox.Text = _wizard.HeightText;

        // Retained so existing automation can still address the background by value.
        _backgroundCombo = new ComboBox
        {
            Name = "WizardBackgroundCombo",
            ItemsSource = _wizard.BackgroundChoices,
            SelectedIndex = 0,
            IsVisible = false,
        };
        AutomationProperties.SetName(_backgroundCombo, "Цвет фона");

        _error.Classes.Add("danger");
        _error.IsVisible = false;
        _backgroundBlock = BuildBackgroundBlock();

        Content = Ui.WorkspaceBackdrop(new DockPanel
        {
            Children =
            {
                new Border
                {
                    MinHeight = 104,
                    Padding = new Thickness(24, 16),
                    Child = BuildIntro(),
                }.Classed("app-bar", "wizard-header").Docked(Dock.Top),
                new Border
                {
                    Padding = new Thickness(24, 16),
                    Child = new StackPanel { Spacing = 8, Children = { _error, BuildFooter() } },
                }.Classed("dialog-footer").Docked(Dock.Bottom),
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new Border
                    {
                        Padding = new Thickness(24),
                        Child = new StackPanel
                        {
                            Spacing = 20,
                            Children = { BuildNameBlock(), BuildStartingPoint(), BuildSizeBlock(), _backgroundBlock, _backgroundCombo },
                        },
                    },
                },
            },
        });

        SyncPresetButtons();
        SyncBackgroundButtons();
    }

    private Control BuildStartingPoint()
    {
        _startingPoint.IsEnabled = _catalog is not null && RegionalArtPack.IsAvailable;
        _startingPoint.SelectionChanged += (_, _) =>
        {
            if (_backgroundBlock is not null) _backgroundBlock.IsVisible = _startingPoint.SelectedIndex == 0;
            if (_startingPoint.SelectedIndex <= 0) { SyncPresetButtons(); return; }
            _presetKey = "Custom"; _widthBox.Text = "4096"; _heightBox.Text = "3072";
            _titleBox.Text = _startingPoint.SelectedIndex == 2 ? "Долина Листвы" : "Новый регион";
            SyncPresetButtons();
        };
        AutomationProperties.SetName(_startingPoint, "С чего начать карту");
        return Ui.Section("С чего начать", new StackPanel { Spacing = 6, Children = {
            _startingPoint,
            new TextBlock { Text = "Регион подключает оригинальный комплект «Тихий атлас» в вашу библиотеку. " +
                "Пустой регион задаёт палитру и слои. Долина — самостоятельная карта с редактируемыми лесами, хребтами, поселениями, реками и дорогами.",
                TextWrapping = TextWrapping.Wrap }.Classed("muted", "role-caption") } });
    }

    private static Control BuildIntro() => new DockPanel
    {
        Children =
        {
            new Border
            {
                Child = ApprovedArtwork.Image("map", 72, 72),
                Margin = new Thickness(0, 0, 20, 0),
            }.Docked(Dock.Left),
            new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Новая карта" }.Classed("role-screen"),
                    new TextBlock
                    {
                        Text = "Выберите основу и размер. Содержимое и оформление карты можно менять позже.",
                        TextWrapping = TextWrapping.Wrap,
                    }.Classed("muted"),
                },
            },
        },
    };

    private Control BuildNameBlock()
    {
        AutomationProperties.SetName(_titleBox, "Название карты");
        return new StackPanel
        {
            Spacing = 9,
            Children =
            {
                new TextBlock { Text = "Название" }.Classed("role-section"),
                new Border { Padding = new Thickness(3), Child = _titleBox }.Classed("ornate-field"),
            },
        };
    }

    private Control BuildSizeBlock()
    {
        // A uniform grid, not a fixed-width strip: every card gets the same share of the dialog,
        // so a longer Russian label widens all of them instead of being clipped.
        var strip = new UniformGrid { Columns = 5 };

        foreach (var preset in Presets)
        {
            strip.Children.Add(PresetCard(preset.Key, preset.Title, preset.Detail));
        }

        strip.Children.Add(PresetCard("Custom", "Свой размер", "Точные значения"));

        AutomationProperties.SetName(_widthBox, "Ширина в пикселях");
        AutomationProperties.SetName(_heightBox, "Высота в пикселях");

        var swap = Ui.TextButton("SwapOrientationButton", "Повернуть", () =>
        {
            _wizard.WidthText = _widthBox.Text ?? "";
            _wizard.HeightText = _heightBox.Text ?? "";
            _wizard.SwapOrientation();
            _widthBox.Text = _wizard.WidthText;
            _heightBox.Text = _wizard.HeightText;
        }, "compact");
        ToolTip.SetTip(swap, "Поменять ширину и высоту местами");

        _customBlock = new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            LabelledField("Ширина", _widthBox),
                            LabelledField("Высота", _heightBox),
                            new StackPanel
                            {
                                Spacing = 6,
                                VerticalAlignment = VerticalAlignment.Bottom,
                                Children = { swap },
                            },
                        },
                    },
                    _sizeHint.Classed("faint", "role-caption"),
                },
            },
        }.Classed("group").Named("WizardCustomSize");

        _customBlock.IsVisible = false;

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Размер холста" }.Classed("role-section"),
                strip,
                new TextBlock
                {
                    Text = "Крупный холст даёт больше места для деталей. «Свой размер» позволяет задать точные ширину и высоту.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption").Named("WizardSizeMeaning"),
                _customBlock,
            },
        };
    }

    private ToggleButton PresetCard(string key, string title, string detail)
    {
        var button = new ToggleButton
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 102,
            Margin = new Thickness(4),
            Content = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Border
                    {
                        Height = 28,
                        Child = ApprovedArtwork.Image("map",
                            28, 28),
                    },
                    new TextBlock
                    {
                        Text = title,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,

                        FontSize = 14,
                        Foreground = Application.Current?.FindResource("MapTextBrush") as IBrush,
                    },
                    new TextBlock
                    {
                        Text = detail,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = Application.Current?.FindResource("MapMutedBrush") as IBrush,
                    }.Classed("muted"),
                },
            },
        }.Named("WizardPreset" + key).Classed("preset-card");

        AutomationProperties.SetName(button, $"{title}, {detail}");
        ToolTip.SetTip(button, $"{title} · {detail}");

        button.Click += (_, _) =>
        {
            _presetKey = key;
            if (key != "Custom")
            {
                var preset = Presets.First(item => item.Key == key);
                _widthBox.Text = preset.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _heightBox.Text = preset.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            SyncPresetButtons();
        };

        _presetButtons.Add(button);
        return button;
    }

    private Control BuildBackgroundBlock()
    {
        var strip = new UniformGrid { Columns = 4 };

        foreach (var (key, title) in Backgrounds)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.Parse(key)),
                BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var button = new ToggleButton
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        swatch,
                        new TextBlock
                        {
                            Text = title,
                            VerticalAlignment = VerticalAlignment.Center,

                            FontSize = 13,
                            Foreground = Application.Current?.FindResource("MapTextBrush") as IBrush,
                        },
                    },
                },
                Height = 56,
                Margin = new Thickness(4),
            }.Named("WizardBackground" + key.TrimStart('#')).Classed("background-card");

            AutomationProperties.SetName(button, "Фон: " + title);
            ToolTip.SetTip(button, "Фон: " + title);

            var captured = key;
            button.Click += (_, _) =>
            {
                _wizard.Background = captured;
                SyncBackgroundButtons();
            };

            _backgroundButtons.Add(button);
            strip.Children.Add(button);
        }

        return new StackPanel
        {
            Spacing = 10,
            Children = { new TextBlock { Text = "Фон" }.Classed("role-section"), strip },
        };
    }

    private Control BuildFooter()
    {
        var create = new Button
        {
            Name = "CreateMapButton",
            Width = 200,
            MinHeight = 40,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    MapIcons.Stroked(MapIcons.NewMap, 18),
                    new TextBlock
                    {
                        Text = "Создать карту",

                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        }.Classed("primary", "wizard-action");
        create.Click += async (_, _) =>
        {
            create.IsEnabled = false;
            try { await CreateAsync(); }
            finally { create.IsEnabled = true; }
        };
        AutomationProperties.SetName(create, "Создать карту");
        create.IsDefault = true;

        var cancel = new Button
        {
            Name = "CancelWizardButton",
            Width = 120,
            MinHeight = 40,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Отмена",

                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        }.Classed("wizard-action");
        cancel.Click += (_, _) => Close();
        AutomationProperties.SetName(cancel, "Отмена");
        cancel.IsCancel = true;

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Child = new DockPanel
            {
                Children =
                {
                    create.Docked(Dock.Right),
                    cancel.Docked(Dock.Left),
                },
            },
        };
    }

    private static Control LabelledField(string label, Control field)
    {
        field.Width = 120;
        return new StackPanel
        {
            Spacing = 5,
            Children = { new TextBlock { Text = label }.Classed("role-panel"), field },
        };
    }

    private void SyncPresetButtons()
    {
        var regional = _startingPoint.SelectedIndex > 0;
        _sizeHint.Text = regional
            ? "Для региона — от 512 до 8192 пикселей по каждой стороне."
            : $"Допустимо от {NewMapWizardViewModel.MinDimension} до {NewMapWizardViewModel.MaxDimension} пикселей по каждой стороне.";
        foreach (var button in _presetButtons)
        {
            var preset = Presets.FirstOrDefault(p => button.Name == "WizardPreset" + p.Key);
            button.IsEnabled = !regional || preset is null || (preset.Width <= 8192 && preset.Height <= 8192);
            var active = button.Name == "WizardPreset" + _presetKey;
            Ui.SetClass(button, "active", active);
            button.IsChecked = active;
        }

        _customBlock.IsVisible = _presetKey == "Custom";
    }

    private void SyncBackgroundButtons()
    {
        foreach (var button in _backgroundButtons)
        {
            var active = button.Name == "WizardBackground" + _wizard.Background.TrimStart('#');
            Ui.SetClass(button, "active", active);
            button.IsChecked = active;
        }

        var index = Backgrounds.ToList().FindIndex(item => item.Key == _wizard.Background);
        if (index >= 0)
        {
            _backgroundCombo.SelectedIndex = index;
        }
    }

    private async Task CreateAsync()
    {
        _wizard.Title = _titleBox.Text ?? "";
        _wizard.WidthText = _widthBox.Text ?? "";
        _wizard.HeightText = _heightBox.Text ?? "";
        if (_backgroundCombo.SelectedItem is string chosen)
        {
            _wizard.Background = chosen;
        }

        var document = _wizard.Create(DateTimeOffset.UtcNow);
        if (document is null)
        {
            _error.Text = _wizard.ValidationError;
            _error.IsVisible = true;
            _presetKey = "Custom";
            SyncPresetButtons();
            return;
        }

        if (_startingPoint.SelectedIndex > 0 && _catalog is not null)
        {
            if (document.Canvas.Width is < 512 or > 8192 || document.Canvas.Height is < 512 or > 8192)
            {
                _error.Text = "Для иллюстрированного региона выберите от 512 до 8192 пикселей по каждой стороне.";
                _error.IsVisible = true; return;
            }
            _error.Text = "Подключение комплекта и создание региона…"; _error.IsVisible = true;
            var illustrated = _startingPoint.SelectedIndex == 2;
            try
            {
                _created = await Task.Run(() => RegionalMapFactory.Create(RegionalArtPack.Import(_catalog),
                    _catalog, document.Title, document.Canvas.Width, document.Canvas.Height, illustrated));
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            { _error.Text = "Не удалось создать регион: " + error.Message; return; }
        }
        else _created = document;
        Close();
    }

    /// <summary>Returns the created blank document, or null when the user cancelled.</summary>
    public static async Task<MapDocument?> ShowAsync(Window owner, AssetCatalog? catalog = null)
    {
        var window = new WizardWindow(catalog);
        await window.ShowDialog(owner);
        return window._created;
    }
}
