using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private Guid? _selectedAreaId;
    private PlacementArea? _areaDraft, _areaOriginal;
    private LayerId? _areaLayer;
    private IReadOnlyList<MapAssetReference> _areaAssets = [];
    private int _areaNode = -1;
    private bool _areaCreating, _areaDragging, _areaInsert;
    private bool _areaPickObject;
    private IReadOnlyList<SceneObject> _areaPreviewObjects = [];
    private IReadOnlyList<SceneObject> _areaOverrides = [];
    private string? _areaPreviewError;
    private bool _areaPreviewInvalid;
    private PlacementArea? _newAreaTemplate;
    private bool _areaStartRequested;
    private double _naturalAreaDensity = 70, _naturalAreaSizeVariation = 30;
    private PlacementAreaPattern _naturalAreaPattern;
    private readonly Dictionary<string, StampContentBounds> _areaContentBoundsCache = [];
    private CancellationTokenSource? _areaCountCancellation;
    private Task _areaCountTask = Task.CompletedTask;
    private long? _areaPredictedCount;
    private string? _areaCountError;
    public Task PendingEditableAreaCount => _areaCountTask;
    public long? PlannedEditableAreaCount => _areaCreating ? _areaPredictedCount : SelectedEditableArea?.MemberCount;
    public string EditableAreaResult
    {
        get
        {
            var count = PlannedEditableAreaCount is { } n ? $"Объектов: {n:N0}"
                : _areaCreating && _areaDraft is { Nodes.Length: >= 3 } ? _areaCountError ?? "Подсчёт объектов…" : "Обозначьте границу области";
            if (!UsesNaturalAreaLayout) return count;
            var density = EditableAreaDensity;
            var pattern = density == 0 ? "пустая область" : EditableAreaPattern switch
            {
                PlacementAreaPattern.Ridge => density <= 40 ? "основные вершины" : density <= 75 ? "гряда и ответвления" : "гряда с предгорьями",
                PlacementAreaPattern.Groups => density <= 40 ? "небольшие группы" : density <= 75 ? "группы с просветами" : "плотные отдельные группы",
                _ => density <= 40 ? "ядра массивов" : density <= 75 ? "массивы с просветами" : "массивы с неровной опушкой",
            };
            return $"{count} · {pattern}\nРазмеры: {(EditableAreaSizeVariation == 0 ? "одинаковые" : $"до ±{EditableAreaSizeVariation:0}%")}";
        }
    }
    public bool UsesNaturalAreaLayout => DisplayedEditableArea is not { LayoutVersion: < 3 };
    public double EditableAreaDensity => DisplayedEditableArea?.Density ?? _naturalAreaDensity;
    public double EditableAreaSizeVariation => DisplayedEditableArea?.SizeVariationPercent ?? _naturalAreaSizeVariation;
    public PlacementAreaPattern EditableAreaPattern => DisplayedEditableArea?.Pattern ?? _naturalAreaPattern;
    public string EditableAreaPatternHint => EditableAreaPattern switch
    {
        PlacementAreaPattern.Ridge => "Извилистая гряда: крупные вершины вдоль хребта, меньшие по бокам и на ответвлениях.",
        PlacementAreaPattern.Groups => "Отдельные плотные группы: крупные объекты внутри, мелкие по краям, свободные проходы между группами.",
        _ => "Связные массивы: плотная середина, неровный край и просветы. Подходит для лесов и больших скоплений.",
    };
    public IReadOnlyList<SceneObject>? EditableAreaPreviewOverrides => _areaDragging ? _areaOverrides : null;

    public IReadOnlyList<PlacementArea> EditableAreas => Session.Document.Layers.OfType<ObjectLayer>()
        .SelectMany(l => l.PlacementAreas).ToArray();
    public PlacementArea? SelectedEditableArea => Session.Document.Layers.OfType<ObjectLayer>()
        .SelectMany(l => l.PlacementAreas).FirstOrDefault(p => p.Id == _selectedAreaId);
    public PlacementArea? DisplayedEditableArea
    {
        get
        {
            if (_areaDraft is not null) return _areaDraft;
            var selected = SelectedEditableArea;
            return selected is not null && Session.Document.Layers.OfType<ObjectLayer>()
                .Any(l => l.Visible && l.StateFor(selected.Sublayer).Visible && l.PlacementAreas.Contains(selected))
                ? selected : null;
        }
    }
    public bool IsCreatingEditableArea => _areaCreating;
    public bool CanPlaceEditableArea => _areaCreating && _areaDraft is { Nodes.Length: >= 3 } && !_areaPreviewInvalid;
    public string EditableAreaPlacementHint
    {
        get
        {
            if (_areaCreating)
                return _areaPreviewError ?? (_areaDraft is { Nodes.Length: >= 3 }
                    ? "Граница готова. Нажмите «Разместить». Enter — разместить · Esc — отменить."
                    : $"Вершин: {_areaDraft?.Nodes.Length ?? 0} из 3. Добавьте вершины кликами по холсту.");
            if (!_areaStartRequested && PendingPlacement is not { Mode: StampPlacementMode.FillArea }
                && SelectedEditableArea is { } area)
                return area.MemberCount > 0
                    ? $"Размещено объектов: {area.MemberCount}. Границу можно редактировать. Для ещё одной области нажмите «Новая редактируемая область»."
                    : area.Density == 0
                        ? "Область создана пустой: плотность равна 0. Увеличьте плотность, чтобы разместить объекты."
                        : "Область создана, но объекты не поместились. Увеличьте область или плотность, уменьшите масштаб либо выберите свободное место.";
            return "Обозначьте область кликами по холсту: нужны минимум 3 вершины. Затем нажмите «Разместить».";
        }
    }
    public PlacementArea? CurrentAreaSource => StampPlacementMode == StampPlacementMode.FillArea
        ? _areaDraft ?? SelectedEditableArea ?? _newAreaTemplate : null;
    public bool HasAreaGesture => _areaDraft is not null;
    public int SelectedAreaNodeIndex => _areaNode;
    public int LastAreaChangedObjects { get; private set; }
    public int LastAreaVisitedCells { get; private set; }
    public int LastAreaChangedCells { get; private set; }
    public bool CanDetachAreaObject => Session.SelectedObjectId is { } id
        && PlacementAreaOwnership.Owner(Session.Document, id) is not null;
    public string EditableAreaSummary => DisplayedEditableArea is { } p
        ? _areaCreating
            ? $"{p.Name} · вершин: {p.Nodes.Length} · предпросмотр: {_areaPreviewObjects.Count} шт."
            : $"{p.Name} · вершин: {p.Nodes.Length} · объектов: {p.MemberCount} · исключений: {p.Exclusions.Count}"
        : "Выберите изображение или набор, затем создайте область кликами по холсту.";

    public void StartEditableArea()
    {
        _newAreaTemplate = SelectedEditableArea ?? _newAreaTemplate;
        CancelEditableAreaGesture(); _selectedAreaId = null; _areaNode = -1;
        _selectedPathId = null; CancelEditablePathGesture();
        ActiveTool = EditorTool.Stamp; StampPlacementMode = StampPlacementMode.FillArea;
        DeselectObject();
        ArmStampPlacement();
        _areaStartRequested = true;
        StatusDetail = "Область: ставьте вершины кликами; «Разместить» внизу панели или Enter — заполнить, Esc — отменить.";
        RaiseAreaState();
    }

    public bool AddEditableAreaNode(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
            || x > Session.Document.Canvas.Width || y > Session.Document.Canvas.Height) return false;
        if (!_areaCreating)
        {
            var settings = _stampToolSettings with { Mode = StampPlacementMode.FillArea };
            if (_newAreaTemplate is { } template)
            {
                _areaDraft = template with { Id = Guid.NewGuid(), Name = NextAreaName(template.Name.Split(" · ", 2).Last()),
                    Nodes = [], Cells = ImmutableDictionary<long, ImmutableArray<PlacementAreaMember>>.Empty,
                    MemberCount = 0, Exclusions = [], OccupiedSlots = [], LayoutVersion = 3,
                    Density = _naturalAreaDensity, SizeVariationPercent = _naturalAreaSizeVariation, Seed = NextGestureSeed(),
                    Pattern = _naturalAreaPattern, ContentBounds = ReadAreaContentBounds(template.Variants, template.ContentBounds),
                    ScalePercent = settings.ScalePercent, RotationDegrees = settings.RotationDegrees,
                    Sublayer = settings.Sublayer, Opacity = settings.ObjectOpacity, Style = settings.Style ?? template.Style,
                    Request = template.Request with { BaseWidth = template.Request.BaseWidth * settings.ScalePercent / template.ScalePercent,
                        RandomizeVariant = settings.RandomizeVariant, RandomAngleStep = settings.RandomAngleStep,
                        RandomFlip = settings.RandomFlip } };
                _areaAssets = []; _areaLayer = ActiveObjectLayer()?.Id;
            }
            else
            {
                if (!TryStampInputs(settings with { RandomizeVariant = true }, out var request, out var sources, out var records, out var refusal))
                { StatusDetail = refusal; return false; }
                sources = sources.OrderByDescending(s => s.Sha256 == CurrentVariant?.Sha256).ToArray();
                request = request with { RandomizeVariant = settings.RandomizeVariant };
                // Freeze every allowed variant now, including variants this area may not initially use.
                var variantPlan = sources.Select(s => new StampPlacementItem(s.Sha256, 0, 0, 1, 1, 0, false, false, s.MaskSha256)).ToArray();
                if (ResolvePlan(variantPlan, records, out var references, out refusal) is null)
                { StatusDetail = refusal; return false; }
                _areaAssets = references; _areaLayer = ActiveObjectLayer()?.Id;
                _areaDraft = new PlacementArea
                {
                    Name = NextAreaName(SelectedSet?.Name ?? "штампы"), AssetSetId = _selectedSetId, LayoutVersion = 3,
                    VariantId = CurrentVariant?.Id, Variants = [.. sources], Request = request,
                    Seed = NextGestureSeed(), Density = _naturalAreaDensity, SizeVariationPercent = _naturalAreaSizeVariation,
                    Pattern = _naturalAreaPattern, ContentBounds = ReadAreaContentBounds(sources),
                    RotationDegrees = settings.RotationDegrees, ScalePercent = settings.ScalePercent,
                    Sublayer = settings.Sublayer, Opacity = settings.ObjectOpacity,
                    Style = settings.Style ?? new StampStyle(sources[0].Sha256),
                };
            }
            _areaCreating = true; PendingPlacement = null;
        }
        var point = SnapPlacementPoint(x, y);
        if (_areaDraft!.Nodes.LastOrDefault() is { } last && last.X == point.X && last.Y == point.Y) return false;
        _areaDraft = _areaDraft with { Nodes = _areaDraft.Nodes.Add(new(Guid.NewGuid(), point.X, point.Y)) };
        _areaNode = _areaDraft.Nodes.Length - 1;
        UpdateAreaPreview(); RaiseAreaState(); return true;
    }

    public bool FinishEditableArea()
    {
        if (!_areaCreating || _areaDraft is not { Nodes.Length: >= 3 } draft)
        { StatusDetail = "Для области нужны хотя бы три разных вершины."; return false; }
        if (_areaPreviewInvalid) { StatusDetail = _areaPreviewError!; return false; }
        var (layer, create) = EnsureObjectLayer();
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return false; }
        var command = new PlacementAreaCommand(layer.Id, draft.Id, draft, "Создать редактируемую область", _areaAssets);
        var revision = Session.Revision;
        ExecuteWithOptionalLayer(command, create, command.Name);
        if (Session.Revision == revision) return false;
        _selectedAreaId = draft.Id; _activeLayerId = layer.Id; _gestureCounter++;
        CancelEditableAreaGesture(); PendingPlacement = null; Session.ClearSelection();
        LastAreaChangedObjects = command.ChangedObjects; LastAreaVisitedCells = command.VisitedCells;
        LastAreaChangedCells = command.ChangedCells;
        AfterEdit(); RaiseAreaState();
        StatusDetail = EditableAreaPlacementHint;
        return true;
    }

    public void SelectEditableArea(Guid id)
    {
        CancelPlacement(); CancelEditableAreaGesture();
        var layer = Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.PlacementAreas.Any(p => p.Id == id));
        if (layer is null) return;
        _selectedPathId = null; CancelEditablePathGesture();
        ActiveTool = EditorTool.Stamp; _selectedAreaId = id; _activeLayerId = layer.Id;
        _areaNode = 0; PendingPlacement = null; Session.ClearSelection(); SyncEditableAreaSettings(); RaiseAreaState();
    }

    private void SyncEditableAreaSettings()
    {
        if (SelectedEditableArea is not { } path) return;
        if (path.LayoutVersion >= 3)
        { _naturalAreaDensity = path.Density; _naturalAreaSizeVariation = path.SizeVariationPercent; _naturalAreaPattern = path.Pattern; }
        _stampToolSettings = _stampToolSettings with { Mode = StampPlacementMode.FillArea,
            Density = path.LayoutVersion >= 3 ? _stampToolSettings.Density : path.Density,
            ScalePercent = path.ScalePercent, RotationDegrees = path.RotationDegrees, Sublayer = path.Sublayer,
            ObjectOpacity = path.Opacity, Style = path.Style, RandomizeVariant = path.Request.RandomizeVariant,
            RandomAngleStep = path.Request.RandomAngleStep, RandomFlip = path.Request.RandomFlip };
        RaiseStampToolState();
    }

    public bool PressEditableArea(double x, double y)
    {
        if (ActiveTool != EditorTool.Stamp) return false;
        if (_areaPickObject) { _areaPickObject = false; return false; }
        if (_areaCreating || _areaStartRequested || PendingPlacement is { Mode: StampPlacementMode.FillArea })
        { AddEditableAreaNode(x, y); return true; }
        var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
        if (SelectedEditableArea is { } selected && EditableAreaLayer(selected) is not null)
        {
            if (_areaInsert)
            {
                var segment = ClosestAreaSegment(selected, x, y, tolerance * 2);
                if (segment >= 0)
                {
                    _areaInsert = false; var point = SnapPlacementPoint(x, y);
                    _areaNode = segment + 1;
                    return CommitAreaChange(selected with { Nodes = selected.Nodes.Insert(_areaNode,
                        new(Guid.NewGuid(), point.X, point.Y)) }, "Добавить вершину области");
                }
            }
            for (var i = 0; i < selected.Nodes.Length; i++)
                if (Math.Abs(selected.Nodes[i].X - x) <= tolerance && Math.Abs(selected.Nodes[i].Y - y) <= tolerance)
                {
                    _areaNode = i; _areaOriginal = selected; _areaDraft = selected; _areaDragging = true;
                    _areaLayer = EditableAreaLayer(selected)!.Id; RaiseAreaState(); return true;
                }
        }
        if (ActiveTool == EditorTool.Stamp && StampPlacementMode == StampPlacementMode.FillArea)
            foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>().Reverse().Where(l => l.Visible))
                foreach (var path in layer.PlacementAreas.Reverse())
                    if (layer.StateFor(path.Sublayer).Visible && (ClosestAreaSegment(path, x, y, tolerance) >= 0
                        || PlacementAreaGeometry.Contains(path.Nodes, x, y)))
                    { SelectEditableArea(path.Id); return true; }
        return false;
    }
    public bool IsDraggingAreaNode => _areaDragging;
    public void MoveEditableAreaNode(double x, double y)
    {
        if (!_areaDragging || _areaDraft is null || _areaNode < 0) return;
        var point = SnapPlacementPoint(Math.Clamp(x, 0, Session.Document.Canvas.Width), Math.Clamp(y, 0, Session.Document.Canvas.Height));
        var node = _areaDraft.Nodes[_areaNode];
        _areaDraft = _areaDraft with { Nodes = _areaDraft.Nodes.SetItem(_areaNode, node with { X = point.X, Y = point.Y }) };
        UpdateAreaPreview();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }
    public bool EndEditableAreaNodeDrag()
    {
        if (!_areaDragging || _areaDraft is not { } draft) return false;
        var changed = _areaOriginal is { } original && !original.Nodes.SequenceEqual(draft.Nodes);
        CancelEditableAreaGesture();
        return changed && CommitAreaChange(draft, "Переместить вершину области");
    }

    public void InsertEditableAreaNode()
    { _areaInsert = true; StatusDetail = "Щёлкните по сегменту области, чтобы добавить вершину."; }
    public void PickEditableAreaObject()
    { _areaPickObject = true; StatusDetail = "Щёлкните по нужному штампу, затем нажмите «Отделить выбранный объект»."; }
    public void DeleteEditableAreaNode()
    {
        if (_areaCreating && _areaDraft is { Nodes.Length: > 0 } draft)
        {
            _areaDraft = draft with { Nodes = draft.Nodes.RemoveAt(draft.Nodes.Length - 1) };
            _areaNode = _areaDraft.Nodes.Length - 1; UpdateAreaPreview(); RaiseAreaState(); return;
        }
        if (SelectedEditableArea is not { } path || _areaNode < 0 || _areaNode >= path.Nodes.Length) return;
        if (path.Nodes.Length <= 3) { StatusDetail = "Оставьте три вершины или удалите всю область."; return; }
        var next = path with { Nodes = path.Nodes.RemoveAt(_areaNode) };
        _areaNode = Math.Min(_areaNode, next.Nodes.Length - 1); CommitAreaChange(next, "Удалить вершину области");
    }
    public void SetEditableAreaDensity(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value >= int.MaxValue || UsesNaturalAreaLayout && value > 100)
        { StatusDetail = UsesNaturalAreaLayout ? "Плотность области — от 0 до 100%." : "Плотность — целое число от 0 объектов на 10 000 ед²."; return; }
        if (UsesNaturalAreaLayout) _naturalAreaDensity = Math.Round(value);
        else _stampToolSettings = _stampToolSettings with { Density = Math.Round(value) };
        if (_areaCreating && _areaDraft is { } draft)
        { _areaDraft = draft with { Density = Math.Round(value) }; UpdateAreaPreview(); RaiseAreaState(); }
        else if (SelectedEditableArea is { } p) CommitAreaChange(p with { Density = Math.Round(value) }, "Изменить плотность области");
        RaiseStampToolState();
    }
    public void SetEditableAreaSizeVariation(double value)
    {
        if (!UsesNaturalAreaLayout) return;
        if (!double.IsFinite(value) || value is < 0 or > 50)
        { StatusDetail = "Разброс размера — от 0 до 50%. Ноль оставляет одинаковый размер."; return; }
        _naturalAreaSizeVariation = Math.Round(value);
        if (_areaCreating && _areaDraft is { } draft)
        { _areaDraft = draft with { SizeVariationPercent = _naturalAreaSizeVariation }; UpdateAreaPreview(); RaiseAreaState(); }
        else if (SelectedEditableArea is { } p)
            CommitAreaChange(p with { SizeVariationPercent = _naturalAreaSizeVariation }, "Изменить разнообразие размеров области");
        else RaiseAreaState();
    }
    public void SetEditableAreaPattern(PlacementAreaPattern value)
    {
        if (!UsesNaturalAreaLayout || !Enum.IsDefined(value)) return;
        _naturalAreaPattern = value;
        if (_areaCreating && _areaDraft is { } draft)
        { _areaDraft = draft with { Pattern = value }; UpdateAreaPreview(); RaiseAreaState(); }
        else if (SelectedEditableArea is { } area)
            CommitAreaChange(area with { Pattern = value }, "Изменить рисунок области");
        else RaiseAreaState();
    }
    public void SetEditableAreaSeed(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue) return;
        if (_areaCreating && _areaDraft is { } draft)
        { _areaDraft = draft with { Seed = (int)Math.Round(value) }; UpdateAreaPreview(); RaiseAreaState(); }
        else if (SelectedEditableArea is { } p) CommitAreaChange(p with { Seed = (int)Math.Round(value) }, "Изменить seed области");
    }
    public void ApplyStampSettingsToEditableArea()
    {
        if (SelectedEditableArea is not { } p) return;
        var settings = _stampToolSettings;
        var width = p.Request.BaseWidth * settings.ScalePercent / p.ScalePercent;
        CommitAreaChange(p with { Density = p.LayoutVersion >= 3 ? p.Density : settings.Density,
            ScalePercent = settings.ScalePercent, RotationDegrees = settings.RotationDegrees,
            Sublayer = settings.Sublayer, Opacity = settings.ObjectOpacity, Style = settings.Style ?? p.Style,
            Request = p.Request with { BaseWidth = width, RandomizeVariant = settings.RandomizeVariant,
                RandomAngleStep = settings.RandomAngleStep, RandomFlip = settings.RandomFlip } }, "Изменить параметры области");
    }
    public void ArrangeEditableAreaNaturally()
    {
        if (SelectedEditableArea is not { LayoutVersion: < 3 } p) return;
        if (CommitAreaChange(PlacementAreaPlanner.InitializeComposition(p with { LayoutVersion = 3, OccupiedSlots = [], Density = _naturalAreaDensity,
            SizeVariationPercent = _naturalAreaSizeVariation, Pattern = _naturalAreaPattern,
            ContentBounds = ReadAreaContentBounds(p.Variants, p.ContentBounds) }), "Расположить область естественно"))
            StatusDetail = "Объекты собраны в естественные группы без наложений. Отделённые объекты сохранены; действие можно отменить.";
    }
    public void TryAnotherEditableAreaArrangement()
    {
        if (DisplayedEditableArea is not { } p) return;
        SetEditableAreaSeed(unchecked(p.Seed + 104729));
    }
    public void DetachSelectedAreaObject()
    {
        if (Session.SelectedObjectId is not { } id || PlacementAreaOwnership.Owner(Session.Document, id) is not { } path) return;
        var layer = EditableAreaLayer(path); if (layer is null) return;
        if (!RunAreaCommand(new(layer.Id, path.Id, path, "Отделить объект от области", detach: id))) return;
        _selectedAreaId = path.Id; RaiseAreaState(); StatusDetail = "Объект отделён. Его место исключено из будущих пересчётов области.";
    }
    public void BakeEditableArea()
    {
        if (SelectedEditableArea is not { } path || EditableAreaLayer(path) is not { } layer) return;
        if (RunAreaCommand(new(layer.Id, path.Id, null, "Превратить область в обычные объекты", bake: true)))
        { _selectedAreaId = null; RaiseAreaState(); }
    }
    public void DeleteEditableArea()
    {
        if (SelectedEditableArea is not { } path || EditableAreaLayer(path) is not { } layer) return;
        if (RunAreaCommand(new(layer.Id, path.Id, null, "Удалить область и связанные объекты")))
        { _selectedAreaId = null; RaiseAreaState(); }
    }

    private ObjectLayer? EditableAreaLayer(PlacementArea path)
    {
        var layer = Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.PlacementAreas.Any(p => p.Id == path.Id));
        if (layer is not { Visible: true, Locked: false } || layer.StateFor(path.Sublayer) is not { Visible: true, Locked: false })
        { StatusDetail = "Слой области скрыт или заблокирован."; return null; }
        return layer;
    }
    private bool CommitAreaChange(PlacementArea next, string name)
    {
        if (EditableAreaLayer(next) is not { } layer) return false;
        if (next == SelectedEditableArea) return false;
        return RunAreaCommand(new(layer.Id, next.Id, next, name));
    }
    private bool RunAreaCommand(PlacementAreaCommand command)
    {
        var revision = Session.Revision; Session.Execute(command);
        if (revision == Session.Revision) return false;
        LastAreaChangedObjects = command.ChangedObjects; LastAreaVisitedCells = command.VisitedCells;
        LastAreaChangedCells = command.ChangedCells;
        AfterEdit(geometryOnly: true); RaiseAreaState(); return true;
    }
    public bool CancelEditableAreaGesture()
    {
        CancelAreaCount();
        var active = _areaDraft is not null || _areaInsert || _areaPickObject || _areaStartRequested;
        _areaDraft = _areaOriginal = null; _areaAssets = []; _areaPreviewObjects = []; _areaOverrides = [];
        _areaPreviewError = null; _areaPreviewInvalid = false; _areaStartRequested = false;
        _areaCreating = _areaDragging = _areaInsert = false; _areaLayer = null;
        _areaPickObject = false;
        if (active) { RaiseAreaState(); InvalidateScene(EditorChangeReason.CanvasInvalidation); }
        return active;
    }
    private void UpdateAreaPreview()
    {
        CancelAreaCount();
        if (_areaDraft is not { Nodes.Length: >= 3 } path)
        { _areaPreviewObjects = []; _areaOverrides = []; _areaPreviewError = null; _areaPreviewInvalid = false; return; }
        try
        {
            if (_areaCreating && path.LayoutVersion >= 3)
                _areaDraft = path = PlacementAreaPlanner.InitializeComposition(path);
            var obstacles = path.LayoutVersion > 0 && _areaLayer is { } layerId
                ? new PlacementAreaObstacles(Session.Document, layerId, path.Id, path.Sublayer, path.LayoutVersion, path) : null;
            var samples = PlacementAreaPlanner.Preview(path, _areaDragging ? _areaOriginal : null, obstacles: obstacles, memory: _workingMemory);
            _areaPreviewError = null; _areaPreviewInvalid = false;
            if (!_areaDragging)
            {
                _areaPreviewObjects = samples;
                if (_areaCreating)
                {
                    var cancellation = _areaCountCancellation = new CancellationTokenSource();
                    _areaCountTask = CountAreaDraftAsync(_areaCountTask, path, obstacles, cancellation);
                }
                return;
            }
            var index = DocumentRevisionIndex.For(Session.Document);
            _areaOverrides = samples.Where(o => index.FindObject(o.Id) is not null).ToArray();
            _areaPreviewObjects = samples.Where(o => index.FindObject(o.Id) is null).ToArray();
            // Bounded deletion samples show the changed edge even while the polygon is shrinking.
            if (_areaOriginal is { } original)
            {
                var removed = PlacementAreaPlanner.Preview(original, path, memory: _workingMemory).Where(o =>
                    !PlacementAreaGeometry.Contains(path.Nodes, o.X + o.Width / 2, o.Y + o.Height / 2))
                    .Select(o => o with { ObjectOpacity = 0 });
                _areaOverrides = _areaOverrides.Concat(removed).Take(128).ToArray();
            }
        }
        catch (ArgumentException ex)
        { _areaPreviewObjects = []; _areaOverrides = []; _areaPreviewInvalid = true; _areaPreviewError = ex.Message; StatusDetail = ex.Message; return; }
        catch (InvalidOperationException ex)
        {
            _areaPreviewObjects = []; _areaOverrides = []; _areaPreviewInvalid = false;
            _areaPreviewError = ex.Message + (_areaCreating ? " Нажмите «Разместить», чтобы повторить попытку." : "");
            StatusDetail = _areaPreviewError; return;
        }
    }

    private void CancelAreaCount()
    {
        _areaCountCancellation?.Cancel(); _areaCountCancellation = null;
        _areaPredictedCount = null; _areaCountError = null;
    }
    private async Task CountAreaDraftAsync(Task previous, PlacementArea draft, PlacementAreaObstacles? obstacles,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            // Debounce input and serialize counters. Moving vertices never queues simultaneous
            // full-map counters or publishes the result of an obsolete seed/shape/density.
            await Task.Delay(150, token); await previous; token.ThrowIfCancellationRequested();
            var count = await Task.Run(() => PlacementAreaPlanner.Count(draft, obstacles, _workingMemory, token), token);
            if (!token.IsCancellationRequested && ReferenceEquals(_areaDraft, draft))
            { _areaPredictedCount = count; Raise(nameof(PlannedEditableAreaCount)); Raise(nameof(EditableAreaResult)); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(_areaDraft, draft))
            { _areaCountError = "Подсчёт недоступен"; Raise(nameof(EditableAreaResult)); }
        }
        finally
        {
            if (ReferenceEquals(_areaCountCancellation, cancellation)) _areaCountCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RefreshAreaDraftSettings()
    {
        if (!_areaCreating || _areaDraft is not { } draft) return;
        var settings = _stampToolSettings;
        _areaDraft = draft with { Density = draft.LayoutVersion >= 3 ? draft.Density : settings.Density, ScalePercent = settings.ScalePercent,
            RotationDegrees = settings.RotationDegrees, Sublayer = settings.Sublayer, Opacity = settings.ObjectOpacity,
            Style = settings.Style ?? draft.Style,
            Request = draft.Request with { BaseWidth = draft.Request.BaseWidth * settings.ScalePercent / draft.ScalePercent,
                RandomizeVariant = settings.RandomizeVariant, RandomAngleStep = settings.RandomAngleStep, RandomFlip = settings.RandomFlip } };
        UpdateAreaPreview(); RaiseAreaState();
    }
    private void RaiseAreaState()
    {
        Raise(nameof(EditableAreas)); Raise(nameof(SelectedEditableArea)); Raise(nameof(EditableAreaSummary));
        Raise(nameof(IsCreatingEditableArea)); Raise(nameof(CanDetachAreaObject));
        Raise(nameof(CanPlaceEditableArea)); Raise(nameof(EditableAreaPlacementHint));
        Raise(nameof(UsesNaturalAreaLayout)); Raise(nameof(EditableAreaDensity)); Raise(nameof(EditableAreaSizeVariation));
        Raise(nameof(PlannedEditableAreaCount)); Raise(nameof(EditableAreaResult));
        Raise(nameof(EditableAreaPattern)); Raise(nameof(EditableAreaPatternHint));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }
    private ImmutableDictionary<string, StampContentBounds> ReadAreaContentBounds(IEnumerable<StampVariantSource> variants,
        ImmutableDictionary<string, StampContentBounds>? saved = null)
    {
        var result = (saved ?? ImmutableDictionary<string, StampContentBounds>.Empty).ToBuilder();
        foreach (var source in variants)
        {
            if (result.ContainsKey(source.Sha256)) continue;
            if (!_areaContentBoundsCache.TryGetValue(source.Sha256, out var bounds))
            {
                if (StampImages?.TryGetImage(source.Sha256) is not { } image) continue;
                bounds = StampContentMeasurer.Measure(image);
                // This small scalar cache is not the image cache and never grows with stamp count.
                if (_areaContentBoundsCache.Count >= 256) _areaContentBoundsCache.Clear();
                _areaContentBoundsCache[source.Sha256] = bounds;
            }
            result[source.Sha256] = bounds;
        }
        return result.ToImmutable();
    }
    private string NextAreaName(string assetName)
    {
        var names = EditableAreas.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        { var name = $"Область {n} · {assetName}"; if (!names.Contains(name)) return name; }
    }
    private static int ClosestAreaSegment(PlacementArea path, double x, double y, double tolerance)
    {
        var best = tolerance * tolerance; var found = -1;
        for (var i = 0; i < path.Nodes.Length; i++)
        {
            var a = path.Nodes[i]; var b = path.Nodes[(i + 1) % path.Nodes.Length]; var dx = b.X - a.X; var dy = b.Y - a.Y;
            var length = dx * dx + dy * dy;
            var t = length == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
            var distance = Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2);
            if (distance <= best) { best = distance; found = i; }
        }
        return found;
    }
}
