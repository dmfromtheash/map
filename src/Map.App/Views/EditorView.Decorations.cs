using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// R08 «Масштаб и украшения»: the tool panel (map scale, measuring, adding a scale bar, compass
/// rose, cartouche or live legend) and the property block of a selected decoration.
/// </summary>
public sealed partial class EditorView
{
    private static readonly (string Key, string Text)[] InkChoices =
        [("#FF6B4A12", "Золото"), ("#FF6E1E1E", "Киноварь"), ("#FF3B2A1A", "Сепия"), ("#FF1E2430", "Чернила")];

    private static readonly (string Key, string Text)[] PaperChoices =
        [("#FFF6E7C1", "Светлый пергамент"), ("#FFF1E3C4", "Пергамент"), ("#FFFFFFFF", "Белая"), ("#00000000", "Без бумаги"), ("#FF2A2118", "Тёмная")];

    private Control BuildMeasurePanel()
    {
        var distance = new TextBox { Name = "MapScaleDistanceBox", MinWidth = 90, PlaceholderText = "нет" };
        var unit = new ComboBox { Name = "MapScaleUnitComboBox", ItemsSource = MapScale.Units.ToArray(), MinWidth = 90, SelectedIndex = 0 };
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption").Named("MapScaleStateText");
        var measured = new TextBlock { TextWrapping = TextWrapping.Wrap }.Named("MeasureResultText");
        var syncing = false;
        void Sync()
        {
            syncing = true;
            var scale = _editor.MapScale;
            if (!distance.IsFocused)
                distance.Text = _editor.MapScalePer100Pixels is { } d ? d.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU")) : "";
            if (scale is not null)
            {
                var index = Array.IndexOf(MapScale.Units.ToArray(), scale.Unit);
                if (index >= 0) unit.SelectedIndex = index;
            }

            state.Text = scale is null
                ? "Масштаб не задан: карта меряется в пикселях."
                : "100 px на карте = " + scale.Format(100) + ". Вся ширина карты — " + scale.Format(_editor.Session.Document.Canvas.Width) + ".";
            measured.Text = _editor.MeasureText ?? "Протяните по карте, чтобы измерить расстояние.";
            syncing = false;
        }

        void Apply()
        {
            if (syncing) return;
            var text = (distance.Text ?? "").Trim().Replace('.', ',');
            var value = text.Length == 0 ? 0 : double.TryParse(text, NumberStyles.Float, CultureInfo.GetCultureInfo("ru-RU"), out var parsed) ? parsed : double.NaN;
            if (double.IsNaN(value)) { state.Text = "Введите число, например 2,5."; return; }
            _editor.SetMapScale(value, unit.SelectedItem as string ?? MapScale.Units[0]);
            Sync();
            SyncAppearanceSections();
        }

        distance.LostFocus += (_, _) => Apply();
        distance.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Apply(); };
        unit.SelectionChanged += (_, _) => { if (!syncing && _editor.MapScale is not null) Apply(); };
        var clear = Ui.TextButton("MapScaleClearButton", "Снять масштаб", () => { _editor.SetMapScale(0, ""); Sync(); SyncAppearanceSections(); }, "compact");
        _editor.SceneChanged += (_, _) => Sync();
        _editor.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EditorViewModel.StatusDetail)) Sync(); };
        Sync();

        var add = new Ui.ChoiceGrid { MaxColumns = 2, MinimumItemWidth = 110, Gap = 4 };
        foreach (var kind in Enum.GetValues<DecorationKind>())
        {
            var captured = kind;
            add.Children.Add(Ui.TextButton("AddDecoration" + kind + "Button", EditorViewModel.DecorationTitle(kind), () =>
            {
                _editor.AddDecoration(captured);
                SyncAppearanceSections();
            }, "compact"));
        }

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Explanation("Протяните по карте — увидите расстояние в единицах карты. Масштаб задаётся один раз для всей карты."),
                Ui.Subgroup("Масштаб карты", new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "100 px =", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                                distance, unit,
                            },
                        },
                        state, clear,
                    },
                }),
                Ui.Subgroup("Измерение", measured),
                Ui.Subgroup("Добавить на карту", add),
            },
        };
    }

    private Control BuildDecorationSection()
    {
        DecorationStyle? Style() => _editor.SelectedDecoration;
        var title = new TextBox { Name = "DecorationTitleBox", PlaceholderText = "Заглавие" };
        var subtitle = new TextBox { Name = "DecorationSubtitleBox", PlaceholderText = "Вторая строка: автор, год…" };
        title.LostFocus += (_, _) => { if (!_syncingAppearance && (title.Text ?? "") != (Style()?.Title ?? "")) _editor.SetDecorationTitle(title.Text ?? ""); };
        subtitle.LostFocus += (_, _) => { if (!_syncingAppearance && (subtitle.Text ?? "") != (Style()?.Subtitle ?? "")) _editor.SetDecorationSubtitle(subtitle.Text ?? ""); };
        var titleRow = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Заглавие" }.Classed("role-caption"), title } };
        var subtitleRow = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Вторая строка" }.Classed("role-caption"), subtitle } };
        // One «Вид» choice per kind, built once: a sync must never add to the list it is running.
        var variantChoices = Enum.GetValues<DecorationKind>()
            .Where(kind => EditorViewModel.DecorationVariantTitles(kind).Count > 0)
            .ToDictionary(kind => kind, kind => AppearanceChoice("DecorationVariantChoice" + kind, "Вид",
                [.. EditorViewModel.DecorationVariantTitles(kind).Select((text, i) => (i.ToString(CultureInfo.InvariantCulture), text))],
                () => Style()?.Variant.ToString(CultureInfo.InvariantCulture),
                key => _editor.SetDecorationVariant(int.Parse(key, CultureInfo.InvariantCulture))));
        var variantHost = new StackPanel { Name = "DecorationVariantHost" };
        foreach (var choice in variantChoices.Values) variantHost.Children.Add(choice);
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption").Named("DecorationInfoText");
        _appearanceSyncs.Add(() =>
        {
            var style = Style();
            if (style is null) return;
            if (!title.IsFocused) title.Text = style.Title;
            if (!subtitle.IsFocused) subtitle.Text = style.Subtitle;
            titleRow.IsVisible = style.Kind is DecorationKind.Cartouche or DecorationKind.Legend;
            subtitleRow.IsVisible = style.Kind == DecorationKind.Cartouche;
            foreach (var (kind, choice) in variantChoices) choice.IsVisible = kind == style.Kind;

            info.Text = style.Kind switch
            {
                DecorationKind.ScaleBar => style.Scale is null
                    ? "Масштаб карты не задан — линейка в пикселях. Задайте его в инструменте «Масштаб и украшения»."
                    : "Линейка следует за масштабом карты: 100 px = " + style.Scale.Format(100) + ".",
                DecorationKind.Legend => "Легенда собирается сама из того, что есть на карте: " + style.Entries.Count + " строк.",
                DecorationKind.Compass => "Роза ветров. Поворот — рамкой выделения.",
                _ => "Картуш: заглавие карты в рамке.",
            };
        });

        return Ui.Section("Украшение карты", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                info, variantHost, titleRow, subtitleRow,
                AppearanceChoice("DecorationInkChoice", "Чернила", InkChoices, () => Style()?.InkColor, _editor.SetDecorationInk),
                AppearanceChoice("DecorationPaperChoice", "Бумага", PaperChoices, () => Style()?.PaperColor, _editor.SetDecorationPaper),
            },
        });
    }
}
