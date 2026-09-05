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
using Map.App.Views;
using Map.Core.Documents;

namespace Map.App.Controls;

/// <summary>
/// Hue/saturation/value conversion kept local on purpose: it is fifteen lines, it has no
/// dependency, and it round-trips through the one canonical "#AARRGGBB" contract the documents use.
/// </summary>
internal readonly record struct ColorHsv(double Hue, double Saturation, double Value, double Alpha)
{
    public static ColorHsv FromColor(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
        }

        if (hue < 0) hue += 360;
        return new ColorHsv(hue, max <= 0 ? 0 : delta / max, max, color.A / 255.0);
    }

    public Color ToColor()
    {
        var hue = ((Hue % 360) + 360) % 360;
        var saturation = Math.Clamp(Saturation, 0, 1);
        var value = Math.Clamp(Value, 0, 1);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };

        return Color.FromArgb(
            (byte)Math.Round(Math.Clamp(Alpha, 0, 1) * 255),
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    public Color HueColor() => new ColorHsv(Hue, 1, 1, 1).ToColor();
}

/// <summary>The saturation/value square. Pointer drag and arrow keys both move the marker.</summary>
internal sealed class ColorSpectrumPad : Control
{
    private ColorHsv _value = new(0, 1, 1, 1);

    public ColorSpectrumPad()
    {
        Focusable = true;
        Height = 132;
        AutomationProperties.SetName(this, "Насыщенность и яркость");
        ToolTip.SetTip(this, "Насыщенность и яркость. Стрелками — точная настройка");
    }

    public event EventHandler<ColorHsv>? Picked;

    public ColorHsv Value
    {
        get => _value;
        set
        {
            _value = value;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        Focus();
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this))
        {
            Apply(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.02;
        var (saturation, value) = (_value.Saturation, _value.Value);
        switch (e.Key)
        {
            case Key.Left: saturation -= step; break;
            case Key.Right: saturation += step; break;
            case Key.Up: value += step; break;
            case Key.Down: value -= step; break;
            default: return;
        }

        Value = _value with
        {
            Saturation = Math.Clamp(saturation, 0, 1),
            Value = Math.Clamp(value, 0, 1),
        };
        Picked?.Invoke(this, Value);
        e.Handled = true;
    }

    private void Apply(Point position)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        Value = _value with
        {
            Saturation = Math.Clamp(position.X / Bounds.Width, 0, 1),
            Value = Math.Clamp(1 - (position.Y / Bounds.Height), 0, 1),
        };
        Picked?.Invoke(this, Value);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        context.FillRectangle(new SolidColorBrush(_value.HueColor()), rect);
        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.White, 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            },
            rect);
        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                    new GradientStop(Colors.Black, 1),
                },
            },
            rect);

        var centre = new Point(
            _value.Saturation * rect.Width,
            (1 - _value.Value) * rect.Height);
        context.DrawEllipse(null, new Pen(Brushes.Black, 3), centre, 6, 6);
        context.DrawEllipse(null, new Pen(Brushes.White, 1.5), centre, 6, 6);

        if (IsFocused)
        {
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5), rect.Deflate(1));
        }
    }
}

/// <summary>The hue and alpha bars. Same interaction shape as the pad, one dimension.</summary>
internal sealed class ColorChannelBar : Control
{
    private double _position;
    private Color _baseColor = Colors.White;

    public ColorChannelBar(bool alpha, string accessibleName, string tip)
    {
        IsAlpha = alpha;
        Focusable = true;
        Height = 18;
        AutomationProperties.SetName(this, accessibleName);
        ToolTip.SetTip(this, tip);
    }

    public bool IsAlpha { get; }

    public event EventHandler<double>? Picked;

    /// <summary>The opaque colour the alpha ramp fades into; ignored by the hue bar.</summary>
    public Color BaseColor
    {
        get => _baseColor;
        set
        {
            _baseColor = value;
            InvalidateVisual();
        }
    }

