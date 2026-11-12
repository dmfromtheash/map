using System.Reflection;
using Map.Core.Documents;
using Map.Core.Editing;
using SkiaSharp;

namespace Map.Rendering;

/// <summary>
/// The pixels behind <see cref="MapFillTextures"/>. Each material is one repeating tile
/// embedded in this assembly, decoded once per process and handed out as a repeating shader in
/// world space, so the viewport, a tile and a single-image export cannot disagree about the grain.
/// <para>
/// Bundled: only the owner's own snow and ice grounds (redrawn at four times their source size,
/// made seamless; the owner removed every other texture on 2026-10-18). Retired texture IDs retain their
/// metadata but no pixels. A missing tile or unknown
/// id yields no shader, and the caller
/// simply draws the plain fill it would have drawn before - a map written by a newer version of
/// Map still opens and still looks like a map.
/// </para>
/// <para>
/// Library textures (<c>asset:&lt;sha256&gt;</c>) come from sources added with
/// <see cref="AddAssetSource"/>, are verified against their hash, decoded once at most
/// <see cref="MaxAssetTextureSide"/> on a side and kept for the <see cref="MaxCachedAssetTextures"/>
/// most recently used. One no source can supply draws as the plain fill.
/// </para>
/// </summary>
public static class MapTextureLibrary
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SKImage?> Images = [];
    private static readonly Dictionary<string, BrushTexture> BrushTextures = [];

    /// <summary>Library textures kept decoded at once; the least recently used one is dropped beyond this.</summary>
    public const int MaxCachedAssetTextures = 12;

    /// <summary>The longest side a library texture is decoded at (the owner's own grounds are 2048²).</summary>
    public const int MaxAssetTextureSide = 2048;

    private static readonly List<Func<string, byte[]?>> AssetSources = [];
    private static readonly LinkedList<string> AssetUse = [];
    private static readonly HashSet<string> MissingAssets = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a source of library texture bytes (<c>asset:</c> ids) by content hash: the editor
    /// answers from the map's embedded assets first and then from the asset library. Disposing the
    /// result removes the source. The pixels are checked against the hash, so any source that
    /// answers yields the same picture and the cache can be shared by every open map.
    /// </summary>
    public static IDisposable AddAssetSource(Func<string, byte[]?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (Gate)
        {
            AssetSources.Add(source);
            MissingAssets.Clear();
        }
        return new SourceRegistration(source);
    }

    /// <summary>
    /// Forgets which library textures were not found, so they are asked for again (a drive was
    /// attached, a map was opened). Textures already decoded stay cached.
    /// </summary>
    public static void RetryMissingAssets()
    {
        lock (Gate) MissingAssets.Clear();
    }

    private sealed class SourceRegistration(Func<string, byte[]?> source) : IDisposable
    {
        private Func<string, byte[]?>? _source = source;

        public void Dispose()
        {
            lock (Gate)
            {
                if (_source is null) return;
                AssetSources.Remove(_source);
                _source = null;
            }
        }
    }

    /// <summary>The same pixels for Core's world-space brush sampler, decoded once.</summary>
    public static BrushTexture? BrushTexture(string id)
    {
        lock (Gate)
        {
            if (BrushTextures.TryGetValue(id, out var cached)) return cached;
            if (Image(id) is not { } image) return null;
            using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) return null;
            var pixels = new byte[image.Width * image.Height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            return BrushTextures[id] = new BrushTexture(image.Width, image.Height, pixels)
            { MirrorRepeat = MapFillTextures.Find(id)!.MirrorRepeat };
        }
    }

    /// <summary>
    /// The decoded tile of a material: a built-in one, or a library texture its sources can supply.
    /// Null when the id is unknown or the library texture is not available.
    /// </summary>
    public static SKImage? Image(string? id)
    {
        if (MapFillTextures.Find(id) is not { } texture) return null;
        lock (Gate)
        {
            if (MapFillTextures.AssetSha256(texture.Id) is { } sha) return AssetImage(texture.Id, sha);
            if (Images.TryGetValue(texture.Id, out var cached)) return cached;
            var image = Decode(texture.Id);
            Images[texture.Id] = image;
            return image;
        }
    }

    // Under Gate. A dropped image is not disposed: a renderer on another thread may still hold it,
    // and the finalizer releases it once nobody does.
    private static SKImage? AssetImage(string id, string sha)
    {
        if (Images.TryGetValue(id, out var cached) && cached is not null)
        {
            AssetUse.Remove(id);
            AssetUse.AddFirst(id);
            return cached;
        }
        if (MissingAssets.Contains(id)) return null;
        SKImage? image = null;
        foreach (var source in AssetSources.ToArray())
        {
            byte[]? bytes;
            try { bytes = source(sha); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { bytes = null; }
            if (bytes is null || !string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), sha, StringComparison.Ordinal))
                continue;
            image = DecodeAsset(bytes);
            if (image is not null) break;
        }
        if (image is null)
        {
            MissingAssets.Add(id);
            return null;
        }
        Images[id] = image;
        AssetUse.AddFirst(id);
        while (AssetUse.Count > MaxCachedAssetTextures)
        {
            var dropped = AssetUse.Last!.Value;
            AssetUse.RemoveLast();
            Images.Remove(dropped);
            BrushTextures.Remove(dropped);
        }
        return image;
    }

    private static SKImage? DecodeAsset(byte[] bytes)
    {
        using var decoded = SKBitmap.Decode(bytes);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;
        var scale = Math.Min(1.0, (double)MaxAssetTextureSide / Math.Max(decoded.Width, decoded.Height));
        if (scale >= 1) return SKImage.FromBitmap(decoded);
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(decoded.Width * scale)), Math.Max(1, (int)Math.Round(decoded.Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using var resized = decoded.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return resized is null ? null : SKImage.FromBitmap(resized);
    }

    private static SKImage? Decode(string id)
    {
        var name = "Map.Rendering.Textures." + id + ".png";
        using var stream = typeof(MapTextureLibrary).Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var data = SKData.Create(stream);
        // Decoded once into owned pixels: an encoded-backed image may be decoded again whenever
        // Skia's raster cache drops it, and the owner's grounds are 2048² (16 MiB each) - a
        // PNG decode on the render thread for every redraw that samples them.
        var encoded = SKImage.FromEncodedData(data);
        if (encoded is null) return null;
        var raster = encoded.ToRasterImage(ensurePixelData: true);
        if (!ReferenceEquals(raster, encoded)) encoded.Dispose();
        return raster;
    }

    /// <summary>
    /// A repeating shader for one material, sized so a tile covers <paramref name="worldPerTile"/>
    /// canvas units. The caller draws it in world space; the local matrix carries the scale, so
    /// zooming changes how much of the grain is on screen and never how big the grain is.
    /// </summary>
    public static SKShader? Shader(string? id, double worldPerTile)
    {
        if (Image(id) is not { } image) return null;
        var scale = (float)(Math.Clamp(worldPerTile, MapFillTextures.MinimumScale, MapFillTextures.MaximumScale)
            / Math.Max(1, image.Width));
        var mode = MapFillTextures.Find(id)!.MirrorRepeat ? SKShaderTileMode.Mirror : SKShaderTileMode.Repeat;
        // Trilinear: a far view averages the grain instead of picking one texel out of many
        // (a large tile shimmers then), and a close one blends texels instead of showing squares.
        return image.ToShader(mode, mode, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            SKMatrix.CreateScale(scale, scale));
    }

    /// <summary>
    /// Paints the material over whatever is already on the canvas inside the current clip, at the
    /// given strength. The material carries its own colour, so the result is the chosen fill
    /// colour with the material mixed into it - which is how a coloured material behaves on paper.
    /// </summary>
    public static bool DrawFill(SKCanvas canvas, string? id, double worldPerTile, double opacity, SKRect area)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (opacity <= 0) return false;
        if (Shader(id, worldPerTile) is not { } shader) return false;
        using (shader)
        using (var paint = new SKPaint
        {
            Shader = shader,
            IsAntialias = true,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(Math.Round(opacity * 255), 0, 255)),
        })
        {
            canvas.DrawRect(area, paint);
        }
        return true;
    }
}
