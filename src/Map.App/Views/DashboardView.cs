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
    private readonly WrapPanel _recentPanel = new() { };

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

        var newButton = new Button { Name = "NewMapButton", Content = "Новая карта" };
        newButton.Click += async (_, _) => await _newMapRequested();

        var openButton = new Button { Name = "OpenFileButton", Content = "Открыть файл" };
        openButton.Click += async (_, _) => await _openFileRequested();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(28),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Name = "DashboardTitle",
                        Text = "Библиотека карт",
                        FontSize = 24,
                        FontWeight = FontWeight.SemiBold,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children = { newButton, openButton },
                    },
                    _error,
                    _recoveryPanel,
                    new TextBlock
                    {
                        Name = "RecentHeader",
                        Text = "Недавние карты",
                        FontSize = 16,
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(0, 12, 0, 0),
                    },
                    _recentPanel,
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
            var restore = new Button
            {
                Name = recoveryIndex == 0 ? "RestoreButton" : $"RestoreButton{recoveryIndex}",
                Content = "Восстановить",
            };
            var captured = card;
            restore.Click += async (_, _) => await _restoreRequested(captured);

            var discard = new Button
            {
                Name = recoveryIndex == 0 ? "DiscardButton" : $"DiscardButton{recoveryIndex}",
                Content = "Отбросить",
            };
            discard.Click += (_, _) => _dashboard.Discard(captured);

            _recoveryPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x33, 0x22)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10),
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
            });

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
            _recentPanel.Children.Add(new TextBlock
            {
                Name = "NoRecentText",
                Text = "Пока нет недавних карт. Создайте первую.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9E)),
            });
        }
    }

    private Control BuildRecentCard(RecentCard card, int index)
    {
        Control thumbnail;
        if (card.ThumbnailPng is { Length: > 0 } bytes)
        {
            try
            {
                thumbnail = new Image
                {
                    Source = new Bitmap(new MemoryStream(bytes)),
                    Width = 200,
                    Height = 112,
                    Stretch = Stretch.Uniform,
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

        var button = new Button
        {
            Name = $"RecentCard{index}",
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(10),
            IsEnabled = card.IsAvailable,
            Content = new StackPanel
            {
                Spacing = 6,
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
                        Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9E)),
                        FontSize = 12,
                    },
                },
            },
        };

        var captured = card;
        button.Click += async (_, _) => await _openRecentRequested(captured);
        return button;
    }

    private static Control ThumbnailPlaceholder() => new Border
    {
        Width = 200,
        Height = 112,
        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x38)),
        Child = new TextBlock
        {
            Text = "нет миниатюры",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x74, 0x80)),
            FontSize = 12,
        },
    };
}
