using System.Globalization;
using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Persistence;
using Map.Presentation.Services;
using Map.Rendering;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    // ---------------------------------------------------------------- placement

    private const double PlacementPreviewOpacity = 0.55;
    private const int MaximumScatterPreviewObjects = 64;
    private const double PlacementScaleStep = 0.01;
    private const double PlacementScaleLargeStep = 0.1;
    private const double PlacementRotationStep = 1;
    private const double PlacementRotationLargeStep = 15;

    public PlacementSession? PendingPlacement
    {
        get => _pendingPlacement;
        private set
        {
            var before = _pendingPlacement;
            if (SetField(ref _pendingPlacement, value))
            {
                if ((before is null) != (value is null)) Raise(nameof(HasPendingPlacement));
                Raise(nameof(PlacementPreviewObjects));
                if (before?.InvalidReason != value?.InvalidReason) Raise(nameof(PlacementRefusal));
                if (before?.Target != value?.Target) RaiseStampToolState();
                InvalidateScene(EditorChangeReason.CanvasInvalidation);
            }
        }
    }

    public bool HasPendingPlacement => _pendingPlacement is not null;

    public IReadOnlyList<SceneObject> PlacementPreviewObjects => HasAreaGesture ? _areaPreviewObjects : HasPathGesture ? _pathPreviewObjects
        : _pendingPlacement is { PointerVisible: true } pending
        ? pending.PreviewObjects
        : _vectorPreviewObject is { } vector ? [vector]
        : _textPreviewObject is { } text ? [text]
        : _shapeBooleanPreviewObject is { } boolean ? [boolean]
        : [];

    public LayerId? PlacementPreviewLayerId => _areaLayer ?? _pathLayer ?? _pendingPlacement?.LayerId
        ?? _vectorPreviewLayerId ?? _textPreviewLayerId ?? _pendingShapeBoolean?.LayerId;

    public string PlacementRefusal => _pendingPlacement?.InvalidReason ?? "";

    public void ArmPrimitivePlacement(SceneObjectKind kind)
    {
        if (kind is not (SceneObjectKind.Rectangle or SceneObjectKind.Ellipse))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ActiveTool = EditorTool.Select;
        var centre = ViewCentre();
        var mode = StampPlacementMode == StampPlacementMode.Area
            ? StampPlacementMode.One
            : StampPlacementMode;
        PendingPlacement = BuildPrimitivePlacement(kind, centre.X, centre.Y, mode, pointerVisible: false);
        StatusDetail = kind == SceneObjectKind.Ellipse
            ? "Объект: эллипс · укажите место на холсте."
            : "Объект: прямоугольник · укажите место на холсте.";
    }

    public bool ArmStampPlacement()
    {
        CancelStampSeries();
        ActiveTool = EditorTool.Stamp;
        var centre = ViewCentre();
        PendingPlacement = BuildStampPlacement(centre.X, centre.Y, pointerVisible: false);
        if (PendingPlacement.InvalidReason is { Length: > 0 } refusal)
        {
            StatusDetail = refusal;
            return false;
        }

        StatusDetail = "Ассет выбран · укажите место на холсте.";
        return true;
    }

    private PlacementSession? _seriesOrigin;
    private readonly List<StampPlacementItem> _seriesPlan = [];
    private StampPlacementRequest? _seriesRequest;
    private IReadOnlyList<StampVariantSource> _seriesSources = [];
    private double _seriesLastX, _seriesLastY, _seriesRemaining, _seriesStep;
    public bool IsStampSeriesActive => _seriesOrigin is not null;
    public double SeriesSpacingPercent => _stampToolSettings.SeriesSpacingPercent;
    public void SetSeriesSpacingPercent(double value, bool persist)
    {
        CancelStampSeries();
        _stampToolSettings = _stampToolSettings with { SeriesSpacingPercent = Math.Clamp(value, 10, 400) };
        Raise(nameof(SeriesSpacingPercent));
        Raise(nameof(StampToolSettings));
        if (persist) PersistLocalPreferences();
    }

    public bool BeginStampSeries(double worldX, double worldY)
    {
        CancelStampSeries();
        if (PendingPlacement is not { Target: PlacementTargetKind.Stamp, Mode: StampPlacementMode.Series, CanCommit: true } pending)
            return false;
        var settings = pending.StampSettingsSnapshot ?? _stampToolSettings;
        if (!TryStampInputs(settings, out var request, out var sources, out _, out var refusal))
        { StatusDetail = refusal; return false; }
        _seriesOrigin = pending;
        _seriesRequest = request with { AreaRadius = 0, Density = 0, Seed = pending.Seed };
        _seriesSources = sources;
        _seriesStep = Math.Max(1, request.BaseWidth * settings.SeriesSpacingPercent / 100);
        _seriesRemaining = _seriesStep;
        _seriesLastX = worldX;
        _seriesLastY = worldY;
        AddSeriesPoint(worldX, worldY);
        UpdateSeriesPreview();
        return true;
    }

    public void ContinueStampSeries(double worldX, double worldY)
    {
        if (_seriesOrigin is null) return;
        if (worldX < 0 || worldY < 0 || worldX > Session.Document.Canvas.Width || worldY > Session.Document.Canvas.Height)
        { CancelStampSeries(); return; }
        var dx = worldX - _seriesLastX;
        var dy = worldY - _seriesLastY;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0.000001) return;
        var distance = _seriesRemaining;
        while (distance <= length)
        {
            AddSeriesPoint(_seriesLastX + dx * distance / length, _seriesLastY + dy * distance / length);
            distance += _seriesStep;
        }
        _seriesRemaining = distance - length;
        _seriesLastX = worldX;
        _seriesLastY = worldY;
        UpdateSeriesPreview();
    }

    private void AddSeriesPoint(double x, double y)
    {
        var request = _seriesRequest! with { CenterX = x, CenterY = y,
            Seed = unchecked(_seriesRequest!.Seed + _seriesPlan.Count * 104729) };
        var item = StampPlacement.PlanArea(request, _seriesSources)[0];
        _seriesPlan.Add(ApplyPlacementRotation([item], _seriesOrigin!.RotationDegrees)[0]);
    }

    private void UpdateSeriesPreview()
    {
        if (_seriesOrigin is not { } pending) return;
        PendingPlacement = pending with
        {
            PlannedCount = _seriesPlan.Count,
            PreviewObjects = _seriesPlan.TakeLast(MaximumScatterPreviewObjects)
                .Select(item => StampObject(item, "Серия", pending.StampSettingsSnapshot!, PlacementPreviewOpacity)).ToArray(),
            WorldX = _seriesLastX, WorldY = _seriesLastY,
        };
    }

    public bool EndStampSeries()
    {
        if (_seriesOrigin is not { } origin) return false;
        var plan = _seriesPlan.ToArray();
        var pending = origin with { WorldX = _seriesLastX, WorldY = _seriesLastY };
        _seriesOrigin = null;
        _seriesPlan.Clear();
        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null || plan.Length == 0) { RefreshArmedPlacement(); return false; }
        return CommitStampPlacement(pending, layer, createLayer, plan);
    }

    public bool CancelStampSeries()
    {
        if (_seriesOrigin is not { } origin) return false;
        _seriesOrigin = null;
        _seriesPlan.Clear();
        _seriesSources = [];
        _seriesRequest = null;
        PendingPlacement = origin;
        return true;
    }

    public void UpdatePlacementPreview(double worldX, double worldY)
    {
        if (PendingPlacement is not { } pending)
        {
            return;
        }

        var snapped = SnapPlacementPoint(worldX, worldY);
        PendingPlacement = pending.Target switch
        {
            PlacementTargetKind.Rectangle or PlacementTargetKind.Ellipse =>
                RepositionPrimitivePlacement(pending, snapped.X, snapped.Y, pointerVisible: true),
            _ => BuildStampPlacement(
                snapped.X, snapped.Y, pointerVisible: true),
        };
    }

    public void SetPlacementPointerVisible(bool visible)
    {
        if (PendingPlacement is { } pending && pending.PointerVisible != visible)
        {
            PendingPlacement = pending with { PointerVisible = visible };
        }
    }

    /// <summary>
    /// Resizes the transient object around its own centre before placement. W/S therefore control
    /// the same size before and after placement without touching the document or history.
    /// </summary>
    public bool ResizePendingPlacement(int direction, bool largerStep = false)
    {
        if (PendingPlacement is not { } pending || direction == 0)
        {
            return false;
        }

        var delta = Math.Sign(direction)
            * (largerStep ? PlacementScaleLargeStep : PlacementScaleStep);
        if (pending.Target == PlacementTargetKind.Stamp)
        {
            SetStampScalePercent(_stampToolSettings.ScalePercent + (delta * 100), commit: true);
            StatusDetail = $"Размер перед установкой: {_stampToolSettings.ScalePercent:0.#}% · W/S.";
            return true;
        }

        var factor = MultiSelectionGeometry.ClampScaleFactor(pending.PreviewObjects, 1 + delta);
        var preview = MultiSelectionGeometry.ScaleAroundCentre(
            pending.PreviewObjects, factor, pending.WorldX, pending.WorldY);
        PendingPlacement = pending with
        {
            Width = preview[0].Width,
            Height = preview[0].Height,
            PreviewObjects = preview,
        };
        StatusDetail = $"Размер перед установкой: {preview[0].Width:0.#} × {preview[0].Height:0.#} · W/S.";
        return true;
    }

    /// <summary>
    /// Turns the transient object around its own centre before placement. The document, selection
    /// and history stay untouched; pointer moves keep the same angle and commit consumes the exact
    /// previewed rotation.
    /// </summary>
    public bool RotatePendingPlacement(int direction, bool largerStep = false)
    {
        if (PendingPlacement is not { } pending || direction == 0)
        {
            return false;
        }

        var delta = Math.Sign(direction)
            * (largerStep ? PlacementRotationLargeStep : PlacementRotationStep);
        if (pending.Target == PlacementTargetKind.Stamp)
        {
            SetStampRotationDegrees(_stampToolSettings.RotationDegrees + delta, commit: true);
            StatusDetail = $"Поворот перед установкой: {_stampToolSettings.RotationDegrees:0}° · A/D.";
            return true;
        }

        var rotation = NormalizeAngle(pending.RotationDegrees + delta);
        var preview = pending.PreviewObjects.Select(item => WithGeometry(
            item, item.X, item.Y, item.Width, item.Height,
            NormalizeAngle(item.RotationDegrees + delta))).ToArray();
        PendingPlacement = pending with
        {
            RotationDegrees = rotation,
            PreviewObjects = preview,
        };
        StatusDetail = $"Поворот перед установкой: {rotation:0}° · A/D.";
        return true;
    }

    public bool CancelPlacement()
    {
        var cancelledPath = CancelEditablePathGesture() | CancelEditableAreaGesture();
        CancelStampSeries();
        if (PendingPlacement is null)
        {
            return cancelledPath;
        }

        PendingPlacement = null;
        StatusDetail = "Размещение отменено.";
        return true;
    }

    public bool CreateObjectLayerAndContinuePlacement()
    {
        if (PendingPlacement is null)
        {
            return false;
        }

        AddObjectLayer();
        RefreshArmedPlacement();
        StatusDetail = "Создан слой объектов. Размещение можно продолжить.";
        return true;
    }

    private int _stampEraseEntries;

    /// <summary>
    /// The scatter circle run backwards: every stamp whose centre lies inside the circle at the
    /// pointer leaves the map. Held Alt in the Area mode drives it (as Inkarnate's subtract
    /// radius does); a whole drag is one undo entry, however many events it took.
    /// </summary>
    public bool EraseStampsAround(double worldX, double worldY)
    {
        if (PendingPlacement is not { Target: PlacementTargetKind.Stamp } pending) return false;
        var layer = pending.LayerId is { } layerId ? Session.Document.FindLayer(layerId) as ObjectLayer : ActiveObjectLayer();
        if (layer is not { Visible: true, Locked: false }) return false;
        var radius = Math.Max(1, StampAreaRadius);
        var ids = layer.Objects
            .Where(o => o.Kind == SceneObjectKind.Stamp
                && Math.Pow(o.X + (o.Width / 2) - worldX, 2) + Math.Pow(o.Y + (o.Height / 2) - worldY, 2) <= radius * radius)
            .Select(o => o.Id).ToList();
        if (ids.Count == 0) return false;
        Session.Execute(new DeleteObjectsCommand(Session.Document, ids));
        _stampEraseEntries++;
        AfterEdit();
        StatusDetail = $"Убрано ассетов: {ids.Count}. Alt + протягивание стирает дальше.";
        return true;
    }

    /// <summary>Folds the deletions of one Alt-drag into a single history entry.</summary>
    public void EndStampErase()
    {
        if (_stampEraseEntries > 1) Session.MergeLastEntries(_stampEraseEntries, "Стереть ассеты");
        _stampEraseEntries = 0;
        Raise(nameof(CanUndo)); Raise(nameof(CanRedo));
    }

    public bool CommitPendingPlacement()
    {
        if (PendingPlacement is not { } pending)
        {
            return false;
        }

        var layer = pending.LayerId is { } layerId
            ? Session.Document.FindLayer(layerId) as ObjectLayer
            : null;
        AddLayerCommand? createLayer = null;
        if (layer is not { Visible: true, Locked: false })
        {
            // Putting a tree on the map must not begin with housekeeping. When the document has no
            // usable object layer we make one here and hand it to the commit, which folds it into
            // the same history entry, so one undo takes the object and the layer away together.
            (layer, createLayer) = EnsureObjectLayer();
            if (layer is null)
            {
                PendingPlacement = pending with { LayerId = null, InvalidReason = NoUsableObjectLayer };
                StatusDetail = NoUsableObjectLayer;
                return false;
            }
        }

        return pending.Target == PlacementTargetKind.Stamp
            ? CommitStampPlacement(pending, layer, createLayer)
            : CommitPrimitivePlacement(pending, layer, createLayer);
    }

    /// <summary>
    /// The object layer that receives new objects. Prefers the active layer, then the topmost
    /// visible unlocked object layer, and only otherwise describes a new one. The returned command
    /// is null when an existing layer was reused; when it is not null the caller MUST execute it as
    /// part of its own edit, because the returned layer is not in the document yet.
    /// </summary>
    private (ObjectLayer? Layer, AddLayerCommand? Create) EnsureObjectLayer()
    {
        if (ActiveObjectLayer() is { } existing)
        {
            return (existing, null);
        }

        // Object layers exist but the user hid or locked every one of them. That is a deliberate
        // decision about this map, and quietly adding a sibling layer would work around it, so the
        // caller refuses instead. Only a document with no object layer at all gets one made for it.
        if (!HasNoObjectLayer)
        {
            return (null, null);
        }

        var created = new ObjectLayer(
            LayerId.New(), EntityNameGenerator.Layer(Session.Document, typeof(ObjectLayer)),
            true, false, 1.0, []);
        return (created, new AddLayerCommand(created, _activeLayerId));
    }

    /// <summary>The map has no object layer at all - an unprepared document, not a locked one.</summary>
    private bool HasNoObjectLayer => !Session.Document.Layers.OfType<ObjectLayer>().Any();

    /// <summary>What to tell the user when every existing object layer refuses new objects.</summary>
    private const string NoUsableObjectLayer =
        "Слой объектов скрыт или заблокирован. Снимите блокировку или создайте новый слой объектов.";

    /// <summary>Runs an edit, together with the layer it needed, as exactly one history entry.</summary>
    private void ExecuteWithOptionalLayer(IEditCommand edit, AddLayerCommand? createLayer, string name) =>
        Session.Execute(createLayer is null ? edit : new CompositeCommand(name, [createLayer, edit]));

    private bool CommitPrimitivePlacement(
        PlacementSession pending, ObjectLayer layer, AddLayerCommand? createLayer)
    {
        var preview = pending.PreviewObjects.Single();
        var kind = pending.Target == PlacementTargetKind.Ellipse
            ? SceneObjectKind.Ellipse
            : SceneObjectKind.Rectangle;
        var added = new SceneObject(
            ObjectId.New(), kind, EntityNameGenerator.Object(Session.Document, kind),
            preview.X, preview.Y, preview.Width, preview.Height, preview.RotationDegrees,
            preview.FillColor, preview.StrokeColor, preview.StrokeWidth, true, false);
        var previousSelection = Session.SelectedObjectIds.ToArray();
        ExecuteWithOptionalLayer(
            new AddObjectCommand(layer.Id, added), createLayer, "Place object on a new layer");
        ApplyPlacementSelection(previousSelection, added.Id, pending);
        _activeLayerId = layer.Id;
        AfterEdit();
        FinishOrContinuePlacement(pending, added.Name);
        return true;
    }

    public (double Prepare, double Names, double Commit, double Metadata) LastPlacementTiming { get; private set; }

    private bool CommitStampPlacement(
        PlacementSession pending, ObjectLayer layer, AddLayerCommand? createLayer,
        IReadOnlyList<StampPlacementItem>? gesturePlan = null)
    {
        var timing = System.Diagnostics.Stopwatch.StartNew();
        if (CollectEditTimings) LastEditTimings.Clear();
        var settings = pending.StampSettingsSnapshot ?? _stampToolSettings;
        if (!TryStampInputs(settings, out var requestTemplate, out var sources, out var records, out var refusal))
        {
            StatusDetail = refusal;
            return false;
        }

        var request = requestTemplate with
        {
            CenterX = pending.WorldX,
            CenterY = pending.WorldY,
            Seed = pending.Seed,
        };
        if (gesturePlan is null) request = request with { ExistingCollisions = ExistingCollisionBoxes(request, layer.Id) };
        var plan = gesturePlan ?? (pending.Mode == StampPlacementMode.Area
            ? StampPlacement.PlanArea(request, sources)
            : StampPlacement.PlanOne(request, sources[0]));
        if (plan.Count == 0)
        {
            StatusDetail = "Здесь нет свободного места: с «Без наложений» каждый ассет задел бы уже стоящий.";
            return false;
        }
        if (gesturePlan is null) plan = ApplyPlacementRotation(plan, settings.RotationDegrees);
        if (ResolvePlan(plan, records, out var references, out refusal) is not { } byHash)
        {
            StatusDetail = refusal;
            return false;
        }

        var preferredName = SelectedSet?.Name
            ?? records[plan[0].Sha256].DisplayName;
        var prepareMs = timing.Elapsed.TotalMilliseconds;
        var names = EntityNameGenerator.Objects(
            Session.Document, SceneObjectKind.Stamp, plan.Count, preferredName);
        var namesMs = timing.Elapsed.TotalMilliseconds - prepareMs;
        var objects = new List<SceneObject>(plan.Count);
        for (var index = 0; index < plan.Count; index++)
        {
            var item = plan[index];
            objects.Add(StampObject(item, names[index], settings, previewOpacityMultiplier: null));
        }

        var placedLabel = CurrentVariantLabel;
        var previousSelection = Session.SelectedObjectIds.ToArray();
        _gestureCounter++;
        if (CollectEditTimings) LastEditTimings.Add(("prepare+names", prepareMs + namesMs));
        Timed("session-execute", () => ExecuteWithOptionalLayer(
            new PlaceAssetObjectsCommand(layer.Id, references, objects),
            createLayer, "Place stamp on a new layer"));
        Timed("placement-selection", () => ApplyPlacementSelection(previousSelection, objects[^1].Id, pending));
        _activeLayerId = layer.Id;
        AfterEdit();
        var commitMs = timing.Elapsed.TotalMilliseconds - prepareMs - namesMs;
        if (_selectedSetId is not null && CurrentVariant is { } placedVariant)
        {
            _assetCatalog!.TouchRecent(_selectedSetId, placedVariant.Id, _clock());
        }

        if (RandomVariantAfterPlacement && _selectedSetVariants.Count > 1)
        {
            _currentVariantIndex = StampPlacement.NextVariantIndex(
                request.Seed, _currentVariantIndex, _selectedSetVariants.Count);
            RememberCurrentVariant();
            RaiseVariantState();
        }

        var placementMessage = objects.Count == 1
            ? $"Размещён вариант {placedLabel}."
            : $"Размещено объектов: {objects.Count}, вариантов: {plan.Select(item => item.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count()}.";
        Timed("raise recent", () => { Raise(nameof(CompactRecentSets)); Raise(nameof(RecentCount)); });
        Timed("finish-or-continue", () => FinishOrContinuePlacement(pending, objects[^1].Name));
        StatusDetail = placementMessage + " Ассет остаётся активным · Esc завершить.";
        if (_assetCatalog?.MetadataSaveError is not null)
            StatusDetail += " Объекты размещены; недавние пока не удалось сохранить на диск.";
        LastPlacementTiming = (prepareMs, namesMs, commitMs,
            timing.Elapsed.TotalMilliseconds - prepareMs - namesMs - commitMs);
        return true;
    }

    private void ApplyPlacementSelection(
        IReadOnlyList<ObjectId> previousSelection, ObjectId addedId, PlacementSession placement)
    {
        var select = placement.AutoSelect == PlacementAutoSelectPolicy.Always
            || placement.AutoSelect == PlacementAutoSelectPolicy.Auto
                && placement.Target != PlacementTargetKind.Stamp
                && placement.Mode == StampPlacementMode.One;
        if (select)
        {
            Session.SelectedObjectId = addedId;
        }
        else
        {
            Session.SetSelection(previousSelection);
        }
    }

    private void FinishOrContinuePlacement(PlacementSession committed, string name)
    {
        if (committed.Target != PlacementTargetKind.Stamp
            && committed.Mode == StampPlacementMode.One)
        {
            PendingPlacement = null;
            StatusDetail = $"Создан «{name}».";
            return;
        }

        PendingPlacement = committed.Target == PlacementTargetKind.Stamp
            ? BuildStampPlacement(
                committed.WorldX, committed.WorldY, pointerVisible: true)
            : RepositionPrimitivePlacement(
                committed, committed.WorldX, committed.WorldY, pointerVisible: true);
        StatusDetail = committed.Target == PlacementTargetKind.Stamp
            ? $"Размещён «{name}». Ассет остаётся активным · Esc завершить."
            : $"Создан «{name}». Режим серии остаётся активным.";
    }

    private PlacementSession RepositionPrimitivePlacement(
        PlacementSession pending,
        double centerX,
        double centerY,
        bool pointerVisible)
    {
        var layer = ActiveObjectLayer();
        return pending with
        {
            Mode = StampPlacementMode == StampPlacementMode.Area
                ? StampPlacementMode.One
                : StampPlacementMode,
            AutoSelect = PlacementAutoSelect,
            LayerId = layer?.Id,
            WorldX = centerX,
            WorldY = centerY,
            PointerVisible = pointerVisible,
            InvalidReason = layer is null && !HasNoObjectLayer ? NoUsableObjectLayer : null,
            WillCreateLayer = layer is null && HasNoObjectLayer,
            PreviewObjects = MultiSelectionGeometry.Translate(
                pending.PreviewObjects, centerX - pending.WorldX, centerY - pending.WorldY),
        };
    }

    private PlacementSession BuildPrimitivePlacement(
        SceneObjectKind kind,
        double centerX,
        double centerY,
        StampPlacementMode mode,
        bool pointerVisible,
        double rotationDegrees = 0)
    {
        rotationDegrees = NormalizeAngle(rotationDegrees);
        var layer = ActiveObjectLayer();
        var viewportWorldWidth = Session.Viewport.ViewWidth > 0
            ? Session.Viewport.ViewWidth / Math.Max(Session.Viewport.Scale, 0.01)
            : 1_000;
        var width = Math.Clamp(viewportWorldWidth * 0.18, 96, 240);
        width = Math.Min(width, Session.Document.Canvas.Width);
        var height = Math.Min(kind == SceneObjectKind.Ellipse ? width * 0.68 : width * 0.62,
            Session.Document.Canvas.Height);
        var preview = new SceneObject(
            PendingPlacement?.PreviewObjects.FirstOrDefault()?.Id ?? ObjectId.New(),
            kind,
            kind == SceneObjectKind.Ellipse ? "Будущий эллипс" : "Будущий прямоугольник",
            centerX - width / 2,
            centerY - height / 2,
            width,
            height,
            rotationDegrees,
            kind == SceneObjectKind.Ellipse ? "#FF5B8266" : "#FF3E5C76",
            "#FF10131A",
            2,
            true,
            false)
        {
            ObjectOpacity = PlacementPreviewOpacity,
        };
        return new PlacementSession
        {
            Target = kind == SceneObjectKind.Ellipse
                ? PlacementTargetKind.Ellipse
                : PlacementTargetKind.Rectangle,
            Mode = mode,
            AutoSelect = PlacementAutoSelect,
            LayerId = layer?.Id,
            WorldX = centerX,
            WorldY = centerY,
            Width = width,
            Height = height,
            RotationDegrees = rotationDegrees,
            FillColor = preview.FillColor,
            StrokeColor = preview.StrokeColor,
            StrokeWidth = preview.StrokeWidth,
            PreviewOpacity = PlacementPreviewOpacity,
            PointerVisible = pointerVisible,
            InvalidReason = layer is null && !HasNoObjectLayer ? NoUsableObjectLayer : null,
            WillCreateLayer = layer is null && HasNoObjectLayer,
            Seed = 0,
            PlannedCount = 1,
            PreviewObjects = [preview],
        };
    }

    private PlacementSession BuildStampPlacement(
        double centerX,
        double centerY,
        bool pointerVisible)
    {
        var settings = _stampToolSettings;
        var rotationDegrees = NormalizeAngle(settings.RotationDegrees);
        var layer = ActiveObjectLayer();
        if (!TryStampInputs(settings, out var requestTemplate, out var sources, out var records, out var refusal))
        {
            return EmptyStampPlacement(
                centerX, centerY, layer?.Id, pointerVisible, refusal, rotationDegrees);
        }

        var request = requestTemplate with
        {
            CenterX = centerX,
            CenterY = centerY,
            Seed = NextGestureSeed(),
        };
        request = request with { ExistingCollisions = ExistingCollisionBoxes(request, layer?.Id) };
        var plan = StampPlacementMode == StampPlacementMode.Area
            ? StampPlacement.PlanAreaPreview(request, sources, MaximumScatterPreviewObjects)
            : StampPlacement.PlanOne(request, sources[0]);
        if (plan.Count == 0)
        {
            // Only «Без наложений» can leave nothing: every box would hit a stamp already there.
            return EmptyStampPlacement(centerX, centerY, layer?.Id, pointerVisible,
                "Здесь нет свободного места: с «Без наложений» каждый ассет задел бы уже стоящий.", rotationDegrees);
        }
        plan = ApplyPlacementRotation(plan, rotationDegrees);
        var preview = plan.Select((item, index) => StampObject(
            item,
            index == 0 ? "Размещаемый ассет" : $"Размещаемый ассет {index + 1}",
            settings,
            PlacementPreviewOpacity)).ToList();
        return new PlacementSession
        {
            Target = PlacementTargetKind.Stamp,
            Mode = StampPlacementMode,
            AutoSelect = PlacementAutoSelect,
            LayerId = layer?.Id,
            WorldX = centerX,
            WorldY = centerY,
            Width = preview[0].Width,
            Height = preview[0].Height,
            RotationDegrees = rotationDegrees,
            FillColor = "#FFFFFFFF",
            StrokeColor = "#00000000",
            StrokeWidth = 0,
            PreviewOpacity = PlacementPreviewOpacity,
            PointerVisible = pointerVisible,
            InvalidReason = layer is null && !HasNoObjectLayer ? NoUsableObjectLayer : null,
            WillCreateLayer = layer is null && HasNoObjectLayer,
            Seed = request.Seed,
            PlannedCount = StampPlacementMode == StampPlacementMode.Area
                ? StampPlacement.CountFor(request.Density, request.AreaRadius)
                : 1,
            PreviewObjects = preview,
            AssetSetId = _selectedSetId,
            VariantId = CurrentVariant?.Id,
            VariantSha256 = CurrentVariant?.Sha256,
            StampSettingsSnapshot = settings,
        };
    }

    private PlacementSession EmptyStampPlacement(
        double centerX,
        double centerY,
        LayerId? layerId,
        bool pointerVisible,
        string refusal,
        double rotationDegrees) => new()
    {
        Target = PlacementTargetKind.Stamp,
        Mode = StampPlacementMode,
        AutoSelect = PlacementAutoSelect,
        LayerId = layerId,
        WorldX = centerX,
        WorldY = centerY,
        Width = 0,
        Height = 0,
        RotationDegrees = rotationDegrees,
        FillColor = "#FFFFFFFF",
        StrokeColor = "#00000000",
        StrokeWidth = 0,
        PreviewOpacity = PlacementPreviewOpacity,
        PointerVisible = pointerVisible,
        InvalidReason = refusal,
        Seed = NextGestureSeed(),
        PlannedCount = 0,
        PreviewObjects = [],
        AssetSetId = _selectedSetId,
        VariantId = CurrentVariant?.Id,
        VariantSha256 = CurrentVariant?.Sha256,
        StampSettingsSnapshot = _stampToolSettings,
    };

    private bool TryStampInputs(
        StampToolSettings settings,
        out StampPlacementRequest request,
        out IReadOnlyList<StampVariantSource> sources,
        out Dictionary<string, AssetRecord> records,
        out string refusal)
    {
        request = new StampPlacementRequest();
        sources = [];
        records = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        refusal = "";
        if (_assetCatalog is null || _assetResolver is null)
        {
            refusal = "Локальный каталог ассетов недоступен.";
            return false;
        }
        if (_assetCatalog.Status == AssetLibraryStatus.Damaged)
        {
            refusal = "Индекс библиотеки повреждён. " + _assetCatalog.Diagnostic;
            return false;
        }
        if (SelectedSet is null || CurrentVariant is null)
        {
            refusal = "Сначала выберите набор и вариант в библиотеке.";
            return false;
        }

        var scatter = settings.Mode is StampPlacementMode.Area or StampPlacementMode.Series or StampPlacementMode.Path or StampPlacementMode.FillArea;
        var randomize = settings.RandomizeVariant && scatter;
        var candidates = randomize
            ? _selectedSetVariants
            : [_selectedSetVariants[_currentVariantIndex]];
        var resolvedSources = new List<StampVariantSource>(candidates.Count);
        foreach (var variant in candidates)
        {
            if (_assetCatalog.Find(variant.Sha256) is not { } record)
            {
                continue;
            }
            records.TryAdd(record.Sha256, record);
            if (variant.MaskSha256 is { } maskSha
                && _assetCatalog.Find(maskSha) is { } maskRecord)
            {
                records.TryAdd(maskRecord.Sha256, maskRecord);
            }
            resolvedSources.Add(new StampVariantSource(
                record.Sha256, record.Width, record.Height, variant.MaskSha256,
                StampCollision.ClampFraction(_assetCatalog.FindSet(SelectedSet.SetId)?.Placement?.CollisionFraction)));
        }
        if (resolvedSources.Count == 0)
        {
            refusal = "Содержимое выбранного набора отсутствует в каталоге.";
            return false;
        }

        sources = resolvedSources;
        request = new StampPlacementRequest
        {
            AreaRadius = settings.AreaRadius,
            Density = settings.Density,
            RandomizeVariant = randomize && sources.Count > 1,
            RandomAngleStep = scatter ? settings.RandomAngleStep : Map.Core.Editing.StampRandomAngleStep.Off,
            RandomFlip = settings.RandomFlip && scatter && SelectedSetCanFlip,
            BaseWidth = DefaultStampReferenceWidth() * settings.ScalePercent / 100.0,
            RotationJitterDegrees = scatter ? settings.RotationJitterDegrees : 0,
            SizeJitter = scatter ? settings.SizeJitterPercent / 100.0 : 0,
            OpacityJitter = scatter ? settings.OpacityJitterPercent / 100.0 : 0,
            HueJitterDegrees = scatter ? settings.HueJitterDegrees : 0,
            SaturationJitter = scatter ? settings.SaturationJitterPercent / 100.0 : 0,
            BrightnessJitter = scatter ? settings.BrightnessJitterPercent / 100.0 : 0,
            AvoidOverlap = settings.AvoidOverlap && settings.Mode == StampPlacementMode.Area,
            CollisionRotationDegrees = NormalizeAngle(settings.RotationDegrees),
        };
        return true;
    }

    /// <summary>
    /// Collision boxes of the stamps already on the target layer around a scatter gesture. Each
    /// stamp uses the passport of its own card (30 % when it has none); only the neighbourhood
    /// of the circle is read, through the layer's cached spatial index.
    /// </summary>
    private IReadOnlyList<StampCollisionBox> ExistingCollisionBoxes(StampPlacementRequest request, LayerId? layerId)
    {
        if (!request.AvoidOverlap || layerId is not { } id
            || Session.Document.FindLayer(id) is not ObjectLayer layer || layer.Objects.Count == 0) return [];
        var margin = request.AreaRadius + (request.BaseWidth * 4);
        var bounds = new WorldBounds(request.CenterX - margin, request.CenterY - margin,
            request.CenterX + margin, request.CenterY + margin);
        var fractions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var boxes = new List<StampCollisionBox>();
        foreach (var item in ObjectSpatialIndex.For(layer).Query(bounds))
        {
            if (item.Kind != SceneObjectKind.Stamp || item.Stamp is not { } stamp || !item.Visible) continue;
            if (!fractions.TryGetValue(stamp.AssetSha256, out var fraction))
            {
                fraction = StampCollision.ClampFraction(_assetCatalog?.FindSetForContent(stamp.AssetSha256)?.Placement?.CollisionFraction);
                fractions[stamp.AssetSha256] = fraction;
            }
            boxes.Add(StampCollision.Box(item.X + (item.Width / 2), item.Y + (item.Height / 2),
                item.Width, item.Height, item.RotationDegrees, fraction));
        }
        return boxes;
    }

    private static SceneObject StampObject(
        StampPlacementItem item,
        string name,
        StampToolSettings settings,
        double? previewOpacityMultiplier) => new(
        ObjectId.New(), SceneObjectKind.Stamp, name,
        item.X, item.Y, item.Width, item.Height, item.RotationDegrees,
        "#FFFFFFFF", "#00000000", 0, true, false)
    {
        Sublayer = settings.Sublayer,
        ObjectOpacity = Math.Clamp(settings.ObjectOpacity * item.OpacityFactor, 0, 1) * (previewOpacityMultiplier ?? 1),
        Stamp = new StampStyle(item.Sha256)
        {
            ReferenceWidth = item.Width / Math.Max(0.1, settings.ScalePercent / 100.0),
            ReferenceHeight = item.Height / Math.Max(0.1, settings.ScalePercent / 100.0),
            FlipHorizontal = (settings.Style?.FlipHorizontal ?? false) ^ item.FlipHorizontal,
            FlipVertical = (settings.Style?.FlipVertical ?? false) ^ item.FlipVertical,
            MaskAssetSha256 = item.MaskSha256,
            ClipMaskEnabled = item.MaskSha256 is not null
                && (settings.Style?.ClipMaskEnabled ?? false),
            Blend = settings.Style?.Blend ?? BlendModes.Normal,
            Blur = settings.Style?.Blur ?? 0,
            ShadowOffsetX = settings.Style?.ShadowOffsetX ?? ShadowPreset.StandardOffsetX,
            ShadowOffsetY = settings.Style?.ShadowOffsetY ?? ShadowPreset.StandardOffsetY,
            ShadowBlur = settings.Style?.ShadowBlur ?? ShadowPreset.StandardBlur,
            ShadowOpacity = settings.Style?.ShadowOpacity ?? 0,
            ShadowPlacement = settings.Style?.ShadowPlacement ?? StampShadowPlacement.Object,
            ShadowColor = settings.Style?.ShadowColor ?? AppearanceColor.OpaqueBlack,
            Hue = NormalizeSignedAngle((settings.Style?.Hue ?? 0) + item.HueShift),
            Saturation = Math.Clamp((settings.Style?.Saturation ?? 0) + item.SaturationShift, -1, 1),
            Brightness = Math.Clamp((settings.Style?.Brightness ?? 0) + item.BrightnessShift, -1, 1),
            Contrast = settings.Style?.Contrast ?? 0,
        },
    };

    private static double NormalizeSignedAngle(double degrees)
    {
        var wrapped = ((degrees + 180) % 360 + 360) % 360 - 180;
        return wrapped;
    }

    private static IReadOnlyList<StampPlacementItem> ApplyPlacementRotation(
        IReadOnlyList<StampPlacementItem> plan,
        double rotationDegrees)
    {
        if (rotationDegrees == 0)
        {
            return plan;
        }

        return [.. plan.Select(item => item with
        {
            RotationDegrees = NormalizeUnsignedAngle(item.RotationDegrees + rotationDegrees),
        })];
    }

    private VectorPoint SnapPlacementPoint(double worldX, double worldY) =>
        ActiveSnapGrid() is { } grid
            ? GridSnapService.Snap(grid, new VectorPoint(worldX, worldY))
            : new VectorPoint(WholeGeometryValue(worldX), WholeGeometryValue(worldY));

    private void RefreshArmedPlacement()
    {
        RefreshAreaDraftSettings();
        if (PendingPlacement is not { } pending)
        {
            return;
        }

        PendingPlacement = pending.Target == PlacementTargetKind.Stamp
            ? BuildStampPlacement(
                pending.WorldX, pending.WorldY, pending.PointerVisible)
            : RepositionPrimitivePlacement(
                pending, pending.WorldX, pending.WorldY, pending.PointerVisible);
    }

    private void UpdateStampToolSettings(
        Func<StampToolSettings, StampToolSettings> change,
        bool persist)
    {
        var changed = NormalizeStampToolSettings(change(_stampToolSettings));
        if (changed == _stampToolSettings)
        {
            if (persist) PersistLocalPreferences();
            return;
        }

        _stampToolSettings = changed;
        RaiseStampToolState();
        RefreshArmedPlacement();
        if (persist)
        {
            PersistLocalPreferences();
        }
    }

    private void RaiseStampToolState()
    {
        Raise(nameof(StampToolSettings));
        Raise(nameof(IsEditingFutureStamp));
        Raise(nameof(StampPanelHeading));
        Raise(nameof(StampPanelStyle));
        Raise(nameof(StampPanelMaskSha256));
        Raise(nameof(StampClipMaskThumbnailPath));
        Raise(nameof(StampClipMaskStatus));
        Raise(nameof(StampClipMaskCanToggle));
        Raise(nameof(StampPanelCanEditLibraryMask));
        Raise(nameof(StampPanelVariantHasLibraryMask));
        Raise(nameof(StampForegroundStatus));
        Raise(nameof(StampScalePercent));
        Raise(nameof(StampRotationDegrees));
        Raise(nameof(StampSublayer));
        Raise(nameof(StampOpacityPercent));
        Raise(nameof(StampPlacementMode));
        Raise(nameof(PlacementAutoSelect));
        Raise(nameof(StampAreaRadius));
        Raise(nameof(StampDensity));
        Raise(nameof(RandomVariantAfterPlacement));
        Raise(nameof(StampRandomAngleStep));
        Raise(nameof(StampRandomAngle));
        Raise(nameof(StampRandomFlip));
        Raise(nameof(StampAvoidOverlap));
        Raise(nameof(PlannedStampCount));
        Raise(nameof(StampAreaSummary));
    }

    private static StampToolSettings NormalizeStampToolSettings(StampToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var style = settings.Style;
        if (style is not null)
        {
            var color = AppearanceColor.Normalize(style.ShadowColor) ?? AppearanceColor.OpaqueBlack;
            var blend = BlendModes.IsSupported(style.Blend) ? style.Blend : BlendModes.Normal;
            var maskSha256 = NormalizeSha256(style.MaskAssetSha256);
            style = style with
            {
                MaskAssetSha256 = maskSha256,
                ClipMaskEnabled = maskSha256 is not null && style.ClipMaskEnabled,
                Blend = blend,
                Blur = Finite(style.Blur, 0, 0, AppearanceLimits.MaxBlur),
                ShadowOffsetX = Finite(style.ShadowOffsetX, ShadowPreset.StandardOffsetX,
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowOffsetY = Finite(style.ShadowOffsetY, ShadowPreset.StandardOffsetY,
                    -AppearanceLimits.MaxShadowOffset, AppearanceLimits.MaxShadowOffset),
                ShadowBlur = Finite(style.ShadowBlur, ShadowPreset.StandardBlur, 0,
                    AppearanceLimits.MaxShadowBlur),
                ShadowOpacity = Finite(style.ShadowOpacity, 0, 0, 1),
                ShadowPlacement = Enum.IsDefined(style.ShadowPlacement)
                    ? style.ShadowPlacement
                    : StampShadowPlacement.Object,
                ShadowColor = color,
                Hue = Finite(style.Hue, 0, -180, 180),
                Saturation = Finite(style.Saturation, 0, -1, 1),
                Brightness = Finite(style.Brightness, 0, -1, 1),
                Contrast = Finite(style.Contrast, 0, -1, 1),
            };
        }

        return settings with
        {
            ScalePercent = Finite(settings.ScalePercent, 100, 10, 400),
            RotationDegrees = NormalizeUnsignedAngle(Finite(settings.RotationDegrees, 0, -360_000, 360_000)),
            Sublayer = Math.Clamp(settings.Sublayer, -10, 10),
            ObjectOpacity = Finite(settings.ObjectOpacity, 1, 0, 1),
            Style = style,
            Mode = Enum.IsDefined(settings.Mode) ? settings.Mode : StampPlacementMode.One,
            AutoSelect = Enum.IsDefined(settings.AutoSelect)
                ? settings.AutoSelect
                : PlacementAutoSelectPolicy.Auto,
            AreaRadius = Finite(settings.AreaRadius, 160, MinStampAreaRadius, MaxStampAreaRadius),
            Density = Finite(settings.Density, 40, MinStampDensity, MaxStampDensity),
            SeriesSpacingPercent = Finite(settings.SeriesSpacingPercent, 75, 10, 400),
            RandomAngleStep = IsSupportedRandomAngleStep(settings.RandomAngleStep)
                ? settings.RandomAngleStep
                : Map.Core.Editing.StampRandomAngleStep.Off,
        };
    }

    private static bool IsSupportedRandomAngleStep(Map.Core.Editing.StampRandomAngleStep value) =>
        value is Map.Core.Editing.StampRandomAngleStep.Off
            or Map.Core.Editing.StampRandomAngleStep.Degrees15
            or Map.Core.Editing.StampRandomAngleStep.Degrees30
            or Map.Core.Editing.StampRandomAngleStep.Degrees45
            or Map.Core.Editing.StampRandomAngleStep.Degrees60
            or Map.Core.Editing.StampRandomAngleStep.Degrees90
            or Map.Core.Editing.StampRandomAngleStep.Degrees180;

    private void PersistLocalPreferences() => _saveLocalPreferences?.Invoke(
        new EditorLocalPreferences(PlacementAutoSelect, _primaryAutosaveEnabled, _stampToolSettings,
            LibraryTileSize, LibraryShowNames));

    /// <summary>
    /// One pointer gesture, all or nothing. The plan is resolved first, then every distinct hash
    /// it needs is verified and read exactly once, and only a fully resolved plan is committed as
    /// a single undoable command. If one variant is missing or changed, nothing is placed at all:
    /// a shortened plan committed as a success is the one outcome the user cannot detect.
    /// </summary>
    public bool PlaceCurrentVariantAt(double worldX, double worldY)
    {
        if (PendingPlacement is not { Target: PlacementTargetKind.Stamp })
        {
            ArmStampPlacement();
        }
        UpdatePlacementPreview(worldX, worldY);
        return CommitPendingPlacement();
    }

    /// <summary>
    /// Resolves every distinct hash the plan needs, once each, and returns null the moment one of
    /// them cannot be placed. The cost of a gesture therefore follows the number of variants it
    /// uses, not the thousands of stamps it may put on the canvas.
    /// </summary>
    private Dictionary<string, MapAssetReference>? ResolvePlan(
        IReadOnlyList<StampPlacementItem> plan,
        Dictionary<string, AssetRecord> records,
        out List<MapAssetReference> references,
        out string refusal)
    {
        var byHash = new Dictionary<string, MapAssetReference>(StringComparer.OrdinalIgnoreCase);
        references = [];
        refusal = "";

        var existing = Session.Document.Assets.ToDictionary(
            asset => asset.Sha256, StringComparer.OrdinalIgnoreCase);
        var neededHashes = plan.SelectMany(item => item.MaskSha256 is { } mask
                ? new[] { item.Sha256, mask }
                : new[] { item.Sha256 })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var newRecords = new List<AssetRecord>();
        long advertisedBytes = 0;
        try
        {
            foreach (var sha256 in neededHashes)
            {
                if (existing.ContainsKey(sha256))
                {
                    continue;
                }

                if (!records.TryGetValue(sha256, out var record))
                {
                    refusal = "Содержимое выбранного набора отсутствует в каталоге. Ничего не размещено.";
                    return null;
                }

                newRecords.Add(record);
                advertisedBytes = checked(advertisedBytes + record.ByteLength);
            }
        }
        catch (OverflowException)
        {
            refusal = "Размер выбранного набора переполняет допустимый бюджет проекта. Ничего не размещено.";
            return null;
        }

        if (!MapProjectStore.CanAdmitEmbeddedBytes(
                Session.Document, advertisedBytes, newRecords.Count, out refusal))
        {
            refusal += " Ничего не размещено.";
            return null;
        }

        foreach (var sha256 in neededHashes)
        {
            if (byHash.ContainsKey(sha256))
            {
                continue;
            }

            if (existing.TryGetValue(sha256, out var existingReference))
            {
                byHash[sha256] = existingReference;
                references.Add(existingReference);
                continue;
            }

            if (!records.TryGetValue(sha256, out var record))
            {
                refusal = "Содержимое варианта или его маски отсутствует в каталоге. Ничего не размещено.";
                return null;
            }

            var resolution = _assetResolver!.Resolve(record);
            if (!resolution.IsAvailable)
            {
                refusal = Unplaceable(record, resolution.Availability);
                return null;
            }

            byHash[sha256] = resolution.Reference!;
            references.Add(resolution.Reference!);
            if (!MapProjectStore.CanAdmitAdditionalAssets(Session.Document, references, out refusal))
            {
                refusal += " Ничего не размещено.";
                return null;
            }
        }

        return byHash;
    }

    /// <summary>
    /// Reuses the immutable payload already owned by the document. This keeps repeated actions
    /// independent of a deleted catalog original and prevents undo entries from retaining a fresh
    /// 64 MiB clone for the same SHA on every click.
    /// </summary>
    private MapAssetReference? ResolveAssetForDocument(
        AssetRecord record,
        out AssetAvailability availability,
        out string? refusal)
    {
        if (Session.Document.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase))
            is { } existing)
        {
            availability = AssetAvailability.Available;
            refusal = null;
            return existing;
        }

        if (!MapProjectStore.CanAdmitEmbeddedBytes(
                Session.Document, record.ByteLength, additionalAssets: 1, out var preflight))
        {
            availability = AssetAvailability.Missing;
            refusal = preflight;
            return null;
        }

        var resolution = _assetResolver!.Resolve(record);
        availability = resolution.Availability;
        if (!resolution.IsAvailable || resolution.Reference is null)
        {
            refusal = null;
            return null;
        }

        if (!MapProjectStore.CanAdmitAdditionalAssets(
                Session.Document, [resolution.Reference], out var exactFailure))
        {
            refusal = exactFailure;
            return null;
        }

        refusal = null;
        return resolution.Reference;
    }

    /// <summary>Names the variant and the exact reason it stopped the whole gesture.</summary>
    private static string Unplaceable(AssetRecord record, AssetAvailability availability) => availability switch
    {
        AssetAvailability.Missing =>
            $"Вариант «{record.DisplayName}» отсутствует на диске. Ничего не размещено.",
        AssetAvailability.HashMismatch =>
            $"Файл варианта «{record.DisplayName}» не совпадает с сохранённым SHA-256. Ничего не размещено.",
        AssetAvailability.Unsupported =>
            $"Формат варианта «{record.DisplayName}» не поддерживается. Ничего не размещено.",
        _ => $"Вариант «{record.DisplayName}» сейчас недоступен. Ничего не размещено.",
    };

    /// <summary>The same gesture a canvas press runs, centred on the current view.</summary>
    public bool PlaceCurrentVariantAtViewCentre()
    {
        var (x, y) = ViewCentre();
        return PlaceCurrentVariantAt(x, y);
    }

    /// <summary>Swaps only the asset of the selected stamp; transform and style are untouched.</summary>
    public void ReplaceSelectedStampWithCurrentVariant()
    {
        if (_assetCatalog is null || CurrentVariant is not { } variant)
        {
            StatusDetail = "Сначала выберите набор и вариант в библиотеке.";
            return;
        }

        if (_assetCatalog.Find(variant.Sha256) is not { } record)
        {
            StatusDetail = "Содержимое варианта отсутствует в каталоге.";
            return;
        }

        ReplaceSelectedStampAsset(record, variant);
    }

    /// <summary>One import of many files becomes one named card the tool can immediately use.</summary>
    public AssetSet? ImportAssetSet(IReadOnlyList<string> paths, string setName)
    {
        if (_assetCatalog is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return null;
        }

        try
        {
            var set = _assetCatalog.ImportSet(paths, setName, new AssetImportOptions
            {
                Provenance = new AssetProvenance
                {
                    Source = "user-imported",
                    LicenseStatus = AssetLicenseStatus.Unknown,
                },
            }, _clock());
            Raise(nameof(Assets));
            RefreshLibrary();
            SelectAssetSet(set.Id);
            StatusDetail = set.VariantIds.Count == 1
                ? $"Импортирован ассет «{set.Name}»."
                : $"Импортирован набор «{set.Name}»: {set.VariantIds.Count} вариантов.";
            return set;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusDetail = "Не удалось импортировать набор: " + ex.Message;
            _log.Error("Asset set import failed", ex);
            return null;
        }
    }

    private double DefaultStampReferenceWidth() =>
        Math.Max(1, Math.Min(256.0, Session.Document.Canvas.Width / 4.0));

    private double DefaultStampWidth() =>
        DefaultStampReferenceWidth() * _stampToolSettings.ScalePercent / 100.0;

    /// <summary>
    /// The seed of the gesture that is about to be planned. It is a fixed mix rather than a
    /// framework hash, because the framework randomises string hashing per process and a replay
    /// promised in the documentation has to survive a restart. The counter is not consumed here:
    /// only a committed gesture advances it, so a refused one changes nothing.
    /// </summary>
    private int NextGestureSeed() =>
        GestureSeed.From(Session.Revision, _selectedSetId, _currentVariantIndex, _gestureCounter + 1);

    private void RaiseLibrary()
    {
        Raise(nameof(LibrarySets));
        Raise(nameof(LibraryGroups));
        Raise(nameof(LibraryCollections));
        Raise(nameof(VisibleLibraryTiles));
        Raise(nameof(VisibleLibraryTileWindow));
        Raise(nameof(LibraryStatus));
        Raise(nameof(LibraryDiagnostic));
        Raise(nameof(IsLibraryAvailable));
        Raise(nameof(LibraryRows));
        Raise(nameof(SelectedLibraryRowIndex));
        Raise(nameof(HasActiveLibraryFilters));
        Raise(nameof(ActiveLibraryFilterSummary));
        Raise(nameof(CompactRecentSets));
        Raise(nameof(CompactFavoriteSets));
    }

    private void RaiseVariantState()
    {
        Raise(nameof(SelectedSetId));
        Raise(nameof(SelectedSet));
        Raise(nameof(SelectedSetVariants));
        Raise(nameof(CurrentVariantIndex));
        Raise(nameof(CurrentVariant));
        Raise(nameof(CurrentVariantLabel));
        Raise(nameof(StampFamilyVariants));
        Raise(nameof(StampFamilyName));
        Raise(nameof(CurrentStampFamilyLabel));
        Raise(nameof(IsCurrentVariantTheCover));
        Raise(nameof(SelectedLibraryRowIndex));
        Raise(nameof(CanMakeCurrentVariantCover));
    }

    public AssetRecord? ImportAsset(string path, bool linked)
    {
        if (_assetCatalog is null)
        {
            StatusDetail = "Локальный каталог ассетов недоступен.";
            return null;
        }

        try
        {
            var imported = _assetCatalog.Import(path, new AssetImportOptions
            {
                Linked = linked,
                Provenance = new AssetProvenance
                {
                    Source = "user-imported",
                    LicenseStatus = AssetLicenseStatus.Unknown,
                },
            }, _clock());
            Raise(nameof(Assets));
            StatusDetail = $"Импортирован ассет «{imported.DisplayName}».";
            return imported;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось импортировать изображение: " + ex.Message;
            _log.Error("Asset import failed", ex);
            return null;
        }
    }

    public void PlaceStamp(AssetRecord record, bool embedded = true, double? x = null, double? y = null)
    {
        if (_assetCatalog is null || _assetResolver is null)
        {
            return;
        }

        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null)
        {
            StatusDetail = NoUsableObjectLayer;
            return;
        }

        var knownReference = Session.Document.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase));
        if (knownReference is null
            && !MapProjectStore.CanAdmitEmbeddedBytes(
                Session.Document,
                embedded ? record.ByteLength : 0,
                additionalAssets: 1,
                out var preflightFailure))
        {
            StatusDetail = preflightFailure;
            return;
        }

        MapAssetReference? reference = knownReference;
        if (reference is null)
        {
            var resolution = _assetCatalog.ReadVerified(record);
            if (resolution.Status != AssetResolutionStatus.Available
                || resolution.Path is null || resolution.Content is null)
            {
                StatusDetail = resolution.Status == AssetResolutionStatus.HashMismatch
                    ? "Файл ассета изменился и не совпадает с сохранённым hash. Выберите замену явно."
                    : "Выберите доступный ассет и разблокированный слой объектов.";
                return;
            }

            var thumbnail = _assetCatalog.TryThumbnailPath(record, out var thumbnailPath)
                ? AssetImagePolicy.TryReadOptionalThumbnail(thumbnailPath!)
                : null;
            string? relative = null;
            if (!embedded && DocumentPath is { } documentPath
                && ProjectLinkedAssetPath.TryMakeRelative(documentPath, resolution.Path!, out var projectRelative))
            {
                relative = projectRelative;
            }
            reference = new MapAssetReference(
                record.Sha256,
                record.DisplayName,
                record.MediaType,
                record.Extension,
                embedded ? ProjectAssetStorage.Embedded : ProjectAssetStorage.Linked,
                record.ImportedUtc,
                record.Provenance.Source,
                record.Provenance.LicenseId,
                embeddedContent: embedded ? resolution.Content : null,
                catalogId: embedded ? null : record.Sha256,
                relativePath: embedded ? null : relative,
                fallbackThumbnailPng: thumbnail);
            if (!MapProjectStore.CanAdmitAdditionalAssets(
                    Session.Document, [reference], out var exactFailure))
            {
                StatusDetail = exactFailure;
                return;
            }
        }
        var width = Math.Min(256.0, Session.Document.Canvas.Width / 4.0);
        var height = width * record.Height / record.Width;
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, record.DisplayName,
            x ?? ((Session.Document.Canvas.Width - width) / 2),
            y ?? ((Session.Document.Canvas.Height - height) / 2),
            width, height, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(record.Sha256)
            {
                ReferenceWidth = width,
                ReferenceHeight = height,
            },
        };

        ExecuteWithOptionalLayer(
            new PlaceAssetObjectsCommand(layer.Id, reference, [stamp]),
            createLayer, "Place stamp on a new layer");
        Session.SelectedObjectId = stamp.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void ScatterStamp(AssetRecord record, int count, int seed = 12345)
    {
        if (_assetResolver is null || count < 1)
        {
            return;
        }

        var (layer, createLayer) = EnsureObjectLayer();
        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (layer is null || reference is null)
        {
            StatusDetail = layer is null ? NoUsableObjectLayer : assetRefusal ?? "Ассет недоступен для размещения.";
            return;
        }

        var random = new Random(seed);
        var objects = new List<SceneObject>(count);
        for (var i = 0; i < count; i++)
        {
            var size = 48 + (random.NextDouble() * 80);
            objects.Add(new SceneObject(
                ObjectId.New(), SceneObjectKind.Stamp, $"{record.DisplayName} {i + 1}",
                random.NextDouble() * Math.Max(1, Session.Document.Canvas.Width - size),
                random.NextDouble() * Math.Max(1, Session.Document.Canvas.Height - size),
                size, size * record.Height / record.Width,
                random.Next(-180, 181), "#FFFFFFFF", "#00000000", 0, true, false)
            {
                Stamp = new StampStyle(record.Sha256)
                {
                    ReferenceWidth = size,
                    ReferenceHeight = size * record.Height / record.Width,
                    FlipHorizontal = random.Next(2) == 0,
                },
            });
        }

        ExecuteWithOptionalLayer(
            new PlaceAssetObjectsCommand(layer.Id, reference, objects),
            createLayer, "Scatter stamps on a new layer");
        Session.SelectedObjectId = objects[^1].Id;
        _activeLayerId = layer.Id;
        AfterEdit();
    }

    public void ReplaceSelectedStampAsset(AssetRecord record, AssetVariant? variant = null)
    {
        if (_assetResolver is null || Session.SelectedObject is not { Stamp: not null } selected)
        {
            StatusDetail = "Выберите ассет на карте и новый ассет из каталога.";
            return;
        }

        var reference = ResolveAssetForDocument(
            record, out _, out var assetRefusal);
        if (reference is null)
        {
            StatusDetail = assetRefusal ?? "Новый ассет отсутствует или не совпадает с его SHA-256.";
            return;
        }

        variant ??= _assetCatalog?.Variants.FirstOrDefault(item =>
            string.Equals(item.Sha256, record.Sha256, StringComparison.OrdinalIgnoreCase));
        MapAssetReference? maskReference = null;
        if (variant?.MaskSha256 is { } maskSha)
        {
            maskReference = ResolveMaskForDocument(maskSha, out var maskRefusal);
            if (maskReference is null)
            {
                StatusDetail = maskRefusal ?? "Маска нового варианта отсутствует или повреждена.";
                return;
            }
        }

        Session.Execute(new ReplaceStampAssetCommand(
            selected,
            reference,
            record.Height / Math.Max(1.0, record.Width),
            variant?.MaskSha256,
            maskReference));
        AfterEdit();
    }

    public void KeepSelectedStampPlaceholder()
    {
        StatusDetail = "Заглушка оставлена: ссылка и исходный SHA-256 не изменены.";
    }

    public void UpdateSelectedStamp(
        double? opacity = null, bool toggleFlip = false, bool toggleShadow = false,
        double? hue = null, double? saturation = null)
    {
        if (Session.SelectedObject is not { Stamp: { } style } before || before.Locked)
        {
            StatusDetail = "Выберите незаблокированный ассет.";
            return;
        }

        var after = CopyObject(before) with
        {
            ObjectOpacity = opacity ?? before.ObjectOpacity,
            Stamp = style with
            {
                FlipHorizontal = toggleFlip ? !style.FlipHorizontal : style.FlipHorizontal,
                ShadowOpacity = toggleShadow ? (style.ShadowOpacity > 0 ? 0 : 0.55) : style.ShadowOpacity,
                Hue = hue ?? style.Hue,
                Saturation = saturation ?? style.Saturation,
            },
        };
        Session.Execute(new ReplaceObjectsCommand([before], [after], "Edit stamp style"));
        AfterEdit();
    }
}
