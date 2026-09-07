using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// The live editing state around one immutable document snapshot: history, selection, viewport
/// and revision bookkeeping. The session owns which snapshot is current; nothing else in the app
/// may swap it. Selection and viewport are session state and are never saved into the scene.
/// </summary>
public sealed class EditingSession
{
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private StateNode _currentState;
    private StateNode? _savedState;
    private readonly Map.Core.Resources.WorkingMemoryBudget _memory;

    public EditingSession(MapDocument document, Map.Core.Resources.WorkingMemoryBudget? memory = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        _memory = memory ?? Map.Core.Resources.WorkingMemoryBudget.Shared;
        _currentState = new StateNode(parent: null);
        _savedState = _currentState;
        SavedRevision = 0;
    }

    /// <summary>The current immutable snapshot - the single source of truth.</summary>
    public MapDocument Document { get; private set; }

    public Viewport Viewport { get; } = new();

    private readonly List<ObjectId> _selection = [];
    private List<ObjectId> _previousSelection = [];
    private ObjectId? _primarySelection;

    /// <summary>Ordered multi-selection without duplicates. Session state, never persisted.</summary>
    public IReadOnlyList<ObjectId> SelectedObjectIds => _selection;

    /// <summary>
    /// The primary selected object. Setting it keeps the old single-selection contract: the value
    /// REPLACES the whole selection, so every pre-10C caller still behaves exactly as before.
    /// </summary>
    public ObjectId? SelectedObjectId
    {
        get => _primarySelection;
        set => SetSelection(value is { } id ? [id] : [], value);
    }

    /// <summary>Every selected object that still exists, in selection order.</summary>
    public IReadOnlyList<SceneObject> SelectedObjects =>
        [.. _selection.Select(id => DocumentEditor.FindObject(Document, id)).OfType<SceneObject>()];

    /// <summary>
    /// Replaces the selection with the given ids: duplicates collapse to the first occurrence,
    /// ids that are not part of the document are dropped, and the primary defaults to the last
    /// remaining id when the requested one is absent.
    /// </summary>
    public void SetSelection(IEnumerable<ObjectId> ids, ObjectId? primary = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var next = new List<ObjectId>();
        var seen = new HashSet<ObjectId>();
        foreach (var id in ids)
        {
            if (seen.Add(id) && DocumentEditor.FindObject(Document, id) is not null)
            {
                next.Add(id);
            }
        }

        RememberBeforeChange(next);
        _selection.Clear();
        _selection.AddRange(next);
        _primarySelection = primary is { } wanted && seen.Contains(wanted) && next.Contains(wanted)
            ? wanted
            : next.Count > 0 ? next[^1] : null;
    }

    /// <summary>Ctrl+click contract: adds an absent id (it becomes primary) or removes a present one.</summary>
    public void ToggleSelection(ObjectId id)
    {
        if (_selection.Contains(id))
        {
            var next = _selection.Where(existing => existing != id);
            SetSelection(next, _primarySelection == id ? null : _primarySelection);
        }
        else
        {
            SetSelection([.. _selection, id], id);
        }
    }

    /// <summary>Ctrl+marquee contract: appends only the ids the selection does not hold yet.</summary>
    public void AddToSelection(IEnumerable<ObjectId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        SetSelection([.. _selection, .. ids]);
    }

    public void ClearSelection() => SetSelection([]);

    /// <summary>
    /// Restores the previous whole selection (the ` / ~ shortcut). Ids that left the document in
    /// the meantime are skipped. Returns false when there is nothing to restore.
    /// </summary>
    public bool RestorePreviousSelection()
    {
        if (_previousSelection.Count == 0)
        {
            return false;
        }

        SetSelection(_previousSelection);
        return _selection.Count > 0;
    }

    /// <summary>Keeps the last different non-empty set so the user can always come back to it.</summary>
    private void RememberBeforeChange(List<ObjectId> next)
    {
        if (_selection.Count > 0 && !_selection.SequenceEqual(next))
        {
            _previousSelection = [.. _selection];
        }
    }

    /// <summary>Drops selected ids the current snapshot no longer contains.</summary>
    private void PruneSelection()
    {
        if (_selection.Count == 0)
        {
            return;
        }

        var alive = _selection.Where(id => DocumentEditor.FindObject(Document, id) is not null).ToList();
        if (alive.Count == _selection.Count)
        {
            return;
        }

        _selection.Clear();
        _selection.AddRange(alive);
        if (_primarySelection is { } primary && !alive.Contains(primary))
        {
            _primarySelection = alive.Count > 0 ? alive[^1] : null;
        }
    }

    /// <summary>Monotonic edit counter. Bumped once per executed/undone/redone command.</summary>
    public long Revision { get; private set; }

    /// <summary>
    /// Revision proven durable on disk, advanced only via <see cref="MarkSaved"/> after a
    /// successful atomic replace. A failed save never advances it.
    /// </summary>
    public long SavedRevision { get; private set; }

