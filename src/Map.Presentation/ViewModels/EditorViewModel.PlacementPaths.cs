using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private Guid? _selectedPathId;
    private PlacementPath? _pathDraft, _pathOriginal;
    private LayerId? _pathLayer;
    private IReadOnlyList<MapAssetReference> _pathAssets = [];
    private int _pathNode = -1;
    private bool _pathCreating, _pathDragging, _pathInsert;
    private bool _pathPickObject;
    private IReadOnlyList<SceneObject> _pathPreviewObjects = [];
    private IReadOnlyList<SceneObject> _pathOverrides = [];
    private long _pathPreviewTotal;
    public IReadOnlyList<SceneObject>? EditablePathPreviewOverrides => _pathDragging ? _pathOverrides : null;

    public IReadOnlyList<PlacementPath> EditablePaths => Session.Document.Layers.OfType<ObjectLayer>()
        .SelectMany(l => l.PlacementPaths).ToArray();
    public PlacementPath? SelectedEditablePath => Session.Document.Layers.OfType<ObjectLayer>()
        .SelectMany(l => l.PlacementPaths).FirstOrDefault(p => p.Id == _selectedPathId);
    public PlacementPath? DisplayedEditablePath
    {
        get
        {
            if (_pathDraft is not null) return _pathDraft;
            var selected = SelectedEditablePath;
            return selected is not null && Session.Document.Layers.OfType<ObjectLayer>()
                .Any(l => l.Visible && l.StateFor(selected.Sublayer).Visible && l.PlacementPaths.Contains(selected))
                ? selected : null;
        }
    }
    public bool IsCreatingEditablePath => _pathCreating;
    public bool HasPathGesture => _pathDraft is not null;
    public int SelectedPathNodeIndex => _pathNode;
    public int LastPathChangedObjects { get; private set; }
    public int LastPathRegeneratedSegments { get; private set; }
    public bool CanDetachPathObject => Session.SelectedObjectId is { } id
        && PlacementPathOwnership.Owner(Session.Document, id) is not null;
    public string EditablePathSummary => DisplayedEditablePath is { } p
        ? _pathCreating
            ? $"{p.Name} · узлов: {p.Nodes.Length} · после завершения: {_pathPreviewTotal} объектов. Предпросмотр: {_pathPreviewObjects.Count} из {_pathPreviewTotal}."
            : $"{p.Name} · узлов: {p.Nodes.Length} · объектов: {p.Members.Length} · исключений: {p.Exclusions.Count}"
        : "Выберите изображение или набор, затем создайте путь кликами по холсту.";

    public void StartEditablePath()
    {
        CancelEditablePathGesture(); _selectedPathId = null; _pathNode = -1;
        ActiveTool = EditorTool.Stamp; StampPlacementMode = StampPlacementMode.Path;
        ArmStampPlacement();
        StatusDetail = "Путь: ставьте узлы кликами; «Завершить путь» или Enter — сохранить, Esc — отменить.";
        RaisePathState();
    }

    public bool AddEditablePathNode(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
            || x > Session.Document.Canvas.Width || y > Session.Document.Canvas.Height) return false;
        if (!_pathCreating)
        {
            var settings = _stampToolSettings with { Mode = StampPlacementMode.Path };
            if (!TryStampInputs(settings with { RandomizeVariant = true }, out var request, out var sources, out var records, out var refusal))
            { StatusDetail = refusal; return false; }
            sources = sources.OrderByDescending(s => s.Sha256 == CurrentVariant?.Sha256).ToArray();
            request = request with { RandomizeVariant = settings.RandomizeVariant };
            // Freeze every allowed variant now, including ones this short row may not initially use.
            var variantPlan = sources.Select(s => new StampPlacementItem(s.Sha256, 0, 0, 1, 1, 0, false, false, s.MaskSha256)).ToArray();
            if (ResolvePlan(variantPlan, records, out var references, out refusal) is null)
            { StatusDetail = refusal; return false; }
            _pathAssets = references; _pathLayer = ActiveObjectLayer()?.Id;
            _pathDraft = new PlacementPath
            {
                Name = "Путь: " + (SelectedSet?.Name ?? "штампы"), AssetSetId = _selectedSetId,
                VariantId = CurrentVariant?.Id, Variants = [.. sources], Request = request,
                Seed = NextGestureSeed(), Spacing = Math.Max(1, Math.Round(request.BaseWidth * settings.SeriesSpacingPercent / 100)),
                RotationDegrees = settings.RotationDegrees, ScalePercent = settings.ScalePercent,
                Sublayer = settings.Sublayer, Opacity = settings.ObjectOpacity,
                Style = settings.Style ?? new StampStyle(sources[0].Sha256),
            };
            _pathCreating = true; PendingPlacement = null;
        }
        var point = SnapPlacementPoint(x, y);
        if (_pathDraft!.Nodes.LastOrDefault() is { } last && last.X == point.X && last.Y == point.Y) return false;
        _pathDraft = _pathDraft with { Nodes = _pathDraft.Nodes.Add(new(Guid.NewGuid(), point.X, point.Y)) };
        _pathNode = _pathDraft.Nodes.Length - 1;
        UpdatePathPreview(); RaisePathState(); return true;
    }

    public bool FinishEditablePath()
    {
        if (!_pathCreating || _pathDraft is not { Nodes.Length: >= 2 } draft)
        { StatusDetail = "Для пути нужны хотя бы два разных узла."; return false; }
        var (layer, create) = EnsureObjectLayer();
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return false; }
        var command = new PlacementPathCommand(layer.Id, draft.Id, draft, "Создать редактируемый путь", _pathAssets);
        var revision = Session.Revision;
        ExecuteWithOptionalLayer(command, create, command.Name);
        if (Session.Revision == revision) return false;
        _selectedPathId = draft.Id; _activeLayerId = layer.Id; _gestureCounter++;
        CancelEditablePathGesture(); PendingPlacement = null; Session.ClearSelection();
        LastPathChangedObjects = command.ChangedObjects; LastPathRegeneratedSegments = command.RegeneratedSegments;
        AfterEdit(); RaisePathState();
        StatusDetail = "Путь создан. Перетаскивайте узлы; шаг и seed доступны ниже. Для ручной правки отделите штамп.";
        return true;
    }

    public void SelectEditablePath(Guid id)
    {
        CancelPlacement(); CancelEditablePathGesture();
        var layer = Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.PlacementPaths.Any(p => p.Id == id));
        if (layer is null) return;
        ActiveTool = EditorTool.Stamp; _selectedPathId = id; _activeLayerId = layer.Id;
        _pathNode = 0; PendingPlacement = null; Session.ClearSelection(); SyncEditablePathSettings(); RaisePathState();
    }

    private void SyncEditablePathSettings()
    {
        if (SelectedEditablePath is not { } path) return;
        _stampToolSettings = _stampToolSettings with { Mode = StampPlacementMode.Path,
            ScalePercent = path.ScalePercent, RotationDegrees = path.RotationDegrees, Sublayer = path.Sublayer,
            ObjectOpacity = path.Opacity, Style = path.Style, RandomizeVariant = path.Request.RandomizeVariant,
            RandomAngleStep = path.Request.RandomAngleStep, RandomFlip = path.Request.RandomFlip };
        RaiseStampToolState();
    }

    public bool PressEditablePath(double x, double y)
    {
        if (ActiveTool != EditorTool.Stamp) return false;
        if (_pathPickObject) { _pathPickObject = false; return false; }
        if (_pathCreating || PendingPlacement is { Mode: StampPlacementMode.Path })
        { AddEditablePathNode(x, y); return true; }
        var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
        if (SelectedEditablePath is { } selected && EditablePathLayer(selected) is not null)
        {
            if (_pathInsert)
            {
                var segment = ClosestPathSegment(selected, x, y, tolerance * 2);
                if (segment >= 0)
                {
                    _pathInsert = false; var point = SnapPlacementPoint(x, y);
                    _pathNode = segment + 1;
                    return CommitPathChange(selected with { Nodes = selected.Nodes.Insert(_pathNode,
                        new(Guid.NewGuid(), point.X, point.Y)) }, "Добавить узел пути");
                }
            }
            for (var i = 0; i < selected.Nodes.Length; i++)
                if (Math.Abs(selected.Nodes[i].X - x) <= tolerance && Math.Abs(selected.Nodes[i].Y - y) <= tolerance)
                {
                    _pathNode = i; _pathOriginal = selected; _pathDraft = selected; _pathDragging = true;
                    _pathLayer = EditablePathLayer(selected)!.Id; RaisePathState(); return true;
                }
        }
        if (ActiveTool == EditorTool.Stamp && StampPlacementMode == StampPlacementMode.Path)
            foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>().Reverse().Where(l => l.Visible))
                foreach (var path in layer.PlacementPaths.Reverse())
                    if (layer.StateFor(path.Sublayer).Visible && ClosestPathSegment(path, x, y, tolerance) >= 0)
                    { SelectEditablePath(path.Id); return true; }
        return false;
    }
    public bool IsDraggingPathNode => _pathDragging;
    public void MoveEditablePathNode(double x, double y)
    {
        if (!_pathDragging || _pathDraft is null || _pathNode < 0) return;
        var point = SnapPlacementPoint(Math.Clamp(x, 0, Session.Document.Canvas.Width), Math.Clamp(y, 0, Session.Document.Canvas.Height));
        var node = _pathDraft.Nodes[_pathNode];
        _pathDraft = _pathDraft with { Nodes = _pathDraft.Nodes.SetItem(_pathNode, node with { X = point.X, Y = point.Y }) };
        UpdatePathPreview();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }
    public bool EndEditablePathNodeDrag()
    {
        if (!_pathDragging || _pathDraft is not { } draft) return false;
        var changed = _pathOriginal is { } original && !original.Nodes.SequenceEqual(draft.Nodes);
        CancelEditablePathGesture();
        return changed && CommitPathChange(draft, "Переместить узел пути");
    }

    public void InsertEditablePathNode()
    { _pathInsert = true; StatusDetail = "Щёлкните по сегменту пути, чтобы добавить узел."; }
    public void PickEditablePathObject()
    { _pathPickObject = true; StatusDetail = "Щёлкните по нужному штампу, затем нажмите «Отделить выбранный объект»."; }
    public void DeleteEditablePathNode()
    {
        if (SelectedEditablePath is not { } path || _pathNode < 0 || _pathNode >= path.Nodes.Length) return;
        if (path.Nodes.Length <= 2) { StatusDetail = "Оставьте два узла или удалите весь путь."; return; }
        var next = path with { Nodes = path.Nodes.RemoveAt(_pathNode) };
        _pathNode = Math.Min(_pathNode, next.Nodes.Length - 1); CommitPathChange(next, "Удалить узел пути");
    }
    public void SetEditablePathSpacing(double value)
    {
        if (!double.IsFinite(value) || value < 1 || value > int.MaxValue) { StatusDetail = "Шаг должен быть целым числом от 1."; return; }
        if (SelectedEditablePath is { } p) CommitPathChange(p with { Spacing = Math.Round(value) }, "Изменить шаг пути");
    }
    public void SetEditablePathSeed(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue) return;
        if (SelectedEditablePath is { } p) CommitPathChange(p with { Seed = (int)Math.Round(value) }, "Изменить seed пути");
    }
    public void ApplyStampSettingsToEditablePath()
    {
        if (SelectedEditablePath is not { } p) return;
        var settings = _stampToolSettings;
        var width = p.Request.BaseWidth * settings.ScalePercent / p.ScalePercent;
        CommitPathChange(p with { ScalePercent = settings.ScalePercent, RotationDegrees = settings.RotationDegrees,
            Sublayer = settings.Sublayer, Opacity = settings.ObjectOpacity, Style = settings.Style ?? p.Style,
            Request = p.Request with { BaseWidth = width, RandomizeVariant = settings.RandomizeVariant,
                RandomAngleStep = settings.RandomAngleStep, RandomFlip = settings.RandomFlip } }, "Изменить параметры пути");
    }
    public void DetachSelectedPathObject()
    {
        if (Session.SelectedObjectId is not { } id || PlacementPathOwnership.Owner(Session.Document, id) is not { } path) return;
        var layer = EditablePathLayer(path); if (layer is null) return;
        if (!RunPathCommand(new(layer.Id, path.Id, path, "Отделить объект от пути", detach: id))) return;
        _selectedPathId = path.Id; RaisePathState(); StatusDetail = "Объект отделён. Его место исключено из будущих пересчётов пути.";
    }
    public void BakeEditablePath()
    {
        if (SelectedEditablePath is not { } path || EditablePathLayer(path) is not { } layer) return;
        if (RunPathCommand(new(layer.Id, path.Id, null, "Превратить путь в обычные объекты", bake: true)))
        { _selectedPathId = null; RaisePathState(); }
    }
    public void DeleteEditablePath()
    {
        if (SelectedEditablePath is not { } path || EditablePathLayer(path) is not { } layer) return;
        if (RunPathCommand(new(layer.Id, path.Id, null, "Удалить путь и связанные объекты")))
        { _selectedPathId = null; RaisePathState(); }
    }

    private ObjectLayer? EditablePathLayer(PlacementPath path)
    {
        var layer = Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.PlacementPaths.Any(p => p.Id == path.Id));
        if (layer is not { Visible: true, Locked: false } || layer.StateFor(path.Sublayer) is not { Visible: true, Locked: false })
        { StatusDetail = "Слой пути скрыт или заблокирован."; return null; }
        return layer;
    }
    private bool CommitPathChange(PlacementPath next, string name)
    {
        if (EditablePathLayer(next) is not { } layer) return false;
        if (next == SelectedEditablePath) return false;
        return RunPathCommand(new(layer.Id, next.Id, next, name));
    }
    private bool RunPathCommand(PlacementPathCommand command)
    {
        var revision = Session.Revision; Session.Execute(command);
        if (revision == Session.Revision) return false;
        LastPathChangedObjects = command.ChangedObjects; LastPathRegeneratedSegments = command.RegeneratedSegments;
        AfterEdit(geometryOnly: true); RaisePathState(); return true;
    }
    public bool CancelEditablePathGesture()
    {
        var active = _pathDraft is not null || _pathInsert || _pathPickObject;
        _pathDraft = _pathOriginal = null; _pathAssets = []; _pathPreviewObjects = []; _pathOverrides = [];
        _pathPreviewTotal = 0;
        _pathCreating = _pathDragging = _pathInsert = false; _pathLayer = null;
        _pathPickObject = false;
        if (active) { RaisePathState(); InvalidateScene(EditorChangeReason.CanvasInvalidation); }
        return active;
    }
    private void UpdatePathPreview()
    {
        if (_pathDraft is not { Nodes.Length: >= 2 } path)
        { _pathPreviewObjects = []; _pathOverrides = []; _pathPreviewTotal = 0; return; }
        _pathPreviewTotal = PlacementPathPlanner.Count(path);
        var samples = PlacementPathPlanner.Preview(path, segments: _pathDragging ? new HashSet<int> { _pathNode - 1, _pathNode } : null);
        if (!_pathDragging) { _pathPreviewObjects = samples; return; }
        var index = DocumentRevisionIndex.For(Session.Document);
        _pathOverrides = samples.Where(o => index.FindObject(o.Id) is not null).ToArray();
        _pathPreviewObjects = samples.Where(o => index.FindObject(o.Id) is null).ToArray();
    }
    private void RaisePathState()
    {
        Raise(nameof(EditablePaths)); Raise(nameof(SelectedEditablePath)); Raise(nameof(EditablePathSummary));
        Raise(nameof(IsCreatingEditablePath)); Raise(nameof(CanDetachPathObject));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }
    private static int ClosestPathSegment(PlacementPath path, double x, double y, double tolerance)
    {
        var best = tolerance * tolerance; var found = -1;
        for (var i = 0; i < path.Nodes.Length - 1; i++)
        {
            var a = path.Nodes[i]; var b = path.Nodes[i + 1]; var dx = b.X - a.X; var dy = b.Y - a.Y;
            var length = dx * dx + dy * dy;
            var t = length == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1);
            var distance = Math.Pow(x - a.X - t * dx, 2) + Math.Pow(y - a.Y - t * dy, 2);
            if (distance <= best) { best = distance; found = i; }
        }
        return found;
    }
}
