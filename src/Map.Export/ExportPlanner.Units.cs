namespace Map.Export;

public enum ExportSizeUnit { Pixels, Millimetres, Inches }

public static partial class ExportPlanner
{
    /// <summary>Nearest integer pixel; half pixels round away from zero. 210×297 mm at 300 DPI = 2480×3508.</summary>
    public static (int Width, int Height) ToPixels(double width, double height, ExportSizeUnit unit, double dpi)
    {
        if (!Enum.IsDefined(unit) || !double.IsFinite(dpi) || dpi <= 0 || dpi > 9600)
            throw new ExportValidationException("DPI должен быть больше 0 и не больше 9600.");
        var factor = PixelsPerUnit(unit, dpi);
        int Convert(double value)
        {
            var pixels = Math.Round(value * factor, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(value) || value <= 0 || !double.IsFinite(pixels) || pixels < 1 || pixels > int.MaxValue)
                throw new ExportValidationException("Размер должен давать от 1 до 2147483647 пикселей; уменьшите размер или DPI.");
            return (int)pixels;
        }
        return (Convert(width), Convert(height));
    }

    public static double PixelsPerUnit(ExportSizeUnit unit, double dpi) => unit switch
    {
        ExportSizeUnit.Pixels => 1,
        ExportSizeUnit.Millimetres => dpi / 25.4,
        ExportSizeUnit.Inches => dpi,
        _ => throw new ExportValidationException("Неизвестная единица размера."),
    };
}
