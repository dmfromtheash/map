using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The style of the open sea (R12, <see cref="SeaStyle"/>): pen water lines, depth contours,
/// engraved ripples or watercolour depth bands, read from the distance to the nearest shore of
/// every land in view. It lies on the page under the land, so the land's own coast line and
/// shadow stay on top of it; the land's ring surf is left out while a sea style is on, so no line
/// is drawn twice.
/// </summary>
public static partial class SceneRenderer
{
    /// <summary>Largest field the sea is computed in; a larger view is measured at a coarser texel.</summary>
    private const long SeaFieldBudget = 6_000_000;

    /// <summary>The sea style the page asks for, or null when there is none to draw.</summary>
    internal static SeaStyle? ActiveSea(MapDocument document, RenderOptions options) =>
        document.Layers.Count > 0 && document.Layers[0] is BaseColorLayer { Visible: true, Sea: { Strength: > 0 } sea }
        && !(options.IsExport && !options.IncludeBaseColor) && SeaShader.Available
            ? sea : null;

    private static void DrawSea(SKCanvas canvas, MapDocument document, List<LandLayer> lands, WorldBounds view, RenderOptions options)
    {
        if (options.FastTerrain || ActiveSea(document, options) is not { } sea) return;
        var visible = lands.Where(land => land is { Visible: true, Opacity: > 0 } && land.GeometryTiles.TileCount > 0).ToList();
        if (visible.Count == 0) return;
        var matrix = canvas.TotalMatrix;
        var deviceScale = Math.Max(1e-4, Math.Sqrt((matrix.ScaleX * matrix.ScaleX) + (matrix.SkewY * matrix.SkewY)));
        var reach = sea.Reach;
        var padded = new WorldBounds(view.Left - reach, view.Top - reach, view.Right + reach, view.Bottom + reach);
        var worldWidth = padded.Right - padded.Left; var worldHeight = padded.Bottom - padded.Top;
        // Texels per world unit: the device resolution, or coarser when the padded view is large.
        var texel = Math.Min(deviceScale, Math.Sqrt(SeaFieldBudget / Math.Max(1, worldWidth * worldHeight)));
        var width = Math.Max(1, (int)Math.Ceiling(worldWidth * texel));
        var height = Math.Max(1, (int)Math.Ceiling(worldHeight * texel));

        var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
        using var coverage = SKSurface.Create(info);
        if (coverage is null) return;
        coverage.Canvas.Clear(SKColors.Transparent);
        coverage.Canvas.Scale((float)texel);
        coverage.Canvas.Translate((float)-padded.Left, (float)-padded.Top);
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            DrawLandCoverage(coverage.Canvas, visible, padded, options, paint);
        byte[] alpha;
        using (var bitmap = new SKBitmap(info))
        {
            if (!coverage.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0)) return;
            alpha = bitmap.GetPixelSpan().ToArray();
        }
        // With no shore within reach every texel lies at the far limit: the open sea at its deepest.
        var distance = CoastDistanceField.SignedDistance(alpha, width, height);
        var texels = new Half[width * height * 4];
        var limit = (float)(reach * 2);
        for (var i = 0; i < distance.Length; i++)
        {
            texels[i * 4] = (Half)Math.Clamp(distance[i] / (float)texel, -limit, limit);
            texels[(i * 4) + 3] = (Half)1f;
        }
        var fieldInfo = new SKImageInfo(width, height, SKColorType.RgbaF16, SKAlphaType.Premul);
        var bytes = new byte[texels.Length * 2];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(texels.AsSpan()).CopyTo(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var fieldImage = SKImage.FromPixels(fieldInfo, data, fieldInfo.RowBytes);
        if (fieldImage is null) return;
        var toWorld = SKMatrix.CreateScaleTranslation((float)(1 / texel), (float)(1 / texel), (float)padded.Left, (float)padded.Top);
        using var field = fieldImage.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), toWorld);
        using var shader = SeaShader.Create(field, sea, 1 / deviceScale);
        if (shader is null) return;
        using var sheet = new SKPaint { Shader = shader };
        canvas.DrawRect(ToRect(view), sheet);
    }
}

