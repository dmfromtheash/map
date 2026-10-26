using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Lighting;

/// <summary>The light pass of the Свет section as one SkSL shader (the V0 prototype's, on Map's buffers).</summary>
internal static class LightingShader
{
    private const string Source = """
uniform shader scene;
uniform shader shape;
uniform shader shadowMask;
uniform shader noiseTex;
uniform float viewScale;
uniform float2 viewOffset;
uniform float3 sunDir;
uniform float3 sunCol;
uniform float3 ambCol;
uniform float3 fogCol;
uniform float3 invNeutral;
uniform float3 wb;
uniform float fogDensity;
uniform float exposure;
uniform float saturation;
uniform float relight;
uniform float shadowStrength;
uniform float lightCount;
uniform float4 lp[16];
uniform float4 lc[16];

float3 toLin(float3 c) { return c * c * (c * 0.2848 + 0.7152); }

half4 main(float2 p) {
    float4 sc = scene.eval(p);
    if (sc.a <= 0.002) return half4(0.0);
    float3 painted = clamp(sc.rgb / sc.a, 0.0, 1.0);
    float3 base = toLin(painted);
    // Shape buffer: normal x, y and relief height of the stamps, coverage in alpha.
    float4 nn = shape.eval(p);
    float cov = nn.a;
    float3 sh = cov > 0.02 ? clamp(nn.rgb / cov, 0.0, 1.0) : float3(0.5, 0.5, 0.0);
    float2 nxy = sh.xy * 2.0 - 1.0;
    float3 n = normalize(float3(nxy, sqrt(max(1.0 - dot(nxy, nxy), 0.0))));
    n = normalize(mix(float3(0.0, 0.0, 1.0), n, clamp(cov * 1.5, 0.0, 1.0)));
    float height = cov > 0.02 ? sh.z : 0.0;
    // Light mask: the ink lines and the flat snow apron at the foot keep closer to the painting.
    float lum = dot(painted, float3(0.299, 0.587, 0.114));
    float ink = clamp((0.30 - lum) / 0.20, 0.0, 1.0);
    float apron = clamp(1.0 - height / 0.12, 0.0, 1.0);
    float own = clamp(0.85 - 0.6 * ink - 0.35 * apron, 0.15, 1.0);
    float mask = cov > 0.02 ? mix(1.0, own, clamp(cov * 1.5, 0.0, 1.0)) : 1.0;
    float2 w = (p - viewOffset) / viewScale;

    // Sun on the normals, relative to flat ground so the day key keeps the painting's brightness.
    float3 L = normalize(sunDir);
    float flatZ = max(L.z, 0.25);
    float dirTerm = clamp(max(dot(n, L), 0.0) / flatZ, 0.0, 1.7);
    float sunShape = mix(1.0, dirTerm, clamp(mask * relight, 0.0, 1.0));

    // Cast shadows fall on the ground, not on the objects that cast them.
    float occ = shadowMask.eval(p).a * (1.0 - smoothstep(0.25, 0.75, cov));
    float shade = 1.0 - shadowStrength * occ;

    float3 light = ambCol + sunCol * sunShape * shade;
    float3 halo = float3(0.0);
    for (int i = 0; i < 16; i++) {
        float2 dv = lp[i].xy - p;
        float d = length(dv);
        float r = lp[i].w;
        float rd = lc[i].a;
        if (float(i) < lightCount && d < max(r * 1.5, rd)) {
            float att = clamp(1.0 - d / r, 0.0, 1.0);
            att = att * sqrt(att);
            // The dim zone (R14): a low, even light from the bright circle out to its end.
            if (rd > r) { att += 0.2 * (1.0 - smoothstep(mix(r, rd, 0.6), rd, d)); }
            float3 ld = normalize(float3(dv.x, -dv.y, lp[i].z));
            float ndl = max(dot(n, ld), 0.0);
            float shapeL = mix(1.0, ndl * 1.6, clamp(mask * relight, 0.0, 1.0));
            light += lc[i].rgb * att * shapeL * 2.4;
            float g = d / (r * 0.16);
            float core = d / max(r * 0.03, 1.5);
            halo += lc[i].rgb * (0.30 * exp(-g * g) + 1.6 * exp(-core * core));
        }
    }

    float3 col = base * light * invNeutral;

    // Low-lying haze: thick on the ground, thin on the objects, gone on the peaks.
    float fn = noiseTex.eval(w / 6.0).a;
    float low = 1.0 - smoothstep(0.05, 0.55, height * cov);
    float fog = clamp(fogDensity * low * (0.55 + 0.9 * fn), 0.0, 0.85);
    col = mix(col, fogCol * (ambCol + sunCol * 0.5) * invNeutral, fog);

    col *= wb;
    col += halo * (1.0 - 0.5 * fog);
    col *= exp2(exposure);
    float luma = dot(col, float3(0.2126, 0.7152, 0.0722));
    col = mix(float3(luma), col, saturation);
    float3 s = clamp(col, 0.0, 1.0);
    float3 x = sqrt(s);
    for (int k = 0; k < 3; k++) {
        float3 f = x * x * (x * 0.2848 + 0.7152) - s;
        float3 df = x * (x * 0.8544 + 1.4304) + 1e-4;
        x = clamp(x - f / df, 0.0, 1.0);
    }
    return half4(half3(x * sc.a), half(sc.a));
}
""";

