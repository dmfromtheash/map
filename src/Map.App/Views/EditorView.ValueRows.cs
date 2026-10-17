using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Map.App.Views;

/// <summary>
/// One way to set a number anywhere in the editor: the name of the value with a plain-language
/// explanation, a slider to feel it, a box to type it exactly, its unit, and a reset to the
/// default that appears only while the value differs from it. The slider answers the thumb,
/// the arrow keys (Shift - ten steps) and a double click (default); the mouse wheel is left
/// to the panel's scrolling on purpose.
/// </summary>
public sealed partial class EditorView
{
    /// <summary>Remembered strengths of effects switched off, so switching them back on restores the value.</summary>
    private readonly Dictionary<string, double> _effectMemory = [];

    private Control ValueRow(
        string sliderName, string boxName, string? readoutName, string label,
        double minimum, double maximum, string unit, Func<double?> read, Action<double, bool> apply,
        double step = 1, double? defaultValue = null, string? description = null,
        Control? trailing = null, TextBox? box = null)
    {
        var ownBox = box is null;
        box ??= NumberBox(boxName);
        box.MinWidth = 58; box.Width = 64; box.MinHeight = 26; box.Padding = new Thickness(6, 2);
        box.HorizontalContentAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(box, unit.Length == 0 ? label : $"{label}, {unit}");

        var unitText = new TextBlock
        {
            Text = unit, VerticalAlignment = VerticalAlignment.Center, MinWidth = unit.Length == 0 ? 0 : 22,
            Margin = new Thickness(4, 0, 0, 0),
        }.Classed("faint");

        // Kept for automation that reads the formatted value; the box is what the user sees.
        var readout = new TextBlock { IsVisible = false }.Named(readoutName ?? sliderName + "Value");

        var slider = new Slider
        {
            Minimum = minimum, Maximum = maximum,
            Value = WholeNumber(Math.Clamp(read() ?? minimum, minimum, maximum)),
            SmallChange = step, LargeChange = step * 10, TickFrequency = 1, IsSnapToTickEnabled = true,
            MinHeight = 26,
        }.Named(sliderName);
        AutomationProperties.SetName(slider, $"{label}, {unit}, целое значение");

        var range = $"{WholeNumber(minimum).ToString("0", CultureInfo.InvariantCulture)}–{WholeNumber(maximum).ToString("0", CultureInfo.InvariantCulture)}{(unit.Length == 0 ? "" : " " + unit)}";
        var defaultText = defaultValue is { } d ? $" По умолчанию {WholeNumber(d).ToString("0", CultureInfo.InvariantCulture)}{(unit.Length == 0 ? "" : " " + unit)}." : "";
        var tip = (description is null ? "" : description + " ") + $"Диапазон {range}.{defaultText}"
            + $" Перетащите ползунок или введите число в поле; стрелки — шаг {WholeNumber(step):0}, с Shift — ×10; двойной щелчок по ползунку — значение по умолчанию.";
        ToolTip.SetTip(slider, tip);

        var caption = Ui.ParameterLabel(label);
        ToolTip.SetTip(caption, tip);

        Button? reset = null;
        if (defaultValue is { } fallback)
        {
            reset = ResetGlyph(sliderName + "Reset",
                $"Вернуть по умолчанию: {WholeNumber(fallback).ToString("0", CultureInfo.InvariantCulture)}{(unit.Length == 0 ? "" : " " + unit)}",
                () => Commit(fallback));
            reset.Width = 22; reset.Height = 22; reset.MinHeight = 22; reset.Margin = new Thickness(2, 0, 2, 0);
        }

        var dragging = false;
        string Format(double value) => WholeNumber(value).ToString("0", CultureInfo.InvariantCulture);
        void Show(double value)
        {
            readout.Text = Format(value);
            if (!box.IsFocused) box.Text = Format(value);
            if (reset is not null) reset.IsVisible = defaultValue is { } d && Math.Abs(WholeNumber(value) - WholeNumber(d)) > 0.0001;
        }

        void Commit(double value)
        {
            if (_syncingAppearance) return;
            value = WholeNumber(value);
            apply(value, true);
            SyncAppearanceSections();
        }

        Show(slider.Value);

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            var value = WholeNumber(slider.Value);
            if (Math.Abs(slider.Value - value) > 0.0001) { slider.Value = value; return; }
            Show(value);
            if (!_syncingAppearance && dragging) apply(value, false);
        };
        slider.AddHandler(PointerPressedEvent, (_, _) => dragging = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        slider.PointerCaptureLost += (_, _) =>
        {
            if (!dragging) return;
            dragging = false;
            Commit(slider.Value);
        };
        slider.KeyDown += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return;
            var direction = e.Key is Key.Right or Key.Up ? 1 : -1;
            slider.Value = Math.Clamp(slider.Value + (direction * step * 10), minimum, maximum);
            e.Handled = true;
        };
        slider.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
                Commit(slider.Value);
        };
        // The wheel scrolls the panel and never the value: a value changes only by dragging the
        // thumb, by the arrow keys or by typing into the box (owner's request, 2026-10-16).
        slider.DoubleTapped += (_, e) =>
        {
            if (defaultValue is not { } d) return;
            dragging = false;
            e.Handled = true;
            Commit(d);
        };

        if (ownBox)
        {
            var committed = box.Text ?? "";
            void CommitBox()
            {
                if (_syncingAppearance) return;
                var text = box.Text ?? "";
                if (text == committed) return;
                if (!TryWholeNumber(text, out var value))
                {
                    _editor.ReportInspectorError($"«{label}» должно быть целым числом.");
                    box.Text = committed;
                    return;
                }
                committed = text;
                Commit(value);
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                CommitBox(); e.Handled = true; Canvas.Focus();
            };
            box.GotFocus += (_, _) => committed = box.Text ?? "";
            box.LostFocus += (_, _) => CommitBox();
        }

        _appearanceSyncs.Add(() =>
        {
            if (dragging) return;
            var current = read();
            slider.IsEnabled = current is not null;
            box.IsEnabled = current is not null;
            if (current is { } value)
            {
                var clamped = WholeNumber(Math.Clamp(value, minimum, maximum));
                if (Math.Abs(slider.Value - clamped) > 0.0001) slider.Value = clamped;
                Show(value);
            }
            else if (reset is not null) reset.IsVisible = false;
        });

        var header = new DockPanel { LastChildFill = true };
        if (trailing is not null) header.Children.Add(trailing.Docked(Dock.Right));
        header.Children.Add(unitText.Docked(Dock.Right));
        header.Children.Add(box.Docked(Dock.Right));
        if (reset is not null) header.Children.Add(reset.Docked(Dock.Right));
        header.Children.Add(readout.Docked(Dock.Right));
        header.Children.Add(caption);

        var panel = new StackPanel { Margin = new Thickness(0, 2), Children = { header, slider } };
        if (description is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -2, 0, 2),
            }.Classed("faint", "role-caption"));
        }

        return panel;
    }

    /// <summary>
    /// One effect of a look - a switch with a name and a short explanation, and its own settings
    /// beneath, which are greyed out while the effect is off. Switching an effect off remembers
    /// its strength; switching it on brings that strength back, or the default the first time.
    /// </summary>
    private Control EffectSection(string name, string title, string description,
        Func<double?> strength, double defaultStrength, Action<double> setStrength, params Control[] settings)
    {
        var toggle = AppearanceToggle(name + "Toggle", title, () => strength() > 0, on =>
        {
            var current = strength() ?? 0;
            if (on == current > 0) return;
            if (on)
            {
                setStrength(_effectMemory.TryGetValue(name, out var remembered) && remembered > 0 ? remembered : defaultStrength);
            }
            else
            {
                _effectMemory[name] = current;
                setStrength(0);
            }
            SyncAppearanceSections();
        });
        ToolTip.SetTip(toggle, description);
        var body = new StackPanel { Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var setting in settings) body.Children.Add(setting);
        _appearanceSyncs.Add(() =>
        {
            var on = strength() > 0;
            body.IsEnabled = on;
            body.Opacity = on ? 1 : 0.55;
        });
        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                toggle,
                new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
                body,
            },
        }.Named(name + "Section");
    }
}
