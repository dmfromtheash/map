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

// Cells (surf lace, ice floes, rocks): x = distance to the nearest point, y = how far inside its cell, z = the cell's own number.
float3 voro(float2 x) {
    float2 n = floor(x); float2 f = fract(x);
    float f1 = 8.0; float f2 = 8.0; float id = 0.0;
    for (int j = -1; j <= 1; j++) {
        for (int i = -1; i <= 1; i++) {
            float2 g = float2(float(i), float(j));
            float2 o = float2(hash(n + g), hash(n + g + 57.0));
            float2 r = g + o - f; float d = dot(r, r);
            if (d < f1) { f2 = f1; f1 = d; id = hash(n + g + 13.0); } else if (d < f2) { f2 = d; }
        }
    }
    return float3(sqrt(f1), sqrt(f2) - sqrt(f1), id);
}

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
    } else if (manner < 5.5) {
        float e1 = 30.0 + (fbm(q / 60.0) - 0.5) * 16.0;
        float e2 = 120.0 + (fbm(q / 90.0 + 4.0) - 0.5) * 50.0;
        col = mix(cShallow, cMiddle, smoothstep(e1 - 3.0, e1 + 3.0, dL));
        col = mix(col, cDeep, smoothstep(e2 - 6.0, e2 + 6.0, dL));
        col *= 1.0 + wash * 0.05 + grain * 0.03;
        float lines = max(penLine(dL, 9.0, 1.5, q, 1.0) * 0.8, penLine(dL, 21.0, 1.2, q, 2.0) * 0.55);
        col = mix(col, cInk, lines);
    } else if (manner < 6.5) {
        // «Штрих-тень у берега»: level pen lines of uneven length hugging the coast.
        col = mix(cShallow, cMiddle, smoothstep(20.0, 140.0, dL));
        col = mix(col, cDeep, smoothstep(140.0, 260.0, dL) * 0.4);
        col *= 1.0 + wash * 0.05 + grain * 0.02;
        float rowH = 3.4;
        float row = floor(q.y / rowH); float fy = fract(q.y / rowH);
        float len = 9.0 + 34.0 * noise(float2(row * 0.37, q.x / 140.0)) + 10.0 * hash(float2(row, 5.0));
        float inside = 1.0 - smoothstep(len - 3.0, len, dL);
        float thick = 1.0 - smoothstep(0.16, 0.42, abs(fy - 0.5) * 2.0);
        float skip = step(0.16, hash(float2(row, floor(q.x / 60.0))));
        float fine = 1.0 - smoothstep(1.2, 2.5, px / scale);
        col = mix(col, cInk, inside * thick * skip * 0.62 * fine + inside * 0.16 * (1.0 - fine));
        col = mix(col, cInk, (1.0 - smoothstep(0.0, 26.0, dL)) * 0.10);
    } else if (manner < 7.5) {
        // «Акварельный ореол»: pigment pooled at the shore, running out into bare paper.
        float edge = 95.0 + (fbm(q / 70.0) - 0.5) * 80.0;
        float t = clamp(dL / edge, 0.0, 1.0);
        float pigment = pow(1.0 - t, 1.5);
        float3 paper = mix(cShallow, float3(0.97, 0.96, 0.93), 0.62);
        col = mix(paper, cDeep, pigment * 0.9);
        col = mix(col, cMiddle, pigment * (1.0 - pigment) * 0.9);
        col *= 1.0 + (fbm(q / 1.7) - 0.5) * 0.14 * pigment + wash * 0.05;
        float dried = smoothstep(0.86, 0.98, dL / edge) * (1.0 - smoothstep(0.98, 1.04, dL / edge));
        col = mix(col, cMiddle, dried * 0.45);
        col = mix(col, cInk, (1.0 - smoothstep(0.0, 5.0, dL)) * 0.22);
        col *= 1.0 + grain * 0.03;
    } else if (manner < 8.5) {
        // «Пена и прибой»: lace of white surf along the shore, two fainter lines further out.
        float3 water = mix(cMiddle, cDeep, smoothstep(20.0, 200.0, dL));
        water = mix(water, cShallow, (1.0 - smoothstep(0.0, 44.0, dL)) * 0.75);
        water *= 1.0 + wash * 0.06 + grain * 0.02;
        float2 warp = float2(fbm(q / 9.0 + 2.0), fbm(q / 9.0 + 31.0)) * 1.6;
        float3 v = voro(q / 7.5 + warp);
        float hole = smoothstep(0.02, 0.34, v.y);
        float wob = (fbm(q / 26.0 + 3.0) - 0.5) * 12.0;
        float ragged = (fbm(q / 6.0 + 17.0) - 0.5) * 6.0;
        // A solid white rim that frays outwards into lace, as a wave runs up and thins.
        float core = 1.0 - smoothstep(3.5, 6.0, dL + ragged);
        float fray = (1.0 - smoothstep(7.0, 19.0, dL + wob * 0.5)) * (1.0 - hole);
        float b2 = (1.0 - smoothstep(0.8, 2.6, abs(dL + wob - 30.0))) * smoothstep(0.40, 0.52, noise(q / 34.0 + 7.0));
        float b3 = (1.0 - smoothstep(0.6, 2.0, abs(dL + wob * 1.3 - 52.0))) * smoothstep(0.52, 0.62, noise(q / 40.0 + 13.0));
        float foam = max(core, smoothstep(0.25, 0.55, fray));
        foam = max(foam, b2 * (0.45 + 0.4 * (1.0 - hole)));
        foam = max(foam, b3 * (0.3 + 0.3 * (1.0 - hole)));
        col = mix(water, float3(0.95, 0.97, 0.96), clamp(foam, 0.0, 1.0));
    } else if (manner < 9.5) {
        // «Лёд: припай и льдины»: fast ice along the shore, cracked; drifting floes thin out to sea.
        float3 water = mix(cMiddle, cDeep, smoothstep(10.0, 160.0, dL)) * 0.84;
        water *= 1.0 + wash * 0.06;
        float3 ice = float3(0.93, 0.96, 0.97);
        float3 rim = mix(cShallow, float3(0.55, 0.85, 0.88), 0.6);
        float shelfEdge = 20.0 + (fbm(q / 28.0) - 0.5) * 26.0;
        float shelf = 1.0 - smoothstep(shelfEdge - 0.8, shelfEdge + 0.8, dL);
        float3 v = voro(q / 15.0 + (fbm(q / 40.0) - 0.5) * 0.6);
        float prob = 1.0 - smoothstep(shelfEdge, shelfEdge + 120.0, dL);
        float exist = step(1.0 - prob * 0.92, v.z);
        float floe = exist * smoothstep(0.12, 0.18, v.y) * (1.0 - shelf);
        float under = exist * smoothstep(0.03, 0.10, v.y) * (1.0 - shelf);
        float3 c = voro(q / 9.0);
        float crack = (1.0 - smoothstep(0.02, 0.07, c.y)) * shelf * step(0.45, noise(q / 22.0 + 5.0));
        col = mix(water, rim, (1.0 - smoothstep(0.0, 3.0, abs(dL - shelfEdge - 1.5))) * (1.0 - shelf) * 0.8);
        col = mix(col, rim, clamp(under - floe, 0.0, 1.0) * 0.8);
        col = mix(col, ice, floe);
        col = mix(col, ice, shelf);
        col = mix(col, rim, crack * 0.55);
        col = mix(col, cInk, (1.0 - smoothstep(0.0, 1.2, abs(dL - shelfEdge))) * 0.35);
        col *= 1.0 + grain * 0.03;
    } else {
        // «Отмели и подводные камни»: pale shoals with a dotted edge and dark rocks ringed with surf.
        col = mix(cShallow, cMiddle, smoothstep(10.0, 100.0, dL));
        col = mix(col, cDeep, smoothstep(100.0, 250.0, dL) * 0.6);
        float near = 1.0 - smoothstep(30.0, 150.0, dL);
        float s = fbm(q / 38.0 + 11.0);
        float3 light = mix(cShallow, float3(1.0), 0.38);
        col = mix(col, mix(cShallow, float3(1.0), 0.16), smoothstep(0.56, 0.60, s) * near);
        col = mix(col, light, smoothstep(0.64, 0.68, s) * near);
        float dotted = (1.0 - smoothstep(0.004, 0.010, abs(s - 0.58))) * near * step(0.5, fract(q.x / 5.0 + q.y / 7.0));
        col = mix(col, cInk, dotted * 0.45);
        float3 v = voro(q / 26.0);
        float rockOn = step(0.78, v.z) * near * step(8.0, dL);
        float r = v.x * 26.0 + (noise(q / 2.2 + 40.0) - 0.5) * 3.4;
        float size = 2.4 + 3.4 * fract(v.z * 7.3);
        float rock = rockOn * (1.0 - smoothstep(size - 0.6, size, r));
        float ring = rockOn * (1.0 - smoothstep(size + 1.0, size + 3.2, r)) * smoothstep(0.38, 0.5, noise(q / 3.5 + 3.0)) - rock;
        col = mix(col, light, clamp(ring, 0.0, 1.0) * 0.8);
        col = mix(col, mix(cInk, cDeep, 0.3), rock * (0.72 + 0.2 * noise(q / 1.6)));
        col = mix(col, cInk, rockOn * (1.0 - smoothstep(0.0, 0.9, abs(r - size + 0.3))) * 0.7);
        col *= 1.0 + wash * 0.05 + grain * 0.02;
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
