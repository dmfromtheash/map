using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Core.Editing;

namespace Map.App.Controls;

/// <summary>
/// The actual stamp a brush tip prints, drawn at thumbnail size.
/// <para>
/// The tip chooser used to be seven same-looking text buttons, so the only way to learn what
/// "Краевой блок" meant was to paint with it and undo. This draws the engine's own alpha mask -
/// the very bytes the stroke stamps - so the list shows the shape instead of naming it.
/// </para>
/// </summary>
public sealed class BrushTipPreview : Control
{
    private BrushTipShape? _tip;
    private double _softness;

    public BrushTipPreview(BrushTipShape? tip, double softness)
    {
        _tip = tip;
        _softness = Math.Clamp(softness, 0, 1);
        Width = 34;
        Height = 34;
        IsHitTestVisible = false;
    }

    /// <summary>Null draws the plain round tip, whose edge is the softness instead of a mask.</summary>
    public BrushTipShape? Tip
    {
        get => _tip;
        set
        {
            _tip = value;
            InvalidateVisual();
        }
    }

    public double Softness
    {
        get => _softness;
        set
        {
            _softness = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var ink = (Application.Current?.FindResource("MapTextBrush") as IBrush) ?? Brushes.White;
        context.FillRectangle(
            (Application.Current?.FindResource("MapControlBrush") as IBrush) ?? Brushes.Black,
            rect, 3);

        if (_tip is null)
        {
            DrawRoundTip(context, rect, ink);
            return;
        }

        // One filled cell per opaque mask pixel, coarsened to keep the thumbnail cheap.
        var step = Math.Max(1, _tip.Width / 24);
        var cellWidth = rect.Width / (_tip.Width / (double)step);
        var cellHeight = rect.Height / (_tip.Height / (double)step);
        for (var y = 0; y < _tip.Height; y += step)
        {
            for (var x = 0; x < _tip.Width; x += step)
            {
                if (_tip.Alpha[(y * _tip.Width) + x] == 0)
                {
                    continue;
                }

                context.FillRectangle(ink, new Rect(
                    rect.X + (x / (double)step * cellWidth),
                    rect.Y + (y / (double)step * cellHeight),
                    Math.Ceiling(cellWidth),
                    Math.Ceiling(cellHeight)));
            }
        }
    }

    private void DrawRoundTip(DrawingContext context, Rect rect, IBrush ink)
    {
        var centre = rect.Center;
        var radius = Math.Min(rect.Width, rect.Height) / 2 - 3;
        if (_softness <= 0.01)
        {
            context.DrawEllipse(ink, null, centre, radius, radius);
            return;
        }

        // The soft tip is the same circle with the outer part of the radius fading out, which is
        // exactly what Softness does to the dab.
        var solid = Math.Clamp(1 - _softness, 0, 1);
        var colour = (ink as ISolidColorBrush)?.Color ?? Colors.White;
        context.DrawEllipse(
            new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(colour, solid),
                    new GradientStop(Color.FromArgb(0, colour.R, colour.G, colour.B), 1),
                },
            },
            null, centre, radius, radius);
    }
}
