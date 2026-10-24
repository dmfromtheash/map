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
    private const int MaxShapeDraws = 1500;

    public static bool Applies(MapDocument document, RenderOptions options) =>
        document.Lighting is { Enabled: true } && !options.DisableLighting;

    public static void Render(SKCanvas canvas, MapDocument document, RenderOptions options, Action<SKCanvas> drawScene)
    {
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

        using var sceneSurface = NewSurface(gpu, device.Width, device.Height);
        if (sceneSurface is null) { drawScene(canvas); return; }
        sceneSurface.Canvas.Clear(SKColors.Transparent);
        sceneSurface.Canvas.SetMatrix(deviceMatrix);
        drawScene(sceneSurface.Canvas);
        using var scene = sceneSurface.Snapshot();

        // The document area this frame shows, widened for shadows cast from just outside it.
        var view = new WorldBounds(
            (device.Left - m.TransX) / scale, (device.Top - m.TransY) / scale,
            (device.Right - m.TransX) / scale, (device.Bottom - m.TransY) / scale);
        var reach = (view.Bottom - view.Top) * 0.75 + 64 / scale;
        var wide = new WorldBounds(view.Left - reach, view.Top - reach, view.Right + reach, view.Bottom + reach);

        using var normalSurface = NewSurface(gpu, device.Width, device.Height)!;
        using var dataSurface = NewSurface(gpu, device.Width, device.Height)!;
        using var shadowSurface = NewSurface(gpu, device.Width + 2 * ShadowMargin, device.Height + 2 * ShadowMargin)!;
        normalSurface.Canvas.Clear(SKColors.Transparent);
        dataSurface.Canvas.Clear(SKColors.Transparent);
        shadowSurface.Canvas.Clear(SKColors.Transparent);
        normalSurface.Canvas.SetMatrix(deviceMatrix);
        dataSurface.Canvas.SetMatrix(deviceMatrix);
        shadowSurface.Canvas.SetMatrix(SKMatrix.Concat(SKMatrix.CreateTranslation(ShadowMargin, ShadowMargin), deviceMatrix));

        var shadowStrength = lighting.Shadows.Effective;
        var lengths = new List<double>();
        var shapeDraws = 0;
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
                if (item.Height * scale < 12 || ++shapeDraws > MaxShapeDraws) continue;
                if (options.Stamps?.TryGetImage(stamp.AssetSha256) is not { } image) continue;
                var maps = LightMaps.Get(stamp.AssetSha256, image, wait: options.IsExport);
                var rect = new SKRect((float)item.X, (float)item.Y, (float)(item.X + item.Width), (float)(item.Y + item.Height));
                var alpha = (byte)Math.Clamp(Math.Round(item.ObjectOpacity * layer.Opacity * 255), 0, 255);
                var bounds = SceneGeometry.BoundsOf(item);
                if (bounds.Intersects(view))
                {
                    DrawShape(normalSurface.Canvas, item, stamp, rect, alpha, maps?.Normal ?? image, maps is null ? FlatNormal : NormalFlip(stamp));
                    DrawShape(dataSurface.Canvas, item, stamp, rect, alpha, maps?.Data ?? image, maps is null ? FlatData : null);
                }
                if (shadowStrength > 0)
                {
                    DrawShadow(shadowSurface.Canvas, item, stamp, image, maps?.BaseFraction ?? 1f, state, alpha);
                    lengths.Add(item.Height);
                }
            }
        }

        using var normal = normalSurface.Snapshot();
        using var data = dataSurface.Snapshot();
        using var shadowRaw = shadowSurface.Snapshot();
        using var shadowBlurSurface = NewSurface(gpu, shadowRaw.Width, shadowRaw.Height)!;
        shadowBlurSurface.Canvas.Clear(SKColors.Transparent);
        if (lengths.Count > 0)
        {
            lengths.Sort();
            var typical = lengths[lengths.Count / 2];
            var sigma = (float)Math.Clamp((2.5 + 0.010 * state.ShadowLength * typical) * scale, 1.2, 28);
            using var blur = SKImageFilter.CreateBlur(sigma, sigma);
            using var paint = new SKPaint { ImageFilter = blur };
            shadowBlurSurface.Canvas.DrawImage(shadowRaw, 0, 0, paint);
        }
        using var shadow = shadowBlurSurface.Snapshot();

        using var shader = LightingShader.Build(scene, normal, data, shadow, NoiseTexture.Image, device, ShadowMargin, m, state, lighting,
            Lights(lighting, state, m, device));
        if (shader is null) { canvas.Save(); canvas.ResetMatrix(); canvas.DrawImage(scene, device.Left, device.Top); canvas.Restore(); return; }
        using var final = new SKPaint { Shader = shader };
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawRect(SKRect.Create(device.Left, device.Top, device.Width, device.Height), final);
        canvas.Restore();
    }

    private static SKSurface? NewSurface(GRContext? gpu, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        return gpu is null ? SKSurface.Create(info) : SKSurface.Create(gpu, false, info) ?? SKSurface.Create(info);
    }

    // Stamps without shape maps yet: their silhouette only (flat normal, a moderate height), so
    // they still hide what stands behind them in the shape buffers.
    private static readonly float[] FlatNormalMatrix = [0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0];
    private static readonly float[] FlatDataMatrix = [0, 0, 0, 0, 1, 0, 0, 0, 0, 0.35f, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0];
    private static SKColorFilter FlatNormal => SKColorFilter.CreateColorMatrix(FlatNormalMatrix);
    private static SKColorFilter FlatData => SKColorFilter.CreateColorMatrix(FlatDataMatrix);

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
    /// a tight dark band and an oval under the foot seat the object on the ground.
    /// </summary>
    private static void DrawShadow(SKCanvas canvas, SceneObject item, StampStyle stamp, SKImage image, float baseFraction,
        LightingState state, byte alpha)
    {
        var lx = state.SunX; var ly = state.SunY;
        var len = Math.Sqrt(lx * lx + ly * ly);
        double dx = len > 1e-3 ? -lx / len : 0, dy = len > 1e-3 ? ly / len : 1;
        var k = state.ShadowLength;
        float x0 = (float)item.X, y0 = (float)item.Y, w = (float)item.Width, h = (float)item.Height;
        var foot = y0 + h * baseFraction;
        var b = (float)(foot - 0.06 * h);
        // Sheared along the ground away from the sun, flattened toward the foot as the sun stands higher.
        var squash = (float)Math.Clamp(1 - 0.35 * dy * k, 0.45, 1);
        var skew = (float)(dx * k * 0.6);
        // world -> shadow: X' = X + skew (b - Y);  Y' = b + (Y - b) * squash
        var toShadow = new SKMatrix(1, -skew, skew * b, 0, squash, b * (1 - squash), 0, 0, 1);
        var sprite = SKMatrix.CreateScaleTranslation(w / image.Width, h / image.Height, x0, y0);
        if (stamp.FlipHorizontal || stamp.FlipVertical)
            sprite = SKMatrix.Concat(sprite, SKMatrix.CreateScale(stamp.FlipHorizontal ? -1 : 1, stamp.FlipVertical ? -1 : 1,
                image.Width / 2f, image.Height / 2f));
        var baseLocal = (b - y0) / h * image.Height;
        var local = SKRect.Create(-40, -40, image.Width + 80, image.Height + 80);

        using var silhouette = SKColorFilter.CreateBlendMode(SKColors.Black, SKBlendMode.SrcIn);
        using var fade = SKShader.CreateLinearGradient(new SKPoint(0, baseLocal), new SKPoint(0, 0),
            [SKColors.Black, SKColors.Black.WithAlpha(40)], SKShaderTileMode.Clamp);
        canvas.Save();
        canvas.Concat(SKMatrix.Concat(toShadow, sprite));
        using (var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) })
            canvas.SaveLayer(local, layerPaint);
        using (var p = new SKPaint { ColorFilter = silhouette })
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
        using (var g = new SKPaint { Shader = fade, BlendMode = SKBlendMode.DstIn })
            canvas.DrawRect(local, g);
        canvas.Restore();
        canvas.Restore();

        // Contact: the silhouette nudged down a hair, so the outline itself sits on the ground.
        // (An oval under the "foot" sat under the loose snow flakes, apart from the mountain.)
        canvas.Save();
        canvas.Concat(SKMatrix.Concat(SKMatrix.CreateTranslation(0, 0.012f * h), sprite));
        using (var p = new SKPaint { ColorFilter = silhouette, Color = SKColors.White.WithAlpha((byte)(alpha * 0.5)) })
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), p);
        canvas.Restore();
    }

    /// <summary>The map's lights near this frame, in device pixels: position, height, radius, and colour × level.</summary>
    private static (float[] Pos, float[] Col, int Count) Lights(MapLighting lighting, LightingState state, SKMatrix m, SKRectI device)
    {
        var pos = new float[MaxLights * 4]; var col = new float[MaxLights * 4];
        if (state.LightsLevel <= 0 || lighting.Sources.IsDefaultOrEmpty) return (pos, col, 0);
        var scale = m.ScaleX;
        var cx = (device.Left + device.Right) / 2f; var cy = (device.Top + device.Bottom) / 2f;
        var chosen = lighting.Sources
            .Select(s => (Source: s, X: (float)(s.X * scale + m.TransX), Y: (float)(s.Y * scale + m.TransY), R: (float)(s.Radius * scale)))
            .Where(l => l.X + l.R * 1.5f >= device.Left && l.X - l.R * 1.5f <= device.Right
                && l.Y + l.R * 1.5f >= device.Top && l.Y - l.R * 1.5f <= device.Bottom && l.R >= 1)
            .OrderBy(l => (l.X - cx) * (l.X - cx) + (l.Y - cy) * (l.Y - cy))
            .Take(MaxLights).ToList();
        for (var i = 0; i < chosen.Count; i++)
        {
            var (source, x, y, r) = chosen[i];
            var c = LightingState.Linear(source.Color);
            var level = state.LightsLevel * source.Intensity;
            pos[i * 4] = x; pos[i * 4 + 1] = y; pos[i * 4 + 2] = (float)(Math.Max(12, source.Radius * 0.12) * scale); pos[i * 4 + 3] = r;
            col[i * 4] = (float)(c.R * level); col[i * 4 + 1] = (float)(c.G * level); col[i * 4 + 2] = (float)(c.B * level);
        }
        return (pos, col, chosen.Count);
    }
}
