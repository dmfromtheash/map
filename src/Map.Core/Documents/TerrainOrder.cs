namespace Map.Core.Documents;

/// <summary>
/// The one order of the terrain. Every piece of land (a Land layer) and every river or lake has
/// a level from <see cref="MinimumLevel"/> to <see cref="MaximumLevel"/>, exactly like an object
/// has its level inside its layer. Terrain is drawn level by level: land first, then the water
/// of that level. So water carves and flows over land at its own level or below, and land at a
/// higher level covers it - a river passing under a plateau, a lake lying on a terrace.
/// Inside one level the layer stack decides, as before.
/// </summary>
public static class TerrainOrder
{
    public const int MinimumLevel = -10;
    public const int MaximumLevel = 10;

    public static bool IsValidLevel(int level) => level is >= MinimumLevel and <= MaximumLevel;

    public static int Clamp(int level) => Math.Clamp(level, MinimumLevel, MaximumLevel);

    /// <summary>Land layers of the document in terrain draw order: by level, then by stack position.</summary>
    public static IReadOnlyList<LandLayer> Lands(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var ordered = new List<(LandLayer Land, int Index)>();
        for (var index = 0; index < document.Layers.Count; index++)
            if (document.Layers[index] is LandLayer land) ordered.Add((land, index));
        return [.. ordered.OrderBy(pair => pair.Land.Level).ThenBy(pair => pair.Index).Select(pair => pair.Land)];
    }

    /// <summary>True when the water lies on this land: it is carved into it and clipped to it.</summary>
    public static bool Covers(River river, LandLayer land)
    {
        ArgumentNullException.ThrowIfNull(river);
        ArgumentNullException.ThrowIfNull(land);
        return river.Level >= land.Level;
    }

    /// <summary>
    /// The topmost visible piece of land under a world point, by terrain order: the last one
    /// whose coverage is set there. Null over the sea and over hidden land.
    /// </summary>
    public static LandLayer? LandAt(MapDocument document, double x, double y)
    {
        var lands = Lands(document);
        for (var i = lands.Count - 1; i >= 0; i--)
        {
            var land = lands[i];
            if (!land.Visible || land.Opacity <= 0) continue;
            if (CoverageAt(land, x, y) > 127) return land;
        }
        return null;
    }

    /// <summary>Coverage byte of one land layer at a world point; a missing tile is sea.</summary>
    public static byte CoverageAt(LandLayer land, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(land);
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        var key = new TileKey((int)Math.Floor(x / TileMap.TileSize), (int)Math.Floor(y / TileMap.TileSize));
        if (land.GeometryTiles.TryGet(key) is not { } tile) return 0;
        var px = (int)Math.Clamp(x - (key.X * TileMap.TileSize), 0, TileMap.TileSize - 1);
        var py = (int)Math.Clamp(y - (key.Y * TileMap.TileSize), 0, TileMap.TileSize - 1);
        return tile[(py * TileMap.TileSize) + px];
    }

    /// <summary>World bounds of the tiles a land layer holds, or null when it holds none.</summary>
    public static WorldBounds? Bounds(LandLayer land)
    {
        ArgumentNullException.ThrowIfNull(land);
        WorldBounds? bounds = null;
        foreach (var key in land.GeometryTiles.Keys)
        {
            var rect = new WorldBounds(key.X * TileMap.TileSize, key.Y * TileMap.TileSize,
                (key.X + 1) * TileMap.TileSize, (key.Y + 1) * TileMap.TileSize);
            bounds = bounds is { } current
                ? new WorldBounds(Math.Min(current.Left, rect.Left), Math.Min(current.Top, rect.Top),
                    Math.Max(current.Right, rect.Right), Math.Max(current.Bottom, rect.Bottom))
                : rect;
        }
        return bounds;
    }
}
