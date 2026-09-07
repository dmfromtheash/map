using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// What a Delete, a drag or a panel action addresses in a water system. Rivers are one connected
/// construction, so the thing under the cursor is genuinely ambiguous: the whole system, one of
/// its channels (a lake being a closed one) or a single node. The level is explicit and visible
/// instead of being guessed from the active tool.
/// </summary>
public enum WaterSelectionLevel
{
    /// <summary>The whole water system: every channel, lake, distributary and tributary of it.</summary>
    System,

    /// <summary>One channel or lake of the selected system.</summary>
    Channel,

    /// <summary>One node of the selected channel; the node stays part of its channel.</summary>
    Node,
}

public sealed partial class EditorViewModel
{
    private readonly List<Guid> _waterSelection = [];
    private WaterSelectionLevel _waterLevel = WaterSelectionLevel.System;
    private (double X, double Y)? _waterDragFrom;
    private Dictionary<Guid, River>? _waterDragDrafts;
    private bool _waterDragMoved;

    /// <summary>Ids of the selected water systems, in selection order; the last one is primary.</summary>
    public IReadOnlyList<Guid> WaterSelectionIds => _waterSelection;

    public int WaterSelectionCount => _waterSelection.Count;

    public IReadOnlyList<River> SelectedWaters
    {
        get
        {
            var all = Rivers;
            var result = new List<River>(_waterSelection.Count);
            foreach (var id in _waterSelection)
                if (all.FirstOrDefault(r => r.Id == id) is { } river) result.Add(river);
            return result;
        }
    }

    public WaterSelectionLevel WaterLevel
    {
        get => _waterLevel;
        set
        {
            if (_waterLevel == value) return;
            _waterLevel = value;
            if (value != WaterSelectionLevel.System && SelectedRiver is { } river && SelectedRiverChannel is null)
                _selectedChannelId = river.Channels[0].Id;
            if (value == WaterSelectionLevel.Node && _riverNode < 0 && SelectedRiverChannel is { } channel && channel.Nodes.Length > 0)
                _riverNode = 0;
            Raise(nameof(WaterLevel)); Raise(nameof(IsWaterNodeMode)); RaiseWaterState();
        }
    }

    /// <summary>Node editing is a mode, not a side effect of having the river tool open.</summary>
    public bool IsWaterNodeMode => _waterLevel == WaterSelectionLevel.Node;

    public string WaterSelectionSummary
    {
        get
        {
            var waters = SelectedWaters;
            if (waters.Count == 0) return "Вода не выбрана.";
            if (waters.Count > 1) return "Выбрано водных систем: " + waters.Count + ".";
            var river = waters[0];
            var channel = SelectedRiverChannel;
            return _waterLevel switch
            {
                WaterSelectionLevel.Node when channel is not null && _riverNode >= 0 =>
                    $"«{river.Name}» · {channel.Name} · узел {_riverNode + 1} из {channel.Nodes.Length}",
                WaterSelectionLevel.Channel when channel is not null =>
                    $"«{river.Name}» · {(channel.Closed ? "озеро" : "русло")} «{channel.Name}»",
                _ => $"«{river.Name}» · русел и озёр: {river.Channels.Length}",
            };
        }
    }

    /// <summary>Combined world bounds of the selected water, for the selection frame on canvas.</summary>
    public WorldBounds? WaterSelectionFrame
    {
        get
        {
            WorldBounds? frame = null;
            foreach (var river in SelectedWaters)
            {
                var shown = _waterDragDrafts is { } drafts && drafts.TryGetValue(river.Id, out var draft) ? draft : river;
                var bounds = RiverGeometry.Evaluate(shown).Bounds;
                frame = frame is { } current
                    ? new WorldBounds(Math.Min(current.Left, bounds.Left), Math.Min(current.Top, bounds.Top),
                        Math.Max(current.Right, bounds.Right), Math.Max(current.Bottom, bounds.Bottom))
                    : bounds;
            }
            return frame;
        }
    }

    // ---------------------------------------------------------------- hit testing and picking

