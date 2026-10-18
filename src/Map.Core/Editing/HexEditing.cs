using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

/// <summary>Which part of a hex a hex gesture writes.</summary>
public enum HexPaintTarget
{
    /// <summary>The terrain tile; erasing it clears the whole hex.</summary>
    Terrain,

    /// <summary>The feature drawn over the terrain; a feature needs terrain under it.</summary>
    Feature,

    /// <summary>The region of a painted hex (<see cref="HexTileChoice.Region"/>; 0 takes it out of every region).</summary>
    Region,

    /// <summary>The fog of war of a painted hex: reveal it (<see cref="HexTileChoice.Reveal"/>) or cover it again.</summary>
    Fog,
}

/// <summary>One tile of a mixed brush and how often it comes up relative to the others.</summary>
public sealed record HexMixEntry(string TileId, int VariantCount, double Weight = 1);

/// <summary>The tile a hex gesture puts down and how it chooses among the tile's variants.</summary>
/// <param name="TileId">Library id of the tile.</param>
/// <param name="VariantCount">How many pictures the tile has (at least 1).</param>
/// <param name="Target">Terrain, feature, region or fog.</param>
/// <param name="RandomVariant">Each hex gets its own picture, chosen by the hex position and <paramref name="Seed"/>.</param>
/// <param name="FixedVariant">The picture used when <paramref name="RandomVariant"/> is off.</param>
/// <param name="Seed">Changes the random pictures without changing which hexes are painted.</param>
public sealed record HexTileChoice(
    string TileId,
    int VariantCount,
    HexPaintTarget Target = HexPaintTarget.Terrain,
    bool RandomVariant = true,
    int FixedVariant = 0,
    int Seed = 0)
{
    /// <summary>
    /// Several tiles with weights (the Hex Kit multi-select, the Hexxmap sampler): every hex gets one
    /// of them, chosen by its position. Empty means just <see cref="TileId"/>.
    /// </summary>
    public IReadOnlyList<HexMixEntry> Mix { get; init; } = [];

    /// <summary>Terrain pictures are turned by this many 60° steps (0..5).</summary>
    public int Rotation { get; init; }

    /// <summary>Each hex gets its own turn of the picture (only for tiles that may turn).</summary>
    public bool RandomRotation { get; init; }

    /// <summary>The tile may be turned; tall tiles and tiles with a top never are.</summary>
    public bool Rotatable { get; init; } = true;

    /// <summary>Region id a <see cref="HexPaintTarget.Region"/> gesture writes.</summary>
    public int Region { get; init; }

    /// <summary>A <see cref="HexPaintTarget.Fog"/> gesture reveals (true) or covers again (false).</summary>
    public bool Reveal { get; init; } = true;
}

/// <summary>
/// Pure hex-editing rules shared by every hex gesture: footprints, flood fill, the change a tile
/// makes to one hex, and the deterministic variant choice. Nothing here touches a document.
/// </summary>
public static class HexEditing
{
    /// <summary>Largest footprint radius in hexes (radius 0 is one hex).</summary>
    public const int MaxRadius = 8;

    /// <summary>Flood fill never grows past this many hexes in one gesture.</summary>
    public const int MaxFillHexes = 262_144;

    /// <summary>The hexes of a footprint of the given radius that lie on the board.</summary>
    public static IEnumerable<HexCoord> Footprint(HexBoard board, HexCoord center, int radius) =>
        HexCoord.Range(center, Math.Clamp(radius, 0, MaxRadius)).Where(board.Contains);

    /// <summary>
    /// The connected region around <paramref name="start"/> whose hexes hold the same terrain
    /// (or are all empty when the start is empty), bounded by the board.
    /// </summary>
    public static IReadOnlyList<HexCoord> FloodRegion(HexBoard board, HexCoord start, int limit = MaxFillHexes)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (!board.Contains(start))
        {
            return [];
        }

