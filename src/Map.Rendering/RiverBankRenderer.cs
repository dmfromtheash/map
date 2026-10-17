using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>Earth ledges, pen hatching and broken shore waves derived from the shared water outline.</summary>
public static class RiverBankRenderer
{
    private readonly record struct Point(SKPoint Edge, SKPoint Normal, SKPoint Tangent, float Width);

    /// <summary>
    /// What the derived bank geometry depends on: the bodies that form the shared outline (the
    /// river records, by reference, and the width they were evaluated at) and the look. The
    /// zoom is not part of it: everything kept here is world-space geometry; the scale only
    /// decides what of it is drawn.
    /// </summary>
    internal sealed record BankKey(River[] Rivers, double MinimumWidth, RiverStyle Style)
    {
        public bool Equals(BankKey? other) => other is not null && MinimumWidth == other.MinimumWidth
            && Style == other.Style && Rivers.Length == other.Rivers.Length
            && !Rivers.Where((river, i) => !ReferenceEquals(river, other.Rivers[i])).Any();
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(MinimumWidth); hash.Add(Style); hash.Add(Rivers.Length);
            foreach (var river in Rivers) hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(river));
            return hash.ToHashCode();
        }
    }

    private sealed record Contour(Point[] Points, SKPath Ribbon, SKPath Outline, SKPath[] WashBands, SKPath[] Fringe, float Length)
    {
        public void Dispose()
        {
            Ribbon.Dispose(); Outline.Dispose();
            foreach (var band in WashBands) band.Dispose();
            foreach (var band in Fringe) band.Dispose();
        }
    }

    private sealed class BankGeometry
    {
        public List<Contour>? Contours;
        public SKPath[]? Lines;
        public SKPath[]? Shallows;
        public void Dispose()
        {
            if (Contours is not null) foreach (var contour in Contours) contour.Dispose();
            if (Lines is not null) foreach (var line in Lines) line.Dispose();
            if (Shallows is not null) foreach (var band in Shallows) band.Dispose();
        }
    }

    // The ledge points and the shore-wave lines ask the union path whether thousands of sample
    // points lie inside it; on a map with a few rivers that was 100-250 ms of every rebuilt
    // terrain frame. Both are pure world-space geometry of the union and the style, so they are
    // kept for the last few outlines. Paths are handed out as copies: an entry may be evicted by
    // an export on another thread while the viewport is still drawing from it.
    private static readonly object CacheGate = new();
    private static readonly List<(BankKey Key, BankGeometry Value)> Cache = [];
    private const int CacheCapacity = 16;

    /// <summary>Diagnostics: how many times the derived geometry was built, and the time it took.</summary>
    public static long Builds;
    public static double BuildMilliseconds;

    private static T Cached<T>(BankKey? key, Func<BankGeometry, T?> read, Func<T> build, Action<BankGeometry, T> store) where T : class
    {
        var outer = build;
        build = () =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var made = outer();
            System.Threading.Interlocked.Increment(ref Builds);
            BuildMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return made;
        };
        if (key is null) return build();
        lock (CacheGate)
        {
            for (var i = 0; i < Cache.Count; i++)
            {
                if (!Cache[i].Key.Equals(key)) continue;
                var entry = Cache[i];
                if (i != Cache.Count - 1) { Cache.RemoveAt(i); Cache.Add(entry); }
                if (read(entry.Value) is { } held) return held;
                var made = build(); store(entry.Value, made); return made;
            }
        }
        var built = build();
        lock (CacheGate)
        {
            var fresh = new BankGeometry(); store(fresh, built);
            Cache.Add((key, fresh));
            while (Cache.Count > CacheCapacity) { Cache[0].Value.Dispose(); Cache.RemoveAt(0); }
        }
        return built;
    }

    private static List<Contour> BuildContours(SKPath water, RiverStyle style)
    {
        var contours = new List<Contour>();
        using var measure = new SKPathMeasure(water, false);
        do
        {
            var length = measure.Length;
            if (length < 2) continue;
            // World-space samples and a finite bound keep tile/export and viewport drawings equal.
            var count = Math.Clamp((int)Math.Ceiling(length / Math.Max(.8, style.BankReliefWidth / 6)), 8, 8192);
            var points = new Point[count];
            var ribbon = new SKPath();
            var outline = new SKPath();
            var painted = style.TextureId == "watercolour-water";
            var inkOpen = false;
            var flip = FlipsOutward(water, measure, length);
            for (var i = 0; i < count; i++)
            {
                measure.GetPositionAndTangent(length * i / count, out var p, out var tangent);
                var normal = Outward(tangent, flip);
                var undulation = RiverGeometry.Noise(3181, p.X * .043 + p.Y * .029);
                // The bank facing the map's upper edge is higher; the opposite side is a low shore.
                // Broad variations give a ledge, not a uniformly inflated tube.
                var facing = Math.Clamp((-normal.Y + normal.X * .25 + 1) / 2, 0, 1);
                var width = (float)(style.BankReliefWidth * (.30 + .83 * facing + .20 * undulation));
                if (painted)
                    width = Math.Min((float)style.BankReliefWidth * 1.15f,
                        width * (float)(1 + .34 * RiverGeometry.Noise(5837, p.X * .31 + p.Y * .23)));
                points[i] = new Point(p, normal, tangent, width);
                var outer = Add(p, normal, width);
                if (i == 0) ribbon.MoveTo(outer); else ribbon.LineTo(outer);
                if (!painted || RiverGeometry.Noise(3829, p.X * .15 + p.Y * .21) > -.18)
                {
                    if (inkOpen) outline.LineTo(outer); else outline.MoveTo(outer);
                    inkOpen = true;
                }
                else inkOpen = false;
            }
            if (!painted) outline.Close();
            for (var i = count - 1; i >= 0; i--) ribbon.LineTo(points[i].Edge);
            ribbon.Close();
            var washes = painted
                ? new[] { WashBand(points, .02f, .31f, 1423), WashBand(points, .38f, .65f, 2831), WashBand(points, .76f, .96f, 6947) }
                : Array.Empty<SKPath>();
            var fringe = painted
                ? new[] { WashBand(points, .88f, 1.40f, 8351), WashBand(points, .90f, 1.22f, 1747) }
                : Array.Empty<SKPath>();
            contours.Add(new Contour(points, ribbon, outline, washes, fringe, length));
        } while (measure.NextContour());
        return contours;
    }

    // Longitudinal pigment follows the bank, including bends and junctions. Build it with
    // the cached world-space outline so zoom, tile borders and export do not move the marks.
    private static SKPath WashBand(Point[] points, float inner, float outer, int seed)
    {
        var path = new SKPath();
        SKPoint At(Point point, float fraction)
        {
            var variation = (float)RiverGeometry.Noise(seed, point.Edge.X * .16 + point.Edge.Y * .12);
            return Add(point.Edge, point.Normal, point.Width * Math.Clamp(fraction + variation * .11f, 0, 1.5f));
        }
        for (var i = 0; i < points.Length; i++)
        {
            var point = At(points[i], outer);
            if (i == 0) path.MoveTo(point); else path.LineTo(point);
        }
        for (var i = points.Length - 1; i >= 0; i--) path.LineTo(At(points[i], inner));
        path.Close();
        return path;
    }

    internal static void Draw(SKCanvas canvas, SKPath water, RiverStyle style, double scale, BankKey? key = null)
    {
        if (style.BankReliefWidth <= 0 || style.BankReliefWidth * scale < .65) return;
        // RiverRenderer clips relief and ink together. Applying land alpha here again
        // would fade the earth ledge sooner than its outline on a soft coast.
        canvas.Save();
        canvas.ClipPath(water, SKClipOperation.Difference, antialias: true);
        using var earth = new SKPaint { Color = SceneRenderer.ParseColor(style.BankReliefColor), IsAntialias = true };
        var painted = style.TextureId == "watercolour-water";
        using var rim = new SKPaint { Color = SceneRenderer.ParseColor(style.BankColor).WithAlpha(painted ? (byte)135 : (byte)210),
            Style = SKPaintStyle.Stroke, StrokeWidth = .8f, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        using var hatch = new SKPaint { Color = SceneRenderer.ParseColor(style.BankColor).WithAlpha(
            (byte)Math.Round(style.BankHatchOpacity * 255)), Style = SKPaintStyle.Stroke,
            StrokeWidth = .65f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var light = new SKPaint { Color = new SKColor(240, 224, 187, 90), Style = SKPaintStyle.Stroke,
            StrokeWidth = .65f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var wetWash = new SKPaint { Color = new SKColor(53, 62, 53, 90), IsAntialias = true };
        using var dryWash = new SKPaint { Color = new SKColor(207, 187, 151, 86), IsAntialias = true };
        using var grassRim = new SKPaint { Color = new SKColor(173, 184, 127, 94), IsAntialias = true };
        using var fringeWash = new SKPaint { Color = new SKColor(114, 128, 78, 36), IsAntialias = true };

        List<Contour> contours;
        lock (CacheGate)
        {
            contours = Cached(key, g => g.Contours, () => BuildContours(water, style), (g, c) => g.Contours = c)
                .Select(c => c with { Ribbon = new SKPath(c.Ribbon), Outline = new SKPath(c.Outline),
                    WashBands = c.WashBands.Select(b => new SKPath(b)).ToArray(),
                    Fringe = c.Fringe.Select(b => new SKPath(b)).ToArray() }).ToList();
        }
        try
        {
        var clip = canvas.LocalClipBounds;
        clip.Inflate((float)(style.BankReliefWidth * 2 + 3), (float)(style.BankReliefWidth * 2 + 3));
        foreach (var contour in contours)
        {
            if (!clip.IntersectsWith(contour.Ribbon.Bounds)) continue;
            var points = contour.Points; var count = points.Length; var length = contour.Length;
            var ribbon = contour.Ribbon;
            foreach (var fringe in contour.Fringe) canvas.DrawPath(fringe, fringeWash);
            canvas.DrawPath(ribbon, earth);
            if (contour.WashBands.Length == 3)
            {
                canvas.Save();
                canvas.ClipPath(ribbon, SKClipOperation.Intersect, antialias: true);
                canvas.DrawPath(contour.WashBands[0], wetWash);
                canvas.DrawPath(contour.WashBands[1], dryWash);
                canvas.DrawPath(contour.WashBands[2], grassRim);
                canvas.Restore();
            }
            canvas.DrawPath(contour.Outline, rim);
            if (style.BankHatchOpacity <= 0 || style.BankReliefWidth * scale < 2.5) continue;

            canvas.Save();
            canvas.ClipPath(ribbon, SKClipOperation.Intersect, antialias: true);
            var spacing = Math.Max(3.4, style.BankReliefWidth * .65);
            var next = spacing * .3;
            for (var i = 0; i < count; i++)
            {
                var distance = length * i / count;
                if (distance < next) continue;
                var p = points[i];
                var variation = RiverGeometry.Noise(5731, p.Edge.X * .21 + p.Edge.Y * .14);
                next = distance + spacing * (.90 + .60 * variation);
                if (!clip.Contains(p.Edge.X, p.Edge.Y)) continue;
                if (p.Width * scale < 2) continue;
                var start = Add(p.Edge, p.Normal, p.Width * .15f);
                var end = Add(Add(p.Edge, p.Normal, p.Width * (float)(.60 + .30 * variation)),
                    p.Tangent, p.Width * (float)(.12 + .16 * variation));
                var middle = Add(start, p.Normal, p.Width * .23f);
                using var stroke = new SKPath();
                stroke.MoveTo(start); stroke.QuadTo(middle, end);
                canvas.DrawPath(stroke, hatch);
                if (variation > .2) canvas.DrawLine(Add(start, p.Tangent, 1), Add(end, p.Tangent, 1), light);
                if (painted)
                {
                    var grain = Add(Add(p.Edge, p.Normal, p.Width * .55f), p.Tangent, (float)(variation * 3));
                    canvas.DrawCircle(grain, (float)(.5 + .25 * variation), wetWash);
                    canvas.DrawCircle(Add(grain, p.Tangent, 1.4f), .4f, dryWash);
                }
            }
            canvas.Restore();
        }
        }
        finally { foreach (var contour in contours) contour.Dispose(); }
        canvas.Restore();
    }

    public static void DrawInkOutline(SKCanvas canvas, SKPath water, RiverStyle style)
    {
        using var ink = new SKPaint { Color = SceneRenderer.ParseColor(style.BankColor), IsAntialias = true };
        var painted = style.TextureId == "watercolour-water";
        if (painted)
        {
            // Short pressure-varying marks let the pigment define the shore between them.
            // A continuous opaque line made painted rivers look like a bordered road.
            ink.Style = SKPaintStyle.Stroke; ink.StrokeWidth = (float)(style.BankWidth * .65);
            ink.StrokeCap = SKStrokeCap.Round; ink.Color = ink.Color.WithAlpha(145);
        }
        using var measure = new SKPathMeasure(water, false);
        do
        {
            if (measure.Length < 1) continue;
            var count = Math.Clamp((int)Math.Ceiling(measure.Length / 1.2), 8, 8192);
            var inner = new SKPoint[count];
            using var ribbon = new SKPath();
            var open = false;
            for (var i = 0; i < count; i++)
            {
                measure.GetPositionAndTangent(measure.Length * i / count, out var p, out var tangent);
                if (painted)
                {
                    var present = RiverGeometry.Noise(6847, p.X * .12 + p.Y * .17) > .12;
                    if (!present) { open = false; continue; }
                    if (open) ribbon.LineTo(p); else ribbon.MoveTo(p);
                    open = true; continue;
                }
                var normal = new SKPoint(-tangent.Y, tangent.X);
                var pressure = (float)(style.BankWidth * (.45 + .18 * Math.Abs(tangent.X)
                    + .22 * RiverGeometry.Noise(9811, p.X * .091 + p.Y * .077)));
                var outer = Add(p, normal, pressure);
                inner[i] = Add(p, normal, -pressure);
                if (i == 0) ribbon.MoveTo(outer); else ribbon.LineTo(outer);
            }
            if (!painted)
            {
                for (var i = count - 1; i >= 0; i--) ribbon.LineTo(inner[i]);
                ribbon.Close();
            }
            canvas.DrawPath(ribbon, ink);
        } while (measure.NextContour());
    }

    internal static void DrawPaintedShallows(SKCanvas canvas, SKPath water, RiverStyle style, double width, BankKey key)
    {
        SKPath[] bands;
        lock (CacheGate)
        {
            bands = Cached(key, g => g.Shallows, () => BuildPaintedShallows(water, width), (g, p) => g.Shallows = p)
                .Select(p => new SKPath(p)).ToArray();
        }
        var color = SceneRenderer.ParseColor(style.ShallowColor);
        using var paint = new SKPaint { IsAntialias = true };
        try
        {
            canvas.Save(); canvas.ClipPath(water, SKClipOperation.Intersect, antialias: true);
            for (var i = 0; i < bands.Length; i++)
            {
                paint.Color = color.WithAlpha((byte)((20 + i * 6) * color.Alpha / 255));
                canvas.DrawPath(bands[i], paint);
            }
            canvas.Restore();
        }
        finally { foreach (var band in bands) band.Dispose(); }
    }

    private static SKPath[] BuildPaintedShallows(SKPath water, double width)
    {
        var bands = Enumerable.Range(0, 6).Select(_ => new SKPath()).ToArray();
        using var measure = new SKPathMeasure(water, false);
        do
        {
            var length = measure.Length;
            if (length < 2) continue;
            var count = Math.Clamp((int)Math.Ceiling(length), 8, 8192);
            var flip = FlipsOutward(water, measure, length);
            var edge = new SKPoint[count]; var normal = new SKPoint[count]; var depth = new float[count];
            for (var i = 0; i < count; i++)
            {
                measure.GetPositionAndTangent(length * i / count, out edge[i], out var tangent);
                normal[i] = Outward(tangent, flip);
                var p = edge[i];
                depth[i] = (float)(width * (.85 + .36 * RiverGeometry.Noise(6419, p.X * .08 + p.Y * .11)
                    + .22 * RiverGeometry.Noise(9151, p.X * .49 + p.Y * .33)));
            }
            for (var band = 0; band < bands.Length; band++)
            {
                var path = bands[band];
                path.MoveTo(edge[0]);
                for (var i = 1; i < count; i++) path.LineTo(edge[i]);
                for (var i = count - 1; i >= 0; i--)
                    path.LineTo(Add(edge[i], normal[i], -depth[i] * (1 - band * .16f)));
                path.Close();
            }
        } while (measure.NextContour());
        return bands;
    }

    internal static void DrawWaterLines(SKCanvas canvas, SKPath water, RiverStyle style, double scale, BankKey? key = null)
    {
        if (style.HighlightOpacity <= 0 || scale < .18) return;
        canvas.Save();
        canvas.ClipPath(water, SKClipOperation.Intersect, antialias: true);
        using var paint = new SKPaint { Color = SceneRenderer.ParseColor(style.HighlightColor).WithAlpha(
            (byte)Math.Round(style.HighlightOpacity * 255)), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = .7f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        SKPath[] lines;
        lock (CacheGate)
        {
            lines = Cached(key, g => g.Lines, () => BuildWaterLines(water, style), (g, l) => g.Lines = l)
                .Select(line => new SKPath(line)).ToArray();
        }
        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (style.TextureId == "watercolour-water") paint.StrokeWidth = i < 3 ? 1.6f : .85f;
                canvas.DrawPath(lines[i], paint);
            }
        }
        finally { foreach (var line in lines) line.Dispose(); }
        canvas.Restore();
    }

    /// <summary>The two shore-wave strands of every contour, in world space, as one path per strand.</summary>
    private static SKPath[] BuildWaterLines(SKPath water, RiverStyle style)
    {
        var painted = style.TextureId == "watercolour-water";
        var strands = Enumerable.Range(0, painted ? 5 : 2).Select(_ => new SKPath()).ToArray();
        using var measure = new SKPathMeasure(water, false);
        do
        {
            var length = measure.Length;
            var count = Math.Clamp((int)Math.Ceiling(length / 1.25), 8, 8192);
            var flip = FlipsOutward(water, measure, length);
            for (var strand = 0; strand < strands.Length; strand++)
            {
                var line = strands[strand];
                var open = false;
                for (var i = 0; i < count; i++)
                {
                    var distance = length * i / count;
                    measure.GetPositionAndTangent(distance, out var edge, out var tangent);
                    var normal = Outward(tangent, flip);
                    var drift = RiverGeometry.Noise(7471 + strand, edge.X * .027 + edge.Y * .041);
                    var offset = (float)(2.8 + strand * 4.3 + drift * .9);
                    if (painted)
                        offset = (float)(1.3 + strand * 1.55 + RiverGeometry.Noise(9257 + strand, edge.X * .24 + edge.Y * .33) * 1.05);
                    var p = Add(edge, normal, -offset);
                    var visible = (distance + strand * 29) % (70 + strand * 13) < 35 + drift * 17;
                    if (painted) visible = (distance + strand * 7) % (11 + strand * 6) < 4 + drift * 3;
                    // A narrow creek has no room for both shore waves; never draw one across its bank.
                    if (!visible || !water.Contains(p.X, p.Y) || !water.Contains(
                            p.X - normal.X * 1.8f, p.Y - normal.Y * 1.8f)) { open = false; continue; }
                    if (open) line.LineTo(p); else { line.MoveTo(p); open = true; }
                }
            }
        } while (measure.NextContour());
        return strands;
    }

    /// <summary>
    /// Which side of a contour the water lies on is a property of the contour, not of each
    /// sample: nine samples vote by a point-in-path test, and every sample then turns its
    /// tangent the same way. Asking the path about thousands of samples cost over a second on
    /// a long river; a sample in a narrow reach that once voted alone the other way no longer
    /// flips its ledge.
    /// </summary>
    private static bool FlipsOutward(SKPath water, SKPathMeasure measure, float length)
    {
        var inside = 0; const int votes = 9;
        for (var v = 0; v < votes; v++)
        {
            measure.GetPositionAndTangent(length * (v + .5f) / votes, out var p, out var tangent);
            if (water.Contains(p.X - tangent.Y * .35f, p.Y + tangent.X * .35f)) inside++;
        }
        return inside * 2 > votes;
    }

    private static SKPoint Outward(SKPoint tangent, bool flip) =>
        flip ? new SKPoint(tangent.Y, -tangent.X) : new SKPoint(-tangent.Y, tangent.X);

    private static SKPoint Add(SKPoint p, SKPoint direction, float distance) =>
        new(p.X + direction.X * distance, p.Y + direction.Y * distance);
}
