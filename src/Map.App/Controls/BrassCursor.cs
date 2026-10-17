using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Map.App.Controls;

/// <summary>
/// Map's own pointer (2026-10-17): a slender brass needle-arrow with a dark outline and a small
/// bead, drawn in code at the screen's scale so it stays crisp. Text fields and the canvas keep
/// their own cursors; this replaces only the plain arrow.
/// </summary>
public static class BrassCursor
{
    private static readonly Dictionary<int, Cursor> Cache = [];

    private const string Needle = "M1.2 1.2 L1.2 20.5 L5.6 16.4 L9.1 24.2 L11.9 23 L8.5 15.3 L14.6 14.9 Z";

    public static Cursor For(double scaling)
    {
        var key = (int)Math.Round(Math.Clamp(scaling, 1, 3) * 4);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var scale = key / 4.0;
        var size = (int)Math.Ceiling(28 * scale);
        using var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            {
                var geometry = Geometry.Parse(Needle);
                // soft drop shadow, then the outlined brass body and a highlight along its spine
                using (context.PushTransform(Matrix.CreateTranslation(1.1, 1.4)))
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, geometry);
                var brass = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.Parse("#FBE9BE"), 0),
                        new GradientStop(Color.Parse("#D9B56C"), 0.45),
                        new GradientStop(Color.Parse("#8E6A2E"), 1),
                    },
                };
                context.DrawGeometry(brass, new Pen(new SolidColorBrush(Color.Parse("#1E140A")), 1.3, lineJoin: PenLineJoin.Round), geometry);
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(170, 0xFF, 0xF4, 0xD8)), 0.9),
                    new Point(2.4, 4), new Point(2.4, 16.5));
                context.DrawEllipse(new SolidColorBrush(Color.Parse("#7A2E1C")),
                    new Pen(new SolidColorBrush(Color.Parse("#1E140A")), 0.8), new Point(10.4, 21.2), 1.5, 1.5);
            }
        }
        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;
        var cursor = new Cursor(new Bitmap(stream), new PixelPoint((int)Math.Round(1.2 * scale), (int)Math.Round(1.2 * scale)));
        Cache[key] = cursor;
        return cursor;
    }
}
