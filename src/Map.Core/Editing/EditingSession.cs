using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>
/// The live editing state around one immutable document snapshot: history, selection, viewport
/// and revision bookkeeping. The session owns which snapshot is current; nothing else in the app
/// may swap it. Selection and viewport are session state and are never saved into the scene.
/// </summary>
public sealed class EditingSession
{
    private readonly List<IEditCommand> _undo = [];
    private readonly List<IEditCommand> _redo = [];

    public EditingSession(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
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

    public bool IsDirty => Revision != SavedRevision;

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
        _undo.Add(command);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Document = command.Revert(Document);
        Revision++;
        _redo.Add(command);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Document = command.Apply(Document);
        Revision++;
        _undo.Add(command);
        return true;
    }

    /// <summary>
    /// Marks a specific revision saved. The caller passes the revision captured when the saved
    /// snapshot was taken, so edits made during a background save keep the document dirty.
    /// </summary>
    public void MarkSaved(long revision) => SavedRevision = revision;
}
