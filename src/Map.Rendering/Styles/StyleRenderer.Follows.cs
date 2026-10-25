using SkiaSharp;
using Map.Core.Documents;

namespace Map.Rendering.Styles;

public static partial class StyleRenderer
{
    internal static bool HasAnimatedLighting(MapDocument document, RenderOptions options) =>
        Lighting.LightingRenderer.Applies(document, options)
        && document.Lighting is { Lights.Enabled: true, Lights.Strength: > 0 } light
        && !light.Sources.IsDefaultOrEmpty && light.Sources.Any(s => s.Flicker && s.Intensity > 0);

    internal static bool FollowsExactly(SKMatrix before, SKMatrix current) =>
        before.ScaleX == current.ScaleX && before.ScaleY == current.ScaleY
        && Math.Abs(current.TransX - before.TransX - MathF.Round(current.TransX - before.TransX)) < 0.0001f
        && Math.Abs(current.TransY - before.TransY - MathF.Round(current.TransY - before.TransY)) < 0.0001f;

    private static SKRect PaddedDestination(SKRectI device, int gutter) =>
        SKRect.Create(device.Left - gutter, device.Top - gutter, device.Width + 2 * gutter, device.Height + 2 * gutter);

    /// <summary>
    /// Maps the kept device raster into the current camera. A view may use it only while it
    /// fits wholly inside the gutter. At its boundary a new frame is built; exports never enter
    /// this path. Zoom reuse is temporary (like light's Follows); settled frames are exact.
    /// </summary>
    internal static bool TryFollow(SKMatrix before, SKRectI savedDevice, int gutter, SKMatrix current,
        SKRectI device, out SKRect destination)
    {
        destination = default;
        if (savedDevice != device || before.ScaleX <= 0 || before.ScaleY <= 0
            || current.SkewX != 0 || current.SkewY != 0 || current.Persp0 != 0 || current.Persp1 != 0) return false;
        var sx = current.ScaleX / before.ScaleX;
        var sy = current.ScaleY / before.ScaleY;
        if (sx is < 0.5f or > 2 || sy is < 0.5f or > 2) return false;
        var padded = PaddedDestination(savedDevice, gutter);
        var dx = current.TransX - sx * before.TransX;
        var dy = current.TransY - sy * before.TransY;
        destination = new SKRect(sx * padded.Left + dx, sy * padded.Top + dy,
            sx * padded.Right + dx, sy * padded.Bottom + dy);
        return destination.Left <= device.Left && destination.Top <= device.Top
            && destination.Right >= device.Right && destination.Bottom >= device.Bottom;
    }
}
