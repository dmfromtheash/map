using System.Runtime.CompilerServices;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Draws the rivers of an object layer from the pure Core geometry. All channels of one river
/// are merged into a single outline with Skia path ops, so a tributary mouth has no seam, no
/// double opacity and one continuous bank line. Rivers of the layer that share one look are
/// drawn as one body of water too: where two systems cross, or a creek of one system reaches
/// the lake of another, the bank, the halo and the shallows follow the outline of the union,
/// never the outline of each part. The same evaluation serves the viewport, tiles and
/// single-image export; nothing here writes back to the document.
/// </summary>
public static class RiverRenderer
{
    /// <summary>A river never drops below this on-screen width, so far zoom keeps it as a thread.</summary>
    public const double MinimumScreenWidth = 1.5;

    private static readonly ConditionalWeakTable<River, BodyCache> Bodies = [];

    /// <summary>World-space coverage needed beyond an export tile for coast joins and bank fades.</summary>
    public static double CoastCoveragePadding(River river)
    {
        var half = river.Channels.SelectMany(c => c.Nodes).Select(n => n.Width * river.WidthScale / 2).DefaultIfEmpty(0).Max();
        var padding = river.Style.CutsLand ? 24d : 0; // Three sigmas of the bounded mouth rounding.
        if (river.Style.ClipToLand)
            padding = Math.Max(padding, Math.Max(Math.Clamp(half * 2.2, 3, 80) * 1.85,
                Math.Clamp(half * 2.4, 8, 48) * 1.9));
        return padding;
    }

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

    /// <param name="neighbours">
    /// Visible rivers of the other object layers. They are not drawn here, but a river of this
    /// layer that touches one of them shares its outline with it, so water on two layers still
    /// reads as one body where it meets: no bank line across the join, one halo, one rim.
    /// </param>
    public static void DrawLayerRivers(SKCanvas canvas, ObjectLayer layer, WorldBounds view, RenderOptions options,
        Action<SKCanvas, SKPaint>? drawLandCoverage = null, IReadOnlyList<River>? neighbours = null)
    {
        if (layer.Rivers.IsEmpty && options.RiverOverrides is not { Count: > 0 }) return;
        DrawRivers(canvas, Resolve(layer, options), view, options, drawLandCoverage, neighbours);
    }

    /// <summary>
    /// Draws the given rivers - normally every river of one terrain level, whatever layer each
    /// lives on - as one set: rivers sharing a look share one outline.
    /// </summary>
    public static void DrawRivers(SKCanvas canvas, IReadOnlyList<River> rivers, WorldBounds view, RenderOptions options,
        Action<SKCanvas, SKPaint>? drawLandCoverage = null, IReadOnlyList<River>? neighbours = null)
    {
        ArgumentNullException.ThrowIfNull(rivers);
        var visible = new List<River>();
        foreach (var river in rivers)
        {
            if (!river.Visible || river.Style.Opacity <= 0) continue;
            if (!RiverGeometry.Evaluate(river).Bounds.Intersects(view)) continue;
            var reach = CoastCoveragePadding(river) + river.Style.BankWidth + river.Style.BankShadowBlur * 4
                + river.Style.BankReliefWidth * 2 + 4 / Math.Max(.01,options.ViewScale);
            if (!BodyTouches(river,new WorldBounds(view.Left-reach,view.Top-reach,view.Right+reach,view.Bottom+reach),options.ViewScale)) continue;
            visible.Add(river);
        }
        if (visible.Count == 0) return;
        var around = new List<River>();
        if (neighbours is { Count: > 0 })
            foreach (var river in neighbours)
                if (river.Visible && RiverGeometry.Evaluate(river).Bounds.Intersects(view)) around.Add(river);

        // One body of water per look: rivers whose bank, halo and shallows are the same are
        // drawn together, so their union has one outline. Water colour, material and highlights
        // stay per river inside that shared outline. Order is kept, so a later river still covers
        // an earlier one where both are opaque - exactly as before for rivers that do not touch.
        var groups = new List<(RiverStyle Key, List<River> Rivers)>();
        foreach (var river in visible)
        {
            var key = EdgeKey(river.Style);
            // Merge adjacent looks only. A/B/A must retain its stack order at crossings.
            if (groups.Count == 0 || groups[^1].Key != key) groups.Add((key, [river]));
            else groups[^1].Rivers.Add(river);
        }
        foreach (var (key, members) in groups)
        {
            var context = around.Where(river => EdgeKey(river.Style) == key).ToList();
            DrawGroup(canvas, members, context, options.ViewScale, options.FastTerrain, key.ClipToLand ? drawLandCoverage : null, options.Stats);
            if (options.Stats is { } stats) { stats.ObjectsDrawn += members.Count; stats.DrawCalls++; }
        }
    }

