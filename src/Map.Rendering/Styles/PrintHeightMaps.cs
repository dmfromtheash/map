using System.Collections.Concurrent;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>Silhouette height for the approved etching. Built once per asset, never stored in a map file.</summary>
internal static class PrintHeightMaps
{
    private static readonly ConcurrentDictionary<string, Lazy<SKImage>> Images = new(StringComparer.Ordinal);

    public static SKImage Get(string sha, SKImage source) => Images.GetOrAdd(sha,
        _ => new Lazy<SKImage>(() => Build(source))).Value;

    private static SKImage Build(SKImage source)
    {
        // Same recipe as MountainHeight in the approved generator. Work at a bounded asset size;
        // 1024 document pixels is the reference width used to place the mockup's mountains.
        var w = Math.Min(1024, source.Width);
        var h = Math.Max(1, (int)Math.Round(w * (double)source.Height / source.Width));
        using var mask = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        source.ScalePixels(mask.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear));
        var bytes = mask.GetPixelSpan();
        var alpha = new float[w * h];
        for (var i = 0; i < alpha.Length; i++) alpha[i] = bytes[i * 4 + 3] / 255f;
        var inward = Lighting.LightMaps.DistanceInside(alpha, w, h, 0.5f);
        using var field = new SKBitmap(new SKImageInfo(w, h, SKColorType.RgbaF16, SKAlphaType.Premul));
        var values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(field.GetPixelSpan());
        for (var x = 0; x < w; x++)
        {
            var depth = 0;
            for (var y = 0; y < h; y++)
            {
                var i = y * w + x;
                var inside = alpha[i] > 0.5f;
                depth = inside ? depth + 1 : 0;
                // Store distances per asset width, so instances of any size use the prototype's
                // 320/140 document-pixel constants (not a different relief at each asset scale).
                values[i * 4] = (Half)(depth / (float)w);
                values[i * 4 + 1] = (Half)(inward[i] / w);
                values[i * 4 + 2] = (Half)(inside ? 1 : 0);
                values[i * 4 + 3] = (Half)1;
            }
        }
        return SKImage.FromBitmap(field);
    }
}
