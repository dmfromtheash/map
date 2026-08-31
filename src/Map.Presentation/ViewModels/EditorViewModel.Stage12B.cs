using System.Globalization;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public enum ResizeRegionMode
{
    Resize,
    Region,
}

public sealed partial class EditorViewModel
{
    private bool _canvasWorkflowOpen;
    private ResizeRegionMode _canvasWorkflowMode;
    private CanvasAnchor _canvasAnchor = CanvasAnchor.Center;
    private bool _canvasBoundarySnap;
    private string _canvasWidthText = "";
    private string _canvasHeightText = "";
    private string _regionXText = "";
    private string _regionYText = "";
    private string _regionWidthText = "";
    private string _regionHeightText = "";
    private string _regionTitleText = "";
    private string _canvasWorkflowError = "";
    private string _canvasWorkflowSummary = "";
    private string _canvasObjectSummary = "";
    private CanvasResizePlan? _canvasResizePlan;
    private MapDocument? _canvasResizePreviewDocument;
    private CanvasRegion? _canvasRegionPreview;
    private (double X, double Y)? _regionDragStart;
    private bool _syncingRegionFields;
    private bool _creatingRegionMap;

    public bool CanvasWorkflowOpen => _canvasWorkflowOpen;
    public ResizeRegionMode CanvasWorkflowMode => _canvasWorkflowMode;
    public CanvasAnchor CanvasAnchor => _canvasAnchor;
    public bool CanvasBoundarySnap => _canvasBoundarySnap;
    public bool IsCreatingRegionMap => _creatingRegionMap;
    public MapDocument? CanvasResizePreviewDocument => _canvasResizePreviewDocument;
    public CanvasRegion? CanvasRegionPreview => _canvasRegionPreview;

    public string CanvasWidthText
    {
        get => _canvasWidthText;
        set
        {
            if (SetField(ref _canvasWidthText, value))
            {
                RebuildCanvasWorkflow();
            }
        }
    }

    public string CanvasHeightText
    {
        get => _canvasHeightText;
        set
        {
            if (SetField(ref _canvasHeightText, value))
            {
                RebuildCanvasWorkflow();
            }
        }
    }

    public string RegionXText
    {
        get => _regionXText;
        set => SetRegionField(ref _regionXText, value, nameof(RegionXText));
    }

    public string RegionYText
    {
        get => _regionYText;
        set => SetRegionField(ref _regionYText, value, nameof(RegionYText));
    }

    public string RegionWidthText
    {
        get => _regionWidthText;
        set => SetRegionField(ref _regionWidthText, value, nameof(RegionWidthText));
    }

    public string RegionHeightText
    {
        get => _regionHeightText;
        set => SetRegionField(ref _regionHeightText, value, nameof(RegionHeightText));
    }

    public string RegionTitleText
    {
        get => _regionTitleText;
        set
        {
            if (SetField(ref _regionTitleText, value))
            {
                RebuildCanvasWorkflow();
            }
        }
    }

    public string CanvasWorkflowError
    {
        get => _canvasWorkflowError;
        private set => SetField(ref _canvasWorkflowError, value);
    }

    public string CanvasWorkflowSummary
    {
        get => _canvasWorkflowSummary;
        private set => SetField(ref _canvasWorkflowSummary, value);
    }

    public string CanvasObjectSummary
    {
        get => _canvasObjectSummary;
        private set => SetField(ref _canvasObjectSummary, value);
    }

    public CanvasBoundaryOverlay? CanvasBoundaryOverlay
    {
        get
        {
            if (!CanvasWorkflowOpen)
            {
                return null;
            }

            if (CanvasWorkflowMode == ResizeRegionMode.Resize && _canvasResizePlan is { } resize)
            {
                return new CanvasBoundaryOverlay(
                    resize.OldBoundsInResult,
                    new WorldBounds(0, 0, resize.Width, resize.Height),
                    null);
            }

            return _canvasRegionPreview is { } region
                ? new CanvasBoundaryOverlay(null, null, region.Bounds)
                : null;
        }
    }