    /// <summary>
    /// The part of a style that shapes the shared outline. Two rivers whose keys are equal are
    /// one body of water; what is reset here is drawn per river inside it.
    /// </summary>
    private static RiverStyle EdgeKey(RiverStyle style) => style with
    {
        WaterColor = "", TextureId = null, TextureOpacity = 0, TextureScale = 1,
        HighlightColor = "", HighlightOpacity = 0,
    };

    /// <summary>Erases the merged body of a river from whatever the canvas holds (land coverage carving).</summary>
    public static void CutBody(SKCanvas canvas, River river, double viewScale, SKBlendMode blend = SKBlendMode.DstOut)
    {
        var scale = Math.Max(viewScale, 1e-4);
        var cache = Bodies.GetValue(river, static _ => new BodyCache());
        lock (cache)
        {
            var entry = cache.Get(river, MinimumScreenWidth / scale);
            using var erase = new SKPaint { BlendMode = blend, IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(entry.Body, erase);
            // A hair of extra clearance keeps the coast outline from peeking under the bank stroke.
            using var edge = new SKPaint { BlendMode = blend, IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)Math.Max(river.Style.BankWidth * 0.5, 0.6 / scale), StrokeJoin = SKStrokeJoin.Round };
            canvas.DrawPath(entry.Body, edge);
        }
    }

    /// <summary>Conservative exact-body query: a winding river's large bounding box can contain
    /// a completely dry viewport. Callers include their full effect reach in the rectangle.</summary>
    internal static bool BodyTouches(River river, WorldBounds view, double viewScale)
    {
        var cache = Bodies.GetValue(river, static _ => new BodyCache());
        lock (cache)
        {
            var entry = cache.Get(river, MinimumScreenWidth / Math.Max(viewScale, 1e-4));
            using var rectangle = new SKPath();
            rectangle.AddRect(new SKRect((float)view.Left,(float)view.Top,(float)view.Right,(float)view.Bottom));
            using var intersection = new SKPath();
            // Failed path operations must draw normally, never omit water or a bank.
            return !entry.Body.Op(rectangle,SKPathOp.Intersect,intersection) || !intersection.IsEmpty;
        }
    }

    /// <summary>Draws one river at the given view scale. Public so the editor can preview a draft identically.</summary>
    public static void Draw(SKCanvas canvas, River river, double viewScale, bool fastPreview, Action<SKCanvas, SKPaint>? clipToLand = null) =>
        DrawGroup(canvas, [river], [], viewScale, fastPreview, clipToLand);

    /// <summary>One evaluated river of a group: its geometry, merged body and the width the shallows follow.</summary>
    private readonly record struct Part(River River, RiverGeometrySet Geometry, SKPath Body, double AverageHalfWidth, double WidthKey);

