using SkiaSharp;

namespace V0Skia;

/// <summary>Light state for one moment: everything the light pass needs, nothing it caches.</summary>
internal sealed record LightState(
    float[] Sun, float[] SunColor, float[] Ambient, float[] FogColor, float FogDensity,
    float ShadowLength, float Exposure, float Saturation, float[] LightPos, float[] LightColor,
    float[] InvNeutral, float Time, float[] WhiteBalance)
{
    /// <summary>How dark a full cast shadow is: the share of the key light in key + fill (luminance).</summary>
    public float ShadowStrength
    {
        get
        {
            float Lum(float[] c) => 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];
            var key = Lum(SunColor); var fill = Lum(Ambient);
            return key + fill > 1e-4f ? 0.95f * key / (key + fill) : 0f;
        }
    }
}

/// <summary>
/// Map's own 2D light, done as one SkSL pass over the assembled frame buffers:
/// sun on normals (mixed with the painting by the light mask), soft heightfield shadows,
/// ambient, warm local lights with halos, calm stylised water, low-lying fog, final grade.
/// The same shader runs on the GPU (screen) and on the CPU (export).
/// </summary>
internal static class LightPass
{
    public static float Relight = 0.85f; // how far normals may re-shape the painting (gentle 0.5 … strong 1.0)

