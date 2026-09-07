using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private ComboBox? _fillAreaPicker;
    private TextBlock? _fillAreaSummary;
    private Control? _fillAreaPanel;
    private Control? _fillAreaPlacementActions;
    private TextBlock? _fillAreaPlacementHint;
    private TextBlock? _fillAreaResult;
    private Button? _fillAreaFinish, _fillAreaDetach;
    private Button? _fillAreaNatural, _fillAreaAnother;
    private TextBlock? _fillAreaArrangementHint;
    private TextBlock? _fillAreaDensityUnits;
    private Control? _fillAreaSizeVariation;
    private ComboBox? _fillAreaPattern;
    private Control? _fillAreaJoinEdges;
    private bool _syncingAreas;
    private Avalonia.Media.Imaging.Bitmap? _fillAreaThumbnail;
    private string? _fillAreaThumbnailHash;

    private Control BuildEditableAreaPanel()
    {
        _fillAreaPicker = new ComboBox { Name = "EditableAreaPicker", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            PlaceholderText = "Области этой карты" };
        _fillAreaPicker.SelectionChanged += (_, _) =>
        {
            if (!_syncingAreas && _fillAreaPicker.SelectedItem is AreaChoice item) _editor.SelectEditableArea(item.Id);
        };
        _fillAreaSummary = new TextBlock { Name = "EditableAreaSummary", TextWrapping = TextWrapping.Wrap };
        _fillAreaDetach = Ui.TextButton("DetachAreaObjectButton", "Отделить выбранный объект", _editor.DetachSelectedAreaObject, "compact");
        _fillAreaNatural = Ui.TextButton("NaturalEditableAreaButton", "Расположить естественно", _editor.ArrangeEditableAreaNaturally, "compact");
        _fillAreaAnother = Ui.TextButton("AnotherEditableAreaArrangementButton", "Другой рисунок", _editor.TryAnotherEditableAreaArrangement, "compact");
        _fillAreaArrangementHint = new TextBlock { Name = "EditableAreaArrangementHint", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        _fillAreaDensityUnits = new TextBlock { Name = "EditableAreaDensityUnits", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        _fillAreaSizeVariation = AppearanceNumberBox("EditableAreaSizeVariationBox", "Размеры, ±%", null,
            () => _editor.EditableAreaSizeVariation, _editor.SetEditableAreaSizeVariation);
        _fillAreaPattern = new ComboBox { Name = "EditableAreaPatternBox", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemsSource = new[] { new PatternChoice(PlacementAreaPattern.Masses, "Массивы"),
                new PatternChoice(PlacementAreaPattern.Ridge, "Гряда"), new PatternChoice(PlacementAreaPattern.Groups, "Группы"),
                new PatternChoice(PlacementAreaPattern.Clearings, "Вокруг просветов") } };
        _fillAreaPattern.SelectionChanged += (_, _) =>
        {
            if (!_syncingAreas && _fillAreaPattern.SelectedItem is PatternChoice choice) _editor.SetEditableAreaPattern(choice.Pattern);
        };
        _fillAreaJoinEdges = AppearanceToggle("EditableAreaJoinEdgesToggle", "Смыкать края",
            () => _editor.EditableAreaJoinEdges, _editor.SetEditableAreaJoinEdges);
        ToolTip.SetTip(_fillAreaJoinEdges, "Небольшое перекрытие крон и подножий. Выключите, чтобы объекты стояли раздельно.");
        _fillAreaPanel = new StackPanel
        {
            Spacing = 6, Children =
            {
                _fillAreaPicker, _fillAreaSummary,
                Ui.TextButton("StartEditableAreaButton", "Новая редактируемая область", _editor.StartEditableArea, "compact"),
                _fillAreaNatural, _fillAreaPattern, _fillAreaArrangementHint,
                AppearanceNumberBox("EditableAreaDensityBox", "Плотность", null, () => _editor.EditableAreaDensity, _editor.SetEditableAreaDensity),
                _fillAreaDensityUnits, _fillAreaSizeVariation, _fillAreaJoinEdges, _fillAreaAnother,
                new TextBlock { Text = "Центры внутри границы; края не обрезаются. В предпросмотре до 64 объектов; полный результат — после «Разместить».", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
                AppearanceNumberBox("EditableAreaSeedBox", "Seed", null, () => _editor.DisplayedEditableArea?.Seed, _editor.SetEditableAreaSeed),
                Ui.TextButton("InsertEditableAreaNodeButton", "Добавить вершину на ребре", _editor.InsertEditableAreaNode, "compact"),
                Ui.TextButton("DeleteEditableAreaNodeButton", "Удалить вершину (в черновике — последнюю)", _editor.DeleteEditableAreaNode, "compact"),
                Ui.TextButton("SelectAreaObjectButton", "Выбрать объект области", _editor.PickEditableAreaObject, "compact"),
                _fillAreaDetach,
                Ui.TextButton("ApplyEditableAreaSettingsButton", "Применить параметры штампа к области", _editor.ApplyStampSettingsToEditableArea, "compact"),
                Ui.TextButton("BakeEditableAreaButton", "Превратить область в обычные объекты", _editor.BakeEditableArea, "compact"),
                Ui.TextButton("DeleteEditableAreaButton", "Удалить область и связанные объекты", _editor.DeleteEditableArea, "compact", "destructive"),
            },
        };
        return _fillAreaPanel;
    }

    private Control BuildEditableAreaPlacementActions()
    {
        _fillAreaFinish = Ui.TextButton("FinishEditableAreaButton", "Разместить", () => _editor.FinishEditableArea(), "primary");
        _fillAreaFinish.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        _fillAreaPlacementHint = new TextBlock { Name = "EditableAreaPlacementHint", TextWrapping = TextWrapping.Wrap }
            .Classed("faint", "role-caption");
        _fillAreaResult = new TextBlock { Name = "EditableAreaResult", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        // The commit action stays outside the scrolling settings, including before the first vertex.
        _fillAreaPlacementActions = new Border
        {
            Name = "EditableAreaPlacementActions", IsVisible = false, Padding = new Avalonia.Thickness(12, 8, 12, 12),
            Child = new StackPanel { Spacing = 8, Children = { _fillAreaResult, _fillAreaPlacementHint, _fillAreaFinish } },
        };
        return _fillAreaPlacementActions;
    }

    private void SyncEditableAreaPanel()
    {
        if (_fillAreaPanel is null || _fillAreaPicker is null || _fillAreaSummary is null) return;
        _syncingAreas = true;
        try
        {
            var choices = _editor.EditableAreas.Select(p => new AreaChoice(p.Id, p.Name)).ToArray();
            if (_fillAreaPicker.ItemsSource is not AreaChoice[] previous || !previous.SequenceEqual(choices))
                _fillAreaPicker.ItemsSource = choices;
            _fillAreaPicker.SelectedItem = choices.FirstOrDefault(p => p.Id == _editor.SelectedEditableArea?.Id);
            _fillAreaSummary.Text = _editor.EditableAreaSummary;
            _stampGestureBlock.IsVisible = _editor.SelectedSet is not null || _editor.SelectedEditablePath is not null || _editor.CurrentAreaSource is not null;
            if (_fillAreaPlacementActions is not null)
            {
                _fillAreaPlacementActions.IsVisible = _editor.ActiveTool == Map.Presentation.ViewModels.EditorTool.Stamp
                    && _editor.StampPlacementMode == Map.Presentation.ViewModels.StampPlacementMode.FillArea;
                _fillAreaFinish!.IsEnabled = _editor.CanPlaceEditableArea;
                _fillAreaPlacementHint!.Text = _editor.EditableAreaPlacementHint;
                _fillAreaResult!.Text = _editor.EditableAreaResult;
            }
            _fillAreaNatural!.IsVisible = _editor.SelectedEditableArea is { LayoutVersion: < 4 };
            _fillAreaAnother!.IsEnabled = _editor.DisplayedEditableArea is not null;
            _fillAreaArrangementHint!.Text = _editor.DisplayedEditableArea is { LayoutVersion: < 4 }
                ? "Сохранена прежняя расстановка. «Расположить естественно» соберёт объекты в группы; Ctrl+Z вернёт прежний вид."
                : _editor.EditableAreaPatternHint;
            _fillAreaPattern!.IsVisible = _editor.UsesNaturalAreaLayout;
            _fillAreaPattern.SelectedItem = _fillAreaPattern.Items.OfType<PatternChoice>().FirstOrDefault(p => p.Pattern == _editor.EditableAreaPattern);
            _fillAreaDensityUnits!.Text = _editor.UsesNaturalAreaLayout
                ? "0–100% · добавляет объекты от середины к краям. Число объектов — внизу."
                : "До указанного числа объектов на 10 000 квадратных единиц карты. Размер изображений и промежутки ограничивают заполнение.";
            _fillAreaSizeVariation!.IsVisible = _editor.UsesNaturalAreaLayout;
            _fillAreaJoinEdges!.IsVisible = _editor.UsesNaturalAreaLayout;
            _fillAreaDetach!.IsEnabled = _editor.CanDetachAreaObject;
            _fillAreaPanel.IsVisible = _editor.StampPlacementMode == Map.Presentation.ViewModels.StampPlacementMode.FillArea
                || choices.Length > 0 || _editor.IsCreatingEditableArea;
            SyncAppearanceSections();
        }
        finally { _syncingAreas = false; }
    }

    private sealed record AreaChoice(Guid Id, string Name)
    { public override string ToString() => Name; }
    private sealed record PatternChoice(PlacementAreaPattern Pattern, string Name)
    { public override string ToString() => Name; }

    private void SyncAreaAssetCard()
    {
        if (_editor.CurrentAreaSource is not { } area) return;
        var sha = area.Variants.FirstOrDefault()?.Sha256;
        if (sha != _fillAreaThumbnailHash)
        {
            _fillAreaThumbnail?.Dispose(); _fillAreaThumbnail = null; _fillAreaThumbnailHash = sha;
            if (sha is not null && _editor.StampImages?.TryGetImageForPixelSize(sha, 64, 64) is { } image)
            {
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var stream = data.AsStream();
                _fillAreaThumbnail = new Avalonia.Media.Imaging.Bitmap(stream);
            }
        }
        _stampToolThumbnail.Source = _fillAreaThumbnail;
        _stampToolCurrentText.Text = area.Name;
        _stampVariantName.Text = "Сохранённый вариант";
        _stampVariantCounter.Text = area.Request.RandomizeVariant ? $"{area.Variants.Length} вариантов" : "1 вариант";
        _stampCurrentAssetButton.IsEnabled = true;
        Avalonia.Automation.AutomationProperties.SetName(_stampCurrentAssetButton, "Начать новую область: " + area.Name);
        ToolTip.SetTip(_stampCurrentAssetButton, "Новая область теми же сохранёнными изображениями");
        _stampToolAreaText.Text = area.LayoutVersion >= 3 ? $"Область · плотность {area.Density:0}%"
            : $"Область · плотность {area.Density:0} на 10 000 ед²";
    }
}