    /// <summary>
    /// Draws rivers that share one look as one body of water. The order of the passes is the
    /// order of a drawn map: the soft dark halo the ink leaves on the shore, the water itself,
    /// its material, the light shallows along the outline, the flow strokes, and last the bank
    /// line - each of the shared passes on the union, so no seam can exist where two parts meet.
    /// </summary>
    private static void DrawGroup(SKCanvas canvas, IReadOnlyList<River> rivers, IReadOnlyList<River> context,
        double viewScale, bool fastPreview, Action<SKCanvas, SKPaint>? clipToLand, RenderStats? stats = null)
    {
        if (rivers.Count == 0) return;
        var scale = Math.Max(viewScale, 1e-4);
        var minimumWidth = MinimumScreenWidth / scale;
        var style = rivers[0].Style;

        // Every body is taken under its own cache lock and released before drawing starts, so
        // two rivers of one group never hold two locks at once. The path is COPIED out of the
        // cache: the cache disposes its oldest entry when a fifth zoom level arrives, and another
        // renderer (export, thumbnail) may do that while this frame is still drawing. Drawing the
        // cached instance outside the lock crashed the process inside Skia (access violation in
        // ClipPath) when the owner zoomed; a private copy cannot be disposed under this frame.
        var parts = new List<Part>(rivers.Count);
        var others = new List<Part>(context.Count);
        try
        {
            foreach (var river in rivers) parts.Add(Take(river, minimumWidth));
            foreach (var river in context) others.Add(Take(river, minimumWidth));
            DrawParts(canvas, parts, others, style, scale, fastPreview, clipToLand, stats);
        }
        finally
        {
            foreach (var part in parts) part.Body.Dispose();
            foreach (var part in others) part.Body.Dispose();
        }

        static Part Take(River river, double minimumWidth)
        {
            var cache = Bodies.GetValue(river, static _ => new BodyCache());
            lock (cache)
            {
                var entry = cache.Get(river, minimumWidth);
                return new Part(river, entry.Geometry, new SKPath(entry.Body), entry.AverageHalfWidth, entry.WidthKey);
            }
        }
    }

