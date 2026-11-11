using System;
using System.Collections.Generic;
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

namespace Map.App.Views;

/// <summary>The handout choice of the new-map dialog (R09f): picture cards of the five sheets and «Без хендаута».</summary>
public sealed partial class WizardWindow
{
    private readonly List<(ToggleButton Button, MapHandout Handout)> _handoutButtons = [];

    private Control BuildHandoutBlock()
    {
        var grid = new UniformGrid { Columns = 3 };
        foreach (var handout in MapHandouts.All)
        {
            var picture = handout == MapHandout.None
                ? (Control)new Border
                {
                    Width = 152, Height = 96, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                    BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
                    Child = new TextBlock
                    {
                        Text = "Карта как есть", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    }.Classed("faint"),
                }
                : HandoutPicture(handout);
            var button = new ToggleButton
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(4),
                Padding = new Thickness(6),
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        picture,
                        new TextBlock { Text = MapHandouts.Name(handout), FontSize = 13, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            }.Named("WizardHandout" + MapHandouts.Id(handout).Replace("-", "", StringComparison.Ordinal)).Classed("background-card");
            AutomationProperties.SetName(button, "Хендаут: " + MapHandouts.Name(handout));
            var captured = handout;
            button.Click += (_, _) =>
            {
                _wizard.Handout = captured;
                SyncBackgroundButtons();
            };
            _handoutButtons.Add((button, handout));
            grid.Children.Add(button);
        }

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Хендаут" }.Classed("role-section"),
                new TextBlock
                {
                    Text = "Лист для игроков: карта на старом листе, лежащем на столе. Сохраняется на «Экспорте» кнопкой " +
                        "«Сохранить хендаут…». Холст остаётся как нарисован. С хендаутом фон становится цвета бумаги.",
                    TextWrapping = TextWrapping.Wrap,
                }.Classed("faint", "role-caption"),
                grid,
            },
        };
    }

    private void SyncHandoutButtons()
    {
        foreach (var (button, handout) in _handoutButtons)
        {
            var active = handout == _wizard.Handout;
            Ui.SetClass(button, "active", active);
            button.IsChecked = active;
        }
    }

    /// <summary>The sheet's picture on the sample map with the owner's mountains, made by the program's own handout export.</summary>
    private static Control HandoutPicture(MapHandout handout)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Map.App/Assets/Handouts/" + MapHandouts.Id(handout) + ".jpg"));
            var image = new Image { Source = new Bitmap(stream), Height = 96, Stretch = Stretch.Uniform, IsHitTestVisible = false };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            return image;
        }
        catch (Exception)
        {
            // A missing picture is a cosmetic gap; the card still names the sheet.
            return new Border { Height = 96 };
        }
    }
}
