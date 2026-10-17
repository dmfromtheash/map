using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The assets strip in the top bar (2026-10-17). The owner pointed at Inkarnate's stamp options:
/// while the stamp tool is in hand, the top bar holds icon-labelled number boxes - size,
/// opacity, rotation, sublayer - each with step arrows and a reset (measured there: 24 px icon,
/// 60x32 box, 12 px steps, ~100 px per field). Map's strip follows that layout in its own
/// Candle & Ornament look, adds the blend mode, and edits what the stamp panel edits: the next
/// stamp while placing, the selected stamps while they are selected.
/// </summary>
public sealed partial class EditorView
{
    private Control? _stampStrip;
    private readonly List<Action> _stampStripSyncs = [];

    private const string StripSizeIcon = "M14 4 H20 V10 M20 4 L13.5 10.5 M10 20 H4 V14 M4 20 L10.5 13.5";
    private const string StripOpacityIcon = "M12 3.5 C15.6 7.8 18 10.9 18 13.8 A6 6 0 0 1 6 13.8 C6 10.9 8.4 7.8 12 3.5 Z M12 8 V19.6";
    private const string StripRotationIcon = "M19.5 12 A7.5 7.5 0 1 1 16.8 6.2 M17.4 2.6 V6.8 H13.2";
    private const string StripBlendIcon = "M9.5 6.5 A5.5 5.5 0 1 1 9.5 17.5 A5.5 5.5 0 1 1 9.5 6.5 Z M14.5 6.5 A5.5 5.5 0 1 1 14.5 17.5 A5.5 5.5 0 1 1 14.5 6.5 Z";