    /// <summary>0..1 along the bar: hue/360 for the hue bar, alpha for the alpha bar.</summary>
    public double Position
    {
        get => _position;
        set
        {
            _position = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        Focus();
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this))
        {
            Apply(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.05 : 1.0 / 180;
        switch (e.Key)
        {
            case Key.Left or Key.Down: Position -= step; break;
            case Key.Right or Key.Up: Position += step; break;
            case Key.Home: Position = 0; break;
            case Key.End: Position = 1; break;
            default: return;
        }

        Picked?.Invoke(this, Position);
        e.Handled = true;
    }

    private void Apply(Point point)
    {
        if (Bounds.Width <= 0) return;
        Position = point.X / Bounds.Width;
        Picked?.Invoke(this, Position);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (IsAlpha)
        {
            DrawCheckerboard(context, rect);
            context.FillRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, BaseColor.R, BaseColor.G, BaseColor.B), 0),
                        new GradientStop(Color.FromArgb(255, BaseColor.R, BaseColor.G, BaseColor.B), 1),
                    },
                },
                rect);
        }
        else
        {
            var stops = new GradientStops();
            for (var index = 0; index <= 6; index++)
            {
                stops.Add(new GradientStop(
                    new ColorHsv(index * 60, 1, 1, 1).ToColor(), index / 6.0));
            }

            context.FillRectangle(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = stops,
                },
                rect);
        }

        var x = Math.Clamp(_position * rect.Width, 1.5, rect.Width - 1.5);
        context.DrawLine(new Pen(Brushes.Black, 3), new Point(x, 0), new Point(x, rect.Height));
        context.DrawLine(new Pen(Brushes.White, 1.5), new Point(x, 0), new Point(x, rect.Height));

        if (IsFocused)
        {
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5), rect.Deflate(1));
        }
    }

    internal static void DrawCheckerboard(DrawingContext context, Rect rect)
    {
        const double cell = 6;
        context.FillRectangle(Brushes.White, rect);
        var dark = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
        for (var y = 0; y * cell < rect.Height; y++)
        {
            for (var x = (y % 2); x * cell < rect.Width; x += 2)
            {
                context.FillRectangle(dark, new Rect(
                    rect.X + (x * cell), rect.Y + (y * cell),
                    Math.Min(cell, rect.Right - (rect.X + (x * cell))),
                    Math.Min(cell, rect.Bottom - (rect.Y + (y * cell)))));
            }
        }
    }
}

/// <summary>A colour swatch that reads honestly through transparency.</summary>
internal sealed class ColorSwatchPreview : Control
{
    private Color _color = Colors.Transparent;

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        ColorChannelBar.DrawCheckerboard(context, rect);
        context.FillRectangle(new SolidColorBrush(_color), rect);
    }
}

/// <summary>
/// The one colour control of the editor.
/// <para>
/// Every colour used to be a bare hex <c>TextBox</c>: the user could not see the current colour,
/// could not choose one visually, and a typo silently wrote nothing or an error line. This is the
/// single replacement - a swatch that shows the value, a popover with hue, saturation/value and
/// alpha, quick swatches, session recents, and the exact hex field kept as an extra way in rather
/// than the only one.
/// </para>
/// <para>
/// History: dragging inside the popover previews live and writes nothing. Exactly one commit
/// happens when the user applies, so one colour decision is one undo entry. Escape and Cancel put
/// the original colour back and leave no history at all.
/// </para>
/// </summary>
public sealed class ColorField
{
    /// <summary>
    /// A neutral working palette: a grey ramp plus evenly spaced hues. Deliberately generic - it
    /// is a starting point for the owner's own colours, not a copy of anyone's brand set.
    /// </summary>
    private static readonly string[] DefaultSwatches =
    [
        "#FF000000", "#FF3A3A3A", "#FF6B6B6B", "#FF9E9E9E", "#FFD0D0D0", "#FFFFFFFF",
        "#FF7F1D1D", "#FFC0392B", "#FFE8743B", "#FFE0A94B", "#FFE8DC8A", "#FFF3EFD8",
        "#FF1F4B33", "#FF3E8E5A", "#FF7FBE86", "#FF2C5B7A", "#FF4C90B8", "#FF9CC8FF",
        "#FF3B2A5C", "#FF6C4FA3", "#FFA98BD6", "#FF5A3B28", "#FF8C6239", "#FFC3A183",
    ];

