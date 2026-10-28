using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Map.App.Controls;

namespace Map.App.Views;

/// <summary>
/// The shared component vocabulary of Map's interface. Both screens build from exactly these
/// pieces, which is what makes the dashboard and the editor read as one product. Every icon-only
/// control created here is forced to carry a tooltip and an accessible name.
/// </summary>
public static class Ui
{
    // Workspace geometry measured in Inkarnate through REA/CDP at 2560×1343 (2026-10-16): a 48 px
    // floating rail of 46×48 tool buttons, a 289 px tool panel that floats over the canvas 12 px
    // below the top bar, a 240 px layer panel. Colours stay Map's own.
    // DEC-070: 68 px since the rail names its tools under the medallions (48 px before, icons only).
    public const double RailWidth = 68;
    public const double ContextPanelWidth = 289;
    public const double SidePanelWidth = 240;
    /// <summary>Gap between the top chrome and the floating rail / tool panel.</summary>
    public const double FloatingGap = 12;

    /// <summary>A calm, opaque workspace surface shared by every native window.</summary>
    public static Grid WorkspaceBackdrop(Control content) => new Grid
    {
        Children = { content },
    }.Classed("workspace");

    public static T Named<T>(this T control, string name) where T : Control
    {
        control.Name = name;
        return control;
    }