    public void BeginCanvasWorkflow()
    {
        var canvas = Session.Document.Canvas;
        _canvasWorkflowOpen = true;
        _canvasWorkflowMode = ResizeRegionMode.Resize;
        _canvasAnchor = CanvasAnchor.Center;
        _canvasBoundarySnap = false;
        _canvasWidthText = canvas.Width.ToString(CultureInfo.InvariantCulture);
        _canvasHeightText = canvas.Height.ToString(CultureInfo.InvariantCulture);
        _regionXText = "0";
        _regionYText = "0";
        _regionWidthText = canvas.Width.ToString(CultureInfo.InvariantCulture);
        _regionHeightText = canvas.Height.ToString(CultureInfo.InvariantCulture);
        _regionTitleText = Session.Document.Title + " — регион";
        _regionDragStart = null;
        RaiseCanvasWorkflowState();
        RebuildCanvasWorkflow();
    }

    public void SetCanvasWorkflowMode(ResizeRegionMode mode)
    {
        if (_canvasWorkflowMode == mode)
        {
            return;
        }

        _canvasWorkflowMode = mode;
        _regionDragStart = null;
        Raise(nameof(CanvasWorkflowMode));
        RebuildCanvasWorkflow();
    }

    public void SetCanvasAnchor(CanvasAnchor anchor)
    {
        if (_canvasAnchor == anchor)
        {
            return;
        }

        _canvasAnchor = anchor;
        Raise(nameof(CanvasAnchor));
        RebuildCanvasWorkflow();
    }

    public void SetCanvasBoundarySnap(bool enabled)
    {
        if (_canvasBoundarySnap == enabled)
        {
            return;
        }

        _canvasBoundarySnap = enabled;
        Raise(nameof(CanvasBoundarySnap));
        RebuildCanvasWorkflow();
    }

    public void CancelCanvasWorkflow()
    {
        if (!_canvasWorkflowOpen)
        {
            return;
        }

        CloseCanvasWorkflow();
        StatusDetail = "Изменение размера или региона отменено. Карта и файл не изменены.";
    }

    public bool ApplyCanvasResize()
    {
        if (!_canvasWorkflowOpen
            || _canvasWorkflowMode != ResizeRegionMode.Resize
            || _canvasResizePlan is null
            || _canvasResizePreviewDocument is null)
        {
            CanvasWorkflowError = string.IsNullOrWhiteSpace(CanvasWorkflowError)
                ? "Сначала задайте допустимый новый размер."
                : CanvasWorkflowError;
            return false;
        }

        var before = Session.Document;
        var plan = _canvasResizePlan;
        if (plan.OffsetX == 0 && plan.OffsetY == 0
            && plan.Width == before.Canvas.Width && plan.Height == before.Canvas.Height)
        {
            CanvasWorkflowError = "Новый размер совпадает с текущим; применять нечего.";
            return false;
        }

        var after = _canvasResizePreviewDocument;
        CloseCanvasWorkflow(notifyScene: false);
        Session.Execute(new ResizeCanvasCommand(before, after));
        AfterEdit();
        StatusDetail = $"Размер карты изменён до {plan.Width} × {plan.Height} px одной командой. "
            + "Данные за границей сохранены; действие можно отменить через Undo.";
        return true;
    }

    public async Task<bool> CreateMapFromRegionAsync()
    {
        if (_creatingRegionMap)
        {
            return false;
        }

        if (!_canvasWorkflowOpen
            || _canvasWorkflowMode != ResizeRegionMode.Region
            || _canvasRegionPreview is not { } region)
        {
            CanvasWorkflowError = string.IsNullOrWhiteSpace(CanvasWorkflowError)
                ? "Сначала задайте допустимый регион."
                : CanvasWorkflowError;
            return false;
        }

        var title = (_regionTitleText ?? "").Trim();
        if (title.Length == 0)
        {
            CanvasWorkflowError = "Введите непустое название новой карты.";
            return false;
        }

        var source = Session.Document;
        MapDocument extracted;
        try
        {
            extracted = CanvasTransformService.ExtractRegion(
                source,
                region,
                title,
                DocumentId.New(),
                _clock());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            CanvasWorkflowError = "Не удалось подготовить регион: " + ex.Message;
            _log.Error("Region extraction failed", ex);
            return false;
        }

        var suggested = SafeProjectFileName(title) + MapProjectFormat.Extension;
        var path = await _filePicker.PickSaveAsync(suggested).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusDetail = "Создание карты из региона отменено. Исходная карта и файл не изменены.";
            return false;
        }

