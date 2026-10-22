using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// R10 «Красота» in the picture: the cold contact shadow under a placed asset, its colour tint and
/// the blurred underlay of a whole area. Light comes from the north-west, so shadows fall to the
/// south-east in a cold blue-grey, as on the owner's mountains. Nothing here changes the document.
/// </summary>
internal static class AreaBeautyRenderer
{
    /// <summary>The cold shadow colour of contact shadows and underlays.</summary>
    internal static readonly SKColor ShadowInk = new(22, 30, 46);
    private const int SpriteWidth = 64, SpriteHeight = 32;
    private static readonly Lazy<SKImage> Sprite = new(BuildSprite, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A soft ellipse in the shadow colour, alpha falling off smoothly to its rim.</summary>
    internal static SKImage ShadowSprite => Sprite.Value;

    /// <summary>Sprite key of the shadow in the dense atlas: never a content hash.</summary>
    internal const string ShadowSpriteKey = "r10-ground-shadow";

    private static SKImage BuildSprite()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(SpriteWidth, SpriteHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < SpriteHeight; y++)
        for (var x = 0; x < SpriteWidth; x++)
        {
            var dx = (x + .5) / SpriteWidth * 2 - 1; var dy = (y + .5) / SpriteHeight * 2 - 1;
            var r = Math.Sqrt(dx * dx + dy * dy);
            var a = r >= 1 ? 0 : Math.Pow(1 - r * r, 1.6);
            var alpha = (byte)Math.Round(a * 255);
            bitmap.SetPixel(x, y, ShadowInk.WithAlpha(alpha));
        }
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>
    /// The shadow ellipse in the object's own (unrotated) rectangle: at the foot, shifted to the
    /// south-east, and kept inside the rectangle so tiles and culling need no wider bounds.
    /// </summary>
    internal static SKRect ShadowRect(SKRect rect, StampGroundShadow shadow)
    {
        var width = (float)(shadow.Width * rect.Width);
        var height = Math.Min(width * .34f, rect.Height * .5f);
        width = Math.Min(width, rect.Width);
        var cx = rect.Left + (float)shadow.FootX * rect.Width + width * .12f;
        var cy = rect.Top + (float)shadow.FootY * rect.Height + height * .08f;
        cx = Math.Clamp(cx, rect.Left + width / 2, rect.Right - width / 2);
        cy = Math.Clamp(cy, rect.Top + height / 2, rect.Bottom - height / 2);
        return new SKRect(cx - width / 2, cy - height / 2, cx + width / 2, cy + height / 2);
    }

    internal static void DrawGroundShadow(SKCanvas canvas, SKRect rect, StampGroundShadow shadow, byte objectAlpha)
    {
        if (!shadow.IsValid || shadow.Opacity <= 0) return;
        var alpha = (byte)Math.Clamp(Math.Round(shadow.Opacity * objectAlpha), 0, 255);
        if (alpha == 0) return;
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha), IsAntialias = true };
        canvas.DrawImage(ShadowSprite, ShadowRect(rect, shadow), new SKSamplingOptions(SKFilterMode.Linear), paint);
    }

    /// <summary>The multiplying tint of a stamp, or white when it has none.</summary>
    internal static SKColor Tint(StampStyle stamp) =>
        stamp.Tint is { } tint && SKColor.TryParse(tint, out var color) ? color.WithAlpha(255) : SKColors.White;

    /// <summary>The colour adjustments and the tint of a stamp as one filter, or null when neutral.</summary>
    internal static SKColorFilter? Compose(SKColorFilter? adjustments, StampStyle stamp)
    {
        if (stamp.Tint is null) return adjustments;
        var tint = Tint(stamp);
        if (tint == SKColors.White) return adjustments;
        var modulate = SKColorFilter.CreateBlendMode(tint, SKBlendMode.Modulate);
        if (adjustments is null) return modulate;
        var composed = SKColorFilter.CreateCompose(modulate, adjustments);
        modulate.Dispose(); adjustments.Dispose();
        return composed;
    }

    internal static bool HasMass(SceneObject item) =>
        item.Visible && item.Stamp?.GroundShadow is { IsValid: true, Mass: > 0 };

    /// <summary>
    /// The «подложка» blot in the object's own rectangle: as wide as the object, half as high as
    /// wide (the sprite's shape, so one uniform scale draws it), standing on the foot and shifted to
    /// the south-east, kept inside the rectangle.
    /// </summary>
    internal static SKRect MassRect(SKRect rect, StampGroundShadow shadow)
    {
        var width = Math.Min(rect.Width, Math.Max((float)(shadow.Width * rect.Width) * 1.2f, rect.Width * .6f));
        var height = width * SpriteHeight / SpriteWidth;
        if (height > rect.Height) { height = rect.Height; width = height * SpriteWidth / SpriteHeight; }
        var cx = rect.Left + (float)shadow.FootX * rect.Width + width * .08f;
        var cy = rect.Top + (float)shadow.FootY * rect.Height - height * .3f;
        cx = Math.Clamp(cx, rect.Left + width / 2, rect.Right - width / 2);
        cy = Math.Clamp(cy, rect.Top + height / 2, rect.Bottom - height / 2);
        return new SKRect(cx - width / 2, cy - height / 2, cx + width / 2, cy + height / 2);
    }

    /// <summary>
    /// «Подложка массива»: before any object of the run, one soft dark blot under each object that
    /// carries it, in one atlas submission. Where the group is dense the blots overlap into a dark
    /// mass; a clearing between groups stays light. Nothing is drawn when no object has a blot.
    /// </summary>
    internal static void DrawMasses(SKCanvas canvas, IReadOnlyList<SceneObject> ordered, RenderOptions options)
    {
        var count = 0;
        foreach (var item in ordered) if (HasMass(item)) count++;
        if (count == 0) return;
        var sprite = ShadowSprite;
        var transforms = new SKRotationScaleMatrix[count]; var sources = new SKRect[count]; var colors = new SKColor[count];
        var i = 0;
        foreach (var item in ordered)
        {
            if (!HasMass(item)) continue;
            var shadow = item.Stamp!.GroundShadow!;
            var local = MassRect(new SKRect(0, 0, (float)item.Width, (float)item.Height), shadow);
            var scale = local.Width / SpriteWidth;
            var angle = item.RotationDegrees * Math.PI / 180;
            var cos = Math.Cos(angle); var sin = Math.Sin(angle);
            // The object's rectangle turns about its centre: map the blot's local corner the same way.
            var ox = local.Left - item.Width / 2; var oy = local.Top - item.Height / 2;
            var x = item.X + item.Width / 2 + cos * ox - sin * oy;
            var y = item.Y + item.Height / 2 + sin * ox + cos * oy;
            transforms[i] = new((float)(scale * cos), (float)(scale * sin), (float)x, (float)y);
            sources[i] = new SKRect(0, 0, SpriteWidth, SpriteHeight);
            colors[i] = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(shadow.Mass * item.ObjectOpacity * 255), 0, 255));
            i++;
        }
        using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.SrcOver };
        canvas.DrawAtlas(sprite, sources, transforms, colors, SKBlendMode.Modulate,
            new SKSamplingOptions(SKFilterMode.Linear), paint);
        if (options.Stats is { } stats) stats.DrawCalls++;
    }
}
