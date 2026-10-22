namespace Map.Rendering;

public sealed partial class HexChunkCache
{
    private long _tileSetRevision = -1;

    // Called on the rendering thread, before accepting completed background chunks.
    // A just-imported set must replace already cached missing-tile markers without editing the document.
    internal void RefreshTileSets()
    {
        var revision = HexUserTileSets.Revision;
        if (_tileSetRevision == revision) return;
        Clear();
        _tileSetRevision = revision;
    }
}
