using Map.Spike.Core.Documents;
using SkiaSharp;

namespace Map.Spike.Rendering;

/// <summary>
/// Uploads tile buffers to Skia images and keeps them until the owning tile's revision changes.
/// Revision-based invalidation, not "redraw everything on any property change" (docs/04).
///
/// This is a render-side cache: it reads document tiles and never writes to them.
/// </summary>
public sealed class TileImageCache : IDisposable
{
    private readonly Dictionary<(Guid Layer, TileKey Key), Entry> _entries = [];
    private bool _disposed;

    public int EntryCount => _entries.Count;
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    /// <summary>Bytes held by cached images, counted from the source buffers they were built from.</summary>
    public long ApproximateBytes { get; private set; }

    public SKImage? GetRasterTile(RasterLayer layer, TileKey key)
    {
        var pixels = layer.TryGetTile(key);
        if (pixels is null)
        {
            return null;
        }

        return Get(
            layer.Id.Value,
            key,
            layer.RevisionOf(key),
            pixels,
            () => CreateRgbaImage(pixels, layer.TileSize));
    }

    public SKImage? GetMaskTile(MaskLayer mask, TileKey key)
    {
        var pixels = mask.TryGetTile(key);
        if (pixels is null)
        {
            return null;
        }

        return Get(
            mask.Id.Value,
            key,
            mask.RevisionOf(key),
            pixels,
            () => CreateAlphaImage(pixels, mask.TileSize, invert: false));
    }

    /// <summary>
    /// Inverted mask coverage, used only by the on-screen mask edit overlay. Cached under a
    /// separate synthetic layer id so it never collides with the real mask image.
    /// </summary>
    public SKImage? GetInvertedMaskTile(MaskLayer mask, TileKey key)
    {
        var pixels = mask.TryGetTile(key);
        if (pixels is null)
        {
            return null;
        }

        var syntheticLayer = InvertedIdFor(mask.Id.Value);
        return Get(
            syntheticLayer,
            key,
            mask.RevisionOf(key),
            pixels,
            () => CreateAlphaImage(pixels, mask.TileSize, invert: true));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _entries.Values)
        {
            entry.Image.Dispose();
        }

        _entries.Clear();
        ApproximateBytes = 0;
    }

    private SKImage Get(Guid layerId, TileKey key, long revision, byte[] source, Func<SKImage> factory)
    {
        var cacheKey = (layerId, key);

        if (_entries.TryGetValue(cacheKey, out var existing))
        {
            if (existing.Revision == revision)
            {
                Hits++;
                return existing.Image;
            }

            existing.Image.Dispose();
            ApproximateBytes -= existing.SourceBytes;
            _entries.Remove(cacheKey);
        }

        Misses++;
        var image = factory();
        _entries[cacheKey] = new Entry(revision, image, source.Length);
        ApproximateBytes += source.Length;
        return image;
    }

    private static SKImage CreateRgbaImage(byte[] premultipliedRgba, int tileSize)
    {
        var info = new SKImageInfo(tileSize, tileSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            premultipliedRgba, System.Runtime.InteropServices.GCHandleType.Pinned);

        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), tileSize * 4);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally
        {
            handle.Free();
        }
    }

    private static SKImage CreateAlphaImage(byte[] coverage, int tileSize, bool invert)
    {
        var data = coverage;
        if (invert)
        {
            data = new byte[coverage.Length];
            for (var i = 0; i < coverage.Length; i++)
            {
                data[i] = (byte)(255 - coverage[i]);
            }
        }

        var info = new SKImageInfo(tileSize, tileSize, SKColorType.Alpha8, SKAlphaType.Premul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            data, System.Runtime.InteropServices.GCHandleType.Pinned);

        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), tileSize);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally
        {
            handle.Free();
        }
    }

    private static Guid InvertedIdFor(Guid layerId)
    {
        Span<byte> bytes = stackalloc byte[16];
        layerId.TryWriteBytes(bytes);
        bytes[0] ^= 0xFF;
        return new Guid(bytes);
    }

    private sealed record Entry(long Revision, SKImage Image, int SourceBytes);
}
