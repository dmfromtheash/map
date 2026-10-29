using SkiaSharp;

namespace Map.Export;

public enum HexPaperFormat { A4, A3, Letter, Custom, A2, A1, A0 }

public static partial class HexPrintExporter
{
    /// <summary>Physical sheet size. Bleed is inside the sheet margins, outside the map trim rectangle.</summary>
    public static (double Width, double Height) PaperSizeMillimetres(HexPrintOptions options)
    {
        var (width, height) = (options.PaperFormat ?? (options.A3 ? HexPaperFormat.A3 : HexPaperFormat.A4)) switch
        {
            HexPaperFormat.A4 => (210d, 297d),
            HexPaperFormat.A3 => (297d, 420d),
            HexPaperFormat.A2 => (420d, 594d),
            HexPaperFormat.A1 => (594d, 841d),
            HexPaperFormat.A0 => (841d, 1189d),
            HexPaperFormat.Letter => (215.9, 279.4),
            HexPaperFormat.Custom => (options.CustomWidthMillimetres, options.CustomHeightMillimetres),
            _ => throw new InvalidOperationException("Неизвестный формат бумаги."),
        };
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 60 || height < 60 || width > 2000 || height > 2000)
            throw new InvalidOperationException("Размер листа: от 60 до 2000 мм по каждой стороне.");
        if (!double.IsFinite(options.BleedMillimetres) || options.BleedMillimetres < 0 || options.BleedMillimetres > 5)
            throw new InvalidOperationException("Вылет за линию реза: от 0 до 5 мм.");
        if (!double.IsFinite(options.HexMillimetres) || !double.IsFinite(options.OverlapMillimetres))
            throw new InvalidOperationException("Размер гекса и перекрытие листов должны быть числами.");
        if (options.OverlapMillimetres is < 0 or > 30)
            throw new InvalidOperationException("Перекрытие листов: от 0 до 30 мм.");
        // Custom dimensions are literal width/height; orientation only affects standard paper.
        if (options.Landscape && options.PaperFormat != HexPaperFormat.Custom) (width, height) = (height, width);
        return (width, height);
    }

    private static void DrawCropMarks(SKCanvas canvas, SKRect trim, float bleed)
    {
        using var ink = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 0.25f, IsAntialias = true };
        var gap = bleed + 2f;
        const float Length = 8f;
        foreach (var y in new[] { trim.Top, trim.Bottom })
        {
            canvas.DrawLine(trim.Left - gap - Length, y, trim.Left - gap, y, ink);
            canvas.DrawLine(trim.Right + gap, y, trim.Right + gap + Length, y, ink);
        }
        foreach (var x in new[] { trim.Left, trim.Right })
        {
            canvas.DrawLine(x, trim.Top - gap - Length, x, trim.Top - gap, ink);
            canvas.DrawLine(x, trim.Bottom + gap, x, trim.Bottom + gap + Length, ink);
        }
    }
}