    private static readonly List<string> Recent = [];

    private readonly Func<string?> _read;
    private readonly Action<string, bool> _apply;
    private readonly Action? _cancelPreview;
    private readonly bool _supportsAlpha;
    private readonly bool _supportsPreview;
    private readonly ColorSwatchPreview _swatch = new() { Width = 26, Height = 16 };
    private readonly TextBlock _valueText;
    private readonly Button _button;

    private ColorSpectrumPad _pad = null!;
    private ColorChannelBar _hueBar = null!;
    private ColorChannelBar _alphaBar = null!;
    private ColorSwatchPreview _beforePreview = null!;
    private ColorSwatchPreview _afterPreview = null!;
    private TextBox _hexBox = null!;
    private TextBlock _hexError = null!;
    private StackPanel _recentPanel = null!;
    private Flyout? _flyout;
    private ColorHsv _draft;
    private string _original = AppearanceColor.OpaqueBlack;
    private bool _syncing;

    public ColorField(
        string name,
        string label,
        Func<string?> read,
        Action<string, bool> apply,
        Action? cancelPreview = null,
        bool supportsAlpha = true,
        bool supportsPreview = true,
        double captionWidth = 92)
    {
        _read = read;
        _apply = apply;
        _cancelPreview = cancelPreview;
        _supportsAlpha = supportsAlpha;
        _supportsPreview = supportsPreview && cancelPreview is not null;

        _valueText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.Named(name + "Text");

        _button = new Button
        {
            Content = new DockPanel
            {
                Children =
                {
                    new Border
                    {
                        Width = 26,
                        Height = 16,
                        BorderThickness = new Thickness(1),
                        BorderBrush = Application.Current?.FindResource("MapBorderStrongBrush") as IBrush,
                        CornerRadius = new CornerRadius(3),
                        ClipToBounds = true,
                        Margin = new Thickness(0, 0, 8, 0),
                        Child = _swatch,
                    }.Docked(Dock.Left),
                    _valueText,
                },
            },
        }.Named(name).Classed("value");

        AutomationProperties.SetName(_button, label + ": выбор цвета");
        ToolTip.SetTip(_button, $"{label}. Нажмите, чтобы выбрать цвет");
        _button.Click += (_, _) => Open();

        Row = Ui.PropertyRow(label, _button, captionWidth: captionWidth);
        Sync();
    }

    /// <summary>The caption plus swatch button, ready to drop into an inspector section.</summary>
    public Control Row { get; }

    /// <summary>The control's stable name, so a smoke can address one specific colour property.</summary>
    public string Name => _button.Name ?? "";

    /// <summary>What the row currently shows, i.e. what the user reads next to the swatch.</summary>
    public string DisplayedValue => _valueText.Text ?? "";

    /// <summary>
    /// Own-process smoke hooks. They drive the same popover state the pointer drives - the draft,
    /// the live preview and the single commit - and return the popover's real content so a smoke
    /// can measure it. Nothing here bypasses the apply/cancel callbacks the user's clicks use.
    /// </summary>
    public Control OpenForSmoke()
    {
        Open();
        return (Control)_flyout!.Content!;
    }

    public void PickForSmoke(string hex, bool commit)
    {
        _draft = ColorHsv.FromColor(ParseOrBlack(AppearanceColor.Normalize(hex) ?? AppearanceColor.OpaqueBlack));
        SyncPopup(updateHex: true);
        if (commit)
        {
            Commit();
        }
        else
        {
            Preview();
        }
    }

    public void CancelForSmoke() => Cancel();

    /// <summary>The colour the popover would apply right now, in the canonical document form.</summary>
    public string DraftForSmoke() => Canonical();

