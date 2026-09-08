using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Earth ledges, pen hatching and broken shore waves derived from the shared water outline.</summary>
internal static class RiverBankRenderer
{
    private readonly record struct Point(SKPoint Edge, SKPoint Normal, SKPoint Tangent, float Width);

    public static void Draw(SKCanvas canvas, SKPath water, RiverStyle style, double scale,
        Action<SKCanvas, SKPaint>? land, SKRect bounds)
    {
        if (style.BankReliefWidth <= 0 || style.BankReliefWidth * scale < .65) return;
        canvas.SaveLayer(bounds, null);
        canvas.Save();
        canvas.ClipPath(water, SKClipOperation.Difference, antialias: true);
        using var earth = new SKPaint { Color = SceneRenderer.ParseColor(style.BankReliefColor), IsAntialias = true };
        using var rim = new SKPaint { Color = SceneRenderer.ParseColor(style.BankColor).WithAlpha(210),
            Style = SKPaintStyle.Stroke, StrokeWidth = .8f, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        using var hatch = new SKPaint { Color = SceneRenderer.ParseColor(style.BankColor).WithAlpha(
            (byte)Math.Round(style.BankHatchOpacity * 255)), Style = SKPaintStyle.Stroke,
            StrokeWidth = .65f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var light = new SKPaint { Color = new SKColor(240, 224, 187, 90), Style = SKPaintStyle.Stroke,
            StrokeWidth = .65f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };

        using var measure = new SKPathMeasure(water, false);
        do
        {
            var length = measure.Length;
            if (length < 2) continue;
            // World-space samples and a finite bound keep tile/export and viewport drawings equal.
            var count = Math.Clamp((int)Math.Ceiling(length / Math.Max(.8, style.BankReliefWidth / 6)), 8, 8192);
            var points = new Point[count];
            using var ribbon = new SKPath();
            using var outline = new SKPath();
            for (var i = 0; i < count; i++)
            {
                measure.GetPositionAndTangent(length * i / count, out var p, out var tangent);
                var normal = Outward(water, p, tangent);
                var undulation = RiverGeometry.Noise(3181, p.X * .043 + p.Y * .029);
                // The bank facing the map's upper edge is higher; the opposite side is a low shore.
                // Broad variations give a ledge, not a uniformly inflated tube.
                var facing = Math.Clamp((-normal.Y + normal.X * .25 + 1) / 2, 0, 1);
                var width = (float)(style.BankReliefWidth * (.30 + .83 * facing + .20 * undulation));
                points[i] = new Point(p, normal, tangent, width);
                var outer = Add(p, normal, width);
                if (i == 0) { ribbon.MoveTo(outer); outline.MoveTo(outer); }
                else { ribbon.LineTo(outer); outline.LineTo(outer); }
            }
            outline.Close();
            for (var i = count - 1; i >= 0; i--) ribbon.LineTo(points[i].Edge);
            ribbon.Close();
            canvas.DrawPath(ribbon, earth);
            canvas.DrawPath(outline, rim);
            if (style.BankHatchOpacity <= 0 || style.BankReliefWidth * scale < 2.5) continue;

            canvas.Save();
            canvas.ClipPath(ribbon, SKClipOperation.Intersect, antialias: true);
            var spacing = Math.Max(3.4, style.BankReliefWidth * .65);
            var next = spacing * .3;
            for (var i = 0; i < count; i++)
            {
                var distance = length * i / count;
                if (distance < next) continue;
                var p = points[i];
                var variation = RiverGeometry.Noise(5731, p.Edge.X * .21 + p.Edge.Y * .14);
                next = distance + spacing * (.90 + .60 * variation);
                if (p.Width * scale < 2) continue;
                var start = Add(p.Edge, p.Normal, p.Width * .15f);
                var end = Add(Add(p.Edge, p.Normal, p.Width * (float)(.60 + .30 * variation)),
                    p.Tangent, p.Width * (float)(.12 + .16 * variation));
                var middle = Add(start, p.Normal, p.Width * .23f);
                using var stroke = new SKPath();
                stroke.MoveTo(start); stroke.QuadTo(middle, end);
                canvas.DrawPath(stroke, hatch);
                if (variation > .2) canvas.DrawLine(Add(start, p.Tangent, 1), Add(end, p.Tangent, 1), light);
            }
            canvas.Restore();
        } while (measure.NextContour());
        canvas.Restore();
        if (land is not null)
        {
            // Ledges belong to earth. They end at the coast, leaving an open estuary in the sea.
            using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
            canvas.SaveLayer(bounds, keep);
            using var coverage = new SKPaint();
            land(canvas, coverage);
            canvas.Restore();
        }
        canvas.Restore();
    }

    public static void DrawInkOutline(SKCanvas canvas, SKPath water, RiverStyle style)
    {
        using var ink = new SKPaint { Color = SceneRenderer.ParseColor(style.BankColor), IsAntialias = true };
        using var measure = new SKPathMeasure(water, false);
        do
        {
            if (measure.Length < 1) continue;
            var count = Math.Clamp((int)Math.Ceiling(measure.Length / 1.2), 8, 8192);
            var inner = new SKPoint[count];
            using var ribbon = new SKPath();
            for (var i = 0; i < count; i++)
            {
                measure.GetPositionAndTangent(measure.Length * i / count, out var p, out var tangent);
                var normal = new SKPoint(-tangent.Y, tangent.X);
                var pressure = (float)(style.BankWidth * (.45 + .18 * Math.Abs(tangent.X)
                    + .22 * RiverGeometry.Noise(9811, p.X * .091 + p.Y * .077)));
                var outer = Add(p, normal, pressure);
                inner[i] = Add(p, normal, -pressure);
                if (i == 0) ribbon.MoveTo(outer); else ribbon.LineTo(outer);
            }
            for (var i = count - 1; i >= 0; i--) ribbon.LineTo(inner[i]);
            ribbon.Close(); canvas.DrawPath(ribbon, ink);
        } while (measure.NextContour());
    }

    public static void DrawWaterLines(SKCanvas canvas, SKPath water, RiverStyle style, double scale)
    {
        if (style.HighlightOpacity <= 0 || scale < .18) return;
        canvas.Save();
        canvas.ClipPath(water, SKClipOperation.Intersect, antialias: true);
        using var paint = new SKPaint { Color = SceneRenderer.ParseColor(style.HighlightColor).WithAlpha(
            (byte)Math.Round(style.HighlightOpacity * 255)), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = .7f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var measure = new SKPathMeasure(water, false);
        do
        {
            var length = measure.Length;
            var count = Math.Clamp((int)Math.Ceiling(length / 1.25), 8, 8192);
            for (var strand = 0; strand < 2; strand++)
            {
                using var line = new SKPath();
                var open = false;
                for (var i = 0; i < count; i++)
                {
                    var distance = length * i / count;
                    measure.GetPositionAndTangent(distance, out var edge, out var tangent);
                    var normal = Outward(water, edge, tangent);
                    var drift = RiverGeometry.Noise(7471 + strand, edge.X * .027 + edge.Y * .041);
                    var offset = (float)(2.8 + strand * 4.3 + drift * .9);
                    var p = Add(edge, normal, -offset);
                    var visible = (distance + strand * 29) % (70 + strand * 13) < 35 + drift * 17;
                    // A narrow creek has no room for both shore waves; never draw one across its bank.
                    if (!visible || !water.Contains(p.X, p.Y) || !water.Contains(
                            p.X - normal.X * 1.8f, p.Y - normal.Y * 1.8f)) { open = false; continue; }
                    if (open) line.LineTo(p); else { line.MoveTo(p); open = true; }
                }
                canvas.DrawPath(line, paint);
            }
        } while (measure.NextContour());
        canvas.Restore();
    }

    private static SKPoint Outward(SKPath water, SKPoint p, SKPoint tangent)
    {
        var normal = new SKPoint(-tangent.Y, tangent.X);
        return water.Contains(p.X + normal.X * .35f, p.Y + normal.Y * .35f)
            ? new SKPoint(-normal.X, -normal.Y) : normal;
    }

    private static SKPoint Add(SKPoint p, SKPoint direction, float distance) =>
        new(p.X + direction.X * distance, p.Y + direction.Y * distance);
}
