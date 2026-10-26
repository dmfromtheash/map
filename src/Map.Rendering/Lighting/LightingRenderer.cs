using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Lighting;

/// <summary>
/// The Свет section of a map (DEC-057, the V0 prototype moved into Map). The scene is drawn as
/// usual into an off-screen surface; beside it, shape buffers are drawn from the visible stamps
/// (normals and a light mask from <see cref="LightMaps"/>, every stamp's coverage), and a shadow
/// mask lays each stamp's silhouette from its base along the light. One SkSL pass then lights
/// the frame: sun on the normals, cast shadows on the ground, ambient, the map's own lights with
/// halos, low-lying fog, white balance, exposure and saturation. The same code runs for the
/// canvas (GPU) and for the export (CPU tiles): everything is placed in document coordinates.
/// </summary>
internal static class LightingRenderer
{
    private const int MaxLights = 16;
    private const int ShadowMargin = 64;

    /// <summary>Device pixels per pixel of the shadow mask.</summary>
    private const float ShadowScale = 2;
    private const int MaxShapeDraws = 1500;

    [ThreadStatic] private static bool _lastFrameComplete;
    /// <summary>The enclosing style may keep a lit raster only after all of its shape maps are ready.</summary>
    internal static bool LastFrameComplete => _lastFrameComplete;

    public static bool Applies(MapDocument document, RenderOptions options) =>
        document.Lighting is { Enabled: true } && !options.DisableLighting;

