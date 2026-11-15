using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// The wheel zoomed into the widest kept camera frame stretched many times: a high frame counter over a blurred
/// picture (owner and USER, 2026-11-15). A kept frame is shown only near its own scale.
/// </summary>
public sealed class CameraFrameStretchTests
{
    private static (CameraFrameCache Cache, SKRect Page) Captured()
    {
        var page = new SKRect(0, 0, 4000, 4000);
        var cache = new CameraFrameCache();
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        surface.Canvas.Scale(.1f); // 400 px show the whole 4000 px page
        surface.Canvas.Clear(SKColors.SteelBlue);
        Assert.True(cache.Capture(surface, surface.Canvas, page));
        return (cache, page);
    }

    [Theory]
    [InlineData(.1f, true)]    // same scale: the pan reuses it
    [InlineData(.12f, true)]   // 1.2x: within the limit
    [InlineData(.2f, false)]   // 2x: the scene is drawn instead
    [InlineData(.8f, false)]   // 8x: never a stretched blur
    public void A_kept_frame_is_shown_only_near_its_own_scale(float scale, bool reused)
    {
        var (cache, page) = Captured();
        using var target = SKSurface.Create(new SKImageInfo(400, 400));
        target.Canvas.Scale(scale);
        var visible = new SKRect(0, 0, 400 / scale, 400 / scale);
        Assert.Equal(reused, cache.TryDraw(target.Canvas, visible, page));
        Assert.Equal(reused ? 0 : 1, cache.RefusedStretched);
    }
}
