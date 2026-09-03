using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Map.App.Controls;

namespace Map.App.Views;

/// <summary>
/// The shared component vocabulary of Map's interface. Both screens build from exactly these
/// pieces, which is what makes the dashboard and the editor read as one product. Every icon-only
/// control created here is forced to carry a tooltip and an accessible name.
/// </summary>
public static class Ui
{
    public const double RailWidth = 56;
    public const double ContextPanelWidth = 264;
    public const double SidePanelWidth = 300;
    private static readonly Lazy<Bitmap> WatercolorBitmap = new(() =>
    {
        using var stream = AssetLoader.Open(
            new Uri("avares://Map.App/Assets/BlueWatercolorBackdrop.png"));
        return new Bitmap(stream);
    });

    // ------------------------------------------------------------------ basics

    /// <summary>
    /// Real approved watercolor bitmap beneath an arbitrary surface. Loading the embedded asset
    /// synchronously avoids a first-frame flash in the native off-screen and desktop windows.
    /// </summary>
    public static Grid WatercolorBackdrop(Control content) => new()
    {
        Background = new ImageBrush(WatercolorBitmap.Value)
        {
            Stretch = Stretch.UniformToFill,
        },
        Children = { content },
    };

    public static T Named<T>(this T control, string name) where T : Control
    {
        control.Name = name;
        return control;
    }

    public static T Classed<T>(this T control, params string[] classes) where T : StyledElement
    {
        foreach (var value in classes)
        {
            control.Classes.Add(value);
        }

        return control;
    }

