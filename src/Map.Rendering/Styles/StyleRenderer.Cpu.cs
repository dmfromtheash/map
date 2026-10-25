using SkiaSharp;

namespace Map.Rendering.Styles;

public static partial class StyleRenderer
{
    /// <summary>
    /// The expensive painter export pass is pixel-local. Rasterise independent full-width
    /// bands into one allocation, with bounded workers rather than a full frame per worker.
    /// The shader and its raster inputs are immutable; each worker owns its canvas and paint.
    /// </summary>
    private static SKImage? ShadeCpuBands(SKShader shader, int width, int height, int margin)
    {
        const int bandHeight = 64;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var pixels = new SKBitmap(info);
        var address = pixels.GetPixels();
        if (address == IntPtr.Zero) return null;
        pixels.Erase(SKColors.Transparent);
        var rowBytes = pixels.RowBytes;
        Parallel.For(0, (height + bandHeight - 1) / bandHeight,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, band =>
            {
                var top = band * bandHeight;
                var rows = Math.Min(bandHeight, height - top);
                using var surface = SKSurface.Create(info.WithSize(width, rows),
                    IntPtr.Add(address, checked(top * rowBytes)), rowBytes)!;
                // Only integral raster translations: every lane sees exactly the original
                // padded pixel centre. Keep full-width rows (including SIMD tail lanes).
                surface.Canvas.Translate(-margin, -margin - top);
                using var paint = new SKPaint { Shader = shader };
                surface.Canvas.DrawRect(SKRect.Create(margin, margin, width, height), paint);
            });
        // All writers have joined. The image retains the immutable allocation after this
        // bitmap wrapper is disposed; no copy or mutable shared frame escapes the pass.
        pixels.SetImmutable();
        return SKImage.FromBitmap(pixels);
    }
}
