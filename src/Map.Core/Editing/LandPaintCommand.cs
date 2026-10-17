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
