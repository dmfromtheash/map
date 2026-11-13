using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// The painter form maps of an asset kept on disk (A2 step 4, 2026-11-09). A file holds the exact pixels of the five maps,
/// Brotli-compressed, keyed by the asset's own pixels, the wet/block set, <see cref="PainterFormMaps.Recipe"/> and the
/// SkiaSharp version that drew them — so a stored set is the set the build would paint. Only current files are kept:
/// <see cref="Trim"/> deletes files of another recipe or Skia and the least recently used ones over <see cref="LimitBytes"/>.
/// <see cref="Folder"/> is set by the application (the profile's «StyleCache»); unset — tests — nothing touches the disk.
/// </summary>
internal static class FormMapDiskCache
{
    private const int Magic = 0x4D46504D; // "MPFM"
    internal static long LimitBytes { get; set; } = 1L << 30;

    /// <summary>The cache folder, or null for no disk cache.</summary>
    internal static string? Folder { get; set; }

    private static readonly string Stamp =
        $"r{PainterFormMaps.Recipe}-s{typeof(SKImage).Assembly.GetName().Version}";

    /// <summary>File name for this asset and set, or null when there is no folder or the pixels cannot be read.</summary>
    internal static string? KeyOf(SKImage source, bool wet)
    {
        if (Folder is null || source.IsTextureBacked) return null;
        // A picture decoded lazily from its file has no pixels to peek at: decode a copy for the fingerprint.
        using var decoded = source.PeekPixels() is null ? source.ToRasterImage(ensurePixelData: true) : null;
        using var pixmap = (decoded ?? source).PeekPixels();
        if (pixmap is null) return null;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<int> head = [source.Width, source.Height, (int)pixmap.ColorType, (int)pixmap.AlphaType];
        sha.AppendData(MemoryMarshal.AsBytes(head));
        var span = pixmap.GetPixelSpan();
        var row = pixmap.Info.BytesPerPixel * pixmap.Width;
        for (var y = 0; y < pixmap.Height; y++) sha.AppendData(span.Slice(y * pixmap.RowBytes, row));
        return $"{Convert.ToHexString(sha.GetHashAndReset())}-{(wet ? "wet" : "block")}-{Stamp}.pfm";
    }

    internal static SKImage[]? TryLoad(string key)
    {
        var path = Path.Combine(Folder!, key);
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            using var brotli = new BrotliStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(brotli);
            if (reader.ReadInt32() != Magic) return null;
            var count = reader.ReadInt32();
            var images = new SKImage[count];
            for (var i = 0; i < count; i++)
            {
                var info = new SKImageInfo(reader.ReadInt32(), reader.ReadInt32(), (SKColorType)reader.ReadInt32(), (SKAlphaType)reader.ReadInt32());
                var bytes = reader.ReadBytes(info.BytesSize);
                if (bytes.Length != info.BytesSize) { foreach (var made in images) made?.Dispose(); return null; }
                images[i] = SKImage.FromPixelCopy(info, bytes, info.RowBytes);
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return images;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            // A damaged or half-read file is rebuilt and written again.
            return null;
        }
    }

    internal static void Save(string key, IReadOnlyList<SKImage> images)
    {
        // The folder may be switched off between the key and the save (tests turn the cache off while others render).
        if (Folder is not { } folder) return;
        var path = Path.Combine(folder, key);
        var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            using (var file = File.Create(temp))
            using (var brotli = new BrotliStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(brotli))
            {
                writer.Write(Magic);
                writer.Write(images.Count);
                foreach (var image in images)
                {
                    var info = new SKImageInfo(image.Width, image.Height, image.ColorType, image.AlphaType);
                    var bytes = new byte[info.BytesSize];
                    var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    try
                    {
                        if (!image.ReadPixels(info, pin.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                            throw new IOException("form map pixels not readable");
                    }
                    finally { pin.Free(); }
                    writer.Write(info.Width); writer.Write(info.Height); writer.Write((int)info.ColorType); writer.Write((int)info.AlphaType);
                    writer.Write(bytes);
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: without the file the next run builds the set again.
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes files of another recipe or Skia, leftovers of a crash and the oldest files over the limit.</summary>
    internal static void Trim()
    {
        if (Folder is not { } folder || !Directory.Exists(folder)) return;
        var keep = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (file.Name.EndsWith("-" + Stamp + ".pfm", StringComparison.Ordinal)) keep.Add(file);
            else Delete(file);
        }
        var total = keep.Sum(f => f.Length);
        foreach (var file in keep.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= LimitBytes) break;
            total -= file.Length;
            Delete(file);
        }

        static void Delete(FileInfo file)
        {
            try { file.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
