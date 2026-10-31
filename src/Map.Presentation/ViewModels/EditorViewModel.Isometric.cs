using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R38: the map seen in isometry - turned to one of four sides, heights scaled - as a preview and a
/// PNG. Buildings are procedural blocks until the owner's isometric set exists.
/// </summary>
public sealed partial class EditorViewModel
{
    private double? _isometricHeightPreview;

    /// <summary>The view kept in the map (plan 2.3), with the height a slider drag is showing.</summary>
    public IsometricSettings IsometricSettings => new()
    {
        Turn = Session.Document.Isometric.Turn,
        HeightScale = _isometricHeightPreview ?? Session.Document.Isometric.HeightScale,
    };

    /// <summary>The side to look from, one undo step.</summary>
    public void SetIsometricTurn(int turn) =>
        ApplyIsometric(Session.Document.Isometric with { Turn = Math.Clamp(turn, 0, 3) });

    /// <summary>
    /// The heights: while a slider is dragged only the preview follows (<paramref name="commit"/>
    /// false); letting go stores the value in the map as one undo step.
    /// </summary>
    public void SetIsometricHeight(double height, bool commit)
    {
        var value = Math.Round(Math.Clamp(height, MapIsometric.MinimumHeight, MapIsometric.MaximumHeight), 2);
        if (!commit) { _isometricHeightPreview = value; return; }
        _isometricHeightPreview = null;
        ApplyIsometric(Session.Document.Isometric with { HeightScale = value });
    }

    private void ApplyIsometric(MapIsometric after)
    {
        var before = Session.Document.Isometric;
        if (after == before) return;
        Session.Execute(new SetIsometricCommand(before, after));
        AfterEdit();
    }

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
