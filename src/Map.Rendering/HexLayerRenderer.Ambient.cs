using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

public static partial class HexLayerRenderer
{
    /// <summary>Below this many screen pixels per hex the world is still (it would only flicker).</summary>
    public const double AmbientFromPixels = 24;

    /// <summary>More visible hexes than this and the world is still: the frame stays cheap.</summary>
    private const int MaxAmbientHexes = 6000;

    private static readonly HashSet<string> Water = ["starter/sea", "starter/lake", "starter/shallows"];

    /// <summary>
    /// The world alive while it is looked at (owner, 2026-10-21), drawn over the kept pictures and
    /// never into them: light sparkles on water, a breath of wind running over the forests as a soft
    /// band of light, and slow cloud shadows drifting across. Nothing moves the pictures themselves.
    /// </summary>
    private static void DrawAmbient(SKCanvas canvas, HexLayer layer, WorldBounds view, double viewScale, double seconds)
    {
        var board = layer.Board;
        var layout = board.Layout;
        var r = layout.Size;
        var pixel = (float)(1 / Math.Max(viewScale, 1e-6));
        using var forests = new SKPath();
        var count = 0;
        using var sparkle = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeWidth = 1.3f * pixel };
        foreach (var hex in board.HexesIn(view))
        {
            if (++count > MaxAmbientHexes) return;
            if (board[hex] is not { } cell) continue;
            var (cx, cy) = layout.Center(hex);
            if (Water.Contains(cell.Terrain))
            {
                // Two glints per hex, each fading in and out on its own slow beat.
                var noise = HexEditing.Noise(hex, 913);
                for (var i = 0; i < 2; i++)
                {
                    var seed = noise >> (i * 11);
                    var phase = (seed & 0x3FF) / 1023.0 * Math.Tau;
                    var speed = 0.7 + ((seed >> 10) & 0xF) / 15.0 * 0.6;
                    var glint = Math.Pow(Math.Max(0, Math.Sin(seconds * speed + phase)), 6);
                    if (glint < 0.02) continue;
                    var x = cx + (((seed >> 3) & 0xFF) / 255.0 - 0.5) * r * 1.1;
                    var y = cy + (((seed >> 14) & 0xFF) / 255.0 - 0.5) * r * 0.9;
                    var length = r * (0.12 + 0.1 * glint);
                    sparkle.Color = new SKColor(0xFF, 0xFF, 0xF4, (byte)Math.Round(150 * glint));
                    canvas.DrawLine((float)(x - length), (float)y, (float)(x + length), (float)y, sparkle);
                }
            }
            else if (HexStarterTiles.Find(cell.Terrain) is { } tile
                && (tile.Dynamic is HexDynamicIcon.Trees or HexDynamicIcon.Pines || cell.Terrain is "starter/forest" or "starter/pines" or "starter/old-forest" or "starter/jungle"))
            {
                using var outline = HexOutline(layout, hex, r * 0.02);
                forests.AddPath(outline);
            }
        }

        if (!forests.IsEmpty)
        {
            // Wind: a soft band of light (and a fainter shade after it) sweeping over the canopy.
            var span = (float)(r * 9);
            var travel = (float)(seconds * r * 1.6 % (span * 2));
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(span, span * 0.35f),
                [SKColors.Transparent, new SKColor(0xFF, 0xF6, 0xD8, 30), SKColors.Transparent, new SKColor(0x10, 0x18, 0x08, 22), SKColors.Transparent],
                [0f, 0.18f, 0.36f, 0.5f, 0.7f], SKShaderTileMode.Repeat,
                SKMatrix.CreateTranslation(travel + (float)view.Left, (float)view.Top));
            using var wind = new SKPaint { IsAntialias = true, Shader = shader };
            canvas.DrawPath(forests, wind);
        }

        // Clouds: a few large soft shadows drifting slowly across the view.
        var width = view.Right - view.Left;
        var height = view.Bottom - view.Top;
        var cloud = Math.Max(width, height) * 0.22;
        using var shade = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 4; i++)
        {
            var drift = (seconds * r * 0.35 + i * width * 0.37) % (width + cloud * 2) - cloud;
            var x = view.Left + drift;
            var y = view.Top + height * (0.18 + 0.22 * i) + Math.Sin(seconds * 0.05 + i) * height * 0.04;
            var radius = (float)(cloud * (0.8 + 0.15 * i));
            using var gradient = SKShader.CreateRadialGradient(new SKPoint((float)x, (float)y), radius,
                [new SKColor(0x18, 0x14, 0x20, 24), new SKColor(0x18, 0x14, 0x20, 12), SKColors.Transparent], [0f, 0.55f, 1f], SKShaderTileMode.Clamp);
            shade.Shader = gradient;
            canvas.Save();
            canvas.Scale(1.6f, 1, (float)x, (float)y);
            canvas.DrawCircle((float)x, (float)y, radius, shade);
            canvas.Restore();
        }
    }
}