    /// <param name="parts">The rivers this pass paints.</param>
    /// <param name="context">
    /// Rivers of other layers that share the look: they take part in the outline and in every
    /// seam decision, but their water is painted by their own layer.
    /// </param>
    private static void DrawParts(SKCanvas canvas, List<Part> parts, List<Part> context, RiverStyle style, double scale,
        bool fastPreview, Action<SKCanvas, SKPaint>? clipToLand, RenderStats? stats = null)
    {
        var passStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        void Pass(int index)
        {
            if (stats is not null) stats.RiverPassMilliseconds[index] += System.Diagnostics.Stopwatch.GetElapsedTime(passStarted).TotalMilliseconds;
            passStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        var all = context.Count == 0 ? parts : [.. parts, .. context];
        using var union = UnionOf(all);
        // The bodies were evaluated at these widths: zero for a river the on-screen floor never
        // touched, so its bank geometry is shared by every zoom level that leaves it unclamped.
        var bankKey = new RiverBankRenderer.BankKey([.. all.Select(p => p.River)], all.Max(p => p.WidthKey), style);
        using var ownUnion = context.Count == 0 ? new SKPath(union) : UnionOf(parts);
        var bounds = parts[0].Geometry.Bounds;
        foreach (var part in parts.Skip(1)) bounds = Union(bounds, part.Geometry.Bounds);
        var rect = new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom);
        var fadeChannels = style.MouthFade > 0
            ? parts.SelectMany(p => p.Geometry.Channels.Values).Where(g => !g.JoinsParent && !g.Closed).ToArray()
            : [];

        // The shallows follow the width of the water: a lake or a wide river lightens along its
        // shore, a creek narrower than the band simply reads a little lighter as a whole. The
        // rim is a soft gradient rather than a stroke, so it needs no seam handling at a
        // confluence and never turns a thin channel into a light tube with dark edges.
        var flowing = parts.Where(p => p.AverageHalfWidth > 0).Select(p => p.AverageHalfWidth).ToList();
        var typicalHalf = flowing.Count > 0 ? flowing.Average() : 0;
        var paintedWater = style.TextureId == "watercolour-water";
        var shallow = style.ShallowWidth * (paintedWater ? 1 : 1 + 1 / Math.Max(1, scale * 8));
        if (typicalHalf > 0) shallow = Math.Min(shallow, typicalHalf * 0.55);
        var hasShallow = !fastPreview && style.ShallowWidth > 0 && shallow * scale >= 0.75;

        // A river meets the sea along the coast itself, the way a drawn atlas has it: its water
        // ends on the shore line with only a narrow soft edge. Running on beyond it for its own
        // width left a straight-sided tongue in the sea - pale with its material and shallows,
        // or dark with its own colour - that no coast has.
        var feather = clipToLand is not null ? Math.Clamp(typicalHalf * 0.15, 1.5, 6) : 0;
        var layerRect = feather > 0 ? SKRect.Inflate(rect, (float)(feather * 2), (float)(feather * 2)) : rect;

        var grouped = style.Opacity < 1 || fadeChannels.Length > 0 || clipToLand is not null;
        if (grouped)
        {
            using var group = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(style.Opacity * 255), 0, 255)) };
            canvas.SaveLayer(layerRect, group);
        }
        else canvas.Save();

        var bank = style.BankWidth;
        if (bank > 0 && bank * scale < 0.6) bank = 0.6 / scale;

        // With neighbours in the outline, the shared passes stay near this layer's own water:
        // the neighbour's own layer already drew its halo and bank along the rest of it.
        using var nearby = new SKPath();
        if (context.Count > 0)
        {
            using var reach = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round,
                StrokeWidth = (float)(2 * (bank + style.BankShadowBlur * 3 + style.BankReliefWidth * 1.8 + 2)),
            };
            reach.GetFillPath(ownUnion, nearby);
            nearby.AddPath(ownUnion);
        }
        void EnterNearby() { canvas.Save(); if (context.Count > 0) canvas.ClipPath(nearby, SKClipOperation.Intersect, antialias: true); }

        void BeginBanks() { if (clipToLand is not null) canvas.SaveLayer(layerRect, null); }
        void EndBanks()
        {
            if (clipToLand is null) return;
            RiverCoastRenderer.MaskBanks(canvas, clipToLand, layerRect);
            canvas.Restore();
        }

        BeginBanks();
        var haloVisible = style.BankShadowOpacity > 0 && !fastPreview && style.BankShadowBlur * scale >= 0.5;
        if (haloVisible)
        {
            EnterNearby();
            using var halo = new SKPaint
            {
                Color = SceneRenderer.ParseColor(style.BankColor).WithAlpha((byte)Math.Clamp(Math.Round(style.BankShadowOpacity * 255), 0, 255)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(bank + style.BankShadowBlur),
                StrokeJoin = SKStrokeJoin.Round,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)style.BankShadowBlur),
            };
            canvas.DrawPath(union, halo);
            canvas.Restore();
        }
        Pass(0);

        if (!fastPreview && style.BankReliefWidth > 0)
        {
            EnterNearby();
            RiverBankRenderer.Draw(canvas, union, style, scale, bankKey);
            canvas.Restore();
        }

        EndBanks();
        Pass(1);

        // Only water continues beyond the coast. Banks above and below this pass receive
        // the original land alpha once, so a soft edge cannot shorten or harden their ends.
        if (clipToLand is not null) canvas.SaveLayer(layerRect, null);
        foreach (var part in parts)
        {
            using var water = new SKPaint { Color = SceneRenderer.ParseColor(part.River.Style.WaterColor), IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawPath(part.Body, water);
        }

        // Where one body of water lies over another, its own edge must not show as a seam: the
        // fill above is opaque, but antialiasing leaves a hair of the lower water's edge. One
        // pass of the union in the leading water colour, clipped to the union, closes it without
        // changing a single pixel inside a river that touches nothing.
        if (all.Count > 1)
        {
            canvas.Save();
            canvas.ClipPath(union, SKClipOperation.Intersect, antialias: true);
            foreach (var part in parts)
            {
                using var seam = new SKPaint
                {
                    Color = SceneRenderer.ParseColor(part.River.Style.WaterColor), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(1.5 / scale, bank), StrokeJoin = SKStrokeJoin.Round,
                };
                foreach (var other in all)
                {
                    if (ReferenceEquals(other.River, part.River)) continue;
                    canvas.Save();
                    canvas.ClipPath(other.Body, SKClipOperation.Intersect, antialias: true);
                    canvas.DrawPath(part.Body, seam);
                    canvas.Restore();
                }
            }
            canvas.Restore();
        }

        Pass(2);
        if (!fastPreview)
        {
            // The material is laid once over every part that shares it, inside their common
            // outline: laid per part, the overlap of a creek ending inside a lake would carry
            // the grain twice and show as a lighter patch in the lake.
            var materials = parts
                .Where(p => p.River.Style.TextureOpacity > 0 && MapTextureLibrary.Image(p.River.Style.TextureId) is not null)
                .GroupBy(p => (p.River.Style.TextureId, p.River.Style.TextureScale, p.River.Style.TextureOpacity));
            foreach (var material in materials)
            {
                var members = material.ToList();
                using var area = members.Count == parts.Count ? new SKPath(union) : UnionOf(members);
                var (id, textureScale, opacity) = material.Key;
                canvas.Save();
                canvas.ClipPath(area, SKClipOperation.Intersect, antialias: true);
                MapTextureLibrary.DrawFill(canvas, id, textureScale, opacity, rect);
                canvas.Restore();
            }
        }

        Pass(3);
        if (clipToLand is not null)
        {
            // Grow and blur the composed coverage, never individual tiles: the wash stays
            // continuous at tile boundaries and applies only to the water fill/material.
            using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
            canvas.SaveLayer(layerRect, keep);
            using var dilate = SKImageFilter.CreateDilate((float)(feather * 0.5), (float)(feather * 0.5));
            using var soften = SKImageFilter.CreateBlur((float)(feather * 0.45), (float)(feather * 0.45), dilate);
            using var wash = new SKPaint { ImageFilter = soften };
            canvas.SaveLayer(layerRect, wash);
            using var plain = new SKPaint();
            clipToLand(canvas, plain);
            canvas.Restore();
            canvas.Restore();
            canvas.Restore();
        }

        Pass(4);
        BeginBanks();
        if (hasShallow && paintedWater)
        {
            canvas.Save();
            canvas.ClipPath(ownUnion, SKClipOperation.Intersect, antialias: true);
            RiverBankRenderer.DrawPaintedShallows(canvas, union, style, shallow, bankKey);
            canvas.Restore();
        }
        else if (hasShallow)
        {
            // The rim is the light colour everywhere the water is, minus a blurred copy of the
            // body: full in the middle, half at the shore, so the light stays along the outline
            // and fades inward over about the band's width.
            // Clipped to this layer's own water: the neighbour's rim belongs to the neighbour.
            canvas.Save();
            canvas.ClipPath(ownUnion, SKClipOperation.Intersect, antialias: true);
            canvas.SaveLayer(rect, null);
            using (var light = new SKPaint { Color = SceneRenderer.ParseColor(style.ShallowColor), IsAntialias = true, Style = SKPaintStyle.Fill })
                canvas.DrawPath(union, light);
            // The core that stays deep is the water drawn half a band in from the shore, then
            // softened: the shore itself carries the full light tone and it fades into the deep
            // water over about the band's width, like the foam of a painted river. A core that
            // reached the shore left only half the tone there, so the rim barely showed.
            // The inset is a raster erosion of the drawn water: a path difference with the stroked
            // shore failed on complex outlines and left whole reaches without a deep core.
            using (var erode = SKImageFilter.CreateErode((float)(shallow * 0.5), (float)(shallow * 0.5)))
            using (var soften = SKImageFilter.CreateBlur((float)Math.Max(0.35, shallow * 0.5), (float)Math.Max(0.35, shallow * 0.5), erode))
            using (var core = new SKPaint { BlendMode = SKBlendMode.DstOut, ImageFilter = soften })
            {
                canvas.SaveLayer(rect, core);
                using (var black = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill })
                    canvas.DrawPath(union, black);
                canvas.Restore();
            }
            canvas.Restore();
            canvas.Restore();
        }

        Pass(5);
        if (!fastPreview && style.BankReliefWidth > 0)
        {
            canvas.Save();
            canvas.ClipPath(ownUnion, SKClipOperation.Intersect, antialias: true);
            RiverBankRenderer.DrawWaterLines(canvas, union, style, scale, bankKey);
            canvas.Restore();
        }
        foreach (var part in parts)
        {
            if (part.River.Style.HighlightOpacity <= 0 || style.BankReliefWidth > 0) continue;
            // Flow strokes belong to the channel that flows: where its end lies inside another
            // body of water they would cross that water, so they stop at its edge.
            canvas.Save();
            foreach (var other in all)
                if (!ReferenceEquals(other.River, part.River))
                    canvas.ClipPath(other.Body, SKClipOperation.Difference, antialias: true);
            DrawHighlights(canvas, part.Geometry, part.River.Style, part.Body, scale);
            canvas.Restore();
        }

        Pass(6);
        if (bank > 0)
        {
            EnterNearby();
            if (!fastPreview && style.BankReliefWidth > 0)
            {
                RiverBankRenderer.DrawInkOutline(canvas, union, style);
            }
            else
            {
                using var edge = new SKPaint
                {
                    Color = SceneRenderer.ParseColor(style.BankColor), IsAntialias = true,
                    Style = SKPaintStyle.Stroke, StrokeWidth = (float)bank, StrokeJoin = SKStrokeJoin.Round,
                };
                canvas.DrawPath(union, edge);
            }
            canvas.Restore();
        }

        EndBanks();
        Pass(7);

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
        canvas.Restore();
    }

    private static SKPath UnionOf(IReadOnlyList<Part> parts)
    {
        if (parts.Count == 1) return new SKPath(parts[0].Body);
        var result = new SKPath(parts[0].Body);
        for (var i = 1; i < parts.Count; i++)
        {
            var merged = new SKPath();
            if (result.Op(parts[i].Body, SKPathOp.Union, merged))
            {
                result.Dispose();
                result = merged;
            }
            else if (RobustPathOps.TryUnion(result, parts[i].Body, merged))
            {
                // A degenerate operand (DEC-051): the topology fallback resolves the union.
                result.Dispose();
                result = merged;
            }
            else
            {
                // Keep both outlines in one path rather than lose water.
                merged.Dispose();
                result.AddPath(parts[i].Body);
            }
        }
        return result;
    }

    private static WorldBounds Union(WorldBounds a, WorldBounds b) => new(
        Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

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
            var width = Math.Max(0.6 / scale, averageHalf * 0.10);
            var seed = geometry.Channel.Id.GetHashCode();
            using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)width, StrokeCap = SKStrokeCap.Round };
            using var second = new SKPaint { Color = color.WithAlpha((byte)(color.Alpha * 0.65)), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = (float)(width * 0.8), StrokeCap = SKStrokeCap.Round };
            // Short, uneven and drifting across the channel. Even dashes down the middle read as
            // lane markings on a road, which is the one thing water never looks like.
            using var left = DashedOffsetPath(samples, 0.30, averageHalf * 1.5, averageHalf * 3.4, 0, seed);
            using var right = DashedOffsetPath(samples, -0.34, averageHalf * 1.2, averageHalf * 4.2, averageHalf * 2.6, seed ^ 0x51ED);
            canvas.DrawPath(left, paint);
            canvas.DrawPath(right, second);
        }
        canvas.Restore();
    }

    private static SKPath DashedOffsetPath(
        RiverSample[] samples, double offsetFraction, double dash, double gap, double phase, int seed)
    {
        var path = new SKPath();
        var period = dash + gap;
        var open = false;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            var position = (s.Distance + phase) % period;
            // The length of each stroke and its distance from the centre both wander, so no two
            // strands are the same and none of them runs the length of the channel.
            var lengthWobble = 0.55 + 0.75 * (0.5 + 0.5 * RiverGeometry.Noise(seed, (s.Distance + phase) / Math.Max(1e-6, period)));
            var on = position < dash * lengthWobble;
            var nx = -s.TangentY; var ny = s.TangentX;
            var drift = 1 + 0.35 * RiverGeometry.Noise(seed ^ 0x2545, s.Distance / Math.Max(1e-6, period * 1.7));
            var offset = s.HalfWidth * offsetFraction * drift;
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
        using var union = new SKPath();
        foreach (var geometry in set.Channels.Values)
        {
            var outline = RiverGeometry.Outline(geometry, style.EdgeRoughness);
            if (outline.Count < 3) continue;
            using var channel = new SKPath();
            channel.MoveTo((float)outline[0].X, (float)outline[0].Y);
            for (var i = 1; i < outline.Count; i++) channel.LineTo((float)outline[i].X, (float)outline[i].Y);
            channel.Close();
            TrimAtLake(channel, set, geometry);
            // A lake may be traced in either direction. Adding opposite windings to one path
            // cancels their overlap and leaves an earth plug in its inflow/outflow.
            if (union.IsEmpty) union.AddPath(channel);
            else
            {
                using var merged = new SKPath();
                if (union.Op(channel, SKPathOp.Union, merged)
                    || RobustPathOps.TryUnion(union, channel, merged)) { union.Reset(); union.AddPath(merged); }
                else union.AddPath(channel);
            }
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

    /// <summary>
    /// A channel that leaves or enters a lake starts or ends exactly on the lake's outline, so
    /// its rounded end reached past the shore: a tooth where an outflow left the lake and a bump
    /// beside an inflow. Near that point, whatever of the channel lies outside the lake is
    /// removed; the channel then opens straight out of the lake as one body of water.
    /// </summary>
    private static void TrimAtLake(SKPath channel, RiverGeometrySet set, RiverChannelGeometry geometry)
    {
        if (geometry.Closed || geometry.Samples.Length < 2) return;
        var samples = geometry.Samples;
        // Only the half of the disc on the lake's side is cut: for an outflow the channel runs
        // along +tangent away from the lake, so its tooth lies behind the start (−tangent); for
        // an inflow the channel arrives along +tangent, so the bump lies ahead of the end.
        // Cutting the whole disc (R04, 2026-10-21) also removed the channel's own first stretch
        // outside the lake and left a plug of land with a rim across the exit.
        if (geometry.LeavesParent && geometry.Channel.Source is { } source
            && set.Channels.TryGetValue(source.ParentChannelId, out var from) && from.Closed)
            Trim(channel, samples[0], from, -samples[0].TangentX, -samples[0].TangentY);
        if (geometry.JoinsParent && geometry.Channel.Mouth is { } mouth
            && set.Channels.TryGetValue(mouth.ParentChannelId, out var into) && into.Closed)
            Trim(channel, samples[^1], into, samples[^1].TangentX, samples[^1].TangentY);

        static void Trim(SKPath path, RiverSample at, RiverChannelGeometry lake, double sideX, double sideY)
        {
            var radius = (float)(Math.Max(at.HalfWidth, 0.5) * 1.6 + 1);
            using var shore = new SKPath();
            shore.MoveTo((float)lake.Samples[0].X, (float)lake.Samples[0].Y);
            for (var i = 1; i < lake.Samples.Length; i++) shore.LineTo((float)lake.Samples[i].X, (float)lake.Samples[i].Y);
            shore.Close();
            using var around = HalfDisc(at.X, at.Y, radius, sideX, sideY);
            using var outside = new SKPath();
            if (!around.Op(shore, SKPathOp.Difference, outside) && !RobustPathOps.TryDifference(around, shore, outside)) return;
            using var trimmed = new SKPath();
            if ((!path.Op(outside, SKPathOp.Difference, trimmed) && !RobustPathOps.TryDifference(path, outside, trimmed)) || trimmed.IsEmpty) return;
            path.Reset();
            path.AddPath(trimmed);
        }
    }

    /// <summary>
    /// The half of a disc of <paramref name="radius"/> around (x, y) that lies on the side of the
    /// unit direction (sideX, sideY); a full disc when the direction is degenerate.
    /// </summary>
    private static SKPath HalfDisc(double x, double y, float radius, double sideX, double sideY)
    {
        var path = new SKPath();
        var length = Math.Sqrt(sideX * sideX + sideY * sideY);
        if (length < 1e-9) { path.AddCircle((float)x, (float)y, radius); return path; }
        var ux = sideX / length; var uy = sideY / length;
        var vx = -uy; var vy = ux;
        const int steps = 24;
        for (var i = 0; i <= steps; i++)
        {
            var angle = -Math.PI / 2 + Math.PI * i / steps;
            var cos = Math.Cos(angle); var sin = Math.Sin(angle);
            var px = (float)(x + radius * (cos * ux + sin * vx));
            var py = (float)(y + radius * (cos * uy + sin * vy));
            if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
        }
        path.Close();
        return path;
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
        private double? _narrowest;

        /// <summary>
        /// The narrowest drawn width of the river with no floor at all. A floor below it changes
        /// nothing, so every such zoom level shares one body under the key 0 instead of
        /// re-evaluating the geometry and rebuilding the outline for each new scale.
        /// </summary>
        private double Narrowest(River river) => _narrowest ??= RiverGeometry.Evaluate(river).Channels.Values
            .Where(g => !g.Closed).SelectMany(g => g.Samples).Select(s => s.HalfWidth * 2).DefaultIfEmpty(0).Min();

        /// <summary>
        /// The floor a body is evaluated at. Below the river's own narrowest width the floor is
        /// moot (key 0). Above it, the floor is raised to the next doubling of that width: the
        /// thinnest part of the river then draws between 1.5 and 3 screen pixels instead of
        /// exactly 1.5, and one body serves a whole octave of zoom instead of every scale
        /// getting its own outline, ledges and shore lines.
        /// </summary>
        private double KeyFor(River river, double minimumWidth)
        {
            var narrowest = Narrowest(river);
            if (minimumWidth <= narrowest) return 0;
            if (narrowest <= 0.01) return Math.Round(minimumWidth, 3);
            var octave = Math.Ceiling(Math.Log2(minimumWidth / narrowest));
            return Math.Round(narrowest * Math.Pow(2, octave), 3);
        }

        public (RiverGeometrySet Geometry, SKPath Body, double AverageHalfWidth, double WidthKey) Get(River river, double minimumWidth)
        {
            var key = KeyFor(river, minimumWidth);
            foreach (var entry in _entries)
                if (entry.MinimumWidth == key) return (entry.Geometry, entry.Body, entry.AverageHalfWidth, key);
            var geometry = RiverGeometry.Evaluate(river, key);
            var body = BuildBody(geometry, river.Style);
            // The width of the water that flows: a lake's outline samples carry no width and
            // would only pull the average towards zero.
            var flowing = geometry.Channels.Values.SelectMany(g => g.Samples).Where(s => s.HalfWidth > 0).ToList();
            var average = flowing.Count > 0 ? flowing.Average(s => s.HalfWidth) : 0;
            if (_entries.Count >= 4)
            {
                _entries[0].Body.Dispose();
                _entries.RemoveAt(0);
            }
            _entries.Add((key, geometry, body, average));
            return (geometry, body, average, key);
        }
    }
}
