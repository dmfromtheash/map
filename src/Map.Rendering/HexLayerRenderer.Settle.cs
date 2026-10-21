using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>How a hex the tool just changed eases into its new state.</summary>
public enum HexSettleKind
{
    /// <summary>A tile lands: it comes down a little larger, settles, then gives way to the finished hex.</summary>
    Land,

    /// <summary>A tile is taken away: it shrinks and fades off the hex.</summary>
    Lift,

    /// <summary>Nothing new to draw (a region, the fog, the pipette): a soft ring answers the touch.</summary>
    Touch,
}

/// <summary>
/// One hex easing into what the tool made of it. <paramref name="T"/> below 0 means it waits for
/// its turn in a wave and still shows <paramref name="From"/>; 0..1 is the way through.
/// </summary>
public sealed record HexSettle(HexCoord Hex, HexCell? From, HexCell? To, HexSettleKind Kind, double T);

/// <summary>The hexes of one layer easing in this frame.</summary>
public sealed record HexSettles(LayerId LayerId, IReadOnlyList<HexSettle> Items);

public static partial class HexLayerRenderer
{
    /// <summary>The share of a landing spent coming down; after it the finished hex shows through.</summary>
    private const double LandShare = 0.6;

    /// <summary>
    /// Draws the hexes easing in over the kept pictures (owner, 2026-10-21: placing should feel
    /// tactile). The kept pictures already show the finished hex; a landing tile covers it while it
    /// comes down and then fades, so its transitions and living icons appear after it has landed.
    /// </summary>
    private static void DrawSettles(SKCanvas canvas, HexLayer layer, IReadOnlyList<HexSettle> settles, double viewScale)
    {
        var layout = layer.Board.Layout;
        var pixel = (float)(1 / Math.Max(viewScale, 1e-6));
        using var paint = new SKPaint { IsAntialias = true };
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
        using var fade = new SKPaint { Color = SKColors.White };
        // Waiting hexes first (they show what was there), then the moving ones over them.
        foreach (var settle in settles)
        {
            if (settle.T >= 0 || settle.From is not { } from) continue;
            DrawCell(canvas, layout, settle.Hex, from, pixel);
        }

        foreach (var settle in settles)
        {
            if (settle.T < 0) continue;
            var t = Math.Clamp(settle.T, 0, 1);
            var (cx, cy) = layout.Center(settle.Hex);
            using var outline = HexOutline(layout, settle.Hex, 0);
            switch (settle.Kind)
            {
                case HexSettleKind.Land when settle.To is { } to:
                {
                    var down = OutCubic(Math.Min(1, t / LandShare));
                    var cover = t < LandShare ? 1 : 1 - HexChunkCache.Ease((t - LandShare) / (1 - LandShare));
                    var lift = layout.Size * 0.12 * (1 - down);
                    var scale = 1 + 0.09 * (1 - down);
                    // Its shadow on the ground, darkest just before it lands. No blur and no layer:
                    // a brush stroke has dozens of these on screen at once.
                    paint.Color = new SKColor(0, 0, 0, (byte)Math.Round(56 * (1 - down) * cover));
                    canvas.DrawPath(outline, paint);
                    fade.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * cover));
                    canvas.Save();
                    canvas.Translate((float)cx, (float)(cy - lift));
                    canvas.Scale((float)scale);
                    canvas.Translate((float)-cx, (float)-cy);
                    DrawCell(canvas, layout, settle.Hex, to, pixel, fade);
                    canvas.Restore();
                    // A short warm rim as it touches down.
                    rim.StrokeWidth = 1.25f * pixel;
                    rim.Color = new SKColor(0xF4, 0xD0, 0x70, (byte)Math.Round(150 * Math.Sin(Math.PI * Math.Min(1, t / (LandShare + 0.15)))));
                    canvas.DrawPath(outline, rim);
                    break;
                }
                case HexSettleKind.Lift when settle.From is { } gone:
                {
                    var e = HexChunkCache.Ease(t);
                    fade.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * (1 - e)));
                    canvas.Save();
                    var scale = 1 - 0.14 * e;
                    canvas.Translate((float)cx, (float)(cy + layout.Size * 0.05 * e));
                    canvas.Scale((float)scale);
                    canvas.Translate((float)-cx, (float)-cy);
                    DrawCell(canvas, layout, settle.Hex, gone, pixel, fade);
                    canvas.Restore();
                    break;
                }
                default:
                {
                    var e = OutCubic(t);
                    rim.StrokeWidth = 1.25f * pixel;
                    rim.Color = new SKColor(0xF4, 0xD0, 0x70, (byte)Math.Round(140 * (1 - t)));
                    canvas.Save();
                    var scale = (float)(1 + 0.22 * e);
                    canvas.Scale(scale, scale, (float)cx, (float)cy);
                    canvas.DrawPath(outline, rim);
                    canvas.Restore();
                    break;
                }
            }
        }
    }

    private static double OutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>A cell's own pictures (its terrain and what stands on it), without its neighbours' transitions.</summary>
    private static void DrawCell(SKCanvas canvas, HexLayout layout, HexCoord hex, HexCell cell, float pixel, SKPaint? paint = null)
    {
        DrawTile(canvas, layout, hex, cell.Terrain, cell.Variant, 0.5f * pixel, cell.Rotation, paint);
        if (cell.Feature is { } feature) DrawTile(canvas, layout, hex, feature, cell.FeatureVariant, 0, 0, paint);
    }

    /// <summary>The chosen tile, faint, on the hexes under the pointer: what a click would put down.</summary>
    private static void DrawGhostTile(SKCanvas canvas, HexBoard board, HexCursorGlow glow, HexGhostTile ghost)
    {
        var strength = Math.Clamp(glow.Strength, 0, 1);
        if (strength <= 0.02 || glow.Hexes.Count > 37) return;
        var layout = board.Layout;
        using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * 0.5 * strength)) };
        foreach (var hex in glow.Hexes)
        {
            var bounds = layout.Bounds(hex);
            var room = ghost.Overhang * (bounds.Bottom - bounds.Top);
            canvas.SaveLayer(new SKRect((float)bounds.Left, (float)(bounds.Top - room), (float)bounds.Right, (float)bounds.Bottom), fade);
            using (var clip = HexOutline(layout, hex, 0))
            {
                // A tall tile shows whole; a flat one stays inside its hex.
                if (ghost.Overhang <= 0) canvas.ClipPath(clip, antialias: true);
            }

            DrawTile(canvas, layout, hex, ghost.TileId, ghost.Variant, 0, ghost.Rotation);
            canvas.Restore();
        }
    }
}

/// <summary>The tile a click would put down, shown faint under the pointer.</summary>
public sealed record HexGhostTile(string TileId, int Variant, int Rotation, double Overhang);
