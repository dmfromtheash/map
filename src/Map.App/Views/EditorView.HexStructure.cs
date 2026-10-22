using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.Core.Documents;
using Map.Presentation.ViewModels;
using Map.Rendering;
using Map.Core.Editing;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildHexPresentationChoice()
    {
        var choice = new ComboBox { Name = "HexPresentationChoice", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Рельеф", "Политика", "Путешествие", "Знания игроков" }, SelectedIndex = (int)_editor.HexPresentation };
        choice.SelectionChanged += (_, _) =>
        {
            if (!_syncingHex && choice.SelectedIndex >= 0) _editor.HexPresentation = (HexPresentation)choice.SelectedIndex;
        };
        HexSync(() => choice.SelectedIndex = (int)_editor.HexPresentation, nameof(EditorViewModel.HexPresentation));
        return Ui.Subgroup("Представление", choice);
    }

    private Control BuildHexParentChoice(HexRegion region)
    {
        var regions = _editor.HexRegions;
        var options = new[] { (Id: 0, Name: "Без родителя") }.Concat(regions
            .Where(r => HexRegionHierarchy.CanParent(regions, region.Id, r.Id))
            .Select(r => (r.Id, Name: HexRegionHierarchy.Label(regions, r)))).ToArray();
        var choice = new ComboBox { Name = "HexRegionParent_" + region.Id, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = options.Select(o => o.Name).ToArray(), SelectedIndex = Array.FindIndex(options, o => o.Id == region.ParentRegion) };
        choice.SelectionChanged += (_, _) =>
        {
            if (!_syncingHex && choice.SelectedIndex >= 0) _editor.SetHexRegionParent(region.Id, options[choice.SelectedIndex].Id);
        };
        return Ui.Subgroup("Родитель", choice);
    }

    private Control BuildHexPoiSection()
    {
        var list = new StackPanel { Name = "HexPoiList", Spacing = 5 };
        object? shown = null;
        HexSync(() =>
        {
            var points = _editor.HexPoints;
            var signature = (_editor.HexKeyHex, points);
            if (Equals(shown, signature)) return;
            shown = signature;
            list.Children.Clear();
            var types = HexStarterTiles.All.Where(t => t.Target == HexPaintTarget.Feature).ToArray();
            for (var i = 0; i < points.Count; i++)
            {
                var index = i;
                var point = points[i];
                var name = new TextBox { Name = "HexPoiName_" + i, Text = point.Name, Watermark = "Имя точки интереса", MaxLength = 120 };
                name.LostFocus += (_, _) =>
                {
                    if (index < _editor.HexPoints.Count) _editor.SetHexPoi(index, _editor.HexPoints[index] with { Name = name.Text ?? "" });
                };
                var type = new ComboBox { Name = "HexPoiType_" + i, HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = types.Select(t => t.Name).ToArray(), SelectedIndex = Array.FindIndex(types, t => t.Id == point.Type) };
                type.SelectionChanged += (_, _) =>
                {
                    if (!_syncingHex && type.SelectedIndex >= 0 && index < _editor.HexPoints.Count)
                        _editor.SetHexPoi(index, _editor.HexPoints[index] with { Type = types[type.SelectedIndex].Id, Variant = 0 });
                };
                var visible = new CheckBox { Name = "HexPoiPlayer_" + i, Content = "Виден игрокам", IsChecked = point.PlayerVisible };
                visible.IsCheckedChanged += (_, _) =>
                {
                    if (!_syncingHex && index < _editor.HexPoints.Count)
                        _editor.SetHexPoi(index, _editor.HexPoints[index] with { PlayerVisible = visible.IsChecked == true });
                };
                list.Children.Add(new StackPanel { Spacing = 3, Children =
                {
                    name, type, visible,
                    HexGrid(3,
                        Ui.TextButton("HexPoiUp_" + i, "Выше", () => _editor.MoveHexPoi(index, -1), "compact"),
                        Ui.TextButton("HexPoiDown_" + i, "Ниже", () => _editor.MoveHexPoi(index, 1), "compact"),
                        Ui.TextButton("HexPoiRemove_" + i, "Удалить", () => _editor.RemoveHexPoi(index), "compact")),
                } });
            }
        }, [.. DocumentChanged, nameof(EditorViewModel.HexKeyHex), nameof(EditorViewModel.HexPoints)]);
        return Ui.Subgroup("Точки интереса", new StackPanel { Spacing = 5, Children =
        { list, Ui.TextButton("HexPoiAddButton", "Добавить точку", () => _editor.AddHexPoi(), "compact") } });
    }
}