    public static void Render(SKCanvas canvas, MapDocument document, RenderOptions options, Action<SKCanvas> drawScene)
    {
        _lastFrameComplete = false;
        var lighting = document.Lighting!;
        var device = canvas.DeviceClipBounds;
        var m = canvas.TotalMatrix;
        if (device.Width <= 0 || device.Height <= 0 || m.SkewX != 0 || m.SkewY != 0 || m.ScaleX <= 0
            || Math.Abs(m.ScaleX - m.ScaleY) > 1e-4 * m.ScaleX || m.Persp0 != 0 || m.Persp1 != 0)
        {
            drawScene(canvas);
            return;
        }

        var scale = m.ScaleX;
        var gpu = options.IsExport ? null : options.GpuContext;
        var deviceMatrix = SKMatrix.Concat(SKMatrix.CreateTranslation(-device.Left, -device.Top), m);
        var state = LightingState.At(lighting);

        var measure = LightingDiagnostics.MeasureStages;
        var stage = measure ? Stage(gpu) : 0;

        // Only the light changed (a time slider, a flickering torch, a switch): the map and its
        // shape buffers are the previous frame's, and only the light pass runs again.
        // The canvas keeps the frame of its view between frames; the export draws every tile afresh.
        var canvasFrame = gpu is not null;
        var key = canvasFrame ? KeyFor(document, options, gpu!, m, device) : null;
        KeptFrame frame;
        var reused = false;
        SKImage? movingScene = null;
        var buffers = SKMatrix.Identity;
        if (key is not null && Kept is { } k && k.Key == key)
        {
            frame = k;
            reused = true;
            if (measure) { LightingDiagnostics.LastSceneMilliseconds = 0; stage = Stage(gpu); }
        }
        else if (key is null && canvasFrame && Kept is { Key: { } keptKey } moving && Follows(moving, keptKey, document, options, m, device, out buffers))
        {
            // The camera or a brush is moving over unchanged stamps: the map is drawn afresh, the
            // kept shape buffers and shadows follow it (moved and scaled) until the view settles
            // and the exact frame is built. A pan rebuilt them every frame (15-20 fps with light).
            movingScene = SceneImage(gpu, device, deviceMatrix, drawScene);
            if (movingScene is null) { drawScene(canvas); return; }
            frame = moving;
            reused = true;
            if (measure) { LightingDiagnostics.LastSceneMilliseconds = Elapsed(gpu, stage); stage = Stage(gpu); }
        }
        else
        {
            if (canvasFrame) DropKept();
            var built = BuildFrame(document, options, gpu, m, device, deviceMatrix, scale, drawScene, measure, ref stage);
            if (built is null) { drawScene(canvas); return; }
            frame = built;
            // A frame still waiting for refined terrain, detail tiles or shape maps is drawn once
            // and not kept: the next frame rebuilds it complete.
            if (key is not null && built.Complete && options.Stats is { TerrainRefinementsPending: 0, DetailTilesPending: 0 })
                Kept = frame = built with { Key = key };
        }
        LightingDiagnostics.LastBuffersReused = reused;
        _lastFrameComplete = frame.Complete;

        try
        {
            var shadow = ShadowFor(frame, lighting, state, gpu, frame.Key?.Device ?? device, frame.Matrix.ScaleX);
            if (measure)
            {
                LightingDiagnostics.LastBuffersMilliseconds = Elapsed(gpu, stage);
                LightingDiagnostics.LastShapeDraws = frame.ShapeDraws;
                stage = Stage(gpu);
            }

            var scene = movingScene ?? frame.Scene;
            using var shader = LightingShader.Build(scene, frame.Shape, shadow, NoiseTexture.Image, device,
                frame.Key?.Device ?? device, buffers, ShadowMargin, ShadowScale, m, state, lighting,
                Lights(lighting, state, m, device, options.LightAnimationSeconds));
            if (shader is null) { canvas.Save(); canvas.ResetMatrix(); canvas.DrawImage(scene, device.Left, device.Top); canvas.Restore(); return; }
            using var final = new SKPaint { Shader = shader };
            canvas.Save();
            canvas.ResetMatrix();
            canvas.DrawRect(SKRect.Create(device.Left, device.Top, device.Width, device.Height), final);
            canvas.Restore();
        }
        finally
        {
            movingScene?.Dispose();
            if (!ReferenceEquals(frame, Kept)) frame.Dispose();
        }
        if (measure)
        {
            LightingDiagnostics.LastShaderMilliseconds = Elapsed(gpu, stage);
            LightingDiagnostics.Samples.Enqueue(new LightingFrameSample(LightingDiagnostics.LastSceneMilliseconds,
                LightingDiagnostics.LastBuffersMilliseconds, LightingDiagnostics.LastShaderMilliseconds,
                LightingDiagnostics.LastShapeDraws, LightingDiagnostics.LastBuffersReused));
        }
        // A flickering torch or fire in view at evening or night asks for the next frame (about
        // 24 a second) while the canvas may animate; idle, the map rests.
        if (options.LightAnimationSeconds is not null && state.LightsLevel > 0.02 && FlickerInView(lighting, m, device))
            options.RequestAmbientFrame?.Invoke();
    }

    /// <summary>The map, its shape buffers and the raw shadow silhouettes of one view.</summary>
    private sealed record KeptFrame(SKImage Scene, SKImage Shape, IReadOnlyList<ShadowCaster> Casters,
        SKMatrix Matrix, double TypicalHeight, int ShapeDraws, bool Complete) : IDisposable
    {
        public FrameKey? Key { get; init; }

        public SKImage? Shadow { get; set; }
        public ShadowKey? ShadowFor { get; set; }

        public void Dispose() { Scene.Dispose(); Shape.Dispose(); Shadow?.Dispose(); }
    }

    /// <summary>One stamp's cast shadow: the sprite and where it stands.</summary>
    private sealed record ShadowCaster(SKImage Sprite, float X, float Y, float Width, float Height, float BaseFraction,
        bool FlipHorizontal, bool FlipVertical, byte Alpha);

    /// <summary>What the map and its shape buffers depend on, apart from the light itself.</summary>
    private sealed record FrameKey(MapDocument Unlit, SKMatrix Matrix, SKRectI Device, GRContext Gpu,
        object? HexCursor, object? HexOverlay, int? HexLevel, int? HexGhostLevel, HexPresentation Presentation, bool HexPlayer,
        IStampImageSource? Stamps);

