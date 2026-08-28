using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage10DBrushTipTests
{
    // -------------------------------------------------------------------------- determinism

    [Fact]
    public void The_same_seed_reproduces_exactly_the_same_stroke_tiles()
    {
        var first = PaintDiagonal(RandomizedSettings(seed: 41));
        var second = PaintDiagonal(RandomizedSettings(seed: 41));

        Assert.Equal(
            first.Coverage.Keys.OrderBy(k => (k.X, k.Y)),
            second.Coverage.Keys.OrderBy(k => (k.X, k.Y)));
        foreach (var key in first.Coverage.Keys)
        {
            Assert.Equal(first.Coverage[key], second.Coverage[key]);
        }
    }

    [Fact]
    public void A_different_seed_changes_the_randomized_stroke()
    {
        var first = PaintDiagonal(RandomizedSettings(seed: 41));
        var second = PaintDiagonal(RandomizedSettings(seed: 42));

        Assert.False(SameCoverage(first, second));
    }

    // ---------------------------------------------------------------- scatter and jitters

    [Fact]
    public void Scatter_spreads_dabs_away_from_the_straight_path()
    {
        var neutral = PaintHorizontal(new BrushSettings { Radius = 10, Spacing = 1 });
        var scattered = PaintHorizontal(new BrushSettings
        {
            Radius = 10,
            Spacing = 1,
            Scatter = 1,
            Seed = 5,
        });

        Assert.True(FarthestFromPath(scattered, pathY: 300) > FarthestFromPath(neutral, pathY: 300) + 4);
    }

    [Fact]
    public void Size_jitter_changes_the_coverage_against_the_neutral_stroke()
    {
        var neutral = PaintHorizontal(new BrushSettings { Radius = 10, Spacing = 1 });
        var jittered = PaintHorizontal(new BrushSettings
        {
            Radius = 10,
            Spacing = 1,
            SizeJitter = 0.8,
            Seed = 5,
        });

        Assert.False(SameCoverage(neutral, jittered));
    }

    [Fact]
    public void Opacity_jitter_weakens_some_dabs()
    {
        var neutral = PaintHorizontal(new BrushSettings { Radius = 10, Spacing = 1, Softness = 0 });
        var jittered = PaintHorizontal(new BrushSettings
        {
            Radius = 10,
            Spacing = 1,
            Softness = 0,
            OpacityJitter = 0.9,
            Seed = 5,
        });

        // A hard neutral dab paints 255 at its centre; jitter must leave at least one dab weaker.
        Assert.Contains(jittered.Coverage.Values, tile => tile.Any(v => v is > 0 and < 250));
        Assert.False(SameCoverage(neutral, jittered));
    }

    [Fact]
    public void Random_flips_change_an_asymmetric_tip_stroke()
    {
        var straight = PaintHorizontal(TipSettings() with { Seed = 7 });
        var flipped = PaintHorizontal(TipSettings() with
        {
            Seed = 7,
            RandomFlipHorizontal = true,
            RandomFlipVertical = true,
        });

        Assert.False(SameCoverage(straight, flipped));
    }

    // ------------------------------------------------------------------------- tip geometry

    [Fact]
    public void The_tip_angle_rotates_the_asymmetric_tip()
    {
        // Left half opaque, right half empty: a half-plane survives any resampling details.
        var settings = TipSettings();

        var zero = new RasterStrokeSession(TileMap.Empty(4), settings);
        zero.AddPoint(100, 100);
        var half = new RasterStrokeSession(TileMap.Empty(4), settings with { AngleDegrees = 180 });
        half.AddPoint(100, 100);

        Assert.True(CoverageAt(zero, 92, 100) > 0);
        Assert.Equal(0, CoverageAt(zero, 108, 100));
        Assert.Equal(0, CoverageAt(half, 92, 100));
        Assert.True(CoverageAt(half, 108, 100) > 0);
    }

    // ---------------------------------------------------------------------------- pressure

    [Fact]
    public void Pressure_scales_the_dab_size_only_when_enabled()
    {
        var enabled = new RasterStrokeSession(TileMap.Empty(4), new BrushSettings
        {
            Radius = 16,
            Softness = 0,
            PressureAffectsSize = true,
        });
        enabled.AddPoint(100, 100, pressure: 0.5);

        var disabled = new RasterStrokeSession(TileMap.Empty(4), new BrushSettings
        {
            Radius = 16,
            Softness = 0,
        });
        disabled.AddPoint(100, 100, pressure: 0.5);

        Assert.Equal(0, CoverageAt(enabled, 112, 100));
        Assert.True(CoverageAt(enabled, 104, 100) > 0);
        Assert.True(CoverageAt(disabled, 112, 100) > 0);
    }

    [Fact]
    public void Pressure_scales_the_dab_opacity_only_when_enabled()
    {
        var enabled = new RasterStrokeSession(TileMap.Empty(4), new BrushSettings
        {
            Radius = 16,
            Softness = 0,
            PressureAffectsOpacity = true,
        });
        enabled.AddPoint(100, 100, pressure: 0.5);

        var disabled = new RasterStrokeSession(TileMap.Empty(4), new BrushSettings
        {
            Radius = 16,
            Softness = 0,
        });
        disabled.AddPoint(100, 100, pressure: 0.5);

        Assert.InRange(CoverageAt(enabled, 100, 100), 120, 135);
        Assert.Equal(255, CoverageAt(disabled, 100, 100));
    }

    // -------------------------------------------------------------- one stroke, one entry

    [Fact]
    public void One_randomized_tip_stroke_is_one_undo_entry_over_touched_tiles_only()
    {
        var brushId = LayerId.New();
        var document = new MapDocument(
            DocumentId.New(), "tip", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(4096, 4096, "px", "#FFFFFFFF"),
            [
                new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
                BrushLayer.CreateEmpty(brushId, "paint"),
            ]);
        var session = new EditingSession(document);
        var stroke = new RasterStrokeSession(
            TileMap.Empty(4), RandomizedSettings(seed: 11) with { Radius = 24 });
        for (var x = 400; x <= 900; x += 25)
        {
            stroke.AddPoint(x, 700);
        }

        var (before, after) = stroke.Commit();
        session.Execute(new BrushStrokeCommand(brushId, before, after, "tip stroke"));

        Assert.Equal(1, session.UndoDepth);
        Assert.True(((BrushLayer)session.Document.FindLayer(brushId)!).Tiles.TileCount <= before.Count);
        Assert.True(session.Undo());
        Assert.Equal(0, ((BrushLayer)session.Document.FindLayer(brushId)!).Tiles.TileCount);
        Assert.True(session.Redo());
        Assert.True(((BrushLayer)session.Document.FindLayer(brushId)!).Tiles.TileCount > 0);
    }

    // ----------------------------------------------------------------------------- helpers

    private static BrushSettings RandomizedSettings(int seed) => new()
    {
        Radius = 12,
        Spacing = 0.8,
        Scatter = 0.6,
        SizeJitter = 0.5,
        OpacityJitter = 0.4,
        RandomFlipHorizontal = true,
        RandomFlipVertical = true,
        Seed = seed,
        Tip = HalfPlaneTip(),
    };

    private static BrushSettings TipSettings() => new()
    {
        Radius = 16,
        Softness = 0,
        Spacing = 1,
        Tip = HalfPlaneTip(),
    };

    /// <summary>32x32 alpha tip: opaque left half, empty right half.</summary>
    private static BrushTipShape HalfPlaneTip()
    {
        var alpha = new byte[32 * 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                alpha[(y * 32) + x] = 255;
            }
        }

        return new BrushTipShape(32, 32, alpha);
    }

    private static RasterStrokeSession PaintDiagonal(BrushSettings settings)
    {
        var stroke = new RasterStrokeSession(TileMap.Empty(4), settings);
        for (var step = 0; step <= 10; step++)
        {
            stroke.AddPoint(200 + (step * 30), 200 + (step * 18));
        }

        return stroke;
    }

    private static RasterStrokeSession PaintHorizontal(BrushSettings settings)
    {
        var stroke = new RasterStrokeSession(TileMap.Empty(4), settings);
        for (var x = 200; x <= 500; x += 25)
        {
            stroke.AddPoint(x, 300);
        }

        return stroke;
    }

    private static bool SameCoverage(RasterStrokeSession first, RasterStrokeSession second)
    {
        var keys = first.Coverage.Keys.ToHashSet();
        if (!keys.SetEquals(second.Coverage.Keys))
        {
            return false;
        }

        return keys.All(key => first.Coverage[key].AsSpan().SequenceEqual(second.Coverage[key]));
    }

    private static double FarthestFromPath(RasterStrokeSession stroke, int pathY)
    {
        var farthest = 0.0;
        foreach (var (key, tile) in stroke.Coverage)
        {
            for (var index = 0; index < tile.Length; index++)
            {
                if (tile[index] == 0)
                {
                    continue;
                }

                var worldY = (key.Y * TileMap.TileSize) + (index / TileMap.TileSize) + 0.5;
                farthest = Math.Max(farthest, Math.Abs(worldY - pathY));
            }
        }

        return farthest;
    }

    private static byte CoverageAt(RasterStrokeSession stroke, int worldX, int worldY)
    {
        var key = TileMap.KeyAt(worldX, worldY);
        if (!stroke.Coverage.TryGetValue(key, out var tile))
        {
            return 0;
        }

        var localX = worldX - (key.X * TileMap.TileSize);
        var localY = worldY - (key.Y * TileMap.TileSize);
        return tile[(localY * TileMap.TileSize) + localX];
    }
}
