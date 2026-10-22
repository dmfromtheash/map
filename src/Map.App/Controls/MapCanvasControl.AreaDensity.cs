using System;
using Map.Presentation.ViewModels;
using SkiaSharp;

namespace Map.App.Controls;

/// <summary>
/// R10 density brush on the canvas: while the brush is on, the area's raster is shown inside its
/// boundary - green where it is thicker than set, ochre where thinner - with the brush circle.
/// Only an overlay of the editor; the document and the export never draw it.
/// </summary>
public sealed partial class MapCanvasControl
{
    private static SKImage? _densityImage;
    private static (int Version, int Width, int Height) _densityKey;

    private static void DrawAreaDensityOverlay(SKCanvas canvas, AreaDensityOverlay density, EditorViewModel editor, float scale)
    {
        if (density.Width > 0 && density.Height > 0)
        {
            var key = (density.Version, density.Width, density.Height);
            if (_densityImage is null || _densityKey != key)
            {
                _densityImage?.Dispose();
                var info = new SKImageInfo(density.Width, density.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var pixels = new byte[density.Width * density.Height * 4];
                for (var i = 0; i < density.Values.Length && i < density.Width * density.Height; i++)
                {
                    var v = density.Values[i];
                    var o = i * 4;
                    if (v > 128) { pixels[o] = 90; pixels[o + 1] = 200; pixels[o + 2] = 120; pixels[o + 3] = (byte)Math.Min(170, (v - 128) * 1.4); }
                    else if (v < 128) { pixels[o] = 214; pixels[o + 1] = 150; pixels[o + 2] = 70; pixels[o + 3] = (byte)Math.Min(170, (128 - v) * 1.4); }
                }
                using var bitmap = new SKBitmap();
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
                    _densityImage = SKImage.FromBitmap(bitmap).ToRasterImage(true);
                }
                finally { handle.Free(); }
                _densityKey = key;
            }
            using var clip = new SKPath();
            for (var i = 0; i < density.Nodes.Length; i++)
            {
                var n = density.Nodes[i];
                if (i == 0) clip.MoveTo((float)n.X, (float)n.Y); else clip.LineTo((float)n.X, (float)n.Y);
            }
            clip.Close();
            canvas.Save();
            canvas.ClipPath(clip, antialias: true);
            canvas.DrawImage(_densityImage, new SKRect((float)density.Left, (float)density.Top,
                (float)(density.Left + density.Width * density.CellSize), (float)(density.Top + density.Height * density.CellSize)),
                new SKSamplingOptions(SKFilterMode.Linear));
            canvas.Restore();
        }
        if (editor.AreaDensityBrushPoint is { } point)
        {
            using var ring = new SKPaint { Color = new SKColor(240, 226, 196, 220), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f / scale };
            using var halo = new SKPaint { Color = new SKColor(20, 16, 10, 160), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f / scale };
            canvas.DrawCircle((float)point.X, (float)point.Y, (float)editor.AreaDensityBrushRadius, halo);
            canvas.DrawCircle((float)point.X, (float)point.Y, (float)editor.AreaDensityBrushRadius, ring);
        }
    }
}
