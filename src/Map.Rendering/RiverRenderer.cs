using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Draws the rivers of an object layer from the pure Core geometry. All channels of one river
/// are merged into a single outline with Skia path ops, so a tributary mouth has no seam, no
/// double opacity and one continuous bank line. The same evaluation serves the viewport, tiles
/// and single-image export; nothing here writes back to the document.
/// </summary>
public static class RiverRenderer
{
    /// <summary>A river never drops below this on-screen width, so far zoom keeps it as a thread.</summary>
    public const double MinimumScreenWidth = 1.5;

    private static readonly ConditionalWeakTable<River, BodyCache> Bodies = [];

    /// <summary>Rivers of the layer with live editor substitutions applied by id.</summary>
    public static IReadOnlyList<River> Resolve(ObjectLayer layer, RenderOptions options)
    {
        if (options.RiverOverrides is not { Count: > 0 } overrides) return layer.Rivers;
        var result = new List<River>(layer.Rivers.Length + 1);
        foreach (var river in layer.Rivers)
            result.Add(overrides.FirstOrDefault(o => o.Id == river.Id) ?? river);
        if (options.RiverPreviewLayerId == layer.Id)
            foreach (var extra in overrides)
                if (layer.Rivers.All(r => r.Id != extra.Id)) result.Add(extra);
        return result;
    }

    public static void DrawLayerRivers(SKCanvas canvas, ObjectLayer layer, WorldBounds view, RenderOptions options,
        Action<SKCanvas, SKPaint>? drawLandCoverage = null)
    {
        if (layer.Rivers.IsEmpty && options.RiverOverrides is not { Count: > 0 }) return;
        foreach (var river in Resolve(layer, options))
        {
            if (!river.Visible) continue;
            if (!RiverGeometry.Evaluate(river).Bounds.Intersects(view)) continue;
            Draw(canvas, river, options.ViewScale, options.FastPreview, river.Style.ClipToLand ? drawLandCoverage : null);
            if (options.Stats is { } stats) { stats.ObjectsDrawn++; stats.DrawCalls++; }
        }
    }

