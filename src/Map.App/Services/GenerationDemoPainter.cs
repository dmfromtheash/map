using System;
using System.Linq;
using Map.Core.Documents;
using Map.Presentation.ViewModels;
using Map.Rendering;
using SkiaSharp;

namespace Map.App.Services;

/// <summary>
/// Draws a frame of a generation card (<see cref="GenerationDemos"/>): the demo map by the program's renderer, the next
/// state faded over it, then the marks - protected and selected hexes, protected objects, the area's outline, the preview's
/// frame, the calculation's bar, the brush and pointer, one note.
/// </summary>
public static class GenerationDemoPainter
{
    public static void Paint(SKCanvas c, GenerationDemoFrame frame, int width, int height, IStampImageSource? source)
    {
        using var stamps = source?.Lease();
        var (document, view, marks) = frame;
        var scale = width / (view.Right - view.Left);
        c.Save();
        c.ClipRect(new SKRect(0, 0, width, height));
        c.Clear(new SKColor(0x1A, 0x14, 0x0E));
        c.Save();
        c.Scale((float)scale);
        c.Translate((float)-view.Left, (float)-view.Top);
        SceneRenderer.Render(c, document, new RenderOptions { IsExport = true, VisibleWorld = view, ViewScale = scale, Stamps = stamps });
        if (marks.FadeTo is { } next && marks.Fade > 0)
        {
            using var alpha = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Clamp(marks.Fade, 0, 1))) };
            c.SaveLayer(alpha);
            SceneRenderer.Render(c, next, new RenderOptions { IsExport = true, VisibleWorld = view, ViewScale = scale, Stamps = stamps });
            c.Restore();
        }
        c.Restore();
        DrawMarks(c, marks, view, scale, width, height);
        c.Restore();
    }

    private static void DrawMarks(SKCanvas c, GenerationDemoMarks m, WorldBounds view, double scale, int width, int height)
    {
        SKPoint P(double x, double y) => new((float)((x - view.Left) * scale), (float)((y - view.Top) * scale));
        using var gold = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#E7C982"), Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f,
            PathEffect = SKPathEffect.CreateDash([7, 5], 0) };
        using var goldFill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#40E7C982") };
        using var light = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F6E7C4"), Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f };
        using var lightFill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#30F6E7C4") };
        if (m.Layout is { } layout)
        {
            SKPath Hex(HexCoord h)
            {
                var p = new SKPath();
                for (var k = 0; k < 6; k++) { var (x, y) = layout.Corner(h, k); if (k == 0) p.MoveTo(P(x, y)); else p.LineTo(P(x, y)); }
                p.Close(); return p;
            }
            foreach (var h in m.Selected) { using var p = Hex(h); c.DrawPath(p, lightFill); c.DrawPath(p, light); }
            foreach (var h in m.Protected) { using var p = Hex(h); c.DrawPath(p, goldFill); c.DrawPath(p, gold); }
        }
        if (m.Outline is { Count: > 2 } outline)
        {
            using var line = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#E6F0D79A"), Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f,
                PathEffect = SKPathEffect.CreateDash([8, 6], 0) };
            using var path = new SKPath();
            path.MoveTo(P(outline[0].X, outline[0].Y));
            foreach (var p in outline.Skip(1)) path.LineTo(P(p.X, p.Y));
            path.Close(); c.DrawPath(path, line);
        }
        foreach (var r in m.Frames)
        {
            var box = new SKRect(P(r.Left, r.Top).X - 3, P(r.Left, r.Top).Y - 3, P(r.Right, r.Bottom).X + 3, P(r.Right, r.Bottom).Y + 3);
            c.DrawRect(box, goldFill); c.DrawRect(box, gold);
        }
        if (m.Preview)
        {
            using var frame = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#CCE7C982"), Style = SKPaintStyle.Stroke, StrokeWidth = 4,
                PathEffect = SKPathEffect.CreateDash([12, 8], 0) };
            c.DrawRect(new SKRect(3, 3, width - 3, height - 3), frame);
        }
        if (m.Progress >= 0)
        {
            using var back = new SKPaint { Color = SKColor.Parse("#AA1A140E") };
            using var bar = new SKPaint { Color = SKColor.Parse("#D4AE62") };
            c.DrawRect(new SKRect(0, height - 8, width, height), back);
            c.DrawRect(new SKRect(0, height - 8, (float)(width * m.Progress), height), bar);
        }
        if (m.Cursor is { } cursor)
        {
            var at = P(cursor.X, cursor.Y);
            if (m.BrushRadius > 0)
            {
                using var brush = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#E6F6E7C4"), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                c.DrawCircle(at, (float)(m.BrushRadius * scale * 1.6), brush);
            }
            else if (m.Click)
            {
                using var ring = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#CCE7C982"), Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
                c.DrawCircle(at, 18, ring);
            }
            using var arrow = new SKPath();
            arrow.MoveTo(at);
            foreach (var (x, y) in new[] { (0.0, 26.0), (7.0, 20.0), (13.0, 32.0), (17.0, 30.0), (11.0, 18.0), (20.0, 18.0) }) arrow.LineTo(at.X + (float)x, at.Y + (float)y);
            arrow.Close();
            using var fill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#1A140F") };
            using var edge = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F6E7C4"), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            c.DrawPath(arrow, fill); c.DrawPath(arrow, edge);
        }
        if (m.Note is { Length: > 0 } note)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 22);
            using var ink = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F0D79A") };
            using var back = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#DD1A140E") };
            var w = font.MeasureText(note);
            var box = new SKRect(width - w - 24, height - 48, width - 8, height - 14);
            c.DrawRoundRect(box, 6, 6, back);
            c.DrawText(note, box.Left + 8, box.Bottom - 9, font, ink);
        }
    }
}
