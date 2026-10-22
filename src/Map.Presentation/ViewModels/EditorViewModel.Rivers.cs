using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>How the river tool takes new courses: node clicks or a freehand drag.</summary>
public enum RiverDrawMode { Nodes, Freehand }

/// <summary>Stage 19: rivers, distributaries and lakes as one editable water construction.</summary>
public sealed partial class EditorViewModel
{
    private Guid? _selectedChannelId;

    /// <summary>
    /// The primary selected water system. It is not a second selection state: it reads and writes
    /// the shared water selection, so the river panel, the object list and the canvas can never
    /// disagree about what is selected.
    /// </summary>
    private Guid? _selectedRiverId
    {
        get => _waterSelection.Count > 0 ? _waterSelection[^1] : null;
        set
        {
            _waterSelection.Clear();
            if (value is { } id) _waterSelection.Add(id);
        }
    }
    private int _riverNode = -1;
    private River? _riverDraft, _riverOriginal;
    private LayerId? _riverLayer;
    private Guid? _riverDraftChannelId;
    private bool _riverCreating, _riverCreatingLake, _riverDragging, _riverDraggingMouth, _riverDraggingSource, _riverInsert, _riverStroking;
    private double _newRiverWidth = 12;
    private RiverDrawMode _riverDrawMode = RiverDrawMode.Nodes;
    private List<VectorPoint>? _riverStroke;
    private RiverJunction? _riverStrokeSource;
    private River? _riverStrokeSourceRiver;
    private double _riverMeanderPercent = 20;
    private double _riverFlowSourceWidth = 4, _riverFlowGrowth = 6;
    private double _riverSimplifyTolerance = 6;
    private bool _riverPreviewing;
    private int _riverMeanderSeed;

    public IReadOnlyList<River> Rivers => Session.Document.Layers.OfType<ObjectLayer>().SelectMany(l => l.Rivers).ToArray();
    public River? SelectedRiver => _selectedRiverId is { } id ? Rivers.FirstOrDefault(r => r.Id == id) : null;
    public RiverChannel? SelectedRiverChannel => SelectedRiver?.FindChannel(_selectedChannelId ?? Guid.Empty);

    /// <summary>The river to draw handles for: a live draft during a gesture, otherwise the selection on a visible layer.</summary>
    public River? DisplayedRiver
    {
        get
        {
            if (_riverDraft is not null) return _riverDraft;
            var selected = SelectedRiver;
            if (selected is null || RiverLayerOf(selected.Id) is not { Visible: true }) return null;
            if (_waterDragDrafts is { } drafts && drafts.TryGetValue(selected.Id, out var dragged)) return dragged;
            return selected.Visible ? selected : null;
        }
    }

    public Guid? DisplayedRiverChannelId => _riverDraftChannelId ?? _selectedChannelId;
    /// <summary>
    /// Live substitutions the canvas draws instead of the saved rivers: the gesture draft and the
    /// water systems currently following the pointer. The document itself is untouched.
    /// </summary>
    public IReadOnlyList<River>? RiverRenderOverrides
    {
        get
        {
            var dragged = WaterDragOverrides;
            if (_riverDraft is null) return dragged;
            if (dragged is null) return [_riverDraft];
            return [.. dragged.Where(r => r.Id != _riverDraft.Id), _riverDraft];
        }
    }
    public LayerId? RiverPreviewLayerId => _riverLayer;
    public bool IsCreatingRiver => _riverCreating;
    public bool IsCreatingLake => _riverCreating && _riverCreatingLake;
    public bool HasRiverGesture => _riverDraft is not null;
    public bool IsDraggingRiver => _riverDragging || _riverDraggingMouth || _riverDraggingSource || _riverStroking || IsDraggingWater;
    public int SelectedRiverNodeIndex => _riverNode;

    public RiverDrawMode RiverDrawMode
    {
        get => _riverDrawMode;
        set { if (SetField(ref _riverDrawMode, value)) RaiseRiverState(); }
    }

    public double NewRiverWidth
    {
        get => _newRiverWidth;
        set { if (double.IsFinite(value)) SetField(ref _newRiverWidth, Math.Clamp(value, River.MinimumWidth, 4096)); }
    }

    public double RiverMeanderPercent { get => _riverMeanderPercent; set { if (double.IsFinite(value)) SetField(ref _riverMeanderPercent, Math.Clamp(value, 1, 100)); } }
    public double RiverSimplifyTolerance { get => _riverSimplifyTolerance; set { if (double.IsFinite(value)) SetField(ref _riverSimplifyTolerance, Math.Clamp(value, 0.5, 512)); } }

    /// <summary>True while a shape change is shown on the canvas but not yet part of the map.</summary>
    public bool HasRiverPreview => _riverPreviewing && _riverDraft is not null;

    /// <summary>Curvature handle of the selected node, in percent; 100 % is the plain smooth curve.</summary>
    public double? SelectedRiverNodeTensionPercent =>
        SelectedRiverChannel is { } tc && _riverNode >= 0 && _riverNode < tc.Nodes.Length ? tc.Nodes[_riverNode].Tension * 100 : null;

    public double? SelectedChannelTensionPercent =>
        SelectedRiverChannel is { } cc && cc.Nodes.Length > 0 ? cc.Nodes.Average(n => n.Tension) * 100 : null;
    public double RiverFlowSourceWidth { get => _riverFlowSourceWidth; set { if (double.IsFinite(value)) SetField(ref _riverFlowSourceWidth, Math.Clamp(value, River.MinimumWidth, 4096)); } }
    public double RiverFlowGrowth { get => _riverFlowGrowth; set { if (double.IsFinite(value)) SetField(ref _riverFlowGrowth, Math.Clamp(value, 0, 4096)); } }

