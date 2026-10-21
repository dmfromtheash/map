using System.Globalization;
using Map.Export;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private ExportSizeUnit _exportSizeUnit;
    private string _exportDpiText = "300";
    public ExportSizeUnit ExportSizeUnit
    {
        get => _exportSizeUnit;
        set
        {
            if (_exportRunning || !Enum.IsDefined(value) || value == _exportSizeUnit) return;
            var factor = ExportPlanner.PixelsPerUnit(_exportSizeUnit, ExportDpi) / ExportPlanner.PixelsPerUnit(value, ExportDpi);
            _exportSizeUnit = value;
            if (TryExportNumber(_exportOutputWidthText, out var w)) SetExportDisplayDimension(ref _exportOutputWidthText, w * factor, nameof(ExportOutputWidthText));
            if (TryExportNumber(_exportOutputHeightText, out var h)) SetExportDisplayDimension(ref _exportOutputHeightText, h * factor, nameof(ExportOutputHeightText));
            Raise(nameof(ExportSizeUnit));
            RebuildExportPlanAndPreview();
        }
    }
    public string ExportDpiText
    {
        get => _exportDpiText;
        set { if (SetField(ref _exportDpiText, value)) RebuildExportPlanAndPreview(); }
    }
    private double ExportDpi => TryExportNumber(_exportDpiText, out var dpi) && dpi > 0 ? dpi : 300;
    private static bool TryExportNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private string FormatExportDimension(double value) => (_exportSizeUnit == ExportSizeUnit.Pixels
        ? Math.Max(1, Math.Round(value, MidpointRounding.AwayFromZero)) : value).ToString("0.######", CultureInfo.InvariantCulture);
    private (int Width, int Height) ExportPixelSize()
    {
        if (!TryExportNumber(_exportOutputWidthText, out var w) || !TryExportNumber(_exportOutputHeightText, out var h))
            throw new ExportValidationException("Введите ширину и высоту числами в выбранных единицах.");
        if (!TryExportNumber(_exportDpiText, out var dpi)) throw new ExportValidationException("Введите DPI числом.");
        return ExportPlanner.ToPixels(w, h, _exportSizeUnit, dpi);
    }
}