    private Control BuildStampStrip()
    {
        var fields = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                StripField("StampStripScale", StripSizeIcon, "Размер ассета", "%", 1, 1000, 5, 100,
                    () => _editor.StampScalePercent, value => _editor.SetStampScalePercent(value)),
                StripField("StampStripOpacity", StripOpacityIcon, "Непрозрачность", "%", 0, 100, 5, 100,
                    () => _editor.StampOpacityPercent, value => _editor.SetStampOpacityPercent(value)),
                StripField("StampStripRotation", StripRotationIcon, "Поворот", "°", 0, 359, 15, 0,
                    () => _editor.StampRotationDegrees, value => _editor.SetStampRotationDegrees(value)),
                StripField("StampStripSublayer", MapIcons.LineLayers, "Подслой: что рисуется поверх внутри слоя", "", -10, 10, 1, 0,
                    () => _editor.StampSublayer, value => _editor.SetStampSublayer((int)Math.Round(value))),
                StripBlend(),
            },
        };
        _stampStrip = new Border
        {
            Name = "StampTopStrip",
            Margin = new Thickness(18, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = fields,
        };
        return _stampStrip;
    }

    /// <summary>Icon, number box, step arrows and reset - one Inkarnate-style field.</summary>
    private Control StripField(
        string name, string icon, string caption, string unit, double min, double max, double step,
        double reset, Func<double?> read, Action<double> write)
    {
        var box = new TextBox
        {
            Name = name + "Box",
            Width = 56,
            MinHeight = 30,
            Height = 30,
            Padding = new Thickness(8, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(box, caption);

        var resetButton = new Button
        {
            Name = name + "Reset",
            Width = 16, Height = 30, MinHeight = 30, Padding = new Thickness(0),
            Content = MapIcons.Stroked(MapIcons.LineReset, 11),
        };
        resetButton.Classes.Add("strip-step");

        void Commit(double value)
        {
            if (read() is null) return;
            write(Math.Clamp(Math.Round(value), min, max));
            Sync();
        }

        void CommitText()
        {
            var text = (box.Text ?? "").Replace(unit, "").Replace(',', '.').Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) Commit(value);
            else Sync();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Up) { Commit((read() ?? reset) + step); e.Handled = true; }
            else if (e.Key == Key.Down) { Commit((read() ?? reset) - step); e.Handled = true; }
            else if (e.Key == Key.Escape) { Sync(); _canvas.Focus(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => CommitText();

        RepeatButton Step(string glyph, double delta, string tip)
        {
            var button = new RepeatButton
            {
                Name = name + (delta > 0 ? "Up" : "Down"),
                Width = 14, Height = 15, MinHeight = 15, Padding = new Thickness(0),
                Content = MapIcons.Stroked(glyph, 9),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Delay = 350, Interval = 80,
            };
            button.Classes.Add("strip-step");
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => Commit((read() ?? reset) + delta);
            return button;
        }

        ToolTip.SetTip(resetButton, $"Сбросить: {reset.ToString(CultureInfo.InvariantCulture)}{unit}");
        resetButton.Click += (_, _) => Commit(reset);

        var iconHost = new Border
        {
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = MapIcons.Stroked(icon, 20),
        };
        ToolTip.SetTip(iconHost, caption);

        void Sync()
        {
            var value = read();
            var enabled = value is not null;
            box.IsEnabled = enabled;
            foreach (var part in new Control[] { resetButton }) part.IsEnabled = enabled;
            if (box.IsKeyboardFocusWithin) return;
            box.Text = value is { } v ? Math.Round(v).ToString(CultureInfo.InvariantCulture) + unit : "—";
        }
        _stampStripSyncs.Add(Sync);

        return new StackPanel
        {
            Name = name,
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                iconHost,
                box,
                new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center,
                    Children = { Step("M6 15 L12 9 L18 15", step, "Больше"), Step("M6 9 L12 15 L18 9", -step, "Меньше") } },
                resetButton,
            },
        };
    }

    private Control StripBlend()
    {
        var keys = BlendModes.Supported.ToArray();
        var combo = new ComboBox
        {
            Name = "StampStripBlend",
            MinWidth = 150,
            Height = 30, MinHeight = 30,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = keys.Select(EditorLabels.Blend).ToArray(),
        };
        AutomationProperties.SetName(combo, "Смешивание ассета");
        var syncing = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (syncing || combo.SelectedIndex < 0 || _editor.StampPanelStyle is null) return;
            var key = keys[combo.SelectedIndex];
            if (key != (_editor.StampPanelStyle.Blend ?? BlendModes.Normal)) _editor.SetStampBlend(key);
        };
        _stampStripSyncs.Add(() =>
        {
            syncing = true;
            var style = _editor.StampPanelStyle;
            combo.IsEnabled = style is not null;
            combo.SelectedIndex = Array.IndexOf(keys, style?.Blend ?? BlendModes.Normal);
            syncing = false;
        });
        var iconHost = new Border { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
            Child = MapIcons.Stroked(StripBlendIcon, 20) };
        ToolTip.SetTip(iconHost, "Смешивание: как ассет ложится на то, что под ним");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { iconHost, combo },
        };
    }

    /// <summary>Shows the strip while assets are in hand or selected and refreshes its numbers.</summary>
    private void SyncStampStrip()
    {
        if (_stampStrip is null) return;
        var show = _editor.ActiveTool == EditorTool.Stamp || _editor.SelectionIsAllStamps;
        _stampStrip.IsVisible = show;
        if (!show) return;
        foreach (var sync in _stampStripSyncs) sync();
    }

    /// <summary>
    /// The big call to action of a creating tool: a framed, lit, full-width command with a
    /// solid glyph, a serif title and a one-line hint, so "where do I start" is never a question.
    /// </summary>
    private static Button HeroButton(string name, string title, string hint, MapIcons.SolidGlyph glyph, Action action)
    {
        var caption = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold }.Classed("hero-title"),
                new TextBlock { Text = hint, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }.Classed("hero-hint"),
            },
        };
        var button = new Button
        {
            Name = name,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // A grid, not a horizontal stack: the hint gets the remaining width and wraps.
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 14,
                Children = { MapIcons.Solid24(glyph, 30), caption },
            },
        };
        Grid.SetColumn(caption, 1);
        button.Classes.Add("create-hero");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title + " — " + hint);
        button.Click += (_, _) => action();
        return Tactile.Attach(button, ornaments: false);
    }
}