    public static T Docked<T>(this T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    public static void SetClass(StyledElement element, string className, bool enabled)
    {
        element.Classes.Remove(className);
        if (enabled)
        {
            element.Classes.Add(className);
        }
    }

    /// <summary>
    /// A label that never breaks a layout: long Russian map, layer and object names are trimmed
    /// with an ellipsis and the full value moves into the tooltip.
    /// </summary>
    public static TextBlock Trimmed(string text, params string[] classes)
    {
        var block = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed(classes);

        if (!string.IsNullOrWhiteSpace(text))
        {
            ToolTip.SetTip(block, text);
        }

        return block;
    }

    // ----------------------------------------------------------------- buttons

    public static Button TextButton(string name, string text, Action action, params string[] classes)
    {
        var button = new Button { Content = text }.Named(name).Classed(classes);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    public static Button AsyncButton(
        string name, string text, Func<System.Threading.Tasks.Task> action, params string[] classes)
    {
        var button = new Button { Content = text }.Named(name).Classed(classes);
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>Asynchronous label-and-icon command with the same semantics as CommandButton.</summary>
    public static Button AsyncCommandButton(
        string name, string text, string icon, Func<System.Threading.Tasks.Task> action, params string[] classes)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    MapIcons.Stroked(icon, 16),
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
        }.Named(name).Classed(classes);

        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>
    /// An icon-only control. The accessible name and the tooltip are mandatory arguments, so a
    /// nameless icon button cannot be created by accident.
    /// </summary>
    public static Button IconButton(string name, string accessibleName, string icon, Action action)
    {
        var button = new Button { Content = MapIcons.Stroked(icon, 17) }.Named(name).Classed("icon");
        AutomationProperties.SetName(button, accessibleName);
        ToolTip.SetTip(button, accessibleName);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A label-and-icon command used inside panels and popovers.</summary>
    public static Button CommandButton(
        string name, string text, string icon, Action action, params string[] classes)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { MapIcons.Stroked(icon, 16), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
        }.Named(name).Classed(classes);

        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    /// A tool rail button. Active state is carried by three independent signals - fill, border and
    /// the marker bar on the leading edge - so it survives greyscale and colour-blind viewing.
    /// </summary>
    public static Button ToolButton(string name, string title, string icon, string shortcut, Action action)
    {
        var marker = new Border
        {
            Width = 3,
            Height = 20,
            CornerRadius = new CornerRadius(0, 2, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            IsVisible = false,
        }.Named(name + "Marker");

        var button = new ToggleButton
        {
            Content = new Panel
            {
                Children = { marker, MapIcons.Stroked(icon, 21) },
            },
        }.Named(name).Classed("tool-button");

        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, shortcut.Length > 0 ? $"{title}  ({shortcut})" : title);
        button.Click += (_, _) =>
        {
            action();
            // The active tool is a mode, not an on/off command. Clicking it again must not
            // publish an unchecked state to UI Automation.
            button.IsChecked = true;
        };
        return button;
    }

    public static void SetToolActive(Button button, bool active)
    {
        SetClass(button, "active", active);
        if (button is ToggleButton toggle)
        {
            toggle.IsChecked = active;
        }

        if (button.Content is Panel panel && panel.Children.Count > 0 && panel.Children[0] is Border marker)
        {
            marker.IsVisible = active;
            marker.Background = active
                ? button.FindResource("MapAccentBrush") as IBrush ?? Brushes.Transparent
                : Brushes.Transparent;
        }
    }

    // -------------------------------------------------------------- structure

    /// <summary>The header strip that titles a panel and optionally carries trailing controls.</summary>
    public static Border PanelHeader(string name, string title, params Control[] trailing)
    {
        var text = Trimmed(title, "role-panel").Named(name);
        var panel = new DockPanel();

        if (trailing.Length > 0)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            group.Children.AddRange(trailing);
            panel.Children.Add(group.Docked(Dock.Right));
        }

        panel.Children.Add(text);
        return new Border { Child = panel }.Classed("panel-header");
    }

    /// <summary>A titled block inside a panel. Titles are sentence case and never shout.</summary>
    public static Control Section(string title, Control content, params string[] classes)
    {
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title }.Classed("role-panel"),
                content,
            },
        }.Classed(classes);
    }

    /// <summary>
    /// The disclosure that holds the rare settings of a tool. Common parameters stay visible;
    /// everything a user touches once a month lives behind this one collapsed row.
    /// </summary>
    public static Expander Advanced(string name, Control content)
    {
        var expander = new Expander
        {
            Header = new TextBlock { Text = "Дополнительно" }.Classed("role-panel"),
            Content = content,
            IsExpanded = false,
        }.Named(name);

        AutomationProperties.SetName(expander, "Дополнительно");
        return expander;
    }

    /// <summary>Label on the left, editor on the right - the single inspector row shape.</summary>
    public static Control PropertyRow(string label, Control editor, string? unit = null, double captionWidth = 92)
    {
        var caption = ParameterLabel(label, captionWidth);
        ToolTip.SetTip(caption, label);
        AutomationProperties.SetName(editor, unit is null ? label : $"{label}, {unit}");

        var row = new DockPanel { Margin = new Thickness(0, 2) };
        if (unit is not null)
        {
            row.Children.Add(new TextBlock
            {
                Text = unit,
                Width = 22,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.Classed("faint").Docked(Dock.Right));
        }

        row.Children.Add(caption.Docked(Dock.Left));
        row.Children.Add(editor);
        return row;
    }

    /// <summary>A named slider with a live readout, used by every tool parameter.</summary>
    public static Control SliderRow(
        string name, string label, double minimum, double maximum, double value,
        string unit, Action<double> changed, double step = 1)
    {
        var readout = new TextBlock
        {
            Text = Format(value, unit),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
        }.Classed("muted").Named(name + "Value");

        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            SmallChange = step,
            LargeChange = step * 5,
        }.Named(name);
        AutomationProperties.SetName(slider, label);
        ToolTip.SetTip(slider, $"{label}. Стрелками — на {Format(step, unit)}");

        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                readout.Text = Format(slider.Value, unit);
                changed(slider.Value);
            }
        };

        return new StackPanel
        {
            Margin = new Thickness(0, 2),
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        readout.Docked(Dock.Right),
                        ParameterLabel(label),
                    },
                },
                slider,
            },
        };

        static string Format(double raw, string suffix) =>
            suffix.Length == 0
                ? Math.Round(raw).ToString("0")
                : $"{Math.Round(raw):0} {suffix}";
    }

    /// <summary>
    /// One row of mutually exclusive choices. Returns the buttons for state syncing.
    /// <para>
    /// The strip wraps. It used to be a horizontal <see cref="StackPanel"/> inside a fixed-width
    /// panel with horizontal scrolling switched off, so a set with long Russian labels - the six
    /// Shape modes - simply ran off the edge and its last options could not be clicked at all.
    /// A mode the user cannot reach is a missing feature, not a cosmetic overflow.
    /// </para>
    /// </summary>
    public static (Control Control, IReadOnlyList<Button> Buttons) Segmented(
        string namePrefix, IReadOnlyList<(string Key, string Text, string? Icon)> options, Action<string> chosen)
    {
        var buttons = new List<Button>(options.Count);
        var strip = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var option in options)
        {
            object content = option.Icon is null
                ? option.Text
                : new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        MapIcons.Stroked(option.Icon, 15),
                        new TextBlock { Text = option.Text, VerticalAlignment = VerticalAlignment.Center },
                    },
                };

            var button = new ToggleButton { Content = content, Margin = new Thickness(0, 0, 2, 2) }
                .Named(namePrefix + option.Key)
                .Classed("segment");
            AutomationProperties.SetName(button, option.Text);
            ToolTip.SetTip(button, option.Text);

            var key = option.Key;
            button.Click += (_, _) => chosen(key);
            buttons.Add(button);
            strip.Children.Add(button);
        }

        var host = new Border
        {
            Background = Application.Current?.FindResource("MapFieldBrush") as IBrush,
            BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(2, 2, 0, 0),
            Child = strip,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        return (host, buttons);
    }

    /// <summary>Icon-only variant of the segmented control, for shape and mode pickers.</summary>
    public static (Control Control, IReadOnlyList<Button> Buttons) IconSegmented(
        string namePrefix, IReadOnlyList<(string Key, string Title, string Icon)> options, Action<string> chosen)
    {
        var buttons = new List<Button>(options.Count);
        var strip = new WrapPanel { ItemWidth = 34, ItemHeight = 32 };

        foreach (var option in options)
        {
            var button = new Button { Content = MapIcons.Stroked(option.Icon, 17) }
                .Named(namePrefix + option.Key)
                .Classed("segment");
            AutomationProperties.SetName(button, option.Title);
            ToolTip.SetTip(button, option.Title);

            var key = option.Key;
            button.Click += (_, _) => chosen(key);
            buttons.Add(button);
            strip.Children.Add(button);
        }

        return (strip, buttons);
    }

    // --------------------------------------------------------------- list rows

    /// <summary>
    /// The one row shape shared by layers, objects, assets and notes: a kind icon, a trimmed
    /// primary label, a muted secondary label, and state badges that stay visible without hover.
    /// </summary>
    public static Button ListRow(
        string name,
        string icon,
        string primary,
        string? secondary,
        IReadOnlyList<(string Icon, string Title)> states,
        Action activate,
        string? tooltip = null)
    {
        var content = new DockPanel { LastChildFill = true };

        if (states.Count > 0)
        {
            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            foreach (var state in states)
            {
                var glyph = MapIcons.Stroked(state.Icon, 14);
                var badge = new Border { Child = glyph, Padding = new Thickness(3, 2) }.Classed("badge");
                ToolTip.SetTip(badge, state.Title);
                AutomationProperties.SetName(badge, state.Title);
                badges.Children.Add(badge);
            }

            content.Children.Add(badges.Docked(Dock.Right));
        }

        if (!string.IsNullOrWhiteSpace(secondary))
        {
            content.Children.Add(new TextBlock
            {
                Text = secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            }.Classed("faint", "role-caption").Docked(Dock.Right));
        }

        content.Children.Add(new Border
        {
            Width = 20,
            Child = MapIcons.Stroked(icon, 16),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        }.Docked(Dock.Left));

        content.Children.Add(Trimmed(primary));

        var row = new ToggleButton { Content = content }.Named(name).Classed("list-row");
        AutomationProperties.SetName(row, tooltip ?? primary);
        ToolTip.SetTip(row, tooltip ?? primary);
        row.Click += (_, _) =>
        {
            activate();
            row.IsChecked = true;
        };
        return row;
    }

    /// <summary>Synchronises a selectable row with both its visual class and UIA TogglePattern.</summary>
    public static void SetSelected(Button button, bool selected)
    {
        SetClass(button, "selected", selected);
        if (button is ToggleButton toggle)
        {
            toggle.IsChecked = selected;
        }
    }

    /// <summary>
    /// A property whose editor is a button showing the setting's current value. Cycling controls
    /// must never be a verb-only button: the user has to see what the value is now before deciding
    /// whether to change it.
    /// </summary>
    public static (Control Row, Button Button) ValueRow(
        string name, string label, string value, Action action)
    {
        var text = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Named(name + "Text");

        // A trailing chevron is what separates "this is a control you can change" from "this is a
        // read-only field", which matters because the value itself is the label.
        var button = new Button
        {
            Content = new DockPanel
            {
                Children =
                {
                    MapIcons.Stroked(MapIcons.ChevronDown, 13).Docked(Dock.Right),
                    text,
                },
            },
        }.Named(name).Classed("value");

        AutomationProperties.SetName(button, $"{label}: {value}");
        ToolTip.SetTip(button, $"{label}: {value}. Нажмите, чтобы сменить.");
        button.Click += (_, _) => action();

        var caption = ParameterLabel(label, 92);
        ToolTip.SetTip(caption, label);

        var row = new DockPanel
        {
            Margin = new Thickness(0, 2),
            Children = { caption.Docked(Dock.Left), button },
        };

        return (row, button);
    }

    /// <summary>
    /// Inspector caption with the approved semantic glyph when the label describes a known
    /// parameter. The text remains present: icons aid scanning but never replace meaning.
    /// </summary>
    public static Control ParameterLabel(string label, double? width = null)
    {
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Classed("muted");

        var icon = MapIcons.ForProperty(label);
        if (icon is null)
        {
            if (width is { } plainWidth) text.Width = plainWidth;
            return text;
        }

        var caption = new DockPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 20,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = MapIcons.Stroked(
                        icon, 14, Application.Current?.FindResource("MapMutedBrush") as IBrush),
                }.Docked(Dock.Left),
                text,
            },
        };
        if (width is { } captionWidth) caption.Width = captionWidth;
        return caption;
    }

    /// <summary>Updates a value button in place, keeping its accessible name truthful.</summary>
    public static void SetValue(Button button, string label, string value)
    {
        if (button.Content is DockPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is TextBlock text)
                {
                    text.Text = value;
                }
            }
        }

        AutomationProperties.SetName(button, $"{label}: {value}");
        ToolTip.SetTip(button, $"{label}: {value}. Нажмите, чтобы сменить.");
    }

    /// <summary>A trailing toggle that lives inside a structure row.</summary>
    public static Button RowAction(string name, string title, string icon, Action action)
    {
        var button = new ToggleButton { Content = MapIcons.Stroked(icon, 15) }.Named(name).Classed("row-action");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => action();
        return button;
    }

    public static void SetRowAction(Button button, string title, bool on)
    {
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        SetClass(button, "on", on);
        if (button is ToggleButton toggle)
        {
            toggle.IsChecked = on;
        }
    }

    /// <summary>
    /// A structure row with its own controls: the wide part selects the item, the trailing part
    /// acts on it. Each piece is separately focusable, so the whole row works from the keyboard.
    /// </summary>
    public static Border StructureRow(
        string name, string icon, string primary, string? secondary,
        Action activate, string tooltip, params Button[] actions)
    {
        var label = new DockPanel { LastChildFill = true };

        if (!string.IsNullOrWhiteSpace(secondary))
        {
            label.Children.Add(new TextBlock
            {
                Text = secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 4, 0),
            }.Classed("faint", "role-caption").Docked(Dock.Right));
        }

        label.Children.Add(new Border
        {
            Width = 20,
            Child = MapIcons.Stroked(icon, 16),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        }.Docked(Dock.Left));

        label.Children.Add(Trimmed(primary));

        var select = new ToggleButton { Content = label }.Named(name).Classed("list-row");
        AutomationProperties.SetName(select, tooltip);
        ToolTip.SetTip(select, tooltip);
        select.Click += (_, _) =>
        {
            activate();
            select.IsChecked = true;
        };

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 3, 0),
        };
        trailing.Children.AddRange(actions);

        return new Border
        {
            Child = new DockPanel { Children = { trailing.Docked(Dock.Right), select } },
        }.Classed("row-host");
    }

    /// <summary>Synchronises a composite structure row without nesting its trailing actions.</summary>
    public static void SetStructureSelected(Border row, bool selected)
    {
        SetClass(row, "selected", selected);
        if (row.Child is DockPanel panel
            && panel.Children.Count > 0
            && panel.Children[panel.Children.Count - 1] is ToggleButton toggle)
        {
            toggle.IsChecked = selected;
        }
    }

    // ------------------------------------------------------------------ states

    /// <summary>Empty state: says what is missing and what to do, never just "нет данных".</summary>
    public static Control EmptyState(string name, string icon, string title, string hint)
    {
        return new Border
        {
            Padding = new Thickness(16, 22),
            Child = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Child = MapIcons.Stroked(
                            icon, 28, Application.Current?.FindResource("MapFaintBrush") as IBrush),
                    },
                    new TextBlock
                    {
                        Text = title,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    }.Classed("muted"),
                    new TextBlock
                    {
                        Text = hint,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    }.Classed("faint", "role-caption"),
                },
            },
        }.Named(name);
    }

    /// <summary>A key chip used by the tool hint bar.</summary>
    public static Control KeyChip(string text) => new Border
    {
        Child = new TextBlock { Text = text, FontSize = 11 }.Classed("muted"),
        VerticalAlignment = VerticalAlignment.Center,
    }.Classed("key");

    public static Control Spacer(double height) => new Border { Height = height };
}
