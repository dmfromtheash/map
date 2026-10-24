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

        var title = new TextBlock { Text = "Стиль карты", FontSize = 17, FontWeight = FontWeight.SemiBold }.Classed("role-title");
        ToolTip.SetTip(title, "Вся карта в технике туши; подписи остаются чёткими. Каждое изменение — один шаг отмены (Ctrl+Z).");

        var presets = new StackPanel { Spacing = 4 }.Named("StylePresets");
        foreach (var preset in Enum.GetValues<MapStylePreset>())
        {
            var captured = preset;
            var button = new ToggleButton
            {
                Content = EditorViewModel.PresetTitle(preset), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6),
            }.Named("StylePreset" + preset);
            AutomationProperties.SetName(button, "Применить стиль: тушь, " + EditorViewModel.PresetTitle(preset));
            ToolTip.SetTip(button, PresetHint(preset) + " Щелчок применяет стиль с его настройками — один шаг отмены.");
            button.Click += (_, _) => { _editor.ApplyStylePreset(captured); SyncStyleSection(); };
            _styleSyncs.Add(() =>
            {
                var on = _editor.ShownStyle.Enabled && _editor.ShownStyle.Preset == captured;
                button.IsChecked = on;
                Ui.SetClass(button, "active", on);
            });
            presets.Children.Add(button);
        }

        var strength = ValueRow("StyleStrengthSlider", "StyleStrengthBox", null, "Сила стиля", 0, 100, "%",
            () => _editor.ShownStyle.Strength * 100,
            (value, commit) => { _editor.SetStyleStrength(value / 100, commit); if (commit) SyncStyleSection(); },
            step: 1, defaultValue: 100, description: "100 — вся карта в технике туши, меньше — сквозь тушь проступают цвета карты.");

        var effects = new StackPanel { Spacing = 0 };
        foreach (var kind in Enum.GetValues<StyleEffectKind>())
            effects.Children.Add(StyleEffectRow(kind));

        var body = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12, 8, 12, 16),
            Children =
            {
                title, master,
                Group("Тушь", presets),
                Group("Сила", strength),
                Group("Эффекты", effects),
            },
        };
        _styleSyncs.Add(() =>
        {
            var on = _editor.ShownStyle.Enabled;
            foreach (var child in body.Children.Skip(3)) child.Opacity = on ? 1 : 0.55;
        });
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.Named("StyleSection");
    }

    private static string PresetHint(MapStylePreset preset) => preset switch
    {
        MapStylePreset.InkPenHatching => "Перо по белой бумаге: чёрный контур и штриховка по форме гор, тон — густотой и перекрёстными штрихами.",
        MapStylePreset.InkPenStipple => "Перо, пуантилизм: тон складывается из точек туши, контур сохраняется.",
        _ => "Японская мокрая кисть по рисовой бумаге: несколько разбавлений туши, много белой бумаги, растекание, кайма, туман.",
    };

    private static string StyleEffectHint(StyleEffectKind kind) => kind switch
    {
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
        var hint = StyleEffectHint(kind);
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
    }
}
