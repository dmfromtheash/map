using Map.Spike.Core.Documents;

namespace Map.Spike.Core.Commands;

/// <summary>Linear undo/redo stack. One completed gesture == one entry.</summary>
public sealed class CommandHistory
{
    private readonly List<IEditCommand> _undo = [];
    private readonly List<IEditCommand> _redo = [];

    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? NextUndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? NextRedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    public long ApproximateMemoryBytes
    {
        get
        {
            long total = 0;
            foreach (var c in _undo) total += c.ApproximateMemoryBytes;
            foreach (var c in _redo) total += c.ApproximateMemoryBytes;
            return total;
        }
    }

    public void Execute(SpikeDocument document, IEditCommand command)
    {
        command.Apply(document);
        document.BumpRevision();
        _undo.Add(command);
        _redo.Clear();
    }

    public bool Undo(SpikeDocument document)
    {
        if (_undo.Count == 0) return false;
        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Revert(document);
        document.BumpRevision();
        _redo.Add(command);
        return true;
    }

    public bool Redo(SpikeDocument document)
    {
        if (_redo.Count == 0) return false;
        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Apply(document);
        document.BumpRevision();
        _undo.Add(command);
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
