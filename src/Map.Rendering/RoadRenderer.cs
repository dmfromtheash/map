using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Draws the roads of an object layer from the pure Core geometry. All branches of one road are
/// merged into a single body first - joints, flares and a rounded fill of every inner corner -
/// and only then does the drawing begin: the soft shoulder outside the body, the body, its tone,
/// the ruts along each branch, the edge of the whole outline and the small marks by it. Nothing
/// here writes back to the document; the same evaluation serves viewport, tiles and export.
/// </summary>
public static class RoadRenderer
{
    /// <summary>A road never drops below this on-screen width, so far zoom keeps it as a thread.</summary>
    public const double MinimumScreenWidth = 1.5;

    private static readonly ConditionalWeakTable<Road, BodyCache> Bodies = [];

    /// <summary>World-space reach needed beyond an export tile for the shoulder, patches and edge marks.</summary>
    public static double CoveragePadding(Road road)
    {
        ArgumentNullException.ThrowIfNull(road);
        var half = road.Branches.SelectMany(b => b.Vertices).Select(v => v.Width * road.WidthScale / 2).DefaultIfEmpty(0).Max();
        var reach = RoadGeometry.DecorReach(road.Style);
        foreach (var branch in road.Branches)
            if (branch.Style is { } own) reach = Math.Max(reach, RoadGeometry.DecorReach(own));
        return reach + half * 0.8 + 2;
    }

