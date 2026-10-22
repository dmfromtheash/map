using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>How the road tool takes new courses: node clicks or a freehand drag.</summary>
public enum RoadDrawMode { Nodes, Freehand }

/// <summary>What a Delete, a drag or a panel action addresses in a road network.</summary>
public enum RoadSelectionLevel
{
    /// <summary>The whole network: every branch and node of it.</summary>
    Road,

    /// <summary>One branch of the selected road.</summary>
    Branch,

    /// <summary>One node; a shared node moves every branch through it.</summary>
    Node,
}

/// <summary>Stage 20: roads as one editable network of shared nodes and branches.</summary>
public sealed partial class EditorViewModel
{
    private readonly List<Guid> _roadSelection = [];
    private RoadSelectionLevel _roadLevel = RoadSelectionLevel.Road;
    private Guid? _selectedBranchId;
    private int _roadVertex = -1;
    private Road? _roadDraft, _roadOriginal;
    private LayerId? _roadLayer;
    private Guid? _roadDraftBranchId;
    private bool _roadCreating, _roadDragging, _roadInsert, _roadStroking, _roadDraftHosted;
    private double _newRoadWidth = 10;
    private RoadDrawMode _roadDrawMode = RoadDrawMode.Nodes;
    private List<VectorPoint>? _roadStroke;
    private double _roadSimplifyTolerance = 6;
    private (double X, double Y)? _roadDragFrom;
    private Dictionary<Guid, Road>? _roadDragDrafts;
    private bool _roadDragMoved;

