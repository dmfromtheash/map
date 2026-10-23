using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The banks of a river drawn the way map artists draw real rivers - the look the owner chose
/// on 2026-10-22 («В3»): no pen line around the water; its edge is a ragged dry-brush stroke with
/// the pigment pooled dark at the rim, the deep stream swings to the outer bank of every bend,
/// a crescent of sand (a point bar) lies inside each bend and tufts of reeds gather by some of
/// them. Everything follows the seed and the channel distance, so the screen, tiles and export agree.
/// </summary>
internal static class RiverNaturalBanks
{
    private static readonly SKColor Sand = new(0xC9, 0xBF, 0xA3);
    private static readonly SKColor Reed = new(0x4A, 0x55, 0x3E);

    public static void Draw(SKCanvas canvas, RiverGeometrySet set, RiverStyle style, SKPath body, double scale)
    {
        var ink = SceneRenderer.ParseColor(style.BankColor);
        var water = SceneRenderer.ParseColor(style.WaterColor);
        var baseWidth = Math.Max(0.7 / scale, style.BankWidth);
        var flowing = set.Channels.Values.SelectMany(g => g.Samples).Where(s => s.HalfWidth > 0).ToList();
        if (flowing.Count == 0) return;
        var riverHalf = flowing.Average(s => s.HalfWidth);

        // Where one channel meets another the banks open: no reeds across a mouth or a fork.
        var joins = new List<(double X, double Y, double Reach)>();
        foreach (var g in set.Channels.Values)
        {
            if (g.Samples.Length == 0) continue;
            if (g.JoinsParent) joins.Add((g.End.X, g.End.Y, g.Samples[^1].HalfWidth * 1.6 + g.ParentHalfWidthAtMouth * 0.4 + 1));
            if (g.LeavesParent) joins.Add((g.Start.X, g.Start.Y, g.Samples[0].HalfWidth * 1.6 + g.ParentHalfWidthAtSource * 0.4 + 1));
        }
        bool Open(double x, double y) => joins.All(j => (x - j.X) * (x - j.X) + (y - j.Y) * (y - j.Y) > j.Reach * j.Reach);

        canvas.Save();
        canvas.ClipPath(body, SKClipOperation.Intersect, antialias: true);
        foreach (var geometry in set.Channels.Values)
        {
            if (geometry.Closed || geometry.Samples.Length < 2) continue;
            var half = geometry.Samples.Average(s => s.HalfWidth);
            if (half * scale < 2.5) continue;
            StreamAndBars(canvas, geometry, style, water, ink, Bends(geometry.Samples, half), geometry.Channel.Id.GetHashCode(), scale);
        }
        canvas.Restore();

        DryBrushEdge(canvas, body, water, ink, riverHalf, set.River.Id.GetHashCode(), scale);

        canvas.Save();
        canvas.ClipPath(body, SKClipOperation.Difference, antialias: true);
        foreach (var geometry in set.Channels.Values)
        {
            if (geometry.Closed || geometry.Samples.Length < 2) continue;
            var half = geometry.Samples.Average(s => s.HalfWidth);
            if (half * scale < 3) continue;
            Reeds(canvas, geometry, style, baseWidth, Bends(geometry.Samples, half), geometry.Channel.Id.GetHashCode(), scale, ink, Open);
        }
        canvas.Restore();
    }

    /// <summary>
    /// The signed bend of the course at each sample, smoothed over a few widths: positive turns to
    /// the +1 bank of <see cref="BankPoint"/> (which is then the inner bank; checked on a drawn
    /// meander). Scaled by the half width, so 0.3 is a tight bend.
    /// </summary>
    private static double[] Bends(RiverSample[] samples, double half)
    {
        var raw = new double[samples.Length];
        for (var i = 1; i < samples.Length - 1; i++)
        {
            var a = samples[i - 1]; var b = samples[i + 1];
            var ds = Math.Max(1e-6, b.Distance - a.Distance);
            raw[i] = (a.TangentX * b.TangentY - a.TangentY * b.TangentX) / ds * Math.Max(half, samples[i].HalfWidth);
        }
        var window = Math.Max(2, half * 3);
        var smooth = new double[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            double sum = 0; var n = 0;
            for (var j = i; j >= 0 && samples[i].Distance - samples[j].Distance <= window; j--) { sum += raw[j]; n++; }
            for (var j = i + 1; j < samples.Length && samples[j].Distance - samples[i].Distance <= window; j++) { sum += raw[j]; n++; }
            smooth[i] = n > 0 ? sum / n : 0;
        }
        return smooth;
    }

    /// <summary>0..1: how much of a sand bar (inner bank) or a cut bank (outer bank) this bend makes.</summary>
    private static double BendStrength(double bend) => Math.Clamp((Math.Abs(bend) - 0.025) / 0.15, 0, 1);

