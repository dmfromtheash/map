using Map.Core.Documents;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>Reads the shared decoded image in place; no bitmap copies or per-object masks.</summary>
public static class StampContentMeasurer
{
    public sealed record Measurement(StampContentBounds Bounds, StampContentMask? Mask);
    public static StampContentBounds Measure(SKImage image) => Read(image).Bounds;
    public static Measurement Read(SKImage image)
    {
        using var pixels = image.PeekPixels();
        if (pixels is null || pixels.AlphaType == SKAlphaType.Opaque
            || pixels.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888)) return new(StampContentBounds.Full, null);
        var data = pixels.GetPixelSpan();
        var mask = new uint[StampContentMask.Edge];
        var left = image.Width; var right = -1; var top = image.Height; var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            var row = data.Slice(y * pixels.RowBytes, image.Width * 4);
            for (var x = 0; x < image.Width; x++)
                if (row[x * 4 + 3] != 0)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = y;
                    if (row[x * 4 + 3] >= 32)
                        for (var my = y * StampContentMask.Edge / image.Height; my <= ((y + 1) * StampContentMask.Edge - 1) / image.Height; my++)
                            for (var mx = x * StampContentMask.Edge / image.Width; mx <= ((x + 1) * StampContentMask.Edge - 1) / image.Width; mx++)
                                mask[my] |= 1u << mx;
                }
        }
        // Keep a one-source-pixel fringe for filtering at fractional scales.
        var bounds = right < left ? StampContentBounds.Full : new StampContentBounds(
            Math.Max(0, left - 1d) / image.Width, Math.Max(0, top - 1d) / image.Height,
            Math.Min(image.Width, right + 2d) / image.Width, Math.Min(image.Height, bottom + 2d) / image.Height);
        return new(bounds, mask.Any(r => r != 0) ? new([.. mask]) : null);
    }
}
