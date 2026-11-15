using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Map.Assets;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Tests;

// A valid warm local cache made from the owner's pixels. No ScalePixels is needed:
// the Mac's headless native library cannot prepare all of the production mip ladder.
internal static class ColorCacheFixture
{
    public static (AssetRecord Record, StampDerivedAssetStore.Header Header) Seed(AssetCatalog catalog, string cache, int fixture = 1)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Owner fixture root was not found.");
        var record = catalog.Import(Path.Combine(root.FullName, "tests", "fixtures", "assets", $"owner-mountain-{fixture:00}.png"),
            new(), DateTimeOffset.UnixEpoch);
        using var image = SKImage.FromEncodedData(catalog.SourcePath(record)!)!;
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            throw new InvalidOperationException("Owner fixture pixels could not be read.");
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0); writer.Write(image.Width); writer.Write(image.Height);
            writer.Write(bitmap.Bytes.Length); writer.Write(bitmap.Bytes);
        }
        var bytes = raw.ToArray();
        var stamp = StampDerivedAssetStore.FileStamp.Read(catalog.SourcePath(record))!;
        var header = new StampDerivedAssetStore.Header(1, record.Sha256, image.Width, image.Height,
            (uint)SceneRenderer.StampOverviewColor(image), stamp, stamp, 0, 1, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var stem = Path.Combine(cache, "v1", record.Sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        using (var file = File.Create(stem + ".pixels"))
        using (var compressed = new BrotliStream(file, CompressionLevel.Fastest)) compressed.Write(bytes);
        File.WriteAllBytes(stem + ".json", JsonSerializer.SerializeToUtf8Bytes(header));
        return (record, header);
    }
}
