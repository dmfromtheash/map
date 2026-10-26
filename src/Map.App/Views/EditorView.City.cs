using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Map.Core.Editing;

namespace Map.App.Views;

/// <summary>
/// R34/R35: «Город» - the settings of the generated town and «Перестроить город» in a city map;
/// on any saved map «Город из точки…»: a click makes a child city map in the atlas.
/// </summary>
public sealed partial class EditorView
{
    private Window? _cityWindow;
    private Control? _cityContent;

    private void ShowCity()
    {
        _cityWindow = ShowAuxiliaryWindow(_cityWindow, "Город", BuildCityContent(), 600, 600, "CityWindow");
    }

    private Control BuildCityContent()
    {
        if (_cityContent is not null) return _cityContent;
        static int Number(TextBox box, int fallback, int min, int max) =>
            int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;
        var s = _editor.CitySettings;
        var name = new TextBox { Name = "CityNameBox", Text = s.Name };
        var seed = new TextBox { Name = "CitySeedBox", Width = 80, Text = s.Seed.ToString(CultureInfo.InvariantCulture) };
        var radials = new TextBox { Name = "CityRadialsBox", Width = 50, Text = s.Radials.ToString(CultureInfo.InvariantCulture) };
        var rings = new TextBox { Name = "CityRingsBox", Width = 50, Text = s.Rings.ToString(CultureInfo.InvariantCulture) };
        var temples = new TextBox { Name = "CityTemplesBox", Width = 50, Text = s.Temples.ToString(CultureInfo.InvariantCulture) };
        var walls = Ui.PropertyToggle("CityWallsToggle", "Стена с башнями и воротами"); walls.IsChecked = s.Walls;
        var port = Ui.PropertyToggle("CityPortToggle", "Порт: гавань и пирсы"); port.IsChecked = s.Port;
        var guild = Ui.PropertyToggle("CityGuildToggle", "Гильдия"); guild.IsChecked = s.Guild;
        CitySettings Read() => _editor.CitySettings with
        {
            Name = string.IsNullOrWhiteSpace(name.Text) ? "Город" : name.Text.Trim(),
            Seed = Number(seed, 1, 0, 1_000_000), Radials = Number(radials, 6, 3, 12), Rings = Number(rings, 3, 1, 6),
            Temples = Number(temples, 2, 0, 6), Walls = walls.IsChecked == true, Port = port.IsChecked == true, Guild = guild.IsChecked == true,
        };
        var regenerate = Ui.TextButton("CityRegenerateButton", "Перестроить город", () => { _editor.CitySettings = Read(); _editor.RegenerateCity(); }, "accent", "compact");
        ToolTip.SetTip(regenerate, "Слой «Город» и ключ зданий строятся заново; ваши слои остаются. Одна отмена.");
        var another = Ui.TextButton("CityReseedButton", "Другой вариант", () =>
        {
            seed.Text = ((Number(seed, 1, 0, 1_000_000) * 7919 + 13) % 1_000_000).ToString(CultureInfo.InvariantCulture);
            _editor.CitySettings = Read(); _editor.RegenerateCity();
        }, "compact");
        var fromPoint = Ui.TextButton("CityFromPointButton", "Город из точки…", () => { _editor.CitySettings = Read(); _editor.StartCityFromPoint(); }, "compact");
        ToolTip.SetTip(fromPoint, "Щёлкните место на карте: появится подкарта-город в атласе, главные улицы — вдоль дорог региона, реки и дороги перенесутся.");
        var cityOnly = new StackPanel { Spacing = 6, Children = { new Ui.ChoiceGrid { MaxColumns = 2, Children = { regenerate, another } } } };
        cityOnly.IsVisible = _editor.IsCityWorkshop;
        _cityContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children =
                {
                    Ui.Hint("Город строится от площади: главные улицы, кольцевые, кварталы, участки, дома лицом к улице. Дома пока простыми крышами — до арта домов."),
                    name,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = "Вариант", VerticalAlignment = VerticalAlignment.Center }, seed,
                        new TextBlock { Text = "Улиц", VerticalAlignment = VerticalAlignment.Center }, radials,
                        new TextBlock { Text = "Колец", VerticalAlignment = VerticalAlignment.Center }, rings,
                        new TextBlock { Text = "Храмов", VerticalAlignment = VerticalAlignment.Center }, temples,
                    } },
                    walls, port, guild,
                    cityOnly,
                    Ui.Subgroup("Город на карте мира", new StackPanel { Spacing = 6, Children =
                    {
                        new TextBlock { Text = "Город из точки создаёт подкарту в атласе этой карты.", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
                        fromPoint,
                    } }),
                },
            },
        };
        return _cityContent;
    }
}
