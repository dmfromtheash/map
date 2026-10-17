using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Joins the derived river cut to the sea before land outlines and shore waves are drawn.</summary>
internal static class RiverCoastRenderer
{
    internal const double MaximumMouthRadius = 80;
    public static void SmoothMouths(SKCanvas canvas, River river, double scale,
        Func<double, double, bool> insideLand, Action<SKCanvas, SKPaint> drawLand,
        Action<SKCanvas> drawWater)
    {
        foreach (var channel in RiverGeometry.Evaluate(river).Channels.Values)
        {
            if (channel.Closed || channel.Samples.Length < 2) continue;
            // Check both banks as well as the centre: a wide or oblique mouth reaches the sea
            // before its centre does, and a delta arm can touch the coast without crossing it.
            foreach (var side in new[] { -1, 0, 1 })
            {
                var previous = Position(channel.Samples[0], side);
                var wasLand = insideLand(previous.X, previous.Y);
                for (var i = 1; i < channel.Samples.Length; i++)
                {
                    var sample = channel.Samples[i];
                    var point = Position(sample, side);
                    var isLand = insideLand(point.X, point.Y);
                    if (wasLand != isLand)
                    {
                        var a = previous; var b = point;
                        for (var step = 0; step < 8; step++)
                        {
                            var middle = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                            if (insideLand(middle.X, middle.Y) == wasLand) a = middle; else b = middle;
                        }
                        var centre = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                        var half = Math.Max(sample.HalfWidth, RiverRenderer.MinimumScreenWidth / scale / 2);
                        Round(centre, (float)Math.Clamp(half * .4, 1.25, 8), (float)Math.Clamp(half * 2.5, 10, MaximumMouthRadius));
                    }
                    previous = point; wasLand = isLand;
                }
            }
        }

        void Round(SKPoint centre, float sigma, float radius)
        {
            var bounds = new SKRect(centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius);
            if (!bounds.IntersectsWith(canvas.LocalClipBounds)) return;
            var filterBounds = SKRect.Inflate(bounds, sigma * 3, sigma * 3);
            canvas.SaveLayer(filterBounds, null);
            using var blur = SKImageFilter.CreateBlur(sigma, sigma);
            // Blur the UNION of the river and open sea, then recover a firm antialiased edge.
            // A blurred eraser alone leaves translucent land and ghost coastlines in the mouth.
            using var threshold = SKColorFilter.CreateColorMatrix(
            [
                0, 0, 0, 0, 1,
                0, 0, 0, 0, 1,
                0, 0, 0, 0, 1,
                0, 0, 0, 16, -7.5f,
            ]);
            using var rounded = SKImageFilter.CreateColorFilter(threshold, blur);
            using (var filter = new SKPaint { ImageFilter = rounded })
            {
                canvas.SaveLayer(filterBounds, filter);
                using (var sea = new SKPaint { Color = SKColors.White }) canvas.DrawRect(filterBounds, sea);
                // Sea starts beyond the support of the land, not wherever its soft coverage
                // drops below 50%. Thresholding the inverse opacity punched pale holes inland.
                DrawLandSupport(canvas, drawLand, filterBounds, SKBlendMode.DstOut);
                drawWater(canvas);
                canvas.Restore();
            }
            // Confine the rounding to the crossing, with a gradual return to the original
            // channel upstream. Adding this cut never closes a narrow creek or moves its nodes.
            using var fade = SKShader.CreateRadialGradient(centre, radius,
                [SKColors.White, SKColors.White, SKColors.Transparent], [0f, .65f, 1f], SKShaderTileMode.Clamp);
            using (var keep = new SKPaint { Shader = fade, BlendMode = SKBlendMode.DstIn })
                canvas.DrawRect(filterBounds, keep);
            canvas.Restore();
        }
    }

    /// <summary>Banks follow the land's exact coverage, with no inland inset or extra water fade.</summary>
    public static void MaskBanks(SKCanvas canvas, Action<SKCanvas, SKPaint> drawLand, SKRect bounds)
    {
        // An inset proportional to river width stopped the cliff inside feathered ground.
        // Use land opacity once, so the cliff disappears together with that ground.
        using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
        canvas.SaveLayer(bounds, keep);
        using var plain = new SKPaint();
        drawLand(canvas, plain);
        canvas.Restore();
    }

    /// <summary>
    /// Shape only: every covered pixel belongs to land, including a softly painted edge or a
    /// translucent layer. Mouth rounding needs the boundary of that shape rather than its
    /// half-opacity contour; the original soft coverage is preserved when the cut is applied.
    /// </summary>
    private static void DrawLandSupport(SKCanvas canvas, Action<SKCanvas, SKPaint> drawLand,
        SKRect bounds, SKBlendMode blend = SKBlendMode.SrcOver)
    {
        using var support = SKColorFilter.CreateColorMatrix(
        [
            0, 0, 0, 0, 1,
            0, 0, 0, 0, 1,
            0, 0, 0, 0, 1,
            0, 0, 0, 255, 0,
        ]);
        // Apply after the union, masks and layer opacity have been composed by the caller.
        using var paint = new SKPaint { ColorFilter = support, BlendMode = blend };
        canvas.SaveLayer(bounds, paint);
        using (var plain = new SKPaint()) drawLand(canvas, plain);
        canvas.Restore();
    }

    private static SKPoint Position(RiverSample sample, int side) => new(
        (float)(sample.X - sample.TangentY * sample.HalfWidth * side),
        (float)(sample.Y + sample.TangentX * sample.HalfWidth * side));
}
