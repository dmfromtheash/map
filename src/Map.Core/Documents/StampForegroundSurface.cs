namespace Map.Core.Documents;

/// <summary>
/// Document-owned, map-aligned foreground paint revealed only by stamps whose asset clip mask is
/// enabled. The sparse RGBA tiles are not a layer and are never drawn over the canvas directly.
/// </summary>
public sealed record StampForegroundSurface
{
    public StampForegroundSurface(TileMap tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.BytesPerPixel != 4)
        {
            throw new ArgumentException("The stamp foreground surface requires RGBA tiles.", nameof(tiles));
        }

        Tiles = tiles;
    }

    public TileMap Tiles { get; init; }

    public bool IsEmpty => Tiles.TileCount == 0;

    public static StampForegroundSurface CreateEmpty() => new(TileMap.Empty(4));
}