    private static readonly Lazy<SKRuntimeEffect?> Effect = new(() => SKRuntimeEffect.CreateShader(Source, out _));

    /// <summary>Compiles the shader; null if this Skia cannot (the frame is then drawn unlit).</summary>
    public static bool IsAvailable => Effect.Value is not null;

    /// <param name="bufferDevice">Where the shape and shadow buffers were drawn (a kept frame's view).</param>
    /// <param name="buffers">From those device pixels to this frame's: identity unless the view is moving.</param>
    public static SKShader? Build(SKImage scene, SKImage shape, SKImage shadow, SKImage noise,
        SKRectI device, SKRectI bufferDevice, SKMatrix buffers, int shadowMargin, float shadowScale, SKMatrix view,
        LightingState state, MapLighting lighting, (float[] Pos, float[] Col, int Count) lights)
    {
        if (Effect.Value is not { } effect) return null;
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        var linear = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var at = SKMatrix.CreateTranslation(device.Left, device.Top);
        var builder = new SKRuntimeShaderBuilder(effect);
        using var cScene = scene.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest, at);
        // Outside a moved kept buffer there is no shape: transparent (flat), not the edge smeared.
        using var cShape = shape.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, buffers.IsIdentity ? nearest : linear,
            SKMatrix.Concat(buffers, SKMatrix.CreateTranslation(bufferDevice.Left, bufferDevice.Top)));
        using var cShadow = shadow.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, linear,
            SKMatrix.Concat(buffers, SKMatrix.Concat(SKMatrix.CreateTranslation(bufferDevice.Left - shadowMargin, bufferDevice.Top - shadowMargin),
                SKMatrix.CreateScale(shadowScale, shadowScale))));
        using var cNoise = noise.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, linear);
        builder.Children["scene"] = cScene;
        builder.Children["shape"] = cShape;
        builder.Children["shadowMask"] = cShadow;
        builder.Children["noiseTex"] = cNoise;
        builder.Uniforms["viewScale"] = view.ScaleX;
        builder.Uniforms["viewOffset"] = new[] { view.TransX, view.TransY };
        builder.Uniforms["sunDir"] = new[] { (float)state.SunX, (float)state.SunY, (float)state.SunZ };
        builder.Uniforms["sunCol"] = V(state.Sun);
        builder.Uniforms["ambCol"] = V(state.Ambient);
        builder.Uniforms["fogCol"] = V(state.FogColor);
        builder.Uniforms["invNeutral"] = V(state.InvNeutral);
        builder.Uniforms["wb"] = V(state.WhiteBalance);
        builder.Uniforms["fogDensity"] = (float)state.FogDensity;
        builder.Uniforms["exposure"] = (float)state.Exposure;
        builder.Uniforms["saturation"] = (float)state.Saturation;
        builder.Uniforms["relight"] = (float)lighting.Relief.Effective * 1.4f;
        builder.Uniforms["shadowStrength"] = (float)(0.95 * state.KeyShare * lighting.Shadows.Effective);
        builder.Uniforms["lightCount"] = (float)lights.Count;
        builder.Uniforms["lp"] = lights.Pos;
        builder.Uniforms["lc"] = lights.Col;
        return builder.Build();
    }

    private static float[] V((double R, double G, double B) c) => [(float)c.R, (float)c.G, (float)c.B];
}

/// <summary>A small tileable value-noise texture (fixed seed) for the haze, identical on GPU and CPU.</summary>
internal static class NoiseTexture
{
    private static readonly Lazy<SKImage> Raster = new(Make);

    /// <summary>The shared raster texture; Skia uploads it to the GPU once and keeps it. Never disposed.</summary>
    public static SKImage Image => Raster.Value;

    private static SKImage Make()
    {
        const int size = 256;
        var rng = new Random(20261024);
        var px = new byte[size * size * 4];
        for (var c = 0; c < 4; c++)
        {
            var cell = c switch { 0 => 32, 1 => 16, 2 => 8, _ => 64 };
            var g = size / cell;
            var grid = new float[g * g];
            for (var i = 0; i < grid.Length; i++) grid[i] = (float)rng.NextDouble();
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float fx = (float)x / cell, fy = (float)y / cell;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = fx - x0, ty = fy - y0;
                tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
                float v00 = grid[(y0 % g) * g + x0 % g], v10 = grid[(y0 % g) * g + (x0 + 1) % g];
                float v01 = grid[((y0 + 1) % g) * g + x0 % g], v11 = grid[((y0 + 1) % g) * g + (x0 + 1) % g];
                var v = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                px[(y * size + x) * 4 + c] = (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
            }
        }
        return SKImage.FromPixelCopy(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque), px)!;
    }
}
