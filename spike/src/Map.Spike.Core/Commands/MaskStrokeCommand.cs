using Map.Spike.Core.Documents;

namespace Map.Spike.Core.Commands;

/// <summary>One completed mask gesture. Touches the mask only - never the raster it targets.</summary>
public sealed class MaskStrokeCommand : IEditCommand
{
    private readonly LayerId _maskId;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _before;
    private readonly IReadOnlyDictionary<TileKey, byte[]?> _after;

    internal MaskStrokeCommand(
        LayerId maskId,
        IReadOnlyDictionary<TileKey, byte[]?> before,
        IReadOnlyDictionary<TileKey, byte[]?> after,
        string name)
    {
        _maskId = maskId;
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
        if (document.Mask.Id != _maskId)
            throw new InvalidOperationException($"Mask layer {_maskId} is not part of this document.");

        foreach (var (key, pixels) in source)
        {
            document.Mask.SetTile(key, pixels is null ? null : (byte[])pixels.Clone());
        }
    }
}