        _creatingRegionMap = true;
        Raise(nameof(IsCreatingRegionMap));
        try
        {
            var modifiedUtc = _clock();
            var metadata = new SaveMetadata(_appVersion, modifiedUtc, savedRevision: 0);
            await Task.Run(() =>
            {
                var preview = ThumbnailRenderer.RenderPng(extracted);
                _store.Save(extracted, path, metadata, preview);
            }).ConfigureAwait(false);

            _recent.Touch(path, extracted.Id.Value, extracted.Title, modifiedUtc);
            CloseCanvasWorkflow();
            StatusDetail = $"Создана независимая карта из региона: {path}. Исходная карта осталась открыта и не изменена.";
            _log.Info($"Created region map {extracted.Id} at {path}.");
            return true;
        }
        catch (Exception ex) when (ex is MapProjectSaveException or IOException
            or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            CanvasWorkflowError = "Не удалось создать файл региона. Исходная карта и прежний файл назначения не изменены.";
            StatusDetail = CanvasWorkflowError;
            _log.Error($"Region map save to {path} failed", ex);
            return false;
        }
        finally
        {
            _creatingRegionMap = false;
            Raise(nameof(IsCreatingRegionMap));
        }
    }

    public bool BeginRegionSelection(double worldX, double worldY)
    {
        if (!_canvasWorkflowOpen || _canvasWorkflowMode != ResizeRegionMode.Region)
        {
            return false;
        }

        _regionDragStart = (worldX, worldY);
        return true;
    }

    public void UpdateRegionSelection(double worldX, double worldY)
    {
        if (_regionDragStart is not { } start)
        {
            return;
        }

        try
        {
            var region = CanvasRegion.FromCorners(start.X, start.Y, worldX, worldY);
            SetRegionText(region);
            RebuildCanvasWorkflow();
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            CanvasWorkflowError = "Растяните прямоугольник хотя бы на один пиксель.";
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void EndRegionSelection()
    {
        _regionDragStart = null;
        if (_canvasRegionPreview is { } effective)
        {
            SetRegionText(effective);
            RebuildCanvasWorkflow();
        }
    }

    private void RebuildCanvasWorkflow()
    {
        if (!_canvasWorkflowOpen)
        {
            return;
        }

        CanvasWorkflowError = "";
        _canvasResizePlan = null;
        _canvasResizePreviewDocument = null;
        _canvasRegionPreview = null;

        try
        {
            var snapGrid = _canvasBoundarySnap
                ? ActiveSnapGrid() ?? throw new InvalidOperationException(
                    "Нет видимой активной сетки с включённой привязкой.")
                : null;

            if (_canvasWorkflowMode == ResizeRegionMode.Resize)
            {
                if (!TryDimension(_canvasWidthText, out var width)
                    || !TryDimension(_canvasHeightText, out var height))
                {
                    throw new ArgumentException(
                        $"Ширина и высота: целые числа {CanvasDimensionPolicy.Minimum}..{CanvasDimensionPolicy.Maximum} px.");
                }

                var plan = CanvasTransformService.PlanResize(
                    Session.Document, width, height, _canvasAnchor, snapGrid);
                _canvasResizePlan = plan;
                _canvasResizePreviewDocument = CanvasTransformService.ApplyResize(Session.Document, plan);
                CanvasWorkflowSummary =
                    $"Текущая карта: {Session.Document.Canvas.Width} × {Session.Document.Canvas.Height} px. "
                    + $"Итог: {plan.Width} × {plan.Height} px; смещение {plan.OffsetX:+#;-#;0}, {plan.OffsetY:+#;-#;0} px. "
                    + "Красное будет скрыто границей, зелёное станет новой областью. Скрытые данные не удаляются.";
                CanvasObjectSummary =
                    $"Объекты — внутри: {plan.ObjectCounts.Inside}; пересекают границу: "
                    + $"{plan.ObjectCounts.Intersecting}; снаружи: {plan.ObjectCounts.Outside}.";
            }
            else
            {
                if (!TryInteger(_regionXText, out var x)
                    || !TryInteger(_regionYText, out var y)
                    || !TryDimension(_regionWidthText, out var width)
                    || !TryDimension(_regionHeightText, out var height))
                {
                    throw new ArgumentException(
                        $"X/Y должны быть целыми; ширина и высота — {CanvasDimensionPolicy.Minimum}..{CanvasDimensionPolicy.Maximum} px.");
                }

                var region = new CanvasRegion(x, y, width, height);
                if (snapGrid is not null)
                {
                    region = CanvasTransformService.SnapRegion(region, snapGrid);
                    if (!CanvasDimensionPolicy.IsValid(region.Width)
                        || !CanvasDimensionPolicy.IsValid(region.Height))
                    {
                        throw new InvalidOperationException(
                            "После привязки регион меньше минимального размера или больше допустимого.");
                    }
                }

                if (string.IsNullOrWhiteSpace(_regionTitleText))
                {
                    throw new ArgumentException("Введите название новой карты.");
                }

                _canvasRegionPreview = region;
                var counts = RegionCounts(Session.Document, region);
                CanvasWorkflowSummary =
                    $"Регион: X {region.X}, Y {region.Y}, {region.Width} × {region.Height} px. "
                    + "Пересекающиеся объекты копируются целиком и обрезаются только видимой границей новой карты.";
                CanvasObjectSummary =
                    $"Объекты — внутри: {counts.Inside}; пересекают границу: {counts.Intersecting}; "
                    + $"снаружи (не войдут в копию): {counts.Outside}.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            CanvasWorkflowError = ex.Message;
            CanvasWorkflowSummary = "Preview не применён: документ, история, dirty state и файл не изменены.";
            CanvasObjectSummary = "";
        }

        Raise(nameof(CanvasResizePreviewDocument));
        Raise(nameof(CanvasRegionPreview));
        Raise(nameof(CanvasBoundaryOverlay));
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCanvasWorkflowAfterDocumentChange()
    {
        if (_canvasWorkflowOpen)
        {
            RebuildCanvasWorkflow();
        }
    }

    private void CloseCanvasWorkflow(bool notifyScene = true)
    {
        _canvasWorkflowOpen = false;
        _regionDragStart = null;
        _canvasResizePlan = null;
        _canvasResizePreviewDocument = null;
        _canvasRegionPreview = null;
        RaiseCanvasWorkflowState();
        if (notifyScene)
        {
            SceneChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseCanvasWorkflowState()
    {
        Raise(nameof(CanvasWorkflowOpen));
        Raise(nameof(CanvasWorkflowMode));
        Raise(nameof(CanvasAnchor));
        Raise(nameof(CanvasBoundarySnap));
        Raise(nameof(CanvasWidthText));
        Raise(nameof(CanvasHeightText));
        Raise(nameof(RegionXText));
        Raise(nameof(RegionYText));
        Raise(nameof(RegionWidthText));
        Raise(nameof(RegionHeightText));
        Raise(nameof(RegionTitleText));
        Raise(nameof(CanvasResizePreviewDocument));
        Raise(nameof(CanvasRegionPreview));
        Raise(nameof(CanvasBoundaryOverlay));
        Raise(nameof(CanvasWorkflowError));
        Raise(nameof(CanvasWorkflowSummary));
        Raise(nameof(CanvasObjectSummary));
    }

    private void SetRegionField(ref string field, string value, string propertyName)
    {
        if (SetField(ref field, value, propertyName) && !_syncingRegionFields)
        {
            RebuildCanvasWorkflow();
        }
    }

    private void SetRegionText(CanvasRegion region)
    {
        _syncingRegionFields = true;
        try
        {
            RegionXText = region.X.ToString(CultureInfo.InvariantCulture);
            RegionYText = region.Y.ToString(CultureInfo.InvariantCulture);
            RegionWidthText = region.Width.ToString(CultureInfo.InvariantCulture);
            RegionHeightText = region.Height.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncingRegionFields = false;
        }
    }

    private static CanvasContentCounts RegionCounts(MapDocument document, CanvasRegion region)
    {
        var inside = 0;
        var intersecting = 0;
        var outside = 0;
        foreach (var item in document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects))
        {
            var bounds = SceneGeometry.BoundsOf(item);
            if (bounds.Left >= region.X && bounds.Top >= region.Y
                && bounds.Right <= region.Right && bounds.Bottom <= region.Bottom)
            {
                inside++;
            }
            else if (bounds.Left < region.Right && bounds.Right > region.X
                && bounds.Top < region.Bottom && bounds.Bottom > region.Y)
            {
                intersecting++;
            }
            else
            {
                outside++;
            }
        }

        return new CanvasContentCounts(inside, intersecting, outside);
    }

    private static bool TryDimension(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && CanvasDimensionPolicy.IsValid(value);

    private static bool TryInteger(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static string SafeProjectFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(title.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return safe.Length == 0 ? "region" : safe;
    }
}