    /// <summary>Re-reads the current value. Never fights the user while the popover is open.</summary>
    public void Sync()
    {
        if (IsOpen)
        {
            return;
        }

        var canonical = AppearanceColor.Normalize(_read()) ?? AppearanceColor.OpaqueBlack;
        _original = canonical;
        var color = ParseOrBlack(canonical);
        _swatch.Color = color;
        _valueText.Text = Display(canonical, color);
    }

    private bool IsOpen { get; set; }

    private static Color ParseOrBlack(string canonical)
    {
        try
        {
            return Color.Parse(canonical);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return Colors.Black;
        }
    }

    private string Display(string canonical, Color color) =>
        _supportsAlpha && color.A != 255
            ? $"{canonical[3..]} · {Math.Round(color.A / 255.0 * 100)}%"
            : "#" + canonical[3..];

    // ------------------------------------------------------------------ popup

    private void Open()
    {
        _flyout ??= BuildFlyout();
        _original = AppearanceColor.Normalize(_read()) ?? AppearanceColor.OpaqueBlack;
        _draft = ColorHsv.FromColor(ParseOrBlack(_original));
        SyncPopup(updateHex: true);
        _beforePreview.Color = ParseOrBlack(_original);
        BuildRecentSwatches();
        IsOpen = true;
        _flyout.ShowAt(_button);
    }

    private Flyout BuildFlyout()
    {
        _pad = new ColorSpectrumPad();
        _pad.Picked += (_, value) =>
        {
            _draft = value;
            OnDraftChanged();
        };

        _hueBar = new ColorChannelBar(false, "Тон", "Тон. Стрелками — точная настройка");
        _hueBar.Picked += (_, position) =>
        {
            _draft = _draft with { Hue = position * 360 };
            OnDraftChanged();
        };

        _alphaBar = new ColorChannelBar(true, "Непрозрачность", "Непрозрачность цвета");
        _alphaBar.Picked += (_, position) =>
        {
            _draft = _draft with { Alpha = position };
            OnDraftChanged();
        };

        _beforePreview = new ColorSwatchPreview { Height = 22 };
        _afterPreview = new ColorSwatchPreview { Height = 22 };

        _hexBox = new TextBox { PlaceholderText = "#RRGGBB или #AARRGGBB", MaxLength = 9 }
            .Named("ColorPickerHex");
        AutomationProperties.SetName(_hexBox, "Цвет в шестнадцатеричном виде");
        _hexBox.TextChanged += (_, _) =>
        {
            // Three things can raise this event, and only one of them is the user typing.
            // The popover writes the box itself on every draft change, and that write can be
            // delivered after the guard is cleared or even after the popover has closed - which
            // used to leave a live preview gesture behind and send the next colour edit to the
            // object this field was last opened on.
            if (_syncing || !IsOpen) return;
            if (AppearanceColor.Normalize(_hexBox.Text) is { } normalized)
            {
                _hexError.IsVisible = false;
                if (string.Equals(normalized, Canonical(), StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _draft = ColorHsv.FromColor(ParseOrBlack(normalized));
                if (!_supportsAlpha) _draft = _draft with { Alpha = 1 };
                SyncPopup(updateHex: false);
                Preview();
                return;
            }

            // The invalid text stays in the box so the user can fix it, but nothing reaches the
            // document: a typo must never write a wrong colour onto the object.
            _hexError.IsVisible = true;
        };

        _hexError = new TextBlock
        {
            Text = "Цвет задаётся как #RRGGBB или #AARRGGBB.",
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        }.Classed("danger").Named("ColorPickerHexError");

        _recentPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        var swatches = new WrapPanel();
        foreach (var value in DefaultSwatches)
        {
            swatches.Children.Add(SwatchButton(value));
        }

        var apply = Ui.TextButton("ColorPickerApplyButton", "Применить", Commit, "primary", "compact");
        var cancel = Ui.TextButton("ColorPickerCancelButton", "Отмена", Cancel, "compact");

        var body = new StackPanel
        {
            Width = 244,
            Spacing = 8,
            Children =
            {
                _pad,
                _hueBar,
                _alphaBar,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    Height = 22,
                    Children =
                    {
                        _beforePreview,
                        _afterPreview,
                    },
                },
                new TextBlock { Text = "Было · станет" }.Classed("faint"),
                new TextBlock { Text = "Палитра" }.Classed("role-panel"),
                swatches,
                new TextBlock { Text = "Недавние" }.Classed("role-panel"),
                _recentPanel,
                _hexBox,
                _hexError,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, apply },
                },
            },
        };

