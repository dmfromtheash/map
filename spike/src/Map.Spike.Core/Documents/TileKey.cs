namespace Map.Spike.Core.Documents;

/// <summary>Tile coordinate in tile units (not pixels) at the layer's base resolution.</summary>
public readonly record struct TileKey(int X, int Y)
{
    public override string ToString() => $"{X}_{Y}";

    public static TileKey Parse(string s)
    {
        var parts = s.Split('_');
        if (parts.Length != 2) throw new FormatException($"Bad tile key '{s}'.");
        return new TileKey(int.Parse(parts[0]), int.Parse(parts[1]));
    }
}