    public IReadOnlyList<Road> Roads => Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Roads).ToArray();

    /// <summary>The primary selected road: the last one selected.</summary>
    public Road? SelectedRoad => _roadSelection.Count > 0 ? Roads.FirstOrDefault(r => r.Id == _roadSelection[^1]) : null;
    public RoadBranch? SelectedRoadBranch => SelectedRoad?.FindBranch(_selectedBranchId ?? Guid.Empty);
    public IReadOnlyList<Guid> RoadSelectionIds => _roadSelection;
    public int RoadSelectionCount => _roadSelection.Count;

    public IReadOnlyList<Road> SelectedRoads
    {
        get
        {
            var all = Roads;
            var result = new List<Road>(_roadSelection.Count);
            foreach (var id in _roadSelection)
                if (all.FirstOrDefault(r => r.Id == id) is { } road) result.Add(road);
            return result;
        }
    }

    /// <summary>The road to draw handles for: a live draft during a gesture, otherwise the selection on a visible layer.</summary>
    public Road? DisplayedRoad
    {
        get
        {
            if (_roadDraft is not null) return _roadDraft;
            var selected = SelectedRoad;
            if (selected is null || RoadLayerOf(selected.Id) is not { Visible: true }) return null;
            if (_roadDragDrafts is { } drafts && drafts.TryGetValue(selected.Id, out var dragged)) return dragged;
            return selected.Visible ? selected : null;
        }
    }

    public Guid? DisplayedRoadBranchId => _roadDraftBranchId ?? _selectedBranchId;

    /// <summary>Live substitutions the canvas draws instead of the saved roads; the document is untouched.</summary>
    public IReadOnlyList<Road>? RoadRenderOverrides
    {
        get
        {
            IReadOnlyList<Road>? dragged = _roadDragDrafts is { Count: > 0 } drafts ? [.. drafts.Values] : null;
            if (_roadDraft is null) return dragged;
            if (dragged is null) return [_roadDraft];
            return [.. dragged.Where(r => r.Id != _roadDraft.Id), _roadDraft];
        }
    }

    public LayerId? RoadPreviewLayerId => _roadLayer;
    public bool IsCreatingRoad => _roadCreating;
    public bool HasRoadGesture => _roadDraft is not null;
    public bool IsDraggingRoad => _roadDragging || _roadStroking || IsDraggingRoads;
    public bool IsDraggingRoads => _roadDragFrom is not null;
    public int SelectedRoadVertexIndex => _roadVertex;

    public RoadDrawMode RoadDrawMode
    {
        get => _roadDrawMode;
        set { if (SetField(ref _roadDrawMode, value)) RaiseRoadState(); }
    }

    public double NewRoadWidth
    {
        get => _newRoadWidth;
        set { if (double.IsFinite(value)) SetField(ref _newRoadWidth, Math.Clamp(value, Road.MinimumWidth, 4096)); }
    }

    public double RoadSimplifyTolerance
    {
        get => _roadSimplifyTolerance;
        set { if (double.IsFinite(value)) SetField(ref _roadSimplifyTolerance, Math.Clamp(value, 0.5, 512)); }
    }

    public RoadSelectionLevel RoadLevel
    {
        get => _roadLevel;
        set
        {
            if (_roadLevel == value) return;
            _roadLevel = value;
            if (value != RoadSelectionLevel.Road && SelectedRoad is { } road && SelectedRoadBranch is null)
                _selectedBranchId = road.Branches[0].Id;
            if (value == RoadSelectionLevel.Node && _roadVertex < 0 && SelectedRoadBranch is { } branch && branch.Vertices.Length > 0)
                _roadVertex = 0;
            Raise(nameof(RoadLevel)); Raise(nameof(IsRoadNodeMode)); RaiseRoadState();
        }
    }

    public bool IsRoadNodeMode => _roadLevel == RoadSelectionLevel.Node;

    public string RoadSelectionSummary
    {
        get
        {
            var roads = SelectedRoads;
            if (roads.Count == 0) return "Дорога не выбрана.";
            if (roads.Count > 1) return "Выбрано дорог: " + roads.Count + ".";
            var road = roads[0];
            var branch = SelectedRoadBranch;
            return _roadLevel switch
            {
                RoadSelectionLevel.Node when branch is not null && _roadVertex >= 0 && _roadVertex < branch.Vertices.Length =>
                    $"«{road.Name}» · {branch.Name} · узел {_roadVertex + 1} из {branch.Vertices.Length}"
                    + (road.IsShared(branch.Vertices[_roadVertex].NodeId) ? " · общий узел" : ""),
                RoadSelectionLevel.Branch when branch is not null =>
                    $"«{road.Name}» · участок «{branch.Name}»" + (branch.Closed ? " · замкнут" : "") + (branch.Style is null ? "" : " · свой стиль"),
                _ => $"«{road.Name}» · участков: {road.Branches.Length}, узлов: {road.Nodes.Length}",
            };
        }
    }

    public string RoadSummary
    {
        get
        {
            if (_roadCreating)
            {
                var count = _roadDraft?.FindBranch(_roadDraftBranchId ?? Guid.Empty)?.Vertices.Length ?? 0;
                if (count == 0)
                    return _roadDrawMode == RoadDrawMode.Freehand
                        ? "Протяните маршрут по холсту. Начало на существующей дороге — ветвь от общего узла; конец на дороге — соединение."
                        : "Ставьте узлы кликами. Первый клик по дороге или её узлу — ветвь от общего узла; клик по дороге с узлами в черновике — соединение; клик по первому узлу — замкнуть; Enter — свободный конец.";
                return $"Узлов: {count}. Клик по дороге — соединить; клик по первому узлу — замкнуть маршрут; Enter — свободный конец; Esc — отменить.";
            }
            if (SelectedRoad is not { } road) return "Выберите дорогу на холсте или создайте новую.";
            var branch = SelectedRoadBranch;
            if (branch is null) return $"{road.Name} · участков: {road.Branches.Length}";
            var shared = branch.Vertices.Count(v => road.IsShared(v.NodeId));
            return $"{road.Name} · участков: {road.Branches.Length} · {branch.Name}: узлов {branch.Vertices.Length}, общих {shared}" + (branch.Closed ? " · замкнут" : "");
        }
    }

    /// <summary>Combined world bounds of the selected roads, for the selection frame on canvas.</summary>
    public WorldBounds? RoadSelectionFrame
    {
        get
        {
            WorldBounds? frame = null;
            foreach (var road in SelectedRoads)
            {
                var shown = _roadDragDrafts is { } drafts && drafts.TryGetValue(road.Id, out var draft) ? draft : road;
                var bounds = RoadGeometry.Evaluate(shown).Bounds;
                frame = frame is { } current
                    ? new WorldBounds(Math.Min(current.Left, bounds.Left), Math.Min(current.Top, bounds.Top),
                        Math.Max(current.Right, bounds.Right), Math.Max(current.Bottom, bounds.Bottom))
                    : bounds;
            }
            return frame;
        }
    }

    // ------------------------------------------------------------------ creation

    public void StartRoad()
    {
        CancelRoadGesture();
        ActiveTool = EditorTool.Road;
        _roadCreating = true; _roadVertex = -1;
        StatusDetail = _roadDrawMode == RoadDrawMode.Freehand
            ? "Дорога: протяните маршрут по холсту. Начало на дороге — ветвь, конец на дороге — соединение."
            : "Дорога: ставьте узлы кликами. Клик по дороге — ветвь или соединение, клик по первому узлу — замкнуть, Enter — свободный конец, Esc — отменить.";
        RaiseRoadState();
    }

    private RoadBranch? DraftBranch => _roadDraft?.FindBranch(_roadDraftBranchId ?? Guid.Empty);

    /// <summary>Where a click on an existing road lands: a node of it, or a point on a segment.</summary>
    private readonly record struct RoadTarget(ObjectLayer Layer, Road Road, Guid BranchId, Guid? NodeId, int Segment, double Fraction, VectorPoint Point);

    private bool BeginRoadDraft(VectorPoint point, RoadTarget? from)
    {
        var (layer, _) = EnsureObjectLayer();
        if (from is { } target) layer = target.Layer;
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return false; }
        _roadLayer = layer.Id;
        if (from is { } host)
        {
            var road = host.Road;
            var nodeId = host.NodeId;
            if (nodeId is null)
            {
                road = RoadEditor.InsertVertex(road, host.BranchId, host.Segment, host.Fraction, host.Point, out var inserted);
                nodeId = inserted;
            }
            road = RoadEditor.AddBranch(road, nodeId, [], null, _newRoadWidth, out var branchId, $"Участок {road.Branches.Length + 1}");
            _roadDraft = road; _roadDraftBranchId = branchId; _roadDraftHosted = true;
        }
        else
        {
            var road = RoadEditor.NewRoad([point], _newRoadWidth, Services.RegionalMapFactory.IsRegional(Session.Document)
                ? Services.RegionalMapFactory.RoadStyle : RoadStyle.Painted(), EntityNameGenerator.Road(Session.Document));
            _roadDraft = road; _roadDraftBranchId = road.Branches[0].Id; _roadDraftHosted = false;
        }
        _roadVertex = 0; RaiseRoadState(); return true;
    }

    public bool AddRoadNode(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
            || x > Session.Document.Canvas.Width || y > Session.Document.Canvas.Height) return false;
        var point = SnapPlacementPoint(x, y);
        var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
        if (_roadDraft is null)
        {
            var from = HitRoadTarget(x, y, tolerance, editableOnly: true);
            return BeginRoadDraft(from?.Point ?? point, from);
        }
        var draft = _roadDraft; var branch = DraftBranch!;
        var lastNode = draft.FindNode(branch.Vertices[^1].NodeId)!;
        if (lastNode.X == point.X && lastNode.Y == point.Y) return false;
        var node = new RoadNode(Guid.NewGuid(), point.X, point.Y);
        _roadDraft = RoadEditor.ReplaceBranch(draft with { Nodes = draft.Nodes.Add(node) },
            branch with { Vertices = branch.Vertices.Add(new RoadVertex(node.Id, _newRoadWidth)) });
        _roadVertex = branch.Vertices.Length;
        RaiseRoadState(); return true;
    }

    /// <summary>Enter: the drafted branch ends here as a free end and is committed.</summary>
    public bool FinishRoad()
    {
        if (!_roadCreating || _roadDraft is null || DraftBranch is not { } branch || branch.Vertices.Length < 2)
        { StatusDetail = "Для дороги нужны хотя бы два разных узла."; return false; }
        return CommitRoadDraft(null, close: false);
    }

    private bool CommitRoadDraft(RoadTarget? target, bool close)
    {
        var draft = _roadDraft!; var branchId = _roadDraftBranchId!.Value;
        var branch = draft.FindBranch(branchId)!;
        var (layer, create) = EnsureObjectLayer();
        if (_roadLayer is { } draftLayer && Session.Document.FindLayer(draftLayer) is ObjectLayer hostLayer) { layer = hostLayer; create = null; }
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return false; }
        Road requested; Guid? absorbed = null; string name;
        try
        {
            if (close)
            {
                requested = RoadEditor.SetClosed(draft, branchId, true);
                name = _roadDraftHosted ? "Добавить замкнутый участок" : "Создать замкнутую дорогу";
            }
            else if (target is { } into)
            {
                if (!into.Layer.Visible || into.Layer.Locked) { StatusDetail = "Слой дороги скрыт или заблокирован."; return false; }
                if (into.Road.Id == draft.Id)
                {
                    // Joining the road the draft already belongs to: the end shares that node.
                    var road = draft;
                    var nodeId = into.NodeId;
                    if (nodeId is null)
                    {
                        road = RoadEditor.InsertVertex(road, into.BranchId, into.Segment, into.Fraction, into.Point, out var inserted);
                        nodeId = inserted;
                    }
                    requested = RoadEditor.AppendVertex(road, branchId, nodeId!.Value, branch.Vertices[^1].Width);
                    name = "Соединить участок";
                }
                else
                {
                    var host = Roads.FirstOrDefault(r => r.Id == into.Road.Id) ?? into.Road;
                    // Joining is explicit about what it may merge. A branch of a network on one
                    // layer cannot flow into a network on another layer - the source would be left
                    // behind with its branches copied; and two networks of different levels do
                    // not merge silently into one level.
                    if (_roadDraftHosted && into.Layer.Id != layer.Id)
                    { StatusDetail = "Дороги лежат на разных слоях и не соединяются. Перенесите одну сеть на слой другой и повторите."; return false; }
                    if (_roadDraftHosted && host.Level != draft.Level)
                    { StatusDetail = $"Дороги разных уровней ({FormatSublayer(draft.Level)} и {FormatSublayer(host.Level)}) не соединяются. Выровняйте уровень и повторите."; return false; }
                    var nodeId = into.NodeId;
                    if (nodeId is null)
                    {
                        host = RoadEditor.InsertVertex(host, into.BranchId, into.Segment, into.Fraction, into.Point, out var inserted);
                        nodeId = inserted;
                    }
                    var source = _roadDraftHosted ? draft : draft with { Style = host.Style };
                    var merged = RoadEditor.Absorb(host, source);
                    requested = RoadEditor.AppendVertex(merged, branchId, nodeId!.Value, branch.Vertices[^1].Width);
                    if (_roadDraftHosted) absorbed = draft.Id;
                    name = _roadDraftHosted ? "Соединить дороги" : "Присоединить участок";
                    layer = into.Layer; create = null;
                }
            }
            else
            {
                requested = draft;
                name = _roadDraftHosted ? "Добавить участок" : "Создать дорогу";
            }
            requested.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { StatusDetail = ex.Message; return false; }
        var command = new RoadCommand(layer.Id, requested.Id, requested, name, absorbed);
        var revision = Session.Revision;
        ExecuteWithOptionalLayer(command, create, name);
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        CancelRoadGesture();
        _roadSelection.Clear(); _roadSelection.Add(requested.Id); _selectedBranchId = branchId;
        _roadVertex = _roadLevel == RoadSelectionLevel.Node ? branch.Vertices.Length - 1 : -1;
        _activeLayerId = layer.Id;
        Session.ClearSelection(); ClearWaterSelection(); ClearLandSelection();
        AfterEdit(); RaiseRoadState();
        StatusDetail = target is not null
            ? "Участок присоединён через общий узел. Перетаскивайте узел — обе ветви следуют за ним."
            : close ? "Маршрут замкнут."
            : _roadDraftHosted ? "Участок добавлен к дороге." : "Дорога создана и выбрана. Перетаскивайте узлы, задайте ширину участка или узла слева.";
        return true;
    }

    // ------------------------------------------------------------------ freehand stroke

    public bool BeginRoadStroke(double x, double y)
    {
        if (ActiveTool != EditorTool.Road || !_roadCreating || _roadDrawMode != RoadDrawMode.Freehand || _roadDraft is not null) return false;
        if (!AddRoadNode(x, y)) return false;
        _roadStroking = true; _roadStroke = [new VectorPoint(x, y)];
        return true;
    }

    public void ContinueRoadStroke(double x, double y)
    {
        if (!_roadStroking || _roadStroke is null || _roadDraft is null || DraftBranch is not { } branch) return;
        var point = new VectorPoint(Math.Clamp(x, 0, Session.Document.Canvas.Width), Math.Clamp(y, 0, Session.Document.Canvas.Height));
        var last = _roadStroke[^1];
        if (Math.Abs(last.X - point.X) + Math.Abs(last.Y - point.Y) < 2 / Math.Max(.01, Session.Viewport.Scale)) return;
        _roadStroke.Add(point);
        // The gesture never enters the document as raw points: the branch is rebuilt from a
        // simplified course each time, the first vertex (possibly a shared node) staying put.
        var simplified = RiverGeometry.Simplify(_roadStroke, 6 / Math.Max(.01, Session.Viewport.Scale));
        var first = branch.Vertices[0];
        var stale = branch.Vertices.Skip(1).Select(v => v.NodeId).ToHashSet();
        var nodes = _roadDraft.Nodes.Where(n => !stale.Contains(n.Id)).ToList();
        var vertices = new List<RoadVertex> { first };
        foreach (var p in simplified.Skip(1))
        {
            var node = new RoadNode(Guid.NewGuid(), p.X, p.Y);
            nodes.Add(node); vertices.Add(new RoadVertex(node.Id, _newRoadWidth));
        }
        _roadDraft = RoadEditor.ReplaceBranch(_roadDraft with { Nodes = [.. nodes] }, branch with { Vertices = [.. vertices] });
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool EndRoadStroke()
    {
        if (!_roadStroking || _roadStroke is not { Count: > 0 } stroke || DraftBranch is not { } branch) { _roadStroking = false; return false; }
        _roadStroking = false;
        var end = stroke[^1];
        if (branch.Vertices.Length < 2) { CancelRoadGesture(); StatusDetail = "Слишком короткий штрих."; return false; }
        var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
        var target = HitRoadTarget(end.X, end.Y, tolerance, editableOnly: true, excludeBranch: branch.Id);
        if (target is { } into && !(into.Road.Id == _roadDraft!.Id && into.NodeId == branch.Vertices[^2].NodeId))
            return CommitRoadDraft(into, close: false);
        return FinishRoad();
    }

    // ------------------------------------------------------------------ canvas

    public bool PressRoad(double x, double y)
    {
        if (ActiveTool != EditorTool.Road) return false;
        var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
        if (_roadCreating)
        {
            if (_roadDrawMode == RoadDrawMode.Freehand) return false;
            if (_roadDraft is not null && DraftBranch is { } branch && branch.Vertices.Length >= 1)
            {
                // The first node of the draft closes the route; any other road or node joins it.
                var firstNode = _roadDraft.FindNode(branch.Vertices[0].NodeId)!;
                if (branch.Vertices.Length >= 3 && Math.Abs(firstNode.X - x) <= tolerance && Math.Abs(firstNode.Y - y) <= tolerance
                    && !_roadDraftHosted)
                { CommitRoadDraft(null, close: true); return true; }
                var target = HitRoadTarget(x, y, tolerance, editableOnly: true, excludeBranch: branch.Id);
                if (target is { } into && !(into.Road.Id == _roadDraft.Id && into.NodeId == branch.Vertices[^1].NodeId))
                {
                    if (into.Road.Id == _roadDraft.Id && into.NodeId == branch.Vertices[0].NodeId && !_roadDraftHosted)
                    { CommitRoadDraft(null, close: branch.Vertices.Length >= 3); return true; }
                    CommitRoadDraft(into, close: false); return true;
                }
            }
            AddRoadNode(x, y); return true;
        }
        if (SelectedRoad is { Locked: false } selected && _roadLevel == RoadSelectionLevel.Node
            && RoadLayerOf(selected.Id) is { Visible: true, Locked: false } layer)
        {
            var geometry = RoadGeometry.Evaluate(selected);
            if (SelectedRoadBranch is { } branch && geometry.Branches.TryGetValue(branch.Id, out var g))
            {
                for (var i = 0; i < branch.Vertices.Length; i++)
                {
                    var node = selected.FindNode(branch.Vertices[i].NodeId)!;
                    if (Math.Abs(node.X - x) <= tolerance && Math.Abs(node.Y - y) <= tolerance)
                    { _roadVertex = i; _roadOriginal = selected; _roadDraft = selected; _roadDragging = true; _roadLayer = layer.Id; RaiseRoadState(); return true; }
                }
                if (_roadInsert && RoadGeometry.TryClosest(geometry, x, y, tolerance * 2, out var onBranch, out var segment, out var fraction, out _, out _, b => b.Id == branch.Id))
                {
                    _roadInsert = false;
                    var point = RoadGeometry.PointAt(geometry.Branches[onBranch], segment, fraction);
                    var next = RoadEditor.InsertVertex(selected, branch.Id, segment, fraction, point, out _);
                    _roadVertex = segment + 1;
                    return CommitRoad(next, "Добавить узел дороги");
                }
            }
            foreach (var other in selected.Branches)
                for (var i = 0; i < other.Vertices.Length; i++)
                {
                    var node = selected.FindNode(other.Vertices[i].NodeId)!;
                    if (Math.Abs(node.X - x) <= tolerance && Math.Abs(node.Y - y) <= tolerance)
                    {
                        _selectedBranchId = other.Id; _roadVertex = i; _roadOriginal = selected; _roadDraft = selected;
                        _roadDragging = true; _roadLayer = layer.Id; RaiseRoadState(); return true;
                    }
                }
        }
        if (SelectRoadAt(x, y, Session.Viewport.Scale, additive: false))
        {
            if (_roadLevel == RoadSelectionLevel.Node && SelectedRoad is { Locked: false } picked
                && RoadLayerOf(picked.Id) is { Visible: true, Locked: false } pickedLayer)
            { _roadOriginal = picked; _roadDraft = picked; _roadDragging = true; _roadLayer = pickedLayer.Id; }
            else if (SelectedRoad is { Locked: false } && BeginRoadDrag(x, y)) { /* the whole road follows the pointer */ }
            RaiseRoadState();
            return true;
        }
        return false;
    }

    public void MoveRoadPointer(double x, double y)
    {
        if (IsDraggingRoads) { UpdateRoadDrag(x, y); return; }
        if (_roadDraft is null || !_roadDragging || SelectedRoadBranch is not { } branch) return;
        var point = SnapPlacementPoint(Math.Clamp(x, 0, Session.Document.Canvas.Width), Math.Clamp(y, 0, Session.Document.Canvas.Height));
        if (_roadVertex < 0 || _roadVertex >= branch.Vertices.Length) return;
        var nodeId = branch.Vertices[_roadVertex].NodeId;
        var node = _roadDraft.FindNode(nodeId)!;
        if (node.X == point.X && node.Y == point.Y) return;
        _roadDraft = RoadEditor.MoveNode(_roadDraft, nodeId, point.X, point.Y);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool EndRoadDrag()
    {
        if (IsDraggingRoads) return EndRoadsDrag();
        if (_roadDraft is not { } draft || !_roadDragging) return false;
        var changed = _roadOriginal is { } original && !SameRoad(original, draft);
        var layer = _roadLayer;
        CancelRoadGesture();
        return changed && CommitRoad(draft, "Переместить узел дороги", layer);
    }

    public bool CancelRoadGesture()
    {
        CancelRoadDrag();
        var active = _roadDraft is not null || _roadInsert || _roadCreating;
        _roadDraft = _roadOriginal = null; _roadDraftBranchId = null; _roadStroke = null;
        _roadCreating = _roadDragging = _roadInsert = _roadStroking = _roadDraftHosted = false; _roadLayer = null;
        if (active) { RaiseRoadState(); InvalidateScene(EditorChangeReason.CanvasInvalidation); }
        return active;
    }

    // ------------------------------------------------------------------ hit testing and picking

    /// <summary>
    /// The topmost visible road under the point, by its drawn body, obeying terrain levels, layer
    /// masks and clipping. A hidden road and a hidden layer are skipped; a locked one is reported.
    /// </summary>
    public bool HitRoad(double x, double y, double tolerance, out ObjectLayer layer, out Road road, out Guid branchId)
    {
        var document = Session.Document;
        layer = null!; road = null!; branchId = Guid.Empty;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
            || x >= document.Canvas.Width || y >= document.Canvas.Height) return false;
        var coveringLand = TerrainOrder.LandAt(document, x, y);
        var layers = document.Layers.ToList();
        var candidates = document.Layers.OfType<ObjectLayer>()
            .Where(l => l.Visible && l.Opacity > 0 && TerrainOrder.MaskCoverageAt(l, x, y) > 0)
            .SelectMany(l => LayerSurfaceOrder.Resolve(l).Select((entry, order) => (Layer: l, Entry: entry, Order: order)))
            .Where(item => item.Entry.Kind == SurfaceKind.Road)
            .Select(item => (item.Layer, Road: item.Layer.Roads.First(r => r.Id == item.Entry.Id), item.Order))
            .OrderByDescending(item => item.Road.Level).ThenByDescending(item => layers.IndexOf(item.Layer)).ThenByDescending(item => item.Order);
        foreach (var (candidateLayer, candidate, _) in candidates)
        {
            if (!candidate.Visible || candidate.Style.Opacity <= 0
                || coveringLand is { } land && land.Level > candidate.Level
                || !WaterClippingVisibleAt(document, candidateLayer, x, y)) continue;
            var set = RoadGeometry.Evaluate(candidate);
            if (!set.Bounds.Intersects(new WorldBounds(x - tolerance, y - tolerance, x + tolerance, y + tolerance))) continue;
            if (RoadGeometry.TryClosest(set, x, y, tolerance, out var hit, out _, out _, out _, out _))
            { layer = candidateLayer; road = candidate; branchId = hit; return true; }
        }
        return false;
    }

    /// <summary>A click on an existing road for the road tool: the node under the cursor, else the segment point.</summary>
    private RoadTarget? HitRoadTarget(double x, double y, double tolerance, bool editableOnly, Guid? excludeBranch = null)
    {
        // The same visibility rule a plain click uses: a layer at 0 % opacity or masked out at
        // this point shows nothing here, so nothing of it can be hit or joined to.
        foreach (var candidateLayer in Session.Document.Layers.OfType<ObjectLayer>().Reverse()
                     .Where(l => l.Visible && l.Opacity > 0 && TerrainOrder.MaskCoverageAt(l, x, y) > 0))
        {
            if (editableOnly && candidateLayer.Locked) continue;
            var roads = candidateLayer.Roads.AsEnumerable();
            if (_roadDraft is not null && _roadLayer == candidateLayer.Id)
                roads = candidateLayer.Roads.Where(r => r.Id != _roadDraft.Id).Prepend(_roadDraft);
            foreach (var candidate in roads.Reverse())
            {
                if (!candidate.Visible || (editableOnly && candidate.Locked)) continue;
                var set = RoadGeometry.Evaluate(candidate);
                if (!set.Bounds.Intersects(new WorldBounds(x - tolerance, y - tolerance, x + tolerance, y + tolerance))) continue;
                foreach (var node in candidate.Nodes)
                {
                    if (Math.Abs(node.X - x) > tolerance || Math.Abs(node.Y - y) > tolerance) continue;
                    var owner = candidate.BranchesAt(node.Id).FirstOrDefault(b => b.Id != excludeBranch);
                    if (owner is null) continue;
                    return new RoadTarget(candidateLayer, candidate, owner.Id, node.Id, 0, 0, new VectorPoint(node.X, node.Y));
                }
                if (RoadGeometry.TryClosest(set, x, y, tolerance, out var branchId, out var segment, out var fraction, out _, out _,
                        b => b.Id != excludeBranch))
                {
                    var point = RoadGeometry.PointAt(set.Branches[branchId], segment, fraction);
                    return new RoadTarget(candidateLayer, candidate, branchId, null, segment, fraction, point);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// A plain click on a road: selects it (or extends the selection with Ctrl) and keeps the
    /// branch under the cursor active. Returns false when there is no road under the point.
    /// </summary>
    public bool SelectRoadAt(double worldX, double worldY, double viewScale, bool additive)
    {
        var tolerance = 6 / Math.Max(.01, viewScale);
        if (!HitRoad(worldX, worldY, tolerance, out var layer, out var road, out var branchId)) return false;
        if (additive && _roadSelection.Contains(road.Id))
        {
            _roadSelection.Remove(road.Id);
            if (_roadSelection.Count == 0) { _selectedBranchId = null; _roadVertex = -1; }
            RaiseRoadState();
            return true;
        }
        if (!additive && _roadSelection.Count > 1 && _roadSelection.Contains(road.Id))
        {
            _selectedBranchId = branchId; _roadVertex = -1;
            StatusDetail = RoadSelectionSummary; RaiseRoadState();
            return true;
        }
        if (!additive)
        {
            _roadSelection.Clear();
            ClearWaterSelection();
            if (Session.SelectedObjectIds.Count > 0) { Session.ClearSelection(); AfterSelectionChange(); }
        }
        _roadSelection.Add(road.Id);
        ClearLandSelection();
        _selectedBranchId = branchId;
        _roadVertex = _roadLevel == RoadSelectionLevel.Node ? NearestRoadVertex(road, branchId, worldX, worldY) : -1;
        _activeLayerId = layer.Id;
        StatusDetail = road.Locked ? $"«{road.Name}» заблокирована: снимите блокировку, чтобы менять её." : RoadSelectionSummary;
        RaiseRoadState();
        return true;
    }

    private static int NearestRoadVertex(Road road, Guid branchId, double x, double y)
    {
        if (road.FindBranch(branchId) is not { } branch || branch.Vertices.Length == 0) return -1;
        var best = 0; var bestDistance = double.MaxValue;
        for (var i = 0; i < branch.Vertices.Length; i++)
        {
            var node = road.FindNode(branch.Vertices[i].NodeId)!;
            var d = (node.X - x) * (node.X - x) + (node.Y - y) * (node.Y - y);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// A click on the terrain surfaces: whichever of water and road is on top under the point is
    /// selected, by level, then layer stack, then the layer's own surface order.
    /// </summary>
    public bool SelectSurfaceAt(double worldX, double worldY, double viewScale, bool additive)
    {
        var tolerance = 6 / Math.Max(.01, viewScale);
        var water = HitWater(worldX, worldY, tolerance, out var waterLayer, out var river, out _);
        var road = HitRoad(worldX, worldY, tolerance, out var roadLayer, out var hitRoad, out _);
        if (!water && !road) return false;
        if (water && road)
        {
            var layers = Session.Document.Layers.ToList();
            var waterKey = (river.Level, layers.IndexOf(waterLayer), LayerSurfaceOrder.Resolve(waterLayer).ToList().FindIndex(e => e.Id == river.Id));
            var roadKey = (hitRoad.Level, layers.IndexOf(roadLayer), LayerSurfaceOrder.Resolve(roadLayer).ToList().FindIndex(e => e.Id == hitRoad.Id));
            road = roadKey.CompareTo(waterKey) > 0;
            water = !road;
        }
        return road ? SelectRoadAt(worldX, worldY, viewScale, additive) : SelectWaterAt(worldX, worldY, viewScale, additive);
    }

    /// <summary>Roads whose drawn body meets the marquee, for the rubber-band selection.</summary>
    internal IReadOnlyList<Guid> RoadsIn(WorldBounds marquee)
    {
        var found = new List<Guid>();
        foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>().Where(l => l.Visible && l.Opacity > 0))
            foreach (var road in layer.Roads)
            {
                if (!road.Visible || road.Locked) continue;
                var set = RoadGeometry.Evaluate(road);
                if (set.Bounds.Intersects(marquee) && RoadGeometry.IntersectsRect(set, marquee)) found.Add(road.Id);
            }
        return found;
    }

    internal void AddRoadSelection(IReadOnlyList<Guid> ids, bool additive)
    {
        var before = _roadSelection.Count;
        if (!additive) _roadSelection.Clear();
        foreach (var id in ids) if (!_roadSelection.Contains(id)) _roadSelection.Add(id);
        if (_roadSelection.Count > 0) ClearLandSelection();
        if (_roadSelection.Count == 1 && SelectedRoad is { } road && SelectedRoadBranch is null) _selectedBranchId = road.Branches[0].Id;
        if (_roadSelection.Count == 0) { _selectedBranchId = null; _roadVertex = -1; }
        if (ids.Count > 0 || before != _roadSelection.Count) RaiseRoadState();
    }

    public void ClearRoadSelection()
    {
        if (_roadSelection.Count == 0) return;
        _roadSelection.Clear(); _selectedBranchId = null; _roadVertex = -1;
        RaiseRoadState();
    }

    public void SelectRoad(Guid id, Guid? branchId = null)
    {
        CancelRoadGesture();
        var road = Roads.FirstOrDefault(r => r.Id == id);
        if (road is null) return;
        ActiveTool = EditorTool.Road;
        _roadSelection.Clear(); _roadSelection.Add(id); _selectedBranchId = branchId ?? road.Branches[0].Id;
        _roadVertex = _roadLevel == RoadSelectionLevel.Node ? 0 : -1;
        _activeLayerId = RoadLayerOf(id)?.Id ?? _activeLayerId;
        Session.ClearSelection(); ClearWaterSelection(); ClearLandSelection(); RaiseRoadState();
    }

    /// <summary>Selects one road without changing the active tool: used by the object list.</summary>
    public void SelectRoadOnly(Guid id, bool additive = false)
    {
        var road = Roads.FirstOrDefault(r => r.Id == id);
        if (road is null) return;
        CancelRoadGesture();
        if (additive && _roadSelection.Contains(id)) _roadSelection.Remove(id);
        else
        {
            if (!additive)
            {
                _roadSelection.Clear(); ClearWaterSelection();
                if (Session.SelectedObjectIds.Count > 0) { Session.ClearSelection(); AfterSelectionChange(); }
            }
            if (!_roadSelection.Contains(id)) _roadSelection.Add(id);
            _selectedBranchId = road.Branches[0].Id;
        }
        _roadVertex = -1;
        ClearLandSelection();
        _activeLayerId = RoadLayerOf(id)?.Id ?? _activeLayerId;
        RaiseRoadState();
    }

    public void SelectRoadBranch(Guid branchId)
    {
        if (SelectedRoad?.FindBranch(branchId) is null) return;
        _selectedBranchId = branchId; _roadVertex = 0; RaiseRoadState();
    }

    public void SelectRoadVertex(int index)
    {
        if (SelectedRoadBranch is not { } branch || index < 0 || index >= branch.Vertices.Length) return;
        _roadVertex = index; RaiseRoadState();
    }

    // ------------------------------------------------------------------ moving whole roads

    public bool BeginRoadDrag(double worldX, double worldY)
    {
        var roads = SelectedRoads;
        if (roads.Count == 0) return false;
        if (roads.Any(r => r.Locked)) { StatusDetail = "Дорога заблокирована: снимите блокировку, чтобы перемещать её."; return false; }
        if (roads.Any(r => RoadLayerOf(r.Id) is not { Visible: true, Locked: false })) { StatusDetail = "Слой дороги скрыт или заблокирован."; return false; }
        _roadDragFrom = (worldX, worldY);
        _roadDragDrafts = roads.ToDictionary(r => r.Id, r => r);
        _roadDragMoved = false;
        return true;
    }

    public void UpdateRoadDrag(double worldX, double worldY)
    {
        if (_roadDragFrom is not { } from || _roadDragDrafts is null) return;
        var dx = worldX - from.X; var dy = worldY - from.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
        _roadDragMoved = true;
        var moved = new Dictionary<Guid, Road>(_roadDragDrafts.Count);
        foreach (var road in SelectedRoads) moved[road.Id] = RoadEditor.Translate(road, dx, dy);
        _roadDragDrafts = moved;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Commits the whole move as exactly one history entry, across layers.</summary>
    public bool EndRoadsDrag()
    {
        var drafts = _roadDragDrafts; var moved = _roadDragMoved;
        _roadDragFrom = null; _roadDragDrafts = null; _roadDragMoved = false;
        if (drafts is null || !moved) { InvalidateScene(EditorChangeReason.CanvasInvalidation); return false; }
        var steps = new List<IEditCommand>();
        foreach (var pair in drafts)
        {
            if (RoadLayerOf(pair.Key) is not { } layer) continue;
            try { pair.Value.Validate(); } catch (ArgumentException ex) { StatusDetail = ex.Message; return false; }
            steps.Add(new RoadCommand(layer.Id, pair.Key, pair.Value, "Переместить дорогу"));
        }
        if (steps.Count == 0) return false;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("Переместить дорогу", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        AfterEdit(geometryOnly: true); RaiseRoadState();
        StatusDetail = drafts.Count == 1 ? "Дорога перемещена." : "Перемещено дорог: " + drafts.Count + ".";
        return true;
    }

    public void CancelRoadDrag()
    {
        if (_roadDragFrom is null) return;
        _roadDragFrom = null; _roadDragDrafts = null; _roadDragMoved = false;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    // ------------------------------------------------------------------ delete at the chosen level

    /// <summary>Delete for roads: the selected node, the selected branch, or every selected road.</summary>
    public bool DeleteRoadSelection()
    {
        if (_roadSelection.Count == 0) return false;
        if (_roadLevel == RoadSelectionLevel.Node) return DeleteSelectedRoadVertex();
        if (_roadLevel == RoadSelectionLevel.Branch && _roadSelection.Count == 1
            && SelectedRoad is { } single && single.Branches.Length > 1 && SelectedRoadBranch is not null)
        { DeleteSelectedBranch(); return true; }
        return DeleteSelectedRoads();
    }

    public bool DeleteSelectedRoads()
    {
        var roads = SelectedRoads;
        if (roads.Count == 0) return false;
        if (roads.FirstOrDefault(r => r.Locked) is { } locked)
        { StatusDetail = $"«{locked.Name}» заблокирована: снимите блокировку, чтобы удалить её."; return false; }
        var steps = new List<IEditCommand>();
        foreach (var road in roads)
        {
            if (RoadLayerOf(road.Id) is not { } layer) continue;
            if (!layer.Visible || layer.Locked) { StatusDetail = "Слой дороги скрыт или заблокирован."; return false; }
            steps.Add(new RoadCommand(layer.Id, road.Id, null, "Удалить дорогу"));
        }
        if (steps.Count == 0) return false;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("Удалить дорогу", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        var removed = steps.Count;
        _roadSelection.Clear(); _selectedBranchId = null; _roadVertex = -1;
        AfterEdit(geometryOnly: true); RaiseRoadState();
        StatusDetail = removed == 1 ? "Дорога удалена. Отменить — Ctrl+Z." : "Удалено дорог: " + removed + ". Отменить — Ctrl+Z.";
        return true;
    }

    // ------------------------------------------------------------------ node and branch edits

    public void InsertRoadVertex()
    { if (SelectedRoad is null) return; _roadInsert = true; StatusDetail = "Щёлкните по участку, чтобы добавить узел в этом месте."; }

    public bool DeleteSelectedRoadVertex()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch || _roadVertex < 0 || _roadVertex >= branch.Vertices.Length) return false;
        Road next;
        try { next = RoadEditor.DeleteVertex(road, branch.Id, _roadVertex); }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; return false; }
        _roadVertex = Math.Min(_roadVertex, next.FindBranch(branch.Id)!.Vertices.Length - 1);
        return CommitRoad(next, "Удалить узел дороги");
    }

    public double? SelectedRoadVertexWidth =>
        SelectedRoadBranch is { } b && _roadVertex >= 0 && _roadVertex < b.Vertices.Length ? b.Vertices[_roadVertex].Width : null;
    public bool? SelectedRoadVertexSharp =>
        SelectedRoadBranch is { } b && _roadVertex >= 0 && _roadVertex < b.Vertices.Length ? b.Vertices[_roadVertex].Sharp : null;
    public double? SelectedRoadVertexTensionPercent =>
        SelectedRoadBranch is { } b && _roadVertex >= 0 && _roadVertex < b.Vertices.Length ? b.Vertices[_roadVertex].Tension * 100 : null;
    public bool SelectedRoadVertexIsShared =>
        SelectedRoad is { } r && SelectedRoadBranch is { } b && _roadVertex >= 0 && _roadVertex < b.Vertices.Length && r.IsShared(b.Vertices[_roadVertex].NodeId);
    public bool CanSplitSelectedBranch => SelectedRoadBranch is { } b && (b.Closed ? _roadVertex >= 0 && _roadVertex < b.Vertices.Length : _roadVertex > 0 && _roadVertex < b.Vertices.Length - 1);

    public void SetSelectedRoadVertexWidth(double width)
    {
        if (!double.IsFinite(width) || width < Road.MinimumWidth || width > Road.MaximumWidth) { StatusDetail = "Ширина должна быть числом не меньше 1."; return; }
        if (SelectedRoad is { } road && SelectedRoadBranch is { } branch && _roadVertex >= 0 && _roadVertex < branch.Vertices.Length)
            CommitRoad(RoadEditor.SetVertexWidth(road, branch.Id, _roadVertex, width), "Ширина дороги в узле");
    }

    public void SetSelectedRoadVertexSharp(bool sharp)
    {
        if (SelectedRoad is { } road && SelectedRoadBranch is { } branch && _roadVertex >= 0 && _roadVertex < branch.Vertices.Length)
            CommitRoad(RoadEditor.SetVertexSharp(road, branch.Id, _roadVertex, sharp), sharp ? "Острый узел дороги" : "Плавный узел дороги");
    }

    public void SetSelectedRoadVertexTensionPercent(double percent)
    {
        if (!double.IsFinite(percent)) { StatusDetail = "Кривизна узла задаётся в процентах."; return; }
        if (SelectedRoad is { } road && SelectedRoadBranch is { } branch && _roadVertex >= 0 && _roadVertex < branch.Vertices.Length)
            CommitRoad(RoadEditor.SetVertexTension(road, branch.Id, _roadVertex, percent / 100), "Кривизна узла дороги");
    }

    /// <summary>Takes the selected vertex off its shared node: this branch gets a node of its own there.</summary>
    public void DetachSelectedRoadVertex()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch || _roadVertex < 0 || _roadVertex >= branch.Vertices.Length) return;
        var next = RoadEditor.DetachVertex(road, branch.Id, _roadVertex);
        if (ReferenceEquals(next, road)) { StatusDetail = "Этот узел и так принадлежит только этому участку."; return; }
        if (CommitRoad(next, "Отсоединить участок в узле")) StatusDetail = "Участок отсоединён: у него теперь свой узел на том же месте.";
    }

    public void SplitSelectedBranch()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch) return;
        try
        {
            var next = RoadEditor.SplitBranch(road, branch.Id, _roadVertex, out var second);
            if (CommitRoad(next, "Разделить участок")) { _selectedBranchId = second; _roadVertex = 0; RaiseRoadState(); }
        }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; }
    }

    public double? SelectedBranchWidth => SelectedRoadBranch is { } b && b.Vertices.Length > 0 ? Math.Round(b.Vertices.Average(v => v.Width), 2) : null;
    public double? SelectedBranchStartWidth => SelectedRoadBranch is { Closed: false } b ? b.Vertices[0].Width : null;
    public double? SelectedBranchEndWidth => SelectedRoadBranch is { Closed: false } b ? b.Vertices[^1].Width : null;
    public double? SelectedBranchTensionPercent => SelectedRoadBranch is { } b && b.Vertices.Length > 0 ? b.Vertices.Average(v => v.Tension) * 100 : null;
    public bool SelectedBranchIsClosed => SelectedRoadBranch?.Closed == true;
    public bool SelectedBranchHasOwnStyle => SelectedRoadBranch?.Style is not null;

    public void SetSelectedBranchWidth(double width)
    {
        if (!double.IsFinite(width) || width < Road.MinimumWidth || width > Road.MaximumWidth) { StatusDetail = "Ширина должна быть числом не меньше 1."; return; }
        if (SelectedRoad is { } road && SelectedRoadBranch is { } branch)
            CommitRoad(RoadEditor.SetBranchWidth(road, branch.Id, width), "Ширина участка");
    }

    public void SetSelectedBranchWidthProfile(double startWidth, double endWidth)
    {
        if (!double.IsFinite(startWidth) || !double.IsFinite(endWidth) || startWidth < Road.MinimumWidth || endWidth < Road.MinimumWidth
            || startWidth > Road.MaximumWidth || endWidth > Road.MaximumWidth)
        { StatusDetail = "Ширины начала и конца должны быть числами не меньше 1."; return; }
        if (SelectedRoad is { } road && SelectedRoadBranch is { Closed: false } branch)
            CommitRoad(RoadEditor.SetWidthProfile(road, branch.Id, startWidth, endWidth), "Профиль ширины участка");
    }

    public void SetSelectedBranchStartWidth(double width) => SetSelectedBranchWidthProfile(width, SelectedBranchEndWidth ?? width);
    public void SetSelectedBranchEndWidth(double width) => SetSelectedBranchWidthProfile(SelectedBranchStartWidth ?? width, width);

    public void SetSelectedBranchTensionPercent(double percent)
    {
        if (!double.IsFinite(percent)) { StatusDetail = "Плавность задаётся в процентах."; return; }
        if (SelectedRoad is { } road && SelectedRoadBranch is { } branch)
            CommitRoad(RoadEditor.SetBranchTension(road, branch.Id, percent / 100), "Плавность участка");
    }

    public void SmoothSelectedBranch()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch) return;
        var before = branch.Vertices.Length;
        if (CommitRoad(RoadEditor.Smooth(road, branch.Id), "Сгладить участок"))
            StatusDetail = $"Участок сглажен; узлов по-прежнему {before}, общие узлы не двигались.";
    }

    public void SimplifySelectedBranch()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch) return;
        var before = branch.Vertices.Length;
        var next = RoadEditor.SimplifyBranch(road, branch.Id, _roadSimplifyTolerance);
        if (next.FindBranch(branch.Id) is not { } simplified || simplified.Vertices.Length == before)
        { StatusDetail = $"Упрощать нечего: точность {_roadSimplifyTolerance:0.#} ед. уже соблюдена при {before} узлах."; return; }
        if (CommitRoad(next, "Упростить участок"))
        {
            _roadVertex = Math.Clamp(_roadVertex, -1, simplified.Vertices.Length - 1);
            StatusDetail = $"Узлов: {before} → {simplified.Vertices.Length}; общие узлы сохранены.";
            RaiseRoadState();
        }
    }

    public void ReverseSelectedBranch()
    {
        if (SelectedRoad is { } road && SelectedRoadBranch is { } branch)
            CommitRoad(RoadEditor.Reverse(road, branch.Id), "Развернуть участок");
    }

    public void ToggleSelectedBranchClosed()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch) return;
        try { CommitRoad(RoadEditor.SetClosed(road, branch.Id, !branch.Closed), branch.Closed ? "Разомкнуть маршрут" : "Замкнуть маршрут"); }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; }
    }

    public void SetSelectedBranchName(string name)
    {
        if (SelectedRoad is { } road && SelectedRoadBranch is { } branch && !string.IsNullOrWhiteSpace(name) && name.Trim() != branch.Name)
            CommitRoad(RoadEditor.SetBranchName(road, branch.Id, name.Trim()), "Переименовать участок");
    }

    public void DeleteSelectedBranch()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch) return;
        if (road.Branches.Length <= 1) { DeleteSelectedRoads(); return; }
        var next = RoadEditor.DeleteBranch(road, branch.Id);
        _selectedBranchId = next.Branches[0].Id; _roadVertex = 0;
        CommitRoad(next, "Удалить участок");
    }

    /// <summary>Moves the selected branch into a road of its own; the fork it left keeps its node.</summary>
    public void DetachSelectedBranchAsRoad()
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch || RoadLayerOf(road.Id) is not { Visible: true, Locked: false } layer) return;
        try
        {
            var (remaining, detached) = RoadEditor.DetachBranchAsRoad(road, branch.Id, EntityNameGenerator.Road(Session.Document));
            remaining.Validate(); detached.Validate();
            var revision = Session.Revision;
            Session.Execute(new RoadCommand(layer.Id, road.Id, remaining, "Отделить участок в отдельную дорогу", additionalRoad: detached));
            if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return; }
            _roadSelection.Clear(); _roadSelection.Add(detached.Id); _selectedBranchId = detached.Branches[0].Id; _roadVertex = 0;
            AfterEdit(geometryOnly: true); RaiseRoadState();
            StatusDetail = "Участок отделён в отдельную дорогу; общий узел остался у прежней дороги.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { StatusDetail = ex.Message; }
    }

    /// <summary>A copy of every selected road beside the original, as one history entry.</summary>
    public bool DuplicateSelectedRoads()
    {
        var roads = SelectedRoads;
        if (roads.Count == 0) return false;
        var steps = new List<IEditCommand>();
        var copies = new List<Guid>();
        foreach (var road in roads)
        {
            if (RoadLayerOf(road.Id) is not { Visible: true, Locked: false } layer) { StatusDetail = "Слой дороги скрыт или заблокирован."; return false; }
            var offset = Math.Max(12, road.Branches.SelectMany(b => b.Vertices).Max(v => v.Width) * road.WidthScale * 1.5);
            var copy = RoadEditor.Translate(road.WithNewIds(), offset, offset) with { Name = EntityNameGenerator.Road(Session.Document) + " · копия", Locked = false };
            copy = copy with { Seed = copy.Id.GetHashCode() };
            copies.Add(copy.Id);
            steps.Add(new RoadCommand(layer.Id, copy.Id, copy, "Дублировать дорогу"));
        }
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("Дублировать дорогу", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        _roadSelection.Clear(); _roadSelection.AddRange(copies);
        _selectedBranchId = SelectedRoad?.Branches[0].Id; _roadVertex = -1;
        AfterEdit(geometryOnly: true); RaiseRoadState();
        StatusDetail = copies.Count == 1 ? "Копия дороги создана и выбрана." : "Создано копий: " + copies.Count + ".";
        return true;
    }

    // ------------------------------------------------------------------ road-wide edits and style

    public void SetRoadName(string name)
    {
        if (SelectedRoad is { } road && !string.IsNullOrWhiteSpace(name) && name.Trim() != road.Name)
            CommitRoad(road with { Name = name.Trim() }, "Переименовать дорогу");
    }

    public void SetRoadWidthScalePercent(double percent)
    {
        if (!double.IsFinite(percent) || percent is < 1 or > 10000) { StatusDetail = "Масштаб ширины: от 1 до 10000 %."; return; }
        if (SelectedRoad is { } road) CommitRoad(road with { WidthScale = percent / 100 }, "Масштаб ширины дороги");
    }

    /// <summary>
    /// Which style the appearance panel edits: the selected branch's own style when it has one
    /// (and the panel is at the branch or node level), otherwise the road's style.
    /// </summary>
    public bool RoadStyleTargetsBranch => _roadLevel != RoadSelectionLevel.Road && SelectedRoadBranch?.Style is not null;

    /// <summary>The style the panel shows: the branch's own or the road's.</summary>
    public RoadStyle? RoadStyleShown => SelectedRoad is { } road ? (RoadStyleTargetsBranch ? SelectedRoadBranch!.Style : road.Style) : null;

    /// <summary>Gives the selected branch its own copy of the road style, or takes it away.</summary>
    public void SetSelectedBranchOwnStyle(bool own)
    {
        if (SelectedRoad is not { } road || SelectedRoadBranch is not { } branch) return;
        if (own == (branch.Style is not null)) return;
        CommitRoad(RoadEditor.SetBranchStyle(road, branch.Id, own ? road.Style : null), own ? "Свой стиль участка" : "Стиль участка как у дороги");
    }

    /// <summary>One of the three presets, applied to the shown style: the way of drawing changes, not only the colours.</summary>
    public void ApplyRoadPreset(RoadDrawing drawing) =>
        SetRoadStyle(s => RoadStyle.Preset(drawing) with { Opacity = s.Opacity, TextureId = s.TextureId, TextureOpacity = s.TextureOpacity, TextureScale = s.TextureScale },
            drawing switch { RoadDrawing.PenRuts => "Дорога: перо и колея", RoadDrawing.Painterly => "Дорога: мягкая живописная", _ => "Дорога: просёлочная" });

    public string? RoadSurfaceColor => RoadStyleShown?.SurfaceColor;
    public string? RoadToneColor => RoadStyleShown?.ToneColor;
    public string? RoadShoulderColor => RoadStyleShown?.ShoulderColor;
    public string? RoadEdgeColor => RoadStyleShown?.EdgeColor;
    public string? RoadRutColor => RoadStyleShown?.RutColor;
    public string? RoadTextureId => RoadStyleShown?.TextureId;
    public double? RoadTextureOpacityPercent => RoadStyleShown is { } s ? s.TextureOpacity * 100 : null;
    public double? RoadTextureScale => RoadStyleShown?.TextureScale;
    public RoadDrawing? RoadDrawingShown => RoadStyleShown?.Drawing;

    public void SetRoadSurfaceColor(string color) => SetRoadStyle(s => s with { SurfaceColor = AppearanceColor.Normalize(color) ?? s.SurfaceColor }, "Цвет дороги");
    public void SetRoadToneColor(string color) => SetRoadStyle(s => s with { ToneColor = AppearanceColor.Normalize(color) ?? s.ToneColor }, "Тон грунта");
    public void SetRoadShoulderColor(string color) => SetRoadStyle(s => s with { ShoulderColor = AppearanceColor.Normalize(color) ?? s.ShoulderColor }, "Цвет обочины");
    public void SetRoadEdgeColor(string color) => SetRoadStyle(s => s with { EdgeColor = AppearanceColor.Normalize(color) ?? s.EdgeColor }, "Цвет кромки");
    public void SetRoadRutColor(string color) => SetRoadStyle(s => s with { RutColor = AppearanceColor.Normalize(color) ?? s.RutColor }, "Цвет колеи");
    public void SetRoadToneVariationPercent(double percent) => SetRoadStyle(s => s with { ToneVariation = Math.Clamp(percent, 0, 100) / 100 }, "Вариация тона");
    public void SetRoadShoulderWidth(double width) => SetRoadStyle(s => s with { ShoulderWidth = Math.Clamp(width, 0, 256) }, "Ширина обочины");
    public void SetRoadShoulderPercent(double percent) => SetRoadStyle(s => s with { ShoulderOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Сила обочины");
    public void SetRoadEdgeWidth(double width) => SetRoadStyle(s => s with { EdgeWidth = Math.Clamp(width, 0, 32) }, "Толщина кромки");
    public void SetRoadEdgePercent(double percent) => SetRoadStyle(s => s with { EdgeOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Сила кромки");
    public void SetRoadEdgeBreakPercent(double percent) => SetRoadStyle(s => s with { EdgeBreak = Math.Clamp(percent, 0, 100) / 100 }, "Прерывистость кромки");
    public void SetRoadRutPercent(double percent) => SetRoadStyle(s => s with { RutOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Сила колеи");
    public void SetRoadDetailPercent(double percent) => SetRoadStyle(s => s with { DetailOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Детали у кромки");
    public void SetRoadEdgeRoughnessPercent(double percent) => SetRoadStyle(s => s with { EdgeRoughness = Math.Clamp(percent, 0, 100) / 100 }, "Неровность края");
    public void SetRoadOpacityPercent(double percent) => SetRoadStyle(s => s with { Opacity = Math.Clamp(percent, 0, 100) / 100 }, "Непрозрачность дороги");
    public void SetRoadStartCap(RoadEndCap cap) => SetRoadStyle(s => s with { StartCap = cap }, "Начало дороги");
    public void SetRoadEndCap(RoadEndCap cap) => SetRoadStyle(s => s with { EndCap = cap }, "Конец дороги");
    public void SetRoadTaperLength(double length) => SetRoadStyle(s => s with { TaperLength = Math.Clamp(length, 0, Road.MaximumWidth) }, "Длина затухания");

    public void SetRoadTexture(string? id) => SetRoadStyle(
        s => s with { TextureId = MapFillTextures.Find(id) is { Kind: MapFillTextureKind.Ground } ground ? ground.Id : null },
        id is null ? "Убрать материал дороги" : "Материал дороги");
    public void SetRoadTextureOpacityPercent(double percent) => SetRoadStyle(s => s with { TextureOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Сила материала дороги");
    public void SetRoadTextureScale(double scale) => SetRoadStyle(
        s => s with { TextureScale = Math.Clamp(scale, MapFillTextures.MinimumScale, MapFillTextures.MaximumScale) }, "Размер материала дороги");

    private void SetRoadStyle(Func<RoadStyle, RoadStyle> change, string name)
    {
        if (SelectedRoad is not { } road) return;
        if (RoadStyleTargetsBranch && SelectedRoadBranch is { Style: { } own } branch)
        {
            var next = change(own);
            if (next == own) return;
            CommitRoad(RoadEditor.SetBranchStyle(road, branch.Id, next), name);
            return;
        }
        var style = change(road.Style);
        if (style == road.Style) return;
        CommitRoad(road with { Style = style }, name);
    }

    // ------------------------------------------------------------------ level, order, visibility, layer

    public int? RoadSelectionLevelValue
    {
        get
        {
            var roads = SelectedRoads;
            if (roads.Count == 0) return null;
            var first = roads[0].Level;
            return roads.All(r => r.Level == first) ? first : null;
        }
    }

    /// <summary>The level of whatever terrain surfaces are selected: water when any is, else roads.</summary>
    public int? SurfaceSelectionLevelValue => WaterSelectionCount > 0 ? WaterSelectionLevelValue : RoadSelectionLevelValue;
    public int SurfaceSelectionCount => WaterSelectionCount + RoadSelectionCount;

    public bool SetSelectedSurfaceLevel(int level)
    {
        var changed = false;
        if (WaterSelectionCount > 0) changed |= SetSelectedWaterLevel(level);
        if (RoadSelectionCount > 0) changed |= SetSelectedRoadLevel(level);
        return changed;
    }

    public bool ShiftSelectedSurfaceLevel(int delta)
    {
        var changed = false;
        if (WaterSelectionCount > 0) changed |= ShiftSelectedWaterLevel(delta);
        if (RoadSelectionCount > 0) changed |= ShiftSelectedRoadLevel(delta);
        return changed;
    }

    public bool SetSelectedRoadLevel(int level)
    {
        var bounded = TerrainOrder.Clamp(level);
        return EditSelectedRoadLevels(_ => bounded, "Уровень дороги");
    }

    public bool ShiftSelectedRoadLevel(int delta) =>
        delta != 0 && EditSelectedRoadLevels(road => TerrainOrder.Clamp(road.Level + Math.Sign(delta)), "Уровень дороги");

    private bool EditSelectedRoadLevels(Func<Road, int> level, string name)
    {
        var roads = SelectedRoads;
        if (roads.Count == 0) return false;
        if (roads.FirstOrDefault(r => r.Locked) is { } locked)
        { StatusDetail = $"«{locked.Name}» заблокирована: снимите блокировку, чтобы менять уровень."; return false; }
        var steps = new List<IEditCommand>();
        foreach (var road in roads)
        {
            if (RoadLayerOf(road.Id) is not { } layer) continue;
            var next = level(road);
            if (next == road.Level) continue;
            steps.Add(new RoadCommand(layer.Id, road.Id, road with { Level = next }, name));
        }
        if (steps.Count == 0) return false;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand(name, steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        AfterEdit(geometryOnly: true); RaiseRoadState();
        StatusDetail = RoadSelectionLevelValue is { } shared ? $"Дорога · уровень {FormatSublayer(shared)}" : "Уровень дороги изменён.";
        return true;
    }

    /// <summary>
    /// Выше / Ниже / Наверх / Вниз for the selected roads and water together: their order among
    /// the surfaces of their own layer, which decides what covers what at one level.
    /// </summary>
    public bool ReorderSelectedSurfaces(int direction, bool toExtreme = false)
    {
        if (direction == 0) return false;
        if (SelectedRoads.Any(r => r.Locked) || SelectedWaters.Any(r => r.Locked))
        { StatusDetail = "Выбранная поверхность заблокирована: снимите блокировку, чтобы менять порядок."; return false; }
        var selected = SelectedRoads.Select(r => r.Id).Concat(SelectedWaters.Select(r => r.Id)).ToHashSet();
        if (selected.Count == 0) return false;
        var steps = new List<IEditCommand>();
        foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>())
        {
            var order = LayerSurfaceOrder.Resolve(layer).Select(e => e.Id).ToList();
            if (!order.Any(selected.Contains)) continue;
            if (!layer.Visible || layer.Locked) { StatusDetail = "Слой скрыт или заблокирован."; return false; }
            var up = direction > 0;
            if (toExtreme)
            {
                var moved = order.Where(selected.Contains).ToList();
                var rest = order.Where(id => !selected.Contains(id)).ToList();
                order = up ? [.. rest, .. moved] : [.. moved, .. rest];
            }
            else if (up)
            {
                for (var index = order.Count - 2; index >= 0; index--)
                    if (selected.Contains(order[index]) && !selected.Contains(order[index + 1]))
                        (order[index], order[index + 1]) = (order[index + 1], order[index]);
            }
            else
            {
                for (var index = 1; index < order.Count; index++)
                    if (selected.Contains(order[index]) && !selected.Contains(order[index - 1]))
                        (order[index], order[index - 1]) = (order[index - 1], order[index]);
            }
            if (!order.SequenceEqual(LayerSurfaceOrder.Resolve(layer).Select(e => e.Id)))
                steps.Add(new ReorderSurfaceCommand(layer.Id, order, "Порядок поверхностей"));
        }
        if (steps.Count == 0) { StatusDetail = direction > 0 ? "Уже сверху." : "Уже снизу."; return false; }
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("Порядок поверхностей", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        AfterEdit(geometryOnly: true); RaiseRoadState(); RaiseWaterState();
        return true;
    }

    public bool SelectedRoadVisible => SelectedRoads.Count > 0 && SelectedRoads.All(r => r.Visible);
    public bool SelectedRoadLocked => SelectedRoads.Count > 0 && SelectedRoads.All(r => r.Locked);
    public void ToggleSelectedRoadVisible() => SetSelectedRoadVisible(!SelectedRoadVisible);
    public void ToggleSelectedRoadLock() => SetSelectedRoadLocked(!SelectedRoadLocked);
    public void SetSelectedRoadVisible(bool visible) => ApplyToSelectedRoads(r => r with { Visible = visible }, visible ? "Показать дорогу" : "Скрыть дорогу", ignoreLock: false);
    public void SetSelectedRoadLocked(bool locked) => ApplyToSelectedRoads(r => r with { Locked = locked }, locked ? "Заблокировать дорогу" : "Разблокировать дорогу", ignoreLock: true);

    private void ApplyToSelectedRoads(Func<Road, Road> change, string name, bool ignoreLock)
    {
        var roads = SelectedRoads;
        if (roads.Count == 0) return;
        if (!ignoreLock && roads.Any(r => r.Locked)) { StatusDetail = "Дорога заблокирована."; return; }
        var steps = new List<IEditCommand>();
        foreach (var road in roads)
        {
            if (RoadLayerOf(road.Id) is not { Visible: true, Locked: false } layer) { StatusDetail = "Слой дороги скрыт или заблокирован."; return; }
            var next = change(road);
            if (next.Visible == road.Visible && next.Locked == road.Locked) continue;
            steps.Add(new RoadCommand(layer.Id, road.Id, next, name));
        }
        if (steps.Count == 0) return;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand(name, steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return; }
        AfterEdit(geometryOnly: true); RaiseRoadState();
        StatusDetail = name + ": готово.";
    }

    /// <summary>Moves the selected roads to another object layer as one history entry; the whole network travels.</summary>
    public bool MoveSelectedRoadsToLayer(LayerId targetId)
    {
        var roads = SelectedRoads;
        if (roads.Count == 0) return false;
        if (Session.Document.FindLayer(targetId) is not ObjectLayer target) { StatusDetail = "Дорога переносится только на слой объектов."; return false; }
        if (!target.Visible || target.Locked) { StatusDetail = "Целевой слой скрыт или заблокирован."; return false; }
        var steps = new List<IEditCommand>();
        foreach (var road in roads)
        {
            if (RoadLayerOf(road.Id) is not { } source || source.Id == targetId) continue;
            if (!source.Visible || source.Locked) { StatusDetail = "Исходный слой дороги скрыт или заблокирован."; return false; }
            steps.Add(new RoadCommand(source.Id, road.Id, null, "Снять дорогу со слоя"));
            steps.Add(new RoadCommand(targetId, road.Id, road, "Положить дорогу на слой"));
        }
        if (steps.Count == 0) { StatusDetail = "Дорога уже на этом слое."; return false; }
        var revision = Session.Revision;
        Session.Execute(new CompositeCommand("Перенести дорогу на другой слой", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        _activeLayerId = targetId;
        AfterEdit(geometryOnly: true); RaiseRoadState();
        StatusDetail = $"Дорога перенесена на слой «{target.Name}».";
        return true;
    }

    /// <summary>The object layers holding the selected roads, without duplicates.</summary>
    private IReadOnlyList<LayerId> RoadOwnerLayerIds() =>
        [.. SelectedRoads.Select(road => RoadLayerOf(road.Id)).OfType<ObjectLayer>().Select(layer => layer.Id).Distinct()];

    // ------------------------------------------------------------------ plumbing

    private ObjectLayer? RoadLayerOf(Guid roadId) =>
        Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.Roads.Any(r => r.Id == roadId));

    private bool CommitRoad(Road next, string name, LayerId? layerId = null)
    {
        var layer = layerId is { } id ? Session.Document.FindLayer(id) as ObjectLayer : RoadLayerOf(next.Id);
        if (layer is not { Visible: true, Locked: false }) { StatusDetail = "Слой дороги скрыт или заблокирован."; return false; }
        if (SelectedRoad is { } current && SameRoad(current, next)) return false;
        try { next.Validate(); } catch (ArgumentException ex) { StatusDetail = ex.Message; return false; }
        var revision = Session.Revision;
        Session.Execute(new RoadCommand(layer.Id, next.Id, next, name));
        if (revision == Session.Revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; RaiseRoadState(); return false; }
        if (!_roadSelection.Contains(next.Id)) { _roadSelection.Clear(); _roadSelection.Add(next.Id); }
        if (SelectedRoadBranch is null) { _selectedBranchId = next.Branches[0].Id; _roadVertex = 0; }
        AfterEdit(geometryOnly: true); RaiseRoadState(); return true;
    }

    /// <summary>Content equality: record equality of ImmutableArray members compares references only.</summary>
    private static bool SameRoad(Road a, Road b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Id != b.Id || a.Name != b.Name || a.Style != b.Style || a.WidthScale != b.WidthScale || a.Level != b.Level
            || a.Visible != b.Visible || a.Locked != b.Locked || a.Seed != b.Seed
            || a.Nodes.Length != b.Nodes.Length || a.Branches.Length != b.Branches.Length) return false;
        if (!a.Nodes.SequenceEqual(b.Nodes)) return false;
        if (!a.CrossingOverrides.SequenceEqual(b.CrossingOverrides)) return false;
        for (var i = 0; i < a.Branches.Length; i++)
        {
            var x = a.Branches[i]; var y = b.Branches[i];
            if (x.Id != y.Id || x.Name != y.Name || x.Closed != y.Closed || x.Style != y.Style || !x.Vertices.SequenceEqual(y.Vertices)) return false;
        }
        return true;
    }

    private void RaiseRoadState()
    {
        Raise(nameof(Roads)); Raise(nameof(SelectedRoad)); Raise(nameof(SelectedRoadBranch)); Raise(nameof(RoadSummary));
        Raise(nameof(RoadSelectionIds)); Raise(nameof(RoadSelectionCount)); Raise(nameof(SelectedRoads)); Raise(nameof(RoadSelectionSummary));
        Raise(nameof(IsCreatingRoad)); Raise(nameof(SelectedRoadVertexWidth)); Raise(nameof(SelectedRoadVertexSharp)); Raise(nameof(SelectedRoadVertexTensionPercent));
        Raise(nameof(SelectedRoadVertexIsShared)); Raise(nameof(CanSplitSelectedBranch)); Raise(nameof(SelectedBranchWidth));
        Raise(nameof(SelectedBranchStartWidth)); Raise(nameof(SelectedBranchEndWidth)); Raise(nameof(SelectedBranchTensionPercent));
        Raise(nameof(SelectedBranchIsClosed)); Raise(nameof(SelectedBranchHasOwnStyle)); Raise(nameof(RoadStyleTargetsBranch)); Raise(nameof(RoadStyleShown));
        Raise(nameof(RoadDrawMode)); Raise(nameof(RoadTextureId)); Raise(nameof(RoadTextureOpacityPercent)); Raise(nameof(RoadTextureScale));
        Raise(nameof(SelectedRoadVisible)); Raise(nameof(SelectedRoadLocked)); Raise(nameof(RoadSelectionLevelValue)); Raise(nameof(SurfaceSelectionLevelValue));
        Raise(nameof(RoadSelectionFrame)); Raise(nameof(SelectionStatus)); Raise(nameof(SelectedRoadCrossings));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }
}
