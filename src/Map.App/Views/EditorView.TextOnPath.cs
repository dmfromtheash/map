using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// R07 blocks of the text panel: label presets, the «Имя» button and «Вдоль пути» (bind to the
/// nearest river, road or hex path; place, side, upright, flip, gap).
/// </summary>
public sealed partial class EditorView
{
    private LabelPreset _namePreset = LabelPreset.City;

    private Control BuildLabelPresetBlock()
    {
        var grid = new Ui.ChoiceGrid { MaxColumns = 4, MinimumItemWidth = 60, Gap = 4 };
        foreach (var preset in LabelPresets.All)
        {
            var kind = preset;
            grid.Children.Add(Ui.TextButton("LabelPreset" + kind + "Button", LabelPresets.Title(kind), () =>
            {
                _namePreset = kind;
                _editor.ApplyLabelPreset(kind);
                SyncAppearanceSections();
            }, "compact"));
        }

        var name = Ui.TextButton("TextGenerateNameButton", "Имя", () => { _editor.GenerateLabelName(_namePreset); SyncAppearanceSections(); }, "compact");
        ToolTip.SetTip(name, "Придумать название по виду последнего пресета (по умолчанию — город). Отменяется Ctrl+Z.");
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption").Named("TextNameKindText");
        _appearanceSyncs.Add(() => hint.Text = "Имя будет: " + LabelPresets.Title(_namePreset).ToLowerInvariant());
        return Ui.Subgroup("Подпись карты", new StackPanel { Spacing = 4, Children = { grid, name, hint } });
    }

    private Control BuildTextPathBlock()
    {
        TextPathBinding? Path() => _editor.SelectedTextPath;
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption").Named("TextPathStateText");
        var bind = Ui.TextButton("TextBindPathButton", "Привязать к ближайшей реке, дороге или пути", () => { _editor.BindSelectedTextToNearestPath(); SyncAppearanceSections(); }, "compact");
        var unbind = Ui.TextButton("TextUnbindPathButton", "Отвязать", () => { _editor.UnbindSelectedTextPath(); SyncAppearanceSections(); }, "compact");
        var flip = Ui.TextButton("TextPathFlipButton", "Перевернуть", () => { _editor.FlipTextPathDirection(); SyncAppearanceSections(); }, "compact");
        var bound = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                AppearanceSlider("TextPathPositionSlider", "Место на линии", 0, 100, "%",
                    () => Path()?.Position * 100, (v, c) => _editor.SetTextPathPosition(v / 100.0, c), 1),
                AppearanceChoice("TextPathSideChoice", "Сторона",
                    [.. Enum.GetValues<TextPathSide>().Select(s => (s.ToString(), EditorViewModel.TextPathSideTitle(s)))],
                    () => Path()?.Side.ToString(),
                    key => _editor.SetTextPathSide(Enum.Parse<TextPathSide>(key))),
                AppearanceSlider("TextPathGapSlider", "Отступ от линии", -50, 200, "px",
                    () => Path()?.Gap, (v, c) => _editor.SetTextPathGap(v, c)),
                AppearanceToggle("TextPathUprightToggle", "Не вверх ногами",
                    () => Path()?.KeepUpright, _editor.SetTextPathKeepUpright),
                new Ui.ChoiceGrid { MaxColumns = 2, Children = { flip, unbind } },
            },
        }.Named("TextPathBoundControls");
        _appearanceSyncs.Add(() =>
        {
            var path = Path();
            bound.IsVisible = path is not null;
            state.Text = path is null
                ? "Подпись свободна. Поставьте её у линии и привяжите — она пойдёт вдоль и будет следовать за правкой."
                : path.Detached
                    ? "Линия удалена: подпись осталась на месте. Привяжите её к другой линии или отвяжите."
                    : "Идёт по " + EditorViewModel.TextPathTargetTitle(path.Target) + " и следует за её правкой.";
        });
        return Ui.Subgroup("Вдоль пути", new StackPanel { Spacing = 4, Children = { state, bind, bound } });
    }
}
