using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The local dashboard: new map, open file, recent cards with real thumbnails and recovery offers.
/// Built in code so every control carries an explicit UIA name.
/// </summary>
public sealed class DashboardView : UserControl
{
    private readonly DashboardViewModel _dashboard;
    private readonly Func<Task> _newMapRequested;
    private readonly Func<Task> _openFileRequested;
    private readonly Func<RecentCard, Task> _openRecentRequested;
    private readonly Func<RecoveryCard, Task> _restoreRequested;

    private readonly TextBlock _error = new()
    {
        Name = "DashboardErrorText",
        Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75)),
        TextWrapping = TextWrapping.Wrap,
        Text = "",
    };

    private readonly StackPanel _recoveryPanel = new() { Spacing = 8 };
    private readonly WrapPanel _recentPanel = new() { Orientation = Orientation.Horizontal };

    public DashboardView(
        DashboardViewModel dashboard,
        Func<Task> newMapRequested,
        Func<Task> openFileRequested,
        Func<RecentCard, Task> openRecentRequested,
        Func<RecoveryCard, Task> restoreRequested)
    {
        _dashboard = dashboard;
        _newMapRequested = newMapRequested;
        _openFileRequested = openFileRequested;
        _openRecentRequested = openRecentRequested;
        _restoreRequested = restoreRequested;
        Name = "DashboardView";

        var newButton = Styled(new Button { Name = "NewMapButton", Content = "+ Новая карта" }, "primary");
        newButton.Click += async (_, _) => await _newMapRequested();

        var openButton = new Button { Name = "OpenFileButton", Content = "Открыть проект" };
        openButton.Click += async (_, _) => await _openFileRequested();

        var header = Styled(new Border
        {
            Padding = new Thickness(28, 10),
            Child = new DockPanel
            {
                Children =
                {
                    WithDock(Styled(new TextBlock
                    {
                        Text = "ОФЛАЙН",
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    }, "muted"), Dock.Right),
                    new TextBlock
                    {
                        Text = "MAP",
                        FontSize = 16,
                        FontWeight = FontWeight.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    Styled(new TextBlock
                    {
                        Text = "Локальная библиотека",
                        Margin = new Thickness(16, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    }, "muted"),
                },
            },
        }, "top-bar");

        var hero = Styled(new Border
        {
            Child = new DockPanel
            {
                Children =
                {
                    WithDock(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { openButton, newButton },
                    }, Dock.Right),
                    new StackPanel
                    {
                        Spacing = 5,
                        Children =
                        {
                            new TextBlock
                            {
                                Name = "DashboardTitle",
                                Text = "Ваши карты",
                                FontSize = 24,
                                FontWeight = FontWeight.SemiBold,
                            },
                            Styled(new TextBlock
                            {
                                Text = "Создавайте, открывайте и восстанавливайте проекты без облака.",
                                TextWrapping = TextWrapping.Wrap,
                            }, "muted"),
                        },
                    },
                },
            },
        }, "hero-card");

        Content = new DockPanel
        {
            Children =
            {
                WithDock(header, Dock.Top),
                new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Thickness(28, 24, 28, 40),
                        Spacing = 18,
                        Children =
                        {
                            hero,
                            _error,
                            _recoveryPanel,
                            new DockPanel
                            {
                                Margin = new Thickness(0, 10, 0, 0),
                                Children =
                                {
                                    WithDock(Styled(new TextBlock
                                    {
                                        Text = "Проекты хранятся только на этом компьютере",
                                        VerticalAlignment = VerticalAlignment.Center,
                                    }, "muted"), Dock.Right),
                                    new TextBlock
                                    {
                                        Name = "RecentHeader",
                                        Text = "Недавние карты",
                                        Classes = { "section-title" },
                                    },
                                },
                            },
                            _recentPanel,
                        },
                    },
                },
            },
        };

        _dashboard.PropertyChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    public void Refresh()
    {
        _dashboard.Refresh();
        Rebuild();
    }

    private void Rebuild()
    {
        _error.Text = _dashboard.LastError;

        _recoveryPanel.Children.Clear();
        var recoveryIndex = 0;
        foreach (var card in _dashboard.RecoveryCards)
        {
            var restore = Styled(new Button
            {
                Name = recoveryIndex == 0 ? "RestoreButton" : $"RestoreButton{recoveryIndex}",
                Content = "Восстановить",
            }, "primary");
            var captured = card;
            restore.Click += async (_, _) => await _restoreRequested(captured);

            var discard = Styled(new Button
            {
                Name = recoveryIndex == 0 ? "DiscardButton" : $"DiscardButton{recoveryIndex}",
                Content = "Отбросить",
            }, "danger");
            discard.Click += (_, _) => _dashboard.Discard(captured);

            _recoveryPanel.Children.Add(Styled(new Border
            {
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Name = $"RecoveryText{recoveryIndex}",
                            Text = card.Description,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        restore,
                        discard,
                    },
                },
            }, "recovery-card"));

            recoveryIndex++;
        }

        _recentPanel.Children.Clear();
        var recentIndex = 0;
        foreach (var card in _dashboard.RecentCards)
        {
            _recentPanel.Children.Add(BuildRecentCard(card, recentIndex));
            recentIndex++;
        }

        if (recentIndex == 0)
        {
            _recentPanel.Children.Add(Styled(new TextBlock
            {
                Name = "NoRecentText",
                Text = "Пока нет недавних карт. Создайте первую.",
                Margin = new Thickness(4, 12),
            }, "muted"));
        }
    }

    private Control BuildRecentCard(RecentCard card, int index)
    {
        Control thumbnail;
        if (card.ThumbnailPng is { Length: > 0 } bytes)
        {
            try
            {
                thumbnail = new Border
                {
                    Width = 216,
                    Height = 121,
                    CornerRadius = new CornerRadius(8),
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
                thumbnail = ThumbnailPlaceholder();
            }
        }
        else
        {
            thumbnail = ThumbnailPlaceholder();
        }

        var button = Styled(new Button
        {
            Name = $"RecentCard{index}",
            Margin = new Thickness(0, 0, 14, 14),
            IsEnabled = card.IsAvailable,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    thumbnail,
                    new TextBlock
                    {
                        Name = $"RecentTitle{index}",
                        Text = card.Title,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Name = $"RecentSubtitle{index}",
                        Text = card.Subtitle,
                        FontSize = 12,
                        Classes = { "muted" },
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        }, "map-card");

        var captured = card;
        button.Click += async (_, _) => await _openRecentRequested(captured);
        return button;
    }

    private static Control ThumbnailPlaceholder() => new Border
    {
        Width = 216,
        Height = 121,
        CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x32)),
        Child = Styled(new TextBlock
        {
            Text = "Нет миниатюры",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        }, "muted"),
    };

    private static T Styled<T>(T control, params string[] classes) where T : StyledElement
    {
        foreach (var className in classes)
        {
            control.Classes.Add(className);
        }

        return control;
    }

    private static Control WithDock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
