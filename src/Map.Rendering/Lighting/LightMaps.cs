using System.Collections.Concurrent;
using SkiaSharp;

namespace Map.Rendering.Lighting;

/// <summary>
/// Shape maps of a stamp asset for the light pass (DEC-057): a shape map (R, G = the normal's
/// x and y, B = relief height 0..1, A = the asset's alpha; premultiplied) and a cast-shadow
/// sprite. They are derived from the asset's silhouette, not from the brightness of the
/// painting: a dome over the silhouette, sharpened by the hand-written form descriptions
/// (peaks, ridges, bowls) for the owner's mountains that have them, plus a faint brush texture.
/// Built once per asset on the thread pool; the canvas repaints when one is ready
/// (<see cref="Ready"/>), the export waits for it.
/// </summary>
public static class LightMaps
{
    /// <summary>Working size: the longer side of the generated maps.</summary>
    public const int MapSize = 512;

    private static readonly ConcurrentDictionary<string, Lazy<Task<LightMapSet?>>> Sets = new(StringComparer.Ordinal);

    /// <summary>Raised on a pool thread when a set finishes; the canvas repaints.</summary>
    public static event Action? Ready;

    public sealed class LightMapSet(SKImage normal, SKImage shadow, float baseFraction) : IDisposable
    {
        /// <summary>Normal x, y and relief height of the asset, premultiplied by its alpha.</summary>
        public SKImage Normal { get; } = normal;

        /// <summary>
        /// The cast-shadow sprite: the silhouette in black, full at the foot and fading toward the
        /// top (the far end of a long shadow is lighter). Drawn sheared from the foot, one draw
        /// per stamp at the size of the shape maps instead of the painting's own pixels.
        /// </summary>
        public SKImage Shadow { get; } = shadow;

        /// <summary>The lowest covered row as a fraction of the asset height: where it stands on the ground.</summary>
        public float BaseFraction { get; } = baseFraction;

        private GRContext? _context;
        private (SKImage Normal, SKImage Shadow)? _textures;

        /// <summary>
        /// The maps as textures of this GPU context with their mip levels, uploaded once per
        /// context rather than by the draws. The raster images serve the export (no context).
        /// </summary>
        public (SKImage Normal, SKImage Shadow) For(GRContext? gpu)
        {
            if (gpu is null) return (Normal, Shadow);
            if (ReferenceEquals(_context, gpu) && _textures is { } kept) return kept;
            DisposeTextures();
            var textures = (Upload(Normal, gpu), Upload(Shadow, gpu));
            _context = gpu;
            _textures = textures;
            return textures;
        }

        private static SKImage Upload(SKImage raster, GRContext gpu) => raster.ToTextureImage(gpu, true) ?? raster;

        private void DisposeTextures()
        {
            if (_textures is { } t)
            {
                if (!ReferenceEquals(t.Normal, Normal)) t.Normal.Dispose();
                if (!ReferenceEquals(t.Shadow, Shadow)) t.Shadow.Dispose();
            }
            _textures = null;
            _context = null;
        }

        public void Dispose() { DisposeTextures(); Normal.Dispose(); Shadow.Dispose(); }
    }

    /// <summary>Where the shadow fade starts, below the foot, as a fraction of the stamp height.</summary>
    public const float ShadowFootInset = 0.06f;