    private sealed record ShadowKey(double SunX, double SunY, double Length, bool On);

    /// <summary>The last view's frame of the canvas (render thread only; one canvas draws at a time).</summary>
    private static KeptFrame? Kept;

    /// <summary>Releases the kept frame (the light switched off, the view closed, memory pressure).</summary>
    public static void DropKept()
    {
        var kept = Kept;
        Kept = null;
        kept?.Dispose();
    }

    /// <summary>
    /// The key of a frame that may be kept, or null when it must be drawn afresh: the export, a
    /// gesture or a moving camera (their frames change every time), a living hex world.
    /// </summary>
    private static FrameKey? KeyFor(MapDocument document, RenderOptions options, GRContext? gpu, SKMatrix m, SKRectI device)
    {
        if (gpu is null || options.IsExport || options.Stats is null || options.FastTerrain || options.FastPreview
            || options.LodTier != RenderLodTier.Exact || options.Preview is not null || options.MaskEditLayerId is not null
            || options.ObjectOverrides is { Count: > 0 } || options.RiverOverrides is { Count: > 0 } || options.RoadOverrides is { Count: > 0 }
            || options.HexAmbient || options.HexGestureLive || options.HexSettles is not null)
            return null;
        return new FrameKey(document with { Lighting = null }, m, device, gpu, options.HexCursor, options.HexOverlay,
            options.HexLevel, options.HexGhostLevel, options.HexPresentation, options.HexPlayerView, options.Stamps);
    }

    /// <summary>
    /// Whether the kept frame can stand in for this one while something moves: the same map and
    /// window, no stamp being dragged, the view moved or zoomed by at most half or double. Gives
    /// the matrix from the kept frame's device pixels to this frame's.
    /// </summary>
    private static bool Follows(KeptFrame kept, FrameKey keptKey, MapDocument document, RenderOptions options, SKMatrix m,
        SKRectI device, out SKMatrix buffers)
    {
        buffers = SKMatrix.Identity;
        if (options.ObjectOverrides is { Count: > 0 } || options.HexGestureLive || keptKey.Device != device
            || !ReferenceEquals(keptKey.Gpu, options.GpuContext) || !ReferenceEquals(keptKey.Stamps, options.Stamps)) return false;
        var s = m.ScaleX / kept.Matrix.ScaleX;
        if (s < 0.5f || s > 2f || keptKey.Unlit != (document with { Lighting = null })) return false;
        // current = s * kept + (m.T - s * kept.T)
        buffers = new SKMatrix(s, 0, m.TransX - s * kept.Matrix.TransX, 0, s, m.TransY - s * kept.Matrix.TransY, 0, 0, 1);
        return true;
    }

    private static SKImage? SceneImage(GRContext? gpu, SKRectI device, SKMatrix deviceMatrix, Action<SKCanvas> drawScene)
    {
        using var sceneSurface = NewSurface(gpu, device.Width, device.Height);
        if (sceneSurface is null) return null;
        sceneSurface.Canvas.Clear(SKColors.Transparent);
        sceneSurface.Canvas.SetMatrix(deviceMatrix);
        drawScene(sceneSurface.Canvas);
        return sceneSurface.Snapshot();
    }

