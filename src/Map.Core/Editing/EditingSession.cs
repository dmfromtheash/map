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

    public EditingSession(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        _currentState = new StateNode(parent: null);
        _savedState = _currentState;
        SavedRevision = 0;
    }

    /// <summary>The current immutable snapshot - the single source of truth.</summary>
    public MapDocument Document { get; private set; }

    public Viewport Viewport { get; } = new();

    public ObjectId? SelectedObjectId { get; set; }

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
        Document = command.Apply(Document);
        Revision++;
        var afterState = new StateNode(_currentState);
        _undo.Add(new HistoryEntry(command, _currentState, afterState));
        _currentState = afterState;
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Document = entry.Command.Revert(Document);
        Revision++;
        _currentState = entry.BeforeState;
        _redo.Add(entry);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Document = entry.Command.Apply(Document);
        Revision++;
        _currentState = entry.AfterState;
        _undo.Add(entry);
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
