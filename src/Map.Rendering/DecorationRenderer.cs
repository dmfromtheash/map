using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Procedural vector art for the cartographic decorations of R08: scale bar, compass rose,
/// cartouche and live legend. Everything is drawn from the object's box, so it scales and exports
/// crisply at any size. Temporary «Таверна» look (ink on parchment) until the owner picks a design.
/// </summary>
internal static class DecorationRenderer
{
    private static readonly string[] Cardinal = ["С", "В", "Ю", "З"];

    public static void Draw(SKCanvas canvas, DecorationStyle style, SKRect rect, byte alpha, RenderOptions options)
    {
        var ink = SceneRenderer.ParseColor(style.InkColor).WithScaledAlpha(alpha);
        var paper = SceneRenderer.ParseColor(style.PaperColor).WithScaledAlpha(alpha);
        switch (style.Kind)
        {
            case DecorationKind.ScaleBar: DrawScaleBar(canvas, style, rect, ink, paper); break;
            case DecorationKind.Compass: DrawCompass(canvas, style.Variant, rect, ink, paper); break;
            case DecorationKind.Cartouche: DrawCartouche(canvas, style, rect, ink, paper); break;
            default: DrawLegend(canvas, style, rect, ink, paper, alpha, options); break;
        }
    }

    private static SKFont Font(float size, bool bold = false)
    {
        var resolution = TextFontResolver.Resolve("Georgia", "Times New Roman");
        return new SKFont(TextFontResolver.GetTypeface(resolution, bold ? 700 : 400), Math.Max(1, size));
    }

