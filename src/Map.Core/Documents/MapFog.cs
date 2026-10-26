using System.Collections.Immutable;
using System.Numerics;

namespace Map.Core.Documents;

/// <summary>
/// The fog of war of a painted map (R27): what the players have seen. The map is cut into cells of
/// <see cref="Cell"/> map pixels, kept in tiles of 64×64 cells; a tile holds one 64-bit row per
/// line of cells (bit set = revealed), an empty row list is a fully revealed tile and a missing
/// tile is hidden. With <see cref="Enabled"/> off the players see the whole map.
/// </summary>
public sealed record MapFog
{
    public const int Cell = 16;
    public const int TileCells = 64;
    public const int TileWorld = Cell * TileCells;
    public const int MaxTiles = 1 << 20;

    public bool Enabled { get; init; }
    public ImmutableDictionary<long, ImmutableArray<ulong>> Revealed { get; init; } = ImmutableDictionary<long, ImmutableArray<ulong>>.Empty;

    public bool IsDefault => !Enabled && Revealed.IsEmpty;

    public static long Key(int tileX, int tileY) => ((long)tileX << 32) | (uint)tileY;
    public static (int X, int Y) Unkey(long key) => ((int)(key >> 32), (int)key);

    public bool IsRevealedCell(int cellX, int cellY)
    {
        if (cellX < 0 || cellY < 0) return false;
        if (!Revealed.TryGetValue(Key(cellX / TileCells, cellY / TileCells), out var rows)) return false;
        return rows.Length == 0 || (rows[cellY % TileCells] >> (cellX % TileCells) & 1) != 0;
    }

    /// <summary>True when the players see this point: no fog, or its cell revealed.</summary>
    public bool Sees(double x, double y) => !Enabled || IsRevealedCell((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell));

    /// <summary>The share of revealed cells in a tile, 0…1 (for the panel's «открыто» line).</summary>
    public long RevealedCells => Revealed.Values.Sum(rows => rows.Length == 0 ? (long)TileCells * TileCells : rows.Sum(r => (long)BitOperations.PopCount(r)));

    public void Validate()
    {
        if (Revealed is null || Revealed.Count > MaxTiles || Revealed.Any(p => Unkey(p.Key).X < 0 || Unkey(p.Key).Y < 0 || p.Value.IsDefault || p.Value.Length is not (0 or TileCells)))
            throw new ArgumentException("Повреждена маска тумана войны.");
    }
}
