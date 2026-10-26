using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>One material stroke. Undo restores only the touched RGBA tiles; coverage is unchanged.</summary>
public sealed class LandPaintCommand(
    LayerId layerId,
    IReadOnlyDictionary<TileKey, byte[]?> before,
    IReadOnlyDictionary<TileKey, byte[]?> after,
    string name) : IEditCommand
{
    public string Name { get; } = name;
    public long ApproximateMemoryBytes => before.Values.Concat(after.Values).Sum(v => (long)(v?.Length ?? 0));
    public MapDocument Apply(MapDocument document) => Write(document, after);
    public MapDocument Revert(MapDocument document) => Write(document, before);
    private MapDocument Write(MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> tiles) =>
        document.WithLayer(layerId, layer => layer is LandLayer land
            ? land with { PaintTiles = land.PaintTiles.WithMany(tiles) }
            : throw new InvalidOperationException("Material painting requires a land layer."));
}

/// <summary>
/// One stroke of the «маска обрыва» brush (R12): the touched tiles of both masks, so «здесь стена»
/// can clear «здесь пляж» under it and the other way round. Undo restores the bytes of both.
/// </summary>
public sealed class LandCliffMaskCommand(
    LayerId layerId,
    IReadOnlyDictionary<TileKey, byte[]?> wallBefore,
    IReadOnlyDictionary<TileKey, byte[]?> wallAfter,
    IReadOnlyDictionary<TileKey, byte[]?> beachBefore,
    IReadOnlyDictionary<TileKey, byte[]?> beachAfter,
    string name) : IEditCommand
{
    public string Name { get; } = name;
    public long ApproximateMemoryBytes =>
        new[] { wallBefore, wallAfter, beachBefore, beachAfter }.SelectMany(m => m.Values).Sum(v => (long)(v?.Length ?? 0));
    public MapDocument Apply(MapDocument document) => Write(document, wallAfter, beachAfter);
    public MapDocument Revert(MapDocument document) => Write(document, wallBefore, beachBefore);
    private MapDocument Write(MapDocument document, IReadOnlyDictionary<TileKey, byte[]?> wall, IReadOnlyDictionary<TileKey, byte[]?> beach) =>
        document.WithLayer(layerId, layer => layer is LandLayer land
            ? land with { CliffWallTiles = land.CliffWallTiles.WithMany(wall), CliffBeachTiles = land.CliffBeachTiles.WithMany(beach) }
            : throw new InvalidOperationException("The cliff mask requires a land layer."));
}
