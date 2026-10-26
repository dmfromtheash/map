using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// Море in the ribbon under the map (R12): a card per sea style drawn on the open map, and beside
/// them how strongly the style covers the plain sea and how far from the shore it reaches.
/// </summary>
public sealed partial class EditorView
{
    private Control? _seaBody;
    private readonly List<Action> _seaSyncs = [];

    private static IReadOnlyList<(string Key, string Title, SeaManner? Manner)> SeaCards { get; } =
        [("sea-none", "Без стиля", null), .. SeaStyle.Manners.Select(m => ("sea-" + (int)m.Manner, m.Title, (SeaManner?)m.Manner))];

    private Control SeaBody() => _seaBody ??= BuildSeaBody();

    private Control BuildSeaBody()
    {
        var hint = Ui.Hint("Море рисуется по расстоянию до ближайшего берега. Пока стиль моря включён, кольца прибоя у суши не рисуются — их заменяет море.");
        var strength = AppearanceSlider("SeaStrengthSlider", "Сила", 0, 100, "%",
            () => _editor.Sea?.Strength * 100, (v, _) => _editor.SetSeaStrength(v), 5,
            defaultValue: 100, description: "Насколько стиль моря закрывает простой цвет фона. 0 — как без стиля.");
        var reach = AppearanceSlider("SeaScaleSlider", "Размах", SeaStyle.MinimumScale * 100, SeaStyle.MaximumScale * 100, "%",
            () => _editor.Sea?.Scale * 100, (v, _) => _editor.SetSeaScale(v), 5,
            defaultValue: 100, description: "Как далеко от берега уходят линии и полосы: меньше — для маленьких островов, больше — для материка.");
        var off = new TextBlock { Text = "Выберите стиль моря слева.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }.Classed("faint");
        _seaSyncs.Add(() =>
        {
            var on = _editor.Sea is not null;
            strength.IsVisible = on; reach.IsVisible = on; off.IsVisible = !on;
        });
        return new ScrollViewer
        {
            Content = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(10, 0, 4, 0), Children = { hint, off, strength, reach } },
        }.Named("SeaBody");
    }

    private static string SeaState(SeaStyle? sea) => sea is null ? "без стиля" : EditorViewModel.SeaTitle(sea.Manner);

    private static string SeaHint(SeaManner manner) => SeaStyle.Manners.First(m => m.Manner == manner).Description;

    private bool SeaCardActive(string key) => key == "sea-none"
        ? _editor.Sea is null
        : _editor.Sea is { } sea && "sea-" + (int)sea.Manner == key;

    private void ApplySeaCard(string key)
    {
        if (key == "sea-none") { _editor.ClearSea(); return; }
        _editor.ApplySeaManner((SeaManner)int.Parse(key["sea-".Length..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The open map with this card's sea: the current one when it is that manner, else the manner's ready look.</summary>
    private static MapDocument SeaCardDocument(MapDocument document, string key)
    {
        if (document.Layers.Count == 0 || document.Layers[0] is not BaseColorLayer page) return document;
        SeaStyle? sea = null;
        if (key != "sea-none")
        {
            var manner = (SeaManner)int.Parse(key["sea-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            sea = page.Sea is { } current && current.Manner == manner ? current : SeaStyle.For(manner) with { Scale = page.Sea?.Scale ?? 1 };
        }
        return document.WithLayers([page with { Sea = sea }, .. document.Layers.Skip(1)]);
    }
}