    public double? SelectedRiverNodeWidth => SelectedRiverChannel is { Closed: false } c && _riverNode >= 0 && _riverNode < c.Nodes.Length ? c.Nodes[_riverNode].Width : null;
    public bool? SelectedRiverNodeSharp => SelectedRiverChannel is { } c && _riverNode >= 0 && _riverNode < c.Nodes.Length ? c.Nodes[_riverNode].Sharp : null;
    public double? SelectedChannelSourceWidth => SelectedRiverChannel is { Closed: false } c ? c.Nodes[0].Width : null;
    public double? SelectedChannelMouthWidth => SelectedRiverChannel is { Closed: false } c ? c.Nodes[^1].Width : null;
    public bool SelectedChannelIsTributary => SelectedRiverChannel?.Mouth is not null;
    public bool SelectedChannelHasSource => SelectedRiverChannel?.Source is not null;
    public bool SelectedChannelIsLake => SelectedRiverChannel?.Closed == true;
    public bool CanSplitSelectedChannel => SelectedRiverChannel is { Closed: false } c && _riverNode > 0 && _riverNode < c.Nodes.Length - 1;

    public string RiverSummary
    {
        get
        {
            if (_riverCreating)
            {
                var count = _riverDraft?.Channels.FirstOrDefault(c => c.Id == _riverDraftChannelId)?.Nodes.Length ?? 0;
                if (_riverCreatingLake)
                    return count < 3 ? $"Озеро: узлов {count}. Ставьте вершины по контуру, Enter замкнёт (нужно не меньше трёх)."
                        : $"Озеро: узлов {count}. Enter — замкнуть, Esc — отменить.";
                return count == 0
                    ? (_riverDrawMode == RiverDrawMode.Freehand
                        ? "Протяните русло от истока к устью. Начало на существующем русле — рукав, конец на русле или озере — приток."
                        : "Ставьте узлы кликами от истока к устью. Первый клик по руслу — рукав от него; клик по руслу или озеру с узлами в черновике — приток; Enter — свободный конец.")
                    : $"Узлов: {count}. Клик по руслу или озеру — впасть; Enter — свободный конец; Esc — отменить.";
            }
            if (SelectedRiver is not { } river) return "Выберите реку или озеро на холсте, либо создайте новые.";
            var channel = SelectedRiverChannel;
            if (channel is null) return $"{river.Name} · русел: {river.Channels.Length}";
            if (channel.Closed) return $"{river.Name} · {channel.Name}: озеро, вершин {channel.Nodes.Length}";
            var source = channel.Source is { } s ? $" · вытекает из «{river.FindChannel(s.ParentChannelId)?.Name}»" : " · свободный исток";
            var mouth = channel.Mouth is { } m ? $" · впадает в «{river.FindChannel(m.ParentChannelId)?.Name}»" : " · свободный конец";
            return $"{river.Name} · русел: {river.Channels.Length} · {channel.Name}: узлов {channel.Nodes.Length}{source}{mouth}";
        }
    }

    // ------------------------------------------------------------------ creation

    public void StartRiver()
    {
        CancelRiverGesture();
        ActiveTool = EditorTool.River;
        _riverCreating = true; _riverCreatingLake = false; _riverNode = -1;
        StatusDetail = _riverDrawMode == RiverDrawMode.Freehand
            ? "Река: протяните русло по холсту. Начало на русле — рукав, конец на русле или озере — приток."
            : "Река: ставьте узлы кликами от истока к устью. Клик по руслу — рукав или приток, Enter — свободный конец, Esc — отменить.";
        RaiseRiverState();
    }

    public void StartLake()
    {
        CancelRiverGesture();
        ActiveTool = EditorTool.River;
        _riverCreating = true; _riverCreatingLake = true; _riverNode = -1;
        _waterSelection.Clear(); _selectedChannelId = null;
        StatusDetail = _riverDrawMode == RiverDrawMode.Freehand
            ? "Озеро: обведите берег одним движением — контур замкнётся сам. Esc — отмена."
            : "Озеро: ставьте вершины по контуру (нужно не меньше трёх), кнопка «Завершить (Enter)» замкнёт контур. Esc — отмена.";
        RaiseWaterState();
    }

    private RiverChannel? DraftChannel => _riverDraft?.Channels.FirstOrDefault(c => c.Id == _riverDraftChannelId);

