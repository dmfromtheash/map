using System.Buffers;
using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The coast of a piece of land as one signed distance field, and every edge effect read from
/// it in a single pass. Each device pixel knows how far it is from the shore (negative inland,
/// positive at sea); the outline, every surf line and both shadows are then one lookup per pixel
/// instead of a dilation and a blur of the whole view per line. The cost no longer grows with
/// the number of surf lines or the reach of a shadow, and every line keeps the exact width and
/// spacing it was given, however wide the shadow and however far out the surf.
/// </summary>
public static class CoastDistanceField
{
    /// <summary>Largest view, in pixels, the field is computed for; beyond it the filter passes stay.</summary>
    public const int PixelBudget = 24_000_000;

    /// <summary>Off (MAP_COAST_FIELD=0) returns every coast to the filter passes, for comparison.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("MAP_COAST_FIELD") != "0";

    /// <summary>Half coverage is the shoreline; the same contour the surf used to follow.</summary>
    private const byte Shore = 128;

    /// <summary>
    /// Signed Euclidean distance, in pixels, from every pixel to the shoreline: positive at sea,
    /// negative on land, zero exactly between the last land pixel and the first sea pixel.
    /// Exact transform (Felzenszwalb and Huttenlocher), two separable passes per side.
    /// </summary>
    public static float[] SignedDistance(ReadOnlySpan<byte> coverage, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (coverage.Length < width * height)
        {
            throw new ArgumentException("Coverage is smaller than its dimensions.", nameof(coverage));
        }

        var count = width * height;
        var sea = new float[count];   // squared distance to the nearest sea pixel (for land)
        var land = new float[count];  // squared distance to the nearest land pixel (for sea)
        const float Far = 1e20f;
        for (var i = 0; i < count; i++)
        {
            var inland = coverage[i] >= Shore;
            sea[i] = inland ? Far : 0;
            land[i] = inland ? 0 : Far;
        }

        Transform(sea, width, height);
        Transform(land, width, height);
        var signed = sea;
        for (var i = 0; i < count; i++)
        {
            // Each transform measures to the centre of the nearest opposite pixel; the shore lies
            // half a pixel short of it on either side.
            signed[i] = coverage[i] >= Shore
                ? -(MathF.Sqrt(sea[i]) - 0.5f)
                : MathF.Sqrt(land[i]) - 0.5f;
        }

        return signed;
    }