        var terrain = board[start]?.Terrain;
        var region = new List<HexCoord>();
        var seen = new HashSet<HexCoord> { start };
        var queue = new Queue<HexCoord>();
        queue.Enqueue(start);
        while (queue.Count > 0 && region.Count < limit)
        {
            var hex = queue.Dequeue();
            region.Add(hex);
            foreach (var next in hex.Neighbors())
            {
                if (seen.Add(next) && board.Contains(next) && board[next]?.Terrain == terrain)
                {
                    queue.Enqueue(next);
                }
            }
        }

        return region;
    }

    /// <summary>A stable hash of a hex position and a salt: the same hex and salt always give the same number.</summary>
    public static uint Noise(HexCoord hex, int salt)
    {
        unchecked
        {
            var h = (uint)hex.Q * 0x9E3779B1u ^ (uint)hex.R * 0x85EBCA77u ^ (uint)salt * 0xC2B2AE3Du;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }
    }

    /// <summary>
    /// The picture a hex gets: a stable hash of its position, so repainting the same hex with the
    /// same seed gives the same picture and neighbours rarely repeat.
    /// </summary>
    public static int VariantFor(HexCoord hex, HexTileChoice choice) =>
        VariantFor(hex, choice, choice.VariantCount);

    private static int VariantFor(HexCoord hex, HexTileChoice choice, int variantCount)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var count = Math.Max(1, variantCount);
        if (!choice.RandomVariant)
        {
            return Math.Clamp(choice.FixedVariant, 0, count - 1);
        }

        unchecked
        {
            // The first formula of the hex category: kept so painted maps repaint the same way.
            var h = (uint)hex.Q * 0x9E3779B1u ^ (uint)hex.R * 0x85EBCA77u ^ (uint)choice.Seed * 0xC2B2AE3Du;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return (int)(h % (uint)count);
        }
    }

    /// <summary>The tile of the choice a hex gets: the tile itself, or one of a mix by weight.</summary>
    public static (string TileId, int VariantCount) TileFor(HexCoord hex, HexTileChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.Mix.Count == 0) return (choice.TileId, choice.VariantCount);
        var total = 0.0;
        foreach (var entry in choice.Mix) total += Math.Max(0, entry.Weight);
        if (total <= 0) return (choice.Mix[0].TileId, choice.Mix[0].VariantCount);
        var pick = Noise(hex, choice.Seed ^ 0x5bd1e995) / (double)uint.MaxValue * total;
        foreach (var entry in choice.Mix)
        {
            pick -= Math.Max(0, entry.Weight);
            if (pick < 0) return (entry.TileId, entry.VariantCount);
        }

        return (choice.Mix[^1].TileId, choice.Mix[^1].VariantCount);
    }

    /// <summary>The turn of the terrain picture a hex gets.</summary>
    public static int RotationFor(HexCoord hex, HexTileChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!choice.Rotatable) return 0;
        return choice.RandomRotation ? (int)(Noise(hex, choice.Seed ^ 0x1b873593) % 6) : ((choice.Rotation % 6) + 6) % 6;
    }

    /// <summary>What a hex becomes when the tile is put on it, or the hex unchanged when the tile does not apply.</summary>
    public static HexCell? Place(HexCell? current, HexCoord hex, HexTileChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        switch (choice.Target)
        {
            case HexPaintTarget.Region:
                return current is null || current.Region == choice.Region ? current : current.WithRegion(choice.Region);
            case HexPaintTarget.Fog:
                return current is null || current.Revealed == choice.Reveal ? current : current.WithRevealed(choice.Reveal);
        }

        var (tile, variants) = TileFor(hex, choice);
        var variant = VariantFor(hex, choice, variants);
        var rotation = RotationFor(hex, choice);
        return choice.Target switch
        {
            HexPaintTarget.Terrain => current is null
                ? HexCell.Of(tile, variant, rotation: rotation)
                : current.Terrain == tile && !choice.RandomVariant && current.Variant == variant && current.Rotation == rotation
                    ? current
                    // Repainting the terrain keeps the feature, the region and the fog of the hex.
                    : current.WithTerrain(tile, variant, rotation),
            // A feature needs ground: on an empty hex it does nothing.
            HexPaintTarget.Feature => current?.WithFeature(tile, variant),
            _ => current,
        };
    }

    /// <summary>What a hex becomes when it is erased in the given target.</summary>
    public static HexCell? Erase(HexCell? current, HexPaintTarget target) => target switch
    {
        HexPaintTarget.Feature => current?.Feature is null ? current : current.WithFeature(null),
        HexPaintTarget.Region => current is null || current.Region == 0 ? current : current.WithRegion(0),
        HexPaintTarget.Fog => current is null || !current.Revealed ? current : current.WithRevealed(false),
        _ => null,
    };
}

