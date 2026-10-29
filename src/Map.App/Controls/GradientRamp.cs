using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Map.App.Controls;

/// <summary>One stop as the ramp needs it: where it sits and what colour it is.</summary>
public readonly record struct GradientRampStop(double Offset, Color Color);

/// <summary>
/// The editable gradient ramp: the colours as they actually blend, with one draggable marker per
/// stop.
/// <para>
/// It exists because a gradient cannot be judged from a list of numbers. The user needs to see the
/// blend and grab the stop that is wrong. Dragging previews continuously and commits once on
/// release, so moving a stop is one undo entry - the same rule every slider in the inspector
/// follows.
/// </para>
/// </summary>
public sealed class GradientRamp : Control
{
    private const double MarkerRadius = 6;

    private IReadOnlyList<GradientRampStop> _stops = [];
    private int _selectedIndex;
    private int _draggingIndex = -1;

    public GradientRamp()
    {
        Focusable = true;
        Height = 34;
        AutomationProperties.SetName(this, "Полоса градиента");
        ToolTip.SetTip(this, "Точки цвета. Тяните маркер, чтобы сдвинуть; стрелками — точно");
    }

    /// <summary>Raised when the user picks a different stop to work on.</summary>
    public event EventHandler<int>? StopSelected;

    /// <summary>Raised while dragging (commit = false) and once on release (commit = true).</summary>
    public event EventHandler<(int Index, double Offset, bool Commit)>? StopMoved;

    public IReadOnlyList<GradientRampStop> Stops
    {
        get => _stops;
        set
        {
            _stops = value ?? [];
            if (_selectedIndex >= _stops.Count)
            {
                _selectedIndex = Math.Max(0, _stops.Count - 1);
            }

            InvalidateVisual();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = Math.Clamp(value, 0, Math.Max(0, _stops.Count - 1));
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_stops.Count == 0 || Bounds.Width <= 0)
        {
            return;
        }

        Focus();
        var position = e.GetPosition(this).X / Bounds.Width;

        // Grab the nearest stop rather than the one exactly under the pointer: markers are small,
        // and a near miss should move the stop the user aimed at instead of doing nothing.
        var nearest = 0;
        for (var index = 1; index < _stops.Count; index++)
        {
            if (Math.Abs(_stops[index].Offset - position) < Math.Abs(_stops[nearest].Offset - position))
            {
                nearest = index;
            }
        }

        _selectedIndex = nearest;
        _draggingIndex = nearest;
        StopSelected?.Invoke(this, nearest);
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_draggingIndex < 0 || !Equals(e.Pointer.Captured, this) || Bounds.Width <= 0)
        {
            return;
        }

        StopMoved?.Invoke(this,
            (_draggingIndex, Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1), false));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_draggingIndex >= 0 && Bounds.Width > 0)
        {
            StopMoved?.Invoke(this,
                (_draggingIndex, Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1), true));
        }

        _draggingIndex = -1;
        e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_stops.Count == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Tab:
                return;
            case Key.Left or Key.Right:
            {
                var step = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.01)
                    * (e.Key == Key.Left ? -1 : 1);
                StopMoved?.Invoke(this,
                    (_selectedIndex, Math.Clamp(_stops[_selectedIndex].Offset + step, 0, 1), true));
                e.Handled = true;
                return;
            }

            case Key.Up or Key.Down:
            {
                var next = Math.Clamp(_selectedIndex + (e.Key == Key.Up ? -1 : 1), 0, _stops.Count - 1);
                SelectedIndex = next;
                StopSelected?.Invoke(this, next);
                e.Handled = true;
                return;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        var full = new Rect(Bounds.Size);
        if (full.Width <= 0 || full.Height <= 0 || _stops.Count == 0)
        {
            return;
        }

        var ramp = new Rect(0, 0, full.Width, Math.Max(1, full.Height - (MarkerRadius * 2)));
        ColorChannelBar.DrawCheckerboard(context, ramp);

        var stops = new GradientStops();
        foreach (var stop in _stops.OrderBy(item => item.Offset))
        {
            stops.Add(new GradientStop(stop.Color, Math.Clamp(stop.Offset, 0, 1)));
        }

        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = stops,
            },
            ramp);

        for (var index = 0; index < _stops.Count; index++)
        {
            var centre = new Point(
                Math.Clamp(_stops[index].Offset, 0, 1) * full.Width,
                full.Height - MarkerRadius);
            var selected = index == _selectedIndex;
            context.DrawEllipse(
                new SolidColorBrush(_stops[index].Color),
                new Pen(selected ? Brushes.White : Brushes.Black, selected ? 2.5 : 1.5),
                centre, MarkerRadius, MarkerRadius);
        }

        if (IsFocused)
        {
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5), full.Deflate(1));
        }
    }
}