    /// <summary>In-place squared Euclidean distance transform of a grid of 0 / Far values.</summary>
    private static void Transform(float[] grid, int width, int height)
    {
        var longest = Math.Max(width, height);
        var f = ArrayPool<float>.Shared.Rent(longest);
        var d = ArrayPool<float>.Shared.Rent(longest);
        var z = ArrayPool<float>.Shared.Rent(longest + 1);
        var v = ArrayPool<int>.Shared.Rent(longest);
        try
        {
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++) f[y] = grid[(y * width) + x];
                Transform1D(f, d, z, v, height);
                for (var y = 0; y < height; y++) grid[(y * width) + x] = d[y];
            }

            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                Array.Copy(grid, row, f, 0, width);
                Transform1D(f, d, z, v, width);
                Array.Copy(d, 0, grid, row, width);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(f);
            ArrayPool<float>.Shared.Return(d);
            ArrayPool<float>.Shared.Return(z);
            ArrayPool<int>.Shared.Return(v);
        }
    }

    private static void Transform1D(float[] f, float[] d, float[] z, int[] v, int n)
    {
        var k = 0;
        v[0] = 0;
        z[0] = float.NegativeInfinity;
        z[1] = float.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            float s;
            while (true)
            {
                var p = v[k];
                s = ((f[q] + (q * (float)q)) - (f[p] + (p * (float)p))) / (2f * (q - p));
                if (s > z[k]) break;
                k--;
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = float.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            var diff = q - v[k];
            d[q] = (diff * (float)diff) + f[v[k]];
        }
    }

    /// <summary>
    /// How far beyond the coast, in world units, the outer effects of this style reach: the
    /// shadow's offset and blur, the farthest surf line and the outline.
    /// </summary>
    public static double OuterReach(LandEdgeStyle style)
    {
        var shadow = style.OuterShadowOpacity > 0 && style.ShadowBlur > 0
            ? (3 * style.ShadowBlur) + Math.Max(Math.Abs(style.ShadowOffsetX), Math.Abs(style.ShadowOffsetY))
            : 0;
        var ripples = style.RippleCount > 0 && style.RippleWidth > 0 ? SceneRenderer.RippleReach(style) + 1 : 0;
        return Math.Max(Math.Max(shadow, ripples), style.OutlineWidth) + 1;
    }

    /// <summary>
    /// The effects that lie at sea, premultiplied RGBA, in the painting order of the filter
    /// passes: the outer shadow, then the surf lines from the farthest in, then the outline.
    /// <paramref name="ripples"/> is the field the surf follows (the uncarved coast when rivers
    /// cut the land); null uses <paramref name="field"/>.
    /// </summary>
    public static SKImage? OuterEffects(float[] field, float[]? ripples, int width, int height,
        LandEdgeStyle style, double scale, float[]? lowers = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(style);
        var count = width * height;
        var pixels = new byte[count * 4];
        var any = false;

        // Over other land the coast is the coast of the union, but every effect is dropped
        // inside the zone the lower land's own coast already occupies - the reach of that very
        // effect - exactly where the filter passes erased it.
        var shadowReach = (float)(((3 * style.ShadowBlur) + Math.Max(Math.Abs(style.ShadowOffsetX), Math.Abs(style.ShadowOffsetY))) * scale);
        bool Kept(int index, float reach) => lowers is null || lowers[index] > reach;

        // Shadow: the blurred, shifted silhouette as a profile over the shifted distance.
        var shadowAlpha = style.OuterShadowOpacity > 0 && style.ShadowBlur > 0
            ? Math.Clamp(style.OuterShadowOpacity, 0, 1) : 0;
        var sigma = (float)(style.ShadowBlur * scale);
        var offsetX = (int)Math.Round(style.ShadowOffsetX * scale);
        var offsetY = (int)Math.Round(style.ShadowOffsetY * scale);
        if (shadowAlpha > 0)
        {
            any = true;
            for (var y = 0; y < height; y++)
            {
                var sourceY = Math.Clamp(y - offsetY, 0, height - 1);
                for (var x = 0; x < width; x++)
                {
                    var sourceX = Math.Clamp(x - offsetX, 0, width - 1);
                    var distance = field[(sourceY * width) + sourceX];
                    var alpha = shadowAlpha * BlurredEdge(distance, sigma);
                    if (alpha <= 0.002 || !Kept((y * width) + x, shadowReach)) continue;
                    var offset = ((y * width) + x) * 4;
                    pixels[offset + 3] = (byte)Math.Round(alpha * 255); // black shadow: colour stays 0
                }
            }
        }

        // Surf lines and the outline: bands of distance with a feathered edge.
        var bands = new List<(float From, float To, float Feather, SKColor Color, float Reach)>();
        var ripplesCount = Math.Clamp(style.RippleCount, 0, 8);
        if (style.RippleWidth > 0)
        {
            for (var ripple = ripplesCount; ripple >= 1; ripple--)
            {
                var radius = Math.Max(0, style.RippleSpacing * SceneRenderer.RippleDistance(ripple)) * scale;
                var width_ = Math.Max(0.5, style.RippleWidth) * scale;
                var color = SceneRenderer.ParseColor(style.RippleColor);
                color = color.WithAlpha((byte)Math.Round(color.Alpha * SceneRenderer.RippleStrength(ripple)));
                bands.Add(((float)radius, (float)(radius + width_), (float)Math.Max(0.5, width_ * 0.3), color,
                    (float)(radius + (width_ * 1.5))));
            }
        }

        if (style.OutlineWidth > 0)
        {
            bands.Add((0, (float)(style.OutlineWidth * scale), 0.5f, SceneRenderer.ParseColor(style.OutlineColor),
                (float)(style.OutlineWidth * scale)));
        }

        if (bands.Count > 0)
        {
            any = true;
            var farthest = bands.Max(band => band.To + band.Feather);
            for (var i = 0; i < count; i++)
            {
                var distance = ripples is null ? field[i] : ripples[i];
                var outlineDistance = field[i];
                if (distance > farthest && outlineDistance > farthest) continue;
                if (distance < -1 && outlineDistance < -1) continue;
                var offset = i * 4;
                for (var b = 0; b < bands.Count; b++)
                {
                    var (from, to, feather, color, reach) = bands[b];
                    if (!Kept(i, reach)) continue;
                    var isOutline = b == bands.Count - 1 && style.OutlineWidth > 0;
                    var d = isOutline ? outlineDistance : distance;
                    // The surf never lies on the land itself: the land, carved rivers included,
                    // is cut from every ring as the filter pass did.
                    var inner = isOutline ? Band(d, from - 1, to, 0.5f, feather) : Band(d, from, to, feather, feather) * Band(outlineDistance, 0, float.MaxValue, 0.5f, 0);
                    if (inner <= 0.002) continue;
                    Over(pixels, offset, color, inner);
                }
            }
        }

        return any ? Image(pixels, width, height) : null;
    }

    /// <summary>
    /// The inner shadow, premultiplied RGBA: coverage minus its shifted blurred copy, already
    /// masked by the coverage so it never leaks over the sea.
    /// </summary>
    public static SKImage? InnerShadow(float[] field, ReadOnlySpan<byte> coverage, int width, int height,
        LandEdgeStyle style, double scale)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(style);
        if (style.InnerShadowOpacity <= 0 || style.ShadowBlur <= 0) return null;
        var count = width * height;
        var pixels = new byte[count * 4];
        var strength = Math.Clamp(style.InnerShadowOpacity, 0, 1);
        var sigma = (float)(style.ShadowBlur * scale);
        var offsetX = (int)Math.Round(style.ShadowOffsetX * scale);
        var offsetY = (int)Math.Round(style.ShadowOffsetY * scale);
        var any = false;
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Clamp(y - offsetY, 0, height - 1);
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                var cover = coverage[i];
                if (cover == 0) continue;
                var sourceX = Math.Clamp(x - offsetX, 0, width - 1);
                // Inside the land the shifted silhouette fades in over the blur: what is missing
                // from it is the shadow. 1 - blurred(-d) = blurred(d) for a symmetric profile.
                var alpha = strength * BlurredEdge(-field[(sourceY * width) + sourceX], sigma) * (cover / 255.0);
                if (alpha <= 0.002) continue;
                any = true;
                pixels[(i * 4) + 3] = (byte)Math.Round(alpha * 255);
            }
        }

        return any ? Image(pixels, width, height) : null;
    }

    /// <summary>The alpha of a Gaussian-blurred half-plane at signed distance d from its edge (1 deep inside).</summary>
    private static double BlurredEdge(float distance, float sigma)
    {
        if (sigma <= 0) return distance <= 0 ? 1 : 0;
        // Complementary error function of d/(σ√2) over two: full inland, half at the edge, fading out at sea.
        var t = distance / (sigma * 1.4142135f);
        return 0.5 * Erfc(t);
    }

    private static double Erfc(double x)
    {
        // Abramowitz-Stegun 7.1.26, accurate to 1.5e-7 - far below one alpha level.
        var sign = x < 0 ? -1 : 1;
        x = Math.Abs(x);
        var t = 1 / (1 + (0.3275911 * x));
        var y = 1 - ((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return 1 - (sign * y);
    }

    /// <summary>Coverage of a band [from, to] of distance with feathered edges.</summary>
    private static float Band(float distance, float from, float to, float featherIn, float featherOut)
    {
        var inner = featherIn <= 0 ? (distance >= from ? 1f : 0f) : Math.Clamp((distance - from) / featherIn + 0.5f, 0, 1);
        var outer = featherOut <= 0 ? (distance <= to ? 1f : 0f) : Math.Clamp((to - distance) / featherOut + 0.5f, 0, 1);
        return inner * outer;
    }

    private static void Over(byte[] pixels, int offset, SKColor color, float coverage)
    {
        var alpha = color.Alpha / 255.0 * coverage;
        if (alpha <= 0) return;
        var keep = 1 - alpha;
        pixels[offset] = (byte)Math.Clamp(Math.Round((color.Red * alpha) + (pixels[offset] * keep)), 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(Math.Round((color.Green * alpha) + (pixels[offset + 1] * keep)), 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(Math.Round((color.Blue * alpha) + (pixels[offset + 2] * keep)), 0, 255);
        pixels[offset + 3] = (byte)Math.Clamp(Math.Round((255 * alpha) + (pixels[offset + 3] * keep)), 0, 255);
    }

    private static SKImage? Image(byte[] premultiplied, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var data = SKData.CreateCopy(premultiplied);
        return SKImage.FromPixels(info, data, info.RowBytes);
    }
}