/// <summary>The sea runtime effect: one pass per pixel from the shore distance (world units, positive at sea).</summary>
public static class SeaShader
{
    private const string Source = """
uniform shader field;
uniform float manner;
uniform float strength;
uniform float scale;
uniform float px;
uniform float3 cInk; uniform float3 cShallow; uniform float3 cMiddle; uniform float3 cDeep;

float hash(float2 p) { p = mod(p, 997.0); return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1, 0)), u.x), mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }

// A pen line at distance "at" from the shore, width w (world units), broken where the noise drops.
float penLine(float d, float at, float w, float2 p, float seed) {
    float on = step(0.3, noise(p / 46.0 + seed));
    float wob = (noise(p / 23.0 + seed * 3.0) - 0.5) * 2.0;
    float hw = max(w, px) * 0.5;
    return on * (1.0 - smoothstep(hw, hw + px, abs(d - at - wob))) * min(1.0, w / px);
}

half4 main(float2 p) {
    float raw = field.eval(p).r;
    // Keep the program's own coast (its pen line and soft shadow) for the first units off the shore.
    float sea = smoothstep(3.0, 7.0, raw);
    if (sea <= 0.0) return half4(0.0);
    float2 q = p / scale;
    float jit = (fbm(q / 18.0) - 0.5) * 3.0;
    float dL = raw / scale + jit;
    // Paper grain only where it is larger than a pixel; far out it would only flicker.
    float grain = (fbm(q / 2.4) - 0.5) * (1.0 - smoothstep(1.0, 3.0, px / scale));
    float wash = fbm(q / 180.0) - 0.5;
    float3 col;

    if (manner < 1.5) {
        col = mix(cShallow, cMiddle, smoothstep(10.0, 90.0, dL));
        col = mix(col, cDeep, smoothstep(90.0, 250.0, dL) * 0.6);
        col *= 1.0 + wash * 0.05 + grain * 0.02;
        float lines = penLine(dL, 9.0, 1.6, q, 1.0) * 0.85;
        lines = max(lines, penLine(dL, 20.0, 1.3, q, 2.0) * 0.65);
        lines = max(lines, penLine(dL, 35.0, 1.1, q, 3.0) * 0.45);
        lines = max(lines, penLine(dL, 55.0, 0.9, q, 4.0) * 0.28);
        col = mix(col, cInk, lines);
    } else if (manner < 2.5) {
        col = mix(cShallow, cMiddle, smoothstep(5.0, 120.0, dL));
        col = mix(col, cDeep, smoothstep(120.0, 250.0, dL));
        col *= 1.0 + wash * 0.04 + grain * 0.015;
        float s = dL / 22.0; float k = floor(s + 0.5);
        float dist = abs(s - k) * 22.0;
        // Every fourth contour is an index line, drawn heavier.
        float heavy = step(3.5, mod(k, 4.0) + 0.5);
        float w = max(mix(0.9, 1.6, heavy), px / scale);
        float contour = (1.0 - smoothstep(w * 0.5, w * 0.5 + px / scale, dist)) * step(0.5, k) * (1.0 - smoothstep(10.0, 12.0, k));
        col = mix(col, cInk, contour * mix(0.32, 0.55, heavy));
    } else if (manner < 3.5) {
        col = mix(cShallow, cMiddle, smoothstep(10.0, 110.0, dL));
        col = mix(col, cDeep, smoothstep(110.0, 250.0, dL) * 0.5);
        col *= 1.0 + wash * 0.04 + grain * 0.02;
        // Level strokes: rows 5 units apart, short dashes whose share falls with the distance from shore.
        float row = floor(q.y / 5.0);
        float fy = fract(q.y / 5.0);
        float a = q.x / 26.0 + hash(float2(row, 3.0)) * 17.0;
        float seg = floor(a); float fa = fract(a);
        float keep = 1.0 - smoothstep(8.0, 140.0, dL);
        float on = step(1.0 - keep * 0.85, hash(float2(row, seg)));
        float len = 0.35 + 0.5 * hash(float2(seg, row + 7.0));
        float dash = on * step(fa, len) * (1.0 - smoothstep(0.18, 0.42, abs(fy - 0.5) * 2.0));
        // Rows closer than a pixel blur into a tone instead of a moire.
        float fine = 1.0 - smoothstep(1.2, 2.5, px / scale);
        col = mix(col, cInk, dash * mix(0.25, 0.6, keep) * fine + keep * 0.12 * (1.0 - fine));
    } else if (manner < 4.5) {
        // Three bands with wet, irregular edges; the pigment pools at each band's edge.
        float e1 = 28.0 + (fbm(q / 60.0) - 0.5) * 18.0;
        float e2 = 110.0 + (fbm(q / 90.0 + 4.0) - 0.5) * 50.0;
        float b1 = smoothstep(e1 - 3.0, e1 + 3.0, dL);
        float b2 = smoothstep(e2 - 6.0, e2 + 6.0, dL);
        col = mix(cShallow, cMiddle, b1);
        col = mix(col, cDeep, b2);
        float pool = (1.0 - smoothstep(0.0, 4.0, abs(dL - e1))) * 0.08 + (1.0 - smoothstep(0.0, 7.0, abs(dL - e2))) * 0.1;
        col *= 1.0 - pool + wash * 0.06;
        col *= 1.0 + grain * 0.05 * (0.5 + b2);
    } else {
        float e1 = 30.0 + (fbm(q / 60.0) - 0.5) * 16.0;
        float e2 = 120.0 + (fbm(q / 90.0 + 4.0) - 0.5) * 50.0;
        col = mix(cShallow, cMiddle, smoothstep(e1 - 3.0, e1 + 3.0, dL));
        col = mix(col, cDeep, smoothstep(e2 - 6.0, e2 + 6.0, dL));
        col *= 1.0 + wash * 0.05 + grain * 0.03;
        float lines = max(penLine(dL, 9.0, 1.5, q, 1.0) * 0.8, penLine(dL, 21.0, 1.2, q, 2.0) * 0.55);
        col = mix(col, cInk, lines);
    }
    float a = sea * strength;
    return half4(half3(clamp(col, 0.0, 1.0) * a), half(a));
}
""";

