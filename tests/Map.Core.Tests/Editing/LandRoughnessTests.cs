using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>A rough land dab is the union of map-wide cells, not a circle: crisp, jagged, seamless.</summary>
public sealed class LandRoughnessTests
{
    [Fact]
    public void Rough_dab_is_crisp_jagged_and_stays_within_reach_of_the_circle()
    {
        var settings = new BrushSettings { Radius = 60, Softness = .8, RoughCellSize = 24 };
        var stroke = new RasterStrokeSession(TileMap.Empty(1), settings);
        stroke.AddPoint(300, 300);

        var painted = new List<(double X, double Y)>();
        var distinct = new HashSet<byte>();
        foreach (var (key, tile) in stroke.Coverage)
        {
            for (var i = 0; i < tile.Length; i++)
            {
                if (tile[i] == 0) continue;
                distinct.Add(tile[i]);
                painted.Add((key.X * TileMap.TileSize + (i % TileMap.TileSize) + .5,
                    key.Y * TileMap.TileSize + (i / TileMap.TileSize) + .5));
            }
        }

        // Crisp: cells are fully in or fully out, softness plays no part.
        Assert.Equal([255], distinct);
        // Jagged: some pixels lie outside the circle and some inside are left out.
        Assert.Contains(painted, p => Distance(p, (300, 300)) > 60);
        var plain = new RasterStrokeSession(TileMap.Empty(1), settings with { RoughCellSize = 0, Softness = 0 });
        plain.AddPoint(300, 300);
        var plainCount = plain.Coverage.Values.Sum(tile => tile.Count(v => v > 0));
        Assert.NotEqual(plainCount, painted.Count);
        Assert.InRange(painted.Count, plainCount * .6, plainCount * 1.6);
        // Bounded: never farther than the cell reach beyond the circle.
        Assert.All(painted, p => Assert.True(Distance(p, (300, 300)) <= 60 + 24 * RoughCells.Reach));
    }

    [Fact]
    public void Cells_are_a_property_of_the_map_so_overlapping_dabs_agree()
    {
        var settings = new BrushSettings { Radius = 40, RoughCellSize = 32, Seed = 5 };
        var one = new RasterStrokeSession(TileMap.Empty(1), settings);
        one.AddPoint(200, 200);
        var two = new RasterStrokeSession(TileMap.Empty(1), settings with { Seed = 99 });
        two.AddPoint(230, 200);

        // A pixel both dabs claim belongs to the same cell: the first stroke is a subset of the
        // union, and no pixel of the overlap is painted by one dab yet excluded by the other for
        // a reason other than its own cell centre lying outside that dab's circle.
        foreach (var (key, tile) in one.Coverage)
        {
            if (!two.Coverage.TryGetValue(key, out var other)) continue;
            for (var i = 0; i < tile.Length; i++)
            {
                if (tile[i] == 0 || other[i] != 0) continue;
                var x = key.X * TileMap.TileSize + (i % TileMap.TileSize) + .5;
                var y = key.Y * TileMap.TileSize + (i / TileMap.TileSize) + .5;
                Assert.False(RoughCells.InsideRoughCircle(x, y, 230, 200, 40, 32));
            }
        }

        Assert.Equal(RoughCells.NearestSite(1234.5, -77.25, 48), RoughCells.NearestSite(1234.5, -77.25, 48));
        Assert.NotEqual(RoughCells.Site(3, 4, 48), RoughCells.Site(4, 3, 48));
    }

    [Fact]
    public void Every_point_has_a_site_within_reach()
    {
        for (var y = -5; y < 5; y++)
        {
            for (var x = -5; x < 5; x++)
            {
                var worldX = x * 37.3 + 11.1;
                var worldY = y * 29.7 - 4.2;
                var site = RoughCells.NearestSite(worldX, worldY, 40);
                Assert.True(Distance(site, (worldX, worldY)) <= 40 * RoughCells.Reach);
            }
        }
    }

    [Fact]
    public void Rough_coverage_commits_to_land_and_subtracts_with_the_same_cells()
    {
        var settings = new BrushSettings { Radius = 50, RoughCellSize = 40 };
        var add = new RasterStrokeSession(TileMap.Empty(1), settings);
        add.AddPoint(400, 400);
        var (_, added) = add.CommitCoverage(missingTileValue: 0, subtract: false);
        var land = TileMap.Empty(1).WithMany(added);
        Assert.True(land.TileCount > 0);

        var cut = new RasterStrokeSession(land, settings);
        cut.AddPoint(400, 400);
        var (_, removed) = cut.CommitCoverage(missingTileValue: 0, subtract: true);
        Assert.All(removed.Values, tile => Assert.Null(tile));
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