    /// <summary>
    /// The topmost visible water body under the point, by its drawn body and not only near its
    /// centreline. A hidden river and a hidden layer are skipped; a locked one is still reported
    /// so the refusal can name it.
    /// </summary>
    public bool HitWater(double x, double y, double tolerance, out ObjectLayer layer, out River river, out Guid channelId)
    {
        foreach (var candidateLayer in Session.Document.Layers.OfType<ObjectLayer>().Reverse().Where(l => l.Visible && l.Opacity > 0))
            foreach (var candidate in candidateLayer.Rivers.Reverse())
            {
                if (!candidate.Visible) continue;
                var set = RiverGeometry.Evaluate(candidate);
                if (!set.Bounds.Intersects(new WorldBounds(x - tolerance, y - tolerance, x + tolerance, y + tolerance))) continue;
                if (RiverGeometry.TryClosest(set, x, y, tolerance, out var hit, out _, out _, out _, out _))
                { layer = candidateLayer; river = candidate; channelId = hit; return true; }
                if (set.Channels.Values.FirstOrDefault(g => RiverGeometry.InsideLake(g, x, y)) is { } lake)
                { layer = candidateLayer; river = candidate; channelId = lake.Channel.Id; return true; }
            }
        layer = null!; river = null!; channelId = Guid.Empty;
        return false;
    }

    /// <summary>
    /// A plain canvas click on water: selects the system (or extends the selection with Ctrl) and
    /// keeps the channel under the cursor as the active one, so switching to the channel or node
    /// level needs no second pick. Returns false when there is no water under the point.
    /// </summary>
    public bool SelectWaterAt(double worldX, double worldY, double viewScale, bool additive)
    {
        var tolerance = 6 / Math.Max(.01, viewScale);
        if (!HitWater(worldX, worldY, tolerance, out var layer, out var river, out var channelId)) return false;
        if (additive && _waterSelection.Contains(river.Id))
        {
            _waterSelection.Remove(river.Id);
            if (_waterSelection.Count == 0) { _selectedChannelId = null; _riverNode = -1; }
            RaiseWaterState();
            return true;
        }
        if (!additive && _waterSelection.Count > 1 && _waterSelection.Contains(river.Id))
        {
            // A press inside an existing multi-selection keeps it, exactly like the object frame
            // does: the gesture that follows moves everything that is selected.
            _selectedChannelId = channelId;
            _riverNode = -1;
            StatusDetail = WaterSelectionSummary;
            RaiseWaterState();
            return true;
        }
        if (!additive)
        {
            _waterSelection.Clear();
            if (Session.SelectedObjectIds.Count > 0)
            {
                Session.ClearSelection();
                AfterSelectionChange();
            }
        }
        _waterSelection.Add(river.Id);
        _selectedChannelId = channelId;
        _riverNode = _waterLevel == WaterSelectionLevel.Node ? NearestNode(river, channelId, worldX, worldY) : -1;
        _activeLayerId = layer.Id;
        StatusDetail = river.Locked
            ? $"«{river.Name}» заблокирована: снимите блокировку, чтобы менять её."
            : WaterSelectionSummary;
        RaiseWaterState();
        return true;
    }

