using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Map.App.Controls;
using Map.App.Services;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

public sealed partial class EditorView
{
    // ========================================== appearance inspector building blocks

    /// <summary>
    /// A slider that previews while dragging and lands the whole gesture as ONE undo entry when
    /// the pointer or the keyboard lets go. The registered sync re-reads the actual value, so the
    /// control can never display a number the document does not hold.
    /// </summary>
    private Control AppearanceSlider(
        string name, string label, double minimum, double maximum, string unit,
        Func<double?> read, Action<double, bool> apply, double step = 1, Control? trailing = null,
        double? defaultValue = null, string? description = null) =>
        ValueRow(name, name + "Box", name + "Value", label, minimum, maximum, unit, read, apply,
            step, defaultValue, description, trailing);

    /// <summary>A small per-parameter reset that simply commits the neutral value.</summary>
    private static Button ResetGlyph(string name, string title, Action reset)
    {
        var button = new Button
        {
            Content = MapIcons.Stroked(MapIcons.Undo, 12),
            Width = 24,
            Height = 24,
            MinHeight = 24,
            Padding = new Thickness(4, 1),
            Margin = new Thickness(4, 0, 0, 0),
        }.Named(name).Classed("icon");
        AutomationProperties.SetName(button, title);
        ToolTip.SetTip(button, title);
        button.Click += (_, _) => reset();
        return button;
    }

    /// <summary>A numeric field committing on Enter or focus loss; bad input is an error, not a command.</summary>
    private Control AppearanceNumberBox(
        string name, string label, string? unit, Func<double?> read, Action<double> apply,
        string format = "0", double captionWidth = 92, (double Min, double Max)? slider = null,
        double? defaultValue = null, string? description = null, double step = 1)
    {
        var box = NumberBox(name);
        var committedText = "";
        void Commit()
        {
            if (_syncingAppearance)
            {
                return;
            }

            var text = box.Text ?? "";
            if (!TryWholeNumber(text, out var value))
            {
                _editor.ReportInspectorError($"«{label}» должно быть целым числом.");
                SyncStatus();
                return;
            }

            apply(value);
            committedText = text;
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
                // Enter applies and hands the keyboard back to the map, so the next Ctrl+Z,
                // Enter or Esc reaches the editor instead of this field.
                Canvas.Focus();
            }
        };
        box.GotFocus += (_, _) => committedText = box.Text ?? "";
        // Undo/redo inside a field that holds no unapplied typing is the map's undo; before,
        // the text box swallowed Ctrl+Z after every «Плотность», «Шаг» or «Seed» change.
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if ((box.Text ?? "") != committedText || !ShortcutKeyAdapter.TryCreate(e, out var input)) return;
            var command = _editor.Shortcuts.Resolve(input, EditorKeyRouting.Contexts(this));
            if (command == EditorShortcutSettings.Commands.Undo) _editor.Undo();
            else if (command is EditorShortcutSettings.Commands.Redo or EditorShortcutSettings.Commands.RedoAlternate) _editor.Redo();
            else return;
            e.Handled = true;
            // The field shows the value the map has after the undo, and owes nothing on blur.
            box.Text = committedText = read()?.ToString(format, CultureInfo.InvariantCulture) ?? "";
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Leaving a field that was only visited applies nothing: re-applying its text used to
        // write a stale value back over an undo.
        box.LostFocus += (_, _) => { if ((box.Text ?? "") != committedText) Commit(); };

        _appearanceSyncs.Add(() =>
        {
            if (!box.IsFocused)
            {
                box.Text = read()?.ToString(format, CultureInfo.InvariantCulture) ?? "";
            }
        });

        if (slider is not { } range)
        {
            if (description is not null) ToolTip.SetTip(box, description);
            return Ui.PropertyRow(label, box, unit, captionWidth);
        }