    private static KeptFrame? BuildFrame(MapDocument document, RenderOptions options, GRContext? gpu, SKMatrix m, SKRectI device,
        SKMatrix deviceMatrix, float scale, Action<SKCanvas> drawScene, bool measure, ref long stage)
    {
        if (SceneImage(gpu, device, deviceMatrix, drawScene) is not { } scene) return null;
        if (measure) { LightingDiagnostics.LastSceneMilliseconds = Elapsed(gpu, stage); stage = Stage(gpu); }

        // The document area this frame shows, widened for shadows cast from just outside it.
        var view = new WorldBounds(
            (device.Left - m.TransX) / scale, (device.Top - m.TransY) / scale,
            (device.Right - m.TransX) / scale, (device.Bottom - m.TransY) / scale);
        var reach = (view.Bottom - view.Top) * 0.75 + 64 / scale;
        var wide = new WorldBounds(view.Left - reach, view.Top - reach, view.Right + reach, view.Bottom + reach);

        var casters = new List<ShadowCaster>();
        var drawn = new List<(SceneObject Item, StampStyle Stamp, SKRect Rect, byte Alpha, SKImage Normal)>();
        var shapes = new Dictionary<string, LightMaps.LightMapSet?>(StringComparer.Ordinal);
        var shapeDraws = 0;
        var complete = true;
        foreach (var layer in document.Layers)
        {
            if (layer is not ObjectLayer objects || !layer.Visible || layer.Opacity <= 0 || objects.Objects.Count == 0) continue;
            IEnumerable<SceneObject> candidates = ObjectSpatialIndex.For(objects).Query(wide);
            if (objects.SublayerStates.Any(s => !s.Visible)) candidates = candidates.Where(o => objects.StateFor(o.Sublayer).Visible);
            foreach (var item in candidates.OrderBy(o => o.Sublayer))
            {
                if (!item.Visible || item.ObjectOpacity <= 0 || item.Stamp is not { } stamp) continue;
                // Small stamps (a far forest) carry no readable volume or shadow: skipping them keeps
                // a dense view to a few hundred shape draws.
                if (item.Height * scale < 12 || shapeDraws >= MaxShapeDraws) continue;
                // Shape maps are built off the render thread; a stamp without them yet stays
                // unlit for a frame or two (the canvas repaints when they are ready). The
                // painting itself is fetched only to start that build: asking the image source
                // for every stamp decoded evicted pictures again and again (seconds per frame).
                if (!shapes.TryGetValue(stamp.AssetSha256, out var maps))
                {
                    maps = LightMaps.TryGetReady(stamp.AssetSha256, out var known);
                    if (maps is null && !known && options.Stamps?.TryGetImage(stamp.AssetSha256) is { } image)
                        maps = LightMaps.Get(stamp.AssetSha256, image, wait: options.IsExport);
                    if (maps is null && !known) complete = false;
                    shapes[stamp.AssetSha256] = maps;
                }
                if (maps is null) continue;
                shapeDraws++;
                var images = maps.For(gpu);
                var rect = new SKRect((float)item.X, (float)item.Y, (float)(item.X + item.Width), (float)(item.Y + item.Height));
                var alpha = (byte)Math.Clamp(Math.Round(item.ObjectOpacity * layer.Opacity * 255), 0, 255);
                if (SceneGeometry.BoundsOf(item).Intersects(view)) drawn.Add((item, stamp, rect, alpha, images.Normal));
                casters.Add(new ShadowCaster(images.Shadow, rect.Left, rect.Top, rect.Width, rect.Height, maps.BaseFraction,
                    stamp.FlipHorizontal, stamp.FlipVertical, alpha));
            }
        }

        // All shapes into one buffer, then the shadows into another: switching the target between
        // two surfaces at every stamp made each draw its own GPU pass over the whole frame (14 s
        // for 1 500 stamps on the laptop's integrated GPU).
        using var shapeSurface = NewSurface(gpu, device.Width, device.Height)!;
        shapeSurface.Canvas.Clear(SKColors.Transparent);
        shapeSurface.Canvas.SetMatrix(deviceMatrix);
        foreach (var d in drawn) DrawShape(shapeSurface.Canvas, d.Item, d.Stamp, d.Rect, d.Alpha, d.Normal, NormalFlip(d.Stamp));
        var heights = casters.Select(c => (double)c.Height).Order().ToArray();
        return new KeptFrame(scene, shapeSurface.Snapshot(), casters, m,
            heights.Length == 0 ? 0 : heights[heights.Length / 2], shapeDraws, complete);
    }