    /// <summary>The set when it is already built (or failed: null, <paramref name="known"/> true); no image needed.</summary>
    public static LightMapSet? TryGetReady(string sha256, out bool known)
    {
        known = Sets.TryGetValue(sha256, out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompleted;
        return known && lazy!.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;
    }

    /// <summary>The set if ready; otherwise starts building it and returns null (or waits, when asked).</summary>
    public static LightMapSet? Get(string sha256, SKImage source, bool wait)
    {
        var lazy = Sets.GetOrAdd(sha256, key => new Lazy<Task<LightMapSet?>>(() => BuildAsync(key, source)));
        var task = lazy.Value;
        if (task.IsCompletedSuccessfully) return task.Result;
        if (!wait) return null;
        try { return task.GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return null; }
    }

    private static Task<LightMapSet?> BuildAsync(string sha256, SKImage source)
    {
        // Read the pixels on the caller's thread: the source may be a GPU-backed image.
        var w = source.Width >= source.Height ? MapSize : Math.Max(8, (int)Math.Round(MapSize * (double)source.Width / source.Height));
        var h = source.Width >= source.Height ? Math.Max(8, (int)Math.Round(MapSize * (double)source.Height / source.Width)) : MapSize;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (!source.ScalePixels(bitmap.PeekPixels(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
            return Task.FromResult<LightMapSet?>(null);
        var rgba = bitmap.Bytes;
        return Task.Run(() =>
        {
            var set = Build(sha256, rgba, w, h);
            Ready?.Invoke();
            return set;
        });
    }

    internal static LightMapSet? Build(string sha256, byte[] rgba, int w, int h)
    {
        var n = w * h;
        var alpha = new float[n];
        var lum = new float[n];
        for (var i = 0; i < n; i++)
        {
            alpha[i] = rgba[i * 4 + 3] / 255f;
            lum[i] = (0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2]) / 255f;
        }
        if (alpha.Max() < 0.5f) return null;

        var s = w / 700f; // form descriptions use a 700-px wide preview
        var inside = DistanceInside(alpha, w, h, 0.5f);
        var maxDist = Math.Max(1f, inside.Max());
        var dome = new float[n];
        for (var i = 0; i < n; i++) dome[i] = 0.45f * MathF.Pow(Math.Clamp(inside[i] / maxDist, 0, 1), 0.75f);

        var form = new float[n];
        var described = MountainForms.TryGet(sha256, out var shape);
        if (described) shape!.Rasterize(form, w, h, s);

        var height = new float[n];
        for (var i = 0; i < n; i++) height[i] = Math.Max(dome[i], form[i]) * 0.85f + dome[i] * 0.15f;
        Blur(height, w, h, 3.2f * s);

        var edge = DistanceInside(alpha, w, h, 0.35f);
        var detailA = (float[])lum.Clone(); Blur(detailA, w, h, 0.8f * s);
        var detailB = (float[])lum.Clone(); Blur(detailB, w, h, 3.0f * s);
        var detailWeight = described ? 0.010f : 0.025f;
        for (var i = 0; i < n; i++)
        {
            var e = Math.Clamp(edge[i] / (5f * s), 0, 1);
            e = e * e * (3 - 2 * e);
            height[i] = height[i] * (0.25f + 0.75f * e) * Math.Clamp(alpha[i] * 1.5f, 0, 1)
                + detailWeight * (detailA[i] - detailB[i]) * e;
            if (height[i] < 0) height[i] = 0;
        }
        Blur(height, w, h, 0.6f * s);
        var hmax = Math.Max(1e-6f, height.Max());
        for (var i = 0; i < n; i++) height[i] /= hmax;

        // Normals (OpenGL: G = up), relief = 0.30 of the width, as in the V0 prototype; one map
        // carries the normal's x and y (z follows) and the relief height, so the frame draws every
        // stamp once. The light mask (ink lines and the flat apron keep closer to the painting)
        // is read by the light pass from the painting itself and this height.
        var relief = 0.30f * w;
        var normal = new byte[n * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            float H(int xx, int yy) => height[Math.Clamp(yy, 0, h - 1) * w + Math.Clamp(xx, 0, w - 1)];
            var gx = (H(x + 1, y - 1) + 2 * H(x + 1, y) + H(x + 1, y + 1) - H(x - 1, y - 1) - 2 * H(x - 1, y) - H(x - 1, y + 1)) / 8f;
            var gy = (H(x - 1, y + 1) + 2 * H(x, y + 1) + H(x + 1, y + 1) - H(x - 1, y - 1) - 2 * H(x, y - 1) - H(x + 1, y - 1)) / 8f;
            float nx = -gx * relief, ny = gy * relief, nz = 1;
            var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            var i = y * w + x;
            normal[i * 4] = ToByte(nx / len * 0.5f + 0.5f);
            normal[i * 4 + 1] = ToByte(ny / len * 0.5f + 0.5f);
            normal[i * 4 + 2] = ToByte(height[i]);
            normal[i * 4 + 3] = (byte)Math.Clamp(MathF.Round(alpha[i] * 255), 0, 255);
        }

        var baseRow = 0;
        for (var y = h - 1; y >= 0 && baseRow == 0; y--)
            for (var x = 0; x < w; x++)
                if (alpha[y * w + x] > 0.5f) { baseRow = y; break; }
        var baseFraction = (baseRow + 1f) / h;

        // The shadow sprite: black silhouette, full from the shadow's base down, fading to 40/255 at the top.
        var fadeFrom = (baseFraction - ShadowFootInset) * h;
        var shadow = new byte[n * 4];
        for (var y = 0; y < h; y++)
        {
            var fade = fadeFrom <= 0 ? 1f : Math.Clamp(40f / 255f + (1 - 40f / 255f) * (y / fadeFrom), 40f / 255f, 1f);
            for (var x = 0; x < w; x++)
                shadow[(y * w + x) * 4 + 3] = ToByte(alpha[y * w + x] * fade);
        }
        return new LightMapSet(Image(normal, w, h), Image(shadow, w, h), baseFraction);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);

    /// <summary>
    /// A map as a premultiplied image: the light pass divides the buffers by their alpha anyway,
    /// and an unpremultiplied texture cost the GPU a conversion on every draw (14 s for a frame
    /// of 1 500 stamps on the laptop's integrated GPU).
    /// </summary>
    private static SKImage Image(byte[] px, int w, int h)
    {
        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a == 255) continue;
            px[i] = (byte)((px[i] * a + 127) / 255);
            px[i + 1] = (byte)((px[i + 1] * a + 127) / 255);
            px[i + 2] = (byte)((px[i + 2] * a + 127) / 255);
        }
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var data = SKData.CreateCopy(px);
        return SKImage.FromPixels(info, data, w * 4) ?? throw new InvalidOperationException("light map image");
    }

    /// <summary>Euclidean distance (px) from each covered pixel to the nearest uncovered one (Felzenszwalb).</summary>
    internal static float[] DistanceInside(float[] alpha, int w, int h, float threshold)
    {
        const float Inf = 1e12f;
        var f = new float[w * h];
        for (var i = 0; i < f.Length; i++) f[i] = alpha[i] > threshold ? Inf : 0;
        var line = new float[Math.Max(w, h)];
        var outLine = new float[Math.Max(w, h)];
        var v = new int[Math.Max(w, h)];
        var z = new float[Math.Max(w, h) + 1];
        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++) line[y] = f[y * w + x];
            Edt1(line, h, outLine, v, z);
            for (var y = 0; y < h; y++) f[y * w + x] = outLine[y];
        }
        for (var y = 0; y < h; y++)
        {
            Array.Copy(f, y * w, line, 0, w);
            Edt1(line, w, outLine, v, z);
            for (var x = 0; x < w; x++) f[y * w + x] = MathF.Sqrt(outLine[x]);
        }
        return f;
    }

    private static void Edt1(float[] f, int n, float[] d, int[] v, float[] z)
    {
        var k = 0;
        v[0] = 0; z[0] = float.NegativeInfinity; z[1] = float.PositiveInfinity;
        for (var q = 1; q < n; q++)
        {
            float S() => (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
            var sv = S();
            while (sv <= z[k]) { k--; sv = S(); }
            k++; v[k] = q; z[k] = sv; z[k + 1] = float.PositiveInfinity;
        }
        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            var dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    /// <summary>Gaussian approximated by three box passes per axis.</summary>
    internal static void Blur(float[] img, int w, int h, float sigma)
    {
        if (sigma < 0.5f) return;
        var r = Math.Max(1, (int)MathF.Round(sigma * 0.9f));
        var tmp = new float[img.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxH(img, tmp, w, h, r);
            BoxV(tmp, img, w, h, r);
        }
    }

    private static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        var norm = 1f / (2 * r + 1);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            float acc = 0;
            for (var x = -r; x <= r; x++) acc += src[row + Math.Clamp(x, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                dst[row + x] = acc * norm;
                acc += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        }
    }

    private static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        var norm = 1f / (2 * r + 1);
        for (var x = 0; x < w; x++)
        {
            float acc = 0;
            for (var y = -r; y <= r; y++) acc += src[Math.Clamp(y, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = acc * norm;
                acc += src[Math.Min(y + r + 1, h - 1) * w + x] - src[Math.Max(y - r, 0) * w + x];
            }
        }
    }

    /// <summary>Drops every built set (tests, memory pressure).</summary>
    public static void Clear()
    {
        foreach (var lazy in Sets.Values)
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) lazy.Value.Result?.Dispose();
        Sets.Clear();
    }
}
