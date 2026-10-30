using Map.Core.Documents;

namespace Map.Performance;

/// <summary>
/// What a new map gets today, for every map a smoke builds itself: land in the soft watercolour with the owner's snow,
/// the «живая» river, the sea «Рябь штрихом» (DEC-069). Owner, 2026-10-30: the old glossy land and water and the test
/// figures must not get into the checks - the windows a smoke opens are what he sees. A smoke that checks an old
/// style itself (reading an older map, a style switch) names that style where it uses it.
/// </summary>
internal static class CurrentLook
{
    /// <summary>The land of a new layer (<see cref="LandEdgeStyle.SoftWatercolor"/>).</summary>
    public static LandEdgeStyle Land() => LandEdgeStyle.SoftWatercolor();

    /// <summary>The river the editor draws (<see cref="RiverStyle.Natural"/>).</summary>
    public static RiverStyle River() => RiverStyle.Natural();

    /// <summary>
    /// The page of a new map with its sea, as the new-map dialog makes it. The sea is drawn from the shores, so a
    /// map without land and without land drawn in the smoke shows the plain page either way and keeps its own page.
    /// </summary>
    public static BaseColorLayer Page(string name, string color, LayerId? id = null) =>
        new(id ?? LayerId.New(), name, true, false, 1, color) { Sea = SeaStyle.NewMapDefault };

    /// <summary>The page of a new map in its own colour - for a map whose old page stood for paper or ground.</summary>
    public static BaseColorLayer Page(string name = "Цвет фона") => Page(name, PageColor);

    /// <summary>Land of a new map over the whole canvas: valleys, roads and rivers lie on it, not on a bare page.</summary>
    public static LandLayer LandEverywhere(int width, int height, string name = "Земля")
    {
        var full = Enumerable.Repeat((byte)255, TileMap.TileSize * TileMap.TileSize).ToArray();
        var tiles = TileMap.Empty(1);
        for (var x = 0; x * TileMap.TileSize < width; x++)
            for (var y = 0; y * TileMap.TileSize < height; y++)
                tiles = tiles.With(new TileKey(x, y), full);
        return new LandLayer(LayerId.New(), name, true, false, 1, tiles, Land());
    }

    /// <summary>The page colour a new map gets (the first choice of the new-map dialog).</summary>
    public const string PageColor = "#FF1E2430";

    /// <summary>
    /// Colours of the owner's mountains (quartiles of `owner-mountain-01/03/04.png` by brightness), for a shape that
    /// stays because the smoke checks shapes: the ink of the contour, the shadow slate, the lit ice.
    /// </summary>
    public const string MountainInk = "#FF182640", MountainSlate = "#FF52748B", MountainIce = "#FF9ABFD0";
}