        Grid.SetColumn(_beforePreview, 0);
        Grid.SetColumn(_afterPreview, 1);
        _alphaBar.IsVisible = _supportsAlpha;

        body.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };

        var flyout = new Flyout
        {
            Content = new Border { Padding = new Thickness(10), Child = body },
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard,
        };

        // Dismissing by clicking elsewhere is a cancel, not a silent commit: the document keeps
        // the colour it had, and the live preview is rolled back.
        flyout.Closed += (_, _) =>
        {
            if (!IsOpen) return;
            IsOpen = false;
            _cancelPreview?.Invoke();
            Sync();
        };

        return flyout;
    }

    private Button SwatchButton(string canonical)
    {
        var color = ParseOrBlack(canonical);
        var button = new Button
        {
            Width = 26,
            Height = 26,
            MinHeight = 26,
            Margin = new Thickness(0, 0, 4, 4),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new Border
            {
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                BorderBrush = Application.Current?.FindResource("MapBorderBrush") as IBrush,
                ClipToBounds = true,
                Child = new ColorSwatchPreview { Color = color },
            },
        }.Classed("compact");

        var name = "#" + canonical[3..];
        AutomationProperties.SetName(button, "Цвет " + name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) =>
        {
            _draft = ColorHsv.FromColor(color);
            if (!_supportsAlpha) _draft = _draft with { Alpha = 1 };
            SyncPopup(updateHex: true);
            Preview();
        };

        return button;
    }

    private void BuildRecentSwatches()
    {
        _recentPanel.Children.Clear();
        foreach (var value in Recent.Take(7))
        {
            _recentPanel.Children.Add(SwatchButton(value));
        }

        if (_recentPanel.Children.Count == 0)
        {
            _recentPanel.Children.Add(new TextBlock { Text = "Пока пусто" }.Classed("faint"));
        }
    }

    private void OnDraftChanged()
    {
        SyncPopup(updateHex: true);
        Preview();
    }

    private void SyncPopup(bool updateHex)
    {
        _syncing = true;
        try
        {
            if (!_supportsAlpha) _draft = _draft with { Alpha = 1 };
            var color = _draft.ToColor();
            _pad.Value = _draft;
            _hueBar.Position = _draft.Hue / 360;
            _alphaBar.BaseColor = new ColorHsv(_draft.Hue, _draft.Saturation, _draft.Value, 1).ToColor();
            _alphaBar.Position = _draft.Alpha;
            _afterPreview.Color = color;
            if (updateHex)
            {
                _hexBox.Text = Canonical();
                _hexError.IsVisible = false;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private string Canonical()
    {
        var color = _draft.ToColor();
        return "#" + (_supportsAlpha
            ? color.A.ToString("X2", CultureInfo.InvariantCulture)
            : "FF")
            + color.R.ToString("X2", CultureInfo.InvariantCulture)
            + color.G.ToString("X2", CultureInfo.InvariantCulture)
            + color.B.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>Live canvas preview with no history at all; only <see cref="Commit"/> writes one.</summary>
    private void Preview()
    {
        if (_supportsPreview)
        {
            _apply(Canonical(), false);
        }
    }

    private void Commit()
    {
        var canonical = Canonical();
        IsOpen = false;
        _flyout?.Hide();
        _apply(canonical, true);
        Remember(canonical);
        Sync();
    }

    private void Cancel()
    {
        IsOpen = false;
        _flyout?.Hide();
        _cancelPreview?.Invoke();
        Sync();
    }

    private static void Remember(string canonical)
    {
        Recent.RemoveAll(value => string.Equals(value, canonical, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, canonical);
        while (Recent.Count > 7)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }
    }
}
