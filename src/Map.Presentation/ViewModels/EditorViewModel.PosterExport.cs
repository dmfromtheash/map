using Map.Core.Documents;
using Map.Export;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    /// <summary>Both categories use the same poster settings, sheet layout and PDF writer.</summary>
    public async Task<HexPrintResult?> ExportPosterAsync()
    {
        if (_exportDestinationPicker is null || _exportRunning) return null;
        var snapshot = Session.Document;
        var scene = new ExportScene(snapshot, IsHexWorkshop ? ExportHexLevel : null,
            IsHexWorkshop && ExportHexPlayers ? ExportRecipient.Players : ExportRecipient.GameMaster);
        var options = _hexPrint with { Players = scene.Players, Level = scene.HexLevel, IncludeGrid = _exportIncludeGrid };
        var sourcePath = DocumentPath;
        try
        {
            // Validate before opening the system dialog; failed settings never leave a partial PDF.
            HexPrintExporter.Plan(scene, options);
            var path = await _exportDestinationPicker.PickFileAsync(SafeExportName(snapshot.Title) + " - печать.pdf", "pdf", "PDF для печати").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path)) return null;
            CancelExportPreview();
            _exportRunCancellation?.Dispose();
            _exportRunCancellation = null;
            _exportRunning = true;
            RaiseRunState();
            StatusDetail = "Готовлю листы для печати…";
            using var stamps = new ExportStampImageSource(snapshot, sourcePath, _assetCatalog);
            var result = await Task.Run(() => HexPrintExporter.Write(scene, path, options, stamps: stamps)).ConfigureAwait(true);
            StatusDetail = $"PDF: {result.MapPages} листов карты ({result.Rows} × {result.Columns}), обзор: {result.OverviewPages}, ключ: {result.KeyPages} · {path}";
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusDetail = "Не удалось сделать PDF: " + error.Message;
            return null;
        }
        finally
        {
            _exportRunning = false;
            RaiseRunState();
        }
    }

    public async Task<LayerPngResult?> ExportLayersAsync()
    {
        if (_exportDestinationPicker is null || _exportRunning) return null;
        var snapshot = Session.Document;
        var sourcePath = DocumentPath;
        try
        {
            var request = BuildExportRequest() with { Format = ExportImageFormat.Png, OutputMode = ExportOutputMode.SingleImage };
            _imageExporter.Plan(snapshot, request);
            var path = await _exportDestinationPicker.PickLayerPackageAsync(SafeExportName(snapshot.Title) + " - слои").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path)) return null;
            CancelExportPreview();
            _exportRunCancellation?.Dispose();
            _exportRunCancellation = null;
            _exportRunning = true;
            RaiseRunState();
            StatusDetail = "Готовлю PNG видимых слоёв…";
            using var stamps = new ExportStampImageSource(snapshot, sourcePath, _assetCatalog);
            var result = await RunLocalResourceOperationAsync(() => LayerPngExporter.WriteAsync(_imageExporter,
                snapshot, request, path, stamps)).ConfigureAwait(true);
            StatusDetail = $"PNG-слои: {result.Files} файлов, каждый {result.Width} × {result.Height} px · {path}";
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ExportValidationException or ExportOperationException or ArgumentException or OverflowException)
        {
            StatusDetail = "Не удалось сохранить PNG-слои: " + error.Message;
            return null;
        }
        finally
        {
            _exportRunning = false;
            RaiseRunState();
        }
    }
}