    /// <summary>Sets the tooltip and returns the control, for use inside an initializer.</summary>
    public static T WithTip<T>(this T control, string tip) where T : Control
    {
        ToolTip.SetTip(control, tip);
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
        // Only a real change touches the class list. Removing and re-adding an unchanged class made
        // Avalonia re-match the element's styles twice and validate the name with a culture-aware
        // comparison each time: hundreds of times per panel refresh, 370 ms of UI-thread time in a
        // 90-step cliff slider drag on the owner's test map (2026-10-18).
        if (element.Classes.Contains(className) == enabled)
        {
            return;
        }

        if (enabled)
        {
            element.Classes.Add(className);
        }
        else
        {
            element.Classes.Remove(className);
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
        var button = new Button { Content = CommandCaption(text) }.Named(name).Classed(classes);
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    public static Button AsyncButton(
        string name, string text, Func<System.Threading.Tasks.Task> action, params string[] classes)
    {
        var button = new Button { Content = CommandCaption(text) }.Named(name).Classed(classes);
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
            Content = CommandContent(text, icon),
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
            Content = CommandContent(text, icon),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        }.Named(name).Classed(classes);

        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock CommandCaption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Renames a command button built by <see cref="CommandButton"/>, caption and accessible name together.</summary>
    public static void SetCommandText(Button button, string text)
    {
        if (button.Content is Grid grid && grid.Children.OfType<TextBlock>().FirstOrDefault() is { } caption)
            caption.Text = text;
        AutomationProperties.SetName(button, text);
    }

    /// <summary>An independent setting, with a persistent checkbox rather than a mode face.</summary>
    public static ToggleButton PropertyToggle(string name, string label)
    {
        var check = new TextBlock { Text = "✓", IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var caption = CommandCaption(label);
        Grid.SetColumn(caption, 1);
        var toggle = new ToggleButton { Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("20,*"), ColumnSpacing = 8,
            Children = { new Border { Width = 18, Height = 18, BorderThickness = new Thickness(1),
                BorderBrush = Application.Current?.FindResource("MapFieldBorderBrush") as IBrush,
                CornerRadius = new CornerRadius(3), Child = check, VerticalAlignment = VerticalAlignment.Center }, caption },
        } }.Named(name).Classed("property-toggle");
        AutomationProperties.SetName(toggle, label);
        ToolTip.SetTip(toggle, label);
        toggle.IsCheckedChanged += (_, _) => check.IsVisible = toggle.IsChecked == true;
        return toggle;
    }

    private static Control CommandContent(string text, string icon)
    {
        var caption = CommandCaption(text);
        Grid.SetColumn(caption, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8,
            Children = { MapIcons.Stroked(icon, 16), caption },
        };
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
                Children = { marker, MapIcons.Stroked(icon, 32) },
            },
        }.Named(name).Classed("tool-button");

        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, shortcut.Length > 0 ? $"{title}  ({shortcut})" : title);
        button.Click += (_, _) =>
        {
            action();
            // The active tool is a mode, not an on/off command. Clicking it again must not
            // expose an unchecked state to UI Automation.
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
        return new Border
        {
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }.Classed("role-panel"),
                    content,
                },
            },
        }.Classed("property-group").Classed(classes);
    }

    /// <summary>
    /// One named category of a settings panel: a coloured stripe, a title, and everything that
    /// belongs to that part of the work, foldable away when it does not.
    /// <para>
    /// The panels grew past four screens of controls in one flat column, which is unreadable
    /// however good each control is. A category answers "what am I doing" before it answers
    /// "which number is this", and the same four accents mean the same four things in every
    /// panel: green makes new things, blue is what is selected, amber is shape, violet is looks.
    /// </para>
    /// </summary>
    /// <summary>One tab of <see cref="CategoryTabs"/>: its key, caption, line glyph and page.</summary>
    /// <param name="Prefer">The tab opens by itself when its page appears (a note chosen in the list).</param>
    public sealed record CategoryTab(string Key, string Title, string Icon, Control Page, string? Tip = null, bool Prefer = false);

    /// <summary>
    /// Tabs with icons over the categories of a tool panel, one category on screen at a time: the
    /// land, river and road panels had grown to five screens of folded categories one under another
    /// (audit 2026-10-19). A tab is offered while its page is visible (the panel's own logic still
    /// shows or hides a category by scope); a category used as a page loses its own header and
    /// stays unfolded. A hidden page names its tab in its host's Tag (<c>Reveal:&lt;button&gt;</c>),
    /// so automation can open it the way a user would.
    /// </summary>
    public static Control CategoryTabs(string name, params CategoryTab[] tabs)
    {
        var strip = new UniformGrid { Rows = 1 }.Named(name + "Strip");
        var stripHost = new Border { Child = strip, HorizontalAlignment = HorizontalAlignment.Stretch }.Classed("mode-group");
        var buttons = new ToggleButton[tabs.Length];
        var hosts = new Border[tabs.Length];
        var selected = 0;
        var showing = false;
        var wasAvailable = tabs.Select(t => t.Page.IsVisible).ToArray();

        void Show()
        {
            if (showing) return;
            showing = true;
            try
            {
                var available = tabs.Select(t => t.Page.IsVisible).ToArray();
                for (var i = 0; i < tabs.Length; i++)
                {
                    if (tabs[i].Prefer && available[i] && !wasAvailable[i]) selected = i;
                }

                wasAvailable = available;
                if (!available[selected] && Array.IndexOf(available, true) is var first and >= 0) selected = first;
                strip.Children.Clear();
                for (var i = 0; i < tabs.Length; i++)
                {
                    if (available[i]) strip.Children.Add(buttons[i]);
                    var active = i == selected;
                    buttons[i].IsChecked = active;
                    SetClass(buttons[i], "active", active);
                    hosts[i].IsVisible = active && available[i];
                    if (tabs[i].Page is Expander { IsExpanded: false } folded && active) folded.IsExpanded = true;
                }

                strip.Columns = Math.Max(1, strip.Children.Count);
                stripHost.IsVisible = strip.Children.Count > 1;
            }
            finally
            {
                showing = false;
            }
        }

        var panel = new StackPanel { Spacing = 8 }.Named(name);
        panel.Children.Add(stripHost);
        for (var i = 0; i < tabs.Length; i++)
        {
            var index = i;
            var tab = tabs[i];
            var button = new ToggleButton
            {
                Content = new StackPanel
                {
                    Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = MapIcons.Grid24(tab.Icon, 19) },
                        new TextBlock { Text = tab.Title, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
                HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(1, 4), Margin = new Thickness(1, 0), MinHeight = 44,
            }.Named(name + tab.Key).Classed("segment", "mode-choice");
            AutomationProperties.SetName(button, tab.Title);
            ToolTip.SetTip(button, tab.Tip ?? tab.Title);
            button.Click += (_, _) => { selected = index; Show(); };
            buttons[i] = button;
            if (tab.Page is Expander category)
            {
                category.Classes.Add("tabbed");
                category.IsExpanded = true;
            }

            tab.Page.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Show(); };
            hosts[i] = new Border { Child = tab.Page, Tag = "Reveal:" + button.Name };
            panel.Children.Add(hosts[i]);
        }

        Show();
        return panel;
    }

    public static Expander Category(string name, string title, string accent, Control content, bool expanded = true)
    {
        var header = new DockPanel { LastChildFill = true };
        header.Children.Add(new Border
        {
            Width = 4,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed("category-accent", accent).Docked(Dock.Left));
        header.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
        }.Classed("role-panel"));

        var expander = new Expander
        {
            Header = header,
            Content = new Border { Padding = new Thickness(0, 6, 0, 0), Child = content },
            IsExpanded = expanded,
        }.Named(name).Classed("category", accent);
        AutomationProperties.SetName(expander, title);
        return expander;
    }

    /// <summary>
    /// A labelled block inside a category. It carries a caption and nothing else - no card, no
    /// second background - so nesting stays readable instead of turning into boxes in boxes.
    /// </summary>
    public static Control Subgroup(string title, Control content) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption", "subgroup-title"),
            content,
        },
    };

    /// <summary>One muted line of guidance under a category header; never a paragraph.</summary>
    public static Control Hint(string text) =>
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");

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
        var caption = ParameterLabel(label);
        ToolTip.SetTip(caption, label);
        AutomationProperties.SetName(editor, unit is null ? label : $"{label}, {unit}");

        var field = new DockPanel();
        if (unit is not null)
        {
            field.Children.Add(new TextBlock
            {
                Text = unit,
                Width = 22,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.Classed("faint").Docked(Dock.Right));
        }

        field.Children.Add(editor);
        var editorWidth = double.IsNaN(editor.Width) ? editor.MinWidth : Math.Max(editor.Width, editor.MinWidth);
        if (editor is ComboBox or Button or InlineChoice) editorWidth = Math.Max(180, editorWidth);
        return new PropertyRowPanel(caption, field, Math.Max(112, captionWidth),
            Math.Max(112, editorWidth + (unit is null ? 0 : 28)))
        { Margin = new Thickness(0, 2) }.Classed("property-row");
    }

    /// <summary>Choose the form layout while measuring, before children can be arranged outside it.</summary>
    private sealed class PropertyRowPanel : Panel
    {
        private const double ColumnGap = 8;
        private const double RowGap = 4;
        private readonly Control _caption;
        private readonly Control _field;
        private readonly double _captionWidth;
        private readonly double _minimumFieldWidth;

        public PropertyRowPanel(Control caption, Control field, double captionWidth, double minimumFieldWidth)
        {
            _caption = caption;
            _field = field;
            _captionWidth = captionWidth;
            _minimumFieldWidth = minimumFieldWidth;
            Children.Add(caption);
            Children.Add(field);
        }

        private bool Stacked(double width) => width < _captionWidth + ColumnGap + _minimumFieldWidth;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsFinite(availableSize.Width)
                ? availableSize.Width : _captionWidth + ColumnGap + _minimumFieldWidth;
            var stacked = Stacked(width);
            _caption.Measure(new Size(stacked ? width : _captionWidth, double.PositiveInfinity));
            _field.Measure(new Size(stacked ? width : Math.Max(0, width - _captionWidth - ColumnGap),
                double.PositiveInfinity));
            return new Size(width, stacked
                ? _caption.DesiredSize.Height + RowGap + _field.DesiredSize.Height
                : Math.Max(_caption.DesiredSize.Height, _field.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Stacked(finalSize.Width))
            {
                var captionHeight = _caption.DesiredSize.Height;
                _caption.Arrange(new Rect(0, 0, finalSize.Width, captionHeight));
                _field.Arrange(new Rect(0, captionHeight + RowGap, finalSize.Width,
                    Math.Max(0, finalSize.Height - captionHeight - RowGap)));
            }
            else
            {
                _caption.Arrange(new Rect(0, 0, _captionWidth, finalSize.Height));
                _field.Arrange(new Rect(_captionWidth + ColumnGap, 0,
                    Math.Max(0, finalSize.Width - _captionWidth - ColumnGap), finalSize.Height));
            }
            return finalSize;
        }
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
    /// Mutually exclusive choices in equal cells; narrow panels get complete, evenly spaced rows.
    /// </summary>
    public static (Control Control, IReadOnlyList<Button> Buttons) Segmented(
        string namePrefix, IReadOnlyList<(string Key, string Text, string? Icon)> options, Action<string> chosen)
    {
        var buttons = new List<Button>(options.Count);
        var strip = new ChoiceGrid
        {
            MaxColumns = options.Count == 4 ? 4 : 3,
            MinimumItemWidth = options.Count <= 3 ? 60 : 80,
            Gap = 4,
        };

        foreach (var option in options)
        {
            object content = option.Icon is null ? CommandCaption(option.Text) : CommandContent(option.Text, option.Icon);

            var button = new ToggleButton { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch }
                .Named(namePrefix + option.Key)
                .Classed("segment", "mode-choice");
            AutomationProperties.SetName(button, option.Text);
            ToolTip.SetTip(button, option.Text);

            var key = option.Key;
            button.Click += (_, _) => chosen(key);
            buttons.Add(button);
            strip.Children.Add(button);
        }

        // Colours, border and padding come from the "mode-group" style, so a calm panel can
        // restyle its choices; a local value here would outrank every style.
        var host = new Border
        {
            Child = strip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        host.Classes.Add("mode-group");
        return (host, buttons);
    }

    /// <summary>
    /// One row of mutually exclusive modes, each a line glyph over a short caption. Used where
    /// the mode decides what a click on the map does, so it sits first and is read at a glance.
    /// </summary>
    public static (Control Control, IReadOnlyList<Button> Buttons) TallSegmented(
        string namePrefix, IReadOnlyList<(string Key, string Text, string Icon)> options, Action<string> chosen)
    {
        var buttons = new List<Button>(options.Count);
        // Always one row: equal columns, captions trim instead of wrapping the strip.
        var strip = new Avalonia.Controls.Primitives.UniformGrid { Rows = 1, Columns = options.Count };
        foreach (var option in options)
        {
            var button = new ToggleButton
            {
                Content = new StackPanel
                {
                    Spacing = 2,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = MapIcons.Grid24(option.Icon, 19) },
                        new TextBlock { Text = option.Text, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(1, 4),
                Margin = new Thickness(1, 0),
                MinHeight = 44,
            }.Named(namePrefix + option.Key).Classed("segment", "mode-choice");
            AutomationProperties.SetName(button, option.Text);
            ToolTip.SetTip(button, option.Text);
            var key = option.Key;
            button.Click += (_, _) => chosen(key);
            buttons.Add(button);
            strip.Children.Add(button);
        }
        return (new Border { Child = strip, HorizontalAlignment = HorizontalAlignment.Stretch }.Classed("mode-group"), buttons);
    }

    // ---------------------------------------------------------- scope and sections

    /// <summary>Who a block acts on. The four kinds keep one colour each, always with the word.</summary>
    public enum Scope { New, Object, Layer, Tool }

    public static Border ScopeChip(Scope scope, string? text = null)
    {
        var (css, icon, word, colour) = scope switch
        {
            Scope.New => ("scope-new", MapIcons.LineSparkle, "Для новых", "#E7C982"),
            Scope.Object => ("scope-object", MapIcons.LineTarget, "Выделенное", "#9ED4B0"),
            Scope.Layer => ("scope-layer", MapIcons.LineLayers, "Слой", "#D6B3DE"),
            _ => ("scope-tool", MapIcons.LineWand, "Инструмент", "#C2AE8E"),
        };
        var label = new TextBlock { Text = text ?? word };
        var chip = new Border
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { MapIcons.Grid24(icon, 12, new SolidColorBrush(Color.Parse(colour)), 2.2), label },
            },
        }.Classed("scope-chip", css);
        AutomationProperties.SetName(chip, "Область действия: " + label.Text);
        ToolTip.SetTip(chip, scope switch
        {
            Scope.New => "Эти настройки применяются к следующему создаваемому объекту.",
            Scope.Object => "Эти настройки меняют выбранный на карте объект.",
            Scope.Layer => "Эти настройки действуют на весь слой.",
            _ => "Эти настройки определяют, как работает инструмент.",
        });
        return chip;
    }

    /// <summary>
    /// A foldable section of a side panel. Folded, it says what it holds on the right; when any
    /// setting inside differs from its base value, a dot and the name of the change stay visible,
    /// so collapsing a section never hides what the next click will do.
    /// </summary>
    public sealed class SectionPanel : Expander
    {
        private readonly TextBlock _summary = new TextBlock().Classed("section-summary");
        private readonly TextBlock _change = new TextBlock { MaxWidth = 120 }.Classed("section-change");
        private readonly Avalonia.Controls.Shapes.Ellipse _dot = new Avalonia.Controls.Shapes.Ellipse { IsVisible = false }.Classed("change-dot");
        private string _summaryText = "";
        private string? _changeText;

        protected override Type StyleKeyOverride => typeof(Expander);

        public SectionPanel(string name, string title, Control content, bool expanded, Scope? scope = null)
        {
            var titleText = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis }.Classed("role-panel");
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { titleText } };
            if (scope is { } kind) left.Children.Add(ScopeChip(kind));
            left.Children.Add(_dot);
            left.Children.Add(_change);
            _summary.Margin = new Thickness(8, 0, 0, 0);
            _summary.HorizontalAlignment = HorizontalAlignment.Right;
            _summary.TextAlignment = TextAlignment.Right;
            // Title first, summary in the remaining width: a long summary trims, it never overlaps.
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { left, _summary } };
            Grid.SetColumn(_summary, 1);
            Header = header;
            Content = new Border { Padding = new Thickness(0, 4, 0, 0), Child = content };
            IsExpanded = expanded;
            Name = name;
            Classes.Add("section");
            AutomationProperties.SetName(this, title);
            PropertyChanged += (_, e) => { if (e.Property == IsExpandedProperty) Refresh(); };
            Refresh();
        }

        public void SetSummary(string text) { _summaryText = text; Refresh(); }

        /// <summary>Null or empty: nothing inside differs from the base values.</summary>
        public void SetChange(string? text) { _changeText = string.IsNullOrWhiteSpace(text) ? null : text; Refresh(); }

        private void Refresh()
        {
            var folded = !IsExpanded;
            _dot.IsVisible = _changeText is not null;
            _change.Text = folded ? _changeText ?? "" : "";
            _change.IsVisible = folded && _changeText is not null;
            _summary.Text = _summaryText;
            _summary.IsVisible = folded && _changeText is null && _summaryText.Length > 0;
            var tip = _changeText is null ? _summaryText : "Изменено: " + _changeText;
            if (tip.Length > 0) ToolTip.SetTip(this, tip);
        }
    }

    /// <summary>
    /// A rail tool with its short caption under the glyph. Active state keeps three signals:
    /// the light tile, the accent colour and the side marker.
    /// </summary>
    public static Button RailToolButton(string name, string title, string caption, MapIcons.SolidGlyph icon,
        string shortcut, Action action)
    {
        var marker = new Border
        {
            Width = 3, Height = 22,
            CornerRadius = new CornerRadius(0, 2, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-4, 0, 0, 0),
            Background = Brushes.Transparent,
            IsVisible = false,
        }.Named(name + "Marker");
        var glyph = MapIcons.Solid24(icon, 17);
        glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
        var body = new StackPanel
        {
            Spacing = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                // DEC-070: the brass medallion with the glyph, and the tool's name under it (hidden in a compact window).
                new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = glyph }.Classed("rail-medallion"),
                new TextBlock { Text = caption }.Classed("rail-caption"),
            },
        };
        var button = new ToggleButton
        {
            Content = new Panel { Children = { marker, body } },
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        }.Named(name).Classed("tool-button", "rail-tool");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, shortcut.Length > 0 ? $"{title}  ({shortcut})" : title);
        // The rail has no captions any more, so its names come out beside the button and fast.
        ToolTip.SetPlacement(button, PlacementMode.Right);
        ToolTip.SetHorizontalOffset(button, 6);
        ToolTip.SetShowDelay(button, 250);
        button.Click += (_, _) =>
        {
            action();
            button.IsChecked = true;
        };
        return button;
    }

    /// <summary>Icon-only variant of the segmented control, for shape and mode pickers.</summary>
    public static (Control Control, IReadOnlyList<Button> Buttons) IconSegmented(
        string namePrefix, IReadOnlyList<(string Key, string Title, string Icon)> options, Action<string> chosen)
    {
        var buttons = new List<Button>(options.Count);
        var strip = new ChoiceGrid { MaxColumns = 3, MinimumItemWidth = 34, Gap = 6 };

        foreach (var option in options)
        {
            var button = new Button { Content = MapIcons.Stroked(option.Icon, 17) }
                .Named(namePrefix + option.Key)
                .Classed("segment", "mode-choice");
            AutomationProperties.SetName(button, option.Title);
            ToolTip.SetTip(button, option.Title);

            var key = option.Key;
            button.Click += (_, _) => chosen(key);
            buttons.Add(button);
            strip.Children.Add(button);
        }

        return (strip, buttons);
    }

    /// <summary>Equal-width choices and related actions, measured with a real width constraint.</summary>
    public sealed class ChoiceGrid : Panel
    {
        public ChoiceGrid() => Classes.Add("choice-grid");

        public int MaxColumns { get; init; } = 2;
        public double MinimumItemWidth { get; init; } = 80;
        public double Gap { get; init; } = 8;
        private int _columns = 1;
        private double[] _rowHeights = [];

        protected override Size MeasureOverride(Size availableSize)
        {
            var visible = Children.Where(c => c.IsVisible).ToArray();
            if (visible.Length == 0) { _rowHeights = []; return default; }
            var width = double.IsFinite(availableSize.Width) ? availableSize.Width
                : Math.Min(MaxColumns, visible.Length) * (MinimumItemWidth + Gap) - Gap;
            foreach (var child in visible) child.Measure(Size.Infinity);
            var minimumWidth = Math.Max(MinimumItemWidth, visible.Max(c => c.DesiredSize.Width));
            _columns = Math.Min(visible.Length, Math.Min(MaxColumns,
                Math.Max(1, (int)Math.Floor((width + Gap) / (minimumWidth + Gap)))));
            _rowHeights = new double[(visible.Length + _columns - 1) / _columns];
            var cellWidth = Math.Max(0, (width - Gap * (_columns - 1)) / _columns);
            for (var i = 0; i < visible.Length; i++)
            {
                var lastAlone = i == visible.Length - 1 && i % _columns == 0;
                visible[i].Measure(new Size(lastAlone ? width : cellWidth, double.PositiveInfinity));
                _rowHeights[i / _columns] = Math.Max(_rowHeights[i / _columns], visible[i].DesiredSize.Height);
            }
            return new Size(width, _rowHeights.Sum() + Gap * (_rowHeights.Length - 1));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var visible = Children.Where(c => c.IsVisible).ToArray();
            var cellWidth = Math.Max(0, (finalSize.Width - Gap * (_columns - 1)) / _columns);
            var y = 0d;
            for (var row = 0; row < _rowHeights.Length; row++)
            {
                for (var column = 0; column < _columns && row * _columns + column < visible.Length; column++)
                {
                    var index = row * _columns + column;
                    var lastAlone = index == visible.Length - 1 && column == 0;
                    var left = Math.Round(column * (cellWidth + Gap));
                    var right = lastAlone || column == _columns - 1 ? finalSize.Width
                        : Math.Round(column * (cellWidth + Gap) + cellWidth);
                    visible[index].Arrange(new Rect(left, y, Math.Max(0, right - left), _rowHeights[row]));
                }
                y += _rowHeights[row] + Gap;
            }
            return finalSize;
        }
    }

    // --------------------------------------------------------------- list rows

    /// <summary>
    /// The one row shape shared by layers, objects, assets and notes: a kind icon, a trimmed
    /// primary label, a muted secondary label, and state badges that stay visible without hover.
    /// </summary>
    /// <param name="image">
    /// A real picture of what the row is about - a catalog thumbnail of the asset a placed object
    /// draws. When present it replaces the generic kind glyph, because a tree should look like a
    /// tree and not like the tool that stamped it. Null falls back to <paramref name="icon"/>.
    /// </param>
    public static Button ListRow(
        string name,
        string icon,
        string primary,
        string? secondary,
        IReadOnlyList<(string Icon, string Title)> states,
        Action activate,
        string? tooltip = null,
        IImage? image = null,
        Control? trailing = null)
    {
        var content = new DockPanel { LastChildFill = true };

        // A trailing value the row always shows (the object's level), at the far right where the
        // eye lands after the name; state badges follow it inward.
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(trailing.Docked(Dock.Right));
        }

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

        content.Children.Add(new Border
        {
            Width = 20,
            Height = 20,
            Child = image is null
                ? MapIcons.Stroked(icon, 16)
                : new Image { Source = image, Stretch = Stretch.Uniform },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        }.Docked(Dock.Left));

        var caption = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(Trimmed(primary));
        if (!string.IsNullOrWhiteSpace(secondary))
            caption.Children.Add(Trimmed(secondary, "faint", "role-caption"));
        content.Children.Add(caption);

        // The row spans the list, so a trailing value sits in one column down the whole list
        // instead of following each name to a different place.
        var row = new ToggleButton
        {
            Content = content,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        }.Named(name).Classed("list-row");
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

    /// <summary>A choice list in document flow: opening or navigating never applies a value.</summary>
    public sealed class InlineChoice : StackPanel
    {
        private readonly string _label;
        private readonly Func<IReadOnlyList<(string Text, Action Choose)>> _choices;
        private readonly TextBlock _caption;
        private readonly Control _chevron;
        private readonly Border _options;
        private readonly ScrollViewer _scroll;
        private readonly StackPanel _items = new();
        private readonly List<(Button Button, Control Mark, string Text)> _rows = [];
        public Button Header { get; }
        public bool IsExpanded => _options.IsVisible;
        public event EventHandler? Opened;

        public InlineChoice(string name, string label, string value,
            Func<IReadOnlyList<(string Text, Action Choose)>> choices)
        {
            _label = label;
            _choices = choices;
            _caption = new TextBlock { TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center }.Named(name + "Text");
            _chevron = MapIcons.Stroked(MapIcons.ChevronDown, 13);
            Header = new Button
            {
                Content = new DockPanel
                {
                    Children =
                    {
                        new Border { Width = 18, Margin = new Thickness(8, 0, 0, 0),
                            Child = _chevron, VerticalAlignment = VerticalAlignment.Center }.Docked(Dock.Right),
                        _caption,
                    },
                },
            }.Named(name).Classed("value");
            _scroll = new ScrollViewer
            {
                Content = _items, MaxHeight = 192,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            }.Named(name + "Scroll");
            _options = new Border
            {
                Child = _scroll, Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 0),
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
                Background = Application.Current?.FindResource("MapElevatedBrush") as IBrush,
                IsVisible = false,
            }.Named(name + "Options");
            Children.Add(Header);
            Children.Add(_options);
            Header.Click += (_, _) => { if (IsExpanded) Close(); else Open(); };
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && IsExpanded)
                {
                    Close(); Header.Focus(); e.Handled = true;
                }
                else if (Header.IsFocused && e.Key == Key.Down)
                {
                    if (!IsExpanded) Open();
                    _rows.FirstOrDefault(r => r.Text == _caption.Text).Button?.Focus();
                    if (!_rows.Any(r => r.Button.IsFocused)) _rows.FirstOrDefault().Button?.Focus();
                    e.Handled = true;
                }
            };
            SetValue(value);
        }

        public void SetValue(string value)
        {
            _caption.Text = value;
            AutomationProperties.SetName(Header, $"{_label}: {value}");
            ToolTip.SetTip(Header, $"{_label}: {value}. Открыть варианты.");
            foreach (var row in _rows)
            {
                var selected = row.Text == value;
                SetClass(row.Button, "selected", selected);
                row.Mark.IsVisible = selected;
            }
        }

        public void Open()
        {
            if (IsExpanded) return;
            Opened?.Invoke(this, EventArgs.Empty);
            _items.Children.Clear();
            _rows.Clear();
            foreach (var choice in _choices())
            {
                var mark = MapIcons.Stroked(MapIcons.Check, 14);
                var button = new Button
                {
                    Content = new DockPanel
                    {
                        Children =
                        {
                            new Border { Width = 18, Child = mark, Margin = new Thickness(6, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center }.Docked(Dock.Right),
                            new TextBlock { Text = choice.Text, TextWrapping = TextWrapping.Wrap,
                                VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                }.Named(Header.Name + "Option" + _rows.Count).Classed("inline-choice-option");
                AutomationProperties.SetName(button, choice.Text);
                button.Click += (_, _) => { Close(); Header.Focus(); choice.Choose(); };
                button.KeyDown += (_, e) =>
                {
                    var index = _rows.FindIndex(r => r.Button == button);
                    var next = e.Key switch { Key.Down => index + 1, Key.Up => index - 1,
                        Key.Home => 0, Key.End => _rows.Count - 1, _ => -1 };
                    if (e.Key is Key.Down or Key.Up or Key.Home or Key.End)
                    {
                        _rows[Math.Clamp(next, 0, _rows.Count - 1)].Button.Focus();
                        e.Handled = true;
                    }
                };
                _rows.Add((button, mark, choice.Text));
                _items.Children.Add(button);
            }
            _options.IsVisible = true;
            _chevron.RenderTransform = new RotateTransform(180);
            SetValue(_caption.Text ?? "");
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsExpanded) return;
                this.BringIntoView();
                var index = _rows.FindIndex(r => r.Text == _caption.Text);
                _scroll.Offset = new Vector(0, _rows.Count == 0 ? 0 :
                    _rows[Math.Max(0, index - 2)].Button.Bounds.Y);
            }, DispatcherPriority.Loaded);
        }

        public void Close()
        {
            _options.IsVisible = false;
            _chevron.RenderTransform = null;
        }
    }

    /// <summary>
    /// Inspector caption with the approved semantic glyph when the label describes a known
    /// parameter. The text remains present: icons aid scanning but never replace meaning.
    /// </summary>
    /// <summary>A small "i" whose tooltip carries the explanation of the setting beside it.</summary>
    public static Control InfoGlyph(string text)
    {
        var glyph = new Border
        {
            Width = 16, Height = 16, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
            Child = MapIcons.Grid24(MapIcons.LineInfo, 14),
        }.Classed("info-glyph");
        ToolTip.SetTip(glyph, text);
        ToolTip.SetShowDelay(glyph, 150);
        AutomationProperties.SetName(glyph, "Пояснение: " + text);
        return glyph;
    }

    public static Control ParameterLabel(string label, double? width = null)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        }.Classed("muted");

        // A long caption ("Непрозрачность слоя") broke inside the word next to its glyph: such a
        // caption gives the glyph's room to the words and wraps only between them.
        var icon = label.Length > 16 ? null : MapIcons.ForProperty(label);
        if (label.Length > 16) text.FontSize = 12;
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
        // Width always includes the glyph and gap. Adding them again overflows the parent's column.
        if (width is { } captionWidth) caption.Width = captionWidth;
        return caption;
    }

    /// <summary>Updates a value button in place, keeping its accessible name truthful.</summary>
    public static void SetValue(Button button, string label, string value)
    {
        if (button.Parent is InlineChoice choice)
        {
            choice.SetValue(value);
            return;
        }
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

        label.Children.Add(new Border
        {
            Width = 20,
            Child = MapIcons.Stroked(icon, 16),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        }.Docked(Dock.Left));

        var caption = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(Trimmed(primary));
        if (!string.IsNullOrWhiteSpace(secondary))
            caption.Children.Add(Trimmed(secondary, "faint", "role-caption"));
        label.Children.Add(caption);

        var select = new ToggleButton { Content = label, MinHeight = 44 }.Named(name).Classed("list-row");
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

    /// <summary>
    /// A short value at the right edge of a structure row, before its actions: a one-line row
    /// keeps the count visible without a second caption line.
    /// </summary>
    public static void SetStructureMeta(Border row, string meta)
    {
        if (row.Child is not DockPanel panel || panel.Children.Count == 0
            || panel.Children[panel.Children.Count - 1] is not ToggleButton select) return;
        select.MinHeight = 34;
        if (select.Content is DockPanel label && meta.Length > 0)
        {
            label.Children.Insert(0, new TextBlock
            {
                Text = meta, FontSize = 11, Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }.Classed("faint").Docked(Dock.Right));
        }
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
