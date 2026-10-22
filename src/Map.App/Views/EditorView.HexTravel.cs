using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;
using Map.Rendering;

namespace Map.App.Views;

public sealed partial class EditorView
{
    private Control BuildHexMeasureSection()
    {
        var mode = new ComboBox { Name = "HexJourneyModeBox", ItemsSource = new[] { "Пешком", "Верхом", "Телега", "Лодка" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var from = new TextBox { Name = "HexJourneyFromBox", Watermark = "Например 0308", MinWidth = 0 };
        var to = new TextBox { Name = "HexJourneyToBox", Watermark = "Например 1808", MinWidth = 0 };
        var summary = new TextBlock { Name = "HexRouteText", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var routes = new StackPanel { Name = "HexJourneyRoutes", Spacing = 5 };
        var days = new StackPanel { Name = "HexJourneyDays", Spacing = 5 };
        var reach = new CheckBox { Name = "HexJourneyReachBox", Content = "Куда дойдём за 1 / 2 / 3 дня" };
        var secret = new CheckBox { Name = "HexJourneySecretBox", Content = "План ведущего · скрыт от игроков", IsEnabled = false };
        var date = new TextBlock { Name = "HexJourneyDate", TextWrapping = TextWrapping.Wrap };
        var day = new NumericUpDown { Name = "HexJourneyCampaignDayBox", Minimum = 0, Maximum = int.MaxValue, Increment = 1 };
        var miles = new TextBox { Name = "HexJourneyMilesBox" };
        var costs = new Dictionary<string, TextBox>();
        var table = new StackPanel { Spacing = 4 };
        foreach (var tile in HexStarterTiles.All.Where(t => t.Target == HexPaintTarget.Terrain))
        {
            var box = new TextBox { Name = "HexJourneyCost_" + tile.Id.Split('/').Last(), MinWidth = 0, Watermark = tile.TravelCost is null ? "Закрыто" : "Стоимость" };
            costs[tile.Id] = box;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,78") };
            row.Children.Add(new TextBlock { Text = tile.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(box, 1); row.Children.Add(box); table.Children.Add(row);
        }
        void Profile()
        {
            if (!double.TryParse(miles.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var speed)) return;
            var values = new Dictionary<string, double>();
            foreach (var (id, box) in costs)
            {
                if (string.IsNullOrWhiteSpace(box.Text)) continue;
                if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var cost)) return;
                values[id] = cost;
            }
            _editor.SetHexTravelProfile(speed, values);
        }
        var terrains = HexStarterTiles.All.Where(t => t.Target == HexPaintTarget.Terrain).ToArray();
        var encounterTerrain = new ComboBox { Name = "HexEncounterTerrainBox", ItemsSource = new[] { "Любая местность" }.Concat(terrains.Select(t => t.Name)).ToArray(), SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var region = new NumericUpDown { Name = "HexEncounterRegionBox", Minimum = 0, Maximum = HexCell.MaxRegion, Value = 0, Increment = 1 };
        var entries = new TextBox { Name = "HexEncounterEntriesBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 92,
            Watermark = "Каждая строка — одна встреча", MaxLength = 100000 };
        var seed = new NumericUpDown { Name = "HexEncounterSeedBox", Minimum = int.MinValue, Maximum = int.MaxValue, Increment = 1 };
        string Terrain() => encounterTerrain.SelectedIndex > 0 ? terrains[encounterTerrain.SelectedIndex - 1].Id : "";
        void LoadEncounter() => entries.Text = string.Join("\n", _editor.HexJourney.Encounters.FirstOrDefault(t => t.Terrain == Terrain() && t.Region == (int)(region.Value ?? 0))?.Entries ?? []);
        encounterTerrain.SelectionChanged += (_, _) => { if (!_syncingHex) LoadEncounter(); };
        region.ValueChanged += (_, _) => { if (!_syncingHex) LoadEncounter(); };
        mode.SelectionChanged += (_, _) => { if (!_syncingHex && mode.SelectedIndex >= 0) _editor.SetHexTravelMode((TravelMode)mode.SelectedIndex); };
        reach.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexJourney(_editor.HexJourney with { ShowReach = reach.IsChecked == true }); };
        secret.IsCheckedChanged += (_, _) => { if (!_syncingHex) _editor.SetHexJourney(_editor.HexJourney with { SecretRoute = secret.IsChecked == true }); };
        day.ValueChanged += (_, _) => { if (!_syncingHex) _editor.HexCampaignDay = (int)(day.Value ?? 0); };
        seed.ValueChanged += (_, _) => { if (!_syncingHex) _editor.SetHexJourney(_editor.HexJourney with { Seed = (int)(seed.Value ?? 1801) }); };
        void RenderRoutes()
        {
            routes.Children.Clear(); days.Children.Clear(); summary.Text = _editor.HexJourneySummary;
            for (var i = 0; i < _editor.HexJourneyRoutes.Count; i++)
            {
                var index = i; var journey = _editor.HexJourneyRoutes[i];
                var delta = Math.Max(0, journey.Route.Days - _editor.HexJourneyRoutes[0].Route.Days);
                var text = i == _editor.HexJourneySelected ? "● Выбран · " : "Выбрать · ";
                text += journey.Route.Label + (i > 0 ? $"\n+{delta:0.#} дн. / " + (journey.Crossings == 0 ? "без переправы" : $"переправ: {journey.Crossings}") : "\nЛучшее время");
                var button = Ui.TextButton("HexJourneyRoute_" + i, text, () => _editor.SelectHexJourney(index), "compact");
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
                routes.Children.Add(button);
            }
            if (_editor.HexJourneyRoutes.Count > 0)
            {
                if (_editor.HexJourneyRoutes.Count < 3) routes.Children.Add(HexHint("Близких по времени разных путей больше нет (до +30 %, общее меньше 60 %)."));
                foreach (var step in _editor.HexJourneyRoutes[_editor.HexJourneySelected].Days)
                {
                    var index = step.Day - 1;
                    var button = Ui.TextButton("HexJourneyDay_" + step.Day, step.Description, () => _editor.CenterHexJourneyDay(index), "compact");
                    button.HorizontalContentAlignment = HorizontalAlignment.Left;
                    button.Content = new TextBlock { Text = step.Description, TextWrapping = TextWrapping.Wrap };
                    days.Children.Add(button);
                }
            }
            else if (_editor.HexJourney.From is not null && _editor.HexJourney.To is not null) routes.Children.Add(HexHint("Путь по этому профилю не найден."));
            if (_editor.HexJourneyReach?.Limited == true) days.Children.Add(HexHint("Показана часть зоны: достигнут предел расчёта 100000 гексов."));
        }
        HexSync(() =>
        {
            var settings = _editor.HexJourney; var profile = settings.Profile;
            mode.SelectedIndex = (int)settings.Mode; from.Text = _editor.HexJourneyNumber(settings.From); to.Text = _editor.HexJourneyNumber(settings.To);
            reach.IsChecked = settings.ShowReach; secret.IsChecked = true;
            day.Value = _editor.HexCampaignDay; date.Text = _editor.HexJourneyDate; seed.Value = settings.Seed;
            miles.Text = profile.MilesPerDay.ToString("0.##", CultureInfo.CurrentCulture);
            foreach (var (id, box) in costs)
                box.Text = profile.Costs.TryGetValue(id, out var custom) ? custom.ToString("0.##", CultureInfo.CurrentCulture) : "";
            LoadEncounter(); RenderRoutes();
        }, [.. DocumentChanged, nameof(EditorViewModel.HexJourney), nameof(EditorViewModel.HexJourneyRoutes), nameof(EditorViewModel.HexJourneySelected), nameof(EditorViewModel.HexJourneyDate)]);
        return Ui.Category("HexJourneyCategory", "Путешествие", "accent-create", new StackPanel { Spacing = 8, Children =
        {
            mode,
            Ui.Subgroup("Откуда", new StackPanel { Spacing = 4, Children = { from, Ui.TextButton("HexJourneyPickFromButton", "Указать на карте", () => _editor.PickHexJourneyEndpoint(true), "compact") } }),
            Ui.Subgroup("Куда", new StackPanel { Spacing = 4, Children = { to, Ui.TextButton("HexJourneyPickToButton", "Указать на карте", () => _editor.PickHexJourneyEndpoint(false), "compact") } }),
            Ui.TextButton("HexJourneyBuildButton", "Построить маршрут", () => { var a = from.Text ?? ""; var b = to.Text ?? ""; if (_editor.SetHexJourneyNumber(true, a)) _editor.SetHexJourneyNumber(false, b); }, "compact"),
            summary, routes, reach, secret, Ui.Subgroup("Навигатор · нажмите день, чтобы приблизиться", days),
            new Expander { Name = "HexJourneyProfileExpander", Header = "Профиль и стоимость местности", Content = new StackPanel { Spacing = 6, Children =
            { Ui.Subgroup("Миль за день", miles), HexHint("Пустое поле — обычная стоимость местности. Равнина 1, лес 2, горы 3. Лодка проходит только по воде и рекам; телега вне дороги ×3."), table,
                Ui.TextButton("HexJourneyProfileSaveButton", "Сохранить профиль", Profile, "compact") } } },
            new Expander { Name = "HexJourneyCalendarExpander", Header = "Календарь, погода и встречи", Content = new StackPanel { Spacing = 6, Children =
            {
                date, Ui.Subgroup("День кампании", day), HexHint("12 месяцев по 30 дней. День 0 — начало весны. Сезон меняет цену пути; погода и встречи стабильны для дня и ночёвки."),
                Ui.Subgroup("Местность встречи", encounterTerrain), Ui.Subgroup("Номер региона · 0 — любой", region), entries,
                Ui.Subgroup("Зерно встреч и погоды", seed),
                Ui.TextButton("HexEncounterSaveButton", "Сохранить таблицу встреч", () => _editor.SetHexEncounterTable(Terrain(), (int)(region.Value ?? 0), (entries.Text ?? "").Split('\n')), "compact")
            } } },
            new Expander { Header = "Масштаб карты", Content = BuildHexScaleSection() },
        } });
    }
}