    private static readonly Lazy<SKRuntimeEffect?> Effect = new(() =>
    {
        var effect = SKRuntimeEffect.CreateShader(Source, out var errors);
        if (effect is null) LastError = errors;
        return effect;
    });

    public static string? LastError { get; private set; }
    public static bool Available => Effect.Value is not null;

    /// <param name="pixel">World size of one device pixel, for antialiasing the lines.</param>
    internal static SKShader? Create(SKShader field, SeaStyle sea, double pixel)
    {
        if (Effect.Value is not { } effect) return null;
        var builder = new SKRuntimeShaderBuilder(effect);
        builder.Children["field"] = field;
        builder.Uniforms["manner"] = (float)(int)sea.Manner;
        builder.Uniforms["strength"] = (float)Math.Clamp(sea.Strength, 0, 1);
        builder.Uniforms["scale"] = (float)Math.Clamp(sea.Scale, SeaStyle.MinimumScale, SeaStyle.MaximumScale);
        builder.Uniforms["px"] = (float)pixel;
        builder.Uniforms["cInk"] = Rgb(sea.Ink); builder.Uniforms["cShallow"] = Rgb(sea.Shallow);
        builder.Uniforms["cMiddle"] = Rgb(sea.Middle); builder.Uniforms["cDeep"] = Rgb(sea.Deep);
        return builder.Build();
    }

    private static float[] Rgb(string value)
    {
        var color = SKColor.TryParse(value, out var parsed) ? parsed : SKColors.Gray;
        return [color.Red / 255f, color.Green / 255f, color.Blue / 255f];
    }
}
