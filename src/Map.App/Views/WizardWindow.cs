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
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Assets;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The create-map dialog in two steps. Step 1 chooses the category - the workshop the map will
/// live in; categories never mix, so this choice is made once, before anything else, like a
/// style in Inkarnate's "Create New Map" (observed layout: a master list of cards on the left,
/// the chosen one described with a gallery on the right, Cancel / Choose in the footer). Step 2
/// is the settings of the map inside that category: title, starting point, size and background.
/// Presets carry the size decision so a first-time user never has to know that the canvas is
/// measured in pixels; the raw width and height only appear once "Свой размер" is chosen, which
/// is also the only way to reach the very large canvases.
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

    private const int CategoryStep = 1;
    private const int SettingsStep = 2;

    private static readonly Dictionary<string, Bitmap> GalleryBitmaps = new(StringComparer.Ordinal);

    private readonly NewMapWizardViewModel _wizard = new();
    private readonly List<ToggleButton> _presetButtons = [];
    private readonly List<ToggleButton> _backgroundButtons = [];
    private readonly List<ToggleButton> _categoryButtons = [];
    private readonly TextBox _titleBox = new() { Name = "WizardTitleBox" };
    private readonly TextBox _widthBox = new() { Name = "WizardWidthBox" };
    private readonly TextBox _heightBox = new() { Name = "WizardHeightBox" };
    private readonly ComboBox _backgroundCombo;
    private readonly TextBlock _error = new() { Name = "WizardErrorText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sizeHint = new() { Name = "WizardSizeHint", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stepTitle = new() { Name = "WizardStepTitle", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _categoryTitle = new() { Name = "WizardCategoryTitle", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _categoryTagline = new() { Name = "WizardCategoryTagline", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _categoryDescription = new() { Name = "WizardCategoryDescription", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _categoryHighlights = new() { Name = "WizardCategoryHighlights", Spacing = 6 };
    private readonly Border _categoryGallery = new() { Name = "WizardCategoryGallery", Height = 300 };
    private readonly ContentControl _stepHost = new() { Name = "WizardStepHost" };
    private readonly DockPanel _footer = new() { Name = "WizardFooter" };
    private readonly Button _choose;
    private readonly Button _back;
    private readonly Button _create;
    private readonly Button _cancel;
    private Control _categoryStep = null!;
    private Control _settingsStep = null!;
    private Control _customBlock = null!;
    private Control _backgroundBlock = null!;
    private Control _startBlock = null!;
    private Control _sizeBlock = null!;
    private Control _hexBlock = null!;
    private readonly TextBox _hexColumnsBox = new() { Name = "WizardHexColumnsBox" };
    private readonly TextBox _hexRowsBox = new() { Name = "WizardHexRowsBox" };
    private readonly TextBox _hexSizeBox = new() { Name = "WizardHexSizeBox" };
    private readonly TextBlock _hexCanvasHint = new() { Name = "WizardHexCanvasHint", TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<Button> _hexOrientationButtons = [];
    private string _presetKey = "Standard";
    private int _step;
    private MapCategoryDefinition _category = MapCategories.Standard;
    private MapCategoryDefinition? _appliedCategory;
    private MapDocument? _created;
    private readonly AssetCatalog? _catalog;
    private readonly ComboBox _startingPoint = new() { Name = "WizardStartingPoint", ItemsSource = new[]
        { "Чистый холст", "Тихий атлас · пустой регион", "Тихий атлас · долина Листвы" }, SelectedIndex = 0 };
    public MapDocument? CreatedDocument => _created;

    /// <summary>1 while the category is being chosen, 2 on the settings of the map.</summary>
    public int Step => _step;

    /// <summary>The category the new map will be created in.</summary>
    public MapCategoryId SelectedCategory => _category.Id;

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
        _choose = BuildChooseButton();
        _back = BuildBackButton();
        _create = BuildCreateButton();
        _cancel = BuildCancelButton();
        _categoryStep = BuildCategoryStep();
        _settingsStep = BuildSettingsStep();

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
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children = { _error, new Border { Margin = new Thickness(0, 4, 0, 0), Child = _footer } },
                    },
                }.Classed("dialog-footer").Docked(Dock.Bottom),
                _stepHost,
            },
        });

        SyncPresetButtons();
        SyncBackgroundButtons();
        SyncCategoryButtons();
        ShowStep(CategoryStep);
    }

    // ------------------------------------------------------------------ steps

    private void ShowStep(int step)
    {
        _step = step;
        _error.IsVisible = false;
        _footer.Children.Clear();
        if (step == CategoryStep)
        {
            Title = "Новая карта · шаг 1 из 2: категория";
            _stepTitle.Text = "Шаг 1 из 2 · Категория карты. Категории не смешиваются между собой: "
                + "выберите мастерскую, в которой будет жить карта.";
            _stepHost.Content = _categoryStep;
            _footer.Children.Add(_choose.Docked(Dock.Right));
            _footer.Children.Add(_cancel.Docked(Dock.Left));
            _create.IsDefault = false;
            _choose.IsDefault = true;
        }
        else
        {
            Title = "Новая карта · шаг 2 из 2: настройки";
            _stepTitle.Text = $"Шаг 2 из 2 · Настройки карты «{_category.Title}». "
                + "Выберите основу и размер. Содержимое и оформление карты можно менять позже.";
            ApplyCategoryDefaults();
            // A hex map is a board of hexes: no starting pack and no pixel canvas to choose.
            var hex = MapCategories.IsHex(_category.Id);
            _startBlock.IsVisible = !hex;
            _sizeBlock.IsVisible = !hex;
            _hexBlock.IsVisible = hex;
            if (hex) _startingPoint.SelectedIndex = 0;
            _stepHost.Content = _settingsStep;
            _footer.Children.Add(_create.Docked(Dock.Right));
            _footer.Children.Add(_back.Docked(Dock.Left));
            _footer.Children.Add(_cancel.Docked(Dock.Left));
            _choose.IsDefault = false;
            _create.IsDefault = true;
        }
    }

    /// <summary>
    /// A category brings its own starting size, as a style in Inkarnate brings its aspect ratio.
    /// Applied once per chosen category, so the user's own size survives going back and forth.
    /// </summary>
    private void ApplyCategoryDefaults()
    {
        if (ReferenceEquals(_appliedCategory, _category)) return;
        _appliedCategory = _category;
        var preset = Presets.FirstOrDefault(p => p.Width == _category.DefaultWidth && p.Height == _category.DefaultHeight);
        _presetKey = preset?.Key ?? "Custom";
        _widthBox.Text = _category.DefaultWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _heightBox.Text = _category.DefaultHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SyncPresetButtons();
        // Painted hexes read best on parchment; the user can still pick another page colour.
        if (MapCategories.IsHex(_category.Id))
        {
            _wizard.Background = "#FFF2E9DC";
            SyncBackgroundButtons();
        }
    }

    private Control BuildIntro() => new DockPanel
    {
        Children =
        {
            new Border
            {
                Child = ApprovedArtwork.Image("map-new", 72, 72),
                Margin = new Thickness(0, 0, 20, 0),
            }.Docked(Dock.Left),
            new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Новая карта" }.Classed("role-screen"),
                    _stepTitle.Classed("muted"),
                },
            },
        },
    };

    // --------------------------------------------------------- step 1: category

    private Control BuildCategoryStep()
    {
        var list = new StackPanel { Spacing = 8, Name = "WizardCategoryList" };
        foreach (var definition in MapCategories.All)
        {
            list.Children.Add(CategoryCard(definition));
        }

        var master = new Border
        {
            Width = 320,
            Margin = new Thickness(24, 24, 0, 24),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Категории" }.Classed("role-section"),
                        list,
                        new TextBlock
                        {
                            Text = "Каждая категория — отдельная мастерская со своими инструментами, размещением и генерацией. "
                                + "Общие у них только холст, файл карты, отрисовка и скорость.",
                            TextWrapping = TextWrapping.Wrap,
                        }.Classed("faint", "role-caption"),
                    },
                },
            },
        }.Classed("category-master").Named("WizardCategoryMaster");

        _categoryGallery.Classes.Add("category-gallery");
        var detail = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border
            {
                Padding = new Thickness(24, 24, 24, 24),
                Child = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        _categoryTitle.Classed("role-screen"),
                        _categoryTagline.Classed("muted"),
                        _categoryGallery,
                        _categoryDescription,
                        new TextBlock { Text = "Что внутри" }.Classed("role-section"),
                        _categoryHighlights,
                    },
                },
            },
        }.Named("WizardCategoryDetail");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(master, 0);
        Grid.SetColumn(detail, 1);
        grid.Children.Add(master);
        grid.Children.Add(detail);
        UpdateCategoryDetail();
        return grid;
    }

    private ToggleButton CategoryCard(MapCategoryDefinition definition)
    {
        var text = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = definition.Title,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15,
                    Foreground = Application.Current?.FindResource("MapTextBrush") as IBrush,
                },
                new TextBlock
                {
                    Text = definition.Tagline,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Application.Current?.FindResource("MapMutedBrush") as IBrush,
                }.Classed("muted"),
            },
        };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var art = new Border
        {
            Width = 56,
            Height = 56,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = ApprovedArtwork.Image(definition.ArtworkKey, 56, 56),
        };
        Grid.SetColumn(art, 0);
        Grid.SetColumn(text, 1);
        content.Children.Add(art);
        content.Children.Add(text);

        var button = new ToggleButton { Content = content, MinHeight = 84 }
            .Named("WizardCategory" + PascalCase(definition.Id.Value)).Classed("category-card");
        AutomationProperties.SetName(button, "Категория: " + definition.Title);
        ToolTip.SetTip(button, definition.Title);
        button.Click += (_, _) =>
        {
            _category = definition;
            SyncCategoryButtons();
            UpdateCategoryDetail();
        };
        _categoryButtons.Add(button);
        return button;
    }

    private static string PascalCase(string token) => string.Concat(
        token.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private void SyncCategoryButtons()
    {
        foreach (var button in _categoryButtons)
        {
            var active = button.Name == "WizardCategory" + PascalCase(_category.Id.Value);
            Ui.SetClass(button, "active", active);
            button.IsChecked = active;
        }
    }

    private void UpdateCategoryDetail()
    {
        _categoryTitle.Text = _category.Title;
        _categoryTagline.Text = _category.Tagline;
        _categoryDescription.Text = _category.Description;
        _categoryHighlights.Children.Clear();
        foreach (var highlight in _category.Highlights)
        {
            _categoryHighlights.Children.Add(new TextBlock
            {
                Text = "•  " + highlight,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        _categoryGallery.Child = GalleryImage(_category.GalleryKey);
        AutomationProperties.SetName(_choose, "Выбрать категорию: " + _category.Title);
    }

    /// <summary>The large example of the detail pane at its full resolution, decoded once per key.</summary>
    private static Control GalleryImage(string key)
    {
        try
        {
            Bitmap bitmap;
            lock (GalleryBitmaps)
            {
                if (!GalleryBitmaps.TryGetValue(key, out bitmap!))
                {
                    using var stream = AssetLoader.Open(new Uri("avares://Map.App/Assets/Atlas/" + key + ".png"));
                    bitmap = new Bitmap(stream);
                    GalleryBitmaps[key] = bitmap;
                }
            }

            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            return image;
        }
        catch (Exception)
        {
            // Missing artwork is a cosmetic gap, never a reason the map cannot be created.
            return new Border { Child = ApprovedArtwork.Image("map", 112, 112) };
        }
    }

    // --------------------------------------------------------- step 2: settings

    private Control BuildSettingsStep() => new ScrollViewer
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = new Border
        {
            Padding = new Thickness(24),
            Child = new StackPanel
            {
                Spacing = 20,
                Children =
                {
                    BuildNameBlock(), (_startBlock = BuildStartingPoint()), (_sizeBlock = BuildSizeBlock()),
                    (_hexBlock = BuildHexBoardBlock()), _backgroundBlock, _backgroundCombo,
                },
            },
        },
    }.Named("WizardSettingsStep");

    /// <summary>
    /// The board of a hex map instead of a canvas size: which way the hexes point, how many across
    /// and down, and how large one hex is. The canvas follows from the board.
    /// </summary>
    private Control BuildHexBoardBlock()
    {
        _hexColumnsBox.Text = _wizard.HexColumnsText;
        _hexRowsBox.Text = _wizard.HexRowsText;
        _hexSizeBox.Text = _wizard.HexSizeText;
        AutomationProperties.SetName(_hexColumnsBox, "Колонки гексов");
        AutomationProperties.SetName(_hexRowsBox, "Ряды гексов");
        AutomationProperties.SetName(_hexSizeBox, "Размер гекса в пикселях");
        var (orientation, buttons) = Ui.Segmented("WizardHexOrientation",
        [
            (nameof(HexOrientation.Flat), "Плоский верх · колонки", null),
            (nameof(HexOrientation.Pointy), "Острый верх · ряды", null),
        ], key =>
        {
            _wizard.HexOrientation = Enum.Parse<HexOrientation>(key);
            SyncHexBoard();
        });
        _hexOrientationButtons = buttons;
        foreach (var box in new[] { _hexColumnsBox, _hexRowsBox, _hexSizeBox })
        {
            box.TextChanged += (_, _) => SyncHexBoard();
        }

        var block = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Поле гексов" }.Classed("role-section"),
                orientation,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        LabelledField("Колонки", _hexColumnsBox),
                        LabelledField("Ряды", _hexRowsBox),
                        LabelledField("Размер гекса, px", _hexSizeBox),
                    },
                },
                _hexCanvasHint.Classed("faint", "role-caption"),
            },
        }.Named("WizardHexBoard");
        block.IsVisible = false;
        SyncHexBoard();
        return block;
    }

    private void SyncHexBoard()
    {
        _wizard.HexColumnsText = _hexColumnsBox.Text ?? "";
        _wizard.HexRowsText = _hexRowsBox.Text ?? "";
        _wizard.HexSizeText = _hexSizeBox.Text ?? "";
        foreach (var button in _hexOrientationButtons.OfType<ToggleButton>())
        {
            var active = button.Name == "WizardHexOrientation" + _wizard.HexOrientation;
            button.IsChecked = active;
            Ui.SetClass(button, "active", active);
        }

        _hexCanvasHint.Text = _wizard.HexCanvasSize is { } canvas
            ? $"Холст под это поле: {canvas.Width} × {canvas.Height} пикселей. Размер гекса — от центра до угла."
            : $"Колонки и ряды — от 1 до {NewMapWizardViewModel.MaxHexDimension}, размер гекса — "
              + $"от {NewMapWizardViewModel.MinHexSize} до {NewMapWizardViewModel.MaxHexSize} пикселей.";
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
            Height = 124,
            Margin = new Thickness(4),
            Content = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Border
                    {
                        Height = 44,
                        // Each size has its own sheet: the finer its grid, the more of the world it holds.
                        Child = ApprovedArtwork.Image("map-" + key.ToLowerInvariant(), 44, 44),
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

    // ----------------------------------------------------------------- footer

    private Button BuildChooseButton()
    {
        var choose = new Button
        {
            Name = "ChooseCategoryButton",
            MinWidth = 220,
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
                        Text = "Выбрать категорию",
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    MapIcons.Stroked(MapIcons.ChevronRight, 18),
                },
            },
        }.Classed("primary", "wizard-action");
        choose.Click += (_, _) => ShowStep(SettingsStep);
        AutomationProperties.SetName(choose, "Выбрать категорию");
        return choose;
    }

    private Button BuildBackButton()
    {
        var back = new Button
        {
            Name = "WizardBackButton",
            Width = 120,
            MinHeight = 40,
            Margin = new Thickness(0, 0, 8, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    MapIcons.Stroked(MapIcons.Back, 18),
                    new TextBlock { Text = "Назад", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        }.Classed("wizard-action");
        back.Click += (_, _) => ShowStep(CategoryStep);
        AutomationProperties.SetName(back, "Назад к выбору категории");
        ToolTip.SetTip(back, "Вернуться к выбору категории");
        return back;
    }

    private Button BuildCreateButton()
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
        return create;
    }

    private Button BuildCancelButton()
    {
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
        return cancel;
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
        _wizard.Category = _category.Id;
        SyncHexBoard();
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

    /// <summary>Returns the created document, or null when the user cancelled.</summary>
    public static async Task<MapDocument?> ShowAsync(Window owner, AssetCatalog? catalog = null)
    {
        var window = new WizardWindow(catalog);
        await window.ShowDialog(owner);
        return window._created;
    }
}
