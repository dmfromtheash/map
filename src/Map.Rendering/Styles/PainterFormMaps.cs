using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// DEC-061: abstract the painting once per asset, before placing it. The reference mountain is
/// 640 document pixels wide (the three approved small-scene mountains are 614..676). Padding
/// preserves the wet halo; the shifted up/top samples belong to this asset, never its neighbour.
/// Weak ownership follows the stamp image cache and does not keep discarded assets alive.
/// </summary>
internal sealed class PainterFormMaps
{
    internal const int ReferenceWidth = 640, Padding = 64;
    private static readonly ConditionalWeakTable<SKImage, PainterFormMaps> Cache = [];
    private readonly Lazy<Set> _wet;
    private readonly Lazy<Set> _block;

    private PainterFormMaps(SKImage source)
    {
        _wet = new(() => Build(source, true));
        _block = new(() => Build(source, false));
    }

    internal static bool IsBuilt(SKImage source) =>
        Cache.TryGetValue(source, out var maps) && maps._wet.IsValueCreated && maps._block.IsValueCreated;

    internal static Set Get(SKImage source, bool wet) =>
        wet ? Cache.GetValue(source, s => new(s))._wet.Value : Cache.GetValue(source, s => new(s))._block.Value;

    internal sealed class Set(SKImage big, SKImage facets, SKImage wide, SKImage features, SKImage tensor)
    {
        internal SKImage Big { get; } = big;
        internal SKImage Facets { get; } = facets;
        internal SKImage Wide { get; } = wide;
        // Premultiplied by Wide support: R = main crest, G = up, B = top.
        internal SKImage Features { get; } = features;
        internal SKImage Tensor { get; } = tensor;
        ~Set() { Big.Dispose(); Facets.Dispose(); Wide.Dispose(); Features.Dispose(); Tensor.Dispose(); }
    }

