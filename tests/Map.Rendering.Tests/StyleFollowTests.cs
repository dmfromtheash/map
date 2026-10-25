using Map.Rendering.Styles;
using SkiaSharp;

namespace Map.Rendering.Tests;

public sealed class StyleFollowTests
{
    private delegate bool Follow(SKMatrix before, SKRectI savedDevice, int gutter, SKMatrix current,
        SKRectI device, out SKRect destination);
    // Exercise the cache's coordinate contract without adding a public rendering API for tests.
    private static readonly Follow TryFollow = typeof(StyleRenderer).GetMethod("TryFollow",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.CreateDelegate<Follow>();
    private static readonly Func<SKMatrix, SKMatrix, bool> FollowsExactly = typeof(StyleRenderer).GetMethod("FollowsExactly",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.CreateDelegate<Func<SKMatrix, SKMatrix, bool>>();

    [Fact]
    public void Pan_and_zoom_follow_document_coordinates_without_exposing_the_buffer_edge()
    {
        var device = new SKRectI(0, 0, 2048, 1095);
        var before = new SKMatrix(.41f, 0, 100, 0, .41f, -30, 0, 0, 1);
        var pan = before; pan.TransX -= 400; pan.TransY -= 160;
        Assert.True(TryFollow(before, device, 512, pan, device, out var destination));
        Assert.True(FollowsExactly(before, pan));
        var fractional = pan; fractional.TransX += .25f;
        Assert.False(FollowsExactly(before, fractional));
        // A document point has exactly the same screen position through the followed image.
        var doc = new SKPoint(800, 600);
        var old = before.MapPoint(doc);
        var expected = pan.MapPoint(doc);
        Assert.Equal(expected.X, destination.Left + old.X + 512, precision: 3);
        Assert.Equal(expected.Y, destination.Top + old.Y + 512, precision: 3);
        var zoom = SKMatrix.Concat(SKMatrix.CreateScale(1.2f, 1.2f, 1024, 547), before);
        Assert.True(TryFollow(before, device, 512, zoom, device, out _));
        Assert.False(FollowsExactly(before, zoom));
        pan.TransX -= 200;
        Assert.False(TryFollow(before, device, 512, pan, device, out _));
        Assert.False(TryFollow(before, device, 512, before, new SKRectI(0, 0, 1800, 1095), out _));
        var tooFar = SKMatrix.Concat(SKMatrix.CreateScale(3, 3), before);
        Assert.False(TryFollow(before, device, 512, tooFar, device, out _));
    }
}
