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
    private Button? _fillAreaFinish, _fillAreaDetach;
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
        _fillAreaFinish = Ui.TextButton("FinishEditableAreaButton", "Завершить область", () => _editor.FinishEditableArea(), "compact");
        _fillAreaDetach = Ui.TextButton("DetachAreaObjectButton", "Отделить выбранный объект", _editor.DetachSelectedAreaObject, "compact");
        _fillAreaPanel = new StackPanel
        {
            Spacing = 6, Children =
            {
                _fillAreaPicker, _fillAreaSummary,
                Ui.TextButton("StartEditableAreaButton", "Новая редактируемая область", _editor.StartEditableArea, "compact"),
                _fillAreaFinish,
                new TextBlock { Text = "Клики — вершины · Enter — заполнить · Esc — отменить. Центр изображения внутри границы; края не обрезаются. Предпросмотр выборочный, до 64 штампов в изменяемой части.", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
                AppearanceNumberBox("EditableAreaDensityBox", "Плотность", null, () => _editor.DisplayedEditableArea?.Density ?? _editor.StampDensity, _editor.SetEditableAreaDensity),
                new TextBlock { Name = "EditableAreaDensityUnits", Text = "Объектов на 10 000 квадратных единиц карты", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
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
            _fillAreaFinish!.IsVisible = _editor.IsCreatingEditableArea;
            _fillAreaDetach!.IsEnabled = _editor.CanDetachAreaObject;
            _fillAreaPanel.IsVisible = _editor.StampPlacementMode == Map.Presentation.ViewModels.StampPlacementMode.FillArea
                || choices.Length > 0 || _editor.IsCreatingEditableArea;
            SyncAppearanceSections();
        }
        finally { _syncingAreas = false; }
    }

    private sealed record AreaChoice(Guid Id, string Name)
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
        _stampToolAreaText.Text = $"Область · плотность {area.Density:0} на 10 000 ед²";
    }
}
