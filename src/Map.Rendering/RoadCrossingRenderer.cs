using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Draws what a road does over a river (R06): a bridge deck with planks and railings, a light ford
/// band with stones, or a ferry rope. Procedural, in the road's own tones; the owner's art may
/// replace the look later without changing the crossing model.
/// </summary>
public static class RoadCrossingRenderer
{
    private static readonly SKColor Deck = SKColor.Parse("#FF8C6A42");
    private static readonly SKColor Plank = SKColor.Parse("#FF6E5132");
    private static readonly SKColor Rail = SKColor.Parse("#FF4A3521");
    private static readonly SKColor Stone = SKColor.Parse("#FFE2DBC8");

    public static void Draw(SKCanvas canvas, IReadOnlyList<RoadCrossing> crossings, double scale, bool fastPreview)
    {
        foreach (var crossing in crossings)
        {
            if (crossing.Kind == RoadCrossingKind.None || crossing.RoadHalfWidth * scale < 0.8) continue;
            var angle = Math.Atan2(crossing.RoadTangentY, crossing.RoadTangentX) * 180 / Math.PI;
            canvas.Save();
            canvas.Translate((float)crossing.X, (float)crossing.Y);
            canvas.RotateDegrees((float)angle);
            switch (crossing.Kind)
            {
                case RoadCrossingKind.Bridge: DrawBridge(canvas, crossing, scale, fastPreview); break;
                case RoadCrossingKind.Ford: DrawFord(canvas, crossing, scale); break;
                case RoadCrossingKind.Ferry: DrawFerry(canvas, crossing, scale); break;
            }
            canvas.Restore();
        }
    }

    private static void DrawBridge(SKCanvas canvas, RoadCrossing crossing, double scale, bool fastPreview)
    {
        var halfWidth = (float)(crossing.RoadHalfWidth * 1.15);
        var overhang = (float)(crossing.RoadHalfWidth * 1.2);
        var halfLength = (float)(crossing.SpanAlongRoad / 2) + overhang;
        var deck = new SKRect(-halfLength, -halfWidth, halfLength, halfWidth);
        var pixel = (float)(1 / Math.Max(scale, 1e-4));

        // Shadow on the water, cast a little downstream of the deck.
        using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(60), IsAntialias = true })
        {
            if (!fastPreview && halfWidth * scale > 2) shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, halfWidth * 0.35f);
            canvas.DrawRect(new SKRect(deck.Left, deck.Top + halfWidth * 0.5f, deck.Right, deck.Bottom + halfWidth * 0.5f), shadow);
        }
        using (var fill = new SKPaint { Color = Deck, IsAntialias = true })
            canvas.DrawRect(deck, fill);
        // Planks across the deck, when there is room to tell them apart.
        var step = Math.Max(2 * pixel, halfWidth * 0.55f);
        if (step * scale >= 2)
        {
            using var plank = new SKPaint { Color = Plank.WithAlpha(150), IsAntialias = true, StrokeWidth = Math.Max(pixel, halfWidth * 0.08f), Style = SKPaintStyle.Stroke };
            for (var x = -halfLength + step; x < halfLength; x += step)
                canvas.DrawLine(x, -halfWidth, x, halfWidth, plank);
        }
        // Railings along both edges and darker ends where the deck meets the banks.
        using var rail = new SKPaint { Color = Rail, IsAntialias = true, StrokeWidth = Math.Max(pixel, halfWidth * 0.14f), Style = SKPaintStyle.Stroke };
        canvas.DrawLine(-halfLength, -halfWidth, halfLength, -halfWidth, rail);
        canvas.DrawLine(-halfLength, halfWidth, halfLength, halfWidth, rail);
        rail.StrokeWidth = Math.Max(pixel, halfWidth * 0.1f);
        canvas.DrawLine(-halfLength, -halfWidth, -halfLength, halfWidth, rail);
        canvas.DrawLine(halfLength, -halfWidth, halfLength, halfWidth, rail);
    }

    private static void DrawFord(SKCanvas canvas, RoadCrossing crossing, double scale)
    {
        var halfWidth = (float)crossing.RoadHalfWidth;
        var halfLength = (float)(crossing.SpanAlongRoad / 2);
        using (var band = new SKPaint { Color = SKColors.White.WithAlpha(78), IsAntialias = true })
            canvas.DrawRect(new SKRect(-halfLength, -halfWidth, halfLength, halfWidth), band);
        // Stepping stones along the middle, placed the same way on every frame.
        var radius = Math.Max((float)(1 / Math.Max(scale, 1e-4)), halfWidth * 0.22f);
        var count = Math.Max(2, (int)(halfLength * 2 / (radius * 3)));
        var seed = unchecked((int)(crossing.X * 73856093) ^ (int)(crossing.Y * 19349663));
        using var stone = new SKPaint { Color = Stone.WithAlpha(210), IsAntialias = true };
        for (var i = 0; i < count; i++)
        {
            var x = -halfLength + (i + 0.5f) * (2 * halfLength / count);
            var wobble = ((seed + i * 7919) % 100) / 100f - 0.5f;
            canvas.DrawCircle(x, wobble * halfWidth * 0.8f, radius, stone);
        }
    }

    private static void DrawFerry(SKCanvas canvas, RoadCrossing crossing, double scale)
    {
        var halfWidth = (float)crossing.RoadHalfWidth;
        var halfLength = (float)(crossing.SpanAlongRoad / 2) + halfWidth * 0.6f;
        var pixel = (float)(1 / Math.Max(scale, 1e-4));
        using var rope = new SKPaint { Color = Rail, IsAntialias = true, StrokeWidth = Math.Max(pixel, halfWidth * 0.12f), Style = SKPaintStyle.Stroke, PathEffect = SKPathEffect.CreateDash([halfWidth * 0.6f, halfWidth * 0.35f], 0) };
        canvas.DrawLine(-halfLength, 0, halfLength, 0, rope);
        using var post = new SKPaint { Color = Rail, IsAntialias = true };
        canvas.DrawCircle(-halfLength, 0, Math.Max(pixel, halfWidth * 0.25f), post);
        canvas.DrawCircle(halfLength, 0, Math.Max(pixel, halfWidth * 0.25f), post);
        // The ferry itself: a small raft midway.
        using var raft = new SKPaint { Color = Deck, IsAntialias = true };
        canvas.DrawRect(new SKRect(-halfWidth * 0.9f, -halfWidth * 0.55f, halfWidth * 0.9f, halfWidth * 0.55f), raft);
    }
}
