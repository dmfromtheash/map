using Map.Spike.Core.Documents;

namespace Map.Spike.Core.Commands;

/// <summary>
/// Records the pre-stroke content of every tile a gesture touches so the whole gesture can be
/// reverted as one transaction. A null snapshot means "the tile did not exist before".
/// </summary>
internal sealed class TileStrokeRecorder
{
    private readonly Dictionary<TileKey, byte[]?> _before = [];

    public IReadOnlyDictionary<TileKey, byte[]?> Before => _before;
    public int TouchedTileCount => _before.Count;

    /// <summary>Call once per tile before the first write to it.</summary>
    public void NoteBeforeFirstWrite(TileKey key, byte[]? currentContent)
    {
        if (_before.ContainsKey(key)) return;
        _before[key] = currentContent is null ? null : (byte[])currentContent.Clone();
    }
}