    private static int NearestNode(River river, Guid channelId, double x, double y)
    {
        if (river.FindChannel(channelId) is not { } channel || channel.Nodes.Length == 0) return -1;
        var best = 0; var bestDistance = double.MaxValue;
        for (var i = 0; i < channel.Nodes.Length; i++)
        {
            var node = channel.Nodes[i];
            var d = (node.X - x) * (node.X - x) + (node.Y - y) * (node.Y - y);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>Water systems whose drawn body meets the marquee, for the rubber-band selection.</summary>
    internal IReadOnlyList<Guid> WatersIn(WorldBounds marquee)
    {
        var found = new List<Guid>();
        foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>().Where(l => l.Visible && l.Opacity > 0))
            foreach (var river in layer.Rivers)
            {
                if (!river.Visible || river.Locked) continue;
                var set = RiverGeometry.Evaluate(river);
                if (!set.Bounds.Intersects(marquee)) continue;
                // By the drawn body, not by the centreline: a band over the visible edge of a wide
                // river selects it even when it never reaches the middle of the channel.
                if (RiverGeometry.IntersectsRect(set, marquee)) found.Add(river.Id);
            }
        return found;
    }

    internal void AddWaterSelection(IReadOnlyList<Guid> ids, bool additive)
    {
        var before = _waterSelection.Count;
        if (!additive) _waterSelection.Clear();
        foreach (var id in ids) if (!_waterSelection.Contains(id)) _waterSelection.Add(id);
        if (_waterSelection.Count == 1 && SelectedRiver is { } river && SelectedRiverChannel is null)
            _selectedChannelId = river.Channels[0].Id;
        if (_waterSelection.Count == 0) { _selectedChannelId = null; _riverNode = -1; }
        if (ids.Count > 0 || before != _waterSelection.Count) RaiseWaterState();
    }

    public void ClearWaterSelection()
    {
        if (_waterSelection.Count == 0) return;
        _waterSelection.Clear(); _selectedChannelId = null; _riverNode = -1;
        RaiseWaterState();
    }

    // ---------------------------------------------------------------- moving whole water

    /// <summary>True while a water drag is running, so the router keeps the pointer captured.</summary>
    public bool IsDraggingWater => _waterDragFrom is not null;

    /// <summary>Live substitutions for the water being dragged, merged with a creation draft.</summary>
    internal IReadOnlyList<River>? WaterDragOverrides =>
        _waterDragDrafts is { Count: > 0 } drafts ? [.. drafts.Values] : null;

    public bool BeginWaterDrag(double worldX, double worldY)
    {
        var waters = SelectedWaters;
        if (waters.Count == 0) return false;
        if (waters.Any(r => r.Locked))
        { StatusDetail = "Вода заблокирована: снимите блокировку, чтобы перемещать её."; return false; }
        if (waters.Any(r => RiverLayerOf(r.Id) is not { Visible: true, Locked: false }))
        { StatusDetail = "Слой воды скрыт или заблокирован."; return false; }
        _waterDragFrom = (worldX, worldY);
        _waterDragDrafts = waters.ToDictionary(r => r.Id, r => r);
        _waterDragMoved = false;
        return true;
    }

    public void UpdateWaterDrag(double worldX, double worldY)
    {
        if (_waterDragFrom is not { } from || _waterDragDrafts is null) return;
        var dx = worldX - from.X; var dy = worldY - from.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
        _waterDragMoved = true;
        var moved = new Dictionary<Guid, River>(_waterDragDrafts.Count);
        foreach (var river in SelectedWaters) moved[river.Id] = RiverEditor.Translate(river, dx, dy);
        _waterDragDrafts = moved;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    /// <summary>Commits the whole move as exactly one history entry, across layers.</summary>
    public bool EndWaterDrag()
    {
        var drafts = _waterDragDrafts;
        var moved = _waterDragMoved;
        _waterDragFrom = null; _waterDragDrafts = null; _waterDragMoved = false;
        if (drafts is null || !moved) { InvalidateScene(EditorChangeReason.CanvasInvalidation); return false; }
        var steps = new List<IEditCommand>();
        foreach (var pair in drafts)
        {
            if (RiverLayerOf(pair.Key) is not { } layer) continue;
            try { pair.Value.Validate(); } catch (ArgumentException ex) { StatusDetail = ex.Message; return false; }
            steps.Add(new RiverCommand(layer.Id, pair.Key, pair.Value, "Переместить воду"));
        }
        if (steps.Count == 0) return false;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("Переместить воду", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        AfterEdit(geometryOnly: true); RaiseWaterState();
        StatusDetail = drafts.Count == 1 ? "Водная система перемещена." : "Перемещено водных систем: " + drafts.Count + ".";
        return true;
    }

    public void CancelWaterDrag()
    {
        if (_waterDragFrom is null) return;
        _waterDragFrom = null; _waterDragDrafts = null; _waterDragMoved = false;
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    // ---------------------------------------------------------------- delete at the chosen level

    /// <summary>
    /// The one Delete of the editor. It removes what the current level names and nothing else:
    /// the selected node, the selected channel, or every selected water system. When water is not
    /// selected the ordinary object deletion runs, so the key keeps its usual meaning.
    /// </summary>
    public bool DeleteSelection()
    {
        if (_waterSelection.Count == 0)
        {
            if (Session.SelectedObjectIds.Count == 0) return false;
            DeleteSelectedObject();
            return true;
        }
        if (_waterLevel == WaterSelectionLevel.Node) return DeleteSelectedRiverNode();
        if (_waterLevel == WaterSelectionLevel.Channel && _waterSelection.Count == 1
            && SelectedRiver is { } single && single.Channels.Length > 1 && SelectedRiverChannel is not null)
        { DeleteSelectedChannel(); return true; }
        return DeleteSelectedWaters();
    }

    /// <summary>Removes every selected water system as one history entry.</summary>
    public bool DeleteSelectedWaters()
    {
        var waters = SelectedWaters;
        if (waters.Count == 0) return false;
        if (waters.FirstOrDefault(r => r.Locked) is { } locked)
        { StatusDetail = $"«{locked.Name}» заблокирована: снимите блокировку, чтобы удалить её."; return false; }
        var steps = new List<IEditCommand>();
        foreach (var river in waters)
        {
            if (RiverLayerOf(river.Id) is not { } layer) continue;
            if (!layer.Visible || layer.Locked) { StatusDetail = "Слой воды скрыт или заблокирован."; return false; }
            steps.Add(new RiverCommand(layer.Id, river.Id, null, "Удалить воду"));
        }
        if (steps.Count == 0) return false;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand("Удалить воду", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        var removed = steps.Count;
        _waterSelection.Clear(); _selectedChannelId = null; _riverNode = -1;
        AfterEdit(geometryOnly: true); RaiseWaterState();
        StatusDetail = removed == 1
            ? "Водная система удалена. Отменить — Ctrl+Z."
            : "Удалено водных систем: " + removed + ". Отменить — Ctrl+Z.";
        return true;
    }

    // ---------------------------------------------------------------- visibility, lock, layer

    public bool SelectedWaterVisible => SelectedWaters.Count > 0 && SelectedWaters.All(r => r.Visible);
    public bool SelectedWaterLocked => SelectedWaters.Count > 0 && SelectedWaters.All(r => r.Locked);

    public void ToggleSelectedWaterVisible() => SetSelectedWaterVisible(!SelectedWaterVisible);
    public void ToggleSelectedWaterLock() => SetSelectedWaterLocked(!SelectedWaterLocked);

    public void SetSelectedWaterVisible(bool visible) =>
        ApplyToSelectedWaters(river => river with { Visible = visible },
            visible ? "Показать воду" : "Скрыть воду", ignoreLock: false);

    public void SetSelectedWaterLocked(bool locked) =>
        ApplyToSelectedWaters(river => river with { Locked = locked },
            locked ? "Заблокировать воду" : "Разблокировать воду", ignoreLock: true);

    private void ApplyToSelectedWaters(Func<River, River> change, string name, bool ignoreLock)
    {
        var waters = SelectedWaters;
        if (waters.Count == 0) return;
        if (!ignoreLock && waters.Any(r => r.Locked)) { StatusDetail = "Вода заблокирована."; return; }
        var steps = new List<IEditCommand>();
        foreach (var river in waters)
        {
            if (RiverLayerOf(river.Id) is not { Visible: true, Locked: false } layer)
            { StatusDetail = "Слой воды скрыт или заблокирован."; return; }
            var next = change(river);
            if (SameRiverState(river, next)) continue;
            try { next.Validate(); } catch (ArgumentException ex) { StatusDetail = ex.Message; return; }
            steps.Add(new RiverCommand(layer.Id, river.Id, next, name));
        }
        if (steps.Count == 0) return;
        var revision = Session.Revision;
        Session.Execute(steps.Count == 1 ? steps[0] : new CompositeCommand(name, steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return; }
        AfterEdit(geometryOnly: true); RaiseWaterState();
        StatusDetail = name + ": готово.";
    }

    /// <summary>Only the two switches this path changes; the geometry is not rewritten here.</summary>
    private static bool SameRiverState(River a, River b) => a.Visible == b.Visible && a.Locked == b.Locked;

    /// <summary>Moves the selected water systems to another object layer as one history entry.</summary>
    public bool MoveSelectedWaterToLayer(LayerId targetId)
    {
        var waters = SelectedWaters;
        if (waters.Count == 0) return false;
        if (Session.Document.FindLayer(targetId) is not ObjectLayer target)
        { StatusDetail = "Вода переносится только на слой объектов."; return false; }
        if (!target.Visible || target.Locked) { StatusDetail = "Целевой слой скрыт или заблокирован."; return false; }
        var steps = new List<IEditCommand>();
        foreach (var river in waters)
        {
            if (RiverLayerOf(river.Id) is not { } source) continue;
            if (source.Id == targetId) continue;
            if (!source.Visible || source.Locked) { StatusDetail = "Исходный слой воды скрыт или заблокирован."; return false; }
            steps.Add(new RiverCommand(source.Id, river.Id, null, "Снять воду со слоя"));
            steps.Add(new RiverCommand(targetId, river.Id, river, "Положить воду на слой"));
        }
        if (steps.Count == 0) { StatusDetail = "Вода уже на этом слое."; return false; }
        var revision = Session.Revision;
        Session.Execute(new CompositeCommand("Перенести воду на другой слой", steps));
        if (Session.Revision == revision) { StatusDetail = Session.LastResourceError ?? StatusDetail; return false; }
        _activeLayerId = targetId;
        AfterEdit(geometryOnly: true); RaiseWaterState();
        StatusDetail = $"Вода перенесена на слой «{target.Name}».";
        return true;
    }

    private void RaiseWaterState()
    {
        Raise(nameof(WaterSelectionIds)); Raise(nameof(WaterSelectionCount)); Raise(nameof(SelectedWaters));
        Raise(nameof(WaterSelectionSummary)); Raise(nameof(SelectedWaterVisible)); Raise(nameof(SelectedWaterLocked));
        RaiseRiverState();
    }
}
