using System.Globalization;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private readonly IImageExporter _imageExporter;
    private readonly IExportDestinationPicker? _exportDestinationPicker;
    private readonly IFolderLauncher? _folderLauncher;
    private CancellationTokenSource? _exportPreviewCancellation;
    private CancellationTokenSource? _exportRunCancellation;
    private Task _exportPreviewTask = Task.CompletedTask;
    private long _exportPreviewGeneration;
    private ExportPlan? _exportPlan;
    private bool _exportOpen;
    private ExportImageFormat _exportFormat = ExportImageFormat.Png;
    private ExportAreaMode _exportAreaMode = ExportAreaMode.WholeCanvas;
    private ExportOutputMode _exportOutputMode = ExportOutputMode.SingleImage;
    private string _exportRegionXText = "0";
    private string _exportRegionYText = "0";
    private string _exportRegionWidthText = "1";
    private string _exportRegionHeightText = "1";
    private string _exportOutputWidthText = "1";
    private string _exportOutputHeightText = "1";
    private bool _exportAspectLocked = true;
    private bool _exportTransparentBackground;
    private bool _exportIncludeGrid = true;
    private int _exportQuality = 90;
    private string _exportValidationError = "";
    private string _exportEstimateText = "";
    private byte[]? _exportPreviewPng;
    private bool _exportPreviewBusy;
    private bool _exportRunning;
    private double _exportProgress;
    private string _exportProgressText = "";
    private string _exportResultText = "";
    private string? _exportResultPath;
    private ExportOutputMode _exportResultMode;
    private bool _syncingExportFields;
    private ExportImageFormat _defaultExportFormat = ExportImageFormat.Png;
    private int _defaultExportScalePercent = 100;
    private bool _defaultExportIncludeGrid;

    public bool ExportOpen => _exportOpen;
    public ExportImageFormat ExportFormat => _exportFormat;
    public ExportAreaMode ExportAreaMode => _exportAreaMode;
    public ExportOutputMode ExportOutputMode => _exportOutputMode;
    public bool ExportAspectLocked => _exportAspectLocked;
    public bool ExportTransparentBackground => _exportTransparentBackground;
    public bool ExportIncludeGrid => _exportIncludeGrid;
    public int ExportQuality => _exportQuality;
    public bool ExportQualityVisible => _exportFormat is ExportImageFormat.Jpeg or ExportImageFormat.Webp;
    public bool ExportTransparencyAvailable => _exportFormat is not ExportImageFormat.Jpeg;
    public string ExportFormatExplanation => _exportFormat == ExportImageFormat.Jpeg
        ? "JPEG не хранит прозрачность: фон карты включён обязательно."
        : _exportFormat == ExportImageFormat.Png
            ? "PNG сохраняет отрисованные детали без потерь, включая полный размер гигантской карты."
            : "WebP может сохранить прозрачный фон.";
    public int ExportScaleFactor => CurrentExportPlan is { } plan
        && plan.OutputWidth % plan.SourceRegion.Width == 0
        && plan.OutputHeight % plan.SourceRegion.Height == 0
        && plan.OutputWidth / plan.SourceRegion.Width == plan.OutputHeight / plan.SourceRegion.Height
            ? plan.OutputWidth / plan.SourceRegion.Width : 0;
    public string ExportDetailSummary
    {
        get
        {
            if (CurrentExportPlan is not { } plan) return "";
            var x = plan.OutputWidth / (double)plan.SourceRegion.Width;
            var y = plan.OutputHeight / (double)plan.SourceRegion.Height;
            var detail = x < 1 || y < 1
                ? $"Выбрано уменьшение до {Math.Min(x, y):P0}: мелкие детали при приближении будут потеряны. «Полный размер» убирает уменьшение."
                : x == 1 && y == 1
                    ? "Полный размер 1:1 — карта сохраняется без уменьшения."
                    : $"Повторная отрисовка в масштабе {x:N2} × {y:N2}. Детали берутся из исходных объектов; чёткость растров ограничена оригиналами ассетов.";
            return detail + ((long)plan.OutputWidth * plan.OutputHeight > 268_435_456
                ? " Для открытия такого большого PNG нужна программа просмотра, поддерживающая его размер. Можно также сохранить отдельный регион."
                : "");
        }
    }
    public string ExportValidationError => _exportValidationError;
    public string ExportEstimateText => _exportEstimateText;
    public string ExportMemoryDetails => CurrentExportPlan is { } plan
        ? $"Оценка памяти экспорта с кэшами изображений: {plan.EstimatedRawWorkingBytes / (1024d * 1024):N0} МиБ. "
            + $"На диске нужно до {plan.RequiredFreeDiskBytes / (1024d * 1024 * 1024):N2} ГиБ с учётом временной записи; сжатый файл обычно меньше. "
            + "Перед запуском проверяется свободная память с резервом. Память открытой карты и истории не входит в эту оценку."
        : "Выберите допустимые параметры экспорта. Причина отказа показана рядом с командой экспорта.";
    public byte[]? ExportPreviewPng => _exportPreviewPng;
    public bool ExportPreviewBusy => _exportPreviewBusy;
    public bool ExportRunning => _exportRunning;
    public double ExportProgress => _exportProgress;
    public string ExportProgressText => _exportProgressText;
    public string ExportResultText => _exportResultText;
    public string? ExportResultPath => _exportResultPath;
    public ExportPlan? CurrentExportPlan => _exportPlan;
    public bool CanStartExport => _exportPlan is not null && !_exportPreviewBusy
        && _exportPreviewPng is not null && !_exportRunning && _exportDestinationPicker is not null;
    public bool CanCancelExport => _exportRunning && _exportRunCancellation is { IsCancellationRequested: false };
    public bool CanOpenExportFolder => !_exportRunning && _exportResultPath is not null && _folderLauncher is not null;

    public string ExportRegionXText
    {
        get => _exportRegionXText;
        set => SetExportText(ref _exportRegionXText, value, nameof(ExportRegionXText), sourceGeometry: true);
    }

    public string ExportRegionYText
    {
        get => _exportRegionYText;
        set => SetExportText(ref _exportRegionYText, value, nameof(ExportRegionYText), sourceGeometry: true);
    }

    public string ExportRegionWidthText
    {
        get => _exportRegionWidthText;
        set => SetExportText(ref _exportRegionWidthText, value, nameof(ExportRegionWidthText), sourceGeometry: true);
    }

    public string ExportRegionHeightText
    {
        get => _exportRegionHeightText;
        set => SetExportText(ref _exportRegionHeightText, value, nameof(ExportRegionHeightText), sourceGeometry: true);
    }

    public string ExportOutputWidthText
    {
        get => _exportOutputWidthText;
        set
        {
            if (!SetField(ref _exportOutputWidthText, value) || _syncingExportFields) return;
            if (_exportAspectLocked) SyncExportHeightFromWidth();
            RebuildExportPlanAndPreview();
        }
    }

    public string ExportOutputHeightText
    {
        get => _exportOutputHeightText;
        set
        {
            if (!SetField(ref _exportOutputHeightText, value) || _syncingExportFields) return;
            if (_exportAspectLocked) SyncExportWidthFromHeight();
            RebuildExportPlanAndPreview();
        }
    }

    public void ConfigureExportDefaults(
        ExportImageFormat format, int scalePercent, bool includeGrid)
    {
        _defaultExportFormat = format;
        _defaultExportScalePercent = Math.Clamp(scalePercent, 100, 800);
        _defaultExportIncludeGrid = includeGrid;
    }

    public void BeginExport()
    {
        ResetExportHexLevel();
        _exportSizeUnit = ExportSizeUnit.Pixels;
        var canvas = Session.Document.Canvas;
        _exportOpen = true;
        _exportFormat = _defaultExportFormat;
        _exportAreaMode = ExportAreaMode.WholeCanvas;
        _exportOutputMode = _exportFormat != ExportImageFormat.Png
            && (canvas.Width * _defaultExportScalePercent / 100d > ExportEngineSettings.DefaultMaxSingleEdge
                || canvas.Height * _defaultExportScalePercent / 100d > ExportEngineSettings.DefaultMaxSingleEdge)
            ? ExportOutputMode.TilePackage
            : ExportOutputMode.SingleImage;
        _exportRegionXText = "0";
        _exportRegionYText = "0";
        _exportRegionWidthText = canvas.Width.ToString(CultureInfo.InvariantCulture);
        _exportRegionHeightText = canvas.Height.ToString(CultureInfo.InvariantCulture);
        _exportOutputWidthText = Math.Max(1, (int)Math.Round(
            canvas.Width * _defaultExportScalePercent / 100d)).ToString(CultureInfo.InvariantCulture);
        _exportOutputHeightText = Math.Max(1, (int)Math.Round(
            canvas.Height * _defaultExportScalePercent / 100d)).ToString(CultureInfo.InvariantCulture);
        _exportAspectLocked = true;
        _exportTransparentBackground = false;
        _exportIncludeGrid = _defaultExportIncludeGrid;
        _exportQuality = 90;
        _exportProgress = 0;
        _exportProgressText = "";
        _exportResultText = "";
        _exportResultPath = null;
        RaiseExportState();
        RebuildExportPlanAndPreview();
    }

    public void SetExportScaleFactor(int factor)
    {
        if (_exportRunning || !TryExportSourceSize(out var width, out var height)) return;
        if (factor is not (1 or 2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(factor));
        _exportAspectLocked = true;
        SetExportDimension(ref _exportOutputWidthText, checked(width * factor), nameof(ExportOutputWidthText));
        SetExportDimension(ref _exportOutputHeightText, checked(height * factor), nameof(ExportOutputHeightText));
        Raise(nameof(ExportAspectLocked));
        RebuildExportPlanAndPreview();
    }

    public void CloseExport()
    {
        if (_exportRunning) return;
        _exportOpen = false;
        CancelExportPreview();
        _exportPlan = null;
        _exportPreviewPng = null;
        RaiseExportState();
    }

    public void SetExportFormat(ExportImageFormat format)
    {
        if (_exportFormat == format) return;
        _exportFormat = format;
        if (format == ExportImageFormat.Jpeg)
        {
            _exportTransparentBackground = false;
        }
        Raise(nameof(ExportFormat));
        Raise(nameof(ExportQualityVisible));
        Raise(nameof(ExportTransparencyAvailable));
        Raise(nameof(ExportTransparentBackground));
        Raise(nameof(ExportFormatExplanation));
        RebuildExportPlanAndPreview();
    }

    public void SetExportAreaMode(ExportAreaMode mode)
    {
        if (_exportAreaMode == mode) return;
        _exportAreaMode = mode;
        Raise(nameof(ExportAreaMode));
        if (_exportAspectLocked) SyncExportHeightFromWidth();
        RebuildExportPlanAndPreview();
    }

    public void SetExportOutputMode(ExportOutputMode mode)
    {
        if (_exportOutputMode == mode) return;
        _exportOutputMode = mode;
        Raise(nameof(ExportOutputMode));
        RebuildExportPlanAndPreview();
    }

    public void SetExportAspectLocked(bool value)
    {
        if (_exportAspectLocked == value) return;
        _exportAspectLocked = value;
        Raise(nameof(ExportAspectLocked));
        if (value) SyncExportHeightFromWidth();
        RebuildExportPlanAndPreview();
    }

    public void SetExportTransparentBackground(bool value)
    {
        var effective = _exportFormat == ExportImageFormat.Jpeg ? false : value;
        if (_exportTransparentBackground == effective) return;
        _exportTransparentBackground = effective;
        Raise(nameof(ExportTransparentBackground));
        RebuildExportPlanAndPreview();
    }

    public void SetExportIncludeGrid(bool value)
    {
        if (_exportIncludeGrid == value) return;
        _exportIncludeGrid = value;
        Raise(nameof(ExportIncludeGrid));
        RebuildExportPlanAndPreview();
    }

    public void SetExportQuality(int value)
    {
        value = Math.Clamp(value, 1, 100);
        if (_exportQuality == value) return;
        _exportQuality = value;
        Raise(nameof(ExportQuality));
        RebuildExportPlanAndPreview();
    }

    public async Task<bool> StartExportAsync()
    {
        if (_exportRunning || _exportDestinationPicker is null)
        {
            return false;
        }

        var snapshot = Session.Document;
        ExportPlan plan;
        try
        {
            plan = _imageExporter.Plan(snapshot, BuildExportRequest());
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException
            or ExportValidationException)
        {
            SetExportValidationError(ex.Message);
            return false;
        }

        var suggestedBase = SafeExportName(snapshot.Title);
        var destination = plan.OutputMode switch
        {
            ExportOutputMode.SingleImage => await _exportDestinationPicker.PickImageAsync(
                $"{suggestedBase}.{ImageExportEngine.Extension(plan.Format)}", plan.Format)
                .ConfigureAwait(true),
            ExportOutputMode.MbTiles => await _exportDestinationPicker.PickFileAsync(
                suggestedBase + MbTilesWriter.Extension, "mbtiles", "MBTiles — пирамида тайлов в одном файле")
                .ConfigureAwait(true),
            _ => await _exportDestinationPicker.PickTilePackageAsync($"{suggestedBase}-tiles")
                .ConfigureAwait(true),
        };
        if (string.IsNullOrWhiteSpace(destination))
        {
            return false;
        }

        CancelExportPreview();
        _exportRunCancellation?.Dispose();
        _exportRunCancellation = new CancellationTokenSource();
        _exportRunning = true;
        _exportProgress = 0;
        _exportProgressText = "Подготовка экспорта…";
        _exportResultText = "";
        _exportResultPath = null;
        SetExportValidationError("");
        RaiseRunState();

        var sourcePath = DocumentPath;
        var progress = new Progress<ExportProgress>(update =>
        {
            if (update.Fraction >= _exportProgress)
            {
                _exportProgress = update.Fraction;
                Raise(nameof(ExportProgress));
            }
            _exportProgressText = update.Message;
            Raise(nameof(ExportProgressText));
        });

        try
        {
            using var stamps = new ExportStampImageSource(snapshot, sourcePath, _assetCatalog);
            var result = await RunLocalResourceOperationAsync(() => _imageExporter.ExportAsync(
                snapshot,
                plan,
                destination,
                stamps,
                progress,
                _exportRunCancellation.Token), _exportRunCancellation.Token).ConfigureAwait(true);
            _exportResultPath = result.DestinationPath;
            _exportResultMode = result.OutputMode;
            _exportResultText = result.OutputMode switch
            {
                ExportOutputMode.SingleImage => $"Готово: {result.DestinationPath}",
                ExportOutputMode.MbTiles => $"Готов MBTiles ({result.FileCount} тайлов): {result.DestinationPath}",
                _ => $"Готов пакет ({result.FileCount - 1} тайлов): {result.DestinationPath}",
            };
            _log.Info($"EXPORT_SUCCEEDED mode={result.OutputMode}");
            return true;
        }
        catch (OperationCanceledException)
        {
            return ExportCancelled();
        }
        catch (Exception ex) when (IsExportFailure(ex))
        {
            return ExportFailed(ex, "Export failed");
        }
        finally
        {
            _exportRunning = false;
            RaiseRunState();
        }
    }

    // Shared by every export run (image, tiles, MBTiles, VTT): the map and the previous result stay intact.
    private static bool IsExportFailure(Exception ex) =>
        ex is InvalidOperationException or ExportOperationException or ExportValidationException
            or IOException or UnauthorizedAccessException or InvalidDataException;

    private bool ExportCancelled()
    {
        _exportResultText = "Экспорт отменён. Карта и прежний результат сохранены.";
        _exportProgressText = "Отменено после безопасной очистки временных данных.";
        return false;
    }

    private bool ExportFailed(Exception ex, string logMessage)
    {
        _exportValidationError = ex.Message;
        _exportResultText = "Экспорт не завершён. Карта и прежний результат сохранены.";
        _exportProgressText = "Ошибка экспорта.";
        _log.Error(logMessage, ex);
        return false;
    }

    public void CancelExport()
    {
        if (!CanCancelExport) return;
        _exportRunCancellation!.Cancel();
        _exportProgressText = "Отмена… ждём безопасной остановки и очистки.";
        Raise(nameof(ExportProgressText));
        Raise(nameof(CanCancelExport));
    }

    public void OpenExportFolder()
    {
        if (!CanOpenExportFolder) return;
        try
        {
            _folderLauncher!.OpenContainingFolder(_exportResultPath!, _exportResultMode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            SetExportValidationError("Не удалось открыть папку результата: " + ex.Message);
        }
    }

    public Task WaitForExportPreviewAsync() => _exportPreviewTask;

    private void RefreshOpenExportAfterSceneChange()
    {
        if (_exportOpen && !_exportRunning)
        {
            RebuildExportPlanAndPreview();
        }
    }

    private void RebuildExportPlanAndPreview()
    {
        if (!_exportOpen || _exportRunning) return;
        CancelExportPreview();
        _exportPlan = null;
        _exportPreviewPng = null;
        _exportEstimateText = "";
        Raise(nameof(CurrentExportPlan));
        Raise(nameof(ExportPreviewPng));
        Raise(nameof(ExportEstimateText));

        try
        {
            var snapshot = Session.Document;
            var plan = _imageExporter.Plan(snapshot, BuildExportRequest());
            _exportPlan = plan;
            _exportValidationError = "";
            _exportEstimateText = DescribePlan(plan);
            _exportPreviewBusy = true;
            var generation = ++_exportPreviewGeneration;
            var cancellation = new CancellationTokenSource();
            _exportPreviewCancellation = cancellation;
            _exportPreviewTask = RenderExportPreviewAsync(
                generation, snapshot, DocumentPath, plan, cancellation.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException
            or ExportValidationException)
        {
            _exportValidationError = ex.Message;
            _exportPreviewBusy = false;
            _exportPreviewTask = Task.CompletedTask;
        }

        Raise(nameof(CurrentExportPlan));
        Raise(nameof(ExportValidationError));
        Raise(nameof(ExportEstimateText));
        Raise(nameof(ExportPreviewBusy));
        Raise(nameof(CanStartExport));
        Raise(nameof(ExportScaleFactor));
        Raise(nameof(ExportDetailSummary));
        Raise(nameof(ExportMemoryDetails));
    }

    private async Task RenderExportPreviewAsync(
        long generation,
        MapDocument snapshot,
        string? sourcePath,
        ExportPlan plan,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stamps = new ExportStampImageSource(snapshot, sourcePath, _assetCatalog);
            var preview = await RunLocalResourceOperationAsync(() => _imageExporter.RenderPreviewAsync(
                snapshot, plan, stamps, cancellationToken), cancellationToken).ConfigureAwait(true);
            if (generation != _exportPreviewGeneration || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _exportPreviewPng = preview.PngBytes;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExportOperationException or IOException
            or InvalidDataException or OutOfMemoryException)
        {
            if (generation == _exportPreviewGeneration)
            {
                _exportValidationError = "Не удалось создать предпросмотр: " + ex.Message;
            }
        }
        finally
        {
            if (generation == _exportPreviewGeneration)
            {
                _exportPreviewBusy = false;
                Raise(nameof(ExportPreviewPng));
                Raise(nameof(ExportValidationError));
                Raise(nameof(ExportPreviewBusy));
                Raise(nameof(CanStartExport));
            }
        }
    }

    private ExportRequest BuildExportRequest()
    {
        var (outputWidth, outputHeight) = ExportPixelSize();

        CanvasRegion? region = null;
        if (_exportAreaMode == ExportAreaMode.Region)
        {
            if (!TryInt(_exportRegionXText, out var x)
                || !TryInt(_exportRegionYText, out var y)
                || !TryInt(_exportRegionWidthText, out var width)
                || !TryInt(_exportRegionHeightText, out var height))
            {
                throw new ExportValidationException("X, Y, ширина и высота региона должны быть целыми числами.");
            }

            try
            {
                region = new CanvasRegion(x, y, width, height);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
            {
                throw new ExportValidationException(
                    "Регион должен иметь положительный размер и не выходить за числовой диапазон.", ex);
            }
        }

        return new ExportRequest(
            _exportFormat,
            _exportAreaMode,
            region,
            outputWidth,
            outputHeight,
            _exportOutputMode,
            _exportTransparentBackground,
            _exportIncludeGrid,
            _exportQuality)
        {
            // A hex map exports the level being edited, as the master or as the players see it.
            Hex = IsHexWorkshop ? new HexExportView(ExportHexPlayers, ExportHexLevel)
                // R27: a painted map with fog exports as the players see it when asked (Заметки → Туман).
                : Fog.Enabled && ExportFogPlayers ? new HexExportView(true, null) : null,
        };
    }

    private void SetExportText(ref string field, string value, string propertyName, bool sourceGeometry)
    {
        if (!SetField(ref field, value, propertyName) || _syncingExportFields) return;
        if (sourceGeometry && _exportAspectLocked) SyncExportHeightFromWidth();
        RebuildExportPlanAndPreview();
    }

    private void SyncExportHeightFromWidth()
    {
        if (!TryExportNumber(_exportOutputWidthText, out var width) || width <= 0
            || !TryExportSourceSize(out var sourceWidth, out var sourceHeight))
        {
            return;
        }

        var height = width * (sourceHeight / (double)sourceWidth);
        SetExportDisplayDimension(ref _exportOutputHeightText, height, nameof(ExportOutputHeightText));
    }

    private void SyncExportWidthFromHeight()
    {
        if (!TryExportNumber(_exportOutputHeightText, out var height) || height <= 0
            || !TryExportSourceSize(out var sourceWidth, out var sourceHeight))
        {
            return;
        }

        var width = height * (sourceWidth / (double)sourceHeight);
        SetExportDisplayDimension(ref _exportOutputWidthText, width, nameof(ExportOutputWidthText));
    }

    private void SetExportDimension(ref string field, int value, string propertyName) =>
        SetExportDisplayDimension(ref field, value / ExportPlanner.PixelsPerUnit(_exportSizeUnit, ExportDpi), propertyName);

    private void SetExportDisplayDimension(ref string field, double value, string propertyName)
    {
        _syncingExportFields = true;
        try
        {
            var text = FormatExportDimension(value);
            if (!string.Equals(field, text, StringComparison.Ordinal))
            {
                field = text;
                Raise(propertyName);
            }
        }
        finally
        {
            _syncingExportFields = false;
        }
    }

    private bool TryExportSourceSize(out int width, out int height)
    {
        if (_exportAreaMode == ExportAreaMode.WholeCanvas)
        {
            width = Session.Document.Canvas.Width;
            height = Session.Document.Canvas.Height;
            return true;
        }

        width = 0;
        height = 0;
        return TryInt(_exportRegionWidthText, out width) && width > 0
            && TryInt(_exportRegionHeightText, out height) && height > 0;
    }

    private void CancelExportPreview()
    {
        _exportPreviewGeneration++;
        _exportPreviewCancellation?.Cancel();
        _exportPreviewCancellation?.Dispose();
        _exportPreviewCancellation = null;
        _exportPreviewBusy = false;
    }

    private void SetExportValidationError(string message)
    {
        _exportValidationError = message;
        Raise(nameof(ExportValidationError));
        Raise(nameof(CanStartExport));
    }

    private void RaiseRunState()
    {
        Raise(nameof(ExportRunning));
        Raise(nameof(ExportProgress));
        Raise(nameof(ExportProgressText));
        Raise(nameof(ExportResultText));
        Raise(nameof(ExportResultPath));
        Raise(nameof(CanStartExport));
        Raise(nameof(CanCancelExport));
        Raise(nameof(CanOpenExportFolder));
    }

    private void RaiseExportState()
    {
        Raise(nameof(ExportOpen));
        Raise(nameof(ExportFormat));
        Raise(nameof(ExportAreaMode));
        Raise(nameof(ExportOutputMode));
        Raise(nameof(ExportRegionXText));
        Raise(nameof(ExportRegionYText));
        Raise(nameof(ExportRegionWidthText));
        Raise(nameof(ExportRegionHeightText));
        Raise(nameof(ExportOutputWidthText));
        Raise(nameof(ExportOutputHeightText));
        Raise(nameof(ExportAspectLocked));
        Raise(nameof(ExportTransparentBackground));
        Raise(nameof(ExportIncludeGrid));
        Raise(nameof(ExportQuality));
        Raise(nameof(ExportQualityVisible));
        Raise(nameof(ExportTransparencyAvailable));
        Raise(nameof(ExportFormatExplanation));
        Raise(nameof(ExportValidationError));
        Raise(nameof(ExportEstimateText));
        Raise(nameof(ExportPreviewPng));
        Raise(nameof(ExportPreviewBusy));
        RaiseRunState();
    }

    private static string DescribePlan(ExportPlan plan)
    {
        var mode = plan.OutputMode == ExportOutputMode.SingleImage
            ? "один файл"
            : $"пакет {plan.Columns} × {plan.Rows} ({plan.TileCount} тайлов)";
        var stretch = Math.Abs(
            plan.OutputWidth / (double)plan.OutputHeight
            - plan.SourceRegion.Width / (double)plan.SourceRegion.Height) > 0.0001
            ? " · пропорции разорваны, изображение будет растянуто"
            : "";
        return $"{plan.OutputWidth:N0} × {plan.OutputHeight:N0} пикс. · "
            + $"{ImageExportEngine.FormatName(plan.Format)} · {mode}{stretch}.";
    }

    private static string SafeExportName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(title.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(value) ? "map" : value;
    }

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
