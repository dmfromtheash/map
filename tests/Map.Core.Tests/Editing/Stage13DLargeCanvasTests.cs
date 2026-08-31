using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage13DLargeCanvasTests
{
    [Fact]
    public void Public_canvas_policy_stops_at_131k_while_262k_remains_test_only()
    {
        Assert.Equal(131_072, CanvasDimensionPolicy.Maximum);
        Assert.True(CanvasDimensionPolicy.IsValid(131_072));
        Assert.False(CanvasDimensionPolicy.IsValid(131_073));
        Assert.False(CanvasDimensionPolicy.IsValid(262_144));

        // The domain model can materialize a historical or test-only snapshot, but every public
        // creator/resizer is governed by CanvasDimensionPolicy.
        var testOnly = new CanvasDefinition(262_144, 262_144, "px", "#FFFFFFFF");
        Assert.Equal(262_144, testOnly.Width);
    }

    [Theory]
    [InlineData(131_072)]
    [InlineData(262_144)]
    public void Dynamic_fit_pan_and_zoom_keep_large_coordinates_finite_and_exact(int size)
    {
        var viewport = new Viewport();
        viewport.Resize(1_280, 720);

        viewport.FitTo(size, size);

        Assert.InRange(size * viewport.Scale, 0, 720.000001);
        var canvasCentre = viewport.WorldToScreen(size / 2.0, size / 2.0);
        Assert.Equal(640, canvasCentre.X, 6);
        Assert.Equal(360, canvasCentre.Y, 6);

        viewport.PanByScreenDelta(37, -19);
        var anchorBefore = viewport.ScreenToWorld(777, 333);
        viewport.ZoomAt(777, 333, 1.25);
        var anchorAfter = viewport.ScreenToWorld(777, 333);
        Assert.Equal(anchorBefore.X, anchorAfter.X, 7);
        Assert.Equal(anchorBefore.Y, anchorAfter.Y, 7);

        var farScreen = viewport.WorldToScreen(size - 1, size - 1);
        var farWorld = viewport.ScreenToWorld(farScreen.X, farScreen.Y);
        Assert.Equal(size - 1, farWorld.X, 6);
        Assert.Equal(size - 1, farWorld.Y, 6);
        Assert.True(double.IsFinite(viewport.OffsetX));
        Assert.True(double.IsFinite(viewport.OffsetY));
        Assert.True(double.IsFinite(viewport.Scale));
    }

    [Fact]
    public void Sparse_tiles_and_checked_linear_math_cover_131k_and_test_only_262k_boundaries()
    {
        var pixels = new byte[TileMap.TileSize * TileMap.TileSize];
        pixels[0] = 255;
        var publicFar = TileMap.KeyAt(131_071, 131_071);
        var testOnlyFar = TileMap.KeyAt(262_143, 262_143);
        var tiles = TileMap.Empty(1)
            .With(publicFar, pixels)
            .With(testOnlyFar, pixels);

        Assert.Equal(new TileKey(511, 511), publicFar);
        Assert.Equal(new TileKey(1_023, 1_023), testOnlyFar);
        Assert.Equal([testOnlyFar], tiles.KeysIntersecting(
            262_143, 262_143, 262_143, 262_143));
        Assert.Equal(2, tiles.TileCount);
        Assert.Equal(2L * TileMap.TileSize * TileMap.TileSize, tiles.TotalBytes);

        var area = checked((long)262_144 * 262_144);
        var lastPixel = checked(((long)262_144 * 262_143) + 262_143);
        Assert.Equal(68_719_476_736L, area);
        Assert.Equal(area - 1, lastPixel);
    }
}
