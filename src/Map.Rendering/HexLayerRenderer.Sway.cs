using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

public static partial class HexLayerRenderer
{
    /// <summary>
    /// Draws a kept chunk picture with its trees swaying, when the picture carries trees and their
    /// mask is kept (asked of the worker otherwise). False when it was not drawn: the caller draws
    /// the picture as it is.
    /// </summary>
    private static bool DrawSwaying(SKCanvas canvas, HexLayer layer, HexChunkKey key, HexChunk chunk, HexLook look, SKImage image,
        int level, WorldBounds bounds, HexChunkCache cache, HexChunkContext? context, bool merged, double seconds, double strength = 1)
    {
        // Trees are in the ground picture of a chunk no path crosses, else in its features picture.
        if (!(look == HexLook.Features || (look == HexLook.Terrain && merged)) || !Info(chunk).Trees) return false;
        var board = layer.Board;
        var maskLevel = level - 1;
        var mask = cache.TryGet(chunk, board.Orientation, maskLevel, HexLook.Sway, context);
        var maskScale = Math.Pow(2, maskLevel);
        var maskBounds = Snapped(PictureBounds(board, key, HexLook.Sway), maskScale);
        if (mask is null)
        {
            if (cache.BuildInBackground)
            {
                if (!cache.IsQueued(chunk, board.Orientation, maskLevel, HexLook.Sway))
                {
                    EnqueueBuild(layer, key, chunk, HexLook.Sway, maskLevel, cache, context, merged);
                }

                return false;
            }

            // Without a worker (tests, a single frame) the mask is drawn here.
            var width = (int)Math.Ceiling((maskBounds.Right - maskBounds.Left) * maskScale);
            var height = (int)Math.Ceiling((maskBounds.Bottom - maskBounds.Top) * maskScale);
            if (width <= 0 || height <= 0 || width > HexChunkCache.MaxImageSide || height > HexChunkCache.MaxImageSide) return false;
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return false;
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)maskScale);
            surface.Canvas.Translate((float)-maskBounds.Left, (float)-maskBounds.Top);
            DrawChunk(surface.Canvas, layer, key, chunk, HexLook.Sway, clip: null, maskScale, merged);
            mask = surface.Snapshot();
            cache.Add(chunk, board.Orientation, maskLevel, HexLook.Sway, mask, context);
        }

        var scale = Math.Pow(2, level);
        using var picture = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, ChunkSampling,
            SKMatrix.CreateScale((float)(1 / scale), (float)(1 / scale)).PostConcat(SKMatrix.CreateTranslation((float)bounds.Left, (float)bounds.Top)));
        using var swayMask = mask.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, ChunkSampling,
            SKMatrix.CreateScale((float)(1 / maskScale), (float)(1 / maskScale)).PostConcat(SKMatrix.CreateTranslation((float)maskBounds.Left, (float)maskBounds.Top)));
        using var shader = HexTreeSway.Create(picture, swayMask, seconds, board.Layout.Size * 0.05 * strength);
        if (shader is null) return false;
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(new SKRect((float)bounds.Left, (float)bounds.Top,
            (float)(bounds.Left + image.Width / scale), (float)(bounds.Top + image.Height / scale)), paint);
        return true;
    }
}
