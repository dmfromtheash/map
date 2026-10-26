using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// R07b blocks: the importance of the selected label and its region (back to the middle, free it),
/// and the map's label rules - by scale, by overlap, and clear ground under labels.
/// </summary>
public sealed partial class EditorView
{
    private static readonly string[] ImportanceTitles = ["1 — мелкая", "2", "3 — обычная", "4", "5 — главная"];

    private Control BuildTextRegionBlock()
    {
        var importance = AppearanceChoice("LabelImportanceChoice", "Важность",
            [.. Enumerable.Range(1, 5).Select(i => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), ImportanceTitles[i - 1]))],
            () => _editor.SelectedLabelImportance?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key => _editor.SetLabelImportance(int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)));
        ToolTip.SetTip(importance, "При отдалении первыми скрываются мелкие подписи, при наложении — менее важная. Включается в «Подписи карты».");
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption").Named("TextRegionStateText");
        var middle = Ui.TextButton("TextRegionResetButton", "В середину области", () => { _editor.ResetRegionLabelShift(); SyncAppearanceSections(); }, "compact");
        var free = Ui.TextButton("TextRegionUnbindButton", "Отвязать от области", () => { _editor.UnbindRegionLabel(); SyncAppearanceSections(); }, "compact");
        var bound = new Ui.ChoiceGrid { MaxColumns = 2, Children = { middle, free } }.Named("TextRegionControls");
        _appearanceSyncs.Add(() =>
        {
            var region = _editor.SelectedTextRegion;
            bound.IsVisible = state.IsVisible = region is not null;
            if (region is null) return;
            var card = region.EntityId is { } id ? _editor.Entities.FirstOrDefault(e => e.Id == id) : null;
            state.Text = region.Detached
                ? "Область удалена: подпись осталась на месте."
                : "Стоит внутри области и следует за её контуром" + (card is null ? "." : $"; имя из карточки «{card.Name}».")
                  + (region.OffsetX != 0 || region.OffsetY != 0 ? " Сдвинута вручную." : "");
        });
        return Ui.Subgroup("Важность и область", new StackPanel { Spacing = 4, Children = { importance, state, bound } });
    }

    /// <summary>The map's label rules; <paramref name="prefix"/> keeps the names unique where the block appears twice.</summary>
    private Control BuildLabelSettingsBlock(string prefix)
    {
        LabelSettings Current() => _editor.LabelSettings;
        var byScale = AppearanceToggle(prefix + "LabelsByScaleToggle", "Подписи по масштабу",
            () => Current().ByScale, v => _editor.SetLabelSettings(Current() with { ByScale = v }));
        ToolTip.SetTip(byScale, "При отдалении остаются важные подписи (5 — всегда), при приближении появляются остальные.");
        var overlaps = AppearanceToggle(prefix + "LabelsOverlapToggle", "Убирать наложения",
            () => Current().ResolveOverlaps, v => _editor.SetLabelSettings(Current() with { ResolveOverlaps = v }));
        ToolTip.SetTip(overlaps, "Из двух пересекающихся подписей видна более важная.");
        var clear = AppearanceToggle(prefix + "LabelsClearUnderToggle", "Не ставить объекты под подписи",
            () => Current().ClearUnderLabels, v => _editor.SetLabelSettings(Current() with { ClearUnderLabels = v }));
        ToolTip.SetTip(clear, "Леса и горы областей расстановки обходят подписи при следующей перестройке области.");
        return Ui.Subgroup("Подписи карты", new StackPanel { Spacing = 4, Children = { byScale, overlaps, clear } });
    }
}
