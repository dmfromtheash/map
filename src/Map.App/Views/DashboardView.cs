using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.Presentation.Services;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The start of the program, laid out like Inkarnate's dashboard (measured through REA on
/// 2026-10-17): a narrow rail of sections on the left, and a library page with "New map", a
/// search over every map and folder, sorting and filtering, the Maps / Folders / Exports /
/// Favourites tabs with their counts and a grid of 300×220 map cards that show "Открыть", a menu
/// and the dates when the pointer reaches them. Map's own look stays: the home page keeps the
/// owner's artwork with "Ваша мастерская", the tavern palette, the serif headings and the
/// tactile buttons. Every map in the library folder is listed, not only the recently opened ones.
/// </summary>
public sealed class DashboardView : UserControl
{
    private const double CardWidth = 300;
    private const double CardGap = 20;
    private static readonly string[] SortLabels = ["Недавние", "По имени", "По типу", "По размеру"];
    private static readonly Lazy<Bitmap> HomeArtwork = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://Map.App/Assets/Atlas/home-atlas.png"));
        return new Bitmap(stream);
    });

    private enum Section { Home, Maps }

    private readonly DashboardViewModel _dashboard;
    private readonly Func<Task> _newMapRequested;
    private readonly Func<Task> _openFileRequested;
    private readonly Func<RecentCard, Task> _openRecentRequested;
    private readonly Func<RecoveryCard, Task> _restoreRequested;
    private readonly EditorShortcutSettings _shortcuts;
    private readonly Func<RecentCard, Task>? _cloneRequested;
    private readonly Func<RecentCard, Task>? _deleteRequested;
    private readonly Func<Task>? _templateRequested;
    private readonly Func<Task>? _backupRequested;
    private readonly Action? _settingsRequested;
    private readonly Action? _helpRequested;

    private readonly StackPanel _errorPanel = new() { Spacing = 8 };
    private readonly StackPanel _recoveryPanel = new() { Spacing = 10 };
    private readonly Border _preview = new() { Name = "DashboardPreview", IsVisible = false };
    private readonly Border _help = new() { Name = "DashboardHelpTopic", IsVisible = false };
    private readonly TextBlock _recentCount = new() { Name = "RecentCountText" };
    private readonly Dictionary<string, Bitmap> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Section, ToggleButton> _railButtons = [];
    private readonly Border _sectionHost = new();
    private readonly ScrollViewer _scroll;

    // Home
    private readonly StackPanel _home = new() { Name = "DashboardHome", Spacing = 22 };
    private readonly StackPanel _intro = new() { Spacing = 18 };
    private readonly WrapPanel _latestPanel = new() { Name = "DashboardLatestGrid", Orientation = Orientation.Horizontal, ItemSpacing = CardGap, LineSpacing = CardGap };
    private readonly StackPanel _latestBlock = new() { Spacing = 14 };
    private Border? _welcome;
    private LinearGradientBrush? _welcomeShade;
    private sealed record WelcomeLayout(StackPanel Copy, TextBlock Heading, TextBlock Lead);
    private WelcomeLayout? _welcomeLayout;
    private readonly List<Button> _entryButtons = [];
    private double _homeFontSize = 20;
    private bool? _empty;

    // Maps (the library page)
    private readonly StackPanel _maps = new() { Name = "DashboardLibrary", Spacing = 18 };
    private readonly WrapPanel _recentPanel = new() { Name = "RecentCardsGrid", Orientation = Orientation.Horizontal, ItemSpacing = CardGap, LineSpacing = CardGap };
    private readonly Border _noResults = new() { Name = "DashboardNoResults", IsVisible = false };
    private readonly Dictionary<DashboardTab, Button> _tabs = [];
    private readonly StackPanel _breadcrumb = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly StackPanel _tabTools = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private TextBox _search = null!;
    private Ui.InlineChoice _sort = null!;
    private Ui.InlineChoice _typeFilter = null!;
    private Section _section = Section.Home;
    private IReadOnlyList<RecentCard>? _renderedCards;
    private object? _renderedTab;
    private bool _revealPending = true;
    private string? _helpTopic;

    public DashboardView(
        DashboardViewModel dashboard,
        Func<Task> newMapRequested,
        Func<Task> openFileRequested,
        Func<RecentCard, Task> openRecentRequested,
        Func<RecoveryCard, Task> restoreRequested,
        EditorShortcutSettings? shortcuts = null,
        Func<RecentCard, Task>? cloneRequested = null,
        Func<RecentCard, Task>? deleteRequested = null,
        Func<Task>? templateRequested = null,
        Func<Task>? backupRequested = null,
        Action? settingsRequested = null,
        Action? helpRequested = null)
    {
        _dashboard = dashboard;
        _newMapRequested = newMapRequested;
        _openFileRequested = openFileRequested;
        _openRecentRequested = openRecentRequested;
        _restoreRequested = restoreRequested;
        _shortcuts = shortcuts ?? new EditorShortcutSettings();
        _cloneRequested = cloneRequested;
        _deleteRequested = deleteRequested;
        _templateRequested = templateRequested;
        _backupRequested = backupRequested;
        _settingsRequested = settingsRequested;
        _helpRequested = helpRequested;
        Name = "DashboardView";

        BuildHomeSection();
        BuildMapsSection();

        var body = new StackPanel
        {
            Name = "DashboardContent",
            // Wide screens get a wide page: the grid and the artwork fill it (FitCards, FitWelcome).
            MaxWidth = 2400,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(40, 26, 40, 32), Spacing = 18,
            Children = { _errorPanel, _recoveryPanel, _sectionHost, _help },
        };
        _scroll = new ScrollViewer
        {
            Name = "DashboardScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        };
        Content = Ui.WorkspaceBackdrop(new DockPanel
        {
            Children =
            {
                BuildRail().Docked(Dock.Left),
                _scroll,
            },
        });
        LayoutUpdated += (_, _) => FitWelcomeToViewport();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_preview.IsVisible) { ClosePreview(); e.Handled = true; }
            else if (_help.IsVisible) { _help.IsVisible = false; e.Handled = true; }
            else if (_dashboard.CurrentFolder is not null) { _dashboard.CurrentFolder = null; e.Handled = true; }
        };
        AttachedToVisualTree += (_, _) => { _revealPending = true; _dashboard.PropertyChanged += DashboardChanged; Rebuild(); };
        DetachedFromVisualTree += (_, _) =>
        {
            _dashboard.PropertyChanged -= DashboardChanged;
            ReleaseThumbnails(); _renderedCards = null;
        };
        _recentPanel.SizeChanged += (_, _) => FitCards(_recentPanel, oneRow: false);
        _latestPanel.SizeChanged += (_, _) => FitCards(_latestPanel, oneRow: true);
        ShowSection(Section.Home);
        Rebuild();
    }

    /// <summary>
    /// Cards fill their row: as many columns of at least 260 px as the width allows, every card
    /// stretched to share the row (up to 440 px), its picture keeping the 300×220 proportion. The
    /// latest maps on the home page are one full row - as many as fit, none cut in half.
    /// </summary>
    private static void FitCards(WrapPanel panel, bool oneRow)
    {
        var width = panel.Bounds.Width;
        if (width <= 0) return;
        var columns = Math.Max(1, (int)((width + CardGap) / (260 + CardGap)));
        var cardWidth = Math.Min(440, Math.Floor((width - CardGap * (columns - 1)) / columns));
        var index = 0;
        foreach (var card in panel.Children.OfType<StackPanel>())
        {
            if (Math.Abs(card.Width - cardWidth) > .5) card.Width = cardWidth;
            if (card.Children.FirstOrDefault() is Panel picture) picture.Height = Math.Round(cardWidth * 220 / 300);
            card.IsVisible = !oneRow || index < columns;
            index++;
        }
    }

    public void Refresh() { _dashboard.Refresh(); Rebuild(); }

    private void DashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Rebuild();
        else Dispatcher.UIThread.Post(Rebuild);
    }

    private static IBrush? Brush(string key) => Application.Current?.FindResource(key) as IBrush;
    private static FontFamily Display =>
        Application.Current?.FindResource("MapDisplayFont") as FontFamily ?? FontFamily.Default;

    // ------------------------------------------------------------------ rail

    /// <summary>Inkarnate's 80 px section rail: Create on top, the sections, then sound and settings at the foot.</summary>
    private Control BuildRail()
    {
        Button Item(string name, string caption, string tip, MapIcons.SolidGlyph glyph, Action action, Section? section = null)
        {
            Button button = section is null ? new Button() : new ToggleButton();
            button.Content = new StackPanel
            {
                Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = MapIcons.Solid24(glyph, 20) },
                    new TextBlock { Text = caption, HorizontalAlignment = HorizontalAlignment.Center }.Classed("library-rail-caption"),
                },
            };
            button.Name = name;
            button.Classes.Add("library-rail-item");
            AutomationProperties.SetName(button, tip);
            ToolTip.SetTip(button, tip);
            ToolTip.SetPlacement(button, PlacementMode.Right);
            ToolTip.SetShowDelay(button, 300);
            button.Click += (_, _) => action();
            if (section is { } s && button is ToggleButton toggle) _railButtons[s] = toggle;
            return button;
        }

        var create = new Button
        {
            Name = "RailCreateButton",
            Content = new StackPanel
            {
                Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Center,
                        Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                            Child = MapIcons.Solid24(MapIcons.SolidPlus, 18) } }.Classed("library-create-disc"),
                    new TextBlock { Text = "Создать", HorizontalAlignment = HorizontalAlignment.Center }.Classed("library-rail-caption"),
                },
            },
        }.Classed("library-rail-item", "library-rail-create");
        AutomationProperties.SetName(create, "Создать новую карту");
        ToolTip.SetTip(create, $"Создать новую карту  ({_shortcuts.Get(EditorShortcutSettings.Commands.NewMap)})");
        ToolTip.SetPlacement(create, PlacementMode.Right);
        create.Click += async (_, _) => await _newMapRequested();

        var top = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new Border { Margin = new Thickness(0, 4, 0, 10), HorizontalAlignment = HorizontalAlignment.Center,
                    Child = ApprovedArtwork.Image("map", 30, 30).Named("DashboardBrandIcon") },
                create,
                new Border { Height = 8 },
                Item("RailHomeButton", "Главная", "Главная: ваша мастерская и последние карты", MapIcons.SolidHome, () => ShowSection(Section.Home), Section.Home),
                Item("RailMapsButton", "Карты", "Карты: все карты, папки, экспорт и избранное", MapIcons.SolidMaps, () => ShowSection(Section.Maps), Section.Maps),
            },
        };
        if (_templateRequested is not null)
            top.Children.Add(Item("RailTemplateButton", "Шаблоны", "Создать карту из шаблона", MapIcons.SolidTemplate,
                () => _ = _templateRequested()));
        top.Children.Add(Item("RailPngButton", "PNG", "Просмотр готовой картинки карты (PNG)", MapIcons.SolidPicture,
            () => PngMapWindow.ShowFor(TopLevel.GetTopLevel(this) as Window)));

        var bottom = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Bottom };
        var sound = SoundToggle.Create("DashboardSoundButton", withCaption: false);
        sound.HorizontalAlignment = HorizontalAlignment.Center;
        bottom.Children.Add(sound);
        if (_helpRequested is not null)
            bottom.Children.Add(Item("LocalHelpButton", "Справка", "Справка", MapIcons.SolidHelp, _helpRequested));
        if (_settingsRequested is not null)
            bottom.Children.Add(Item("AppSettingsButton", "Настройки", "Настройки программы", MapIcons.SolidGear, _settingsRequested));

        foreach (var button in top.Children.OfType<Button>().Concat(bottom.Children.OfType<Button>()))
            Tactile.Attach(button, ornaments: false);

        return new Border
        {
            Name = "DashboardRail",
            Width = 84,
            Padding = new Thickness(0, 12, 0, 14),
            Child = new DockPanel { Children = { bottom.Docked(Dock.Bottom), top } },
        }.Classed("library-rail");
    }

    private void ShowSection(Section section)
    {
        _section = section;
        foreach (var (key, button) in _railButtons) button.IsChecked = key == section;
        ClosePreview();
        _sectionHost.Child = section == Section.Home ? _home : _maps;
        if (section == Section.Maps) _renderedCards = null;
        Rebuild();
        _scroll?.ScrollToHome();
    }

    // ------------------------------------------------------------------ home

    private void BuildHomeSection()
    {
        var allMaps = Ui.TextButton("DashboardAllMapsButton", "Все карты", () => ShowSection(Section.Maps), "ghost");
        allMaps.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Text = "Все карты" }, MapIcons.Stroked(MapIcons.ChevronRight, 16) } };
        HomeButton(allMaps);
        _latestBlock.Children.Add(new DockPanel
        {
            Children =
            {
                allMaps.Docked(Dock.Right),
                new TextBlock { Text = "Последние карты", FontFamily = Display, FontSize = 24, FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center },
            },
        });
        _latestBlock.Children.Add(_latestPanel);
        _home.Children.Add(_intro);
        _home.Children.Add(_latestBlock);
        _home.Children.Add(BuildHelpLinks());
    }

    // Every button on the home page shares one caption font and size (owner, 2026-10-21);
    // FitWelcomeToViewport scales the size with the window.
    private void HomeButton(Button button)
    {
        button.Classes.Add("home-command");
        button.FontFamily = Display;
        button.FontWeight = FontWeight.SemiBold;
        button.FontSize = _homeFontSize;
    }

    private Button EntryButton(string name, string text, Func<Task> action, bool primary = false)
    {
        var button = Ui.AsyncButton(name, text, action, primary ? "primary" : "dashboard-command");
        button.MinHeight = 40;
        button.Padding = new Thickness(14, 8);
        HomeButton(button);
        _entryButtons.Add(button);
        return button;
    }

    private void BuildIntro(bool empty)
    {
        _intro.Children.Clear(); _entryButtons.Clear();
        _welcome = null; _welcomeShade = null; _welcomeLayout = null;
        var create = EntryButton("NewMapButton", "Создать карту", _newMapRequested, true);
        ToolTip.SetTip(create, $"Создать новую карту  ({_shortcuts.Get(EditorShortcutSettings.Commands.NewMap)})");
        var open = EntryButton("OpenFileButton", "Открыть проект", _openFileRequested);
        ToolTip.SetTip(open, $"Открыть файл проекта .mapproj  ({_shortcuts.Get(EditorShortcutSettings.Commands.OpenMap)})");
        var extras = new WrapPanel { ItemSpacing = 12, LineSpacing = 10 };
        if (_templateRequested is not null)
            extras.Children.Add(EntryButton("CreateFromTemplateButton", "Из шаблона", _templateRequested));
        if (_backupRequested is not null)
            extras.Children.Add(EntryButton("ImportBackupButton", "Импорт копии", _backupRequested));
        _intro.Children.Add(BuildWelcome(create, open, extras, empty));
    }

    // Every welcome command shares one caption font, size and icon; only the height marks the main two.
    private static void WelcomeCommand(Button button, string caption, string icon)
    {
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Padding = new Thickness(20, 10);
        button.CornerRadius = new CornerRadius(8);
        var label = new TextBlock { Name = button.Name + "Caption", Text = caption,
            FontFamily = Display, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        button.Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing = 14,
            Children = { MapIcons.Stroked(icon, 22), label },
        };
    }

    private Control BuildWelcome(Button create, Button open, WrapPanel extras, bool empty)
    {
        WelcomeCommand(create, "Создать карту", MapIcons.NewMap);
        WelcomeCommand(open, "Открыть проект", MapIcons.Open);
        var secondary = new StackPanel { Name = "DashboardSecondaryActions", Spacing = 6 };
        foreach (var button in extras.Children.OfType<Button>().ToArray())
        {
            extras.Children.Remove(button);
            var (caption, icon) = button.Name switch
            {
                "CreateFromTemplateButton" => ("Из шаблона", MapIcons.Duplicate),
                _ => ("Импорт копии", MapIcons.Import),
            };
            WelcomeCommand(button, caption, icon);
            secondary.Children.Add(button);
        }
        secondary.IsVisible = secondary.Children.Count > 0;
        var heading = new TextBlock { Name = "DashboardWelcomeHeading",
            Text = empty ? "Начните с\nчистого листа" : "С возвращением\nв мастерскую",
            FontFamily = Display, FontSize = 40, FontWeight = FontWeight.SemiBold, LineHeight = 44,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var lead = new TextBlock { Name = "DashboardWelcomeLead",
            Text = empty
                ? "Создайте новую карту или откройте\nсохранённый проект."
                : "Продолжите последнюю карту ниже\nили откройте все карты слева.",
            FontSize = 16, LineHeight = 24, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap }.Classed("muted");
        var copy = new StackPanel
        {
            Name = "DashboardWelcomeCopy", Width = 360, Spacing = 16,
            Margin = new Thickness(28, 18), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel { Spacing = 12, Children =
                {
                    new TextBlock { Text = "Ваша мастерская", FontSize = 19, FontFamily = Display,
                        FontStyle = FontStyle.Italic, FontWeight = FontWeight.Medium, TextAlignment = TextAlignment.Center,
                        Foreground = Brush("MapAccentBrush") },
                    heading, lead,
                } },
                new StackPanel { Name = "DashboardWelcomeActions", Spacing = 12,
                    Children = { create, open, secondary } },
            },
        };
        _welcomeLayout = new WelcomeLayout(copy, heading, lead);
        Tactile.Attach(create);
        Tactile.Attach(open);
        foreach (var button in secondary.Children.OfType<Button>()) Tactile.Attach(button);
        if (_revealPending)
        {
            _revealPending = false;
            var entrance = new List<Control> { copy.Children[0], create, open };
            entrance.AddRange(secondary.Children.OfType<Control>());
            Tactile.Reveal(entrance, firstDelayMs: 80, staggerMs: 80);
        }
        var artwork = new Image { Name = "DashboardHeroArtwork", Source = HomeArtwork.Value,
            Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        RenderOptions.SetBitmapInterpolationMode(artwork, BitmapInterpolationMode.HighQuality);
        return _welcome = new Border
        {
            Name = "DashboardWelcome", Height = 420,
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
            BorderBrush = Brush("MapBorderBrush"), ClipToBounds = true,
            Child = new Panel
            {
                Children =
                {
                    artwork,
                    new Border
                    {
                        IsHitTestVisible = false,
                        Background = _welcomeShade = new LinearGradientBrush
                        {
                            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                            GradientStops = new GradientStops
                            {
                                new GradientStop(Color.Parse("#1A140F"), 0),
                                new GradientStop(Color.Parse("#F21A140F"), .28),
                                new GradientStop(Color.Parse("#001A140F"), .62),
                            },
                        },
                    },
                    copy,
                },
            },
        };
    }

    /// <summary>
    /// The artwork takes the screen when there is nothing else; with maps it leaves room for the
    /// row of latest maps below it, so both are seen without scrolling.
    /// </summary>
    private void FitWelcomeToViewport()
    {
        if (_section != Section.Home || _welcome is null || _welcomeLayout is not { } layout || !_welcome.IsEffectivelyVisible
            || !_welcome.IsMeasureValid || _scroll.Viewport.Height <= 0) return;

        var width = _welcome.Bounds.Width;
        if (width <= 0) return;
        var viewport = _scroll.Viewport.Height;
        var compact = width < 1120 || viewport < 750;
        var large = width >= 1720 && viewport >= 1000;
        layout.Copy.Width = Math.Clamp(width * .34, 360, 640);
        layout.Copy.Margin = new Thickness(Math.Clamp(width * .04, 28, 88), compact ? 18 : 40);
        layout.Copy.Spacing = compact ? 14 : 24;
        layout.Heading.FontSize = compact ? 34 : large ? 54 : 44;
        layout.Heading.LineHeight = compact ? 40 : large ? 60 : 50;
        layout.Lead.FontSize = compact ? 16 : 19;
        layout.Lead.LineHeight = compact ? 24 : 28;
        foreach (var button in _entryButtons)
        {
            var main = button.Name is "NewMapButton" or "OpenFileButton";
            button.MinHeight = main ? compact ? 52 : large ? 70 : 62 : compact ? 44 : 54;
        }
        _homeFontSize = compact ? 18 : large ? 22 : 20;
        foreach (var button in _home.GetLogicalDescendants().OfType<Button>())
            if (button.Classes.Contains("home-command") && button.FontSize != _homeFontSize) button.FontSize = _homeFontSize;
        if (!layout.Copy.IsMeasureValid) return;

        // Without maps the picture fills the page; with them it takes about half, and the
        // latest maps sit right under it.
        // Without maps the picture fills the page; with them it takes a little over half the
        // height (taller on tall screens), and the latest maps sit right under it.
        var target = Math.Floor(Math.Max(layout.Copy.DesiredSize.Height + 2,
            _empty == true ? viewport - 120 : Math.Clamp(viewport * .56, 360, 860)));
        if (Math.Abs(_welcome.Height - target) > .5) _welcome.Height = target;

        if (_welcomeShade is not null)
        {
            var solidEnd = (layout.Copy.Margin.Left + layout.Copy.Width * .8) / width;
            var fadeEnd = Math.Min(1, (layout.Copy.Margin.Left + layout.Copy.Width + 260) / width);
            if (Math.Abs(_welcomeShade.GradientStops[1].Offset - solidEnd) > .0001)
                _welcomeShade.GradientStops[1].Offset = solidEnd;
            if (Math.Abs(_welcomeShade.GradientStops[2].Offset - fadeEnd) > .0001)
                _welcomeShade.GradientStops[2].Offset = fadeEnd;
        }
    }

    private Control BuildHelpLinks()
    {
        var links = new WrapPanel { Name = "DashboardHelpLinks", ItemSpacing = 12, LineSpacing = 6 };
        foreach (var (name, title, text) in new[]
        {
            ("Layers", "Слои и объекты", "Слои помогают разделить фон, землю и объекты. В рабочей панели можно менять порядок, видимость и блокировку. Выберите объект на карте, чтобы настроить его размер, поворот и внешний вид."),
            ("Tools", "Работа с инструментами", "Выберите инструмент слева: объекты, земля, кисть, линии, фигуры, текст, маски или заметки. Параметры текущего инструмента находятся рядом с холстом. Esc отменяет незавершённое действие."),
            ("Save", "Где хранятся карты", $"Карты лежат в папке библиотеки ({_dashboard.Library?.Root ?? "выбранной при сохранении"}): всё, что там есть, видно в разделе «Карты». Сохранить: {_shortcuts.Get(EditorShortcutSettings.Commands.Save)}. Отменить: {_shortcuts.Get(EditorShortcutSettings.Commands.Undo)}. Автосохранённые копии предлагаются здесь для восстановления."),
        })
        {
            var key = name;
            var button = Ui.TextButton("DashboardHelp" + name, title, () =>
            {
                var show = !_help.IsVisible || _helpTopic != key;
                _helpTopic = key;
                _help.Child = new DockPanel
                {
                    Children =
                    {
                        Ui.IconButton("CloseDashboardHelpButton", "Закрыть подсказку", MapIcons.Close,
                            () => _help.IsVisible = false).Docked(Dock.Right),
                        new StackPanel { Spacing = 8, Children =
                        {
                            new TextBlock { Text = title, FontWeight = FontWeight.Medium },
                            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 },
                        } },
                    },
                };
                _help.IsVisible = show;
            }, "ghost", "compact");
            HomeButton(button);
            button.MinHeight = 36;
            links.Children.Add(button);
        }
        _help.Padding = new Thickness(16);
        _help.CornerRadius = new CornerRadius(8);
        _help.Background = Brush("MapPropertyGroupBrush");
        return links;
    }

    // ------------------------------------------------------------------ maps (library page)

    private void BuildMapsSection()
    {
        var create = Ui.AsyncButton("NewMapButton", "Новая карта", _newMapRequested, "primary");
        create.MinHeight = 38; create.Padding = new Thickness(16, 6); create.FontSize = 14;
        ToolTip.SetTip(create, $"Создать новую карту  ({_shortcuts.Get(EditorShortcutSettings.Commands.NewMap)})");

        _search = new TextBox { Name = "DashboardSearchBox", MinWidth = 220, VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = "Поиск карт и папок…", Text = _dashboard.SearchText };
        _search.InnerLeftContent = new Border { Margin = new Thickness(10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = MapIcons.Stroked(MapIcons.Search, 16, Brush("MapMutedBrush")) };
        AutomationProperties.SetName(_search, "Поиск карт и папок по названию или типу");
        _search.TextChanged += (_, _) => _dashboard.SearchText = _search.Text ?? "";

        _sort = new Ui.InlineChoice("DashboardSortChoice", "Порядок карт", SortLabels[(int)_dashboard.SortOrder],
            () => SortLabels.Select((label, index) => (label, (Action)(() =>
                _dashboard.SortOrder = (DashboardSortOrder)index))).ToArray()) { Width = 170 };
        _sort.Header.MinHeight = 38;
        _typeFilter = new Ui.InlineChoice("DashboardTypeChoice", "Тип карт", "Все типы",
            () => new[] { ("Все типы", (Action)(() => _dashboard.TypeFilter = "")) }
                .Concat(_dashboard.MapTypes.Select(type => (type, (Action)(() => _dashboard.TypeFilter = type)))).ToArray()) { Width = 190 };
        _typeFilter.Header.MinHeight = 38;
        _sort.Opened += (_, _) => _typeFilter.Close();
        _typeFilter.Opened += (_, _) => _sort.Close();

        var open = Ui.AsyncCommandButton("OpenFileButton", "Открыть файл…", MapIcons.Open, _openFileRequested, "dashboard-command");
        open.MinHeight = 38;
        ToolTip.SetTip(open, $"Открыть файл проекта .mapproj из любой папки  ({_shortcuts.Get(EditorShortcutSettings.Commands.OpenMap)})");
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Top,
            Children = { _sort, _typeFilter, open } };
        if (_backupRequested is not null)
        {
            var backup = Ui.AsyncCommandButton("ImportBackupButton", "Импорт копии", MapIcons.Import, _backupRequested, "dashboard-command");
            backup.MinHeight = 38;
            ToolTip.SetTip(backup, "Загрузить резервную копию карты");
            tools.Children.Add(backup);
        }
        foreach (var button in tools.Children.OfType<Button>().Append(create)) Tactile.Attach(button, ornaments: false);

        var toolbar = new Grid
        {
            Name = "DashboardLibraryTools",
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 16,
        };
        Grid.SetColumn(create, 0); create.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_search, 1); _search.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(tools, 2);
        toolbar.Children.Add(create); toolbar.Children.Add(_search); toolbar.Children.Add(tools);

        var tabs = new StackPanel { Name = "DashboardTabs", Orientation = Orientation.Horizontal, Spacing = 28 };
        foreach (var (tab, name) in new[] { (DashboardTab.Maps, "Карты"), (DashboardTab.Folders, "Папки"),
                     (DashboardTab.Exports, "Экспорт"), (DashboardTab.Favorites, "Избранное") })
        {
            var captured = tab;
            var button = new Button { Name = "DashboardTab" + tab }.Classed("library-tab");
            button.Click += (_, _) => { _dashboard.CurrentFolder = null; _dashboard.Tab = captured; };
            AutomationProperties.SetName(button, name);
            _tabs[tab] = button;
            tabs.Children.Add(button);
        }
        var tabRow = new DockPanel
        {
            Children =
            {
                _tabTools.Docked(Dock.Right),
                _recentCount.Docked(Dock.Right),
                tabs,
            },
        };
        _recentCount.Classes.Add("muted"); _recentCount.Classes.Add("role-caption");
        _recentCount.VerticalAlignment = VerticalAlignment.Center;
        _recentCount.Margin = new Thickness(0, 0, 16, 0);

        _noResults.Classes.Add("state-card");

        _maps.Children.Add(new StackPanel { Spacing = 16, Children =
        {
            new TextBlock { Name = "DashboardTitle", Text = "Мои карты", FontFamily = Display, FontSize = 30,
                FontWeight = FontWeight.SemiBold },
            toolbar,
            new Border { Child = tabRow, Padding = new Thickness(0, 4, 0, 0) }.Classed("library-tab-row"),
        } });
        _maps.Children.Add(_breadcrumb);
        _maps.Children.Add(_preview);
        _maps.Children.Add(_recentPanel);
        _maps.Children.Add(_noResults);
        _maps.Children.Add(BuildLibraryFooter());
    }

    private Control BuildLibraryFooter()
    {
        if (_dashboard.Library is not { } library) return new Border();
        var reveal = Ui.TextButton("RevealLibraryButton", "Открыть в Проводнике", () =>
        {
            library.EnsureExists();
            Reveal(library.Root, select: false);
        }, "ghost", "compact");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                MapIcons.Stroked(MapIcons.Folder, 16, Brush("MapMutedBrush")),
                new TextBlock { Text = "Папка библиотеки: " + library.Root, VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap }.Classed("muted", "role-caption"),
                reveal,
            },
        };
    }

    private void SyncTabs()
    {
        var counts = new Dictionary<DashboardTab, int>
        {
            [DashboardTab.Maps] = _dashboard.TotalRecentCount,
            [DashboardTab.Folders] = _dashboard.FolderCount,
            [DashboardTab.Exports] = _dashboard.ExportCount,
            [DashboardTab.Favorites] = _dashboard.FavoriteCount,
        };
        foreach (var (tab, button) in _tabs)
        {
            var label = tab switch { DashboardTab.Maps => "Карты", DashboardTab.Folders => "Папки", DashboardTab.Exports => "Экспорт", _ => "Избранное" };
            button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children =
            {
                new TextBlock { Text = label, FontSize = 16 },
                new TextBlock { Text = counts[tab].ToString(), FontSize = 16 }.Classed("library-tab-count"),
            } };
            Ui.SetClass(button, "selected", tab == _dashboard.Tab);
        }

        _tabTools.Children.Clear();
        if (_dashboard.Tab == DashboardTab.Folders && _dashboard.Library is not null)
        {
            var add = Ui.CommandButton("NewFolderButton", "Новая папка", MapIcons.Plus, CreateFolder, "dashboard-command");
            add.MinHeight = 34;
            Tactile.Attach(add, ornaments: false);
            _tabTools.Children.Add(add);
        }

        _breadcrumb.Children.Clear();
        _breadcrumb.IsVisible = _dashboard.CurrentFolder is not null;
        if (_dashboard.CurrentFolder is { } folder)
        {
            var back = Ui.TextButton("DashboardFolderBack", "Все карты", () => _dashboard.CurrentFolder = null, "ghost", "compact");
            back.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { MapIcons.Stroked(MapIcons.Back, 14), new TextBlock { Text = "Все карты" } } };
            _breadcrumb.Children.Add(back);
            _breadcrumb.Children.Add(new TextBlock { Text = "/", VerticalAlignment = VerticalAlignment.Center }.Classed("muted"));
            _breadcrumb.Children.Add(MapIcons.Stroked(MapIcons.Folder, 16, Brush("MapAccentBrush")));
            _breadcrumb.Children.Add(new TextBlock { Text = folder, FontSize = 16, FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center });
        }

        var total = _dashboard.TotalRecentCount + _dashboard.FolderCount;
        _search.PlaceholderText = $"Поиск: {total} {EditorLabels.Plural(total, "карта и папка", "карты и папки", "карт и папок")}…";
    }

    private void CreateFolder()
    {
        var name = _dashboard.CreateFolder("Новая папка");
        if (name is not null) _dashboard.LastNotice = $"Папка «{name}» создана. Переносите в неё карты через меню «⋯» на карточке.";
    }

    // ------------------------------------------------------------------ rebuild

    private void Rebuild()
    {
        var empty = _dashboard.TotalRecentCount == 0;
        if (_empty != empty) { _empty = empty; BuildIntro(empty); }
        foreach (var button in _entryButtons) button.IsEnabled = !_dashboard.IsBusy;
        BuildError(); BuildRecovery();
        _recoveryPanel.IsVisible = _recoveryPanel.Children.Count > 0;
        _sort.SetValue(SortLabels[(int)_dashboard.SortOrder]);
        _typeFilter.SetValue(_dashboard.TypeFilter.Length == 0 ? "Все типы" : _dashboard.TypeFilter);

        if (_section == Section.Home)
        {
            BuildLatest();
        }
        else
        {
            SyncTabs();
            var tabKey = (_dashboard.Tab, _dashboard.CurrentFolder, _dashboard.Folders, _dashboard.Exports);
            if (!ReferenceEquals(_renderedCards, _dashboard.RecentCards) || !Equals(_renderedTab, tabKey))
            {
                _renderedTab = tabKey;
                BuildGrid();
            }
        }
        foreach (var button in _recentPanel.GetVisualDescendants().OfType<Button>())
            button.IsEnabled = !_dashboard.IsBusy && button.Tag is not false;
    }

    private void BuildError()
    {
        _errorPanel.Children.Clear();
        foreach (var (name, text, error) in new[]
        {
            ("DashboardNoticeText", _dashboard.LastNotice, false),
            ("DashboardErrorText", _dashboard.LastError, true),
        })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var message = new Border { Child = new TextBlock { Name = name, Text = text,
                TextWrapping = TextWrapping.Wrap } }.Classed("state-card");
            if (error) message.Classes.Add("error");
            _errorPanel.Children.Add(message);
        }
        _errorPanel.IsVisible = _errorPanel.Children.Count > 0;
    }

    private void BuildRecovery()
    {
        _recoveryPanel.Children.Clear();
        var index = 0;
        foreach (var card in _dashboard.RecoveryCards)
        {
            var captured = card;
            var restore = Ui.AsyncButton(index == 0 ? "RestoreButton" : $"RestoreButton{index}", "Восстановить",
                () => _restoreRequested(captured), "primary");
            restore.IsEnabled = !_dashboard.IsBusy;
            var discard = Ui.TextButton(index == 0 ? "DiscardButton" : $"DiscardButton{index}", "Отбросить",
                () => _dashboard.Discard(captured), "destructive");
            discard.Margin = new Thickness(16, 0, 0, 0);
            discard.IsEnabled = !_dashboard.IsBusy;
            ToolTip.SetTip(discard, "Удалить несохранённую копию. Исходный файл не изменится.");
            _recoveryPanel.Children.Add(new Border
            {
                Child = new DockPanel
                {
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                            Children = { restore, discard } }.Docked(Dock.Right),
                        MapIcons.Stroked(MapIcons.Warning, 20, Brush("MapWarnBrush")).Docked(Dock.Left),
                        new StackPanel
                        {
                            Margin = new Thickness(12, 0, 16, 0), Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Name = $"RecoveryText{index}", Text = captured.Description, TextWrapping = TextWrapping.Wrap }.Classed("role-body"),
                                new TextBlock { Text = "Работа прервалась до записи файла. Исходный файл цел — восстановление откроет копию, ничего не перезаписывая.",
                                    TextWrapping = TextWrapping.Wrap }.Classed("muted", "role-caption"),
                            },
                        },
                    },
                },
            }.Classed("state-card", "attention"));
            index++;
        }
    }

    private void BuildLatest()
    {
        _latestPanel.Children.Clear();
        var latest = _dashboard.LatestCards;
        _latestBlock.IsVisible = latest.Count > 0;
        for (var i = 0; i < latest.Count; i++)
            _latestPanel.Children.Add(BuildMapCard(latest[i], i, "Latest"));
        FitCards(_latestPanel, oneRow: true);
    }

    private void BuildGrid()
    {
        ClosePreview(); _recentPanel.Children.Clear(); ReleaseThumbnails();
        _renderedCards = _dashboard.RecentCards;
        var tab = _dashboard.Tab;
        var count = 0;
        if (tab == DashboardTab.Folders && _dashboard.CurrentFolder is null)
        {
            var folders = _dashboard.Folders;
            for (var i = 0; i < folders.Count; i++) _recentPanel.Children.Add(BuildFolderCard(folders[i], i));
            count = folders.Count;
            _recentCount.Text = $"{count} {EditorLabels.Plural(count, "папка", "папки", "папок")}";
            ShowEmpty(count == 0, MapIcons.Folder, "Папок пока нет",
                "Папка собирает карты одной кампании или мира. Создайте папку и переносите в неё карты через меню «⋯».",
                ("Новая папка", CreateFolder));
        }
        else if (tab == DashboardTab.Exports)
        {
            var exports = _dashboard.Exports;
            for (var i = 0; i < exports.Count; i++) _recentPanel.Children.Add(BuildExportCard(exports[i], i));
            count = exports.Count;
            _recentCount.Text = $"{count} {EditorLabels.Plural(count, "картинка", "картинки", "картинок")}";
            ShowEmpty(count == 0, MapIcons.Export, "Готовых картинок пока нет",
                $"Сохраните экспорт карты в папку «{MapLibrary.ExportsFolderName}» библиотеки — картинки появятся здесь.",
                null);
        }
        else
        {
            for (var i = 0; i < _renderedCards.Count; i++)
                _recentPanel.Children.Add(BuildMapCard(_renderedCards[i], i, "Recent"));
            count = _renderedCards.Count;
            var filtered = !string.IsNullOrWhiteSpace(_dashboard.SearchText) || _dashboard.TypeFilter.Length > 0
                || _dashboard.CurrentFolder is not null || tab == DashboardTab.Favorites;
            _recentCount.Text = filtered
                ? $"Показано {count} из {_dashboard.TotalRecentCount}"
                : $"{count} {EditorLabels.Plural(count, "карта", "карты", "карт")}";
            if (count == 0)
            {
                if (tab == DashboardTab.Favorites && string.IsNullOrWhiteSpace(_dashboard.SearchText))
                    ShowEmpty(true, MapIcons.Star, "В избранном пока пусто",
                        "Наведите на карточку карты и нажмите звёздочку — карта появится здесь.", null);
                else if (_dashboard.TotalRecentCount == 0)
                    ShowEmpty(true, MapIcons.NewMap, "Здесь будут ваши карты",
                        "Создайте карту — при сохранении она попадёт в папку библиотеки и всегда будет видна в этом разделе.",
                        ("Создать карту", () => _ = _newMapRequested()));
                else if (_dashboard.CurrentFolder is not null && string.IsNullOrWhiteSpace(_dashboard.SearchText))
                    ShowEmpty(true, MapIcons.Folder, "В этой папке пока нет карт",
                        "Перенесите сюда карту через меню «⋯» на её карточке.", null);
                else
                    ShowEmpty(true, MapIcons.Search, "Ничего не найдено", "Попробуйте другое название или тип карты.",
                        ("Очистить поиск", () => { _search.Text = ""; _dashboard.TypeFilter = ""; }));
            }
            else ShowEmpty(false, "", "", "", null);
        }
        _recentPanel.IsVisible = count > 0;
        FitCards(_recentPanel, oneRow: false);
    }

    private void ShowEmpty(bool show, string icon, string title, string hint, (string Text, Action Action)? action)
    {
        _noResults.IsVisible = show;
        if (!show) { _noResults.Child = null; return; }
        var content = new StackPanel { Spacing = 12, Children = { Ui.EmptyState("NoRecentText", icon, title, hint) } };
        if (action is { } a)
            content.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center,
                Child = Ui.TextButton("ClearDashboardSearchButton", a.Text, a.Action, "dashboard-command") });
        _noResults.Child = content;
    }

    // ------------------------------------------------------------------ cards

    private Bitmap? Thumbnail(string key, byte[]? png, int width = (int)CardWidth * 2)
    {
        if (png is not { Length: > 0 }) return null;
        if (_thumbnails.TryGetValue(key, out var cached)) return cached;
        try
        {
            using var stream = new MemoryStream(png);
            var bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
            _thumbnails[key] = bitmap;
            return bitmap;
        }
        catch (Exception) { return null; }
    }

    private static Control ArtworkPlaceholder(string icon, string caption, string brushKey) => new Border
    {
        Background = Brush("MapSurfaceBrush"),
        Child = new StackPanel
        {
            Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = MapIcons.Stroked(icon, 28, Brush(brushKey)) },
                new TextBlock { Text = caption, FontSize = 12, Foreground = Brush(brushKey), HorizontalAlignment = HorizontalAlignment.Center },
            },
        },
    };

    /// <summary>
    /// One map, as Inkarnate's card: the picture (a click opens the map), and over it, when the
    /// pointer reaches it, the favourite star, the "⋯" menu, "Открыть" and the dates; the name
    /// and a quiet line of type, size and canvas below.
    /// </summary>
    private Control BuildMapCard(RecentCard card, int index, string prefix)
    {
        var captured = card;
        var main = prefix == "Recent";
        Control picture = !card.IsAvailable
            ? ArtworkPlaceholder(MapIcons.Missing, "Файл не найден", "MapDangerBrush")
            : Thumbnail(card.Path, card.ThumbnailPng) is { } bitmap
                ? new Image { Source = bitmap, Stretch = Stretch.UniformToFill }
                : new Border { Background = Brush("MapElevatedBrush"), Child = ApprovedArtwork.Image("map", 96, 96) };

        var openButton = new Button
        {
            Name = main ? $"RecentCard{index}" : $"LatestCard{index}",
            IsEnabled = card.IsAvailable && !_dashboard.IsBusy,
            Tag = card.IsAvailable,
            Content = picture,
        }.Classed("library-card-picture");
        var accessibleName = card.IsAvailable ? $"{card.Title}. Изменена {card.ModifiedUtc.ToLocalTime():g}" : $"{card.Title}. Файл не найден";
        AutomationProperties.SetName(openButton, accessibleName);
        ToolTip.SetTip(openButton, $"{accessibleName}\n{card.Path}");
        openButton.Click += async (_, _) => await _openRecentRequested(captured);

        var overlay = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), IsHitTestVisible = true }.Classed("library-card-overlay");
        // top row: favourite (left), menu (right)
        var top = new DockPanel { Margin = new Thickness(8) };
        if (_dashboard.Library is not null && card.IsAvailable)
        {
            var star = new Button
            {
                Name = $"{prefix}FavoriteButton{index}",
                Content = card.IsFavorite ? MapIcons.Solid24(MapIcons.SolidStar, 18) : MapIcons.Stroked(MapIcons.Star, 18),
            }.Classed("library-card-chip");
            Ui.SetClass(star, "on", card.IsFavorite);
            AutomationProperties.SetName(star, card.IsFavorite ? "Убрать из избранного" : "Добавить в избранное");
            ToolTip.SetTip(star, card.IsFavorite ? "Убрать из избранного" : "В избранное");
            star.Click += (_, e) => { e.Handled = true; _dashboard.SetFavorite(captured, !captured.IsFavorite); };
            top.Children.Add(star.Docked(Dock.Left));
        }
        var menu = new Button { Name = $"{prefix}CardMenu{index}", Content = MapIcons.Solid24(MapIcons.SolidMore, 16), Flyout = BuildCardMenu(card, index) }
            .Classed("library-card-chip");
        menu.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(menu, $"Действия с картой «{card.Title}»");
        ToolTip.SetTip(menu, "Действия: открыть, просмотр, копия, папка, удалить");
        top.Children.Add(menu);
        overlay.Children.Add(top);

        if (card.IsAvailable)
        {
            var open = new Button { Name = $"{prefix}OpenButton{index}", Content = new TextBlock { Text = "Открыть" },
                FontFamily = Display, FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center }.Classed("primary", "library-card-open");
            if (!main) HomeButton(open);
            open.Click += async (_, _) => await _openRecentRequested(captured);
            Grid.SetRow(open, 1);
            overlay.Children.Add(open);

            var dates = new StackPanel { Margin = new Thickness(10, 0, 10, 8), Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right,
                IsHitTestVisible = false };
            dates.Children.Add(new TextBlock { Text = $"Изменена: {card.ModifiedUtc.ToLocalTime():d MMM yyyy, HH:mm}" }.Classed("library-card-date"));
            if (card.CreatedUtc is { } created)
                dates.Children.Add(new TextBlock { Text = $"Создана: {created.ToLocalTime():d MMM yyyy}" }.Classed("library-card-date"));
            Grid.SetRow(dates, 2);
            overlay.Children.Add(dates);
        }

        var pictureHost = new Panel { Height = CardWidth * 220 / 300, Children = { openButton, overlay } }.Classed("library-card-thumb");

        var subtitle = card.IsAvailable
            ? string.Join(" · ", new[] { card.Folder is { Length: > 0 } f ? "папка «" + f + "»" : null, card.CanvasSize.Length > 0 ? card.CanvasSize + " пкс" : null,
                FormatSize(card.SizeBytes) }.Where(s => !string.IsNullOrEmpty(s)))
            : "Файл не найден";
        var title = new DockPanel
        {
            Margin = new Thickness(2, 8, 2, 0),
            Children =
            {
                new Border { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = card.IsFavorite ? MapIcons.Solid24(MapIcons.SolidStar, 13, Brush("MapAccentBrush")) : MapIcons.Stroked(MapIcons.LibraryMaps, 15, Brush("MapMutedBrush")) }.Docked(Dock.Left),
                new TextBlock { Text = card.Title, FontSize = 15, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis }
                    .Named(main ? $"RecentTitle{index}" : $"LatestTitle{index}"),
            },
        };
        var caption = new TextBlock { Text = subtitle, FontSize = 12, Margin = new Thickness(2, 2, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis }.Named(main ? $"RecentSubtitle{index}" : $"LatestSubtitle{index}")
            .Classed(card.IsAvailable ? "muted" : "danger");

        var shell = new StackPanel { Width = CardWidth, Children = { pictureHost, title, caption } }.Classed("library-card");
        Tactile.Attach(openButton, ornaments: false);
        return shell;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.#} ГБ",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.#} МБ",
        >= 1024 => $"{bytes / 1024d:0} КБ",
        _ => $"{bytes} Б",
    };

    private MenuFlyout BuildCardMenu(RecentCard card, int index)
    {
        var captured = card;
        var menu = new MenuFlyout();
        MenuItem Entry(string name, string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Name = name, Header = text, IsEnabled = enabled && !_dashboard.IsBusy };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Items.Add(Entry($"OpenRecentMenu{index}", "Открыть", () => _ = _openRecentRequested(captured), card.IsAvailable));
        menu.Items.Add(Entry($"PreviewRecentButton{index}", "Просмотр", () => ShowPreview(captured), card.IsAvailable));
        if (_cloneRequested is not null)
            menu.Items.Add(Entry($"CloneRecentButton{index}", "Сделать копию", () => _ = _cloneRequested(captured), card.IsAvailable));
        if (_dashboard.Library is not null && card.IsAvailable)
        {
            menu.Items.Add(Entry($"FavoriteRecentMenu{index}", card.IsFavorite ? "Убрать из избранного" : "В избранное",
                () => _dashboard.SetFavorite(captured, !captured.IsFavorite)));
            var move = new MenuItem { Header = "Переместить в папку", Name = $"MoveRecentMenu{index}" };
            move.Items.Add(Entry($"MoveRecentRoot{index}", "Библиотека (без папки)", () => _dashboard.MoveToFolder(captured, ""),
                !string.IsNullOrEmpty(card.Folder) || !InLibrary(card.Path)));
            foreach (var folder in _dashboard.Library.Scan().Folders)
            {
                var name = folder.Name;
                move.Items.Add(Entry($"MoveRecent{index}To{name}", name, () => _dashboard.MoveToFolder(captured, name), card.Folder != name));
            }
            move.Items.Add(new Separator());
            move.Items.Add(Entry($"MoveRecentNewFolder{index}", "Новая папка…", () =>
            {
                if (_dashboard.CreateFolder("Новая папка") is { } created) _dashboard.MoveToFolder(captured, created);
            }));
            menu.Items.Add(move);
        }
        if (card.IsAvailable)
            menu.Items.Add(Entry($"RevealRecentMenu{index}", "Показать в Проводнике", () => Reveal(captured.Path, select: true)));
        if (_deleteRequested is not null)
        {
            menu.Items.Add(new Separator());
            var delete = Entry($"DeleteRecentButton{index}", card.IsAvailable ? "Удалить в Корзину" : "Убрать запись", () => _ = _deleteRequested(captured));
            delete.Classes.Add("destructive");
            menu.Items.Add(delete);
        }
        return menu;
    }

    private bool InLibrary(string path)
    {
        if (_dashboard.Library is not { } library) return false;
        try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(library.Root), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    private Control BuildFolderCard(LibraryFolder folder, int index)
    {
        var name = folder.Name;
        var firstPreview = _dashboard.RecentCards.Concat(_renderedCards ?? []).FirstOrDefault(c => c.Folder == name && c.ThumbnailPng is { Length: > 0 });
        Control picture = new Border
        {
            Background = Brush("MapElevatedBrush"),
            Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = MapIcons.Solid24(MapIcons.SolidFolder, 72, Brush("MapAccentDimBrush")) },
        };
        if (firstPreview is not null && Thumbnail(firstPreview.Path, firstPreview.ThumbnailPng) is { } bitmap)
            picture = new Panel { Children =
            {
                new Image { Source = bitmap, Stretch = Stretch.UniformToFill, Opacity = .55 },
                new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Child = MapIcons.Solid24(MapIcons.SolidFolder, 64, Brush("MapAccentBrush")) },
            } };
        var open = new Button { Name = $"FolderCard{index}", Content = picture }.Classed("library-card-picture");
        AutomationProperties.SetName(open, $"Папка «{name}», {folder.MapCount} {EditorLabels.Plural(folder.MapCount, "карта", "карты", "карт")}");
        open.Click += (_, _) => { _dashboard.Tab = DashboardTab.Maps; _dashboard.CurrentFolder = name; };
        Tactile.Attach(open, ornaments: false);
        var reveal = new Button { Content = MapIcons.Solid24(MapIcons.SolidMore, 16),
            Flyout = new MenuFlyout { Items = { MenuItemOf("Открыть папку", () => { _dashboard.Tab = DashboardTab.Maps; _dashboard.CurrentFolder = name; }),
                MenuItemOf("Показать в Проводнике", () => Reveal(folder.Path, select: false)) } },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) }
            .Classed("library-card-chip");
        var overlay = new Panel { Children = { reveal } }.Classed("library-card-overlay");
        return new StackPanel
        {
            Width = CardWidth,
            Children =
            {
                new Panel { Height = CardWidth * 220 / 300, Children = { open, overlay } }.Classed("library-card-thumb"),
                new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeight.Medium, Margin = new Thickness(2, 8, 2, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{folder.MapCount} {EditorLabels.Plural(folder.MapCount, "карта", "карты", "карт")}", FontSize = 12,
                    Margin = new Thickness(2, 2, 2, 0) }.Classed("muted"),
            },
        }.Classed("library-card");
    }

    private static MenuItem MenuItemOf(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    private Control BuildExportCard(LibraryExport export, int index)
    {
        Control picture;
        try
        {
            if (!_thumbnails.TryGetValue(export.Path, out var bitmap))
            {
                using var stream = File.OpenRead(export.Path);
                bitmap = Bitmap.DecodeToWidth(stream, (int)CardWidth * 2, BitmapInterpolationMode.HighQuality);
                _thumbnails[export.Path] = bitmap;
            }
            picture = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch (Exception) { picture = ArtworkPlaceholder(MapIcons.Missing, "Не удалось прочитать", "MapDangerBrush"); }
        var open = new Button { Name = $"ExportCard{index}", Content = picture }.Classed("library-card-picture");
        AutomationProperties.SetName(open, $"Картинка «{export.Name}»");
        ToolTip.SetTip(open, export.Path);
        var path = export.Path;
        open.Click += (_, _) => PngMapWindow.ShowFor(TopLevel.GetTopLevel(this) as Window, path);
        Tactile.Attach(open, ornaments: false);
        var menu = new Button { Content = MapIcons.Solid24(MapIcons.SolidMore, 16),
            Flyout = new MenuFlyout { Items = { MenuItemOf("Просмотр", () => PngMapWindow.ShowFor(TopLevel.GetTopLevel(this) as Window, path)),
                MenuItemOf("Показать в Проводнике", () => Reveal(path, select: true)) } },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) }
            .Classed("library-card-chip");
        return new StackPanel
        {
            Width = CardWidth,
            Children =
            {
                new Panel { Height = CardWidth * 220 / 300, Children = { open, new Panel { Children = { menu } }.Classed("library-card-overlay") } }
                    .Classed("library-card-thumb"),
                new TextBlock { Text = export.Name, FontSize = 15, FontWeight = FontWeight.Medium, Margin = new Thickness(2, 8, 2, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = $"{export.ModifiedUtc.ToLocalTime():g} · {FormatSize(export.SizeBytes)}", FontSize = 12,
                    Margin = new Thickness(2, 2, 2, 0) }.Classed("muted"),
            },
        }.Classed("library-card");
    }

    private static void Reveal(string path, bool select)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ preview

    private void ClosePreview() { _preview.IsVisible = false; _preview.Child = null; }

    private void ShowPreview(RecentCard card)
    {
        if (_section != Section.Maps) ShowSection(Section.Maps);
        _sort.Close(); _typeFilter.Close();
        var open = Tactile.Attach(
            Ui.AsyncButton("OpenDashboardPreviewButton", "Открыть карту", () => _openRecentRequested(card), "primary"));
        open.IsEnabled = card.IsAvailable && !_dashboard.IsBusy;
        var heading = new DockPanel
        {
            Children =
            {
                Ui.IconButton("CloseDashboardPreviewButton", "Закрыть предпросмотр", MapIcons.Close, ClosePreview).Docked(Dock.Right),
                new TextBlock { Text = card.Title, FontSize = 18, FontWeight = FontWeight.Medium,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        Control picture = Thumbnail(card.Path + "#preview", card.ThumbnailPng, 1200) is { } bitmap
            ? new Image { Source = bitmap, Stretch = Stretch.Uniform, Height = 320 }
            : new Border { Height = 200, Child = ApprovedArtwork.Image("map", 112, 112) };
        _preview.Child = new StackPanel
        {
            Spacing = 12, Children =
            {
                heading, picture,
                new TextBlock { Text = card.Subtitle + (card.Folder is { Length: > 0 } f ? " · папка «" + f + "»" : ""), FontSize = 12, TextWrapping = TextWrapping.Wrap }.Classed("muted"),
                new TextBlock { Text = card.Path, FontSize = 12, TextWrapping = TextWrapping.Wrap }.Classed("faint"),
                new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = open },
            },
        };
        if (!_preview.Classes.Contains("state-card")) _preview.Classes.Add("state-card");
        _preview.IsVisible = true;
        Dispatcher.UIThread.Post(() => _preview.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void ReleaseThumbnails()
    {
        foreach (var bitmap in _thumbnails.Values) bitmap.Dispose();
        _thumbnails.Clear();
    }
}
