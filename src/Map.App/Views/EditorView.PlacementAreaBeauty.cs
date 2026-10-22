using System;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.App.Views;

/// <summary>
/// R10 blocks of the area panel: «Красота» (a switch per property with its slider, shown only
/// while the property is on) and «Кисть плотности» (on/off, «Реже/Гуще/Как было», radius, strength,
/// reset). Every change is one Undo step; older areas have every switch off and look as before.
/// </summary>
public sealed partial class EditorView
{
    private Control BuildEditableAreaBeautyBlock()
    {
        PlacementAreaBeauty B() => _editor.EditableAreaBeauty;
        var softEdge = AppearanceToggle("EditableAreaSoftEdgeToggle", "Мягкий край",
            () => B().SoftEdge, _editor.SetEditableAreaSoftEdge);
        ToolTip.SetTip(softEdge, "К границе области объекты реже и мельче — лес не обрывается по линейке.");
        var edgeWidth = AppearanceSlider("EditableAreaEdgeWidthSlider", "Ширина края", 10, 600, "px",
            () => B().EdgeWidth, (v, c) => _editor.SetEditableAreaEdgeWidth(v, c));
        var clumps = AppearanceToggle("EditableAreaClumpsToggle", "Кучки и поляны",
            () => B().Clumps, _editor.SetEditableAreaClumps);
        ToolTip.SetTip(clumps, "Плотность меняется пятнами: где-то гуще, где-то поляна.");
        var clumpStrength = AppearanceSlider("EditableAreaClumpStrengthSlider", "Сила кучек", 0, 100, "%",
            () => B().ClumpStrength, (v, c) => _editor.SetEditableAreaClumpStrength(v, c));
        var clumpSize = AppearanceSlider("EditableAreaClumpSizeSlider", "Размер кучек", 40, 3000, "px",
            () => B().ClumpSize, (v, c) => _editor.SetEditableAreaClumpSize(v, c), 10);
        var species = AppearanceToggle("EditableAreaSpeciesPatchesToggle", "Рощи одного вида",
            () => B().SpeciesPatches, _editor.SetEditableAreaSpeciesPatches);
        ToolTip.SetTip(species, "Варианты ассета растут пятнами, а не вперемешку в каждой точке.");
        var shadow = AppearanceToggle("EditableAreaGroundShadowToggle", "Тень под объектами",
            () => B().GroundShadow, _editor.SetEditableAreaGroundShadow);
        ToolTip.SetTip(shadow, "Мягкая холодная тень у подножия, к юго-востоку (свет с северо-запада).");
        var shadowOpacity = AppearanceSlider("EditableAreaShadowOpacitySlider", "Сила тени", 0, 100, "%",
            () => B().ShadowOpacity * 100, (v, c) => _editor.SetEditableAreaShadowOpacity(v, c));
        var underlay = AppearanceToggle("EditableAreaUnderlayToggle", "Подложка массива",
            () => B().Underlay, _editor.SetEditableAreaUnderlay);
        ToolTip.SetTip(underlay, "Тёмная земля под объектами: густой массив читается одной массой, поляна остаётся светлой.");
        var underlayOpacity = AppearanceSlider("EditableAreaUnderlayOpacitySlider", "Сила подложки", 0, 100, "%",
            () => B().UnderlayOpacity * 100, (v, c) => _editor.SetEditableAreaUnderlayOpacity(v, c));
        var color = AppearanceToggle("EditableAreaColorVariationToggle", "Разброс цвета",
            () => B().ColorVariation, _editor.SetEditableAreaColorVariation);
        ToolTip.SetTip(color, "Яркость и теплота немного гуляют пятнами — лес не выглядит копией одного дерева.");
        var colorPercent = AppearanceSlider("EditableAreaColorVariationSlider", "Разброс, ±", 0, 10, "%",
            () => B().ColorVariationPercent, (v, c) => _editor.SetEditableAreaColorVariationPercent(v, c));

        var brush = AppearanceToggle("EditableAreaDensityBrushToggle", "Кисть плотности",
            () => _editor.IsAreaDensityBrushActive, _editor.SetAreaDensityBrush);
        ToolTip.SetTip(brush, "Ведите по области: где реже, где гуще. Каждый мазок — один шаг Ctrl+Z.");
        var mode = AppearanceChoice("EditableAreaDensityModeChoice", "Кисть",
            [("Thinner", "Реже"), ("Thicker", "Гуще"), ("Reset", "Как было")],
            () => _editor.AreaDensityBrushMode.ToString(),
            key => _editor.SetAreaDensityBrushMode(Enum.Parse<PlacementAreaDensityBrushMode>(key)));
        var radius = AppearanceSlider("EditableAreaDensityRadiusSlider", "Размер кисти", 10, 800, "px",
            () => _editor.AreaDensityBrushRadius, (v, c) => _editor.SetAreaDensityBrushRadius(v, c));
        var strength = AppearanceSlider("EditableAreaDensityStrengthSlider", "Нажим", 1, 100, "%",
            () => _editor.AreaDensityBrushStrength, (v, c) => _editor.SetAreaDensityBrushStrength(v, c));
        var reset = Ui.TextButton("EditableAreaDensityResetButton", "Сбросить кисть плотности", () =>
        {
            _editor.ClearEditableAreaDensityMask();
            SyncAppearanceSections();
        }, "compact");
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption").Named("EditableAreaBeautyState");
        _appearanceSyncs.Add(() =>
        {
            var b = B();
            edgeWidth.IsVisible = b.SoftEdge;
            clumpStrength.IsVisible = clumpSize.IsVisible = b.Clumps;
            shadowOpacity.IsVisible = b.GroundShadow;
            underlayOpacity.IsVisible = b.Underlay;
            colorPercent.IsVisible = b.ColorVariation;
            var natural = _editor.UsesNaturalAreaLayout;
            brush.IsEnabled = natural && _editor.SelectedEditableArea is not null;
            mode.IsVisible = radius.IsVisible = strength.IsVisible = _editor.IsAreaDensityBrushActive;
            reset.IsVisible = _editor.EditableAreaHasDensityMask && _editor.SelectedEditableArea is not null;
            state.Text = !natural ? "«Красота» — у естественной расстановки (кнопка выше)."
                : !b.Any ? "Всё выключено: область выглядит как раньше."
                : "Каждое свойство включается отдельно; объекты не сдвигаются, только убираются или возвращаются.";
        });
        return Ui.Subgroup("Красота", new StackPanel
        {
            Spacing = 4,
            Children = { state, softEdge, edgeWidth, clumps, clumpStrength, clumpSize, species, shadow, shadowOpacity,
                underlay, underlayOpacity, color, colorPercent, brush, mode, radius, strength, reset },
        });
    }
}