    /// <summary>A point on one bank, exactly on the water's edge (the outline's own wobble), moved out by <paramref name="extra"/>.</summary>
    private static SKPoint BankPoint(RiverSample s, int side, RiverStyle style, int seed, double extra = 0)
    {
        var wobble = RiverGeometry.BankWobble(seed, s, side > 0, style.EdgeRoughness, style.BankManner);
        var reach = (s.HalfWidth + wobble + extra) * side;
        return new SKPoint((float)(s.X - s.TangentY * reach), (float)(s.Y + s.TangentX * reach));
    }

    /// <summary>
    /// The real river from above: the deep stream swings to the outer bank of every bend, and on
    /// the inner bank a ragged crescent of sand shelves into the water, its waterline in shade.
    /// </summary>
    private static void StreamAndBars(SKCanvas canvas, RiverChannelGeometry geometry, RiverStyle style, SKColor water, SKColor ink,
        double[] bends, int seed, double scale)
    {
        var samples = geometry.Samples;
        var half = samples.Average(s => s.HalfWidth);
        using (var stream = new SKPath())
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var s = samples[i];
                var shift = -Math.Sign(bends[i]) * BendStrength(bends[i]) * 0.4 * s.HalfWidth;
                var p = new SKPoint((float)(s.X - s.TangentY * shift), (float)(s.Y + s.TangentX * shift));
                if (i == 0) stream.MoveTo(p); else stream.LineTo(p);
            }
            using var deep = new SKPaint
            {
                Color = Mix(water, ink, .4f).WithAlpha(105), IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(half * 0.75), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)Math.Max(0.5, half * 0.3)),
            };
            canvas.DrawPath(stream, deep);
        }

        using var bars = new SKPath();
        using var edges = new SKPath();
        var bankRun = new List<SKPoint>();
        var edgeRun = new List<SKPoint>();
        var runSide = 0;
        void Close()
        {
            if (bankRun.Count >= 3)
            {
                bars.MoveTo(bankRun[0]);
                foreach (var p in bankRun.Skip(1)) bars.LineTo(p);
                for (var k = edgeRun.Count - 1; k >= 0; k--) bars.LineTo(edgeRun[k]);
                bars.Close();
                edges.MoveTo(edgeRun[0]);
                foreach (var p in edgeRun.Skip(1)) edges.LineTo(p);
            }
            bankRun.Clear(); edgeRun.Clear(); runSide = 0;
        }
        // A point bar is a crescent at the apex of a bend, not a stripe along every curve: find the
        // runs of one bend, then give each a crescent that is fullest at its sharpest point and
        // thins to nothing at both ends (owner 2026-10-23: the bars looked like thin stripes).
        foreach (var (from, to, side) in BendRuns(samples, bends, half))
        {
            var peak = 0.0;
            for (var i = from; i <= to; i++) peak = Math.Max(peak, BendStrength(bends[i]));
            var start = samples[from].Distance; var length = Math.Max(1e-6, samples[to].Distance - start);
            var reach = Math.Min(1.15, 0.75 + 0.6 * peak); // a gentle bend shelves a third of the bed, a tight one more than half
            for (var i = from; i <= to; i++)
            {
                var s = samples[i];
                var t = (s.Distance - start) / length;
                var crescent = Math.Pow(Math.Sin(Math.PI * t), 1.4); // fullest at the apex, long thin horns
                var depth = s.HalfWidth * reach * crescent
                    * (0.85 + 0.15 * RiverGeometry.Noise(seed ^ 0x3A1, s.Distance / Math.Max(3, s.HalfWidth)));
                bankRun.Add(BankPoint(s, side, style, seed, s.HalfWidth * 0.2));
                edgeRun.Add(BankPoint(s, side, style, seed, s.HalfWidth * 0.2 - Math.Max(depth, s.HalfWidth * 0.2)));
            }
            Close();
        }
        if (bars.IsEmpty) return;
        // The waterline of sand is never a smooth curve.
        using (var ragged = SKPathEffect.CreateDiscrete((float)Math.Max(2, half * 0.35), (float)Math.Max(0.3, half * 0.09), (uint)seed))
        using (var shaper = new SKPaint { PathEffect = ragged, Style = SKPaintStyle.Fill })
        using (var rough = new SKPath())
            if (shaper.GetFillPath(bars, rough)) { bars.Reset(); bars.AddPath(rough); }
        using (var shadow = new SKPaint
        {
            Color = Mix(water, ink, .5f).WithAlpha(120), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(0.8 / scale, half * 0.18), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)Math.Max(0.5, half * 0.1)),
        })
            canvas.DrawPath(edges, shadow);
        using (var sand = new SKPaint { Color = Sand, IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawPath(bars, sand);
        // Drier and wetter sand along the bar, as the brush follows the current.
        canvas.Save();
        canvas.ClipPath(bars, SKClipOperation.Intersect, antialias: true);
        using (var streak = new SKPaint
        {
            Color = Mix(Sand, ink, .25f).WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(0.5 / scale, half * 0.06), StrokeCap = SKStrokeCap.Round,
            PathEffect = SKPathEffect.CreateDash([(float)(half * 1.2), (float)(half * 0.7)], (float)(Math.Abs(seed % 7) * half * 0.2)),
        })
            canvas.DrawPath(edges, streak);
        using (var wet = new SKPaint
        {
            Color = Mix(Sand, water, .45f).WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(half * 0.16), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)Math.Max(0.5, half * 0.06)),
        })
            canvas.DrawPath(edges, wet);
        canvas.Restore();
    }

    /// <summary>
    /// Stretches of one bend (same sign) that turn enough to build a bar: at least two widths long
    /// and sharp enough at their peak. Returns sample index ranges and the inner bank's side.
    /// </summary>
    private static List<(int From, int To, int Side)> BendRuns(RiverSample[] samples, double[] bends, double half)
    {
        var runs = new List<(int, int, int)>();
        var from = -1; var side = 0; var peak = 0.0;
        void End(int to)
        {
            if (from >= 0 && peak >= 0.3 && samples[to].Distance - samples[from].Distance >= half * 4) runs.Add((from, to, side));
            from = -1; side = 0; peak = 0;
        }
        for (var i = 0; i < samples.Length; i++)
        {
            var strength = BendStrength(bends[i]);
            var sign = Math.Sign(bends[i]);
            if (strength < 0.12 || samples[i].HalfWidth <= 0 || (from >= 0 && sign != side)) End(Math.Max(0, i - 1));
            if (strength < 0.12 || samples[i].HalfWidth <= 0) continue;
            if (from < 0) { from = i; side = sign; }
            peak = Math.Max(peak, strength);
        }
        End(samples.Length - 1);
        return runs;
    }

    /// <summary>No pen line: the edge of the water is a dry-brush stroke - pigment pooled dark at a ragged rim.</summary>
    private static void DryBrushEdge(SKCanvas canvas, SKPath body, SKColor water, SKColor ink, double half, int seed, double scale)
    {
        using var rim = new SKPaint
        {
            Color = Mix(water, ink, .55f).WithAlpha(170), IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(0.8 / scale, half * 0.22),
            PathEffect = SKPathEffect.CreateDiscrete((float)Math.Max(1.5, half * 0.25), (float)Math.Max(0.3, half * 0.08), (uint)seed),
        };
        canvas.DrawPath(body, rim);
    }

    /// <summary>Tufts of reeds by the inner bank of some bends - gathered, never in a row.</summary>
    private static void Reeds(SKCanvas canvas, RiverChannelGeometry geometry, RiverStyle style, double baseWidth,
        double[] bends, int seed, double scale, SKColor ink, Func<double, double, bool> open)
    {
        var samples = geometry.Samples;
        using var blades = new SKPath();
        var bend = 0;
        var i = 0;
        while (i < samples.Length)
        {
            // One run of a bend: find its peak and let the seed decide whether it gets reeds.
            if (BendStrength(bends[i]) < 0.3) { i++; continue; }
            var side = Math.Sign(bends[i]);
            var peak = i;
            while (i < samples.Length && Math.Sign(bends[i]) == side && BendStrength(bends[i]) >= 0.3)
            {
                if (Math.Abs(bends[i]) > Math.Abs(bends[peak])) peak = i;
                i++;
            }
            bend++;
            if (((uint)(seed + bend * 0x9E3779B1) >> 7) % 3 != 0) continue;
            var s = samples[peak];
            var root = BankPoint(s, side, style, seed, baseWidth * 1.5 + s.HalfWidth * 0.1);
            if (!open(root.X, root.Y)) continue;
            var nx = -s.TangentY * side; var ny = s.TangentX * side;
            for (var k = 0; k < 6; k++)
            {
                var along = (k - 2.5) * s.HalfWidth * 0.14;
                var lean = (k - 2.5) * 0.16 + 0.1 * RiverGeometry.Noise(seed ^ k, k * 1.7);
                var length = s.HalfWidth * (0.32 + 0.18 * (0.5 + 0.5 * RiverGeometry.Noise(seed ^ (k * 31), bend + k)));
                var bx = root.X + (float)(s.TangentX * along); var by = root.Y + (float)(s.TangentY * along);
                var dx = nx + s.TangentX * lean; var dy = ny + s.TangentY * lean;
                var norm = Math.Sqrt(dx * dx + dy * dy);
                blades.MoveTo(bx, by);
                blades.LineTo(bx + (float)(dx / norm * length), by + (float)(dy / norm * length));
            }
        }
        if (blades.IsEmpty) return;
        using var pen = new SKPaint { Color = Mix(Reed, ink, .3f).WithAlpha(190), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(0.5 / scale, baseWidth * 0.55), StrokeCap = SKStrokeCap.Round };
        canvas.DrawPath(blades, pen);
    }

    private static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t), a.Alpha);
}