    /// <summary>The blurred shadow mask for this sun: rebuilt only when the sun or the shadow switch moves.</summary>
    private static SKImage ShadowFor(KeptFrame frame, MapLighting lighting, LightingState state, GRContext? gpu, SKRectI device, float scale)
    {
        var on = lighting.Shadows.Effective > 0 && frame.Casters.Count > 0;
        var key = new ShadowKey(Math.Round(state.SunX, 4), Math.Round(state.SunY, 4), Math.Round(state.ShadowLength, 4), on);
        if (frame.Shadow is not null && frame.ShadowFor == key) return frame.Shadow;
        frame.Shadow?.Dispose();
        // The mask is soft (blurred), so half the resolution serves: a quarter of the pixels
        // to fill while the sun moves.
        var deviceMatrix = SKMatrix.CreateTranslation(-device.Left, -device.Top);
        using var raw = NewSurface(gpu, (int)Math.Ceiling((device.Width + 2 * ShadowMargin) / ShadowScale),
            (int)Math.Ceiling((device.Height + 2 * ShadowMargin) / ShadowScale))!;
        raw.Canvas.Clear(SKColors.Transparent);
        if (on)
        {
            raw.Canvas.SetMatrix(SKMatrix.Concat(SKMatrix.CreateScale(1 / ShadowScale, 1 / ShadowScale),
                SKMatrix.Concat(SKMatrix.CreateTranslation(ShadowMargin, ShadowMargin), SKMatrix.Concat(deviceMatrix, frame.Matrix))));
            DrawShadows(raw.Canvas, frame.Casters, state);
        }
        using var rawImage = raw.Snapshot();
        using var blurred = NewSurface(gpu, rawImage.Width, rawImage.Height)!;
        blurred.Canvas.Clear(SKColors.Transparent);
        if (on)
        {
            var sigma = (float)Math.Clamp((2.5 + 0.010 * state.ShadowLength * frame.TypicalHeight) * scale, 1.2, 28) / ShadowScale;
            using var blur = SKImageFilter.CreateBlur(sigma, sigma);
            using var paint = new SKPaint { ImageFilter = blur };
            blurred.Canvas.DrawImage(rawImage, 0, 0, paint);
        }
        frame.Shadow = blurred.Snapshot();
        frame.ShadowFor = key;
        return frame.Shadow;
    }

