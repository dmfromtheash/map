using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The Стили tab of the right panel (R09, DEC-059), built like Свет: one switch for the whole
/// style, the presets of the family (a click applies one, one Undo step), the strength of the
/// style, and the medium's effects - each on its own row with a switch and a strength slider.
/// Only the effects the chosen preset uses are shown.
/// </summary>
public sealed partial class EditorView
{
    private Control? _styleBody;
    private readonly List<Action> _styleSyncs = [];

    private Control StyleBody() => _styleBody ??= BuildStyleBody();

    private Control BuildStyleBody()
    {
        var master = AppearanceToggle("StyleEnabledToggle", "Стиль карты включён",
            () => _editor.ShownStyle.Enabled, on => { _editor.SetStyleEnabled(on); SyncStyleSection(); });
        ToolTip.SetTip(master, "Выключено — карта рисуется как без стиля. Настройки ниже сохраняются.");

        // The presets are the ribbon's cards (DEC-064); this column holds the switch, the strength and the effects.
        var strength = ValueRow("StyleStrengthSlider", "StyleStrengthBox", null, "Сила стиля", 0, 100, "%",
            () => _editor.ShownStyle.Strength * 100,
            (value, commit) => { _editor.SetStyleStrength(value / 100, commit); if (commit) SyncStyleSection(); },
            step: 1, defaultValue: 100, description: "100 — вся карта в выбранной технике, меньше — проступают исходные цвета карты.");

        var effects = new StackPanel { Spacing = 0 };
        foreach (var kind in Enum.GetValues<StyleEffectKind>())
            effects.Children.Add(StyleEffectRow(kind));

        var body = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(10, 0, 10, 8),
            Children =
            {
                master,
                Group("Сила", strength),
                Group("Эффекты", effects),
            },
        };
        _styleSyncs.Add(() =>
        {
            var on = _editor.ShownStyle.Enabled;
            foreach (var child in body.Children.Skip(1)) child.Opacity = on ? 1 : 0.55;
        });
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.Named("StyleSection");
    }

    private static string PresetHint(MapStylePreset preset) => preset switch
    {
        MapStylePreset.BlueprintSurvey => "Топографическая синька: лист прусской синей, сетка, горы горизонталями формы и линиями гребней, море линиями глубин.",
        MapStylePreset.CharcoalVineStump => "Ивовый уголь: массы гор растушёваны, зерно бумаги в полутонах, свет оставлен бумагой, немного штрихов по форме.",
        MapStylePreset.CharcoalHatching => "Штриховка углём: только линия — по форме, накрест в тени, твёрдый контур; море ровной штриховкой.",
        MapStylePreset.OilAllaPrima => "Алла прима: плоская кисть по плоскостям гор, тёплый свет и холодная тень, тёплый грунт между мазками.",
        MapStylePreset.OilImpastoRhythm => "Ритм импасто: короткие густые штрихи двух оттенков, вдоль берега в море и на суше.",
        MapStylePreset.WatercolorBigShapes => "Крупные пятна: три прозрачных тона по сырой бумаге, пигмент у засохшего края и немного линий гребня.",
        MapStylePreset.UkiyoeSosakuHanga => "Сосаку-ханга: чёрные теневые грани, широкие срезы резца по форме, земляная суша и срезы в море индиго.",
        MapStylePreset.PrintLetterpress => "Высокая печать: чёрная и красная краски, плотные края пятен и зернистая середина, линии воды у берега.",
        MapStylePreset.PrintTintedLithograph => "Литография: зернистый карандашный рисунок, тёплый тон доски и процарапанные светлые места.",
        MapStylePreset.OldMapBlaeu => "Атлас Блау: медная гравюра с ручной раскраской, линии моря, зелёная полоса берега, вержированная бумага.",
        MapStylePreset.EngravingAquatint => "Офорт: контур и главные линии иглой, две ступени акватинты на теневых склонах, бистр и тиснение доски.",
        MapStylePreset.InkPenHatching => "Перо по белой бумаге: чёрный контур и штриховка по форме гор, тон — густотой и перекрёстными штрихами.",
        MapStylePreset.InkPenStipple => "Перо, пуантилизм: тон складывается из точек туши, контур сохраняется.",
        _ => "Японская мокрая кисть по рисовой бумаге: несколько разбавлений туши, много белой бумаги, растекание, кайма, туман.",
    };

    private static string StyleEffectHint(StyleEffectKind kind, MapStylePreset preset) => kind switch
    {
        StyleEffectKind.Grid => "Сетка землемера: крупные клетки и тонкие между ними; на горах бледнее.",
        StyleEffectKind.FormLines => "Белые линии по светотени горы — как горизонтали на карте, каждая вторая ярче.",
        StyleEffectKind.Crest when preset == MapStylePreset.BlueprintSurvey => "Жирные белые линии гребней и силуэта гор.",
        StyleEffectKind.WaterLines when preset == MapStylePreset.BlueprintSurvey => "Линии глубин в море вдоль берега, реже к открытому морю.",
        StyleEffectKind.Paper when preset == MapStylePreset.BlueprintSurvey => "Неровное покрытие листа синей краской.",
        StyleEffectKind.Smudge => "Мягкий тон, втёртый растушёвкой; уголь наполовину заполняет зерно бумаги.",
        StyleEffectKind.CrossHatch => "Второй и третий слой штриховки накрест в тенях и самых тёмных местах.",
        StyleEffectKind.Strokes when preset is MapStylePreset.CharcoalVineStump or MapStylePreset.CharcoalHatching => "Штрихи по направлению горы; на море — ровные горизонтальные.",
        StyleEffectKind.Crest when preset == MapStylePreset.CharcoalVineStump => "Прерывистый силуэт, берег и тёмные акценты в глубоких складках.",
        StyleEffectKind.Contour when preset == MapStylePreset.CharcoalHatching => "Твёрдый прерывистый контур гор, гребней и берега.",
        StyleEffectKind.WaterLines when preset is MapStylePreset.CharcoalVineStump or MapStylePreset.CharcoalHatching => "Три-четыре линии вдоль берега, бледнее к морю.",
        StyleEffectKind.Paper when preset is MapStylePreset.CharcoalVineStump or MapStylePreset.CharcoalHatching => "Уголь ложится на бугорки бумаги: при лёгком нажиме видно зерно.",
        StyleEffectKind.Impasto => "Густая краска с гребнями и бороздками щетины, свет с северо-запада.",
        StyleEffectKind.Imprimatura when preset == MapStylePreset.OilImpastoRhythm => "Тёмные бороздки грунта между штрихами.",
        StyleEffectKind.Imprimatura => "Тёплая сиена грунта проглядывает между мазками.",
        StyleEffectKind.Gloss => "Влажный блеск на гребнях краски.",
        StyleEffectKind.Crest when preset == MapStylePreset.OilAllaPrima => "Несколько тёмных мазков в самых глубоких складках вдоль гребней.",
        StyleEffectKind.Paper when preset == MapStylePreset.OilAllaPrima => "Переплетение холста видно там, где краска тонкая.",
        StyleEffectKind.Bleed when preset == MapStylePreset.WatercolorBigShapes => "Бледная мокрая заливка гор мягко растекается за край рисунка.",
        StyleEffectKind.Edges when preset == MapStylePreset.WatercolorBigShapes => "Пигмент темнеет у края высохших пятен и светлеет в середине.",
        StyleEffectKind.Sea when preset == MapStylePreset.WatercolorBigShapes => "Бирюзовое и синее море, тёмная полоса у берега и мягкая заливка рек.",
        StyleEffectKind.Sea when preset == MapStylePreset.PrintTintedLithograph => "Зернистый карандашный тон моря темнеет вдали от берега.",
        StyleEffectKind.Contour when preset >= MapStylePreset.UkiyoeSosakuHanga => "Печатный контур силуэта гор, берега и главных линий рисунка.",
        StyleEffectKind.Paper when preset >= MapStylePreset.WatercolorBigShapes => "Тонкие волокна и зерно бумаги проступают через рисунок.",
        StyleEffectKind.Granulation => "Пигмент собирается в углублениях бумаги, оставляя зернистый цвет.",
        StyleEffectKind.Crest => "Несколько прерывистых тёмных линий на главных гребнях гор.",
        StyleEffectKind.Gouge => "Широкие светлые срезы и короткие V-образные штрихи вдоль граней гор.",
        StyleEffectKind.SeaCuts => "Длинные светлые срезы на синей доске вдоль берега.",
        StyleEffectKind.WoodGrain => "Волнистые волокна деревянной доски проступают через краску.",
        StyleEffectKind.InkSqueeze => "У края красного пятна краска выдавлена в более плотную кромку.",
        StyleEffectKind.Starved => "В середине плотных пятен меньше краски, и сквозь неё видно зерно бумаги.",
        StyleEffectKind.WaterLines => "Редкие чёрные линии воды повторяют изгиб берега.",
        StyleEffectKind.StoneCrayon => "Тон гор складывается из крупинок карандаша на шероховатом камне.",
        StyleEffectKind.TintPlate => "Тёплая светлая краска второй доски покрывает лист.",
        StyleEffectKind.Scraped => "Светлые места на вершинах и у берега выскоблены до бумаги.",
        StyleEffectKind.Sea => "Линии моря у Блау; ступени акватинты с белой кромкой у офорта.",
        StyleEffectKind.Coast => "Печатная линия вдоль берега и берегов рек.",
        StyleEffectKind.ColourWash => "Зелёная раскраска у берега, охра на горах, синий цвет рек.",
        StyleEffectKind.Aquatint => "Две ступени тона с зерном канифоли на теневых гранях гор.",
        StyleEffectKind.Plate => "След края печатной доски на бумаге.",
        StyleEffectKind.Border => "Двойная рамка и шкала чёрных и белых делений атласа Блау.",
        StyleEffectKind.Strokes => "Волоски кисти ложатся вдоль склонов и форм карты.",
        StyleEffectKind.Bleed => "Мокрая тушь затекает в волокна бумаги за край мазка.",
        StyleEffectKind.Edges => "Где мокрое пятно высохло, по его краю собралась тёмная кайма пигмента.",
        StyleEffectKind.DryBrush => "Кисть с малым запасом туши рвётся на зерне бумаги.",
        StyleEffectKind.Mist => "Горизонтальные полосы, где тушь редеет до бумаги, как пейзаж в тумане.",
        StyleEffectKind.Pen => "Собственная фактура пера: штриховка по форме или точки. Мелкие штрихи при отдалении сливаются в тон.",
        StyleEffectKind.Paper => "Волокна и пятна проклейки бумаги.",
        _ => "Ваш контур тушью у гор возвращается поверх стиля.",
    };

    /// <summary>One effect in one line: its switch with the name and the percent, its strength slider under them.</summary>
    private Control StyleEffectRow(StyleEffectKind kind)
    {
        var name = "Style" + kind;
        var hint = StyleEffectHint(kind, _editor.ShownStyle.Preset);
        var toggle = new CheckBox { MinWidth = 0, MinHeight = 24, Padding = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
            .Named(name + "Toggle");
        ToolTip.SetTip(toggle, hint);
        toggle.Click += (_, _) => { _editor.SetStyleEffect(kind, toggle.IsChecked == true, null, true); SyncStyleSection(); };

        var percent = new TextBlock { Width = 38, TextAlignment = Avalonia.Media.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
            .Named(name + "StrengthValue");
        var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, MinHeight = 18, Height = 22 }
            .Named(name + "Strength");
        ToolTip.SetTip(slider, hint + " Перетащите; двойной щелчок — значение стиля.");
        var syncing = false;
        var dragging = false;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || syncing) return;
            percent.Text = $"{Math.Round(slider.Value):0} %";
            _editor.SetStyleEffect(kind, null, Math.Round(slider.Value) / 100, false);
        };
        slider.AddHandler(PointerPressedEvent, (_, _) => dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            _editor.SetStyleEffect(kind, null, Math.Round(slider.Value) / 100, true);
            SyncStyleSection();
        };
        slider.KeyUp += (_, e) =>
        {
            if (e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down
                or Avalonia.Input.Key.Home or Avalonia.Input.Key.End or Avalonia.Input.Key.PageUp or Avalonia.Input.Key.PageDown)
            { _editor.SetStyleEffect(kind, null, Math.Round(slider.Value) / 100, true); SyncStyleSection(); }
        };
        slider.DoubleTapped += (_, e) =>
        {
            e.Handled = true; dragging = false;
            _editor.SetStyleEffect(kind, null, MapStyle.For(_editor.ShownStyle.Preset).Effect(kind).Strength, true);
            SyncStyleSection();
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") }.Named(name + "Row");
        _styleSyncs.Add(() =>
        {
            var style = _editor.ShownStyle;
            row.IsVisible = MapStyle.EffectsOf(style.Preset).Contains(kind);
            var title = EditorViewModel.StyleEffectTitle(kind, style.Preset);
            var currentHint = StyleEffectHint(kind, style.Preset);
            ToolTip.SetTip(toggle, currentHint);
            ToolTip.SetTip(slider, currentHint + " Перетащите; двойной щелчок — значение стиля.");
            toggle.Content = title;
            AutomationProperties.SetName(toggle, title);
            AutomationProperties.SetName(slider, title + ", сила в процентах");
            if (dragging) return;
            var effect = style.Effect(kind);
            syncing = true;
            toggle.IsChecked = effect.Enabled;
            slider.Value = Math.Round(effect.Strength * 100);
            syncing = false;
            percent.Text = $"{Math.Round(effect.Strength * 100):0} %";
            slider.IsEnabled = effect.Enabled;
            percent.Opacity = effect.Enabled ? 1 : 0.5;
        });
        slider.Margin = new Thickness(2, -2, 2, 2);
        Grid.SetColumn(percent, 1);
        Grid.SetRow(slider, 1); Grid.SetColumnSpan(slider, 2);
        row.Children.Add(toggle); row.Children.Add(percent); row.Children.Add(slider);
        return row;
    }

    /// <summary>Brings the Стили tab in line with the document (after an edit, Undo or Redo).</summary>
    private void SyncStyleSection()
    {
        if (_styleBody is null) return;
        SyncAppearanceSections();
        foreach (var sync in _styleSyncs) sync();
        SyncAppearanceRibbon();
    }
}