/// <summary>
/// One hex gesture as exact before/after values of every hex it touched. Apply writes the after
/// values, Revert the before values; the board shares every untouched chunk.
/// </summary>
public sealed class HexPaintCommand : IEditCommand
{
    private readonly LayerId _layerId;
    private readonly KeyValuePair<HexCoord, HexCell?>[] _before;
    private readonly KeyValuePair<HexCoord, HexCell?>[] _after;

    public HexPaintCommand(
        string name, LayerId layerId,
        IReadOnlyDictionary<HexCoord, HexCell?> before,
        IReadOnlyDictionary<HexCoord, HexCell?> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Name = string.IsNullOrWhiteSpace(name) ? "Hex edit" : name;
        _layerId = layerId;
        _before = [.. before];
        _after = [.. after];
    }

    /// <summary>
    /// Builds the command for a set of new hex values, recording the values they replace and
    /// dropping hexes that would not change. Null when nothing changes.
    /// </summary>
    public static HexPaintCommand? Create(string name, HexLayer layer, IReadOnlyDictionary<HexCoord, HexCell?> after)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(after);
        var before = new Dictionary<HexCoord, HexCell?>();
        var changed = new Dictionary<HexCoord, HexCell?>();
        foreach (var (hex, cell) in after)
        {
            if (!layer.Board.Contains(hex))
            {
                continue;
            }

            var old = layer.Board[hex];
            if (Equals(old, cell))
            {
                continue;
            }

            before[hex] = old;
            changed[hex] = cell;
        }

        return changed.Count == 0 ? null : new HexPaintCommand(name, layer.Id, before, changed);
    }

    public string Name { get; }

    public LayerId LayerId => _layerId;

    public int HexCount => _after.Length;

    public IEnumerable<HexCoord> Hexes => _after.Select(pair => pair.Key);

    public long? MeasuredWorkingBytes => EditCost.BaseBytes + (long)_after.Length * 64;

    public MapDocument Apply(MapDocument document) => Write(document, _after);

    public MapDocument Revert(MapDocument document) => Write(document, _before);

    private MapDocument Write(MapDocument document, KeyValuePair<HexCoord, HexCell?>[] values)
    {
        ArgumentNullException.ThrowIfNull(document);
        var layers = document.Layers.ToList();
        var index = layers.FindIndex(layer => layer.Id == _layerId);
        if (index < 0 || layers[index] is not HexLayer hexLayer)
        {
            throw new InvalidOperationException($"Hex layer {_layerId} is not in the document.");
        }

        layers[index] = hexLayer with { Board = hexLayer.Board.With(values) };
        return document.WithLayers(layers);
    }
}

/// <summary>Changes how a hex layer draws its grid; undoable like every layer setting.</summary>
public sealed class UpdateHexGridCommand(LayerId layerId, HexGridStyle before, HexGridStyle after) : IEditCommand
{
    public string Name => "Hex grid";

    public long? MeasuredWorkingBytes => EditCost.BaseBytes;

    public MapDocument Apply(MapDocument document) => Write(document, after);

    public MapDocument Revert(MapDocument document) => Write(document, before);

    private MapDocument Write(MapDocument document, HexGridStyle style)
    {
        var layers = document.Layers.ToList();
        var index = layers.FindIndex(layer => layer.Id == layerId);
        if (index < 0 || layers[index] is not HexLayer hexLayer)
        {
            throw new InvalidOperationException($"Hex layer {layerId} is not in the document.");
        }

        layers[index] = hexLayer with { Grid = style };
        return document.WithLayers(layers);
    }
}
