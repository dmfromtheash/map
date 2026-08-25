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
        new("Standard", "Обычная", "1920 × 1080", 1920, 1080),
        new("Large", "Крупная", "4096 × 4096", 4096, 4096),
        new("Huge", "Очень крупная", "16384 × 16384", 16384, 16384),
        new("Max", "Максимальная", "65536 × 65536", 65536, 65536),
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
    private Control _customBlock = null!;
    private string _presetKey = "Standard";
    private MapDocument? _created;

    private WizardWindow()
    {
        Title = "Новая карта";
        Name = "WizardWindow";
        Width = 700;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

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

        Content = new Border
        {
            Child = new StackPanel
            {
                Margin = new Thickness(24, 20),
                Spacing = 16,
                Children =
                {
                    BuildIntro(),
                    BuildNameBlock(),
                    BuildSizeBlock(),
                    BuildBackgroundBlock(),
                    _backgroundCombo,
                    _error,
                    BuildFooter(),
                },
            },
        };

        SyncPresetButtons();
        SyncBackgroundButtons();
    }

    private static Control BuildIntro() => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = "Новая карта" }.Classed("role-screen"),
            new TextBlock
            {
                Text = "Размер можно выбрать пресетом; точные значения нужны только для своего размера.",
                TextWrapping = TextWrapping.Wrap,
            }.Classed("muted", "role-caption"),
        },
    };

    private Control BuildNameBlock()
    {
        AutomationProperties.SetName(_titleBox, "Название карты");
        return new StackPanel
        {
            Spacing = 6,
            Children = { new TextBlock { Text = "Название" }.Classed("role-panel"), _titleBox },
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
                    new TextBlock
                    {
                        Text = $"Допустимо от {NewMapWizardViewModel.MinDimension} " +
                               $"до {NewMapWizardViewModel.MaxDimension} пикселей по каждой стороне.",
                        TextWrapping = TextWrapping.Wrap,
                    }.Classed("faint", "role-caption"),
                },
            },
        }.Classed("group").Named("WizardCustomSize");

        _customBlock.IsVisible = false;

        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Размер холста" }.Classed("role-panel"),
                strip,
                _customBlock,
            },
        };
    }

    private ToggleButton PresetCard(string key, string title, string detail)
    {
        var button = new ToggleButton
        {
            Height = 78,
            Margin = new Thickness(2),
            Content = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 12,
                    },
                    new TextBlock
                    {
                        Text = detail,
                        TextAlignment = Avalonia.Media.TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 11,
                    }.Classed("muted"),
                },
            },
        }.Named("WizardPreset" + key).Classed("segment");

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
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (var (key, title) in Backgrounds)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.Parse(key)),
                BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var button = new ToggleButton
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        swatch,
                        new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
                    },
                },
            }.Named("WizardBackground" + key.TrimStart('#')).Classed("segment");

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
            Spacing = 8,
            Children = { new TextBlock { Text = "Фон" }.Classed("role-panel"), strip },
        };
    }

    private Control BuildFooter()
    {
        var create = Ui.TextButton("CreateMapButton", "Создать карту", Create, "primary");
        create.IsDefault = true;

        var cancel = Ui.TextButton("CancelWizardButton", "Отмена", Close);
        cancel.IsCancel = true;
        cancel.MinHeight = 36;
        cancel.Padding = new Thickness(16, 6);

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
        }.Classed("group");
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
        foreach (var button in _presetButtons)
        {
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

    private void Create()
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

        _created = document;
        Close();
    }

    /// <summary>Returns the created blank document, or null when the user cancelled.</summary>
    public static async Task<MapDocument?> ShowAsync(Window owner)
    {
        var window = new WizardWindow();
        await window.ShowDialog(owner);
        return window._created;
    }
}
