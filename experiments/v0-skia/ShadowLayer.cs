using SkiaSharp;

namespace V0Skia;

/// <summary>
/// Cast shadows of the painted mountains, the way a 2D illustrator (and Happy Harvest) does it:
/// each silhouette is laid onto the ground from its base along the light and softened, plus a
/// contact shadow under the base. Painted mountains are drawn in an oblique view, so a point's
/// height above the ground is its distance up from the base line, not a heightfield over its own
/// screen position. Light-dependent, but only a few masked draws: no material tile is touched.
/// </summary>
internal static class ShadowLayer
{
    public static SKImage Draw(MaterialCache cache, SceneInputs scene, LightState s, SKRectI frame)
    {
        using var surface = cache.NewSurface(frame.Width, frame.Height);
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Translate(-frame.Left, -frame.Top);

        float lx = s.Sun[0], ly = s.Sun[1];
        var len2 = MathF.Sqrt(lx * lx + ly * ly);
        float dx = len2 > 1e-3f ? -lx / len2 : 0, dy = len2 > 1e-3f ? ly / len2 : 1; // away from the sun, screen y down
        var k = s.ShadowLength;
        foreach (var m in scene.Mountains)
        {
            float w = m.Albedo.Width * m.Scale, h = m.Albedo.Height * m.Scale;
            var b = m.BaseScreenY - 0.12f * h; // the footprint's middle, a little above the lowest snow
            // The sun stands behind (north of) the mountains in every key, so the silhouette is laid
            // down IN FRONT of the base (mirrored, toward the viewer) and skewed away from the sun;
            // the ground plane foreshortens it.
            var squash = -MathF.Max(0.22f, 0.55f * dy * k + 0.12f);
            // world -> shadow: X' = X + dx k (B - Y);  Y' = B + (Y - B) * squash
            var toShadow = new SKMatrix(1, -dx * k, dx * k * b, 0, squash, b * (1 - squash), 0, 0, 1);
            var sprite = SKMatrix.CreateScaleTranslation(m.Scale, m.Scale, m.X, m.Y);
            var blurSigma = 2.5f + 0.010f * k * h; // penumbra grows with the shadow's length
            // Darkest at the base, fading toward the far end (light wraps in from the sky).
            var spriteH = m.Albedo.Height;
            var baseLocal = (b - m.Y) / m.Scale;
            using var fade = SKShader.CreateLinearGradient(new SKPoint(0, baseLocal), new SKPoint(0, 0),
                [SKColors.Black, SKColors.Black.WithAlpha(40)], SKShaderTileMode.Clamp);
            using var maskFilter = SKColorFilter.CreateBlendMode(SKColors.Black, SKBlendMode.SrcIn);
            using var blur = SKImageFilter.CreateBlur(blurSigma, blurSigma);
            using var layerPaint = new SKPaint { ImageFilter = blur };
            c.Save();
            c.Concat(SKMatrix.Concat(toShadow, sprite));
            c.SaveLayer(SKRect.Create(-40, -40, m.Albedo.Width + 80, spriteH + 80), layerPaint);
            using (var p = new SKPaint { ColorFilter = maskFilter })
                c.DrawImage(m.Albedo, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
            using (var g = new SKPaint { Shader = fade, BlendMode = SKBlendMode.DstIn })
                c.DrawRect(SKRect.Create(-40, -40, m.Albedo.Width + 80, spriteH + 80), g);
            c.Restore();
            c.Restore();

            using var contactBlur = SKImageFilter.CreateBlur(14, 6);
            using var contact = new SKPaint { Color = SKColors.Black.WithAlpha(150), ImageFilter = contactBlur, IsAntialias = true };
            c.DrawOval(new SKRect(m.X + w * 0.12f, b + 0.02f * h, m.X + w * 0.88f, b + 0.13f * h), contact);
        }
        return surface.Snapshot();
    }
}
