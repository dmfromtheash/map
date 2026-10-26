using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The heights inside a land (R22, <see cref="LandLayer.ReliefTiles"/>), drawn the way the owner's
/// sheets model ground: a shade on the slopes turned from the light (the same light as the cliffs),
/// a little light on those turned to it, height contours in the slate ink with every fifth heavier,
/// and, where asked, short hachures down the steep slopes. Everything lies inside the land.
/// </summary>
public static partial class SceneRenderer
{
    /// <summary>Largest relief raster of one view; a larger view samples the heights more coarsely.</summary>
    private const long ReliefBudget = 8_000_000;

    private static void DrawRelief(SKCanvas canvas, LandLayer land, LandEdgeStyle style, WorldBounds view, double deviceScale)
    {
        if (!ReliefShader.Available || land.ReliefTiles.TileCount == 0) return;
        if (style.ReliefShading <= 0 && style.ReliefContours <= 0 && style.ReliefSteepInk <= 0) return;
        // One texel per world pixel at most: the heights are stored no finer.
        var texel = Math.Min(deviceScale, 1.0);
        var margin = 3 / texel;
        var padded = new WorldBounds(view.Left - margin, view.Top - margin, view.Right + margin, view.Bottom + margin);
        var worldWidth = padded.Right - padded.Left; var worldHeight = padded.Bottom - padded.Top;
        texel = Math.Min(texel, Math.Sqrt(ReliefBudget / Math.Max(1, worldWidth * worldHeight)));
        var width = Math.Max(1, (int)Math.Ceiling(worldWidth * texel));
        var height = Math.Max(1, (int)Math.Ceiling(worldHeight * texel));
        var keys = land.ReliefTiles.KeysIntersecting(padded.Left, padded.Top, padded.Right, padded.Bottom).ToList();
        if (keys.Count == 0) return;

        var info = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null) return;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)texel);
        surface.Canvas.Translate((float)-padded.Left, (float)-padded.Top);
        var tileInfo = new SKImageInfo(TileMap.TileSize, TileMap.TileSize, SKColorType.Alpha8, SKAlphaType.Premul);
        using (var copy = new SKPaint { BlendMode = SKBlendMode.Src })
            foreach (var key in keys)
            {
                if (land.ReliefTiles.TryGet(key) is not { } bytes) continue;
                using var tile = SKImage.FromPixelCopy(tileInfo, bytes, TileMap.TileSize);
                if (tile is null) continue;
                surface.Canvas.DrawImage(tile, TileRect(key), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), copy);
            }
        using var stepped = surface.Snapshot();
        // Heights are whole steps: blurred over about two texels the slope is smooth instead of terraced.
        using var smooth = SKSurface.Create(info);
        if (smooth is null) return;
        smooth.Canvas.Clear(SKColors.Transparent);
        using (var blur = SKImageFilter.CreateBlur(1.6f, 1.6f, SKShaderTileMode.Clamp))
        using (var soften = new SKPaint { ImageFilter = blur })
            smooth.Canvas.DrawImage(stepped, 0, 0, soften);
        using var heights = smooth.Snapshot();
        var toWorld = SKMatrix.CreateScaleTranslation((float)(1 / texel), (float)(1 / texel), (float)padded.Left, (float)padded.Top);
        using var field = heights.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), toWorld);
        using var shader = ReliefShader.Create(field, style, 1 / texel, 1 / deviceScale);
        if (shader is null) return;
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(ToRect(view), paint);
    }
}