    /// <summary>Diagnostics: finish the GPU work recorded so far, then start a clock.</summary>
    private static long Stage(GRContext? gpu)
    {
        gpu?.Flush(); gpu?.Submit(synchronous: true);
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static double Elapsed(GRContext? gpu, long started)
    {
        gpu?.Flush(); gpu?.Submit(synchronous: true);
        return System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static SKSurface? NewSurface(GRContext? gpu, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        return gpu is null ? SKSurface.Create(info) : SKSurface.Create(gpu, false, info) ?? SKSurface.Create(info);
    }

    /// <summary>A mirrored stamp turns its normals with it.</summary>
    private static SKColorFilter? NormalFlip(StampStyle stamp)
    {
        if (!stamp.FlipHorizontal && !stamp.FlipVertical) return null;
        float fx = stamp.FlipHorizontal ? -1 : 1, fy = stamp.FlipVertical ? -1 : 1;
        return SKColorFilter.CreateColorMatrix(
        [
            fx, 0, 0, 0, stamp.FlipHorizontal ? 1 : 0,
            0, fy, 0, 0, stamp.FlipVertical ? 1 : 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }

    private static void DrawShape(SKCanvas canvas, SceneObject item, StampStyle stamp, SKRect rect, byte alpha, SKImage image, SKColorFilter? filter)
    {
        using (filter)
        using (var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha), ColorFilter = filter, IsAntialias = true })
        {
            canvas.Save();
            if (item.RotationDegrees != 0) canvas.RotateDegrees((float)item.RotationDegrees, rect.MidX, rect.MidY);
            if (stamp.FlipHorizontal || stamp.FlipVertical)
            {
                canvas.Translate(rect.MidX, rect.MidY);
                canvas.Scale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1);
                canvas.Translate(-rect.MidX, -rect.MidY);
            }
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            canvas.Restore();
        }
    }

    /// <summary>
    /// The cast shadow of one stamp, as painted 2.5D sprites get it: the object's own silhouette,
    /// sheared from its base away from the sun and a little flattened. At the foot the shadow and
    /// the object share one edge, so it can never float apart (owner 2026-10-24: "тени сильно
    /// оторваны от гор"); it shows as a wedge along the far flank and on the ground behind it,
    /// short under a high sun, long at evening. The light pass keeps it off the object itself;
    /// the silhouette nudged down a hair seats the object on the ground. The sprite carries the
    /// fade toward the far end (<see cref="LightMaps.LightMapSet.Shadow"/>). All the shadows of
    /// one asset go in one mesh - a quad each for the shadow and the contact - so a moving sun
    /// costs a draw per asset, not two per stamp (14 ms for 1 500 stamps on the laptop's GPU).
    /// Black over black composes the same in any order, so grouping keeps the picture.
    /// </summary>
    private static void DrawShadows(SKCanvas canvas, IReadOnlyList<ShadowCaster> casters, LightingState state)
    {
        var lx = state.SunX; var ly = state.SunY;
        var len = Math.Sqrt(lx * lx + ly * ly);
        double dx = len > 1e-3 ? -lx / len : 0, dy = len > 1e-3 ? ly / len : 1;
        var k = state.ShadowLength;
        // Sheared along the ground away from the sun, flattened toward the foot as the sun stands higher.
        var squash = (float)Math.Clamp(1 - 0.35 * dy * k, 0.45, 1);
        var skew = (float)(dx * k * 0.6);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        const int QuadsPerCall = 65536 / 4;

        foreach (var group in casters.GroupBy(c => c.Sprite))
        {
            var image = group.Key;
            var all = group.ToArray();
            for (var start = 0; start < all.Length * 2; start += QuadsPerCall)
            {
                var quads = Math.Min(QuadsPerCall, all.Length * 2 - start);
                var positions = new SKPoint[quads * 4];
                var texs = new SKPoint[quads * 4];
                var colors = new SKColor[quads * 4];
                var indices = new ushort[quads * 6];
                for (var q = 0; q < quads; q++)
                {
                    var caster = all[(start + q) / 2];
                    var contact = (start + q) % 2 == 1;
                    float x0 = caster.X, y0 = caster.Y, w = caster.Width, h = caster.Height;
                    var b = y0 + h * caster.BaseFraction - LightMaps.ShadowFootInset * h;
                    var sprite = SKMatrix.CreateScaleTranslation(w / image.Width, h / image.Height, x0, y0);
                    if (caster.FlipHorizontal || caster.FlipVertical)
                        sprite = SKMatrix.Concat(sprite, SKMatrix.CreateScale(caster.FlipHorizontal ? -1 : 1, caster.FlipVertical ? -1 : 1,
                            image.Width / 2f, image.Height / 2f));
                    // world -> shadow: X' = X + skew (b - Y);  Y' = b + (Y - b) * squash.
                    // Contact: the silhouette nudged down a hair, so the outline itself sits on the
                    // ground (an oval under the "foot" sat under the loose snow flakes); only its foot
                    // shows - the light pass keeps shadows off the object - where the sprite is full.
                    var place = contact
                        ? SKMatrix.Concat(SKMatrix.CreateTranslation(0, 0.012f * h), sprite)
                        : SKMatrix.Concat(new SKMatrix(1, -skew, skew * b, 0, squash, b * (1 - squash), 0, 0, 1), sprite);
                    var color = SKColors.White.WithAlpha(contact ? (byte)(caster.Alpha * 0.5) : caster.Alpha);
                    var v = q * 4;
                    texs[v] = new SKPoint(0, 0); texs[v + 1] = new SKPoint(image.Width, 0);
                    texs[v + 2] = new SKPoint(image.Width, image.Height); texs[v + 3] = new SKPoint(0, image.Height);
                    for (var c = 0; c < 4; c++) { positions[v + c] = place.MapPoint(texs[v + c]); colors[v + c] = color; }
                    var i = q * 6;
                    indices[i] = (ushort)v; indices[i + 1] = (ushort)(v + 1); indices[i + 2] = (ushort)(v + 2);
                    indices[i + 3] = (ushort)v; indices[i + 4] = (ushort)(v + 2); indices[i + 5] = (ushort)(v + 3);
                }
                using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texs, colors, indices);
                using var shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, sampling);
                using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                canvas.DrawVertices(vertices, SKBlendMode.Modulate, paint);
            }
        }
    }

    /// <summary>
    /// How bright a flickering light is at this moment: a few slow and quick waves, different for
    /// every light (its id), between about 0.82 and 1.12. Without a clock (export) it is steady at 1.
    /// </summary>
    internal static double Flicker(Guid id, double? seconds)
    {
        if (seconds is not { } t) return 1;
        var phase = (id.GetHashCode() & 0xffff) / 65535.0 * Math.Tau;
        return 1 + 0.07 * Math.Sin(Math.Tau * 1.3 * t + phase) + 0.05 * Math.Sin(Math.Tau * 3.7 * t + 2.1 * phase)
            + 0.03 * Math.Sin(Math.Tau * 7.9 * t + 3.7 * phase);
    }

    private static bool FlickerInView(MapLighting lighting, SKMatrix m, SKRectI device) =>
        lighting.Lights.Effective > 0 && !lighting.Sources.IsDefaultOrEmpty && lighting.Sources.Any(s =>
        {
            if (!s.Flicker) return false;
            float x = (float)(s.X * m.ScaleX + m.TransX), y = (float)(s.Y * m.ScaleX + m.TransY), r = (float)(s.Radius * m.ScaleX);
            return r >= 1 && x + r >= device.Left && x - r <= device.Right && y + r >= device.Top && y - r <= device.Bottom;
        });

    /// <summary>The map's lights near this frame, in device pixels: position, height, radius, and colour × level.</summary>
    private static (float[] Pos, float[] Col, int Count) Lights(MapLighting lighting, LightingState state, SKMatrix m, SKRectI device,
        double? seconds)
    {
        var pos = new float[MaxLights * 4]; var col = new float[MaxLights * 4];
        if (state.LightsLevel <= 0 || lighting.Sources.IsDefaultOrEmpty) return (pos, col, 0);
        var scale = m.ScaleX;
        var cx = (device.Left + device.Right) / 2f; var cy = (device.Top + device.Bottom) / 2f;
        var chosen = lighting.Sources
            .Select(s => (Source: s, X: (float)(s.X * scale + m.TransX), Y: (float)(s.Y * scale + m.TransY), R: (float)(s.Radius * scale),
                Reach: (float)(Math.Max(s.Radius * 1.5, s.DimRadius) * scale)))
            .Where(l => l.X + l.Reach >= device.Left && l.X - l.Reach <= device.Right
                && l.Y + l.Reach >= device.Top && l.Y - l.Reach <= device.Bottom && l.R >= 1)
            .OrderBy(l => (l.X - cx) * (l.X - cx) + (l.Y - cy) * (l.Y - cy))
            .Take(MaxLights).ToList();
        for (var i = 0; i < chosen.Count; i++)
        {
            var (source, x, y, r, _) = chosen[i];
            var c = LightingState.Linear(source.Color);
            var level = state.LightsLevel * source.Intensity * (source.Flicker ? Flicker(source.Id, seconds) : 1);
            pos[i * 4] = x; pos[i * 4 + 1] = y; pos[i * 4 + 2] = (float)(Math.Max(12, source.Radius * 0.12) * scale); pos[i * 4 + 3] = r;
            col[i * 4] = (float)(c.R * level); col[i * 4 + 1] = (float)(c.G * level); col[i * 4 + 2] = (float)(c.B * level);
            col[i * 4 + 3] = source.DimZone > 0 ? (float)(source.DimRadius * scale) : 0f;
        }
        return (pos, col, chosen.Count);
    }
}
