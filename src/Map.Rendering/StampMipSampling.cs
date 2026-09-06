using System.Runtime.InteropServices;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>GPU sampling from the CPU evaluator's mip pixels, including odd-sized sources.
/// The driver's automatic mip builder uses a different odd-size filter and rounding.</summary>
internal static class StampMipSampling
{
    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
        SKRuntimeEffect.CreateShader("""
            uniform shader sheet;
            uniform float4 upper[8];
            uniform float4 lower[8];
            uniform float weights[8];
            half4 main(float2 p) {
                for (int i = 0; i < 8; ++i) {
                    float4 u = upper[i];
                    if (u.z > 0 && p.x >= u.x && p.y >= u.y && p.x <= u.x + u.z && p.y <= u.y + u.w) {
                        float4 l = lower[i];
                        float2 q = l.xy + (p - u.xy) * l.zw / u.zw;
                        return mix(sheet.eval(p), sheet.eval(q), weights[i]);
                    }
                }
                return half4(0);
            }
            """, out var error) ?? throw new InvalidOperationException(error));

    public static float Weight(SKImage image, double pixelWidth, double pixelHeight)
    {
        var scale = (float)Math.Min(pixelWidth / image.Width, pixelHeight / image.Height);
        // Skia's CPU mip accessor includes the same sharpen bias in ComputeLevel.
        return Math.Clamp(-MathF.Log2(Math.Max(float.Epsilon, scale)) - .5f, 0, 1);
    }

    public static SKImage Half(SKImage image)
    {
        var width = Math.Max(1, image.Width / 2); var height = Math.Max(1, image.Height / 2);
        using var source = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!image.ReadPixels(source.Info, source.GetPixels(), source.RowBytes))
            throw new InvalidOperationException("The stamp's mip pixels could not be read.");
        var bytes = new byte[source.ByteCount];
        Marshal.Copy(source.GetPixels(), bytes, 0, bytes.Length);
        var pixels = new byte[checked(width * height * 4)];
        var nx = image.Width == 1 ? 1 : image.Width % 2 == 0 ? 2 : 3;
        var ny = image.Height == 1 ? 1 : image.Height % 2 == 0 ? 2 : 3;
        var divisor = (nx == 3 ? 4 : nx) * (ny == 3 ? 4 : ny);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        for (var channel = 0; channel < 4; channel++)
        {
            var sum = 0;
            for (var dy = 0; dy < ny; dy++)
            for (var dx = 0; dx < nx; dx++)
                sum += bytes[(2 * y + dy) * source.RowBytes + (2 * x + dx) * 4 + channel]
                    * (nx == 3 && dx == 1 ? 2 : 1) * (ny == 3 && dy == 1 ? 2 : 1);
            pixels[(y * width + x) * 4 + channel] = (byte)(sum / divisor);
        }
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        bitmap.SetImmutable();
        return SKImage.FromBitmap(bitmap);
    }

    public static void CopyClamped(SKCanvas canvas, SKImage image, SKRect slot)
    {
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Nearest), SKMatrix.CreateTranslation(slot.Left, slot.Top));
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        var padded = slot; padded.Inflate(2, 2);
        canvas.DrawRect(padded, paint);
    }

    public static SKShader Shader(SKImage sheet, IReadOnlyList<SKRect> upper,
        IReadOnlyList<SKRect> lower, IReadOnlyList<float> weights, SKMatrix? localMatrix = null)
    {
        var upperValues = new float[32]; var lowerValues = new float[32]; var values = new float[8];
        for (var i = 0; i < upper.Count; i++)
        {
            upperValues[i * 4] = upper[i].Left; upperValues[i * 4 + 1] = upper[i].Top;
            upperValues[i * 4 + 2] = upper[i].Width; upperValues[i * 4 + 3] = upper[i].Height;
            lowerValues[i * 4] = lower[i].Left; lowerValues[i * 4 + 1] = lower[i].Top;
            lowerValues[i * 4 + 2] = lower[i].Width; lowerValues[i * 4 + 3] = lower[i].Height;
            values[i] = weights[i];
        }
        var effect = Effect.Value;
        var uniforms = new SKRuntimeEffectUniforms(effect) { ["upper"] = upperValues, ["lower"] = lowerValues, ["weights"] = values };
        using var texture = sheet.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
        var children = new SKRuntimeEffectChildren(effect) { ["sheet"] = texture };
        return localMatrix is { } matrix ? effect.ToShader(uniforms, children, matrix) : effect.ToShader(uniforms, children);
    }

    public static bool DrawImage(SKCanvas canvas, SKImage image, SKRect destination, SKPaint paint)
    {
        var matrix = canvas.TotalMatrix;
        var scale = Math.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        var weight = Weight(image, destination.Width * scale, destination.Height * scale);
        if (weight <= 0 || image.Width > 2048 || image.Height > 2048) return false;
        using var half = Half(image);
        var upper = new SKRect(2, 2, image.Width + 2, image.Height + 2);
        var lower = new SKRect(image.Width + 6, 2, image.Width + 6 + half.Width, 2 + half.Height);
        using var surface = SKSurface.Create(new SKImageInfo((int)lower.Right + 2, image.Height + 4))!;
        surface.Canvas.Clear(SKColors.Transparent);
        CopyClamped(surface.Canvas, image, upper); CopyClamped(surface.Canvas, half, lower);
        using var sheet = surface.Snapshot();
        var local = SKMatrix.CreateScale(destination.Width / image.Width, destination.Height / image.Height);
        local.TransX = destination.Left - 2 * local.ScaleX; local.TransY = destination.Top - 2 * local.ScaleY;
        using var shader = Shader(sheet, [upper], [lower], [weight], local);
        paint.Shader = shader;
        canvas.DrawRect(destination, paint);
        paint.Shader = null;
        return true;
    }
}
