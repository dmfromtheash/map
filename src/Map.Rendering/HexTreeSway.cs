using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// Trees of the living terrain sway in the wind (owner, 2026-10-21) without leaving the kept chunk
/// pictures: a small mask of the chunk (<see cref="HexLook.Sway"/>) holds, for every pixel of a tree
/// that is not hidden by something in front of it, how high it sits in its tree (red) and the tree's
/// own phase (green). This shader draws the chunk picture shifted sideways by that height, so a
/// crown moves and its foot stays; everything else is drawn as it is.
/// </summary>
internal static class HexTreeSway
{
    private const string Source = """
uniform shader picture;
uniform shader mask;
uniform float time;
uniform float amplitude;

half4 main(float2 p) {
    half4 m = mask.eval(p);
    if (m.a < 0.02) { return picture.eval(p); }
    float h = m.r / m.a;
    float phase = m.g / m.a;
    // A slow gust over the whole map and each tree's own beat on top of it.
    float gust = 0.65 + 0.35 * sin(time * 0.45 + p.x * 0.004 + p.y * 0.002);
    float beat = sin(time * 1.7 + phase * 6.2831 + p.x * 0.01);
    float dx = amplitude * h * h * gust * beat * m.a;
    return picture.eval(float2(p.x - dx, p.y + abs(dx) * 0.15));
}
""";

    private static readonly Lazy<SKRuntimeEffect?> Effect = new(() => SKRuntimeEffect.CreateShader(Source, out _));

    public static bool Available => Effect.Value is not null;

    /// <summary>The swaying picture: both children map their pixels onto the canvas (world units).</summary>
    internal static SKShader? Create(SKShader picture, SKShader mask, double seconds, double amplitude)
    {
        if (Effect.Value is not { } effect) return null;
        var builder = new SKRuntimeShaderBuilder(effect);
        builder.Children["picture"] = picture;
        builder.Children["mask"] = mask;
        // Kept small so the float stays precise over a long session.
        builder.Uniforms["time"] = (float)(seconds % 3600);
        builder.Uniforms["amplitude"] = (float)amplitude;
        return builder.Build();
    }
}
