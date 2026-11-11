using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

/// <summary>The handout of the map (R09f): chosen in the new-map dialog or on «Экспорт», saved as one picture.</summary>
public sealed partial class EditorViewModel
{
    /// <summary>The map's handout; changing it is one Undo step.</summary>
    public MapHandout Handout
    {
        get => Session.Document.Handout;
        set
        {
            var before = Session.Document.Handout;
            if (before == value) return;
            Session.Execute(new SetMapHandoutCommand(before, value));
            AfterEdit();
            StatusDetail = value == MapHandout.None ? "Хендаут убран." : $"Хендаут: «{MapHandouts.Name(value)}». Ctrl+Z вернёт прежний.";
        }
    }

    /// <summary>Saves the whole map laid into its handout sheet as one PNG or JPEG.</summary>
    public async Task<HandoutResult?> ExportHandoutAsync()
    {
        if (_exportDestinationPicker is null || _exportRunning) return null;
        var snapshot = Session.Document;
        if (snapshot.Handout == MapHandout.None)
        {
            StatusDetail = "Сначала выберите хендаут.";
            return null;
        }
        var sourcePath = DocumentPath;
        try
        {
            var path = await _exportDestinationPicker.PickFileAsync(SafeExportName(snapshot.Title) + " - хендаут.png", "png", "Хендаут PNG").ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path)) return null;
            CancelExportPreview();
            _exportRunCancellation?.Dispose();
            _exportRunCancellation = null;
            _exportRunning = true;
            RaiseRunState();
            StatusDetail = $"Готовлю хендаут «{MapHandouts.Name(snapshot.Handout)}»…";
            using var stamps = new ExportStampImageSource(snapshot, sourcePath, _assetCatalog);
            var result = await RunLocalResourceOperationAsync(() => HandoutExporter.WriteAsync(_imageExporter,
                snapshot, snapshot.Handout, path, stamps)).ConfigureAwait(true);
            StatusDetail = $"Хендаут: {result.Width} × {result.Height} px · {result.Path}";
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ExportValidationException or ExportOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось сохранить хендаут: " + error.Message;
            return null;
        }
        finally
        {
            _exportRunning = false;
            RaiseRunState();
        }
    }
}
