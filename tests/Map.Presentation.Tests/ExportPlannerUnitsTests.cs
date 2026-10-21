using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class ExportPlannerUnitsTests
{
    [Theory]
    [InlineData(210, 297, ExportSizeUnit.Millimetres, 300, 2480, 3508)]
    [InlineData(8.5, 11, ExportSizeUnit.Inches, 300, 2550, 3300)]
    [InlineData(25.4, 50.8, ExportSizeUnit.Millimetres, 96, 96, 192)]
    [InlineData(100.5, 200.5, ExportSizeUnit.Pixels, 300, 101, 201)]
    public void Physical_size_is_rounded_to_nearest_pixel(double w, double h, ExportSizeUnit unit, double dpi, int x, int y) =>
        Assert.Equal((x, y), ExportPlanner.ToPixels(w, h, unit, dpi));

    [Theory]
    [InlineData(0, 300)] [InlineData(-1, 300)] [InlineData(10, 0)] [InlineData(10, 9601)] [InlineData(double.NaN, 300)] [InlineData(double.PositiveInfinity, 300)]
    public void Invalid_sizes_are_rejected(double width, double dpi) =>
        Assert.Throws<ExportValidationException>(() => ExportPlanner.ToPixels(width, 20, ExportSizeUnit.Millimetres, dpi));

    [Fact]
    public async Task Unit_switch_keeps_pixels_and_aspect_lock_and_dpi_changes_the_physical_export()
    {
        var doc = HexMapFactory.Create("Units", DateTimeOffset.UnixEpoch, MapCategoryId.Standard, HexOrientation.Pointy, 16, 4, 4, "#FFFFFFFF");
        var editor = new EditorViewModel(doc, null, false, new FakeProjectStore(), new FakeRecentMaps(), new FakeFilePicker(), new FakePrompts(), new FakeLog());
        editor.BeginExport(); await editor.WaitForExportPreviewAsync();
        var original = editor.CurrentExportPlan!;
        editor.ExportSizeUnit = ExportSizeUnit.Millimetres;
        await editor.WaitForExportPreviewAsync();
        Assert.Equal((original.OutputWidth, original.OutputHeight), (editor.CurrentExportPlan!.OutputWidth, editor.CurrentExportPlan.OutputHeight));
        Assert.True(editor.ExportAspectLocked);
        editor.SetExportAspectLocked(false);
        editor.ExportOutputWidthText = "210"; editor.ExportOutputHeightText = "297";
        await editor.WaitForExportPreviewAsync();
        Assert.Equal((2480, 3508), (editor.CurrentExportPlan!.OutputWidth, editor.CurrentExportPlan.OutputHeight));
        editor.ExportSizeUnit = ExportSizeUnit.Inches;
        await editor.WaitForExportPreviewAsync();
        Assert.False(editor.ExportAspectLocked);
        Assert.Equal((2480, 3508), (editor.CurrentExportPlan!.OutputWidth, editor.CurrentExportPlan.OutputHeight));
        editor.ExportDpiText = "150"; await editor.WaitForExportPreviewAsync();
        Assert.Equal((1240, 1754), (editor.CurrentExportPlan!.OutputWidth, editor.CurrentExportPlan.OutputHeight));
        editor.ExportSizeUnit = ExportSizeUnit.Millimetres; editor.SetExportAspectLocked(true); editor.ExportOutputWidthText = "25.4";
        await editor.WaitForExportPreviewAsync();
        Assert.InRange(Math.Abs(editor.CurrentExportPlan!.OutputHeight / (double)editor.CurrentExportPlan.OutputWidth - doc.Canvas.Height / (double)doc.Canvas.Width), 0, 0.01);
        editor.CloseExport();
    }
}
