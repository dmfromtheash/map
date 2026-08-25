using Map.Spike.Core.Documents;

namespace Map.Spike.Core.Commands;

/// <summary>
/// One completed brush gesture. Holds before/after images of ONLY the touched tiles, never the
/// whole layer (docs/05 raster-tile rule).
/// </summary>
public sealed class BrushStrokeCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _before;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _after;

    internal BrushStrokeCommand(
        LayerId layerId,
        IReadOnlyDictionary<TileKey, byte[]?> before,
        IReadOnlyDictionary<TileKey, byte[]?> after,
        string name)
    {
        _layerId = layerId;
        _before = before;
        _after = after;
        Name = name;
    }

    public string Name { get; }

    public IReadOnlyCollection<TileKey> TouchedTiles => (IReadOnlyCollection<TileKey>)_before.Keys;

    public long ApproximateMemoryBytes
    {
        get
        {
            long total = 0;
            foreach (var v in _before.Values) total += v?.Length ?? 0;
            foreach (var v in _after.Values) total += v?.Length ?? 0;
            return total;
        }
    }

    public void Apply(SpikeDocument document) => Write(document, _after);

    public void Revert(SpikeDocument document) => Write(document, _before);

    private void Write(SpikeDocument document, IReadOnlyDictionary<TileKey, byte[]?> source)
    {
        var layer = ResolveLayer(document);
        foreach (var (key, pixels) in source)
        {
            layer.SetTile(key, pixels is null ? null : (byte[])pixels.Clone());
        }
    }

    private RasterLayer ResolveLayer(SpikeDocument document)
    {
        if (document.Brush.Id == _layerId) return document.Brush;
        if (document.Decor.Id == _layerId) return document.Decor;
        throw new InvalidOperationException($"Raster layer {_layerId} is not part of this document.");
    }
}
