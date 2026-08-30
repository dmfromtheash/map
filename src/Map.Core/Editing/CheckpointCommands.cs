using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Adds one already-captured immutable checkpoint. It never saves the project.</summary>
public sealed class AddCheckpointCommand(MapCheckpoint checkpoint) : IEditCommand
{
    private readonly MapCheckpoint _checkpoint = checkpoint
        ?? throw new ArgumentNullException(nameof(checkpoint));

    public string Name => "Create checkpoint";

    public MapDocument Apply(MapDocument document)
    {
        if (document.Checkpoints.Any(item => item.Id == _checkpoint.Id))
        {
            throw new InvalidOperationException($"Checkpoint {_checkpoint.Id} already exists.");
        }

        if (document.Checkpoints.Count >= MapCheckpointLimits.MaxCount)
        {
            throw new InvalidOperationException(
                $"A document can contain at most {MapCheckpointLimits.MaxCount} checkpoints.");
        }

        return document.WithCheckpoints([.. document.Checkpoints, _checkpoint]);
    }

    public MapDocument Revert(MapDocument document)
    {
        if (!document.Checkpoints.Any(item => item.Id == _checkpoint.Id))
        {
            throw new InvalidOperationException($"Checkpoint {_checkpoint.Id} is missing.");
        }

        return document.WithCheckpoints(
            [.. document.Checkpoints.Where(item => item.Id != _checkpoint.Id)]);
    }
}

/// <summary>Deletes exactly one checkpoint and restores it at the same position on Undo.</summary>
public sealed class DeleteCheckpointCommand : IEditCommand
{
    private readonly MapCheckpoint _checkpoint;
    private readonly int _index;

    private DeleteCheckpointCommand(MapCheckpoint checkpoint, int index)
    {
        _checkpoint = checkpoint;
        _index = index;
    }

    public string Name => "Delete checkpoint";

    public static DeleteCheckpointCommand Create(MapDocument document, CheckpointId id)
    {
        ArgumentNullException.ThrowIfNull(document);
        var index = document.Checkpoints.ToList().FindIndex(item => item.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Checkpoint {id} does not exist.");
        }

        return new DeleteCheckpointCommand(document.Checkpoints[index], index);
    }

    public MapDocument Apply(MapDocument document)
    {
        if (!document.Checkpoints.Any(item => item.Id == _checkpoint.Id))
        {
            throw new InvalidOperationException($"Checkpoint {_checkpoint.Id} is missing.");
        }

        return document.WithCheckpoints(
            [.. document.Checkpoints.Where(item => item.Id != _checkpoint.Id)]);
    }

    public MapDocument Revert(MapDocument document)
    {
        if (document.Checkpoints.Any(item => item.Id == _checkpoint.Id))
        {
            throw new InvalidOperationException($"Checkpoint {_checkpoint.Id} already exists.");
        }

        var checkpoints = document.Checkpoints.ToList();
        checkpoints.Insert(Math.Min(_index, checkpoints.Count), _checkpoint);
        return document.WithCheckpoints(checkpoints);
    }
}

/// <summary>
/// Replaces all map state in one reversible command while preserving the live document id and
/// checkpoint catalog. The open file path is session/UI state and is never touched here.
/// </summary>
public sealed class RestoreCheckpointCommand : IEditCommand
{
    private readonly MapCheckpoint _checkpoint;
    private readonly MapCheckpointSnapshot _before;

    private RestoreCheckpointCommand(MapCheckpoint checkpoint, MapCheckpointSnapshot before)
    {
        _checkpoint = checkpoint;
        _before = before;
    }

    public string Name => "Restore checkpoint";

    public static RestoreCheckpointCommand Create(MapDocument document, CheckpointId id)
    {
        ArgumentNullException.ThrowIfNull(document);
        var checkpoint = document.Checkpoints.FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException($"Checkpoint {id} does not exist.");
        return new RestoreCheckpointCommand(checkpoint, MapCheckpointSnapshot.Capture(document));
    }

    public MapDocument Apply(MapDocument document)
    {
        if (!document.Checkpoints.Any(item => item.Id == _checkpoint.Id))
        {
            throw new InvalidOperationException($"Checkpoint {_checkpoint.Id} is missing.");
        }

        return _checkpoint.Snapshot.Materialize(document.Id, document.Checkpoints);
    }

    public MapDocument Revert(MapDocument document) =>
        _before.Materialize(document.Id, document.Checkpoints);
}
