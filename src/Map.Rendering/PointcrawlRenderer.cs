using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// R32: the pointcrawl over the map, in the ink of an old route map. A way is a dark line with its
/// hours on a small paper tag and its danger as red-brown dots; a one-way way has an arrowhead; a
/// hidden way (the master's) is dashed and faint. A place is a paper disc with an ink ring and its
/// number, its name below; a place not visited yet has a broken ring; an unknown place for the
/// players shows «?». Sizes stay constant on screen while editing and in map units in a picture.
/// </summary>
internal static class PointcrawlRenderer
{
    private static readonly SKColor Paper = new(0xF4, 0xEA, 0xD2);
    private static readonly SKColor Ink = new(0x2E, 0x26, 0x1F);
    private static readonly SKColor Danger = new(0x9A, 0x2E, 0x22);

    public static void Draw(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        var crawl = document.Pointcrawl;
        if (!crawl.Shown || crawl.Nodes.IsDefaultOrEmpty) return;
        var unit = options.IsExport ? 1.25 : 1 / Math.Max(1e-6, options.ViewScale);
        var players = options.HexPlayerView;
        var nodes = crawl.Nodes.ToDictionary(n => n.Id);
        var radius = (float)(13 * unit);
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var numberFont = new SKFont(typeface, (float)(12 * unit));
        using var regular = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
        using var nameFont = new SKFont(regular, (float)(12 * unit));
        using var tagFont = new SKFont(typeface, (float)(10 * unit));
        using var line = new SKPaint { Color = Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(2.2 * unit), StrokeCap = SKStrokeCap.Round };
        using var fill = new SKPaint { Color = Paper, IsAntialias = true };
        using var ink = new SKPaint { Color = Ink, IsAntialias = true };
        using var halo = new SKPaint { Color = Paper.WithAlpha(230), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(3.5 * unit), StrokeJoin = SKStrokeJoin.Round };

        foreach (var edge in crawl.Edges)
        {
            if (players && edge.Hidden) continue;
            if (!nodes.TryGetValue(edge.From, out var a) || !nodes.TryGetValue(edge.To, out var b)) continue;
            var (ax, ay, bx, by) = ((float)a.X, (float)a.Y, (float)b.X, (float)b.Y);
            var length = MathF.Max(1e-3f, MathF.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay))));
            var (ux, uy) = ((bx - ax) / length, (by - ay) / length);
            // Lines start and end at the discs' rims.
            var (sx, sy, ex, ey) = (ax + (ux * radius), ay + (uy * radius), bx - (ux * radius), by - (uy * radius));
            line.PathEffect = edge.Hidden ? SKPathEffect.CreateDash([(float)(7 * unit), (float)(5 * unit)], 0) : null;
            line.Color = edge.Hidden ? Ink.WithAlpha(120) : Ink;
            canvas.DrawLine(sx, sy, ex, ey, line);
            if (edge.OneWay)
            {
                using var head = new SKPath();
                var size = (float)(9 * unit);
                head.MoveTo(ex, ey);
                head.LineTo(ex - (ux * size) - (uy * size * .55f), ey - (uy * size) + (ux * size * .55f));
                head.LineTo(ex - (ux * size) + (uy * size * .55f), ey - (uy * size) - (ux * size * .55f));
                head.Close();
                canvas.DrawPath(head, ink);
            }

            // The tag: hours on paper at the middle, danger dots after it.
            var (mx, my) = ((ax + bx) / 2, (ay + by) / 2);
            var text = edge.Hours % 1 == 0 ? $"{edge.Hours:0} ч" : $"{edge.Hours:0.#} ч";
            var width = tagFont.MeasureText(text) + (float)(8 * unit) + (edge.Danger * (float)(6 * unit));
            var tag = new SKRect(mx - (width / 2), my - (float)(8 * unit), mx + (width / 2), my + (float)(8 * unit));
            canvas.DrawRoundRect(tag, (float)(4 * unit), (float)(4 * unit), fill);
            line.PathEffect = null; line.Color = Ink.WithAlpha(edge.Hidden ? (byte)120 : (byte)255);
            var thin = line.StrokeWidth; line.StrokeWidth = (float)(1 * unit);
            canvas.DrawRoundRect(tag, (float)(4 * unit), (float)(4 * unit), line);
            line.StrokeWidth = thin;
            canvas.DrawText(text, tag.Left + (float)(4 * unit), my + (float)(3.5 * unit), SKTextAlign.Left, tagFont, ink);
            using var dot = new SKPaint { Color = Danger, IsAntialias = true };
            for (var i = 0; i < edge.Danger; i++)
                canvas.DrawCircle(tag.Right - (float)(4 * unit) - (i * (float)(6 * unit)) - (float)(1 * unit), my, (float)(2.2 * unit), dot);
        }

        foreach (var node in crawl.Nodes)
        {
            var (x, y) = ((float)node.X, (float)node.Y);
            canvas.DrawCircle(x, y, radius, fill);
            line.PathEffect = !players && !node.Visited ? SKPathEffect.CreateDash([(float)(4 * unit), (float)(3 * unit)], 0) : null;
            line.Color = Ink;
            canvas.DrawCircle(x, y, radius, line);
            line.PathEffect = null;
            var unknown = node.Name == "?";
            var label = unknown ? "?" : crawl.NumberOf(node.Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
            canvas.DrawText(label, x, y + (float)(4.3 * unit), SKTextAlign.Center, numberFont, ink);
            if (unknown) continue;
            using var path = nameFont.GetTextPath(node.Name, new SKPoint(x - (nameFont.MeasureText(node.Name) / 2), y + radius + (float)(14 * unit)));
            canvas.DrawPath(path, halo);
            canvas.DrawPath(path, ink);
        }
    }
}
