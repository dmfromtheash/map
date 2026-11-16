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

    /// <summary>
    /// USER (HD 630, 100 000 objects, 2026-11-16): with every moving frame drawing the scene (18-24 ms) the wheel ran at
    /// 34 FPS / 1 % low 10; unlimited stretching gave 60 FPS with heavy blur. A slow scene may stretch the kept frame up
    /// to 4x, but every 4th such frame draws the scene again; a fast scene keeps the 1.25x limit.
    /// </summary>
    [Fact]
    public void A_slow_scene_stretches_the_kept_frame_and_redraws_every_fourth_frame()
    {
        var (cache, page) = Captured();
        using var target = SKSurface.Create(new SKImageInfo(400, 400));
        bool Frame(float scale)
        {
            target.Canvas.ResetMatrix();
            target.Canvas.Scale(scale);
            return cache.TryDraw(target.Canvas, new SKRect(0, 0, 400 / scale, 400 / scale), page);
        }

        cache.NoteMovingScene(3);                 // fast machine
        Assert.False(cache.SlowScene);
        Assert.False(Frame(.2f));                 // 2x: drawn, as before

        cache.NoteMovingScene(20);                // a slow moving scene switches at once
        Assert.True(cache.SlowScene && cache.KeepMovingFrames);
        var shown = Enumerable.Range(0, 8).Select(_ => Frame(.2f)).ToArray();
        Assert.Equal([true, true, true, false, true, true, true, false], shown);
        Assert.Equal(2, cache.SlowRefreshes);
        Assert.False(Frame(.8f));                 // 8x stays beyond even the slow limit
        Assert.True(Frame(.12f));                 // within 1.25x never counts towards a refresh
    }
}
