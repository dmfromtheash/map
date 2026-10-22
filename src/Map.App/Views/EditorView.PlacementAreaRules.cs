using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// R10 block of the area panel, «Где ставить»: a switch per rule (all off by default), the road
/// clearance, the land the area is limited to and «Пересчитать по карте».
/// </summary>
public sealed partial class EditorView
{
    private sealed record LandChoice(LayerId? Id, string Title)
    {
        public override string ToString() => Title;
    }

    private Control BuildEditableAreaRulesBlock()
    {
        PlacementAreaRules Rules() => _editor.EditableAreaRules;
        var avoidWater = AppearanceToggle("EditableAreaAvoidWaterToggle", "Не ставить на воду",
            () => Rules().AvoidWater, _editor.SetEditableAreaAvoidWater);
        ToolTip.SetTip(avoidWater, "Объекты не встанут на реку, озеро и открытое море.");
        var onlyWater = AppearanceToggle("EditableAreaOnlyOnWaterToggle", "Только на воду",
            () => Rules().OnlyOnWater, _editor.SetEditableAreaOnlyOnWater);
        ToolTip.SetTip(onlyWater, "Наоборот: для тростника, кувшинок, скал в море — объекты только на воде.");
        var avoidRoads = AppearanceToggle("EditableAreaAvoidRoadsToggle", "Не ставить на дорогу",
            () => Rules().AvoidRoads, _editor.SetEditableAreaAvoidRoads);
        var margin = AppearanceSlider("EditableAreaRoadMarginSlider", "Отступ от дороги", 0, 200, "px",
            () => Rules().RoadMargin, (value, commit) => _editor.SetEditableAreaRoadMargin(value, commit));
        var onlyLand = AppearanceToggle("EditableAreaOnlyOnLandToggle", "Только на суше",
            () => Rules().OnlyOnLand, _editor.SetEditableAreaOnlyOnLand);
        var land = new ComboBox { Name = "EditableAreaLandBox", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var syncingLand = false;
        land.SelectionChanged += (_, _) =>
        {
            if (!syncingLand && land.SelectedItem is LandChoice choice && choice.Id != Rules().LandLayerId)
                _editor.SetEditableAreaLandLayer(choice.Id);
        };
        var refresh = Ui.TextButton("EditableAreaRefreshRulesButton", "Пересчитать по карте", () =>
        {
            _editor.RefreshEditableAreaRules();
            SyncAppearanceSections();
        }, "compact");
        ToolTip.SetTip(refresh, "После правки реки, дороги или суши: заново проверить, где можно ставить. Отменяется Ctrl+Z.");
        var depth = AppearanceToggle("EditableAreaDepthSortToggle", "Ближние поверх дальних",
            () => _editor.EditableAreaDepthSort, _editor.SetEditableAreaDepthSort);
        ToolTip.SetTip(depth, "Объект ниже на карте рисуется поверх того, что выше: деревья и дома не «висят» друг на друге.");
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption").Named("EditableAreaRulesState");
        _appearanceSyncs.Add(() =>
        {
            var rules = Rules();
            margin.IsVisible = rules.AvoidRoads;
            land.IsVisible = rules.OnlyOnLand;
            refresh.IsVisible = rules.Any && _editor.SelectedEditableArea is not null;
            syncingLand = true;
            var choices = new[] { new LandChoice(null, "Любая суша") }
                .Concat(_editor.EditableAreaLandChoices.Select(c => new LandChoice(c.Id, c.Name))).ToArray();
            if (land.ItemsSource is not LandChoice[] previous || !previous.SequenceEqual(choices)) land.ItemsSource = choices;
            land.SelectedItem = choices.FirstOrDefault(c => c.Id == rules.LandLayerId) ?? choices[0];
            syncingLand = false;
            var blocked = _editor.EditableAreaRuleBlockedCount;
            state.Text = !rules.Any
                ? "Правила выключены: объекты ставятся везде внутри границы."
                : blocked == 0 ? "Правила включены. Все места области подходят."
                : $"Правила оставили пустыми мест: {blocked}. Остальные объекты не сдвинуты.";
        });
        return Ui.Subgroup("Где ставить", new StackPanel
        {
            Spacing = 4,
            Children = { state, avoidWater, onlyWater, avoidRoads, margin, onlyLand, land, refresh, depth },
        });
    }
}
