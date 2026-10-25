using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The Свет tab of the right panel (DEC-057): its own category, never mixed into other settings.
/// One switch for the whole light, the time of day (three buttons and a slider), then every
/// effect on its own row - a switch and a strength slider - and the map's lights, placed with a click.
/// </summary>
public sealed partial class EditorView
{
    private Control? _lightBody;
    private readonly List<Action> _lightSyncs = [];

    private Control LightBody() => _lightBody ??= BuildLightBody();

    private Control BuildLightBody()
    {
        var master = AppearanceToggle("LightEnabledToggle", "Свет и время суток включены",
            () => _editor.ShownLighting.Enabled, on => { _editor.SetLightingEnabled(on); SyncLightSection(); });
        ToolTip.SetTip(master, "Выключено — карта рисуется как без света. Настройки ниже сохраняются.");

        var time = ValueRow("LightTimeSlider", "LightTimeBox", null, "Время суток", 0, 100, "",
            () => _editor.ShownLighting.TimeOfDay * 100,
            (value, commit) => { _editor.SetTimeOfDay(value / 100, commit); if (commit) SyncLightSection(); },
            step: 1, defaultValue: 0, description: "0 — день, 50 — вечер, 100 — ночь. Между ними свет меняется плавно.");

        var effects = new StackPanel { Spacing = 0 };
        foreach (var kind in Enum.GetValues<LightingEffectKind>())
            effects.Children.Add(EffectRow(kind));

        var body = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(10, 0, 10, 8),
            Children =
            {
                // День, Вечер and Ночь are the ribbon's cards (DEC-064); the slider sets any time between them.
                master,
                Group("Время суток", time),
                Group("Эффекты", effects),
                Group("Огни на карте", BuildLightSources()),
            },
        };
        _lightSyncs.Add(() =>
        {
            var on = _editor.ShownLighting.Enabled;
            foreach (var child in body.Children.Skip(1)) child.Opacity = on ? 1 : 0.55;
        });
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.Named("LightSection");
    }

    private static Control Group(string caption, params Control[] content)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = caption.ToUpperInvariant(), FontSize = 11, LetterSpacing = 0.8, FontWeight = FontWeight.SemiBold }.Classed("faint"));
        foreach (var item in content) stack.Children.Add(item);
        return new Border
        {
            Padding = new Thickness(10, 8, 10, 10), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
            Child = stack,
        };
    }

    private static string EffectHint(LightingEffectKind kind) => kind switch
    {
        LightingEffectKind.Relief => "Склоны гор поворачиваются к солнцу: освещённые светлеют, теневые темнеют. Рисунок горы остаётся главным.",
        LightingEffectKind.Shadows => "Тень каждой горы ложится от её подножия в сторону от солнца; вечером длинная, днём короткая.",
        LightingEffectKind.Fog => "Дымка в низинах: густая на земле, редеет на склонах, вершины остаются чистыми. Днём её нет.",
        LightingEffectKind.Lights => "Факелы, окна и костры, поставленные на карту. Загораются к вечеру, ночью — главный свет.",
        _ => "Тёплый вечер и холодная ночь: цвет, яркость и насыщенность по времени суток. Выключено — цвет как днём.",
    };

    /// <summary>
    /// One effect in one line: its switch with the name, its strength slider and the percent. The
    /// slider previews while dragged and is one Undo step on release; a double click resets it.
    /// </summary>
    private Control EffectRow(LightingEffectKind kind)
    {
        var name = "Light" + kind;
        var hint = EffectHint(kind);
        var toggle = new CheckBox { Content = EditorViewModel.EffectTitle(kind), MinWidth = 0, MinHeight = 24, Padding = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
            .Named(name + "Toggle");
        AutomationProperties.SetName(toggle, EditorViewModel.EffectTitle(kind));
        ToolTip.SetTip(toggle, hint);
        toggle.Click += (_, _) => { _editor.SetLightingEffect(kind, toggle.IsChecked == true, null, true); SyncLightSection(); };

        var percent = new TextBlock { Width = 38, TextAlignment = Avalonia.Media.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center }
            .Named(name + "StrengthValue");
        var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, MinHeight = 18, Height = 22 }
            .Named(name + "Strength");
        AutomationProperties.SetName(slider, EditorViewModel.EffectTitle(kind) + ", сила в процентах");
        ToolTip.SetTip(slider, hint + " Перетащите; двойной щелчок — значение по умолчанию.");
        var syncing = false;
        var dragging = false;
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || syncing) return;
            percent.Text = $"{Math.Round(slider.Value):0} %";
            // Dragging or keys: the map follows at once; release or key up writes the Undo step.
            _editor.SetLightingEffect(kind, null, Math.Round(slider.Value) / 100, false);
        };
        slider.AddHandler(PointerPressedEvent, (_, _) => dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            _editor.SetLightingEffect(kind, null, Math.Round(slider.Value) / 100, true);
            SyncLightSection();
        };
        slider.KeyUp += (_, e) =>
        {
            if (e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down
                or Avalonia.Input.Key.Home or Avalonia.Input.Key.End or Avalonia.Input.Key.PageUp or Avalonia.Input.Key.PageDown)
            { _editor.SetLightingEffect(kind, null, Math.Round(slider.Value) / 100, true); SyncLightSection(); }
        };
        slider.DoubleTapped += (_, e) =>
        {
            e.Handled = true; dragging = false;
            _editor.SetLightingEffect(kind, null, EditorViewModel.Effect(new MapLighting(), kind).Strength, true);
            SyncLightSection();
        };
        _lightSyncs.Add(() =>
        {
            if (dragging) return;
            var effect = EditorViewModel.Effect(_editor.ShownLighting, kind);
            syncing = true;
            toggle.IsChecked = effect.Enabled;
            slider.Value = Math.Round(effect.Strength * 100);
            syncing = false;
            percent.Text = $"{Math.Round(effect.Strength * 100):0} %";
            slider.IsEnabled = effect.Enabled;
            percent.Opacity = effect.Enabled ? 1 : 0.5;
        });
        // The switch with its name and the percent on one line, the slider under them at full width.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") }.Named(name + "Row");
        slider.Margin = new Thickness(2, -2, 2, 2);
        Grid.SetColumn(percent, 1);
        Grid.SetRow(slider, 1); Grid.SetColumnSpan(slider, 2);
        row.Children.Add(toggle); row.Children.Add(percent); row.Children.Add(slider);
        return row;
    }

    private Control BuildLightSources()
    {
        const string hint = "Выберите вид огня, нажмите «Ставить огонь кликом» и щёлкайте по карте. Огни видны вечером и ночью.";
        var kinds = new UniformGrid { Rows = 1, Columns = 3 }.Named("LightPresetChoice");
        foreach (var (preset, text) in new[] { (LightSourcePreset.Torch, "Факел"), (LightSourcePreset.Window, "Окно"), (LightSourcePreset.Campfire, "Костёр") })
        {
            var button = new ToggleButton
            {
                Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(2, 5),
            }.Named("LightPreset" + preset);
            AutomationProperties.SetName(button, "Вид огня: " + text);
            button.Click += (_, _) => { _editor.LightPreset = preset; SyncLightSection(); };
            _lightSyncs.Add(() => { var on = _editor.LightPreset == preset; button.IsChecked = on; Ui.SetClass(button, "active", on); });
            kinds.Children.Add(button);
        }
        var place = new ToggleButton
        {
            Content = "Ставить огонь кликом", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        }.Named("LightPlaceToggle").Classed("accent");
        AutomationProperties.SetName(place, "Ставить огонь кликом по карте");
        ToolTip.SetTip(place, hint);
        ToolTip.SetTip(kinds, hint);
        place.Click += (_, _) => { _editor.ArmLightPlacement(place.IsChecked == true); SyncLightSection(); };

        var list = new StackPanel { Spacing = 2 }.Named("LightSourceList");
        var count = new TextBlock().Classed("faint");
        var clear = Ui.CommandButton("LightClearAll", "Убрать все огни", MapIcons.LineReset, () => { _editor.ClearLights(); SyncLightSection(); }, "compact");
        _lightSyncs.Add(() =>
        {
            place.IsChecked = _editor.LightPlacementArmed;
            Ui.SetClass(place, "active", _editor.LightPlacementArmed);
            var sources = _editor.ShownLighting.Sources;
            count.Text = sources.Length == 0 ? "Огней пока нет." : $"Огней на карте: {sources.Length}";
            clear.IsVisible = sources.Length > 0;
            list.Children.Clear();
            var index = 0;
            foreach (var source in sources.Take(40))
            {
                index++;
                var captured = source.Id;
                var kind = source.Color switch { "#ffc27a" => "окно", "#ff8a3c" => "костёр", _ => "огонь" };
                var remove = Ui.IconButton("LightRemove" + index, $"Убрать огонь {index}", MapIcons.LineReset,
                    () => { _editor.RemoveLight(captured); SyncLightSection(); });
                list.Children.Add(new DockPanel
                {
                    Children =
                    {
                        remove.Docked(Dock.Right),
                        new TextBlock { Text = $"{index}. {kind} · ({source.X:0}, {source.Y:0})", VerticalAlignment = VerticalAlignment.Center },
                    },
                });
            }
        });
        return new StackPanel { Spacing = 6, Children = { kinds, place, count, list, clear } };
    }

    /// <summary>Brings the Свет tab in line with the document (after an edit, Undo or Redo).</summary>
    private void SyncLightSection()
    {
        if (_lightBody is null) return;
        SyncAppearanceSections();
        foreach (var sync in _lightSyncs) sync();
        SyncAppearanceRibbon();
    }
}