    /// <summary>Erases the merged body of a river from whatever the canvas holds (land coverage carving).</summary>
    public static void CutBody(SKCanvas canvas, River river, double viewScale)
    {
        var scale = Math.Max(viewScale, 1e-4);
        var cache = Bodies.GetValue(river, static _ => new BodyCache());
        lock (cache)
        {
            var entry = cache.Get(river, MinimumScreenWidth / scale);
            using var erase = new SKPaint { BlendMode = SKBlendMode.DstOut, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(entry.Body, erase);
            // A hair of extra clearance keeps the coast outline from peeking under the bank stroke.
            using var edge = new SKPaint { BlendMode = SKBlendMode.DstOut, IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(river.Style.BankWidth * 0.5, 0.6 / scale), StrokeJoin = SKStrokeJoin.Round };
            canvas.DrawPath(entry.Body, edge);
        }
    }

    /// <summary>Draws one river at the given view scale. Public so the editor can preview a draft identically.</summary>
    public static void Draw(SKCanvas canvas, River river, double viewScale, bool fastPreview, Action<SKCanvas, SKPaint>? clipToLand = null)
    {
        var scale = Math.Max(viewScale, 1e-4);
        var minimumWidth = MinimumScreenWidth / scale;
        var style = river.Style;
        var cache = Bodies.GetValue(river, static _ => new BodyCache());
        lock (cache)
        {
            var entry = cache.Get(river, minimumWidth);
            var set = entry.Geometry;
            var body = entry.Body;
            var bounds = set.Bounds;
            var fadeChannels = style.MouthFade > 0
                ? set.Channels.Values.Where(g => !g.JoinsParent).ToArray()
                : [];
            var rect = new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);
            var grouped = style.Opacity < 1 || fadeChannels.Length > 0 || clipToLand is not null;
            if (grouped)
            {
                using var group = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(style.Opacity * 255), 0, 255)) };
                canvas.SaveLayer(rect, group);
            }
            else canvas.Save();

            var bank = style.BankWidth;
            if (bank > 0 && bank * scale < 0.6) bank = 0.6 / scale;
            var haloVisible = style.BankShadowOpacity > 0 && !fastPreview && style.BankShadowBlur * scale >= 0.5;
            if (haloVisible)
            {
                using var halo = new SKPaint
                {
                    Color = SceneRenderer.ParseColor(style.BankColor).WithAlpha((byte)Math.Clamp(Math.Round(style.BankShadowOpacity * 255), 0, 255)),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)(bank + style.BankShadowBlur),
                    StrokeJoin = SKStrokeJoin.Round,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)style.BankShadowBlur),
                };
                canvas.DrawPath(body, halo);
            }

            using (var water = new SKPaint { Color = SceneRenderer.ParseColor(style.WaterColor), IsAntialias = true, Style = SKPaintStyle.Fill })
                canvas.DrawPath(body, water);

            if (style.HighlightOpacity > 0)
                DrawHighlights(canvas, set, style, body, scale);

            if (bank > 0)
            {
                using var edge = new SKPaint
                {
                    Color = SceneRenderer.ParseColor(style.BankColor), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = (float)bank, StrokeJoin = SKStrokeJoin.Round,
                };
                canvas.DrawPath(body, edge);
            }

            foreach (var channel in fadeChannels)
            {
                // Dissolve the free end into the sea: erase river pixels only, inside the group.
                var end = channel.End;
                var radius = (float)style.MouthFade;
                using var shader = SKShader.CreateRadialGradient(new SKPoint((float)end.X, (float)end.Y), radius,
                    [SKColors.Black, SKColors.Black.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp);
                using var erase = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstOut, IsAntialias = true };
                canvas.DrawCircle((float)end.X, (float)end.Y, radius, erase);
            }
            if (clipToLand is not null)
            {
                // Keep the water only where Land coverage exists: one DstIn with the union of all land layers.
                using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.SaveLayer(rect, keep);
                using var plain = new SKPaint();
                clipToLand(canvas, plain);
                canvas.Restore();
            }
            canvas.Restore();
        }
    }

    /// <summary>
    /// Light flow strokes: short dashes along two lines inside the water. The dash pieces are
    /// cut here by channel distance instead of a Skia dash effect, so a tile and the viewport cut
    /// them identically regardless of the device offset or clip.
    /// </summary>
    private static void DrawHighlights(SKCanvas canvas, RiverGeometrySet set, RiverStyle style, SKPath body, double scale)
    {
        var color = SceneRenderer.ParseColor(style.HighlightColor).WithAlpha((byte)Math.Clamp(Math.Round(style.HighlightOpacity * 255), 0, 255));
        canvas.Save();
        canvas.ClipPath(body, SKClipOperation.Intersect, antialias: true);
        foreach (var geometry in set.Channels.Values)
        {
            var samples = geometry.Samples;
            if (samples.Length < 2) continue;
            var averageHalf = samples.Average(s => s.HalfWidth);
            // Too thin on screen: light strokes would only turn into noise.
            if (averageHalf * scale < 3) continue;
            var width = Math.Max(0.75 / scale, averageHalf * 0.14);
            using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)width, StrokeCap = SKStrokeCap.Round };
            using var second = new SKPaint { Color = color.WithAlpha((byte)(color.Alpha * 0.7)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(width * 0.8), StrokeCap = SKStrokeCap.Round };
            using var left = DashedOffsetPath(samples, 0.34, averageHalf * 3.2, averageHalf * 4.5, 0);
            using var right = DashedOffsetPath(samples, -0.34, averageHalf * 2.4, averageHalf * 5.6, averageHalf * 3.7);
            canvas.DrawPath(left, paint);
            canvas.DrawPath(right, second);
        }
        canvas.Restore();
    }

    private static SKPath DashedOffsetPath(RiverSample[] samples, double offsetFraction, double dash, double gap, double phase)
    {
        var path = new SKPath();
        var period = dash + gap;
        var open = false;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            var position = (s.Distance + phase) % period;
            var on = position < dash;
            var nx = -s.TangentY; var ny = s.TangentX;
            var offset = s.HalfWidth * offsetFraction;
            var point = new SKPoint((float)(s.X + nx * offset), (float)(s.Y + ny * offset));
            if (on)
            {
                if (open) path.LineTo(point); else { path.MoveTo(point); open = true; }
            }
            else if (open)
            {
                path.LineTo(point);
                open = false;
            }
        }
        return path;
    }

    /// <summary>Builds the merged body outline of every channel of a river for one evaluation.</summary>
    internal static SKPath BuildBody(RiverGeometrySet set, RiverStyle style)
    {
        using var union = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var geometry in set.Channels.Values)
        {
            var outline = RiverGeometry.Outline(geometry, style.EdgeRoughness);
            if (outline.Count < 3) continue;
            union.MoveTo((float)outline[0].X, (float)outline[0].Y);
            for (var i = 1; i < outline.Count; i++) union.LineTo((float)outline[i].X, (float)outline[i].Y);
            union.Close();
        }
        var body = new SKPath();
        if (!union.Simplify(body)) { body.Dispose(); body = new SKPath(union); }
        // Round every corner of the merged outline in world space, independently of any clip or
        // device offset, so the inner corner of a confluence merges softly and a tile never
        // disagrees with the viewport. Radius follows the river's average width.
        // A lake contributes hundreds of samples with no half width at all, and averaging those in
        // collapsed the radius to almost nothing: the inner corner where an inflow met the shore
        // stayed a sharp dark V. The radius follows the water that actually has a width.
        var flowing = set.Channels.Values.SelectMany(g => g.Samples).Where(s => s.HalfWidth > 0).ToList();
        var average = flowing.Count > 0 ? flowing.Average(s => s.HalfWidth) : 0;
        var rounded = RoundCorners(body, (float)Math.Min(12, average * 0.8));
        body.Dispose();
        return rounded;
    }

    /// <summary>Replaces each polygon corner by a quadratic arc reaching at most half a segment back and forth.</summary>
    internal static SKPath RoundCorners(SKPath source, float radius)
    {
        var result = new SKPath { FillType = source.FillType };
        if (radius <= 0) { result.AddPath(source); return result; }
        var contour = new List<SKPoint>();
        using var iterator = source.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        void Flush()
        {
            if (contour.Count >= 3) AppendRounded(result, contour, radius);
            else if (contour.Count > 0) { result.MoveTo(contour[0]); for (var i = 1; i < contour.Count; i++) result.LineTo(contour[i]); result.Close(); }
            contour.Clear();
        }
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: Flush(); contour.Add(points[0]); break;
                case SKPathVerb.Line: contour.Add(points[1]); break;
                case SKPathVerb.Quad: contour.Add(points[2]); break;
                case SKPathVerb.Conic: contour.Add(points[2]); break;
                case SKPathVerb.Cubic: contour.Add(points[3]); break;
                case SKPathVerb.Close: Flush(); break;
            }
        }
        Flush();
        return result;
    }

    private static void AppendRounded(SKPath path, List<SKPoint> contour, float radius)
    {
        // Drop a closing duplicate of the first point.
        if (contour.Count > 1 && contour[^1] == contour[0]) contour.RemoveAt(contour.Count - 1);
        var n = contour.Count;
        if (n < 3) return;
        for (var i = 0; i < n; i++)
        {
            var previous = contour[(i + n - 1) % n]; var current = contour[i]; var next = contour[(i + 1) % n];
            var inX = current.X - previous.X; var inY = current.Y - previous.Y; var inLength = MathF.Sqrt(inX * inX + inY * inY);
            var outX = next.X - current.X; var outY = next.Y - current.Y; var outLength = MathF.Sqrt(outX * outX + outY * outY);
            var step = MathF.Min(radius, MathF.Min(inLength, outLength) / 2);
            var start = inLength < 1e-6f ? current : new SKPoint(current.X - inX / inLength * step, current.Y - inY / inLength * step);
            var end = outLength < 1e-6f ? current : new SKPoint(current.X + outX / outLength * step, current.Y + outY / outLength * step);
            if (i == 0) path.MoveTo(start); else path.LineTo(start);
            path.QuadTo(current, end);
        }
        path.Close();
    }

    /// <summary>Bounded per-river cache of merged bodies for the last few view scales.</summary>
    private sealed class BodyCache
    {
        private readonly List<(double MinimumWidth, RiverGeometrySet Geometry, SKPath Body, double AverageHalfWidth)> _entries = [];

        public (RiverGeometrySet Geometry, SKPath Body, double AverageHalfWidth) Get(River river, double minimumWidth)
        {
            var key = Math.Round(minimumWidth, 3);
            foreach (var entry in _entries)
                if (entry.MinimumWidth == key) return (entry.Geometry, entry.Body, entry.AverageHalfWidth);
            var geometry = RiverGeometry.Evaluate(river, key);
            var body = BuildBody(geometry, river.Style);
            var average = geometry.Channels.Values.SelectMany(g => g.Samples).Average(s => s.HalfWidth);
            if (_entries.Count >= 4)
            {
                _entries[0].Body.Dispose();
                _entries.RemoveAt(0);
            }
            _entries.Add((key, geometry, body, average));
            return (geometry, body, average);
        }
    }
}
