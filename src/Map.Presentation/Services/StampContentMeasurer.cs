using Map.Core.Documents;
using SkiaSharp;

namespace Map.Presentation.Services;

/// <summary>Reads the shared decoded image in place; no bitmap copies or per-object masks.</summary>
public static class StampContentMeasurer
{
    public static StampContentBounds Measure(SKImage image)
    {
        using var pixels = image.PeekPixels();
        if (pixels is null || pixels.AlphaType == SKAlphaType.Opaque
            || pixels.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888)) return StampContentBounds.Full;
        var data = pixels.GetPixelSpan();
        var left = image.Width; var right = -1; var top = image.Height; var bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            var row = data.Slice(y * pixels.RowBytes, image.Width * 4);
            for (var x = 0; x < image.Width; x++)
                if (row[x * 4 + 3] != 0)
                { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = y; }
        }
        // Keep a one-source-pixel fringe for filtering at fractional scales.
        return right < left ? StampContentBounds.Full : new(
            Math.Max(0, left - 1d) / image.Width, Math.Max(0, top - 1d) / image.Height,
            Math.Min(image.Width, right + 2d) / image.Width, Math.Min(image.Height, bottom + 2d) / image.Height);
    }
}
