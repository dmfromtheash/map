using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

/// <summary>Rounding the corners of a rough stroke: crisp, bounded, seen in the preview and committed alike.</summary>
public sealed class CoverageSmoothingTests
{
    [Fact]
    public void A_square_loses_its_tips_and_a_notch_is_filled()
    {
        var tile = new byte[256 * 256];
        for (var y = 40; y < 140; y++)
            for (var x = 40; x < 140; x++)
                if (!(x >= 86 && x < 94 && y < 60)) tile[y * 256 + x] = 255; // a square with a narrow notch
        var source = new Dictionary<TileKey, byte[]> { [new TileKey(0, 0)] = tile };

        var rounded = CoverageSmoothing.Round(source, 8);

        var result = rounded[new TileKey(0, 0)];
        Assert.Equal(0, result[40 * 256 + 40]);      // the tip of the corner is gone
        Assert.Equal(255, result[90 * 256 + 90]);    // the interior stays
        Assert.Equal(255, result[56 * 256 + 90]);    // the notch is closed below its mouth
        Assert.Equal(255, result[40 * 256 + 60]);    // a straight edge keeps its line
        Assert.Equal(0, result[39 * 256 + 60]);
        Assert.All(result, value => Assert.True(value is 0 or 255));
        Assert.Single(rounded);
    }

    [Fact]
    public void Rounding_crosses_tile_edges_and_leaves_no_empty_tiles()
    {
        var settings = new BrushSettings { Radius = 70, RoughCellSize = 40, RoughSmoothing = 1 };
        var stroke = new RasterStrokeSession(TileMap.Empty(1), settings);
        stroke.AddPoint(250, 250);
        stroke.AddPoint(270, 262);

        var raw = stroke.Coverage;
        var smooth = stroke.CommittedCoverage;

        Assert.NotSame(raw, smooth);
        Assert.All(smooth.Values, tile => Assert.Contains(tile, value => value != 0));
        Assert.True(smooth.Keys.Count(key => key.X is 0 or 1 && key.Y is 0 or 1) >= 2);
        var rawCount = raw.Values.Sum(t => t.Count(v => v > 0));
        var smoothCount = smooth.Values.Sum(t => t.Count(v => v > 0));
        Assert.InRange(smoothCount, rawCount * .8, rawCount * 1.2);

        var (_, after) = stroke.CommitCoverage(missingTileValue: 0, subtract: false);
        foreach (var (key, tile) in smooth)
        {
            Assert.Equal(tile, after[key]);
        }
    }

    [Fact]
    public void Incremental_rounding_during_a_stroke_equals_the_full_rounding()
    {
        foreach (var (cell, smoothing) in new[] { (40.0, 1.0), (64.0, .5), (24.0, .3), (160.0, 1.0) })
        {
            var settings = new BrushSettings { Radius = 48, RoughCellSize = cell, RoughSmoothing = smoothing, Spacing = .25 };
            var stroke = new RasterStrokeSession(TileMap.Empty(1), settings);
            var random = new Random(2026);
            double x = 300, y = 300;
            for (var step = 0; step < 40; step++)
            {
                x += random.NextDouble() * 60 - 20; y += random.NextDouble() * 40 - 10;
                stroke.AddPoint(x, y);
                var incremental = stroke.CommittedCoverage;
                var full = CoverageSmoothing.Round(stroke.Coverage, settings.RoughSmoothingRadius);
                Assert.Equal(full.Keys.OrderBy(k => (k.X, k.Y)), incremental.Keys.OrderBy(k => (k.X, k.Y)));
                foreach (var (key, tile) in full) Assert.True(tile.AsSpan().SequenceEqual(incremental[key]), $"tile {key} differs at step {step}, cell {cell}");
                Assert.Equal(incremental.Keys.ToHashSet(), stroke.CommittedVersions.Keys.ToHashSet());
            }
        }
    }

    [Fact]
    public void Versions_change_only_for_tiles_a_dab_wrote()
    {
        var stroke = new RasterStrokeSession(TileMap.Empty(1), new BrushSettings { Radius = 20, Softness = 0 });
        stroke.AddPoint(100, 100);
        var first = stroke.Versions.ToDictionary(p => p.Key, p => p.Value);
        stroke.AddPoint(100, 100); // the same hard dab again changes nothing
        Assert.Equal(first, stroke.Versions);
        stroke.AddPoint(1000, 1000);
        Assert.True(stroke.Versions[new TileKey(3, 3)] > first[new TileKey(0, 0)]);
        Assert.All(stroke.Coverage.Values, tile => Assert.Contains(tile, v => v != 0)); // no empty tiles are materialized
    }

    [Fact]
    public void Plain_strokes_are_untouched()
    {
        var stroke = new RasterStrokeSession(TileMap.Empty(1), new BrushSettings { Radius = 30, Softness = .5 });
        stroke.AddPoint(100, 100);
        Assert.Same(stroke.Coverage, stroke.CommittedCoverage);
        Assert.Same(stroke.Coverage, CoverageSmoothing.Round(stroke.Coverage, 0));
    }
}
