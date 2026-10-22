using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

public static partial class HexLayerRenderer
{
    private static void DrawJourneyOverlay(SKCanvas canvas, HexLayer layer, HexOverlay overlay, double scale)
    {
        var pixel = (float)(1 / Math.Max(scale, 1e-6)); var layout = layer.Board.Layout;
        using var ink = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#26394A") };
        using var white = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#FFFDF6") };
        using var font = new SKFont(SKTypeface.Default, 12 * pixel) { Embolden = true };
        void Badge(string text, double x, double y, SKColor color)
        {
            var width = font.MeasureText(text); var box = new SKRect((float)x - width / 2 - 9 * pixel, (float)y - 14 * pixel,
                (float)x + width / 2 + 9 * pixel, (float)y + 8 * pixel);
            ink.Color = SKColor.Parse("#26394A"); canvas.DrawRoundRect(box, 7 * pixel, 7 * pixel, ink);
            box.Inflate(-pixel, -pixel); white.Color = color; canvas.DrawRoundRect(box, 6 * pixel, 6 * pixel, white);
            canvas.DrawText(text, (float)x - width / 2, (float)y + 2 * pixel, font, ink);
        }
        if (overlay.Reach is { } reach)
        {
            foreach (var band in reach.Bands.Reverse())
            {
                using var shape = new SKPath { FillType = SKPathFillType.EvenOdd };
                foreach (var contour in band.Contours)
                    shape.AddPoly(contour.Select(p => new SKPoint((float)p.X, (float)p.Y)).ToArray(), close: true);
                using var fill = new SKPaint { IsAntialias = true, Color = band.Day switch
                { 1 => new SKColor(38, 152, 126, 62), 2 => new SKColor(54, 133, 186, 47), _ => new SKColor(93, 111, 170, 34) } };
                canvas.DrawPath(shape, fill);
                using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f * pixel,
                    Color = band.Day switch { 1 => SKColor.Parse("#2D8775"), 2 => SKColor.Parse("#3E7FA4"), _ => SKColor.Parse("#6D78A3") } };
                canvas.DrawPath(shape, line);
                var outer = band.Contours.OrderByDescending(c => c.Count).FirstOrDefault();
                if (outer is not null)
                {
                    var at = outer.OrderBy(p => p.Y).ElementAt(Math.Min(outer.Count - 1, outer.Count / 8));
                    Badge(band.Day == 1 ? "1 день" : $"{band.Day} дня", at.X, at.Y, SKColor.Parse("#F2F8F3"));
                }
            }
        }
        var selected = Math.Clamp(overlay.SelectedJourney, 0, Math.Max(0, overlay.Journeys.Count - 1));
        foreach (var i in Enumerable.Range(0, overlay.Journeys.Count).OrderBy(i => i == selected ? 1 : 0))
        {
            var journey = overlay.Journeys[i]; var points = journey.Route.Hexes;
            if (points.Count == 0) continue;
            var active = i == selected; using var path = new SKPath();
            for (var n = 0; n < points.Count; n++)
            {
                var (x, y) = layout.Center(points[n]);
                if (n == 0) path.MoveTo((float)x, (float)y);
                else if (overlay.TravelMode == TravelMode.Boat)
                {
                    var a = layout.Center(points[n - 1]); var dx = x - a.X; var dy = y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
                    for (var j = 1; j <= 16; j++)
                    {
                        var t = j / 16.0; var wave = Math.Sin(t * Math.PI * 4) * 2.3 * pixel;
                        path.LineTo((float)(a.X + dx * t - dy / length * wave), (float)(a.Y + dy * t + dx / length * wave));
                    }
                }
                else path.LineTo((float)x, (float)y);
            }
            var color = active ? overlay.TravelMode == TravelMode.Boat ? SKColor.Parse("#238CD3") : SKColor.Parse("#F1B94D") : SKColor.Parse("#B4BCC7");
            using var casing = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (active ? 7 : 5) * pixel,
                Color = active ? SKColor.Parse("#26394A") : new SKColor(64, 77, 90, 145), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (active ? 4 : 3) * pixel,
                Color = color, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using var dash = overlay.TravelMode == TravelMode.Walk ? SKPathEffect.CreateDash([7 * pixel, 5 * pixel], 0) : null;
            line.PathEffect = dash; casing.PathEffect = dash;
            canvas.DrawPath(path, casing); canvas.DrawPath(path, line);
            if (overlay.TravelMode == TravelMode.Cart)
            {
                line.PathEffect = null; line.StrokeWidth = 1.4f * pixel; line.Color = casing.Color; canvas.DrawPath(path, line);
            }
            if (!active)
            {
                // Берём точку, не лежащую на выбранном пути: плашки альтернатив остаются раздельными.
                var others = overlay.Journeys[selected].Route.Hexes.ToHashSet();
                var distinct = points.Where(h => !others.Contains(h)).ToArray(); var at = distinct.Length > 0 ? distinct[distinct.Length / 2] : points[points.Count / 2];
                var (x, y) = layout.Center(at); var delta = Math.Max(0, journey.Route.Days - overlay.Journeys[0].Route.Days);
                Badge($"+{delta:0.#} дн. / " + (journey.Crossings == 0 ? "без переправы" : $"переправ: {journey.Crossings}"), x, y, SKColor.Parse("#E5EAF0"));
                continue;
            }
            foreach (var day in journey.Days)
            {
                var (x, y) = layout.Center(day.Camp); Badge(day.Day.ToString(), x, y, color);
            }
            var start = layout.Center(points[0]); var finish = layout.Center(points[^1]);
            white.Color = SKColors.White; ink.Color = SKColor.Parse("#26394A");
            canvas.DrawCircle((float)start.X, (float)start.Y, 7 * pixel, ink); canvas.DrawCircle((float)start.X, (float)start.Y, 4 * pixel, white);
            using var pin = new SKPath(); pin.MoveTo((float)finish.X, (float)finish.Y);
            pin.CubicTo((float)finish.X - 20 * pixel, (float)finish.Y - 22 * pixel, (float)finish.X + 20 * pixel, (float)finish.Y - 22 * pixel, (float)finish.X, (float)finish.Y);
            canvas.DrawPath(pin, ink); canvas.DrawCircle((float)finish.X, (float)finish.Y - 12 * pixel, 3 * pixel, white);
        }
    }
}