    private static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKPaint Stroke(SKColor color, float width) =>
        new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.5f, width), StrokeJoin = SKStrokeJoin.Round };

    private static void Centered(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint) =>
        canvas.DrawText(text, x - font.MeasureText(text) / 2, y, font, paint);

    // ------------------------------------------------------------------ scale bar

    private static void DrawScaleBar(SKCanvas canvas, DecorationStyle style, SKRect rect, SKColor ink, SKColor paper)
    {
        var margin = rect.Width * 0.05f;
        var layout = MapDecorations.ScaleBarLayout(style.Scale, rect.Width - 2 * margin);
        var length = (float)layout.TotalPixels;
        var left = rect.MidX - length / 2;
        var barHeight = Math.Max(2, rect.Height * 0.22f);
        var top = rect.Top + rect.Height * 0.45f;
        var segment = length / layout.Segments;
        var line = Math.Max(1, barHeight * 0.12f);
        using var inkFill = Fill(ink);
        using var paperFill = Fill(paper);
        using var outline = Stroke(ink, line);
        if (style.Variant == 1)
        {
            canvas.DrawLine(left, top + barHeight, left + length, top + barHeight, outline);
            for (var i = 0; i <= layout.Segments; i++)
            {
                var x = left + i * segment;
                canvas.DrawLine(x, top + (i % layout.Segments == 0 ? 0 : barHeight * 0.45f), x, top + barHeight, outline);
            }
        }
        else
        {
            for (var i = 0; i < layout.Segments; i++)
            {
                var cell = new SKRect(left + i * segment, top, left + (i + 1) * segment, top + barHeight);
                canvas.DrawRect(cell, i % 2 == 0 ? inkFill : paperFill);
            }

            canvas.DrawRect(new SKRect(left, top, left + length, top + barHeight), outline);
        }

        using var font = Font(Math.Max(4, rect.Height * 0.22f));
        var labelY = top - rect.Height * 0.08f;
        // The end labels sit inside the bar's span, so a bar that fills its box never spills past it.
        canvas.DrawText("0", left, labelY, font, inkFill);
        Centered(canvas, Number(layout.TotalDistance / 2), left + length / 2, labelY, font, inkFill);
        var total = Number(layout.TotalDistance) + " " + layout.Unit;
        canvas.DrawText(total, left + length - font.MeasureText(total), labelY, font, inkFill);
        if (style.Scale is null)
        {
            using var small = Font(Math.Max(3, rect.Height * 0.14f));
            Centered(canvas, "масштаб не задан", rect.MidX, top + barHeight + rect.Height * 0.25f, small, inkFill);
        }
    }

    private static string Number(double value) =>
        value.ToString(Math.Abs(value - Math.Round(value)) < 1e-9 ? "0" : "0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    // ------------------------------------------------------------------ compass rose

    private static void DrawCompass(SKCanvas canvas, int variant, SKRect rect, SKColor ink, SKColor paper)
    {
        var cx = rect.MidX;
        var cy = rect.MidY;
        var radius = Math.Min(rect.Width, rect.Height) / 2 * 0.78f;
        var line = Math.Max(0.6f, radius * 0.02f);
        using var inkFill = Fill(ink);
        using var paperFill = Fill(paper);
        using var outline = Stroke(ink, line);
        var points = variant switch { 1 => 4, 2 => 16, 3 => 4, _ => 8 };
        if (variant is 0 or 2)
        {
            canvas.DrawCircle(cx, cy, radius * 0.82f, outline);
            canvas.DrawCircle(cx, cy, radius * 0.76f, outline);
        }

        if (variant == 2)
        {
            for (var i = 0; i < 64; i++)
            {
                var a = i * Math.PI * 2 / 64;
                var inner = radius * (i % 4 == 0 ? 0.68f : 0.72f);
                canvas.DrawLine(cx + (float)Math.Sin(a) * inner, cy - (float)Math.Cos(a) * inner,
                    cx + (float)Math.Sin(a) * radius * 0.76f, cy - (float)Math.Cos(a) * radius * 0.76f, outline);
            }
        }

        if (variant == 3)
        {
            // Minimal: one long north arrow over a thin cross.
            canvas.DrawLine(cx - radius * 0.6f, cy, cx + radius * 0.6f, cy, outline);
            using var arrow = new SKPath();
            arrow.MoveTo(cx, cy - radius);
            arrow.LineTo(cx + radius * 0.16f, cy + radius * 0.35f);
            arrow.LineTo(cx, cy + radius * 0.18f);
            arrow.LineTo(cx - radius * 0.16f, cy + radius * 0.35f);
            arrow.Close();
            canvas.DrawPath(arrow, inkFill);
        }
        else
        {
            // Longer cardinal points drawn last so they sit on top of the intercardinal ones.
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < points; i++)
                {
                    var cardinal = i % (points / 4) == 0;
                    if (cardinal != (pass == 1)) continue;
                    var a = i * Math.PI * 2 / points;
                    var length = cardinal ? radius : radius * (points == 16 && i % 2 == 1 ? 0.45f : 0.62f);
                    var half = (float)(Math.PI / points) * (cardinal ? 1.0f : 0.9f);
                    var width = length * (cardinal ? 0.16f : 0.2f);
                    Ray(canvas, cx, cy, a, length, width, half, inkFill, paperFill, outline);
                }
            }
        }

        canvas.DrawCircle(cx, cy, radius * 0.06f, inkFill);
        using var font = Font(radius * 0.2f, bold: true);
        var labelRadius = radius * 1.12f;
        for (var i = 0; i < 4; i++)
        {
            if (variant == 3 && i != 0) continue;
            var a = i * Math.PI / 2;
            var x = cx + (float)Math.Sin(a) * labelRadius;
            var y = cy - (float)Math.Cos(a) * labelRadius + font.Size * 0.35f;
            Centered(canvas, Cardinal[i], x, y, font, inkFill);
        }
    }

    /// <summary>One point of the rose: a kite split down its axis, one half inked and one paper.</summary>
    private static void Ray(SKCanvas canvas, float cx, float cy, double angle, float length, float width, float half,
        SKPaint ink, SKPaint paper, SKPaint outline)
    {
        float X(double a, float r) => cx + (float)Math.Sin(a) * r;
        float Y(double a, float r) => cy - (float)Math.Cos(a) * r;
        var tip = new SKPoint(X(angle, length), Y(angle, length));
        var left = new SKPoint(X(angle - Math.PI / 2, width / 2), Y(angle - Math.PI / 2, width / 2));
        var right = new SKPoint(X(angle + Math.PI / 2, width / 2), Y(angle + Math.PI / 2, width / 2));
        var centre = new SKPoint(cx, cy);
        using var a = new SKPath();
        a.MoveTo(centre); a.LineTo(left); a.LineTo(tip); a.Close();
        using var b = new SKPath();
        b.MoveTo(centre); b.LineTo(tip); b.LineTo(right); b.Close();
        canvas.DrawPath(a, ink);
        canvas.DrawPath(b, paper);
        canvas.DrawPath(a, outline);
        canvas.DrawPath(b, outline);
    }

    // ------------------------------------------------------------------ cartouche

    private static void DrawCartouche(SKCanvas canvas, DecorationStyle style, SKRect rect, SKColor ink, SKColor paper)
    {
        var line = Math.Max(0.8f, Math.Min(rect.Width, rect.Height) * 0.018f);
        using var paperFill = Fill(paper);
        using var inkFill = Fill(ink);
        using var outer = Stroke(ink, line * 1.6f);
        using var inner = Stroke(ink, line * 0.7f);
        var inset = line * 4;
        switch (style.Variant)
        {
            case 1:
            {
                // Ribbon banner: a band with folded swallow-tail ends.
                var tail = rect.Width * 0.1f;
                var band = new SKRect(rect.Left + tail, rect.Top + rect.Height * 0.12f, rect.Right - tail, rect.Bottom - rect.Height * 0.12f);
                foreach (var sign in new[] { -1, 1 })
                {
                    var edge = sign < 0 ? band.Left : band.Right;
                    var end = sign < 0 ? rect.Left : rect.Right;
                    using var ribbon = new SKPath();
                    ribbon.MoveTo(edge, band.Top + band.Height * 0.2f);
                    ribbon.LineTo(end, band.Top + band.Height * 0.2f);
                    ribbon.LineTo(end + sign * -tail * 0.45f, band.MidY + band.Height * 0.1f);
                    ribbon.LineTo(end, band.Bottom + band.Height * 0.08f);
                    ribbon.LineTo(edge, band.Bottom + band.Height * 0.08f);
                    ribbon.Close();
                    canvas.DrawPath(ribbon, paperFill);
                    canvas.DrawPath(ribbon, inner);
                }

                canvas.DrawRect(band, paperFill);
                canvas.DrawRect(band, outer);
                canvas.DrawRect(SKRect.Inflate(band, -inset, -inset), inner);
                break;
            }
            case 2:
            {
                canvas.DrawOval(rect, paperFill);
                canvas.DrawOval(rect, outer);
                canvas.DrawOval(SKRect.Inflate(rect, -inset, -inset), inner);
                break;
            }
            default:
            {
                var corner = Math.Min(rect.Width, rect.Height) * 0.18f;
                canvas.DrawRoundRect(rect, corner, corner, paperFill);
                canvas.DrawRoundRect(rect, corner, corner, outer);
                var r2 = SKRect.Inflate(rect, -inset, -inset);
                canvas.DrawRoundRect(r2, corner * 0.7f, corner * 0.7f, inner);
                // Small diamonds at the middle of each side.
                var d = inset * 0.9f;
                foreach (var p in new[] { new SKPoint(rect.MidX, rect.Top), new SKPoint(rect.MidX, rect.Bottom), new SKPoint(rect.Left, rect.MidY), new SKPoint(rect.Right, rect.MidY) })
                {
                    using var diamond = new SKPath();
                    diamond.MoveTo(p.X, p.Y - d); diamond.LineTo(p.X + d, p.Y); diamond.LineTo(p.X, p.Y + d); diamond.LineTo(p.X - d, p.Y); diamond.Close();
                    canvas.DrawPath(diamond, inkFill);
                }
                break;
            }
        }

        var title = style.Title.Length == 0 ? "Название карты" : style.Title;
        var hasSubtitle = style.Subtitle.Length > 0;
        var size = rect.Height * (hasSubtitle ? 0.28f : 0.34f);
        using var titleFont = Font(size, bold: true);
        var maxWidth = rect.Width * (style.Variant == 1 ? 0.62f : 0.78f);
        var measured = titleFont.MeasureText(title);
        if (measured > maxWidth) titleFont.Size = size * maxWidth / measured;
        var titleY = rect.MidY + titleFont.Size * (hasSubtitle ? 0.05f : 0.35f);
        Centered(canvas, title, rect.MidX, titleY, titleFont, inkFill);
        if (hasSubtitle)
        {
            using var subFont = Font(size * 0.5f);
            var sub = subFont.MeasureText(style.Subtitle);
            if (sub > maxWidth) subFont.Size = subFont.Size * maxWidth / sub;
            Centered(canvas, style.Subtitle, rect.MidX, titleY + size * 0.75f, subFont, inkFill);
        }
    }

    // ------------------------------------------------------------------ legend

    private static void DrawLegend(SKCanvas canvas, DecorationStyle style, SKRect rect, SKColor ink, SKColor paper, byte alpha, RenderOptions options)
    {
        var line = Math.Max(0.8f, rect.Width * 0.008f);
        using var paperFill = Fill(paper);
        using var inkFill = Fill(ink);
        using var outer = Stroke(ink, line * 1.5f);
        using var inner = Stroke(ink, line * 0.6f);
        canvas.DrawRect(rect, paperFill);
        canvas.DrawRect(rect, outer);
        canvas.DrawRect(SKRect.Inflate(rect, -line * 3, -line * 3), inner);

        var heading = style.Title.Length == 0 ? "Условные обозначения" : style.Title;
        var pad = rect.Width * 0.07f;
        var rows = Math.Max(1, style.Entries.Count);
        var headingSize = Math.Min(rect.Width * 0.075f, rect.Height * 0.09f);
        using var headingFont = Font(headingSize, bold: true);
        var hw = headingFont.MeasureText(heading);
        if (hw > rect.Width - 2 * pad) headingFont.Size = headingSize * (rect.Width - 2 * pad) / hw;
        var top = rect.Top + pad + headingFont.Size;
        Centered(canvas, heading, rect.MidX, top, headingFont, inkFill);
        top += headingFont.Size * 0.6f;

        var rowHeight = Math.Min((rect.Bottom - pad - top) / rows, rect.Height * 0.12f);
        if (rowHeight <= 0) return;
        using var font = Font(rowHeight * 0.48f);
        if (style.Entries.Count == 0)
        {
            Centered(canvas, "Пока нечего показать", rect.MidX, top + rowHeight * 0.7f, font, inkFill);
            return;
        }

        var sample = rowHeight * 0.72f;
        for (var i = 0; i < style.Entries.Count; i++)
        {
            var entry = style.Entries[i];
            var y = top + i * rowHeight + (rowHeight - sample) / 2;
            var box = new SKRect(rect.Left + pad, y, rect.Left + pad + sample, y + sample);
            DrawSample(canvas, entry, box, alpha, options, inner);
            var label = entry.Label;
            var room = rect.Right - pad - (box.Right + sample * 0.4f);
            while (label.Length > 2 && font.MeasureText(label) > room) label = label[..^2] + "…";
            canvas.DrawText(label, box.Right + sample * 0.4f, box.MidY + font.Size * 0.35f, font, inkFill);
        }
    }

    private static void DrawSample(SKCanvas canvas, LegendEntry entry, SKRect box, byte alpha, RenderOptions options, SKPaint outline)
    {
        var color = SceneRenderer.ParseColor(entry.Color).WithScaledAlpha(alpha);
        switch (entry.Kind)
        {
            case LegendEntryKind.Asset:
                if (entry.AssetSha256 is { } sha && options.Stamps?.TryGetImage(sha) is { } image && image.Width > 0 && image.Height > 0)
                {
                    var fit = Math.Min(box.Width / image.Width, box.Height / image.Height);
                    var w = image.Width * fit; var h = image.Height * fit;
                    var target = new SKRect(box.MidX - w / 2, box.MidY - h / 2, box.MidX + w / 2, box.MidY + h / 2);
                    using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
                    canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                }
                else
                {
                    using var placeholder = Fill(color);
                    canvas.DrawCircle(box.MidX, box.MidY, box.Width * 0.3f, placeholder);
                }
                break;
            case LegendEntryKind.Road or LegendEntryKind.River:
            {
                using var stroke = Stroke(color, box.Height * (entry.Kind == LegendEntryKind.River ? 0.22f : 0.18f));
                stroke.StrokeCap = SKStrokeCap.Round;
                using var path = new SKPath();
                path.MoveTo(box.Left, box.MidY + box.Height * 0.2f);
                path.CubicTo(box.Left + box.Width * 0.35f, box.Top, box.Left + box.Width * 0.65f, box.Bottom, box.Right, box.MidY - box.Height * 0.2f);
                canvas.DrawPath(path, stroke);
                break;
            }
            default:
            {
                using var fill = Fill(color);
                canvas.DrawRect(box, fill);
                canvas.DrawRect(box, outline);
                break;
            }
        }
    }
}