    private bool BeginDraft(VectorPoint point, RiverJunction? source, River? sourceRiver)
    {
        var (layer, _) = EnsureObjectLayer();
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return false; }
        _riverLayer = layer.Id;
        var channel = _riverCreatingLake
            ? new RiverChannel { Name = "Озеро", Closed = true, Nodes = [new RiverNode(Guid.NewGuid(), point.X, point.Y, 1)] }
            : new RiverChannel { Name = "Русло 1", Nodes = [new RiverNode(Guid.NewGuid(), point.X, point.Y, _newRiverWidth)] };
        _riverDraftChannelId = channel.Id;
        if (source is not null && sourceRiver is not null)
        {
            // A distributary draft lives inside a copy of its parent river, so it previews attached.
            _riverStrokeSource = source; _riverStrokeSourceRiver = sourceRiver;
            _riverDraft = sourceRiver with { Channels = sourceRiver.Channels.Add(channel with { Name = $"Рукав {sourceRiver.Channels.Length}", Source = source }) };
        }
        else
        {
            _riverStrokeSource = null; _riverStrokeSourceRiver = null;
            _riverDraft = new River { Name = _riverCreatingLake ? EntityNameGenerator.Lake(Session.Document) : EntityNameGenerator.River(Session.Document),
                Channels = [channel], Style = Services.RegionalMapFactory.IsRegional(Session.Document)
                    ? Services.RegionalMapFactory.WaterStyle : RiverStyle.Natural() };
        }
        _riverNode = 0; RaiseRiverState(); return true;
    }

    public bool AddRiverNode(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0
            || x > Session.Document.Canvas.Width || y > Session.Document.Canvas.Height) return false;
        var point = SnapPlacementPoint(x, y);
        if (_riverDraft is null)
        {
            var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
            RiverJunction? source = null; River? sourceRiver = null;
            if (!_riverCreatingLake && HitRiver(x, y, tolerance, out var layer, out var river, out _, out var at) && layer.Visible && !layer.Locked)
            { source = at; sourceRiver = river; point = JunctionPoint(river, at); }
            return BeginDraft(point, source, sourceRiver);
        }
        var draftChannel = DraftChannel!;
        if (draftChannel.Nodes[^1] is { } last && last.X == point.X && last.Y == point.Y) return false;
        draftChannel = draftChannel with { Nodes = draftChannel.Nodes.Add(new RiverNode(Guid.NewGuid(), point.X, point.Y, _riverCreatingLake ? 1 : _newRiverWidth)) };
        _riverDraft = RiverEditor.ReplaceChannel(_riverDraft, draftChannel);
        _riverNode = draftChannel.Nodes.Length - 1;
        RaiseRiverState(); return true;
    }

    /// <summary>Enter: the drafted channel ends here as a free end (or the lake closes) and is committed.</summary>
    public bool FinishRiver()
    {
        var minimum = _riverCreatingLake ? 3 : 2;
        if (!_riverCreating || _riverDraft is null || DraftChannel is not { } channel || channel.Nodes.Length < minimum)
        { StatusDetail = _riverCreatingLake ? "Для озера нужны хотя бы три вершины." : "Для реки нужны хотя бы два разных узла."; return false; }
        return CommitDraft(channel, junction: null, targetLayer: null, targetRiver: null);
    }

    /// <summary>A click on an existing channel or lake while drafting: the draft flows into it.</summary>
    private bool FinishRiverInto(ObjectLayer layer, River target, RiverJunction junction) =>
        DraftChannel is { } channel && CommitDraft(channel, junction, layer, target);

    private bool CommitDraft(RiverChannel channel, RiverJunction? junction, ObjectLayer? targetLayer, River? targetRiver)
    {
        // The lake flag has to be read before the gesture is cancelled below: it used to be read
        // afterwards, which reported a freshly drawn lake as "Река создана".
        var creatingLake = _riverCreatingLake;
        var (layer, create) = EnsureObjectLayer();
        if (layer is null) { StatusDetail = NoUsableObjectLayer; return false; }
        if (targetLayer is not null && (!targetLayer.Visible || targetLayer.Locked)) { StatusDetail = "Слой реки скрыт или заблокирован."; return false; }
        var sourceRiver = _riverStrokeSourceRiver;
        River requested; Guid? absorbed = null; string name;
        if (targetRiver is not null)
        {
            var current = Rivers.FirstOrDefault(r => r.Id == targetRiver.Id) ?? targetRiver;
            var tributary = channel with { Name = $"Приток {current.Channels.Length}", Mouth = junction, Source = _riverStrokeSource };
            if (sourceRiver is not null && sourceRiver.Id != current.Id)
            {
                // The draft branches off one river and joins another: both become one water system.
                requested = RiverEditor.Absorb(current, sourceRiver with { Channels = sourceRiver.Channels }) with { Channels = [.. current.Channels, .. sourceRiver.Channels, tributary] };
                absorbed = sourceRiver.Id;
            }
            else requested = current with { Channels = current.Channels.Add(tributary) };
            name = "Добавить приток";
            layer = targetLayer!; create = null;
        }
        else if (sourceRiver is not null)
        {
            var current = Rivers.FirstOrDefault(r => r.Id == sourceRiver.Id) ?? sourceRiver;
            requested = current with { Channels = current.Channels.Add(channel with { Name = $"Рукав {current.Channels.Length}", Source = _riverStrokeSource }) };
            name = "Добавить рукав";
            layer = RiverLayerOf(current.Id) ?? layer; create = null;
        }
        else
        {
            requested = _riverDraft!;
            name = creatingLake ? "Создать озеро" : "Создать реку";
        }
        try { requested.Validate(); }
        catch (ArgumentException ex) { StatusDetail = ex.Message; return false; }
        // A new water body that the land clip would erase completely is created visible instead,
        // with the reason stated. Silently committing something the canvas never draws is the one
        // outcome the user cannot act on; the "Только по суше" toggle stays available to restore it.
        var clipped = RiverVisibility.IsHiddenByLandClip(Session.Document, requested);
        if (clipped) requested = requested with { Style = requested.Style with { ClipToLand = false } };
        var command = new RiverCommand(layer.Id, requested.Id, requested, name, absorbed);
        var revision = Session.Revision;
        ExecuteWithOptionalLayer(command, create, name);
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        CancelRiverGesture();
        _selectedRiverId = requested.Id; _selectedChannelId = channel.Id;
        _riverNode = _waterLevel == WaterSelectionLevel.Node ? channel.Nodes.Length - 1 : -1;
        _activeLayerId = layer.Id;
        Session.ClearSelection();
        AfterEdit(); RaiseWaterState();
        StatusDetail = targetRiver is not null
            ? "Приток присоединён. Перетаскивайте ромб устья вдоль русла, чтобы перенести место соединения."
            : creatingLake ? "Озеро создано и выбрано. Реки можно завершать на его берегу или начинать от него."
            : "Река создана и выбрана. Перетаскивайте узлы, задайте ширину или профиль истока/устья слева.";
        if (clipped)
            StatusDetail += creatingLake
                ? " Контур лежит вне суши, поэтому «Только по суше» для него выключено — иначе озера не было бы видно."
                : " Русло лежит вне суши, поэтому «Только по суше» для него выключено — иначе реки не было бы видно.";
        return true;
    }

    // ------------------------------------------------------------------ freehand stroke

    public bool BeginRiverStroke(double x, double y)
    {
        if (ActiveTool != EditorTool.River || !_riverCreating || _riverDrawMode != RiverDrawMode.Freehand || _riverDraft is not null) return false;
        if (!AddRiverNode(x, y)) return false;
        _riverStroking = true; _riverStroke = [new VectorPoint(x, y)];
        return true;
    }

    public void ContinueRiverStroke(double x, double y)
    {
        if (!_riverStroking || _riverStroke is null || _riverDraft is null) return;
        var point = new VectorPoint(Math.Clamp(x, 0, Session.Document.Canvas.Width), Math.Clamp(y, 0, Session.Document.Canvas.Height));
        var last = _riverStroke[^1];
        if (Math.Abs(last.X - point.X) + Math.Abs(last.Y - point.Y) < 2 / Math.Max(.01, Session.Viewport.Scale)) return;
        _riverStroke.Add(point);
        var channel = DraftChannel!;
        var nodes = RiverGeometry.Simplify(_riverStroke, 6 / Math.Max(.01, Session.Viewport.Scale));
        var first = channel.Nodes[0];
        var rebuilt = channel with { Nodes = [first, .. nodes.Skip(1).Select(p => new RiverNode(Guid.NewGuid(), p.X, p.Y, _riverCreatingLake ? 1 : _newRiverWidth))] };
        _riverDraft = RiverEditor.ReplaceChannel(_riverDraft, rebuilt);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool EndRiverStroke()
    {
        if (!_riverStroking || _riverStroke is not { Count: > 0 } stroke || DraftChannel is not { } channel) { _riverStroking = false; return false; }
        _riverStroking = false;
        var end = stroke[^1];
        if (channel.Nodes.Length < (_riverCreatingLake ? 3 : 2)) { CancelRiverGesture(); StatusDetail = "Слишком короткий штрих."; return false; }
        if (_riverCreatingLake) return FinishRiver();
        var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
        if (HitRiver(end.X, end.Y, tolerance, out var layer, out var river, out _, out var junction)
            && (_riverStrokeSourceRiver is null || river.Id != _riverStrokeSourceRiver.Id || CanJoin(river, channel, junction)))
            return FinishRiverInto(layer, river, junction);
        return FinishRiver();
    }

    private bool CanJoin(River river, RiverChannel channel, RiverJunction junction) =>
        _riverStrokeSource is not { } source || !river.DependsOn(junction.ParentChannelId, source.ParentChannelId) || junction.ParentChannelId != channel.Id;

    // ------------------------------------------------------------------ canvas

    public bool PressRiver(double x, double y)
    {
        if (ActiveTool != EditorTool.River) return false;
        var tolerance = 9 / Math.Max(.01, Session.Viewport.Scale);
        if (_riverCreating)
        {
            if (_riverDrawMode == RiverDrawMode.Freehand && !_riverCreatingLake) return false;
            var draftCount = DraftChannel?.Nodes.Length ?? 0;
            if (!_riverCreatingLake && draftCount >= 1 && HitRiver(x, y, tolerance, out var targetLayer, out var target, out _, out var junction))
            {
                if (_riverStrokeSourceRiver is not null && target.Id == _riverStrokeSourceRiver.Id && !CanJoin(target, DraftChannel!, junction))
                { StatusDetail = "Рукав не может впадать в собственное течение."; return true; }
                FinishRiverInto(targetLayer, target, junction); return true;
            }
            AddRiverNode(x, y); return true;
        }
        if (SelectedRiver is { Locked: false } selected && _waterLevel == WaterSelectionLevel.Node
            && RiverLayerOf(selected.Id) is { Visible: true, Locked: false } layer)
        {
            var geometry = RiverGeometry.Evaluate(selected);
            if (SelectedRiverChannel is { } channel && geometry.Channels.TryGetValue(channel.Id, out var g))
            {
                if (channel.Mouth is not null && Math.Abs(g.End.X - x) <= tolerance && Math.Abs(g.End.Y - y) <= tolerance)
                { _riverOriginal = selected; _riverDraft = selected; _riverDraggingMouth = true; _riverLayer = layer.Id; RaiseRiverState(); return true; }
                if (channel.Source is not null && Math.Abs(g.Start.X - x) <= tolerance && Math.Abs(g.Start.Y - y) <= tolerance)
                { _riverOriginal = selected; _riverDraft = selected; _riverDraggingSource = true; _riverLayer = layer.Id; RaiseRiverState(); return true; }
                for (var i = 0; i < channel.Nodes.Length; i++)
                    if (Math.Abs(channel.Nodes[i].X - x) <= tolerance && Math.Abs(channel.Nodes[i].Y - y) <= tolerance)
                    {
                        _riverNode = i; _riverOriginal = selected; _riverDraft = selected; _riverDragging = true; _riverLayer = layer.Id;
                        RaiseRiverState(); return true;
                    }
                if (_riverInsert && RiverGeometry.TryClosest(geometry, x, y, tolerance * 2, out var onChannel, out var segment, out var fraction, out _, out _, c => c.Id == channel.Id))
                {
                    _riverInsert = false;
                    var seg = Math.Max(0, segment);
                    var point = RiverGeometry.PointAt(geometry.Channels[onChannel], seg, fraction);
                    var next = RiverEditor.InsertNode(selected, channel.Id, seg, fraction, point);
                    _riverNode = seg + 1;
                    return CommitRiver(next, "Добавить узел реки");
                }
            }
            foreach (var other in selected.Channels)
                for (var i = 0; i < other.Nodes.Length; i++)
                    if (Math.Abs(other.Nodes[i].X - x) <= tolerance && Math.Abs(other.Nodes[i].Y - y) <= tolerance)
                    {
                        _selectedChannelId = other.Id; _riverNode = i; _riverOriginal = selected; _riverDraft = selected;
                        _riverDragging = true; _riverLayer = layer.Id; RaiseRiverState(); return true;
                    }
        }
        if (SelectWaterAt(x, y, Session.Viewport.Scale, additive: false))
        {
            if (_waterLevel == WaterSelectionLevel.Node && SelectedRiver is { Locked: false } picked
                && RiverLayerOf(picked.Id) is { Visible: true, Locked: false } pickedLayer)
            {
                _riverOriginal = picked; _riverDraft = picked; _riverDragging = true; _riverLayer = pickedLayer.Id;
            }
            else if (SelectedRiver is { Locked: false } && BeginWaterDrag(x, y)) { /* the whole system follows the pointer */ }
            RaiseRiverState();
            return true;
        }
        return false;
    }

    public void MoveRiverPointer(double x, double y)
    {
        if (IsDraggingWater) { UpdateWaterDrag(x, y); return; }
        if (_riverDraft is null || SelectedRiverChannel is not { } channel) return;
        var point = SnapPlacementPoint(Math.Clamp(x, 0, Session.Document.Canvas.Width), Math.Clamp(y, 0, Session.Document.Canvas.Height));
        if (_riverDragging && _riverNode >= 0 && _riverNode < channel.Nodes.Length)
        {
            var node = channel.Nodes[_riverNode];
            if (node.X == point.X && node.Y == point.Y) return;
            _riverDraft = RiverEditor.MoveNode(_riverDraft, channel.Id, _riverNode, point.X, point.Y);
        }
        else if (_riverDraggingMouth || _riverDraggingSource)
        {
            var set = RiverGeometry.Evaluate(_riverDraft);
            var draftRiver = _riverDraft;
            if (RiverGeometry.TryClosest(set, x, y, double.MaxValue, out var parentId, out _, out _, out _, out _,
                    c => c.Id != channel.Id && !draftRiver.DependsOn(c.Id, channel.Id))
                && RiverEditor.ClosestJunction(set, set.Channels[parentId], x, y) is { } junction)
                _riverDraft = _riverDraggingMouth ? RiverEditor.SetMouth(_riverDraft, channel.Id, junction) : RiverEditor.SetSource(_riverDraft, channel.Id, junction);
        }
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public bool EndRiverDrag()
    {
        if (IsDraggingWater) return EndWaterDrag();
        if (_riverDraft is not { } draft || (!_riverDragging && !_riverDraggingMouth && !_riverDraggingSource)) return false;
        var name = _riverDraggingMouth ? "Перенести устье притока" : _riverDraggingSource ? "Перенести исток рукава" : "Переместить узел реки";
        var changed = _riverOriginal is { } original && !SameRiver(original, draft);
        var layer = _riverLayer;
        CancelRiverGesture();
        return changed && CommitRiver(draft, name, layer);
    }

    public bool CancelRiverGesture()
    {
        CancelWaterDrag();
        var active = _riverDraft is not null || _riverInsert || _riverCreating;
        _riverPreviewing = false;
        _riverDraft = _riverOriginal = null; _riverDraftChannelId = null; _riverStroke = null;
        _riverStrokeSource = null; _riverStrokeSourceRiver = null;
        _riverCreating = _riverCreatingLake = _riverDragging = _riverDraggingMouth = _riverDraggingSource = _riverInsert = _riverStroking = false; _riverLayer = null;
        if (active) { RaiseRiverState(); InvalidateScene(EditorChangeReason.CanvasInvalidation); }
        return active;
    }

    public void SelectRiver(Guid id, Guid? channelId = null)
    {
        CancelRiverGesture();
        var river = Rivers.FirstOrDefault(r => r.Id == id);
        if (river is null) return;
        ActiveTool = EditorTool.River;
        _selectedRiverId = id; _selectedChannelId = channelId ?? river.Channels[0].Id;
        _riverNode = _waterLevel == WaterSelectionLevel.Node ? 0 : -1;
        _activeLayerId = RiverLayerOf(id)?.Id ?? _activeLayerId;
        Session.ClearSelection(); RaiseWaterState();
    }

    /// <summary>Selects one water system without changing the active tool: used by the object list.</summary>
    public void SelectWater(Guid id, bool additive = false)
    {
        var river = Rivers.FirstOrDefault(r => r.Id == id);
        if (river is null) return;
        CancelRiverGesture();
        if (additive && _waterSelection.Contains(id)) _waterSelection.Remove(id);
        else
        {
            if (!additive)
            {
                _waterSelection.Clear();
                if (Session.SelectedObjectIds.Count > 0) { Session.ClearSelection(); AfterSelectionChange(); }
            }
            if (!_waterSelection.Contains(id)) _waterSelection.Add(id);
            _selectedChannelId = river.Channels[0].Id;
        }
        _riverNode = -1;
        _activeLayerId = RiverLayerOf(id)?.Id ?? _activeLayerId;
        RaiseWaterState();
    }

    public void SelectRiverChannel(Guid channelId)
    {
        if (SelectedRiver?.FindChannel(channelId) is null) return;
        _selectedChannelId = channelId; _riverNode = 0; RaiseRiverState();
    }

    public void SelectRiverNode(int index)
    {
        if (SelectedRiverChannel is not { } c || index < 0 || index >= c.Nodes.Length) return;
        _riverNode = index; RaiseRiverState();
    }

    // ------------------------------------------------------------------ node and channel edits

    public void InsertRiverNode()
    { if (SelectedRiver is null) return; _riverInsert = true; StatusDetail = "Щёлкните по руслу, чтобы добавить узел в этом месте."; }

    public bool DeleteSelectedRiverNode()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { } channel || _riverNode < 0 || _riverNode >= channel.Nodes.Length) return false;
        River next;
        try { next = RiverEditor.DeleteNode(river, channel.Id, _riverNode); }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; return false; }
        _riverNode = Math.Min(_riverNode, next.FindChannel(channel.Id)!.Nodes.Length - 1);
        return CommitRiver(next, "Удалить узел реки");
    }

    public void SetSelectedRiverNodeWidth(double width)
    {
        if (!double.IsFinite(width) || width < River.MinimumWidth || width > River.MaximumWidth) { StatusDetail = "Ширина должна быть числом не меньше 1."; return; }
        if (SelectedRiver is { } river && SelectedRiverChannel is { Closed: false } channel && _riverNode >= 0 && _riverNode < channel.Nodes.Length)
            CommitRiver(RiverEditor.SetNodeWidth(river, channel.Id, _riverNode, width), "Изменить ширину реки в узле");
    }

    /// <summary>
    /// Direct curvature control: the node keeps its place while the bend through it opens or
    /// tightens. This is what replaces smoothing again and again in the hope of a nicer curve:
    /// not a single node is added.
    /// </summary>
    public void SetSelectedRiverNodeTensionPercent(double percent)
    {
        if (!double.IsFinite(percent)) { StatusDetail = "Кривизна узла задаётся в процентах."; return; }
        if (SelectedRiver is { } river && SelectedRiverChannel is { } channel && _riverNode >= 0 && _riverNode < channel.Nodes.Length)
            CommitRiver(RiverEditor.SetNodeTension(river, channel.Id, _riverNode, percent / 100), "Кривизна узла реки");
    }

    /// <summary>The same handle applied to every node of the channel: one smoothness dial.</summary>
    public void SetSelectedChannelTensionPercent(double percent)
    {
        if (!double.IsFinite(percent)) { StatusDetail = "Плавность русла задаётся в процентах."; return; }
        if (SelectedRiver is { } river && SelectedRiverChannel is { } channel)
            CommitRiver(RiverEditor.SetChannelTension(river, channel.Id, percent / 100), "Плавность русла");
    }

    /// <summary>
    /// Removes the nodes a freehand stroke does not need, with the accuracy stated in world units:
    /// no part of the course moves further than the tolerance.
    /// </summary>
    public void SimplifySelectedChannel()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { } channel) return;
        var before = channel.Nodes.Length;
        var next = RiverEditor.SimplifyChannel(river, channel.Id, _riverSimplifyTolerance);
        if (next.FindChannel(channel.Id) is not { } simplified || simplified.Nodes.Length == before)
        { StatusDetail = $"Упрощать нечего: точность {_riverSimplifyTolerance:0.#} ед. уже соблюдена при {before} узлах."; return; }
        if (CommitRiver(next, "Упростить русло"))
        {
            _riverNode = Math.Clamp(_riverNode, -1, simplified.Nodes.Length - 1);
            StatusDetail = $"Узлов: {before} → {simplified.Nodes.Length}; курс сохранён с точностью {_riverSimplifyTolerance:0.#} ед.";
            RaiseRiverState();
        }
    }

    public void SetSelectedRiverNodeSharp(bool sharp)
    {
        if (SelectedRiver is { } river && SelectedRiverChannel is { } channel && _riverNode >= 0 && _riverNode < channel.Nodes.Length)
            CommitRiver(RiverEditor.SetNodeSharp(river, channel.Id, _riverNode, sharp), sharp ? "Острый узел реки" : "Плавный узел реки");
    }

    public void SetSelectedChannelWidthProfile(double sourceWidth, double mouthWidth)
    {
        if (!double.IsFinite(sourceWidth) || !double.IsFinite(mouthWidth) || sourceWidth < River.MinimumWidth || mouthWidth < River.MinimumWidth
            || sourceWidth > River.MaximumWidth || mouthWidth > River.MaximumWidth)
        { StatusDetail = "Ширины истока и устья должны быть числами не меньше 1."; return; }
        if (SelectedRiver is { } river && SelectedRiverChannel is { Closed: false } channel)
            CommitRiver(RiverEditor.SetWidthProfile(river, channel.Id, sourceWidth, mouthWidth), "Профиль ширины русла");
    }

    public void SetSelectedChannelSourceWidth(double width) => SetSelectedChannelWidthProfile(width, SelectedChannelMouthWidth ?? width);
    public void SetSelectedChannelMouthWidth(double width) => SetSelectedChannelWidthProfile(SelectedChannelSourceWidth ?? width, width);

    /// <summary>Splits the selected channel at the selected node; both halves stay connected.</summary>
    public void SplitSelectedChannel()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { } channel) return;
        try
        {
            var next = RiverEditor.SplitChannel(river, channel.Id, _riverNode, out var downstream);
            if (CommitRiver(next, "Разделить русло")) { _selectedChannelId = downstream; _riverNode = 0; RaiseRiverState(); }
        }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; }
    }

    /// <summary>Cuts the selected channel at the selected node; the downstream part becomes its own river.</summary>
    public void DetachDownstreamAsRiver()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { } channel || RiverLayerOf(river.Id) is not { Visible: true, Locked: false } layer) return;
        try
        {
            var (remaining, detached) = RiverEditor.SplitRiver(river, channel.Id, _riverNode, EntityNameGenerator.River(Session.Document));
            remaining.Validate(); detached.Validate();
            var revision = Session.Revision;
            Session.Execute(new RiverCommand(layer.Id, river.Id, remaining, "Отделить нижнее течение в отдельную реку", additionalRiver: detached));
            if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return; }
            _selectedRiverId = detached.Id; _selectedChannelId = detached.Channels[0].Id; _riverNode = 0;
            AfterEdit(geometryOnly: true); RaiseRiverState();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { StatusDetail = ex.Message; }
    }

    public void TrimSelectedChannelBeforeNode() => TrimSelected(_riverNode, (SelectedRiverChannel?.Nodes.Length ?? 1) - 1, "Обрезать русло выше узла");
    public void TrimSelectedChannelAfterNode() => TrimSelected(0, _riverNode, "Обрезать русло ниже узла");

    private void TrimSelected(int from, int to, string name)
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { } channel) return;
        try { if (CommitRiver(RiverEditor.Trim(river, channel.Id, from, to), name)) { _riverNode = Math.Clamp(_riverNode - from, 0, to - from); RaiseRiverState(); } }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; }
    }

    public void ReverseSelectedChannel()
    {
        if (SelectedRiver is { } river && SelectedRiverChannel is { Closed: false } channel)
            CommitRiver(RiverEditor.Reverse(river, channel.Id), "Развернуть течение");
    }

    /// <summary>Relaxes the course in place; the node count is exactly the same afterwards.</summary>
    public void SmoothSelectedChannel()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { } channel) return;
        var before = channel.Nodes.Length;
        if (CommitRiver(RiverEditor.Smooth(river, channel.Id), "Сгладить русло"))
            StatusDetail = $"Русло сглажено; узлов по-прежнему {before}. Форму изгиба меняйте кривизной узла.";
    }

    /// <summary>
    /// Shows the meanders on the canvas without touching the map: the node count of the result is
    /// named up front, so an operation that does multiply points is a decision and not a surprise.
    /// </summary>
    public void PreviewMeanderOnSelectedChannel()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { Closed: false } channel) return;
        if (RiverLayerOf(river.Id) is not { Visible: true, Locked: false } layer) { StatusDetail = "Слой реки скрыт или заблокирован."; return; }
        CancelRiverGesture();
        _riverMeanderSeed = NextGestureSeed();
        River next;
        try { next = RiverEditor.Meander(river, channel.Id, _riverMeanderPercent / 100, _riverMeanderSeed); }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; return; }
        _riverOriginal = river; _riverDraft = next; _riverLayer = layer.Id; _riverPreviewing = true;
        StatusDetail = $"Предпросмотр извилин: узлов станет {next.FindChannel(channel.Id)!.Nodes.Length} вместо {channel.Nodes.Length}. "
            + "«Применить извилины» — принять, Esc — отказаться.";
        RaiseRiverState();
    }

    /// <summary>Commits whatever the preview shows as one undoable change.</summary>
    public bool ApplyRiverPreview()
    {
        if (!HasRiverPreview) return false;
        var draft = _riverDraft!; var layer = _riverLayer;
        _riverPreviewing = false;
        CancelRiverGesture();
        return CommitRiver(draft, "Добавить извилины", layer);
    }

    public void MeanderSelectedChannel() => PreviewMeanderOnSelectedChannel();

    public void ApplyWidthByFlow()
    {
        if (SelectedRiver is { } river)
            CommitRiver(RiverEditor.WidthByFlow(river, _riverFlowSourceWidth, _riverFlowGrowth), "Ширина по притокам");
    }

    public void DetachSelectedChannel()
    {
        if (SelectedRiver is { } river && SelectedRiverChannel is { Mouth: not null } channel)
            CommitRiver(RiverEditor.SetMouth(river, channel.Id, null), "Отделить устье");
    }

    public void DetachSelectedChannelSource()
    {
        if (SelectedRiver is { } river && SelectedRiverChannel is { Source: not null } channel)
            CommitRiver(RiverEditor.SetSource(river, channel.Id, null), "Отделить исток");
    }

    public void DeleteSelectedChannel()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { } channel) return;
        if (river.Channels.Length <= 1) { DeleteSelectedRiver(); return; }
        var next = RiverEditor.DeleteChannel(river, channel.Id);
        _selectedChannelId = next.Channels[0].Id; _riverNode = 0;
        CommitRiver(next, "Удалить русло");
    }

    public void DeleteSelectedRiver()
    {
        if (SelectedRiver is not { } river || RiverLayerOf(river.Id) is not { } layer) return;
        var revision = Session.Revision;
        Session.Execute(new RiverCommand(layer.Id, river.Id, null, "Удалить реку"));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return; }
        _selectedRiverId = null; _selectedChannelId = null; _riverNode = -1;
        AfterEdit(geometryOnly: true); RaiseRiverState();
    }

    public void SetRiverName(string name)
    {
        if (SelectedRiver is { } river && !string.IsNullOrWhiteSpace(name) && name.Trim() != river.Name)
            CommitRiver(river with { Name = name.Trim() }, "Переименовать реку");
    }

    public void SetRiverWidthScalePercent(double percent)
    {
        if (!double.IsFinite(percent) || percent is < 1 or > 10000) { StatusDetail = "Масштаб ширины: от 1 до 10000 %."; return; }
        if (SelectedRiver is { } river) CommitRiver(river with { WidthScale = percent / 100 }, "Масштаб ширины реки");
    }

    public string? RiverWaterColor => SelectedRiver?.Style.WaterColor;
    public string? RiverBankColor => SelectedRiver?.Style.BankColor;
    public string? RiverHighlightColor => SelectedRiver?.Style.HighlightColor;
    public void SetRiverWaterColor(string color) => SetRiverStyle(s => s with { WaterColor = AppearanceColor.Normalize(color) ?? s.WaterColor }, "Цвет воды");
    public void SetRiverBankColor(string color) => SetRiverStyle(s => s with { BankColor = AppearanceColor.Normalize(color) ?? s.BankColor }, "Цвет берега");
    public void SetRiverHighlightColor(string color) => SetRiverStyle(s => s with { HighlightColor = AppearanceColor.Normalize(color) ?? s.HighlightColor }, "Цвет бликов");
    public void SetRiverBankWidth(double width) => SetRiverStyle(s => s with { BankWidth = Math.Clamp(width, 0, 64) }, "Толщина берега");
    public void ApplyIllustratedRiverStyle(bool pen) => SetRiverStyle(s => RiverStyle.Illustrated(pen) with
    { Opacity = s.Opacity, CutsLand = s.CutsLand, ClipToLand = s.ClipToLand, TaperSource = s.TaperSource, MouthFade = s.MouthFade },
        pen ? "Река: атлас пером" : "Река: рисованная карта");
    public void SetRiverBankReliefWidth(double width) => SetRiverStyle(s => s with { BankReliefWidth = Math.Clamp(width, 0, 128) }, "Ширина откоса");
    public void SetRiverBankReliefColor(string color) => SetRiverStyle(s => s with { BankReliefColor = AppearanceColor.Normalize(color) ?? s.BankReliefColor }, "Цвет откоса");
    public void SetRiverBankHatchPercent(double percent) => SetRiverStyle(s => s with { BankHatchOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Штриховка берега");
    public void SetRiverBankShadowPercent(double percent) => SetRiverStyle(s => s with { BankShadowOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Тень берега");
    public void SetRiverHighlightPercent(double percent) => SetRiverStyle(s => s with { HighlightOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Блики реки");
    public void SetRiverOpacityPercent(double percent) => SetRiverStyle(s => s with { Opacity = Math.Clamp(percent, 0, 100) / 100 }, "Непрозрачность реки");
    public void SetRiverTaperSource(bool taper) => SetRiverStyle(s => s with { TaperSource = taper }, "Сужение истока");
    public void SetRiverMouthFade(double length) => SetRiverStyle(s => s with { MouthFade = Math.Clamp(length, 0, River.MaximumWidth) }, "Растворение устья");
    public void SetRiverEdgeRoughnessPercent(double percent) => SetRiverStyle(s => s with { EdgeRoughness = Math.Clamp(percent, 0, 100) / 100 }, "Шероховатость кромки");
    public string? RiverTextureId => SelectedRiver?.Style.TextureId;
    public double? RiverTextureOpacityPercent => SelectedRiver is { } r ? r.Style.TextureOpacity * 100 : null;
    public double? RiverTextureScale => SelectedRiver?.Style.TextureScale;
    public double? RiverShallowWidth => SelectedRiver?.Style.ShallowWidth;
    public string? RiverShallowColor => SelectedRiver?.Style.ShallowColor;

    /// <summary>Picks one of Map's own water materials, or none for a flat fill.</summary>
    public void SetRiverTexture(string? id) => SetRiverStyle(
        s => s with { TextureId = MapFillTextures.Find(id) is { Kind: MapFillTextureKind.Water } water ? water.Id : null },
        id is null ? "Убрать текстуру воды" : "Текстура воды");

    public void SetRiverTextureOpacityPercent(double percent) =>
        SetRiverStyle(s => s with { TextureOpacity = Math.Clamp(percent, 0, 100) / 100 }, "Сила текстуры воды");

    public void SetRiverTextureScale(double scale) => SetRiverStyle(
        s => s with { TextureScale = Math.Clamp(scale, MapFillTextures.MinimumScale, MapFillTextures.MaximumScale) },
        "Размер текстуры воды");

    public void SetRiverShallowWidth(double width) =>
        SetRiverStyle(s => s with { ShallowWidth = Math.Clamp(width, 0, 512) }, "Отмель у берега");

    public void SetRiverShallowColor(string color) =>
        SetRiverStyle(s => s with { ShallowColor = AppearanceColor.Normalize(color) ?? s.ShallowColor }, "Цвет отмели");

    public void SetRiverCutsLand(bool cuts) => SetRiverStyle(s => s with { CutsLand = cuts }, cuts ? "Формировать русло и берега" : "Река поверх земли");
    public void SetRiverClipToLand(bool clip) => SetRiverStyle(s => s with { ClipToLand = clip }, clip ? "Река только по суше" : "Река и над морем");

    private void SetRiverStyle(Func<RiverStyle, RiverStyle> change, string name)
    {
        if (SelectedRiver is not { } river) return;
        var style = change(river.Style);
        if (style == river.Style) return;
        CommitRiver(river with { Style = style }, name);
    }

    // ------------------------------------------------------------------ plumbing

    private ObjectLayer? RiverLayerOf(Guid riverId) =>
        Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l => l.Rivers.Any(r => r.Id == riverId));

    private static VectorPoint JunctionPoint(River river, RiverJunction junction)
    {
        var set = RiverGeometry.Evaluate(river);
        return set.Channels.TryGetValue(junction.ParentChannelId, out var parent) && RiverGeometry.TryJunction(parent, junction, out var point)
            ? new VectorPoint(point.X, point.Y) : new VectorPoint(0, 0);
    }

    private bool HitRiver(double x, double y, double tolerance, out ObjectLayer layer, out River river, out Guid channelId, out RiverJunction junction)
    {
        foreach (var candidateLayer in Session.Document.Layers.OfType<ObjectLayer>().Reverse().Where(l => l.Visible))
            foreach (var candidate in candidateLayer.Rivers.Reverse())
            {
                if (!candidate.Visible) continue;
                var set = RiverGeometry.Evaluate(candidate);
                if (!set.Bounds.Intersects(new WorldBounds(x - tolerance, y - tolerance, x + tolerance, y + tolerance))) continue;
                Guid hitChannel;
                if (!RiverGeometry.TryClosest(set, x, y, tolerance, out hitChannel, out _, out _, out _, out _))
                {
                    var lake = set.Channels.Values.FirstOrDefault(g => RiverGeometry.InsideLake(g, x, y));
                    if (lake is null) continue;
                    hitChannel = lake.Channel.Id;
                }
                if (RiverEditor.ClosestJunction(set, set.Channels[hitChannel], x, y) is not { } at) continue;
                layer = candidateLayer; river = candidate; channelId = hitChannel; junction = at;
                return true;
            }
        layer = null!; river = null!; channelId = Guid.Empty; junction = null!;
        return false;
    }

    private bool CommitRiver(River next, string name, LayerId? layerId = null, Guid? absorbed = null)
    {
        var layer = layerId is { } id ? Session.Document.FindLayer(id) as ObjectLayer : RiverLayerOf(next.Id);
        if (layer is not { Visible: true, Locked: false }) { StatusDetail = "Слой реки скрыт или заблокирован."; return false; }
        if (SelectedRiver is { } current && SameRiver(current, next)) return false;
        var revision = Session.Revision;
        Session.Execute(new RiverCommand(layer.Id, next.Id, next, name, absorbed));
        if (revision == Session.Revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; RaiseRiverState(); return false; }
        _selectedRiverId = next.Id;
        if (SelectedRiverChannel is null) { _selectedChannelId = next.Channels[0].Id; _riverNode = 0; }
        AfterEdit(geometryOnly: true); RaiseRiverState(); return true;
    }

    /// <summary>Content equality: record equality of ImmutableArray members compares references only.</summary>
    private static bool SameRiver(River a, River b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Id != b.Id || a.Name != b.Name || a.Style != b.Style || a.WidthScale != b.WidthScale || a.Channels.Length != b.Channels.Length) return false;
        for (var i = 0; i < a.Channels.Length; i++)
        {
            var x = a.Channels[i]; var y = b.Channels[i];
            if (x.Id != y.Id || x.Name != y.Name || x.Mouth != y.Mouth || x.Source != y.Source || x.Closed != y.Closed || !x.Nodes.SequenceEqual(y.Nodes)) return false;
        }
        return true;
    }

    private void RaiseRiverState()
    {
        Raise(nameof(Rivers)); Raise(nameof(SelectedRiver)); Raise(nameof(SelectedRiverChannel)); Raise(nameof(RiverSummary));
        Raise(nameof(IsCreatingRiver)); Raise(nameof(IsCreatingLake)); Raise(nameof(SelectedRiverNodeWidth)); Raise(nameof(SelectedRiverNodeSharp));
        Raise(nameof(SelectedChannelSourceWidth)); Raise(nameof(SelectedChannelMouthWidth)); Raise(nameof(SelectedChannelIsTributary));
        Raise(nameof(SelectedChannelHasSource)); Raise(nameof(SelectedChannelIsLake)); Raise(nameof(CanSplitSelectedChannel)); Raise(nameof(RiverDrawMode));
        Raise(nameof(SelectedRiverNodeTensionPercent)); Raise(nameof(SelectedChannelTensionPercent)); Raise(nameof(HasRiverPreview));
        Raise(nameof(RiverTextureId)); Raise(nameof(RiverTextureOpacityPercent)); Raise(nameof(RiverTextureScale));
        Raise(nameof(RiverShallowWidth)); Raise(nameof(RiverShallowColor));
        Raise(nameof(WaterSelectionSummary)); Raise(nameof(SelectedWaterVisible)); Raise(nameof(SelectedWaterLocked));
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }
}
