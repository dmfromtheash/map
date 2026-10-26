using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// R29: the marks of transitions - a paper disc with an ink ring and a small ink glyph: steps for
/// stairs, an arch for a cave mouth, a square with a downward stroke for a shaft, a double ring for
/// a portal. A mark keeps about 22 screen pixels while editing and 26 map pixels in a picture. The
/// end of a one-way transition that cannot be entered gets a small bar across.
/// </summary>
internal static class TransitionRenderer
{
    private static readonly SKColor Paper = new(0xF2, 0xE8, 0xD0);
    private static readonly SKColor Ink = new(0x2B, 0x25, 0x20);

    public static void Draw(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        if (document.Transitions.IsDefaultOrEmpty) return;
        var radius = options.IsExport ? 13.0 : 11 / Math.Max(1e-6, options.ViewScale);
        foreach (var t in document.Transitions)
        {
            if (Shows(t.From, options)) Mark(canvas, t.Kind, t.From.X, t.From.Y, radius, closed: t.Direction == TransitionDirection.Back);
            if (t.To is { MapId: null } to && Shows(to, options)) Mark(canvas, t.Kind, to.X, to.Y, radius, closed: t.Direction == TransitionDirection.Forward);
        }
    }

    private static bool Shows(TransitionEnd end, RenderOptions options) =>
        end.HexLevel is null || options.HexLevel is null || end.HexLevel == options.HexLevel;

    private static void Mark(SKCanvas canvas, TransitionKind kind, double x, double y, double r, bool closed)
    {
        var (cx, cy, radius) = ((float)x, (float)y, (float)r);
        using var fill = new SKPaint { Color = Paper, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var ink = new SKPaint { Color = Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = radius * .16f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var shadow = new SKPaint { Color = Ink.WithAlpha(60), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, radius * .25f) };
        canvas.DrawCircle(cx + (radius * .1f), cy + (radius * .15f), radius, shadow);
        canvas.DrawCircle(cx, cy, radius, fill);
        canvas.DrawCircle(cx, cy, radius, ink);
        var g = radius * .52f;
        using var path = new SKPath();
        switch (kind)
        {
            case TransitionKind.Stairs:
                // Three steps rising to the right.
                path.MoveTo(cx - g, cy + (g * .8f));
                path.LineTo(cx - (g * .35f), cy + (g * .8f)); path.LineTo(cx - (g * .35f), cy + (g * .15f));
                path.LineTo(cx + (g * .3f), cy + (g * .15f)); path.LineTo(cx + (g * .3f), cy - (g * .5f));
                path.LineTo(cx + g, cy - (g * .5f));
                break;
            case TransitionKind.CaveEntrance:
                // An arch over the ground line.
                path.MoveTo(cx - g, cy + (g * .75f)); path.LineTo(cx + g, cy + (g * .75f));
                path.MoveTo(cx - (g * .7f), cy + (g * .75f));
                path.CubicTo(cx - (g * .7f), cy - (g * .9f), cx + (g * .7f), cy - (g * .9f), cx + (g * .7f), cy + (g * .75f));
                break;
            case TransitionKind.Shaft:
                // A square mouth with a stroke going down.
                path.AddRect(new SKRect(cx - (g * .75f), cy - (g * .75f), cx + (g * .75f), cy + (g * .75f)));
                path.MoveTo(cx, cy - (g * .35f)); path.LineTo(cx, cy + (g * .45f));
                path.MoveTo(cx - (g * .3f), cy + (g * .15f)); path.LineTo(cx, cy + (g * .45f)); path.LineTo(cx + (g * .3f), cy + (g * .15f));
                break;
            default:
                // A portal: two rings.
                path.AddCircle(cx, cy, g * .85f);
                path.AddCircle(cx, cy, g * .4f);
                break;
        }
        canvas.DrawPath(path, ink);
        if (closed)
        {
            // This end cannot be entered: a bar across the lower right.
            ink.StrokeWidth = radius * .2f;
            canvas.DrawLine(cx + (radius * .45f), cy + (radius * 1.05f), cx + (radius * 1.05f), cy + (radius * .45f), ink);
        }
    }
}