    private const string Source = """
uniform shader albedo;
uniform shader nrm;
uniform shader data;
uniform shader shadowMask;
uniform shader noiseTex;
uniform float shadowStrength;
uniform float3 wb;
uniform float2 origin;
uniform float3 sunDir;
uniform float3 sunCol;
uniform float3 ambCol;
uniform float3 fogCol;
uniform float fogDensity;
uniform float exposure;
uniform float saturation;
uniform float relight;
uniform float time;
uniform float3 invNeutral;
uniform float4 lp[6];
uniform float4 lc[6];

const float HUNIT = 512.0;

float3 toLin(float3 c) { return c * c * (c * 0.2848 + 0.7152); }

float noiseAt(float2 w, float scale, int ch) {
    float4 n = noiseTex.eval(w / scale);
    return ch == 0 ? n.r : (ch == 1 ? n.g : (ch == 2 ? n.b : n.a));
}

half4 main(float2 p) {
    float2 w = p + origin;
    float4 a = albedo.eval(p);
    float4 dd = data.eval(p);
    float3 n = normalize(nrm.eval(p).rgb * 2.0 - 1.0);
    float mask = dd.r;
    float h = dd.g * HUNIT;
    bool water = dd.b > 0.49;
    float shore = water ? (dd.b - 0.5) * 2.0 : 0.0;
    float3 base = toLin(a.rgb);

    // --- Water: calm stylised ripple normal, no glossy highlight.
    if (water) {
        float2 q = float2(w.x * 0.6, w.y * 1.8);
        float r1 = noiseAt(q + float2(time * 9.0, 0.0), 1.6, 1) - 0.5;
        float r2 = noiseAt(q * 0.5 - float2(time * 5.0, time * 1.5), 0.65, 0) - 0.5;
        float rip = (r1 + 0.6 * r2) * (0.35 + 0.65 * smoothstep(0.0, 0.5, shore));
        n = normalize(float3(rip * 0.25, rip * 0.9, 1.0));
        mask = 0.6;
    }

    // --- Sun: direction term relative to flat ground, so the day key reproduces the painting.
    float3 L = normalize(sunDir);
    float flatZ = max(L.z, 0.25);
    // Capped at 1.7x: under a low sun broad faces otherwise glow as pale bands across the painting.
    float dirTerm = clamp(max(dot(n, L), 0.0) / flatZ, 0.0, 1.7);
    float sunShape = mix(1.0, dirTerm, clamp(mask * relight, 0.0, 1.0));

    // --- Cast shadows of the mountains (shadow layer, laid from each base) fall on the ground only;
    // the painted mountains keep their own drawn shade. Darkness follows the key:fill ratio.
    // Soft ground/mountain threshold: a hard one flips on GPU-vs-CPU rounding at the feet.
    float occ = shadowMask.eval(p).a * (1.0 - smoothstep(66.0, 96.0, h));
    float shade = 1.0 - shadowStrength * occ;

    float3 light = ambCol + sunCol * sunShape * shade;

    // --- Local lights: soft falloff, lift faces turned toward them.
    float3 halo = float3(0.0);
    for (int i = 0; i < 6; i++) {
        float2 dv = lp[i].xy - w;
        float d = length(dv);
        float r = lp[i].w;
        if (d < r * 1.5) {
            float att = clamp(1.0 - d / r, 0.0, 1.0);
            att = att * sqrt(att);
            float3 ld = normalize(float3(dv.x, -dv.y, lp[i].z + 8.0));
            float ndl = max(dot(n, ld), 0.0);
            float shapeL = mix(1.0, ndl * 1.6, clamp(mask * relight, 0.0, 1.0));
            light += lc[i].rgb * att * shapeL * 2.4;
            float g = d / (r * 0.16);
            halo += lc[i].rgb * (0.30 * exp(-g * g) + 1.6 * exp(-d * d / 36.0));
        }
    }

    float3 col = base * light * invNeutral;

    // Water also mirrors the sky (ambient + a little sun), more in open water than at the shore.
    if (water) {
        float3 sky = (ambCol * 1.25 + sunCol * 0.35) * invNeutral * toLin(float3(0.80, 0.86, 0.92));
        col = mix(col, sky, 0.22 + 0.18 * smoothstep(0.0, 0.6, shore));
    }

    // --- Low-lying fog (does not reach the peaks), drifting slowly.
    float fn = noiseAt(w + float2(time * 6.0, 0.0), 6.0, 3);
    float low = 1.0 - smoothstep(20.0, 150.0, h);
    float fog = clamp(fogDensity * low * (0.55 + 0.9 * fn) * (water ? 1.25 : 1.0), 0.0, 0.85);
    col = mix(col, fogCol * (ambCol + sunCol * 0.5) * invNeutral, fog);

    col *= wb; // day-for-night white balance; the warm halos below stay warm
    col += halo * (1.0 - 0.5 * fog);

    // --- Grade: exposure, saturation, then back to sRGB.
    col *= exp2(exposure);
    float luma = dot(col, float3(0.2126, 0.7152, 0.0722));
    col = mix(float3(luma), col, saturation);
    float3 s = clamp(col, 0.0, 1.0);
    // Inverse of toLin: Newton steps on the polynomial, so day + neutral light round-trips exactly.
    float3 x = sqrt(s);
    for (int k = 0; k < 3; k++) {
        float3 f = x * x * (x * 0.2848 + 0.7152) - s;
        float3 df = x * (x * 0.8544 + 1.4304) + 1e-4;
        x = clamp(x - f / df, 0.0, 1.0);
    }
    return half4(half3(x), 1.0);
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
        SKRuntimeEffect.CreateShader(Source, out var errors) ?? throw new InvalidOperationException(errors));

    public static void Draw(SKCanvas canvas, FrameBuffers b, SKImage shadows, SKImage noise, SKPoint origin, LightState s, SKRect dst)
    {
        var (albedo, normal, data) = (b.Albedo, b.Normal, b.Data);
        var nearest = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        var linear = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var builder = new SKRuntimeShaderBuilder(Effect.Value);
        using var cA = albedo.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
        using var cN = normal.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
        using var cD = data.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
        using var cS = shadows.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, nearest);
        using var cZ = noise.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, linear);
        builder.Children["albedo"] = cA;
        builder.Children["nrm"] = cN;
        builder.Children["data"] = cD;
        builder.Children["shadowMask"] = cS;
        builder.Uniforms["shadowStrength"] = s.ShadowStrength;
        builder.Uniforms["wb"] = s.WhiteBalance;
        builder.Children["noiseTex"] = cZ;
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["sunDir"] = s.Sun;
        builder.Uniforms["sunCol"] = s.SunColor;
        builder.Uniforms["ambCol"] = s.Ambient;
        builder.Uniforms["fogCol"] = s.FogColor;
        builder.Uniforms["fogDensity"] = s.FogDensity;
        builder.Uniforms["exposure"] = s.Exposure;
        builder.Uniforms["saturation"] = s.Saturation;
        builder.Uniforms["relight"] = Relight;
        builder.Uniforms["time"] = s.Time;
        builder.Uniforms["invNeutral"] = s.InvNeutral;
        builder.Uniforms["lp"] = s.LightPos;
        builder.Uniforms["lc"] = s.LightColor;
        using var shader = builder.Build();
        using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Src };
        canvas.DrawRect(dst, paint);
    }

    /// <summary>Interpolates the scene's time-of-day keys (smoothstep, linear-RGB mix) and the lights.</summary>
    public static LightState At(SceneInputs scene, float t, float time)
    {
        var keys = scene.Keys;
        var i = 0;
        while (i < keys.Count - 2 && t > keys[i + 1].T) i++;
        var k0 = keys[i]; var k1 = keys[i + 1];
        var u = Math.Clamp((t - k0.T) / (k1.T - k0.T), 0f, 1f);
        u = u * u * (3 - 2 * u);
        float L(float x, float y) => x + (y - x) * u;
        float[] Col(SKColor c0, float i0, SKColor c1, float i1)
        {
            var a = Lin(c0); var b = Lin(c1);
            return [L(a[0] * i0, b[0] * i1), L(a[1] * i0, b[1] * i1), L(a[2] * i0, b[2] * i1)];
        }
        var sun = new[] { L(k0.SunDir[0], k1.SunDir[0]), L(k0.SunDir[1], k1.SunDir[1]), L(k0.SunDir[2], k1.SunDir[2]) };
        var day = keys[0];
        var dSun = Lin(day.SunColor); var dAmb = Lin(day.AmbientColor);
        var neutral = new float[3];
        for (var c = 0; c < 3; c++) neutral[c] = 1f / (dAmb[c] * day.AmbientIntensity + dSun[c] * day.SunIntensity);
        var fog = Col(k0.FogColor, 1, k1.FogColor, 1);
        var lightsI = L(k0.LightsIntensity, k1.LightsIntensity);
        var pos = new float[24]; var col = new float[24];
        for (var j = 0; j < 6 && j < scene.Lights.Count; j++)
        {
            var l = scene.Lights[j];
            var flick = 1f;
            if (l.FlickerAmp > 0)
            {
                var rng = new Random(l.Seed);
                var sum = 0f;
                foreach (var hz in l.FlickerHz) sum += MathF.Sin(2 * MathF.PI * hz * time + (float)rng.NextDouble() * 6.283f);
                flick = 1 + l.FlickerAmp * sum / l.FlickerHz.Length;
            }
            var lc = Lin(l.Color);
            pos[j * 4] = l.X; pos[j * 4 + 1] = l.Y; pos[j * 4 + 2] = l.Height; pos[j * 4 + 3] = l.Radius;
            for (var c = 0; c < 3; c++) col[j * 4 + c] = lc[c] * l.Intensity * lightsI * flick;
        }
        return new LightState(sun, Col(k0.SunColor, k0.SunIntensity, k1.SunColor, k1.SunIntensity),
            Col(k0.AmbientColor, k0.AmbientIntensity, k1.AmbientColor, k1.AmbientIntensity), fog,
            L(k0.FogDensity, k1.FogDensity), L(k0.ShadowLength, k1.ShadowLength), L(k0.Exposure, k1.Exposure),
            L(k0.Saturation, k1.Saturation), pos, col, neutral, time,
            [L(k0.WhiteBalance[0], k1.WhiteBalance[0]), L(k0.WhiteBalance[1], k1.WhiteBalance[1]), L(k0.WhiteBalance[2], k1.WhiteBalance[2])]);
    }

    private static float[] Lin(SKColor c) => [Lin(c.Red), Lin(c.Green), Lin(c.Blue)];
    private static float Lin(byte v) { var s = v / 255f; return s * s * (s * 0.2848f + 0.7152f); }
}