    public bool IsDirty => !ReferenceEquals(_currentState, _savedState);

    /// <summary>
    /// Number of commands between the current state and the durable state. A new or recovered
    /// document with no durable state starts at one so the UI never reports zero work at risk.
    /// </summary>
    public long PendingChanges => _savedState is null
        ? _currentState.Depth + 1
        : DistanceBetween(_currentState, _savedState);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

    public SceneObject? SelectedObject =>
        SelectedObjectId is { } id ? DocumentEditor.FindObject(Document, id) : null;

    public void Execute(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var cost = Math.Clamp(command.EstimatedWorkingBytes, 0,
            Map.Core.Resources.WorkingMemoryBudget.SmallOperationLimitBytes);
        if (!_memory.TryReserve(cost, out var reservation, out var reason))
        {
            LastResourceError = reason;
            ResourceAdmissionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        using var held = reservation;
        if (LastResourceError is not null)
        { LastResourceError = null; ResourceAdmissionChanged?.Invoke(this, EventArgs.Empty); }
        MapDocument candidate;
        try { candidate = command.Apply(Document); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            && (command is PlacementPathCommand or PlacementAreaCommand or RiverCommand or CompositeCommand { ContainsPlacementPath: true }
                || Document.Layers.OfType<ObjectLayer>().Any(l => !l.PlacementPaths.IsEmpty || !l.PlacementAreas.IsEmpty || !l.Rivers.IsEmpty)))
        { LastResourceError = ex.Message; ResourceAdmissionChanged?.Invoke(this, EventArgs.Empty); return; }
        if (command is not PlacementPathCommand && !PlacementPathOwnership.Allows(Document, candidate, out var pathReason))
        { LastResourceError = pathReason; ResourceAdmissionChanged?.Invoke(this, EventArgs.Empty); return; }
        if (command is not PlacementAreaCommand && !PlacementAreaOwnership.Allows(Document, candidate, out var areaReason))
        { LastResourceError = areaReason; ResourceAdmissionChanged?.Invoke(this, EventArgs.Empty); return; }
        Document = candidate;
        Revision++;
        var afterState = new StateNode(_currentState);
        _undo.Add(new HistoryEntry(command, _currentState, afterState));
        _currentState = afterState;
        _redo.Clear();
        PruneSelection();
    }

    public string? LastResourceError { get; private set; }
    public event EventHandler? ResourceAdmissionChanged;

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var entry = _undo[^1];
        Document = entry.Command.Revert(Document);
        _undo.RemoveAt(_undo.Count - 1);
        Revision++;
        _currentState = entry.BeforeState;
        _redo.Add(entry);
        PruneSelection();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        var entry = _redo[^1];
        Document = entry.Command.Apply(Document);
        _redo.RemoveAt(_redo.Count - 1);
        Revision++;
        _currentState = entry.AfterState;
        _undo.Add(entry);
        PruneSelection();
        return true;
    }

    /// <summary>
    /// Captures one immutable document state for persistence. Keeping the state identity together
    /// with the snapshot prevents a background save from marking a newer edit as durable.
    /// </summary>
    public SaveCheckpoint CaptureSaveCheckpoint() =>
        new(this, Document, Revision, _currentState);

    /// <summary>Marks only the captured state durable after its atomic save succeeds.</summary>
    public void MarkSaved(SaveCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!ReferenceEquals(checkpoint._owner, this))
        {
            throw new ArgumentException("The save checkpoint belongs to another editing session.", nameof(checkpoint));
        }

        _savedState = checkpoint._state;
        SavedRevision = checkpoint.Revision;
    }

    /// <summary>Marks a new or recovered document as having no durable state yet.</summary>
    public void MarkUnsaved()
    {
        _savedState = null;
        SavedRevision = -1;
    }

    private static long DistanceBetween(StateNode first, StateNode second)
    {
        long distance = 0;
        while (first.Depth > second.Depth)
        {
            first = first.Parent!;
            distance++;
        }

        while (second.Depth > first.Depth)
        {
            second = second.Parent!;
            distance++;
        }

        while (!ReferenceEquals(first, second))
        {
            first = first.Parent!;
            second = second.Parent!;
            distance += 2;
        }

        return distance;
    }

    public sealed class SaveCheckpoint
    {
        internal readonly EditingSession _owner;
        internal readonly StateNode _state;

        internal SaveCheckpoint(
            EditingSession owner,
            MapDocument document,
            long revision,
            StateNode state)
        {
            _owner = owner;
            _state = state;
            Document = document;
            Revision = revision;
        }

        public MapDocument Document { get; }
        public long Revision { get; }
    }

    private sealed record HistoryEntry(
        IEditCommand Command,
        StateNode BeforeState,
        StateNode AfterState);

    internal sealed class StateNode(StateNode? parent)
    {
        public StateNode? Parent { get; } = parent;
        public long Depth { get; } = parent?.Depth + 1 ?? 0;
    }
}
