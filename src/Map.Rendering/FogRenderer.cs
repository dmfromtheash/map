using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// R27: the fog of war of a painted map, drawn over the finished scene. The players see the hidden
/// cells as an opaque, slightly mottled dark ground; the game master sees the same fog as a veil
/// over the map. The cells of the view become one small image (a pixel per cell, fewer when far
/// away) drawn smooth, so the edge of what was seen is soft; the image is kept while neither the
/// fog nor the cells in view change.
/// </summary>
internal static class FogRenderer
{
    /// <summary>The colour of unknown land: dark warm ink, like the margin of an old map.</summary>
    private static readonly SKColor Ink = new(0x24, 0x1F, 0x1B);
    private const int MaxSide = 1024;

    private static readonly object Gate = new();
    private static (MapFog Fog, int Left, int Top, int Width, int Height, int Step, byte Alpha) _key;
    private static SKImage? _image;

    public static void Draw(SKCanvas canvas, MapDocument document, WorldBounds view, double opacity, MapFog? stroke = null)
    {
        var fog = stroke ?? document.Fog;
        if (!fog.Enabled || opacity <= 0) return;
        var cellsX = (int)Math.Ceiling(document.Canvas.Width / (double)MapFog.Cell);
        var cellsY = (int)Math.Ceiling(document.Canvas.Height / (double)MapFog.Cell);
        // Three cells of margin: the softened edge must not end at the border of the view.
        var left = Math.Clamp((int)Math.Floor(view.Left / MapFog.Cell) - 3, 0, cellsX);
        var top = Math.Clamp((int)Math.Floor(view.Top / MapFog.Cell) - 3, 0, cellsY);
        var right = Math.Clamp((int)Math.Ceiling(view.Right / MapFog.Cell) + 3, 0, cellsX);
        var bottom = Math.Clamp((int)Math.Ceiling(view.Bottom / MapFog.Cell) + 3, 0, cellsY);
        if (right <= left || bottom <= top) return;
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(right - left, bottom - top) / (double)MaxSide));
        // Whole steps, so a pan does not shift which cells are sampled.
        left -= left % step; top -= top % step;
        var width = (right - left + step - 1) / step;
        var height = (bottom - top + step - 1) / step;
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);

        lock (Gate)
        {
            var key = (fog, left, top, width, height, step, alpha);
            if (_image is null || !ReferenceEquals(_key.Fog, fog) || _key != key)
            {
                _image?.Dispose();
                _image = Build(fog, left, top, width, height, step, alpha);
                _key = key;
            }

            var destination = new SKRect(left * MapFog.Cell, top * MapFog.Cell, (left + (width * step)) * MapFog.Cell, (top + (height * step)) * MapFog.Cell);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(_image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
        }
    }

    private static SKImage Build(MapFog fog, int left, int top, int width, int height, int step, byte alpha)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        // Hidden = 1 per cell, then two small box blurs: the edge of what was seen fades over about
        // two cells instead of stepping cell by cell.
        var cover = new float[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            cover[(y * width) + x] = fog.IsRevealedCell(left + (x * step), top + (y * step)) ? 0 : 1;
        cover = Soften(Soften(cover, width, height), width, height);

        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var coverage = cover[(y * width) + x];
            if (coverage <= 0.002f) continue;
            var (cx, cy) = (left + (x * step), top + (y * step));
            // A faint mottle, fixed per cell, so the unknown is ground and not a flat card.
            var hash = (uint)((cx * 73856093) ^ (cy * 19349663));
            hash ^= hash >> 13; hash *= 0x5bd1e995; hash ^= hash >> 15;
            var shade = 1 + (((hash & 0xFF) / 255.0) - .5) * .18;
            var a = alpha / 255.0 * coverage;
            var r = (byte)Math.Clamp(Ink.Red * shade * a, 0, 255);
            var g = (byte)Math.Clamp(Ink.Green * shade * a, 0, 255);
            var b = (byte)Math.Clamp(Ink.Blue * shade * a, 0, 255);
            pixels[(y * width) + x] = (uint)(r | (g << 8) | (b << 16) | ((uint)Math.Round(a * 255) << 24));
        }

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>A 3×3 box blur, the border repeated.</summary>
    private static float[] Soften(float[] source, int width, int height)
    {
        var result = new float[source.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var sum = 0f;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                sum += source[(Math.Clamp(y + dy, 0, height - 1) * width) + Math.Clamp(x + dx, 0, width - 1)];
            result[(y * width) + x] = sum / 9;
        }
        return result;
    }
}