    private static Set Build(SKImage source, bool wet)
    {
        var height = Math.Max(1, (int)Math.Round(ReferenceWidth * (double)source.Height / source.Width));
        using var surface = SKSurface.Create(new SKImageInfo(ReferenceWidth + 2 * Padding, height + 2 * Padding))!;
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(source, SKRect.Create(Padding, Padding, ReferenceWidth, height), new SKSamplingOptions(SKFilterMode.Linear));
        using var mount = surface.Snapshot();
        using var soft = Blur(mount, 4);
        using var bigK = Kuwahara(soft, 3.2f);
        var big = Blur(bigK, wet ? 2.5f : 1.5f);
        using var facetSoft = Blur(mount, 1.8f);
        using var facetK = Kuwahara(facetSoft, 2);
        var facets = Blur(facetK, wet ? 1.5f : 1);
        using var crestA = Blur(mount, 2.5f);
        using var crestB = Blur(mount, 7);
        var wide = Blur(mount, 14);
        using var rawTensor = Apply(big, TensorEffect.Value, SKColorType.RgbaF16);
        var tensor = Blur(rawTensor, 5, SKColorType.RgbaF16);
        var builder = new SKRuntimeShaderBuilder(FeaturesEffect.Value);
        var children = new List<SKShader>();
        void Child(string name, SKImage image)
        {
            var shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, new SKSamplingOptions(SKFilterMode.Linear));
            children.Add(shader); builder.Children[name] = shader;
        }
        Child("mount", mount); Child("wide", wide); Child("ca", crestA); Child("cb", crestB);
        builder.Uniforms["wet"] = wet ? 1f : 0f;
        SKImage features;
        using (var fs = SKSurface.Create(new SKImageInfo(mount.Width, mount.Height, SKColorType.RgbaF16))!)
        using (var shader = builder.Build())
        using (var paint = new SKPaint { Shader = shader })
        {
            fs.Canvas.DrawRect(0, 0, mount.Width, mount.Height, paint);
            features = fs.Snapshot();
        }
        foreach (var child in children) child.Dispose();
        return new(big, facets, wide, features, tensor);
    }

    private static SKImage Blur(SKImage image, float sigma, SKColorType type = SKColorType.Bgra8888)
    {
        using var surface = SKSurface.Create(new SKImageInfo(image.Width, image.Height, type))!;
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Decal);
        using var paint = new SKPaint { ImageFilter = filter };
        surface.Canvas.DrawImage(image, 0, 0, paint);
        return surface.Snapshot();
    }

    private static SKImage Apply(SKImage image, SKRuntimeEffect effect, SKColorType type, float? stride = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(image.Width, image.Height, type))!;
        var builder = new SKRuntimeShaderBuilder(effect);
        using var child = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal);
        builder.Children["map"] = child;
        if (stride is { } s) builder.Uniforms["stride"] = s;
        using var shader = builder.Build();
        using var paint = new SKPaint { Shader = shader };
        surface.Canvas.DrawRect(0, 0, image.Width, image.Height, paint);
        return surface.Snapshot();
    }

    private static SKImage Kuwahara(SKImage image, float stride) => Apply(image, KuwaharaEffect.Value, SKColorType.Bgra8888, stride);
    private static readonly Lazy<SKRuntimeEffect> KuwaharaEffect = new(() => Compile("""
uniform shader map;
uniform float stride;
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
half4 main(float2 p) {
    float4 best = map.eval(p);
    float bestVar = 1e9;
    for (int q = 0; q < 4; q++) {
        float2 dir = float2(q == 0 || q == 3 ? -1.0 : 1.0, q < 2 ? -1.0 : 1.0);
        float4 sum = float4(0); float s1 = 0.0; float s2 = 0.0;
        for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) {
            float4 c = map.eval(p + dir * float2(float(i), float(j)) * stride);
            float l = lum(c.rgb) + (1.0 - c.a) * 0.6;
            sum += c; s1 += l; s2 += l * l;
        }
        float m = s1 / 16.0; float v = s2 / 16.0 - m * m;
        if (v < bestVar) { bestVar = v; best = sum / 16.0; }
    }
    return half4(best);
}
"""));

    // Exactly Tensor(big) in the mockups, including premultiplied RGB on transparency.
    private static readonly Lazy<SKRuntimeEffect> TensorEffect = new(() => Compile("""
uniform shader map;
float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }
half4 main(float2 p) {
    float tl = lum(map.eval(p + float2(-1,-1)).rgb), t = lum(map.eval(p + float2(0,-1)).rgb), tr = lum(map.eval(p + float2(1,-1)).rgb);
    float ml = lum(map.eval(p + float2(-1,0)).rgb), mr = lum(map.eval(p + float2(1,0)).rgb);
    float bl = lum(map.eval(p + float2(-1,1)).rgb), b = lum(map.eval(p + float2(0,1)).rgb), br = lum(map.eval(p + float2(1,1)).rgb);
    float gx = (tr + 2.0 * mr + br - tl - 2.0 * ml - bl) * 0.25;
    float gy = (bl + 2.0 * b + br - tl - 2.0 * t - tr) * 0.25;
    return half4(half(gx * gx), half(gy * gy), half(gx * gy * 0.5 + 0.5), 1.0);
}
"""));

    private static readonly Lazy<SKRuntimeEffect> FeaturesEffect = new(() => Compile("""
uniform shader mount;
uniform shader wide;
uniform shader ca;
uniform shader cb;
uniform float wet;
float white(float4 c) { return dot(c.rgb + (1.0 - c.a), float3(0.299,0.587,0.114)); }
half4 main(float2 p) {
    float a = wide.eval(p).a;
    float raw = white(cb.eval(p)) - white(ca.eval(p));
    float crest = wet > 0.5 ? smoothstep(0.04,0.12,raw) * smoothstep(0.3,0.7,mount.eval(p).a)
                           : smoothstep(0.11,0.125,raw) * smoothstep(0.5,0.7,mount.eval(p).a);
    float up = wide.eval(p + float2(0,70)).a;
    float top = 1.0 - wide.eval(p - float2(0,70)).a;
    return half4(half3(float3(crest,up,top) * a),half(a));
}
"""));

    internal static SKRuntimeEffect Compile(string source) =>
        SKRuntimeEffect.CreateShader(source, out var errors) ?? throw new InvalidOperationException(errors);
}