        // With a range the value gets the full row: slider, box, unit, default and explanation.
        // The slider commits on release, on a wheel notch or on a key - one undo entry each.
        return ValueRow(name + "Slider", name, name + "Value", label, range.Min, range.Max, unit ?? "",
            read, (value, commit) => { if (commit) apply(value); }, step, defaultValue, description, box: box);
    }

    /// <summary>
    /// The one colour row of the editor: a swatch showing the current value that opens the shared
    /// picker. Every colour property goes through here, so there is a single behaviour to learn
    /// and a single place where hex parsing, preview and undo granularity are decided.
    /// </summary>
    private Control AppearanceColorBox(
        string name, string label, Func<string?> read, Action<string> apply, double captionWidth = 92)
        => AppearanceColorBox(name, label, read, (value, _) => apply(value), captionWidth,
            supportsAlpha: true, cancelPreview: null);

    /// <summary>
    /// The preview-capable overload. <paramref name="apply"/> receives <c>commit: false</c> while
    /// the user is still dragging inside the picker - that path previews on the canvas and writes
    /// no history - and <c>commit: true</c> exactly once when the choice is applied.
    /// </summary>
    private Control AppearanceColorBox(
        string name, string label, Func<string?> read, Action<string, bool> apply,
        double captionWidth = 92, bool supportsAlpha = true, Action? cancelPreview = null)
    {
        var field = new ColorField(
            name, label, read, apply, cancelPreview,
            supportsAlpha: supportsAlpha,
            supportsPreview: cancelPreview is not null,
            captionWidth: captionWidth);
        _appearanceSyncs.Add(field.Sync);
        _colorFields.Add(field);
        return field.Row;
    }

    /// <summary>Own-process smoke hook: one named colour property of the real editor.</summary>
    public ColorField ColorFieldForSmoke(string name) =>
        _colorFields.FirstOrDefault(field => field.Name == name)
        ?? throw new InvalidOperationException($"No colour field named {name}.");

    /// <summary>A named on/off property whose checked state always mirrors the document.</summary>
    private ToggleButton AppearanceToggle(string name, string label, Func<bool?> read, Action<bool> apply)
    {
        var toggle = Ui.PropertyToggle(name, label);
        AutomationProperties.SetName(toggle, label);
        ToolTip.SetTip(toggle, label);
        toggle.Click += (_, _) =>
        {
            if (!_syncingAppearance)
            {
                apply(toggle.IsChecked == true);
            }
        };

        _appearanceSyncs.Add(() =>
        {
            var value = read() == true;
            toggle.IsChecked = value;
            Ui.SetClass(toggle, "active", value);
        });

        return toggle;
    }

    private Ui.InlineChoice? _expandedPropertyChoice;

    private Ui.InlineChoice PropertyChoice(string name, string label, string value,
        Func<IReadOnlyList<(string Text, Action Choose)>> choices)
    {
        var choice = new Ui.InlineChoice(name, label, value, choices);
        choice.Opened += (_, _) =>
        {
            _expandedPropertyChoice?.Close();
            _expandedPropertyChoice = choice;
        };
        return choice;
    }

    private (Control Row, Button Button) PropertyValueRow(string name, string label, string value,
        IReadOnlyList<(string Text, Action Choose)> options)
    {
        var choice = PropertyChoice(name, label, value, () => options);
        return (Ui.PropertyRow(label, choice), choice.Header);
    }

    /// <summary>Stored keys in a bounded inline list, applied only by choosing a named option.</summary>
    private Control AppearanceChoice(
        string name, string label, IReadOnlyList<(string Key, string Text)> options,
        Func<string?> read, Action<string> apply)
    {
        var choice = PropertyChoice(name, label, options[0].Text,
            () => options.Select(option => (option.Text, (Action)(() =>
            {
                if (!_syncingAppearance && option.Key != read()) apply(option.Key);
            }))).ToArray());

        _appearanceSyncs.Add(() =>
        {
            var current = read();
            var index = 0;
            for (var i = 0; i < options.Count; i++)
            {
                if (options[i].Key == current)
                {
                    index = i;
                    break;
                }
            }

            choice.SetValue(options[index].Text);
        });

        return Ui.PropertyRow(label, choice);
    }

    private Control BlendChoice(string name, Func<string?> read, Action<string> apply) =>
        AppearanceChoice(
            name, "Смешивание",
            [.. BlendModes.Supported.Select(key => (key, EditorLabels.Blend(key)))],
            read, apply);

    /// <summary>
    /// The shared drop-shadow block: named state, three presets as real commands and the custom
    /// sliders, disclosed only because a shadow is a rare setting.
    /// </summary>
    private StackPanel ShadowBlock(
        string prefix,
        Func<(double OffsetX, double OffsetY, double Blur, double Opacity)?> read,
        Action none, Action standard,
        Action<double?, double?, double?, double?, bool> custom)
    {
        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center }
            .Classed("muted").Named(prefix + "ShadowStateText");

        var noneButton = Ui.TextButton(prefix + "ShadowNoneButton", "Нет", () =>
        {
            none();
            SyncAppearanceSections();
        }, "segment");
        var standardButton = Ui.TextButton(prefix + "ShadowStandardButton", "Стандартная", () =>
        {
            standard();
            SyncAppearanceSections();
        }, "segment");

        var sliders = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                AppearanceSlider(prefix + "ShadowOffsetXSlider", "Сдвиг по X", -64, 64, "px",
                    () => read()?.OffsetX, (v, c) => custom(v, null, null, null, c)),
                AppearanceSlider(prefix + "ShadowOffsetYSlider", "Сдвиг по Y", -64, 64, "px",
                    () => read()?.OffsetY, (v, c) => custom(null, v, null, null, c)),
                AppearanceSlider(prefix + "ShadowBlurSlider", "Размытие тени", 0, 64, "px",
                    () => read()?.Blur, (v, c) => custom(null, null, v, null, c)),
                AppearanceSlider(prefix + "ShadowOpacitySlider", "Плотность тени", 0, 100, "%",
                    () => read()?.Opacity * 100, (v, c) => custom(null, null, null, v, c), 5),
            },
        };

        _appearanceSyncs.Add(() =>
        {
            var shadow = read();
            state.Text = shadow is { } value
                ? EditorLabels.ShadowState(value.OffsetX, value.OffsetY, value.Blur, value.Opacity)
                : "";
        });

        return new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        state.Docked(Dock.Right),
                        new TextBlock { Text = "Тень", VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                new Ui.ChoiceGrid { MaxColumns = 2, Children = { noneButton, standardButton } },
                sliders,
            },
        };
    }

    /// <summary>Runs every registered value sync with user-input handlers muted.</summary>
    private void SyncAppearanceSections()
    {
        _syncingAppearance = true;
        try
        {
            foreach (var sync in _appearanceSyncs)
            {
                sync();
            }

            SyncTextureSwatches();
        }
        finally
        {
            _syncingAppearance = false;
        }
    }
}
