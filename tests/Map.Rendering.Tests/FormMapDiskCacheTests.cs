using System.Reflection;
using Map.Rendering.Styles;
using SkiaSharp;

namespace Map.Rendering.Tests;

/// <summary>A2 step 4 (2026-11-09): painter form maps kept on disk between runs.</summary>
[Collection(nameof(FormMapDiskCacheTests))]
public sealed class FormMapDiskCacheTests : IDisposable
{
    private static readonly Assembly Rendering = typeof(StyleRenderer).Assembly;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "map-formmap-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        StyleRenderer.UseFormMapCache(null);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_set_read_from_disk_has_the_bytes_of_the_built_set(bool wet)
    {
        StyleRenderer.UseFormMapCache(_folder);
        // Two decodes of the same file: the first builds and writes, the second (another SKImage) reads the file.
        using var first = OwnerMountain();
        using var second = OwnerMountain();
        var built = Maps(Get(first, wet));
        // Other test classes may draw styles meanwhile and add their own files: look for this asset's file only.
        Assert.True(File.Exists(Path.Combine(_folder, KeyOf(first, wet))));
        var read = Maps(Get(second, wet));
        Assert.Equal(built.Length, read.Length);
        for (var i = 0; i < built.Length; i++)
        {
            Assert.Equal((built[i].Width, built[i].Height, built[i].ColorType, built[i].AlphaType),
                (read[i].Width, read[i].Height, read[i].ColorType, read[i].AlphaType));
            Assert.True(Pixels(built[i]).AsSpan().SequenceEqual(Pixels(read[i])), $"map {i} bytes differ");
        }
    }

    [Fact]
    public void Trim_keeps_only_current_files_within_the_limit()
    {
        Directory.CreateDirectory(_folder);
        StyleRenderer.UseFormMapCache(_folder);
        using var mountain = OwnerMountain();
        _ = Get(mountain, true);
        var current = Path.Combine(_folder, KeyOf(mountain, true));
        Assert.True(File.Exists(current));
        var stale = Path.Combine(_folder, "AB-wet-r0-s0.0.0.0.pfm");
        var leftover = Path.Combine(_folder, "x.pfm.7.tmp");
        File.WriteAllBytes(stale, [1]); File.WriteAllBytes(leftover, [1]);
        var older = Path.Combine(_folder, Path.GetFileName(current).Replace("-wet-", "-block-", StringComparison.Ordinal));
        File.Copy(current, older);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-3));

        var cache = Rendering.GetType("Map.Rendering.Styles.FormMapDiskCache")!;
        var limit = cache.GetProperty("LimitBytes", BindingFlags.Static | BindingFlags.NonPublic)!;
        var saved = limit.GetValue(null);
        try
        {
            // Room for one file: the older of the two current files goes, the newer stays.
            limit.SetValue(null, new FileInfo(current).Length + 1);
            StyleRenderer.UseFormMapCache(_folder);
        }
        finally { limit.SetValue(null, saved); }

        Assert.True(File.Exists(current), "the newest current file stays");
        Assert.False(File.Exists(older), "the oldest file over the limit is deleted");
        Assert.False(File.Exists(stale), "a file of another recipe is deleted");
        Assert.False(File.Exists(leftover), "a half-written file is deleted");
    }

    private static string KeyOf(SKImage source, bool wet) =>
        (string)Rendering.GetType("Map.Rendering.Styles.FormMapDiskCache")!
            .GetMethod("KeyOf", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source, wet])!;

    private static object Get(SKImage source, bool wet)
    {
        var type = Rendering.GetType("Map.Rendering.Styles.PainterFormMaps")!;
        return type.GetMethod("Get", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source, wet])!;
    }

    private static SKImage[] Maps(object set) =>
        new[] { "Big", "Facets", "Wide", "Features", "Tensor" }.Select(name =>
            (SKImage)set.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(set)!).ToArray();

    private static byte[] Pixels(SKImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, image.ColorType, image.AlphaType);
        var bytes = new byte[info.BytesSize];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { Assert.True(image.ReadPixels(info, pin.AddrOfPinnedObject(), info.RowBytes, 0, 0)); }
        finally { pin.Free(); }
        return bytes;
    }

    private static SKImage OwnerMountain()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx"))) directory = directory.Parent;
        var path = Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Map.slnx"),
            "tests", "fixtures", "assets", "owner-mountain-01.png");
        return SKImage.FromEncodedData(path) ?? throw new InvalidDataException(path);
    }
}
