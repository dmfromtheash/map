using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Renders the document preview stored as preview.png. Runs on a CPU raster surface: a thumbnail
/// must not depend on a window or a live GPU context. It uses the same scene evaluator as the
/// editor canvas and never includes selection overlay or any other editor chrome.
/// </summary>
public static class ThumbnailRenderer
{
    public const int Width = 320;
    public const int Height = 180;

    public static byte[] RenderPng(MapDocument document, int width = Width, int height = Height,
        IStampImageSource? stamps = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Could not create a {width}x{height} raster surface.");

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Fit the whole page into the thumbnail, centred.
        var scale = Math.Min(
            width / (double)document.Canvas.Width,
            height / (double)document.Canvas.Height);
        var dx = (width - (document.Canvas.Width * scale)) / 2;
        var dy = (height - (document.Canvas.Height * scale)) / 2;

        canvas.Save();
        canvas.Translate((float)dx, (float)dy);
        canvas.Scale((float)scale);

        SceneRenderer.Render(canvas, document, new RenderOptions
        {
            IsExport = true, Stamps = stamps, ViewScale = scale,
            VisibleWorld = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height),
        }); // no editor-only grid or chrome

        canvas.Restore();
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
