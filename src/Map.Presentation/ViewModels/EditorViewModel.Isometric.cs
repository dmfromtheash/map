using Map.Export;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R38: the map seen in isometry - turned to one of four sides, heights scaled - as a preview and a
/// PNG. Buildings are procedural blocks until the owner's isometric set exists.
/// </summary>
public sealed partial class EditorViewModel
{
    public IsometricSettings IsometricSettings { get; set; } = new();

    public byte[] RenderIsometricPreview(int width) =>
        IsometricRenderer.RenderPng(Session.Document, IsometricSettings with { Width = width });

    public async Task<bool> ExportIsometricAsync(string path, int width)
    {
        var document = Session.Document;
        var settings = IsometricSettings with { Width = Math.Clamp(width, 512, 8192) };
        try
        {
            var png = await Task.Run(() => IsometricRenderer.RenderPng(document, settings));
            await File.WriteAllBytesAsync(path, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось сохранить изометрию: " + ex.Message;
            _log.Error("ISOMETRIC_EXPORT_FAILED", ex);
            return false;
        }
        StatusDetail = $"Изометрия сохранена: {Path.GetFileName(path)}.";
        return true;
    }

    public async Task<bool> PickAndExportIsometricAsync(int width)
    {
        if (await _filePicker.PickSaveAsync($"{SafeExportName(Session.Document.Title)} - изометрия.png") is not { } path) return false;
        return await ExportIsometricAsync(path, width);
    }
}
