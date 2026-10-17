using SkiaSharp;

namespace Map.Rendering;

/// <summary>Exact pixel copies into a stamp atlas; the GPU samples the atlas bilinearly.</summary>
internal static class StampMipSampling
{
    public static void CopyClamped(SKCanvas canvas, SKImage image, SKRect slot)
    {
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Nearest), SKMatrix.CreateTranslation(slot.Left, slot.Top));
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        var padded = slot; padded.Inflate(2, 2);
        canvas.DrawRect(padded, paint);
    }
}
