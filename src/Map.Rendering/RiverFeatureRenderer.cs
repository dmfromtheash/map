using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Waterfalls, rapids and fords on river nodes (R12), drawn the way the owner's ink-and-wash maps
/// mark them: the bank ink for the hard lines and stones, the light flow colour for white water.
/// <list type="bullet">
/// <item>Водопад — an inked lip across the channel, pale falling strokes below it and a band of
/// foam where they land;</item>
/// <item>Пороги — a short stretch of broken water: staggered pale chevrons pointing downstream and
/// small inked rocks with a light wake behind each;</item>
/// <item>Брод — a pale shallow band across the water with two staggered rows of inked stepping-stones.</item>
/// </list>
/// Everything is sized by the local channel width and placed from the node id, so a feature looks
/// the same in the viewport, a tile and an export and never crawls while panning.
/// </summary>
internal static class RiverFeatureRenderer
{
    /// <summary>Below this many device pixels of channel width a feature is not drawn.</summary>
    private const double MinimumScreenWidth = 5;

    public static bool Any(RiverGeometrySet set) =>
        set.Channels.Values.Any(g => !g.Closed && g.Channel.Nodes.Any(n => n.Feature != RiverNodeFeature.None));

    public static void Draw(SKCanvas canvas, RiverGeometrySet set, RiverStyle style, double scale)
    {
        var ink = SceneRenderer.ParseColor(style.BankColor);
        var foam = SceneRenderer.ParseColor(style.HighlightColor).WithAlpha(255);
        foreach (var geometry in set.Channels.Values)
        {
            if (geometry.Closed) continue;
            var nodes = geometry.Channel.Nodes;
            for (var i = 0; i < nodes.Length && i < geometry.NodeSampleIndex.Length; i++)
            {
                var feature = nodes[i].Feature;
                if (feature == RiverNodeFeature.None) continue;
                var sample = geometry.Samples[geometry.NodeSampleIndex[i]];
                var half = sample.HalfWidth;
                if (half * 2 * scale < MinimumScreenWidth) continue;
                var (tx, ty) = Unit(sample.TangentX, sample.TangentY);
                var frame = new Frame(sample.X, sample.Y, tx, ty, half, Seed(nodes[i].Id));
                switch (feature)
                {
                    case RiverNodeFeature.Waterfall: Waterfall(canvas, frame, ink, foam, scale); break;
                    case RiverNodeFeature.Rapids: Rapids(canvas, frame, ink, foam, scale); break;
                    case RiverNodeFeature.Ford: Ford(canvas, frame, ink, foam, scale); break;
                }
            }
        }
    }

    /// <summary>A node's place: <c>along</c> runs downstream, <c>across</c> from the left bank (−) to the right (+).</summary>
    private readonly record struct Frame(double X, double Y, double Tx, double Ty, double Half, int Seed)
    {
        public SKPoint At(double along, double across) =>
            new((float)(X + Tx * along - Ty * across), (float)(Y + Ty * along + Tx * across));
    }