    /// <summary>Roads of the layer with live editor substitutions applied by id.</summary>
    public static IReadOnlyList<Road> Resolve(ObjectLayer layer, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RoadOverrides is not { Count: > 0 } overrides) return layer.Roads;
        var result = new List<Road>(layer.Roads.Length + 1);
        foreach (var road in layer.Roads)
            result.Add(overrides.FirstOrDefault(o => o.Id == road.Id) ?? road);
        if (options.RoadPreviewLayerId == layer.Id)
            foreach (var extra in overrides)
                if (layer.Roads.All(r => r.Id != extra.Id)) result.Add(extra);
        return result;
    }

    public static void DrawRoads(SKCanvas canvas, IReadOnlyList<Road> roads, WorldBounds view, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(roads);
        ArgumentNullException.ThrowIfNull(options);
        foreach (var road in roads)
        {
            if (!road.Visible || road.Style.Opacity <= 0) continue;
            if (!RoadGeometry.Evaluate(road).Bounds.Intersects(view)) continue;
            Draw(canvas, road, options.ViewScale, options.FastTerrain);
            if (options.Stats is { } stats) { stats.ObjectsDrawn++; stats.DrawCalls++; }
        }
    }

    /// <summary>One evaluated road: geometry, merged body, per-branch bodies and shoulders.</summary>
    private sealed record Entry(RoadGeometrySet Geometry, SKPath Body, Dictionary<Guid, SKPath> Branches,
        Dictionary<Guid, SKPath> Shoulders, double AverageHalf)
    {
        public Entry Copy() => new(Geometry, new SKPath(Body),
            Branches.ToDictionary(p => p.Key, p => new SKPath(p.Value)),
            Shoulders.ToDictionary(p => p.Key, p => new SKPath(p.Value)), AverageHalf);

        public void Dispose()
        {
            Body.Dispose();
            foreach (var path in Branches.Values) path.Dispose();
            foreach (var path in Shoulders.Values) path.Dispose();
        }
    }

    /// <summary>Draws one road at the given view scale. Public so the editor can preview a draft identically.</summary>
    public static void Draw(SKCanvas canvas, Road road, double viewScale, bool fastPreview)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(road);
        var scale = Math.Max(viewScale, 1e-4);
        var minimumWidth = MinimumScreenWidth / scale;
        // Copied out of the cache under its lock: another renderer may evict the entry while
        // this frame is drawing, exactly as with rivers.
        Entry entry;
        var cache = Bodies.GetValue(road, static _ => new BodyCache());
        lock (cache) entry = cache.Get(road, minimumWidth).Copy();
        try { DrawEntry(canvas, road, entry, scale, fastPreview); }
        finally { entry.Dispose(); }
    }

    private readonly record struct Group(RoadStyle Style, List<RoadBranchGeometry> Branches, double MaxHalf);

    private static void DrawEntry(SKCanvas canvas, Road road, Entry entry, double scale, bool fastPreview)
    {
        var set = entry.Geometry;
        var bounds = set.Bounds;
        var rect = new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);
        var tiny = entry.AverageHalf * scale < 2.2;
        var seed = road.Seed;

        // Branches grouped by their look. The road's own style is the base everything else lies
        // on; groups are then laid narrow to wide, so a path's buried end never shows on the road.
        var groups = new List<Group>();
        foreach (var geometry in set.Branches.Values)
        {
            var index = groups.FindIndex(g => g.Style == geometry.Style);
            var half = geometry.Samples.Length == 0 ? 0 : geometry.Samples.Max(s => s.HalfWidth);
            if (index < 0) groups.Add(new Group(geometry.Style, [geometry], half));
            else { groups[index].Branches.Add(geometry); groups[index] = groups[index] with { MaxHalf = Math.Max(groups[index].MaxHalf, half) }; }
        }
        groups.Sort((a, b) => a.MaxHalf.CompareTo(b.MaxHalf));
        var single = groups.Count == 1;

        var tapers = set.Branches.Values.Where(g => g.StartTaper > 0 || g.EndTaper > 0).ToList();
        // A free rounded end is where a road runs out into the grass: its rim and the marks by
        // it fade out around the end instead of drawing a closed capsule outline, and the very
        // tip of the ground thins into the grass. (-1: the start, going back; 1: the end.)
        var freeEnds = new List<(RoadSample Sample, int Direction)>();
        foreach (var geometry in set.Branches.Values)
        {
            if (geometry.Closed || geometry.Samples.Length < 2) continue;
            if (geometry.StartJoin is null && geometry.StartTaper <= 0 && geometry.Style.StartCap == RoadEndCap.Round) freeEnds.Add((geometry.Samples[0], -1));
            if (geometry.EndJoin is null && geometry.EndTaper <= 0 && geometry.Style.EndCap == RoadEndCap.Round) freeEnds.Add((geometry.Samples[^1], 1));
        }
        var fadeEnds = freeEnds.Count > 0 && !fastPreview && !tiny;
        var grouped = road.Style.Opacity < 1 || tapers.Count > 0 || fadeEnds;
        if (grouped)
        {
            using var group = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(road.Style.Opacity * 255), 0, 255)) };
            canvas.SaveLayer(SKRect.Inflate(rect, 4, 4), group);
        }
        else canvas.Save();

        using var unions = new DisposableList();
        SKPath GroupUnion(Group group)
        {
            var union = UnionOf(group.Branches.Select(g => entry.Branches[g.Branch.Id]).ToList());
            unions.Add(union);
            return union;
        }
        var groupUnions = groups.Select(GroupUnion).ToList();

        // The region a group's shared passes (shoulder, edge, marks) may touch when several
        // looks meet: around its own branches, not along the whole road.
        SKPath? Reach(int index)
        {
            if (single) return null;
            var style = groups[index].Style;
            var reach = new SKPath();
            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round,
                StrokeWidth = (float)(2 * (RoadGeometry.DecorReach(style) + 2)),
            };
            stroke.GetFillPath(groupUnions[index], reach);
            reach.AddPath(groupUnions[index]);
            unions.Add(reach);
            return reach;
        }
        var reaches = groups.Select((_, i) => Reach(i)).ToList();

        // 1. The shoulder: earth that the road wore into the grass, wider on one side than the
        // other, soft, and quiet under everything drawn after it.
        if (!tiny)
            for (var i = 0; i < groups.Count; i++)
            {
                var style = groups[i].Style;
                if (style.ShoulderWidth <= 0 || style.ShoulderOpacity <= 0 || style.ShoulderWidth * scale < 0.45) continue;
                using var shoulder = UnionOf(groups[i].Branches.Select(g => entry.Shoulders[g.Branch.Id]).ToList());
                canvas.Save();
                if (reaches[i] is { } reach) canvas.ClipPath(reach, SKClipOperation.Intersect, antialias: true);
                using var paint = new SKPaint
                {
                    Color = SceneRenderer.ParseColor(style.ShoulderColor).WithAlpha(Alpha(style.ShoulderOpacity)),
                    IsAntialias = true, Style = SKPaintStyle.Fill,
                };
                if (!fastPreview && style.ShoulderWidth * scale >= 1.2)
                    paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(style.ShoulderWidth * 0.5));
                canvas.DrawPath(shoulder, paint);
                canvas.Restore();
            }

        // 2. The body: the whole merged outline in the road's own tone, then each look over its
        // own branches, narrow first. The merged outline includes the rounded inner corners, so a
        // fork is one surface with no seam, no plug and no darker overlap.
        // With one look on every branch that look IS the surface, own branch style included: the
        // road's base colour must not show through a branch that was given its own colour.
        using (var body = new SKPaint { Color = SceneRenderer.ParseColor((single ? groups[0].Style : road.Style).SurfaceColor), IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawPath(entry.Body, body);
        if (!single)
        {
            canvas.Save();
            canvas.ClipPath(entry.Body, SKClipOperation.Intersect, antialias: true);
            for (var i = 0; i < groups.Count; i++)
            {
                // Every look in turn, narrow to wide - the road's own look included, so a wider
                // branch always lies over the buried end of a narrower one of another look. The
                // wider fills are feathered: inside the body their edge only meets another look
                // at a junction, and there the two materials blend instead of butting.
                var feather = i == 0 || tiny ? 0 : Math.Max(1, groups[i].MaxHalf * 0.3);
                using var fill = new SKPaint
                {
                    Color = SceneRenderer.ParseColor(groups[i].Style.SurfaceColor), IsAntialias = true, Style = SKPaintStyle.Fill,
                    MaskFilter = feather * scale >= 0.6 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)feather) : null,
                };
                canvas.DrawPath(groupUnions[i], fill);
            }
            canvas.Restore();
        }
        if (tiny) { FinishTapers(canvas, tapers, rect); canvas.Restore(); return; }

        // 3. Material, when the user chose one of Map's own ground materials for this look.
        if (!fastPreview)
            for (var i = 0; i < groups.Count; i++)
            {
                var style = groups[i].Style;
                if (style.TextureOpacity <= 0 || MapTextureLibrary.Image(style.TextureId) is null) continue;
                canvas.Save();
                canvas.ClipPath(single ? entry.Body : groupUnions[i], SKClipOperation.Intersect, antialias: true);
                MapTextureLibrary.DrawFill(canvas, style.TextureId, style.TextureScale, style.TextureOpacity, rect);
                canvas.Restore();
            }

        // 4. Tone: soft patches of a second earth colour, elongated along the way, so the ground
        // reads as trodden and the eye follows the direction of travel. Bound to the branch
        // distance and the seed, never to the screen.
        if (!fastPreview)
        {
            canvas.Save();
            canvas.ClipPath(entry.Body, SKClipOperation.Intersect, antialias: true);
            foreach (var geometry in set.Branches.Values) DrawTone(canvas, geometry, seed, scale);
            canvas.Restore();
        }

        // 5. The inner tonal step along the edge: in the painterly drawing it is the edge; in
        // the country drawing a quieter version of it makes the shoulder read as ground, not ink.
        if (!fastPreview)
            for (var i = 0; i < groups.Count; i++)
            {
                var style = groups[i].Style;
                var strength = style.Drawing switch
                {
                    RoadDrawing.Painterly => style.EdgeOpacity,
                    RoadDrawing.Country => style.EdgeOpacity * 0.35,
                    _ => 0,
                };
                if (strength <= 0.01) continue;
                var band = Math.Max(1.2, groups[i].MaxHalf * (style.Drawing == RoadDrawing.Painterly ? 0.55 : 0.32));
                if (band * scale < 0.8) continue;
                canvas.Save();
                canvas.ClipPath(single ? entry.Body : groupUnions[i], SKClipOperation.Intersect, antialias: true);
                if (reaches[i] is { } reach) canvas.ClipPath(reach, SKClipOperation.Intersect, antialias: true);
                canvas.SaveLayer(rect, null);
                using (var dark = new SKPaint { Color = SceneRenderer.ParseColor(style.EdgeColor).WithAlpha(Alpha(strength)), IsAntialias = true, Style = SKPaintStyle.Fill })
                    canvas.DrawPath(entry.Body, dark);
                using (var core = new SKPaint
                {
                    Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.DstOut,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(band * 0.75)),
                })
                    canvas.DrawPath(entry.Body, core);
                canvas.Restore();
                canvas.Restore();
            }

        // 6. Ruts: two broken tracks along each branch that thin out towards a junction and an
        // end, so nothing meets in a star at a fork and nothing runs onto the grass. A junction
        // quiets every branch through it: the one that passes the node as well as the one that
        // ends there, so the tracks of the main road do not run across the mouth of a fork.
        if (!fastPreview)
        {
            var junctions = Junctions(set);
            canvas.Save();
            canvas.ClipPath(entry.Body, SKClipOperation.Intersect, antialias: true);
            foreach (var geometry in set.Branches.Values) DrawRuts(canvas, geometry, seed, scale, junctions);
            canvas.Restore();
        }

        if (fadeEnds) canvas.SaveLayer(SKRect.Inflate(rect, 8, 8), null);

        // 7. The edge of the whole outline: a broken line whose weight changes, drawn once
        // around the union, so a fork has one continuous rim and no line across an entry.
        for (var i = 0; i < groups.Count; i++)
        {
            var style = groups[i].Style;
            if (style.EdgeWidth <= 0 || style.EdgeOpacity <= 0) continue;
            canvas.Save();
            if (reaches[i] is { } reach) canvas.ClipPath(reach, SKClipOperation.Intersect, antialias: true);
            DrawEdge(canvas, entry.Body, style, seed ^ (i * 7919), scale, fastPreview);
            canvas.Restore();
        }

        // 8. Small marks by the edge: pebbles by a country road, ticks by a pen road.
        if (!fastPreview)
            for (var i = 0; i < groups.Count; i++)
            {
                var style = groups[i].Style;
                if (style.DetailOpacity <= 0.01 || style.Drawing == RoadDrawing.Painterly) continue;
                canvas.Save();
                if (reaches[i] is { } reach) canvas.ClipPath(reach, SKClipOperation.Intersect, antialias: true);
                canvas.ClipPath(entry.Body, SKClipOperation.Difference, antialias: true);
                DrawMarks(canvas, entry.Body, style, seed ^ (i * 104729), scale, groups[i].MaxHalf);
                canvas.Restore();
            }
        if (fadeEnds)
        {
            foreach (var (end, _) in freeEnds)
            {
                var radius = (float)Math.Max(2, end.HalfWidth * 2.2 + 3);
                var centre = new SKPoint((float)end.X, (float)end.Y);
                using var shader = SKShader.CreateRadialGradient(centre, radius,
                    [SKColors.Black, SKColors.Black, SKColors.Transparent], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
                using var erase = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstOut, IsAntialias = true };
                canvas.DrawCircle(centre, radius, erase);
            }
            canvas.Restore();
            // The tip of the ground itself: a soft fade over the rounded cap, strongest beyond
            // the last sample, so the end reads as trodden earth giving way to grass.
            foreach (var (end, direction) in freeEnds)
            {
                var half = (float)Math.Max(1, end.HalfWidth + (end.HalfWidth > 0 ? 1 : 0));
                var tip = new SKPoint((float)(end.X + end.TangentX * direction * half * 1.1), (float)(end.Y + end.TangentY * direction * half * 1.1));
                var radius = half * 1.9f;
                using var shader = SKShader.CreateRadialGradient(tip, radius,
                    [SKColors.Black.WithAlpha(235), SKColors.Black.WithAlpha(120), SKColors.Transparent], [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
                using var erase = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstOut, IsAntialias = true };
                canvas.DrawCircle(tip, radius, erase);
            }
        }

        FinishTapers(canvas, tapers, rect);
        canvas.Restore();
    }

    /// <summary>A tapered end dissolves: the last part of the narrowing thread fades out.</summary>
    private static void FinishTapers(SKCanvas canvas, List<RoadBranchGeometry> tapers, SKRect rect)
    {
        foreach (var geometry in tapers)
        {
            if (geometry.StartTaper > 0) Fade(geometry.Samples[0], -1, geometry.StartTaper);
            if (geometry.EndTaper > 0) Fade(geometry.Samples[^1], 1, geometry.EndTaper);
        }
        void Fade(RoadSample end, int direction, double reach)
        {
            var span = (float)(reach * 0.55);
            var from = new SKPoint((float)(end.X - end.TangentX * direction * span), (float)(end.Y - end.TangentY * direction * span));
            var to = new SKPoint((float)end.X, (float)end.Y);
            using var shader = SKShader.CreateLinearGradient(from, to, [SKColors.Transparent, SKColors.Black], [0f, 1f], SKShaderTileMode.Clamp);
            using var erase = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstOut, IsAntialias = true };
            var radius = (float)(reach + end.HalfWidth * 6 + 8);
            var area = new SKRect(to.X - radius, to.Y - radius, to.X + radius, to.Y + radius);
            // Only the side beyond the fade start may be erased; the gradient is transparent before it.
            using var half = new SKPath();
            var nx = -end.TangentY * direction; var ny = end.TangentX * direction;
            half.MoveTo((float)(from.X + nx * radius), (float)(from.Y + ny * radius));
            half.LineTo((float)(from.X - nx * radius), (float)(from.Y - ny * radius));
            half.LineTo((float)(from.X - nx * radius + end.TangentX * direction * radius * 2), (float)(from.Y - ny * radius + end.TangentY * direction * radius * 2));
            half.LineTo((float)(from.X + nx * radius + end.TangentX * direction * radius * 2), (float)(from.Y + ny * radius + end.TangentY * direction * radius * 2));
            half.Close();
            if (!half.Bounds.IntersectsWith(rect)) return;
            canvas.DrawPath(half, erase);
        }
    }

    private static void DrawTone(SKCanvas canvas, RoadBranchGeometry geometry, int seed, double scale)
    {
        var style = geometry.Style;
        if (style.ToneVariation <= 0.01) return;
        var samples = geometry.Samples;
        if (samples.Length < 2) return;
        var averageHalf = samples.Average(s => s.HalfWidth);
        if (averageHalf * scale < 2) return;
        var branchSeed = unchecked(seed * 131 + geometry.Branch.Id.GetHashCode());
        var dark = SceneRenderer.ParseColor(style.ToneColor);
        var light = Lighten(SceneRenderer.ParseColor(style.SurfaceColor), style.Drawing == RoadDrawing.Painterly ? 0.16f : 0.10f);
        using var darkStrong = new SKPath(); using var darkSoft = new SKPath(); using var lightPath = new SKPath();
        var step = Math.Max(4, averageHalf * (style.Drawing == RoadDrawing.Painterly ? 1.6 : 2.3));
        var next = step * 0.4;
        var length = geometry.Length;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            if (s.Distance < next) continue;
            var n1 = RiverGeometry.Noise(branchSeed, s.Distance / step * 0.7);
            var n2 = RiverGeometry.Noise(branchSeed ^ 0x2545F491, s.Distance / step * 0.9 + 3.1);
            var n3 = RiverGeometry.Noise(branchSeed ^ 0x1B873593, s.Distance / step * 1.3 + 7.7);
            next = s.Distance + step * (0.75 + 0.5 * (0.5 + 0.5 * n3));
            var half = s.HalfWidth;
            if (half * scale < 1.5) continue;
            var rx = half * (0.9 + 0.7 * (0.5 + 0.5 * n1));
            var ry = half * (0.42 + 0.3 * (0.5 + 0.5 * n2));
            var across = half * 0.38 * n2;
            var cx = s.X - s.TangentY * across; var cy = s.Y + s.TangentX * across;
            var target = n1 > 0.25 ? lightPath : n1 < -0.15 ? darkStrong : darkSoft;
            using var blob = new SKPath();
            blob.AddOval(new SKRect((float)(cx - rx), (float)(cy - ry), (float)(cx + rx), (float)(cy + ry)));
            var angle = (float)(Math.Atan2(s.TangentY, s.TangentX) * 180 / Math.PI);
            blob.Transform(SKMatrix.CreateRotationDegrees(angle, (float)cx, (float)cy));
            target.AddPath(blob);
        }
        var sigma = (float)Math.Max(0.6, averageHalf * (style.Drawing == RoadDrawing.Painterly ? 0.5 : 0.38));
        var variation = style.ToneVariation;
        Paint(darkStrong, dark, variation * 0.42, sigma);
        Paint(darkSoft, dark, variation * 0.2, sigma);
        Paint(lightPath, light, variation * 0.55, sigma);
        void Paint(SKPath path, SKColor color, double alpha, float blur)
        {
            if (path.IsEmpty) return;
            using var paint = new SKPaint
            {
                Color = color.WithAlpha(Alpha(alpha)), IsAntialias = true, Style = SKPaintStyle.Fill,
                MaskFilter = blur * scale >= 0.6 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur) : null,
            };
            canvas.DrawPath(path, paint);
        }
        _ = length;
    }

    /// <summary>Every node where one branch joins another, with the reach over which tracks give way there.</summary>
    private static List<(double X, double Y, double Reach)> Junctions(RoadGeometrySet set)
    {
        var junctions = new List<(double X, double Y, double Reach)>();
        foreach (var geometry in set.Branches.Values)
        {
            if (geometry.Samples.Length == 0) continue;
            Add(geometry.StartJoin, geometry.Samples[0]);
            Add(geometry.EndJoin, geometry.Samples[^1]);
        }
        return junctions;
        void Add(RoadEndJoin? join, RoadSample end)
        {
            if (join is null || !set.Nodes.TryGetValue(join.NodeId, out var node)) return;
            junctions.Add((node.X, node.Y, Math.Max(join.OtherHalfWidth * 1.6, end.HalfWidth * 2.2)));
        }
    }

    private static void DrawRuts(SKCanvas canvas, RoadBranchGeometry geometry, int seed, double scale,
        List<(double X, double Y, double Reach)> junctions)
    {
        var style = geometry.Style;
        if (style.RutOpacity <= 0.01) return;
        var samples = geometry.Samples;
        if (samples.Length < 2) return;
        var averageHalf = samples.Average(s => s.HalfWidth);
        if (averageHalf * scale < 2.6) return;
        var branchSeed = unchecked(seed * 977 + geometry.Branch.Id.GetHashCode());
        var pen = style.Drawing == RoadDrawing.PenRuts;
        var length = geometry.Length;
        var width = Math.Clamp(averageHalf * (pen ? 0.11 : 0.13), 0.5 / scale, pen ? 1.6 : 2.4);
        var color = SceneRenderer.ParseColor(style.RutColor).WithAlpha(Alpha(style.RutOpacity));
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var second = new SKPaint { Color = color.WithAlpha((byte)(color.Alpha * 0.8)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(width * 0.85), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        var dash = averageHalf * (pen ? 3.2 : 2.2); var gap = averageHalf * (pen ? 1.7 : 2.6);
        // Where the tracks give way: near a joined end, over its flare and the other body; near a
        // free end, over the last stretch; near a taper, over the whole taper.
        double quietStart = 0, quietEnd = 0;
        if (!geometry.Closed)
        {
            quietStart = geometry.StartJoin is { } sj ? Math.Max(sj.OtherHalfWidth * 1.6, samples[0].HalfWidth * 2.2) : samples[0].HalfWidth * 1.4;
            quietEnd = geometry.EndJoin is { } ej ? Math.Max(ej.OtherHalfWidth * 1.6, samples[^1].HalfWidth * 2.2) : samples[^1].HalfWidth * 1.4;
            if (geometry.StartTaper > 0) quietStart = Math.Max(quietStart, geometry.StartTaper * 0.9);
            if (geometry.EndTaper > 0) quietEnd = Math.Max(quietEnd, geometry.EndTaper * 0.9);
        }
        // Junctions along the branch, as the stretches of its own length they cover.
        var nearJunction = new List<(double From, double To)>();
        foreach (var (x, y, reach) in junctions)
        {
            double? from = null, to = null;
            foreach (var s in samples)
            {
                var dx = s.X - x; var dy = s.Y - y;
                if (dx * dx + dy * dy > reach * reach) continue;
                from ??= s.Distance; to = s.Distance;
            }
            if (from is { } a && to is { } b) nearJunction.Add((a, b));
        }
        bool Quiet(double distance) => distance < quietStart || length - distance < quietEnd
            || nearJunction.Any(stretch => distance >= stretch.From && distance <= stretch.To);
        using var left = DashedOffsetPath(samples, 0.30, dash, gap, 0, branchSeed, Quiet, pen);
        using var right = DashedOffsetPath(samples, -0.30, dash, gap, dash * 0.45, branchSeed ^ 0x51ED, Quiet, pen);
        canvas.DrawPath(left, paint);
        canvas.DrawPath(right, second);
    }

    private static SKPath DashedOffsetPath(RoadSample[] samples, double offsetFraction, double dash, double gap, double phase, int seed,
        Func<double, bool> quiet, bool pen)
    {
        var path = new SKPath();
        var period = Math.Max(1e-6, dash + gap);
        var open = false;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            var position = (s.Distance + phase) % period;
            var lengthWobble = 0.5 + 0.8 * (0.5 + 0.5 * RiverGeometry.Noise(seed, (s.Distance + phase) / period));
            // A track is left out now and then, so no two stretches read the same and no track runs the whole way.
            var skip = RiverGeometry.Noise(seed ^ 0x77, (s.Distance + phase) / period + 11.3) > (pen ? 0.62 : 0.48);
            var on = position < dash * lengthWobble && !skip && !quiet(s.Distance);
            var nx = -s.TangentY; var ny = s.TangentX;
            var drift = 1 + 0.32 * RiverGeometry.Noise(seed ^ 0x2545, s.Distance / (period * 1.9));
            var offset = s.HalfWidth * offsetFraction * drift;
            var point = new SKPoint((float)(s.X + nx * offset), (float)(s.Y + ny * offset));
            if (on)
            {
                if (open) path.LineTo(point); else { path.MoveTo(point); open = true; }
            }
            else if (open) { path.LineTo(point); open = false; }
        }
        return path;
    }

    /// <summary>
    /// The rim around the merged body, sampled along its contours in world space: a line that
    /// breaks and changes weight by the seed, so no stretch is an even parallel outline.
    /// </summary>
    private static void DrawEdge(SKCanvas canvas, SKPath body, RoadStyle style, int seed, double scale, bool fastPreview)
    {
        var width = style.EdgeWidth;
        if (width * scale < 0.5) width = 0.5 / scale;
        var color = SceneRenderer.ParseColor(style.EdgeColor).WithAlpha(Alpha(style.EdgeOpacity));
        var pen = style.Drawing == RoadDrawing.PenRuts;
        using var ink = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var line = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var measure = new SKPathMeasure(body, false);
        var contour = 0;
        do
        {
            var length = measure.Length;
            if (length < 2) { contour++; continue; }
            var step = Math.Clamp(width * 1.2, 0.8, 3);
            var count = Math.Clamp((int)Math.Ceiling(length / step), 8, 16384);
            var contourSeed = unchecked(seed + contour * 7 + (int)Math.Round(length));
            var breakPeriod = pen ? 34.0 : 46.0;
            using var ribbon = new SKPath();
            using var stroke = new SKPath();
            var inner = new List<SKPoint>();
            var open = false;
            for (var i = 0; i <= count; i++)
            {
                var distance = length * Math.Min(i, count) / count;
                measure.GetPositionAndTangent((float)distance, out var p, out var tangent);
                var visibility = RiverGeometry.Noise(contourSeed, distance / breakPeriod) * 0.5 + 0.5
                    + 0.25 * RiverGeometry.Noise(contourSeed ^ 0x3C6E, distance / (breakPeriod * 0.37));
                var visible = i < count && (fastPreview || visibility > style.EdgeBreak * 0.95);
                if (pen)
                {
                    // A pen line: weight follows a slow pressure curve and lifts entirely in the
                    // gaps. The swing is modest and does not follow the direction of the stroke:
                    // a direction term made every horizontal stretch of a road heavier than its
                    // other side, and the wide swing read as torn chunks of ink.
                    var pressure = 0.78 + 0.3 * (0.5 + 0.5 * RiverGeometry.Noise(contourSeed ^ 0x9E37, distance / 27.0));
                    var w = (float)(width * pressure);
                    var normal = new SKPoint(-tangent.Y, tangent.X);
                    if (visible)
                    {
                        var outer = new SKPoint(p.X + normal.X * w, p.Y + normal.Y * w);
                        if (!open) { ribbon.MoveTo(outer); open = true; inner.Clear(); }
                        else ribbon.LineTo(outer);
                        inner.Add(new SKPoint(p.X - normal.X * w, p.Y - normal.Y * w));
                    }
                    else if (open)
                    {
                        for (var k = inner.Count - 1; k >= 0; k--) ribbon.LineTo(inner[k]);
                        ribbon.Close(); open = false;
                    }
                }
                else
                {
                    if (visible) { if (open) stroke.LineTo(p); else { stroke.MoveTo(p); open = true; } }
                    else if (open) { stroke.LineTo(p); open = false; }
                }
            }
            if (pen)
            {
                if (open) { for (var k = inner.Count - 1; k >= 0; k--) ribbon.LineTo(inner[k]); ribbon.Close(); }
                canvas.DrawPath(ribbon, ink);
            }
            else canvas.DrawPath(stroke, line);
            contour++;
        } while (measure.NextContour());
    }

    /// <summary>
    /// Stones beside a country road, short ticks beside a pen road; sparse, outside the body only.
    /// A stone is a small light oval with a dark rim, sized from the road's width, lying along the
    /// way - a trodden path collects stones of about a sixth of its width at its edges.
    /// </summary>
    private static void DrawMarks(SKCanvas canvas, SKPath body, RoadStyle style, int seed, double scale, double halfWidth)
    {
        if (scale < 0.35) return;
        var color = SceneRenderer.ParseColor(style.EdgeColor).WithAlpha(Alpha(style.DetailOpacity));
        var pen = style.Drawing == RoadDrawing.PenRuts;
        var stoneSize = Math.Clamp(halfWidth * 0.42, 0.8, 7);
        using var stone = new SKPaint { Color = new SKColor(0xB9, 0xB4, 0xA8).WithAlpha(Alpha(style.DetailOpacity)), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var rim = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)Math.Max(0.45 / scale, Math.Min(style.EdgeWidth, stoneSize * 0.4)) };
        using var tick = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(0.5 / scale, style.EdgeWidth * 0.7), StrokeCap = SKStrokeCap.Round };
        using var measure = new SKPathMeasure(body, false);
        var contour = 0;
        do
        {
            var length = measure.Length;
            var contourSeed = unchecked(seed + contour * 13 + (int)Math.Round(length));
            var spacing = pen ? 9.0 : Math.Max(7.0, stoneSize * 4.5);
            var next = spacing * 0.5;
            while (next < length)
            {
                var distance = next;
                var n = RiverGeometry.Noise(contourSeed, distance / spacing);
                var n2 = RiverGeometry.Noise(contourSeed ^ 0x51ED, distance / (spacing * 2.7) + 5.5);
                next = distance + spacing * (0.7 + 0.9 * (0.5 + 0.5 * n));
                // Groups of marks with quiet stretches between them, never an even row.
                if (n2 < (pen ? 0.42 : 0.3)) continue;
                measure.GetPositionAndTangent((float)distance, out var p, out var tangent);
                var normal = new SKPoint(-tangent.Y, tangent.X);
                if (body.Contains(p.X + normal.X * 0.8f, p.Y + normal.Y * 0.8f)) normal = new SKPoint(-normal.X, -normal.Y);
                if (pen)
                {
                    // A blade of grass leaning along the way, not a leg across it: the stroke
                    // mostly follows the edge and only leaves it a little.
                    var lean = (float)(1.5 + 0.9 * (0.5 + 0.5 * n)) * (n2 > 0.7 ? -1 : 1);
                    var reach = (float)(1.3 + 1.1 * (0.5 + 0.5 * n2));
                    var norm = MathF.Sqrt(1 + lean * lean);
                    var dir = new SKPoint((normal.X + tangent.X * lean) / norm, (normal.Y + tangent.Y * lean) / norm);
                    var start = new SKPoint(p.X + normal.X * 0.7f, p.Y + normal.Y * 0.7f);
                    canvas.DrawLine(start, new SKPoint(start.X + dir.X * reach, start.Y + dir.Y * reach), tick);
                }
                else
                {
                    var rx = (float)(stoneSize * (0.6 + 0.7 * (0.5 + 0.5 * n)));
                    var ry = rx * (float)(0.55 + 0.25 * (0.5 + 0.5 * n2));
                    var away = rx * (float)(0.3 + 1.6 * (0.5 + 0.5 * n2));
                    var angle = MathF.Atan2(tangent.Y, tangent.X) * 180 / MathF.PI + (float)(25 * n);
                    canvas.Save();
                    canvas.Translate(p.X + normal.X * away, p.Y + normal.Y * away);
                    canvas.RotateDegrees(angle);
                    var oval = new SKRect(-rx, -ry, rx, ry);
                    canvas.DrawOval(oval, stone);
                    canvas.DrawOval(oval, rim);
                    canvas.Restore();
                }
            }
            contour++;
        } while (measure.NextContour());
    }

    private static SKColor Lighten(SKColor color, float amount) => new(
        (byte)Math.Clamp(color.Red + (255 - color.Red) * amount, 0, 255),
        (byte)Math.Clamp(color.Green + (255 - color.Green) * amount, 0, 255),
        (byte)Math.Clamp(color.Blue + (255 - color.Blue) * amount, 0, 255), color.Alpha);

    private static byte Alpha(double opacity) => (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);

    private static SKPath UnionOf(IReadOnlyList<SKPath> parts)
    {
        if (parts.Count == 1) return new SKPath(parts[0]);
        var result = new SKPath(parts[0]);
        for (var i = 1; i < parts.Count; i++)
        {
            var merged = new SKPath();
            if (result.Op(parts[i], SKPathOp.Union, merged)) { result.Dispose(); result = merged; }
            else { merged.Dispose(); result.AddPath(parts[i]); }
        }
        return result;
    }

    /// <summary>The body of one branch: its outline plus a round joint at every vertex, resolved to one clean contour.</summary>
    public static SKPath BuildBranchBody(RoadBranchGeometry geometry, int seed, Func<RoadSample, int, double>? extra = null)
    {
        var outline = RoadGeometry.Outline(geometry, seed, extra);
        using var raw = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var contour in outline.Contours)
        {
            if (contour.Count < 3) continue;
            raw.MoveTo((float)contour[0].X, (float)contour[0].Y);
            for (var i = 1; i < contour.Count; i++) raw.LineTo((float)contour[i].X, (float)contour[i].Y);
            raw.Close();
        }
        foreach (var (centre, radius) in outline.Joints)
        {
            // The joint turns the same way as the contour, so it adds instead of cancelling.
            const int steps = 20;
            for (var i = 0; i < steps; i++)
            {
                var angle = Math.PI * 2 * i / steps;
                var x = (float)(centre.X + Math.Cos(angle) * radius); var y = (float)(centre.Y + Math.Sin(angle) * radius);
                if (i == 0) raw.MoveTo(x, y); else raw.LineTo(x, y);
            }
            raw.Close();
        }
        var body = new SKPath();
        if (!raw.Simplify(body)) { body.Dispose(); body = new SKPath(raw); }
        return body;
    }

    /// <summary>
    /// Joins the fillet wedges of every fork and sharp bend to the merged outline, the way
    /// trodden ground spreads into the angle of a fork. The wedges are pure geometry from Core;
    /// only unions are used here, and a failed union keeps the outline it had. (A grow-and-shrink
    /// closing through stroke paths was tried first and misbehaved in about one road in eight.)
    /// </summary>
    public static SKPath JoinFillets(SKPath body, RoadGeometrySet set)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(set);
        var result = new SKPath(body);
        foreach (var wedge in RoadGeometry.Fillets(set))
        {
            if (wedge.Count < 3) continue;
            using var polygon = new SKPath { FillType = SKPathFillType.Winding };
            polygon.MoveTo((float)wedge[0].X, (float)wedge[0].Y);
            for (var i = 1; i < wedge.Count; i++) polygon.LineTo((float)wedge[i].X, (float)wedge[i].Y);
            polygon.Close();
            var merged = new SKPath();
            if (result.Op(polygon, SKPathOp.Union, merged)) { result.Dispose(); result = merged; }
            else merged.Dispose();
        }
        return result;
    }

    private static Entry BuildEntry(Road road, RoadGeometrySet set)
    {
        var branches = new Dictionary<Guid, SKPath>();
        var shoulders = new Dictionary<Guid, SKPath>();
        foreach (var geometry in set.Branches.Values)
        {
            branches[geometry.Branch.Id] = BuildBranchBody(geometry, road.Seed);
            var style = geometry.Style;
            var shoulderSeed = unchecked(road.Seed * 17 + geometry.Branch.Id.GetHashCode());
            shoulders[geometry.Branch.Id] = style.ShoulderWidth <= 0
                ? new SKPath()
                : BuildBranchBody(geometry, road.Seed, (s, side) =>
                {
                    // One side of the shoulder wider than the other, slowly changing: worn ground, not a tube.
                    var period = Math.Max(26, s.HalfWidth * 5);
                    var n = RiverGeometry.Noise(shoulderSeed ^ (side > 0 ? 0x5F3759DF : 0), s.Distance / period + (side > 0 ? 17.3 : 0));
                    return style.ShoulderWidth * (0.55 + 0.6 * (0.5 + 0.5 * n));
                });
        }
        var flowing = set.Branches.Values.SelectMany(g => g.Samples).Where(s => s.HalfWidth > 0).ToList();
        var average = flowing.Count > 0 ? flowing.Average(s => s.HalfWidth) : 0;
        using var union = UnionOf(branches.Values.ToList());
        var body = JoinFillets(union, set);
        return new Entry(set, body, branches, shoulders, average);
    }

    private sealed class DisposableList : IDisposable
    {
        private readonly List<SKPath> _paths = [];
        public void Add(SKPath path) => _paths.Add(path);
        public void Dispose() { foreach (var path in _paths) path.Dispose(); }
    }

    /// <summary>Bounded per-road cache of merged bodies for the last few view scales.</summary>
    private sealed class BodyCache
    {
        private readonly List<(double MinimumWidth, Entry Entry)> _entries = [];

        public Entry Get(Road road, double minimumWidth)
        {
            var key = Math.Round(minimumWidth, 3);
            foreach (var entry in _entries)
                if (entry.MinimumWidth == key) return entry.Entry;
            var geometry = RoadGeometry.Evaluate(road, key);
            var built = BuildEntry(road, geometry);
            if (_entries.Count >= 4) { _entries[0].Entry.Dispose(); _entries.RemoveAt(0); }
            _entries.Add((key, built));
            return built;
        }
    }
}
