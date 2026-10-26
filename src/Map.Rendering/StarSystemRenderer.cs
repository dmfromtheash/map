using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// R33: the systems of a star sector, as on a classic subsector chart - a star dot in the colour of
/// its spectral class (with a soft glow), the starport class and UWP above, the name below, a
/// dashed ring for an amber zone and a solid red ring for a red one, small base letters to the
/// left. Everything scales with the hex.
/// </summary>
internal static class StarSystemRenderer
{
    public static void Draw(SKCanvas canvas, MapDocument document, RenderOptions options)
    {
        foreach (var layer in document.Layers.OfType<HexLayer>())
        {
            if (!layer.Visible || layer.Systems.IsEmpty || (options.HexLevel is { } level && layer.Level != level)) continue;
            var size = (float)layer.Board.Layout.Size;
            using var regular = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
            using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
            using var nameFont = new SKFont(bold, size * .26f);
            using var uwpFont = new SKFont(regular, size * .2f);
            using var text = new SKPaint { Color = new SKColor(0xE8, 0xEC, 0xF4), IsAntialias = true };
            using var faint = new SKPaint { Color = new SKColor(0x9A, 0xA6, 0xBC), IsAntialias = true };
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = size * .05f };
            foreach (var (hex, system) in layer.Systems)
            {
                if (options.HexPlayerView && layer.FogEnabled && layer.Board[hex]?.Revealed != true) continue;
                var (x, y) = layer.Board.Layout.Center(hex);
                var (cx, cy) = ((float)x, (float)y);
                var colour = SKColor.Parse(system.StarColor);
                using (var glow = new SKPaint { Color = colour.WithAlpha(90), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * .1f) })
                    canvas.DrawCircle(cx, cy, size * .16f, glow);
                using (var star = new SKPaint { Color = colour, IsAntialias = true })
                    canvas.DrawCircle(cx, cy, size * .1f, star);
                if (system.Zone != TravelZone.Green)
                {
                    ring.Color = system.Zone == TravelZone.Red ? new SKColor(0xD2, 0x3A, 0x30) : new SKColor(0xE0, 0xA5, 0x2B);
                    ring.PathEffect = system.Zone == TravelZone.Amber ? SKPathEffect.CreateDash([size * .12f, size * .08f], 0) : null;
                    canvas.DrawCircle(cx, cy, size * .42f, ring);
                }
                canvas.DrawText(system.Uwp, cx, cy - (size * .62f), SKTextAlign.Center, uwpFont, faint);
                if (system.Name.Length > 0) canvas.DrawText(system.Name, cx, cy + (size * .78f), SKTextAlign.Center, nameFont, text);
                if (system.Bases.Length > 0) canvas.DrawText(system.Bases, cx - (size * .72f), cy + (size * .07f), SKTextAlign.Center, uwpFont, faint);
            }
        }
    }
}
