using System.Text;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

/// <summary>
/// Exports of the hex workshop beyond the picture: the players' view of PNG and Universal VTT,
/// a Foundry hint next to the dd2vtt, the readable JSON of the map (and back), the hex key as
/// Markdown, and the map printed page by page as a PDF.
/// </summary>
public sealed partial class EditorViewModel
{
    private bool _exportHexPlayers;
    private HexPrintOptions _hexPrint = new();

    /// <summary>PNG, VTT and print show the map as the players see it: opaque fog, player keys only.</summary>
    public bool ExportHexPlayers
    {
        get => _exportHexPlayers;
        set
        {
            if (_exportHexPlayers == value) return;
            _exportHexPlayers = value;
            Raise(nameof(ExportHexPlayers));
            RebuildExportPlanAndPreview();
        }
    }

    public HexPrintOptions HexPrintOptions
    {
        get => _hexPrint;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_hexPrint == value) return;
            _hexPrint = value;
            Raise(nameof(HexPrintOptions));
        }
    }

    /// <summary>The grid type Foundry VTT needs for this board (its scene setting and its macro value).</summary>
    public static (int Type, string Name) FoundryGridType(HexOrientation orientation) => orientation == HexOrientation.Flat
        ? (4, "Hexagonal Columns — Odd")
        : (2, "Hexagonal Rows — Odd");

    /// <summary>
    /// A text file beside the dd2vtt: how to import it into Foundry (the Universal Battlemap Importer
    /// module) and the one-line macro that sets the scene's hex grid; the grid size comes from the file.
    /// </summary>
    private void WriteFoundryHint(string vttPath, HexLayer layer)
    {
        var (type, name) = FoundryGridType(layer.Board.Orientation);
        var text = new StringBuilder()
            .AppendLine("Карта гексов из Map для виртуального стола.")
            .AppendLine()
            .AppendLine("Foundry VTT: включите модуль Universal Battlemap Importer, вкладка Scenes → Universal Battlemap Import, выберите этот .dd2vtt.")
            .AppendLine($"Затем в настройках сцены поставьте тип сетки «{name}» или выполните макрос:")
            .AppendLine($"canvas.scene.update({{ grid: {{ type: {type} }} }});")
            .AppendLine()
            .AppendLine("Roll20: загрузите картинку из .dd2vtt (или экспорт PNG) на карту страницы, в настройках страницы включите гексовую сетку "
                + (layer.Board.Orientation == HexOrientation.Flat ? "Hex (H)" : "Hex (V)") + ".")
            .AppendLine($"Размер гекса «грань к грани» в картинке указан в файле как pixels_per_grid.")
            .ToString();
        try
        {
            File.WriteAllText(Path.ChangeExtension(vttPath, ".foundry.txt"), text);
            _exportResultText += " · подсказка для Foundry рядом (.foundry.txt)";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.Error("FOUNDRY_HINT_FAILED", error);
        }
    }

    /// <summary>Writes the whole hex map as readable JSON.</summary>
    public async Task<string?> ExportHexJsonAsync()
    {
        if (_exportDestinationPicker is null) return null;
        var snapshot = Session.Document;
        var path = await _exportDestinationPicker.PickFileAsync(SafeExportName(snapshot.Title) + ".hex.json", "json", "JSON гексовой карты").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            await Task.Run(() => HexJsonExporter.Write(snapshot, path)).ConfigureAwait(true);
            StatusDetail = "JSON карты сохранён: " + path;
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось сохранить JSON: " + error.Message;
            return null;
        }
    }

    /// <summary>Lays the hexes, paths, regions and key of a hex JSON file onto the active hex layer (one undo step).</summary>
    public bool ImportHexJson(string json)
    {
        if (ActiveHexLayer is not { } layer) return false;
        try
        {
            var after = HexJsonExporter.Read(json, layer);
            ExecuteHexLayerEdit("Импорт JSON гексов", layer, after, EditCost.BaseBytes + (long)after.Board.CellCount * 48);
            Raise(nameof(HexPaths));
            Raise(nameof(HexRegions));
            Raise(nameof(HexKeys));
            StatusDetail = $"Из JSON взято гексов: {after.Board.CellCount}, путей: {after.Paths.Count}, ключей: {after.Notes.Count}.";
            return true;
        }
        catch (InvalidDataException error)
        {
            StatusDetail = error.Message;
            return false;
        }
    }

    /// <summary>Writes the hex key as Markdown: the master's full key, or only what the players may read.</summary>
    public async Task<string?> ExportHexKeyAsync(bool playersOnly)
    {
        if (_exportDestinationPicker is null) return null;
        var markdown = HexKeyMarkdown(playersOnly);
        var name = SafeExportName(Session.Document.Title) + (playersOnly ? " - ключ для игроков" : " - ключ гексов") + ".md";
        var path = await _exportDestinationPicker.PickFileAsync(name, "md", "Ключ гексов (Markdown)").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            await File.WriteAllTextAsync(path, markdown).ConfigureAwait(true);
            StatusDetail = "Ключ сохранён: " + path;
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось сохранить ключ: " + error.Message;
            return null;
        }
    }

    /// <summary>The hex map printed page by page into a PDF (with the key after the map).</summary>
    public async Task<HexPrintResult?> ExportHexPrintAsync()
    {
        if (_exportDestinationPicker is null || ActiveHexLayer is not { } layer) return null;
        var snapshot = Session.Document;
        var path = await _exportDestinationPicker.PickFileAsync(SafeExportName(snapshot.Title) + " - печать.pdf", "pdf", "PDF для печати").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return null;
        var options = _hexPrint with { Level = HexLevelShown, Players = _exportHexPlayers };
        var key = HexKeys
            .Where(row => !options.Players || HexLayerRenderer.PlayersSee(layer, row.Hex, row.Note))
            .Select(row => (row.Number, row.Note)).ToList();
        try
        {
            StatusDetail = "Готовлю листы для печати…";
            var result = await Task.Run(() => HexPrintExporter.Write(snapshot, path, options, key)).ConfigureAwait(true);
            StatusDetail = $"PDF для печати: {result.MapPages} листов карты ({result.Rows} × {result.Columns})"
                + (result.KeyPages > 0 ? $" и {result.KeyPages} листов ключа" : "") + $" · {path}";
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusDetail = "Не удалось сделать PDF: " + error.Message;
            return null;
        }
    }
}
