using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>
/// PC trace 2026-11-15 (wheel at 10 000 objects): each wheel notch past the pan image's 0.8-1.6x limit rebuilt the
/// layer over the view and its margins and flushed it (7.5 s of draws, 3.6 s of GPU flush). While the scale keeps
/// changing the layer is drawn directly; a scale that holds builds the image as before.
/// </summary>
public sealed class PanImageZoomTests
{
    [Fact]
    public void A_zoom_past_the_stand_in_limit_draws_directly_and_a_held_scale_builds_again()
    {
        using var cache = new LayerImageCache();
        var layer = LayerId.New();
        object[] parts = ["layer"];
        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        var builds = 0;
        bool Frame(float scale)
        {
            var canvas = surface.Canvas;
            canvas.ResetMatrix();
            canvas.Scale(scale);
            return cache.TryDrawPanned(canvas, layer, parts, null, 32, (target, _) => { builds++; target.Clear(SKColors.Green); },
                () => true, allowScale: true);
        }

        Assert.True(Frame(1f));        // the gesture's first frame builds once
        Assert.Equal(1, builds);
        Assert.True(Frame(1.2f));      // within 1.6x: the image stands in
        Assert.Equal(1, builds);
        Assert.False(Frame(2.5f));     // past the limit while zooming: drawn directly, no rebuild
        Assert.False(Frame(4f));
        Assert.Equal(1, builds);
        Assert.Equal(2, cache.ZoomRebuildsSkipped);
        Assert.True(Frame(4f));        // the scale holds (a pan or the stop): built again
        Assert.Equal(2, builds);
    }
}
