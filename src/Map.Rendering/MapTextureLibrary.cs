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
/// Only the owner-selected watercolor foothill is bundled. Retired texture IDs retain their
/// metadata but no pixels. A missing tile or unknown
/// id yields no shader, and the caller
/// simply draws the plain fill it would have drawn before - a map written by a newer version of
/// Map still opens and still looks like a map.
/// </para>
/// </summary>
public static class MapTextureLibrary
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SKImage?> Images = [];
    private static readonly Dictionary<string, BrushTexture> BrushTextures = [];

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

    /// <summary>The decoded tile of a built-in material, or null when the id is not one of ours.</summary>
    public static SKImage? Image(string? id)
    {
        if (MapFillTextures.Find(id) is not { } texture) return null;
        lock (Gate)
        {
            if (Images.TryGetValue(texture.Id, out var cached)) return cached;
            var image = Decode(texture.Id);
            Images[texture.Id] = image;
            return image;
        }
    }

    private static SKImage? Decode(string id)
    {
        var name = "Map.Rendering.Textures." + id + ".png";
        using var stream = typeof(MapTextureLibrary).Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var data = SKData.Create(stream);
        return SKImage.FromEncodedData(data);
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
        return image.ToShader(mode, mode,
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