/// <summary>Hillshade, contours and steep hachures from a height raster (0..255 in alpha), in world coordinates.</summary>
public static class ReliefShader
{
    private const string Source = """
uniform shader relief;
uniform float texel;
uniform float px;
uniform float shading;
uniform float contours;
uniform float stepH;
uniform float steep;
uniform float2 sun;
uniform float4 inkColor;
uniform float4 shadeColor;
uniform float4 lightColor;

float H(float2 p) { return relief.eval(p).a * 255.0; }
float hash2(float2 q) { q = mod(q, 997.0); return fract(sin(dot(q, float2(12.9898, 78.233))) * 43758.5453); }
float Hs(float2 p) { return (H(p) * 4.0 + H(p + float2(texel, 0.0)) + H(p - float2(texel, 0.0)) + H(p + float2(0.0, texel)) + H(p - float2(0.0, texel))) / 8.0; }

// a over b, premultiplied.
float4 over(float4 a, float4 b) { return a + b * (1.0 - a.a); }

half4 main(float2 p) {
    float h = Hs(p);
    // The slope over two texels each way: smooth enough for the light, still sharp at a crest.
    float hx = (H(p + float2(2.0 * texel, 0.0)) - H(p - float2(2.0 * texel, 0.0))) / (4.0 * texel);
    float hy = (H(p + float2(0.0, 2.0 * texel)) - H(p - float2(0.0, 2.0 * texel))) / (4.0 * texel);
    float g = length(float2(hx, hy));
    if (h < 0.5 && g < 0.001) { return half4(0.0); }
    // One height step is 0.6 world units: 255 steps make a 150-unit hill on the page.
    float k = 0.6;
    float3 n = normalize(float3(-hx * k, -hy * k, 1.0));
    float3 L = normalize(float3(sun, 1.0));
    float d = dot(n, L) - L.z;

    float4 col = float4(0.0);
    // Light on the slopes turned to the sun, shade on those turned away.
    float lit = shading * clamp(d * 1.4, 0.0, 1.0) * 0.45;
    col = over(float4(lightColor.rgb * lit, lit), col);
    float dark = shading * clamp(-d * 1.8, 0.0, 1.0) * 0.75;
    col = over(float4(shadeColor.rgb * dark, dark), col);

    // Contours: every stepH heights, every fifth heavier; none at the land's own level.
    if (contours > 0.001 && h > stepH * 0.5) {
        float s = h / stepH;
        float k5 = floor(s + 0.5);
        float dist = abs(s - k5) * stepH / max(g, 0.02);
        float heavy = step(4.5, mod(k5, 5.0) + 0.5);
        float w = max(mix(0.7, 1.4, heavy), px);
        float line = (1.0 - smoothstep(w * 0.5, w * 0.5 + px, dist)) * step(0.5, k5);
        float a = contours * line * mix(0.55, 0.85, heavy) * inkColor.a;
        col = over(float4(inkColor.rgb * a, a), col);
    }

    // Hachures down the steep slopes: short strokes along the fall line, spaced along the contour.
    float slope = g * k;
    if (steep > 0.001 && slope > 0.6) {
        // Hachures: one short pen stroke per cell of the ground, set a little off the cell's middle
        // and pointing down the fall line read at that point, longer where the slope is steeper.
        // Read per cell, not per pixel, a stroke stays one straight line however the slope turns.
        float cellSize = 6.0;
        float2 base = floor(p / cellSize);
        float hatch = 0.0;
        for (int j = -1; j <= 1; j++) {
            for (int i = -1; i <= 1; i++) {
                float2 cell = base + float2(float(i), float(j));
                float2 c = (cell + 0.5 + 0.35 * (float2(hash2(cell), hash2(cell + 17.0)) - 0.5)) * cellSize;
                float gx = (H(c + float2(2.0 * texel, 0.0)) - H(c - float2(2.0 * texel, 0.0))) / (4.0 * texel);
                float gy = (H(c + float2(0.0, 2.0 * texel)) - H(c - float2(0.0, 2.0 * texel))) / (4.0 * texel);
                float sl = length(float2(gx, gy)) * k;
                if (sl > 0.9) {
                    float2 dir = float2(gx, gy) / max(length(float2(gx, gy)), 1e-4);
                    float strength = smoothstep(0.9, 1.6, sl);
                    float len = cellSize * mix(0.25, 0.48, strength);
                    float2 d = p - c;
                    float along = dot(d, dir);
                    float across = abs(dot(d, float2(-dir.y, dir.x)));
                    // Thicker at the top of the slope, tapering down it, as a pen lifts.
                    float w = max(mix(0.55, 0.25, clamp(along / len * 0.5 + 0.5, 0.0, 1.0)), px * 0.5);
                    float onStroke = (1.0 - smoothstep(w, w + px, across)) * (1.0 - smoothstep(len, len + px, abs(along)));
                    hatch = max(hatch, onStroke * strength);
                }
            }
        }
        float a = steep * hatch * 0.85 * inkColor.a;
        col = over(float4(inkColor.rgb * a, a), col);
    }
    return half4(col);
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

    internal static SKShader? Create(SKShader relief, LandEdgeStyle style, double texel, double pixel)
    {
        if (Effect.Value is not { } effect) return null;
        var builder = new SKRuntimeShaderBuilder(effect);
        builder.Children["relief"] = relief;
        builder.Uniforms["texel"] = (float)texel;
        builder.Uniforms["px"] = (float)pixel;
        builder.Uniforms["shading"] = (float)Math.Clamp(style.ReliefShading, 0, 1);
        builder.Uniforms["contours"] = (float)Math.Clamp(style.ReliefContours, 0, 1);
        builder.Uniforms["stepH"] = (float)Math.Clamp(style.ReliefContourStep, 4, 64);
        builder.Uniforms["steep"] = (float)Math.Clamp(style.ReliefSteepInk, 0, 1);
        // The light of the cliffs, as a direction on the page towards it (y grows downwards).
        var bearing = style.Cliff.LightAngle * Math.PI / 180;
        builder.Uniforms["sun"] = new[] { (float)Math.Sin(bearing), (float)-Math.Cos(bearing) };
        builder.Uniforms["inkColor"] = Color(style.Cliff.InkColor);
        builder.Uniforms["shadeColor"] = Color(style.Cliff.ShadeColor);
        builder.Uniforms["lightColor"] = Color(style.Cliff.Color);
        return builder.Build();
    }

    private static float[] Color(string value)
    {
        var color = SKColor.TryParse(value, out var parsed) ? parsed : SKColors.Gray;
        return [color.Red / 255f, color.Green / 255f, color.Blue / 255f, color.Alpha / 255f];
    }
}
