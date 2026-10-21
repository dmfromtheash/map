using Map.Export;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    /// <summary>A new folder of linked hex notes, with the same recipient as the other packages.</summary>
    public async Task<HexObsidianResult?> ExportObsidianAsync()
    {
        if (_exportDestinationPicker is null || !IsHexWorkshop) return null;
        var scene = HexDataExportScene();
        var name = SafeExportName(scene.Document.Title) + (scene.Players ? " - Obsidian игрокам" : " - Obsidian ведущему");
        var path = await _exportDestinationPicker.PickObsidianPackageAsync(name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            StatusDetail = "Готовлю папку Obsidian…";
            var result = await Task.Run(() => HexObsidianExporter.Write(scene, path)).ConfigureAwait(true);
            StatusDetail = $"Obsidian: {result.Hexes} гексов, {result.Regions} регионов · {result.Path}";
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusDetail = "Не удалось создать папку Obsidian: " + error.Message;
            return null;
        }
    }
}
