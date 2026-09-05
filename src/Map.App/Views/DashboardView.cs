using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>A local workshop that uses the window: a first-map welcome, then real project previews.</summary>
public sealed class DashboardView : UserControl
{
    private const double ThumbnailHeight = 156;
    private static readonly string[] SortLabels = ["По дате изменения", "По имени", "По типу", "По размеру"];
    private static readonly Lazy<Bitmap> HomeArtwork = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://Map.App/Assets/Atlas/home-atlas.png"));
        return new Bitmap(stream);
    });
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
    private readonly StackPanel _intro = new() { Spacing = 18 };
    private readonly StackPanel _errorPanel = new() { Spacing = 8 };
    private readonly StackPanel _recoveryPanel = new() { Spacing = 10 };
    private readonly WrapPanel _recentPanel = new()
    {
        Name = "RecentCardsGrid", Orientation = Orientation.Horizontal,
        ItemSpacing = 16, LineSpacing = 16,
    };
    private readonly StackPanel _library = new() { Spacing = 16 };
    private readonly Border _preview = new() { Name = "DashboardPreview", IsVisible = false };
    private readonly Border _help = new() { Name = "DashboardHelpTopic", IsVisible = false };
    private readonly Border _noResults = new() { Name = "DashboardNoResults", IsVisible = false };
    private readonly TextBlock _recentCount = new() { Name = "RecentCountText" };
    private readonly List<Button> _entryButtons = [];
    private readonly Dictionary<string, Bitmap> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _body;
    private readonly ScrollViewer _scroll;
    private Border? _welcome;
    private LinearGradientBrush? _welcomeShade;
    private sealed record WelcomeLayout(StackPanel Copy, TextBlock Heading, TextBlock Lead);
    private WelcomeLayout? _welcomeLayout;
    private IReadOnlyList<RecentCard>? _renderedCards;
    private TextBox _search = null!;
    private Ui.InlineChoice _sort = null!;
    private Border? _moreActions;
    private Button? _moreButton;
    private bool? _empty;
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

        _library.Children.Add(BuildRecentHeader());
        _library.Children.Add(_preview);
        _library.Children.Add(_recentPanel);
        _noResults.Child = new StackPanel
        {
            Spacing = 12, Children =
            {
                Ui.EmptyState("NoRecentText", MapIcons.Search, "Ничего не найдено",
                    "Попробуйте другое название или тип карты."),
                new Border { HorizontalAlignment = HorizontalAlignment.Center,
                    Child = Ui.TextButton("ClearDashboardSearchButton", "Очистить поиск", () => _search.Text = "") },
            },
        };
        _noResults.Classes.Add("state-card");
        _library.Children.Add(_noResults);
        _recentPanel.SizeChanged += (_, _) => ResizeCards();

        _body = new StackPanel
        {
            Name = "DashboardContent",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(36, 24, 36, 28), Spacing = 20,
            Children = { _errorPanel, _recoveryPanel, _intro, _library, BuildHelpLinks(), _help },
        };
        _scroll = new ScrollViewer
        {
            Name = "DashboardScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _body,
        };
        Content = Ui.WorkspaceBackdrop(new DockPanel
        {
            Children =
            {
                BuildHeader().Docked(Dock.Top),
                _scroll,
            },
        });
        LayoutUpdated += (_, _) => FitWelcomeToViewport();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_moreActions?.IsVisible == true) { CloseMore(); _moreButton?.Focus(); e.Handled = true; }
            else if (_preview.IsVisible) { ClosePreview(); e.Handled = true; }
            else if (_help.IsVisible) { _help.IsVisible = false; e.Handled = true; }
        };
        AttachedToVisualTree += (_, _) => { _dashboard.PropertyChanged += DashboardChanged; Rebuild(); };
        DetachedFromVisualTree += (_, _) =>
        {
            _dashboard.PropertyChanged -= DashboardChanged;
            ReleaseThumbnails(); _renderedCards = null;
        };
        Rebuild();
    }

    public void Refresh() { _dashboard.Refresh(); Rebuild(); }

    private void DashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Rebuild();
        else Dispatcher.UIThread.Post(Rebuild);
    }

    private static IBrush? Brush(string key) => Application.Current?.FindResource(key) as IBrush;

    private Control BuildHeader()
    {
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ApprovedArtwork.Image("map", 32, 32).Named("DashboardBrandIcon"),
                new TextBlock { Text = "Map", FontSize = 22, FontWeight = FontWeight.Medium,
                    VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Библиотека карт", FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center }.Classed("muted"),
            },
        };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (_helpRequested is not null)
            commands.Children.Add(Ui.CommandButton("LocalHelpButton", "Справка", MapIcons.Help, _helpRequested, "compact"));
        if (_settingsRequested is not null)
            commands.Children.Add(Ui.CommandButton("AppSettingsButton", "Настройки", MapIcons.Gear, _settingsRequested, "compact"));
        foreach (var button in commands.Children.OfType<Button>())
        {
            button.FontSize = 14;
            button.MinHeight = 40;
            button.Padding = new Thickness(12, 8);
        }
        return new Border
        {
            Child = new DockPanel
            {
                Name = "DashboardHeaderContent",
                Margin = new Thickness(20, 10),
                Children = { commands.Docked(Dock.Right), brand },
            },
        }.Classed("app-bar");
    }

    private Button EntryButton(string name, string text, Func<Task> action, bool primary = false)
    {
        var button = Ui.AsyncButton(name, text, async () => { CloseMore(); await action(); },
            primary ? "primary" : "dashboard-command");
        button.MinHeight = 40;
        button.Padding = new Thickness(14, 8);
        button.FontSize = 13;
        _entryButtons.Add(button);
        return button;
    }

    private void BuildIntro(bool empty)
    {
        _intro.Children.Clear(); _entryButtons.Clear();
        _welcome = null; _welcomeShade = null; _welcomeLayout = null;
        _moreActions = null; _moreButton = null;
        var title = new TextBlock { Name = "DashboardTitle", Text = "Ваши карты",
            FontSize = 28, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var create = EntryButton("NewMapButton", empty ? "Создать карту" : "Новая карта", _newMapRequested, true);
        ToolTip.SetTip(create, $"Создать новую карту  ({_shortcuts.Get(EditorShortcutSettings.Commands.NewMap)})");
        var open = EntryButton("OpenFileButton", "Открыть проект", _openFileRequested);
        ToolTip.SetTip(open, $"Открыть файл проекта .mapproj  ({_shortcuts.Get(EditorShortcutSettings.Commands.OpenMap)})");
        var extras = new WrapPanel { ItemSpacing = 12, LineSpacing = 10 };
        if (_templateRequested is not null)
            extras.Children.Add(EntryButton("CreateFromTemplateButton", "Из шаблона", _templateRequested));
        if (_backupRequested is not null)
            extras.Children.Add(EntryButton("ImportBackupButton", "Импорт копии", _backupRequested));

        if (empty)
        {
            _intro.Children.Add(title);
            _intro.Children.Add(BuildWelcome(create, open, extras));
        }
        else
        {
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            commands.Children.Add(create); commands.Children.Add(open);
            if (extras.Children.Count > 0)
            {
                _moreActions = new Border { Name = "DashboardMoreOptions", Child = extras,
                    IsVisible = false, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
                    Background = Brush("MapPropertyGroupBrush"), BorderBrush = Brush("MapBorderBrush"),
                    BorderThickness = new Thickness(1) };
                _moreButton = Ui.TextButton("DashboardMoreButton", "Ещё", () =>
                {
                    if (_moreActions.IsVisible) CloseMore();
                    else { _sort.Close(); _moreActions.IsVisible = true; Ui.SetSelected(_moreButton!, true); }
                });
                _moreButton.Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = "Ещё", FontSize = 13 }, MapIcons.Stroked(MapIcons.ChevronDown, 13) },
                };
                _moreButton.MinHeight = 40;
                commands.Children.Add(_moreButton);
            }
            _intro.Children.Add(new DockPanel { Children = { commands.Docked(Dock.Right), title } });
            if (_moreActions is not null) _intro.Children.Add(_moreActions);
        }
    }

    private static void WelcomeCommand(Button button, string caption, string icon, bool secondary = false)
    {
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Padding = new Thickness(secondary ? 14 : 20, 10);
        button.CornerRadius = new CornerRadius(8);
        var label = new TextBlock { Name = button.Name + "Caption", Text = caption,
            FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        button.Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing = secondary ? 10 : 14,
            Children = { MapIcons.Stroked(icon, secondary ? 18 : 22), label },
        };
    }

    private Control BuildWelcome(Button create, Button open, WrapPanel extras)
    {
        WelcomeCommand(create, "Создать карту", MapIcons.NewMap);
        WelcomeCommand(open, "Открыть проект", MapIcons.Open);
        var secondary = new Grid { Name = "DashboardSecondaryActions", ColumnSpacing = 12 };
        foreach (var button in extras.Children.OfType<Button>().ToArray())
        {
            extras.Children.Remove(button);
            var template = button.Name == "CreateFromTemplateButton";
            WelcomeCommand(button, template ? "Из шаблона" : "Импорт копии",
                template ? MapIcons.Duplicate : MapIcons.Import, secondary: true);
            Grid.SetColumn(button, secondary.Children.Count);
            secondary.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            secondary.Children.Add(button);
        }
        secondary.IsVisible = secondary.Children.Count > 0;
        var heading = new TextBlock { Name = "DashboardWelcomeHeading", Text = "Начните с\nчистого листа",
            FontSize = 36, FontWeight = FontWeight.Bold, LineHeight = 42,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var lead = new TextBlock { Name = "DashboardWelcomeLead",
            Text = "Создайте новую карту или продолжите\nработу с сохранённым проектом.",
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
                    new TextBlock { Text = "ВАША МАСТЕРСКАЯ", FontSize = 14,
                        FontWeight = FontWeight.Medium, TextAlignment = TextAlignment.Center,
                        Foreground = Brush("MapAccentBrush") },
                    heading, lead,
                } },
                new StackPanel { Name = "DashboardWelcomeActions", Spacing = 12,
                    Children = { create, open, secondary } },
            },
        };
        _welcomeLayout = new WelcomeLayout(copy, heading, lead);
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
                                new GradientStop(Color.Parse("#FCFDFF"), 0),
                                new GradientStop(Color.Parse("#F8FCFDFF"), .28),
                                new GradientStop(Color.Parse("#00FCFDFF"), .62),
                            },
                        },
                    },
                    copy,
                },
            },
        };
    }

    private void FitWelcomeToViewport()
    {
        if (_welcome is null || _welcomeLayout is not { } layout || !_welcome.IsEffectivelyVisible || !_body.IsMeasureValid
            || !_welcome.IsMeasureValid || _scroll.Viewport.Height <= 0) return;

        var width = _welcome.Bounds.Width;
        if (width <= 0) return;
        var compact = width < 1120 || _scroll.Viewport.Height < 750;
        var large = width >= 1720 && _scroll.Viewport.Height >= 1000;
        layout.Copy.Width = Math.Clamp(width * .36, 360, 720);
        layout.Copy.Margin = new Thickness(Math.Clamp(width * .04, 28, 88), compact ? 18 : 56);
        layout.Copy.Spacing = compact ? 16 : 28;
        layout.Heading.FontSize = compact ? 36 : large ? 60 : 46;
        layout.Heading.LineHeight = compact ? 42 : large ? 66 : 52;
        layout.Lead.FontSize = compact ? 16 : 20;
        layout.Lead.LineHeight = compact ? 24 : 30;
        foreach (var button in _entryButtons)
        {
            var main = button.Name is "NewMapButton" or "OpenFileButton";
            button.MinHeight = main ? compact ? 56 : large ? 76 : 68 : compact ? 48 : 60;
            button.FontSize = main ? compact ? 18 : large ? 24 : 22 : compact ? 16 : 18;
        }
        // Wait for the new typography to be measured before choosing a safe height.
        if (!layout.Copy.IsMeasureValid) return;

        // DesiredSize includes margins. Keep text, commands and disclosures at their measured
        // size, then give the illustration all remaining height, in both resize directions.
        var otherContentHeight = _body.DesiredSize.Height - _welcome.DesiredSize.Height;
        var height = Math.Max(layout.Copy.DesiredSize.Height + 2,
            Math.Floor(_scroll.Viewport.Height - otherContentHeight));
        if (Math.Abs(_welcome.Height - height) > .5) _welcome.Height = height;

        // The readable area belongs to the text column, not to a percentage of a wide monitor.
        if (_welcomeShade is not null && _welcome.Bounds.Width > 0)
        {
            var solidEnd = (layout.Copy.Margin.Left + layout.Copy.Width * .8) / width;
            var fadeEnd = Math.Min(1, (layout.Copy.Margin.Left + layout.Copy.Width + 260) / width);
            if (Math.Abs(_welcomeShade.GradientStops[1].Offset - solidEnd) > .0001)
                _welcomeShade.GradientStops[1].Offset = solidEnd;
            if (Math.Abs(_welcomeShade.GradientStops[2].Offset - fadeEnd) > .0001)
                _welcomeShade.GradientStops[2].Offset = fadeEnd;
        }
    }

    private Control BuildRecentHeader()
    {
        _search = new TextBox { Name = "DashboardSearchBox", Width = 270, VerticalAlignment = VerticalAlignment.Top,
            PlaceholderText = "Поиск по имени или типу", Text = _dashboard.SearchText };
        AutomationProperties.SetName(_search, "Поиск карт по имени или типу");
        _search.TextChanged += (_, _) => _dashboard.SearchText = _search.Text ?? "";
        _sort = new Ui.InlineChoice("DashboardSortChoice", "Порядок карт", SortLabels[(int)_dashboard.SortOrder],
            () => SortLabels.Select((label, index) => (label, (Action)(() =>
                _dashboard.SortOrder = (DashboardSortOrder)index))).ToArray()) { Width = 200 };
        _sort.Opened += (_, _) => CloseMore();
        _sort.Header.MinHeight = 36;
        _recentCount.VerticalAlignment = VerticalAlignment.Top;
        _recentCount.Margin = new Thickness(0, 9, 0, 0);
        return new WrapPanel
        {
            Name = "DashboardLibraryTools", ItemSpacing = 12, LineSpacing = 10,
            Children = { _search, _sort, _recentCount.Classed("muted", "role-caption") },
        };
    }

    private Control BuildHelpLinks()
    {
        var links = new WrapPanel { Name = "DashboardHelpLinks", ItemSpacing = 12, LineSpacing = 6 };
        foreach (var (name, title, text) in new[]
        {
            ("Layers", "Слои и объекты", "Слои помогают разделить фон, землю и объекты. В рабочей панели можно менять порядок, видимость и блокировку. Выберите объект на карте, чтобы настроить его размер, поворот и внешний вид."),
            ("Tools", "Работа с инструментами", "Выберите инструмент слева: объекты, земля, кисть, линии, фигуры, текст, маски или заметки. Параметры текущего инструмента находятся рядом с холстом. Esc отменяет незавершённое действие."),
            ("Save", "Сохранение карты", $"Сохраните карту как файл .mapproj: {_shortcuts.Get(EditorShortcutSettings.Commands.Save)}. Отмена действия: {_shortcuts.Get(EditorShortcutSettings.Commands.Undo)}. Сохранённый проект появится в библиотеке; автосохранённые копии предлагаются здесь для восстановления."),
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
            button.FontSize = 14;
            button.MinHeight = 36;
            links.Children.Add(button);
        }
        _help.Padding = new Thickness(16);
        _help.CornerRadius = new CornerRadius(8);
        _help.Background = Brush("MapPropertyGroupBrush");
        return links;
    }

    private void CloseMore()
    {
        if (_moreActions is not null) _moreActions.IsVisible = false;
        if (_moreButton is not null) Ui.SetSelected(_moreButton, false);
    }

    private void Rebuild()
    {
        var empty = _dashboard.TotalRecentCount == 0;
        if (_empty != empty) { _empty = empty; BuildIntro(empty); }
        _library.IsVisible = !empty;
        foreach (var button in _entryButtons) button.IsEnabled = !_dashboard.IsBusy;
        BuildError(); BuildRecovery();
        _recoveryPanel.IsVisible = _recoveryPanel.Children.Count > 0;
        _sort.SetValue(SortLabels[(int)_dashboard.SortOrder]);
        if (!ReferenceEquals(_renderedCards, _dashboard.RecentCards)) BuildRecent();
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

            var restore = Ui.AsyncButton(
                index == 0 ? "RestoreButton" : $"RestoreButton{index}",
                "Восстановить",
                () => _restoreRequested(captured),
                "primary");
            restore.IsEnabled = !_dashboard.IsBusy;

            var discard = Ui.TextButton(
                index == 0 ? "DiscardButton" : $"DiscardButton{index}",
                "Отбросить",
                () => _dashboard.Discard(captured),
                "destructive");
            discard.Margin = new Thickness(16, 0, 0, 0);
            discard.IsEnabled = !_dashboard.IsBusy;
            ToolTip.SetTip(discard, "Удалить несохранённую копию. Исходный файл не изменится.");

            _recoveryPanel.Children.Add(new Border
            {
                Child = new DockPanel
                {
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { restore, discard },
                        }.Docked(Dock.Right),
                        MapIcons.Stroked(
                                MapIcons.Warning, 20,
                                Application.Current?.FindResource("MapWarnBrush") as IBrush)
                            .Docked(Dock.Left),
                        new StackPanel
                        {
                            Margin = new Thickness(12, 0, 16, 0),
                            Spacing = 3,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock
                                {
                                    Name = $"RecoveryText{index}",
                                    Text = captured.Description,
                                    TextWrapping = TextWrapping.Wrap,
                                }.Classed("role-body"),
                                new TextBlock
                                {
                                    Text = "Работа прервалась до записи файла. " +
                                           "Исходный файл цел — восстановление откроет копию, ничего не перезаписывая.",
                                    TextWrapping = TextWrapping.Wrap,
                                }.Classed("muted", "role-caption"),
                            },
                        },
                    },
                },
            }.Classed("state-card", "attention"));

            index++;
        }
    }

    private void BuildRecent()
    {
        ClosePreview(); _recentPanel.Children.Clear(); ReleaseThumbnails();
        _renderedCards = _dashboard.RecentCards;
        for (var i = 0; i < _renderedCards.Count; i++)
            _recentPanel.Children.Add(BuildRecentCard(_renderedCards[i], i));
        var count = _renderedCards.Count;
        _recentCount.Text = string.IsNullOrWhiteSpace(_dashboard.SearchText)
            ? $"{count} {EditorLabels.Plural(count, "карта", "карты", "карт")}" : $"Найдено {count} из {_dashboard.TotalRecentCount}";
        _noResults.IsVisible = _dashboard.TotalRecentCount > 0 && count == 0;
        _recentPanel.IsVisible = count > 0;
        ResizeCards();
    }

    private void ResizeCards()
    {
        var width = _recentPanel.Bounds.Width;
        if (width <= 0) width = Math.Max(280, Bounds.Width - 72);
        var columns = Math.Max(1, (int)((width + 16) / 296));
        var cardWidth = Math.Floor((width - (columns - 1) * 16) / columns);
        foreach (var card in _recentPanel.Children) card.Width = cardWidth;
    }

    private void ClosePreview() { _preview.IsVisible = false; _preview.Child = null; }

    private void ShowPreview(RecentCard card)
    {
        CloseMore(); _sort.Close();
        var open = Ui.AsyncButton("OpenDashboardPreviewButton", "Открыть карту", () => _openRecentRequested(card), "primary");
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
        var thumbnail = BuildThumbnail(card);
        thumbnail.Height = 270;
        if (thumbnail is Border { Child: Image image }) image.Stretch = Stretch.Uniform;
        _preview.Child = new StackPanel
        {
            Spacing = 12, Children =
            {
                heading, thumbnail,
                new TextBlock { Text = card.Subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap }.Classed("muted"),
                new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = open },
            },
        };
        _preview.Classes.Add("state-card");
        _preview.IsVisible = true;
        Dispatcher.UIThread.Post(() => _preview.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void ReleaseThumbnails()
    {
        foreach (var bitmap in _thumbnails.Values) bitmap.Dispose();
        _thumbnails.Clear();
    }

    private Control BuildRecentCard(RecentCard card, int index)
    {
        var thumbnail = BuildThumbnail(card);

        var title = new TextBlock { Text = card.Title, TextWrapping = TextWrapping.Wrap }.Named($"RecentTitle{index}");
        title.FontSize = 16;
        title.FontWeight = FontWeight.Medium;

        var subtitle = new TextBlock { Text = card.Subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap }.Named($"RecentSubtitle{index}");
        if (!card.IsAvailable)
        {
            subtitle.Classes.Add("danger");
        }
        else
        {
            subtitle.Classes.Add("muted");
        }

        var footer = new StackPanel
        {
            Margin = new Thickness(16, 10, 16, 13),
            Spacing = 5,
            Children = { title, subtitle },
        };

        var openButton = new Button
        {
            Name = $"RecentCard{index}",
            IsEnabled = card.IsAvailable && !_dashboard.IsBusy,
            Tag = card.IsAvailable,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Content = new StackPanel { Children = { thumbnail, footer } },
        }.Classed("map-card-main");

        var accessibleName = card.IsAvailable
            ? $"{card.Title}. Изменена {card.Subtitle}"
            : $"{card.Title}. Файл не найден";
        AutomationProperties.SetName(openButton, accessibleName);
        ToolTip.SetTip(openButton, card.IsAvailable ? $"{accessibleName}\n{card.Path}" : $"{accessibleName}\n{card.Path}");

        var captured = card;
        openButton.Click += async (_, _) => await _openRecentRequested(captured);

        if (_cloneRequested is null && _deleteRequested is null)
        {
            return new Border
            {
                Width = 305,
                Child = openButton,
            }.Classed("map-card-shell");
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(12, 0, 12, 10),
        };
        var preview = Ui.TextButton($"PreviewRecentButton{index}", "Просмотр", () => ShowPreview(captured), "map-card-action");
        preview.Tag = card.IsAvailable;
        preview.IsVisible = card.IsAvailable;
        preview.IsEnabled = card.IsAvailable && !_dashboard.IsBusy;
        actions.Children.Add(preview);
        if (_cloneRequested is not null)
        {
            var clone = Ui.AsyncCommandButton(
                $"CloneRecentButton{index}", "Копия", MapIcons.Duplicate,
                () => _cloneRequested(captured), "map-card-action");
            // A vanished file cannot be copied; only its entry can still be removed.
            clone.Tag = card.IsAvailable;
            clone.IsVisible = card.IsAvailable;
            clone.IsEnabled = card.IsAvailable && !_dashboard.IsBusy;
            actions.Children.Add(clone);
        }

        if (_deleteRequested is not null)
        {
            // The label states what the button will actually do, so a stale card never promises a
            // Recycle Bin move for a file that is no longer there.
            var delete = Ui.AsyncCommandButton(
                $"DeleteRecentButton{index}",
                card.IsAvailable ? "Удалить" : "Убрать запись",
                MapIcons.Trash,
                () => _deleteRequested(captured),
                "destructive",
                "map-card-action");
            delete.IsEnabled = !_dashboard.IsBusy;
            AutomationProperties.SetName(delete, card.IsAvailable
                ? $"Переместить карту «{card.Title}» в Корзину"
                : $"Убрать запись о недоступной карте «{card.Title}»");
            actions.Children.Add(delete);
        }
        foreach (var action in actions.Children.OfType<Button>())
        {
            action.FontSize = 12;
            action.Padding = new Thickness(5, 4);
            action.MinHeight = 30;
            // Command captions have their own typography; keep the compact card actions together.
            if (action.Content is Control content)
            {
                if (content is TextBlock caption) caption.FontSize = 12;
                foreach (var text in content.GetLogicalDescendants().OfType<TextBlock>()) text.FontSize = 12;
            }
        }
        return new Border
        {
            Width = 305,
            Child = new DockPanel { Children = { actions.Docked(Dock.Bottom), openButton } },
        }.Classed("map-card-shell");
    }

    private Control BuildThumbnail(RecentCard card)
    {
        if (!card.IsAvailable)
        {
            return Placeholder(MapIcons.Missing, "Файл не найден", "MapDangerBrush");
        }

        // Even a small, flat preview is real project content; byte size is not an image-quality test.
        if (card.ThumbnailPng is { Length: > 0 } bytes)
        {
            try
            {
                if (!_thumbnails.TryGetValue(card.Path, out var bitmap))
                {
                    using var stream = new MemoryStream(bytes);
                    bitmap = new Bitmap(stream);
                    _thumbnails[card.Path] = bitmap;
                }
                return new Border
                {
                    Height = ThumbnailHeight,
                    CornerRadius = new CornerRadius(9, 9, 0, 0),
                    ClipToBounds = true,
                    Child = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.UniformToFill,
                    },
                };
            }
            catch (Exception)
            {
                return ApprovedMapThumbnail();
            }
        }

        return ApprovedMapThumbnail();
    }

    private static Control ApprovedMapThumbnail() => new Border
    {
        Height = ThumbnailHeight,
        Padding = new Thickness(8, 6),
        CornerRadius = new CornerRadius(9, 9, 0, 0),
        ClipToBounds = true,
        Background = Application.Current?.FindResource("MapElevatedBrush") as IBrush,
        Child = ApprovedArtwork.Image(
            "map", 112, 112),
    };

    private static Control Placeholder(string icon, string caption, string brushKey)
    {
        var brush = Application.Current?.FindResource(brushKey) as IBrush;
        return new Border
        {
            Height = ThumbnailHeight,
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Background = Application.Current?.FindResource("MapSurfaceBrush") as IBrush,
            Child = new StackPanel
            {
                Spacing = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Child = MapIcons.Stroked(icon, 22, brush),
                    },
                    new TextBlock
                    {
                        Text = caption,
                        FontSize = 12,
                        Foreground = brush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };
    }
}
