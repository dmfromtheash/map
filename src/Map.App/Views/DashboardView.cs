using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Map.App.Controls;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The local library. One screen, one obvious way in: create a map or open one. Recent projects
/// are a responsive grid of real thumbnails; an interrupted save is offered back before anything
/// else on the page. Built in code so every control carries an explicit UIA name.
/// </summary>
public sealed class DashboardView : UserControl
{
    private const double CardWidth = 360;
    private const double ThumbnailHeight = 190;

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

    private readonly StackPanel _errorPanel = new();
    private readonly StackPanel _recoveryPanel = new() { Spacing = 10 };
    private readonly WrapPanel _recentPanel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _recentCount = new() { Name = "RecentCountText" };
    private Button? _openButton;
    private Button? _newButton;

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

        Content = Ui.WatercolorBackdrop(new DockPanel
        {
            Children =
            {
                BuildHeader().Docked(Dock.Top),
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(28, 22, 28, 40),
                        Spacing = 18,
                        Children =
                        {
                            BuildActionBar(),
                            _errorPanel,
                            _recoveryPanel,
                            BuildRecentHeader(),
                            _recentPanel,
                        },
                    },
                },
            },
        });

        _dashboard.PropertyChanged += (_, _) =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Rebuild();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
            }
        };
        Rebuild();
    }

    public void Refresh()
    {
        _dashboard.Refresh();
        Rebuild();
    }

    // ================================================================== chrome

    private Control BuildHeader()
    {
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                MapIcons.Stroked(
                    MapIcons.LibraryMaps, 30, Application.Current?.FindResource("MapAccentBrush") as IBrush),
                new TextBlock
                {
                    Text = "Map",
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Библиотека карт",
                    VerticalAlignment = VerticalAlignment.Center,
                }.Classed("muted"),
            },
        };

        var offline = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Работает без интернета", FontSize = 11 }.Classed("muted"),
        }.Classed("badge");

        var chromeActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { offline },
        };
        if (_helpRequested is not null)
            chromeActions.Children.Add(Ui.CommandButton(
                "LocalHelpButton", "Справка", MapIcons.Help, _helpRequested, "compact"));
        if (_settingsRequested is not null)
            chromeActions.Children.Add(Ui.CommandButton(
                "AppSettingsButton", "Настройки", MapIcons.Gear, _settingsRequested, "compact"));

        return new Border
        {
            Child = new DockPanel
            {
                Margin = new Thickness(28, 12),
                Children = { chromeActions.Docked(Dock.Right), brand },
            },
        }.Classed("app-bar");
    }

    private Control BuildActionBar()
    {
        var newButton = Ui.AsyncCommandButton(
            "NewMapButton", "Новая карта", MapIcons.NewMap, _newMapRequested,
            "primary", "dashboard-new-map");
        _newButton = newButton;
        ToolTip.SetTip(newButton,
            $"Создать новую карту  ({_shortcuts.Get(EditorShortcutSettings.Commands.NewMap)})");

        var openButton = Ui.AsyncCommandButton(
            "OpenFileButton", "Открыть проект", MapIcons.Open, _openFileRequested);
        _openButton = openButton;
        openButton.MinHeight = 36;
        openButton.Padding = new Thickness(16, 6);
        ToolTip.SetTip(openButton,
            $"Открыть файл проекта .mapproj  ({_shortcuts.Get(EditorShortcutSettings.Commands.OpenMap)})");

        var actionButtons = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { openButton, newButton },
        };
        if (_templateRequested is not null)
            actionButtons.Children.Add(Ui.AsyncCommandButton(
                "CreateFromTemplateButton", "Из шаблона", MapIcons.ObjectComposition, _templateRequested));
        if (_backupRequested is not null)
            actionButtons.Children.Add(Ui.AsyncCommandButton(
                "ImportBackupButton", "Импорт backup", MapIcons.Import, _backupRequested));

        return new Border
        {
            Padding = new Thickness(26, 22),
            Child = new DockPanel
            {
                Children =
                {
                    actionButtons.Docked(Dock.Right),
                    new StackPanel
                    {
                        Spacing = 5,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Name = "DashboardTitle",
                                Text = "Ваши карты",
                                FontFamily = "Georgia",
                                FontSize = 28,
                            },
                            new TextBlock
                            {
                                Text = "Проекты хранятся только на этом компьютере.",
                                TextWrapping = TextWrapping.Wrap,
                            }.Classed("muted"),
                        },
                    },
                },
            },
        }.Classed("elevated");
    }

    private Control BuildRecentHeader()
    {
        var search = new TextBox
        {
            Name = "DashboardSearchBox",
            Width = 220,
            PlaceholderText = "Поиск по имени или типу",
            Text = _dashboard.SearchText,
        };
        search.TextChanged += (_, _) => _dashboard.SearchText = search.Text ?? "";

        var sort = new ComboBox
        {
            Name = "DashboardSortChoice",
            Width = 190,
            SelectedIndex = (int)_dashboard.SortOrder,
            ItemsSource = new[] { "Сначала новые", "По имени", "По типу", "По размеру" },
        };
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedIndex >= 0)
                _dashboard.SortOrder = (DashboardSortOrder)sort.SelectedIndex;
        };

        return new DockPanel
        {
            Margin = new Thickness(2, 6, 2, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                MapIcons.Stroked(
                                    MapIcons.Search, 16,
                                    Application.Current?.FindResource("MapMutedBrush") as IBrush),
                                search,
                            },
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                MapIcons.Stroked(
                                    MapIcons.Sort, 16,
                                    Application.Current?.FindResource("MapMutedBrush") as IBrush),
                                sort,
                            },
                        },
                        _recentCount.Classed("muted", "role-caption"),
                    },
                }.Docked(Dock.Right),
                new TextBlock
                {
                    Name = "RecentHeader",
                    Text = "Недавние карты",
                    FontFamily = "Georgia",
                    FontSize = 24,
                },
            },
        };
    }

    // ================================================================== content

    private void Rebuild()
    {
        if (_openButton is not null) _openButton.IsEnabled = !_dashboard.IsBusy;
        if (_newButton is not null) _newButton.IsEnabled = !_dashboard.IsBusy;
        BuildError();
        BuildRecovery();
        BuildRecent();
    }

    private void BuildError()
    {
        _errorPanel.Children.Clear();

        // A successful outcome that is not what the button literally said - "the file was already
        // gone, so only the entry was removed" - has to be visible, and it is not an error.
        if (!string.IsNullOrWhiteSpace(_dashboard.LastNotice))
        {
            _errorPanel.Children.Add(new Border
            {
                Child = new TextBlock
                {
                    Name = "DashboardNoticeText",
                    Text = _dashboard.LastNotice,
                    TextWrapping = TextWrapping.Wrap,
                },
            }.Classed("state-card"));
        }

        if (string.IsNullOrWhiteSpace(_dashboard.LastError))
        {
            return;
        }

        _errorPanel.Children.Add(new Border
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    MapIcons.Stroked(
                        MapIcons.Warning, 20, Application.Current?.FindResource("MapDangerBrush") as IBrush),
                    new TextBlock
                    {
                        Name = "DashboardErrorText",
                        Text = _dashboard.LastError,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        }.Classed("state-card", "error"));
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
        _recentPanel.Children.Clear();
        var index = 0;

        foreach (var card in _dashboard.RecentCards)
        {
            _recentPanel.Children.Add(BuildRecentCard(card, index));
            index++;
        }

        _recentPanel.Children.Add(BuildNewCard());
        _recentCount.Text = index == 0
            ? "Пока пусто"
            : $"{index} {EditorLabels.Plural(index, "карта", "карты", "карт")}";

        if (index == 0)
        {
            _recentPanel.Children.Insert(0, new Border
            {
                Width = CardWidth * 2 + 16,
                Margin = new Thickness(0, 0, 16, 16),
                Child = Ui.EmptyState(
                    "NoRecentText",
                    MapIcons.Layers,
                    "Здесь появятся ваши карты",
                    "Создайте первую карту или откройте существующий файл .mapproj."),
            }.Classed("state-card"));
        }
    }

    private Control BuildNewCard()
    {
        var button = new Button
        {
            Name = "NewMapCardButton",
            Width = CardWidth,
            Height = ThumbnailHeight + 100,
            Margin = new Thickness(0, 0, 16, 16),
            IsEnabled = !_dashboard.IsBusy,
            Content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    ApprovedArtwork.Image("dashboard-new-map-selected", 150, 126),
                    new TextBlock
                    {
                        Text = "Создать новую карту",
                        FontFamily = "Georgia",
                        FontSize = 17,
                        Foreground = Application.Current?.FindResource("MapTextBrush") as IBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        }.Classed("new-card");

        AutomationProperties.SetName(button, "Новая карта");
        ToolTip.SetTip(button, "Создать новую карту");
        button.Click += async (_, _) => await _newMapRequested();
        return button;
    }

    private Control BuildRecentCard(RecentCard card, int index)
    {
        var thumbnail = BuildThumbnail(card);

        var title = Ui.Trimmed(card.Title).Named($"RecentTitle{index}");
        title.FontFamily = "Georgia";
        title.FontSize = 17;

        var subtitle = Ui.Trimmed(card.Subtitle, "role-caption").Named($"RecentSubtitle{index}");
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
                Width = CardWidth,
                Margin = new Thickness(0, 0, 16, 16),
                Child = openButton,
            }.Classed("map-card-shell");
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12, 0, 12, 10),
        };
        if (_cloneRequested is not null)
        {
            var clone = Ui.AsyncCommandButton(
                $"CloneRecentButton{index}", "Копировать", MapIcons.Duplicate,
                () => _cloneRequested(captured), "map-card-action");
            // A vanished file cannot be copied; only its entry can still be removed.
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
        return new Border
        {
            Width = CardWidth,
            Margin = new Thickness(0, 0, 16, 16),
            Child = new StackPanel { Children = { openButton, actions } },
        }.Classed("map-card-shell");
    }

    private static Control BuildThumbnail(RecentCard card)
    {
        if (!card.IsAvailable)
        {
            return Placeholder(MapIcons.Missing, "Файл не найден", "MapDangerBrush");
        }

        // The renderer's tiny uniform placeholder contains no useful map information. In that
        // case the approved folded-map artwork is more honest and much more legible; real rendered
        // thumbnails remain the first choice once they contain actual scene detail.
        if (card.ThumbnailPng is { Length: > 4096 } bytes)
        {
            try
            {
                return new Border
                {
                    Height = ThumbnailHeight,
                    CornerRadius = new CornerRadius(9, 9, 0, 0),
                    ClipToBounds = true,
                    Child = new Image
                    {
                        Source = new Bitmap(new MemoryStream(bytes)),
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
            "dashboard-map-thumbnail", CardWidth - 16, ThumbnailHeight - 12, Stretch.UniformToFill),
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
