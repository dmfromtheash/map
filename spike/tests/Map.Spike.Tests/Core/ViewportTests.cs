using Map.Spike.Core.Geometry;

namespace Map.Spike.Tests.Core;

public class ViewportTests
{
    [Theory]
    [InlineData(400, 300, 1.25)]
    [InlineData(0, 0, 0.8)]
    [InlineData(799, 1, 4.0)]
    public void ZoomAt_keeps_the_world_point_under_the_cursor_fixed(double sx, double sy, double factor)
    {
        var viewport = new Viewport();
        viewport.Resize(800, 600);
        viewport.Set(new Vec2(123.5, -44.25), 0.7);

        var anchorScreen = new Vec2(sx, sy);
        var worldBefore = viewport.ScreenToWorld(anchorScreen);

        viewport.ZoomAt(anchorScreen, factor);

        var worldAfter = viewport.ScreenToWorld(anchorScreen);
        Assert.Equal(worldBefore.X, worldAfter.X, 9);
        Assert.Equal(worldBefore.Y, worldAfter.Y, 9);
    }

    [Fact]
    public void Zoom_is_clamped_and_repeated_zoom_out_does_not_invert_the_view()
    {
        var viewport = new Viewport();
        viewport.Resize(800, 600);

        for (var i = 0; i < 500; i++)
        {
            viewport.ZoomAt(new Vec2(400, 300), 0.5);
        }

        Assert.Equal(Viewport.MinScale, viewport.Scale, 9);

        for (var i = 0; i < 500; i++)
        {
            viewport.ZoomAt(new Vec2(400, 300), 2.0);
        }

        Assert.Equal(Viewport.MaxScale, viewport.Scale, 9);
    }

    [Fact]
    public void Pan_moves_content_with_the_pointer()
    {
        var viewport = new Viewport();
        viewport.Resize(800, 600);
        viewport.Set(Vec2.Zero, 2.0);

        var worldAtOrigin = viewport.ScreenToWorld(Vec2.Zero);
        viewport.PanByScreenDelta(100, 0);

        // Dragging right by 100 screen px at scale 2 shows content 50 world units further left.
        Assert.Equal(worldAtOrigin.X - 50, viewport.ScreenToWorld(Vec2.Zero).X, 9);
    }

    [Fact]
    public void VisibleWorldBounds_matches_the_screen_rectangle()
    {
        var viewport = new Viewport();
        viewport.Resize(640, 480);
        viewport.Set(new Vec2(100, 200), 0.5);

        var bounds = viewport.VisibleWorldBounds();

        Assert.Equal(100, bounds.X, 9);
        Assert.Equal(200, bounds.Y, 9);
        Assert.Equal(1280, bounds.Width, 9);
        Assert.Equal(960, bounds.Height, 9);
    }
}