    private static void Waterfall(SKCanvas canvas, Frame f, SKColor ink, SKColor foam, double scale)
    {
        var h = f.Half;
        var pen = (float)Math.Max(0.8 / scale, h * 0.06);
        // Darker smooth water above the lip, as the stream gathers before the drop.
        using (var pool = Quad(f, (-h * 0.75, -h * 1.15), (h * 0.0, -h * 1.15), (h * 0.0, h * 1.15), (-h * 0.75, h * 1.15)))
        using (var paint = Gradient(f, -h * 0.75, 0, [ink.WithAlpha(0), ink.WithAlpha(52)]))
            canvas.DrawPath(pool, paint);
        // The falling sheet: pale at the lip, thinning out downstream, with a ragged lower edge.
        using (var sheet = new SKPath())
        {
            sheet.MoveTo(f.At(0, -h * 1.15));
            sheet.QuadTo(f.At(h * 0.2, 0), f.At(0, h * 1.15));
            const int teeth = 9;
            for (var k = teeth; k >= 0; k--)
            {
                var across = -h * 1.15 + h * 2.3 * k / teeth;
                sheet.LineTo(f.At(h * (0.85 + 0.25 * Jitter(f.Seed, k, 1)), across));
            }
            sheet.Close();
            using var paint = Gradient(f, 0, h * 1.0, [foam.WithAlpha(190), foam.WithAlpha(60)]);
            canvas.DrawPath(sheet, paint);
        }
        // Streaks down the sheet: thin tapered strokes, light and dark, of uneven length.
        var streaks = Math.Clamp((int)Math.Round(h * scale / 2.6), 5, 18);
        for (var k = 0; k < streaks; k++)
        {
            var across = -h + h * 2 * (k + 0.5) / streaks + h * 0.05 * Jitter(f.Seed, k, 2);
            var start = h * 0.2 * (1 - Math.Pow(across / h, 2)) + h * 0.04;
            var length = h * (0.45 + 0.45 * (0.5 + 0.5 * Jitter(f.Seed, k, 3)));
            var dark = k % 3 == 1;
            Taper(canvas, f, start, across, start + length, across + h * 0.03 * Jitter(f.Seed, k, 4),
                h * (dark ? 0.025 : 0.04), dark ? ink.WithAlpha(70) : foam.WithAlpha(235));
        }
        // Spray where it lands: a soft band and a few crisp droplets.
        using (var mist = new SKPaint
        {
            IsAntialias = true, Color = foam.WithAlpha(150),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)Math.Max(0.6 / scale, h * 0.16)),
        })
        using (var band = Quad(f, (h * 0.95, -h * 1.0), (h * 1.4, -h * 0.9), (h * 1.4, h * 0.9), (h * 0.95, h * 1.0)))
            canvas.DrawPath(band, mist);
        using (var drop = new SKPaint { IsAntialias = true, Color = foam.WithAlpha(225) })
            for (var k = 0; k < 14; k++)
                canvas.DrawCircle(f.At(h * (1.0 + 0.45 * (0.5 + 0.5 * Jitter(f.Seed, k, 5))), h * 0.95 * Jitter(f.Seed, k, 6)),
                    (float)(h * 0.035), drop);
        // The lip, inked last over the sheet, and the rock face at each bank under it.
        using (var lip = new SKPath())
        {
            lip.MoveTo(f.At(-h * 0.02, -h * 1.1));
            lip.QuadTo(f.At(h * 0.2, 0), f.At(-h * 0.02, h * 1.1));
            using var paint = Stroke(ink, pen * 1.25f);
            canvas.DrawPath(lip, paint);
        }
        using var face = Stroke(ink.WithAlpha(200), pen * 0.8f);
        foreach (var side in new[] { -1, 1 })
        {
            canvas.DrawLine(f.At(h * 0.02, side * h * 1.02), f.At(h * 0.6, side * h * 0.95), face);
            canvas.DrawLine(f.At(h * 0.2, side * h * 0.9), f.At(h * 0.5, side * h * 0.86), face);
        }
    }

    private static void Rapids(SKCanvas canvas, Frame f, SKColor ink, SKColor foam, double scale)
    {
        var h = f.Half;
        var reach = h * 2.4;
        // White water: a pale wash over the stretch, strongest in the middle of it.
        using (var wash = Quad(f, (-reach / 2, -h * 1.2), (reach / 2, -h * 1.2), (reach / 2, h * 1.2), (-reach / 2, h * 1.2)))
        using (var paint = Gradient(f, -reach / 2, reach / 2,
            [foam.WithAlpha(0), foam.WithAlpha(70), foam.WithAlpha(70), foam.WithAlpha(0)], [0f, 0.3f, 0.7f, 1f]))
            canvas.DrawPath(wash, paint);
        // Crests: short pale arcs bowed downstream, scattered over a loose grid.
        var rows = Math.Clamp((int)Math.Round(reach * scale / 8), 3, 7);
        using (var crest = Stroke(foam.WithAlpha(220), (float)Math.Max(0.6 / scale, h * 0.035)))
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < 4; c++)
                {
                    var i = r * 4 + c;
                    if (Jitter(f.Seed, i, 1) < -0.3) continue;
                    var along = -reach * 0.42 + reach * 0.84 * (r + 0.5) / rows + h * 0.12 * Jitter(f.Seed, i, 2);
                    var across = (-0.66 + 0.44 * c + (r % 2 == 0 ? 0 : 0.22)) * h + h * 0.08 * Jitter(f.Seed, i, 3);
                    if (Math.Abs(across) > h * 0.85) continue;
                    var span = h * (0.13 + 0.05 * Jitter(f.Seed, i, 4));
                    using var arc = new SKPath();
                    arc.MoveTo(f.At(along - span * 0.35, across - span));
                    arc.QuadTo(f.At(along + span * 0.45, across), f.At(along - span * 0.35, across + span));
                    canvas.DrawPath(arc, crest);
                }
        // Rocks: slate stones, each with a pillow of foam upstream and a V of wake downstream.
        var rocks = 3 + (int)((uint)f.Seed % 2);
        using var wake = Stroke(foam.WithAlpha(185), (float)Math.Max(0.5 / scale, h * 0.03));
        for (var k = 0; k < rocks; k++)
        {
            var along = -reach * 0.35 + reach * 0.7 * (k + 0.5) / rocks + h * 0.15 * Jitter(f.Seed, k, 6);
            var across = h * 0.7 * Jitter(f.Seed, k, 7);
            var r = h * (0.1 + 0.04 * (0.5 + 0.5 * Jitter(f.Seed, k, 8)));
            using (var pillow = new SKPath())
            {
                pillow.MoveTo(f.At(along - r * 0.2, across - r * 1.35));
                pillow.QuadTo(f.At(along - r * 1.8, across), f.At(along - r * 0.2, across + r * 1.35));
                canvas.DrawPath(pillow, wake);
            }
            foreach (var side in new[] { -1, 1 })
            {
                using var arm = new SKPath();
                arm.MoveTo(f.At(along + r * 0.8, across + side * r * 0.9));
                arm.QuadTo(f.At(along + r * 1.8, across + side * r * 1.2), f.At(along + r * 2.6, across + side * r * 1.7));
                canvas.DrawPath(arm, wake);
            }
            Stone(canvas, f, along, across, r, ink, k);
        }
    }

    private static void Ford(SKCanvas canvas, Frame f, SKColor ink, SKColor foam, double scale)
    {
        var h = f.Half;
        var depth = h * 0.7;
        // The shallow: a pale band across the stream with soft edges up and downstream.
        using (var band = Quad(f, (-depth, -h * 1.2), (depth, -h * 1.2), (depth, h * 1.2), (-depth, h * 1.2)))
        using (var paint = Gradient(f, -depth, depth,
            [foam.WithAlpha(0), foam.WithAlpha(140), foam.WithAlpha(140), foam.WithAlpha(0)], [0f, 0.35f, 0.65f, 1f]))
            canvas.DrawPath(band, paint);
        // Stepping-stones: two staggered rows across the water.
        const int stones = 7;
        for (var k = 0; k < stones; k++)
        {
            var across = -h * 0.92 + h * 1.84 * (k + 0.5) / stones;
            var along = (k % 2 == 0 ? -1 : 1) * h * 0.15 + h * 0.05 * Jitter(f.Seed, k, 9);
            var r = h * (0.1 + 0.035 * (0.5 + 0.5 * Jitter(f.Seed, k, 10)));
            Stone(canvas, f, along, across, r, ink, k + 17);
        }
        _ = scale;
    }

    /// <summary>A small rounded stone in the slate of the owner's mountains: light top, deep underside, ink outline.</summary>
    private static void Stone(SKCanvas canvas, Frame f, double along, double across, double r, SKColor ink, int k)
    {
        using var path = new SKPath();
        const int corners = 9;
        var points = new SKPoint[corners];
        for (var c = 0; c < corners; c++)
        {
            var angle = c * Math.PI * 2 / corners;
            var radius = r * (0.85 + 0.25 * (0.5 + 0.5 * Jitter(f.Seed, k * 13 + c, 11)));
            points[c] = f.At(along + Math.Cos(angle) * radius * 1.2, across + Math.Sin(angle) * radius);
        }
        // Rounded through the mid-points, so the stone is a pebble and not a polygon.
        var mid = (SKPoint a, SKPoint b) => new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        path.MoveTo(mid(points[^1], points[0]));
        for (var c = 0; c < corners; c++) path.QuadTo(points[c], mid(points[c], points[(c + 1) % corners]));
        path.Close();
        var centre = f.At(along, across);
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(centre.X - (float)r, centre.Y - (float)r), new SKPoint(centre.X + (float)r, centre.Y + (float)r),
                [new SKColor(0xD4, 0xE3, 0xEC), new SKColor(0x7D, 0x93, 0xA6), new SKColor(0x41, 0x58, 0x73)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(path, fill);
        using var outline = Stroke(ink, (float)Math.Max(r * 0.16, 0.4));
        canvas.DrawPath(path, outline);
    }

    /// <summary>A stroke that starts at full width and ends in a point, as a brush lifting off.</summary>
    private static void Taper(SKCanvas canvas, Frame f, double along0, double across0, double along1, double across1, double width, SKColor color)
    {
        using var path = new SKPath();
        path.MoveTo(f.At(along0, across0 - width));
        path.QuadTo(f.At((along0 + along1) / 2, (across0 + across1) / 2 - width * 0.6), f.At(along1, across1));
        path.QuadTo(f.At((along0 + along1) / 2, (across0 + across1) / 2 + width * 0.6), f.At(along0, across0 + width));
        path.Close();
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawPath(path, paint);
    }

    private static SKPath Quad(Frame f, (double A, double C) p0, (double A, double C) p1, (double A, double C) p2, (double A, double C) p3)
    {
        var path = new SKPath();
        path.MoveTo(f.At(p0.A, p0.C)); path.LineTo(f.At(p1.A, p1.C)); path.LineTo(f.At(p2.A, p2.C)); path.LineTo(f.At(p3.A, p3.C));
        path.Close();
        return path;
    }

    /// <summary>A fill graded along the flow, from <paramref name="from"/> to <paramref name="to"/> downstream.</summary>
    private static SKPaint Gradient(Frame f, double from, double to, SKColor[] colors, float[]? positions = null) => new()
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(f.At(from, 0), f.At(to, 0), colors, positions, SKShaderTileMode.Clamp),
    };

    private static SKPaint Stroke(SKColor color, float width) => new()
    {
        IsAntialias = true, Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
    };

    private static (double X, double Y) Unit(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length < 1e-9 ? (1, 0) : (x / length, y / length);
    }

    private static int Seed(Guid id)
    {
        var bytes = id.ToByteArray();
        return BitConverter.ToInt32(bytes, 0) ^ BitConverter.ToInt32(bytes, 12);
    }

    /// <summary>A deterministic value in −1..1 for one element of one feature.</summary>
    private static double Jitter(int seed, int index, int channel)
    {
        unchecked
        {
            var h = (uint)seed * 0x9E3779B1u ^ (uint)(index * 0x85EBCA77) ^ (uint)(channel * 0xC2B2AE3D);
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h / (double)uint.MaxValue * 2 - 1;
        }
    }
}
