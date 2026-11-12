using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Map.App.Controls;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// R23: «Генератор материка» in the Land tool - the drawn land grows heights, climate, rivers,
/// towns and roads step by step, each a background preview that «Применить» makes one undo step.
/// </summary>
public sealed partial class EditorView
{
    private Control BuildContinentPanel()
    {
        var seed = new TextBox { Name = "ContinentSeedBox", MinWidth = 0 };
        void SyncSeed() => seed.Text = _editor.ContinentSettings.Seed.ToString(CultureInfo.InvariantCulture);
        seed.LostFocus += (_, _) =>
        {
            if (int.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                _editor.ContinentSettings = _editor.ContinentSettings with { Seed = value };
        };
        var dice = Explain(Ui.IconButton("ContinentSeedButton", "Случайное зерно", MapIcons.HexDice, () => { _editor.NewContinentSeed(); SyncSeed(); }),
            PanelDemoKind.GenContinentSeed, "Случайное зерно", "Другое зерно — другие горы, реки и поселения на той же суше.");

        Control Slider(string name, string label, double min, double max, string unit, Func<ContinentSettings, double> read,
            Func<ContinentSettings, double, ContinentSettings> write, double step, string description) =>
            AppearanceSlider(name, label, min, max, unit, () => read(_editor.ContinentSettings),
                (v, _) => _editor.ContinentSettings = write(_editor.ContinentSettings, v), step, description: description);

        // Variant 7 «Плитки-значки» (owner, 2026-11-12): five step tiles and the whole continent, three in a row.
        var steps = FixedTiles(3);
        foreach (var step in Enum.GetValues<ContinentStep>())
        {
            var value = step;
            var (icon, kind, text) = step switch
            {
                ContinentStep.Heights => (MapIcons.GenRelief, PanelDemoKind.GenContinentHeights,
                    "Высота суши растёт от берега вглубь, с шумом и цепями хребтов; нарисованные хребты учитываются."),
                ContinentStep.Erosion => (MapIcons.GenErosion, PanelDemoKind.GenContinentErosion, "Склоны оседают, вода прорезает долины."),
                ContinentStep.Climate => (MapIcons.GenClimate, PanelDemoKind.GenContinentClimate, "Снег, тундра, лес, степь, пустыня и болото по широте, высоте и влажности."),
                ContinentStep.Rivers => (MapIcons.GenRivers, PanelDemoKind.GenContinentRivers, "Реки по склонам к морю — обычные реки, их можно править."),
                _ => (MapIcons.GenSettlements, PanelDemoKind.GenContinentSettlements, "Поселения на ровной земле у воды, дороги между ними по самому лёгкому пути."),
            };
            var name = $"{(int)step + 1}. {EditorViewModel.ContinentStepName(step)}";
            steps.Children.Add(Explain(GenerationTile("ContinentStep" + step, EditorViewModel.ContinentStepName(step), icon, () => _editor.RunContinentStep(value),
                $"шаг {(int)step + 1}", name), kind, name, text));
        }
        var all = Explain(GenerationTile("ContinentAllButton", "Весь материк", MapIcons.GenAll, _editor.RunContinentAll, "шаги 1–5"),
            PanelDemoKind.GenContinentAll, "Весь материк", "Пять шагов по порядку из нарисованной суши: один предпросмотр, «Применить» — одна отмена.");
        Lit(all);
        steps.Children.Add(all);
        SyncSeed();

        return WarmGenerationCards(Ui.Category("LandContinentCategory", "Генератор материка", "accent-create", new StackPanel
        {
            Spacing = 7,
            Children =
            {
                Ui.Hint("Нарисуйте сушу — генератор построит на ней материк по шагам: высоты, эрозию, климат, реки, поселения и дороги. Каждый шаг сначала показывается, «Применить» — одна отмена. Повтор шага заменяет только то, что он сделал раньше; нарисованное вами остаётся."),
                SeedRow(seed, dice),
                Slider("ContinentReliefSlider", "Горы", 0, 100, "%", s => s.Relief * 100, (s, v) => s with { Relief = v / 100 }, 5,
                    "Насколько высоко поднимается суша вдали от берега."),
                Slider("ContinentRidgesSlider", "Хребты", 0, 100, "%", s => s.Ridges * 100, (s, v) => s with { Ridges = v / 100 }, 5,
                    "Сколько рельефа собрано в цепи гор. Хребты, нарисованные кистью «Рельеф → Хребет», тоже учитываются."),
                Slider("ContinentErosionSlider", "Эрозия", 0, 100, "%", s => s.Erosion * 100, (s, v) => s with { Erosion = v / 100 }, 5,
                    "Насколько склоны оседают и вода прорезает долины."),
                Slider("ContinentMoistureSlider", "Влажность", -100, 100, "", s => s.Moisture * 100, (s, v) => s with { Moisture = v / 100 }, 5,
                    "Суше — степи и пустыни, влажнее — леса и болота."),
                Slider("ContinentTemperatureSlider", "Тепло", -100, 100, "", s => s.Temperature * 100, (s, v) => s with { Temperature = v / 100 }, 5,
                    "Холоднее — тундра и снега, теплее — пустыни."),
                Slider("ContinentRiversSlider", "Реки", 0, 30, "/млн", s => s.RiversPerMillion, (s, v) => s with { RiversPerMillion = v }, 1,
                    "Сколько рек на миллион квадратных единиц суши."),
                Slider("ContinentTownsSlider", "Поселения", 0, 30, "/млн", s => s.TownsPerMillion, (s, v) => s with { TownsPerMillion = v }, 1,
                    "Сколько поселений на миллион квадратных единиц суши; дороги соединяют их."),
                Ui.Subgroup("Шаги по порядку", steps),
                BuildGenerationState("Continent"),
            },
        }, expanded: false), PanelDemoKind.GenContinentAll);
    }
}
