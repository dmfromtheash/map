using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public static partial class HexLayerRenderer
{
    private static void DrawParty(SKCanvas canvas, HexLayer layer, double scale)
    {
        var layout = layer.Board.Layout;
        using var dash = SKPathEffect.CreateDash([(float)(layout.Size * .18), (float)(layout.Size * .12)], 0);
        using var trail = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F0BE66"),
            Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(1 / scale, layout.Size * .035), PathEffect = dash };
        using var route = new SKPath();
        for (var i = 0; i < layer.Party.History.Count; i++)
        {
            var p = layout.Center(layer.Party.History[i]);
            if (i == 0) route.MoveTo((float)p.X, (float)p.Y); else route.LineTo((float)p.X, (float)p.Y);
        }
        canvas.DrawPath(route, trail);
        if (layer.Party.Position is not { } h) return;
        var (x, y) = layout.Center(h);
        using var fill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F0BE66") };
        using var rim = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#35291F"), Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(layout.Size * .045) };
        var radius = (float)(layout.Size * .25);
        canvas.DrawCircle((float)x, (float)y, radius, fill);
        canvas.DrawCircle((float)x, (float)y, radius, rim);
        using var font = new SKFont(SKTypeface.Default, (float)(layout.Size * .3));
        using var ink = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#35291F") };
        canvas.DrawText("П", (float)x, (float)y + (float)layout.Size * .1f, SKTextAlign.Center, font, ink);
    }
}
