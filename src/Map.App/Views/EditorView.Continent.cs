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
        var seed = new TextBox { Name = "ContinentSeedBox", Width = 96, MinWidth = 0 };
        void SyncSeed() => seed.Text = _editor.ContinentSettings.Seed.ToString(CultureInfo.InvariantCulture);
        seed.LostFocus += (_, _) =>
        {
            if (int.TryParse(seed.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                _editor.ContinentSettings = _editor.ContinentSettings with { Seed = value };
        };
        var dice = Ui.IconButton("ContinentSeedButton", "Случайное зерно", MapIcons.HexDice, () => { _editor.NewContinentSeed(); SyncSeed(); });

        Control Slider(string name, string label, double min, double max, string unit, Func<ContinentSettings, double> read,
            Func<ContinentSettings, double, ContinentSettings> write, double step, string description) =>
            AppearanceSlider(name, label, min, max, unit, () => read(_editor.ContinentSettings),
                (v, _) => _editor.ContinentSettings = write(_editor.ContinentSettings, v), step, description: description);

        var steps = new Ui.ChoiceGrid { MaxColumns = 2, MinimumItemWidth = 110, Gap = 4 };
        foreach (var step in Enum.GetValues<ContinentStep>())
        {
            var value = step;
            var button = Ui.TextButton("ContinentStep" + step, $"{(int)step + 1}. {EditorViewModel.ContinentStepName(step)}",
                () => _editor.RunContinentStep(value), "compact");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            steps.Children.Add(button);
        }
        var all = Ui.TextButton("ContinentAllButton", "Весь материк", _editor.RunContinentAll, "accent");
        var apply = Ui.TextButton("ContinentApplyButton", "Применить", () => _editor.ApplyGeneration(), "compact");
        var cancel = Ui.TextButton("ContinentCancelButton", "Отмена расчёта / превью", _editor.CancelGeneration, "compact");
        var recalc = Ui.TextButton("ContinentRecalculateButton", "Пересчитать", _editor.RecalculateGeneration, "compact");
        var status = new TextBlock { Name = "ContinentStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var bar = new ProgressBar { Name = "ContinentProgressBar", Minimum = 0, Maximum = 100, Height = 4, IsVisible = false };
        void Sync()
        {
            apply.IsEnabled = _editor.GenerationHasPreview;
            cancel.IsEnabled = _editor.GenerationBusy || _editor.GenerationHasPreview;
            status.Text = _editor.GenerationConflict ? "Карта изменилась. Нажмите «Пересчитать»."
                : _editor.GenerationBusy ? $"Расчёт в фоне… {_editor.GenerationProgress * 100:0} %"
                : _editor.GenerationHasPreview ? "Предпросмотр на карте. Документ ещё не изменён." : "";
            bar.IsVisible = _editor.GenerationBusy; bar.Value = _editor.GenerationProgress * 100;
        }
        _editor.PropertyChanged += (_, _) => Sync();
        var timer = new Avalonia.Threading.DispatcherTimer(System.TimeSpan.FromMilliseconds(250), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => { if (_editor.GenerationBusy) Sync(); });
        timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
        SyncSeed(); Sync();

        return Ui.Category("LandContinentCategory", "Генератор материка", "accent-create", new StackPanel
        {
            Spacing = 7,
            Children =
            {
                Ui.Hint("Нарисуйте сушу — генератор построит на ней материк по шагам: высоты, эрозию, климат, реки, поселения и дороги. Каждый шаг сначала показывается, «Применить» — одна отмена. Повтор шага заменяет только то, что он сделал раньше; нарисованное вами остаётся."),
                new DockPanel { Children = { dice.Docked(Dock.Right), new TextBlock { Text = "Зерно", VerticalAlignment = VerticalAlignment.Center, Width = 92 }.Docked(Dock.Left), seed } },
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
                all, status, bar, apply, cancel, recalc,
            },
        }, expanded: false);
    }
}
