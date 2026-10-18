using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

/// <summary>Export for virtual tabletops (*.dd2vtt): the export area as one PNG plus walls and doors.</summary>
public sealed partial class EditorViewModel
{
    private double? _vttGridSize;

    /// <summary>
    /// Size of one tabletop square in map units. It starts from the map's first visible grid,
    /// otherwise 100; the user can type another value.
    /// </summary>
    public double ExportVttGridSize
    {
        get => _vttGridSize ?? DefaultVttGridSize();
        set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 4, 4096) : DefaultVttGridSize();
            if (_vttGridSize == clamped) return;
            _vttGridSize = clamped;
            Raise(nameof(ExportVttGridSize));
            Raise(nameof(ExportVttSummary));
        }
    }

    /// <summary>The walls and doors the dd2vtt will carry, in words, before anything is written.</summary>
    public string ExportVttSummary
    {
        get
        {
            var walls = VttGeometry.Collect(Session.Document);
            var plan = CurrentExportPlan;
            var ppg = plan is null ? "" : $" · {UniversalVttExporter.PixelsPerGrid(plan, ExportVttGridSize)} px на клетку";
            return walls.Walls.Count == 0
                ? $"Стен нет: у линий, контуров или дорог включите «Стена блокирует свет».{ppg}"
                : $"Стен: {walls.Walls.Count}, дверей: {walls.Portals.Count}{ppg}";
        }
    }

    public string LastVttExportText { get; private set; } = "";

    private double DefaultVttGridSize() =>
        // A tabletop hex is measured across its flat sides (Foundry and the dd2vtt importer agree).
        IsHexWorkshop && ActiveHexLayer is { } hexes ? hexes.Board.Layout.Size * HexLayout.Sqrt3 :
        Session.Document.Layers.OfType<GridLayer>().Where(layer => layer.Visible)
            .SelectMany(layer => layer.Grids).Select(grid => grid.CellSize).FirstOrDefault(size => size > 0) is var size and > 0
            ? size
            : 100;

    /// <summary>
    /// Writes the current export area (whole map or region, at the chosen output size) as one
    /// Universal VTT file. Same picture as a PNG export; the old file is replaced only on success.
    /// </summary>
    public async Task<bool> StartVttExportAsync()
    {
        if (_exportRunning || _exportDestinationPicker is null) return false;
        var snapshot = Session.Document;
        ExportPlan plan;
        try
        {
            plan = _imageExporter.Plan(snapshot, BuildExportRequest() with
            {
                Format = ExportImageFormat.Png,
                OutputMode = ExportOutputMode.SingleImage,
                TransparentBackground = false,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or ExportValidationException)
        {
            SetExportValidationError(ex.Message);
            return false;
        }

        var destination = await _exportDestinationPicker
            .PickVttAsync($"{SafeExportName(snapshot.Title)}.{UniversalVttExporter.Extension}").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(destination)) return false;

        CancelExportPreview();
        _exportRunCancellation?.Dispose();
        _exportRunCancellation = new CancellationTokenSource();
        _exportRunning = true;
        _exportProgress = 0;
        _exportProgressText = "Подготовка файла для виртуального стола…";
        _exportResultText = "";
        _exportResultPath = null;
        SetExportValidationError("");
        RaiseRunState();
        var progress = new Progress<ExportProgress>(update =>
        {
            if (update.Fraction >= _exportProgress) { _exportProgress = update.Fraction; Raise(nameof(ExportProgress)); }
            _exportProgressText = update.Message;
            Raise(nameof(ExportProgressText));
        });
        try
        {
            using var stamps = new ExportStampImageSource(snapshot, DocumentPath, _assetCatalog);
            var result = await RunLocalResourceOperationAsync(() => UniversalVttExporter.ExportAsync(
                _imageExporter, snapshot, plan, destination, stamps, ExportVttGridSize, progress,
                _exportRunCancellation.Token), _exportRunCancellation.Token).ConfigureAwait(true);
            _exportResultPath = result.Path;
            _exportResultMode = ExportOutputMode.SingleImage;
            _exportResultText = $"Готово для виртуального стола: {result.Path} · стен {result.Walls}, дверей {result.Portals}, {result.PixelsPerGrid} px на клетку";
            if (IsHexWorkshop && ActiveHexLayer is { } hexes) WriteFoundryHint(result.Path, hexes);
            LastVttExportText = _exportResultText;
            Raise(nameof(LastVttExportText));
            _log.Info($"VTT_EXPORT_SUCCEEDED walls={result.Walls} portals={result.Portals}");
            return true;
        }
        catch (OperationCanceledException)
        {
            _exportResultText = "Экспорт отменён. Карта и прежний результат сохранены.";
            _exportProgressText = "Отменено после безопасной очистки временных данных.";
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExportOperationException or ExportValidationException
            or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _exportValidationError = ex.Message;
            _exportResultText = "Экспорт не завершён. Карта и прежний результат сохранены.";
            _exportProgressText = "Ошибка экспорта.";
            _log.Error("VTT export failed", ex);
            return false;
        }
        finally
        {
            _exportRunning = false;
            RaiseRunState();
        }
    }

    /// <summary>
    /// «Стена блокирует свет» of the selected lines and shapes (one Undo step). Returns false when
    /// nothing selected carries a stroke.
    /// </summary>
    public bool SetSelectedBlocksLight(bool blocks)
    {
        var changes = new List<(SceneObject Before, SceneObject After)>();
        foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>())
        {
            if (layer.Locked) continue;
            foreach (var item in layer.Objects)
            {
                if (!Session.SelectedObjectIds.Contains(item.Id) || item.Locked) continue;
                SceneObject? after = item switch
                {
                    { Line: { } line } when line.Stroke.BlocksLight != blocks =>
                        item with { Line = line with { Stroke = line.Stroke with { BlocksLight = blocks } } },
                    { Shape: { } shape } when shape.Stroke.BlocksLight != blocks =>
                        item with { Shape = shape with { Stroke = shape.Stroke with { BlocksLight = blocks } } },
                    _ => null,
                };
                if (after is not null) changes.Add((item, after));
            }
        }
        if (changes.Count == 0) return false;
        Session.Execute(new ReplaceObjectsCommand([.. changes.Select(c => c.Before)], [.. changes.Select(c => c.After)],
            blocks ? "Стена блокирует свет" : "Стена не блокирует свет"));
        AfterEdit();
        return true;
    }

    /// <summary>True when every selected line or shape is a wall; null when nothing selected has a stroke.</summary>
    public bool? SelectedBlocksLight
    {
        get
        {
            // The selected objects are looked up by id. Scanning every object of the document
            // for each read (two toggles read it on every panel refresh) cost 4.4 s of UI thread
            // over a few dozen edits on a map of 100 000 objects.
            var selected = Session.SelectedObjectIds;
            if (selected.Count == 0) return null;
            var index = DocumentRevisionIndex.For(Session.Document);
            var strokes = new List<VectorStrokeStyle>();
            foreach (var id in selected)
                if (index.TryFind(id, out var location) && (location.Object.Line?.Stroke ?? location.Object.Shape?.Stroke) is { } stroke)
                    strokes.Add(stroke);
            return strokes.Count == 0 ? null : strokes.All(stroke => stroke.BlocksLight);
        }
    }
}
