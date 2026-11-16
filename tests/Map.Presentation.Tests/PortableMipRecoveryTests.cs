using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Map.Assets;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PortableMipRecoveryTests
{
    [Theory]
    [InlineData("deleted", false)]
    [InlineData("pixels", false)]
    [InlineData("brotli-tail", false)]
    [InlineData("deleted", true)]
    [InlineData("pixels", true)]
    [InlineData("brotli-tail", true)]
    public async Task Old_portable_header_reads_repaired_local_mips_without_preparing_the_source_again(string damage, bool native)
    {
        var root = Path.Combine(Path.GetTempPath(), "map-portable-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var catalog = new AssetCatalog(Path.Combine(root, "catalog"));
            var fixtures = new DirectoryInfo(AppContext.BaseDirectory);
            while (fixtures is not null && !File.Exists(Path.Combine(fixtures.FullName, "Map.slnx"))) fixtures = fixtures.Parent;
            Assert.NotNull(fixtures);
            var record = catalog.Import(Path.Combine(fixtures.FullName, "tests", "fixtures", "assets", "owner-mountain-01.png"),
                new(), DateTimeOffset.UnixEpoch);
            string path;
            // Source bytes never change here. Exclude delayed import FSEvents on macOS;
            // this test exercises portable rejection, not the source watcher.
            await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(root, "builder"), startWatcher: _ => { }))
                path = await builder.BuildPortableAsync(record.Sha256);
            await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(root, "reader"),
                startWatcher: _ => { }, nativeMipImages: native);
            var old = await reader.Request(record.Sha256);
            Assert.NotNull(old);
            Assert.NotNull(old.PortablePath);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (reader.TrustStatistics.Pending > 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
            Assert.Equal(0, reader.TrustStatistics.Pending);
            Damage(path, damage);

            await reader.RequestPixels(old, 0, 0).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reader.TryTakePixels(new(record.Sha256, 0, 0), out var full));
            Assert.True(full!.Image is not null || full.Pixels is not null);
            full.Image?.Dispose();
            Assert.Equal(1, reader.Failures);
            var repaired = await reader.Request(record.Sha256);
            Assert.NotNull(repaired);
            Assert.Null(repaired.PortablePath);
            var expected = reader.ReadMip(repaired, repaired.FirstMip, out var width, out var height);
            Assert.NotNull(expected);
            var before = reader.IoDiagnostics;
            var builds = reader.Builds;
            // A repaired mip must also make progress while an unrelated source decode
            // owns the bounded source lane.
            var sourceGate = (SemaphoreSlim)typeof(StampDerivedAssetStore).GetField("_compactSourceReader",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
            await sourceGate.WaitAsync();
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await reader.RequestPixels(old, 0, old.FirstMip).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.True(reader.TryTakePixels(new(record.Sha256, 0, old.FirstMip), out var mip));
                    Assert.NotNull(mip);
                    var progress = reader.IoDiagnostics;
                    Console.WriteLine($"REQUEST {damage} native={native} attempt={attempt + 1}: local mip reads={progress.LocalMips - before.LocalMips}, source preparations={progress.SourcePreparations - before.SourcePreparations}, managed={mip.Pixels is not null}, image={mip.Image is not null}");
                    Assert.False(mip.Cancelled);
                    Assert.Equal(width, mip.Width);
                    Assert.Equal(height, mip.Height);
                    using var image = mip.Image;
                    if (native)
                    {
                        Assert.NotNull(image);
                        Assert.Null(mip.Pixels);
                        using var pixels = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                        Assert.True(image.ReadPixels(pixels.Info, pixels.GetPixels(), pixels.RowBytes, 0, 0));
                        Assert.Equal(expected, pixels.Bytes);
                    }
                    else Assert.Equal(expected, mip.Pixels);
                }
            }
            finally { sourceGate.Release(); }
            var after = reader.IoDiagnostics;
            Assert.Equal(before.LocalMips + 2, after.LocalMips);
            Assert.Equal(before.SourcePreparations, after.SourcePreparations);
            Assert.Equal(before.PortablePixels, after.PortablePixels);
            Assert.Equal(builds, reader.Builds);
            Assert.Equal(1, reader.Failures);
            Console.WriteLine($"RECOVERY {damage} native={native}: local mip reads={after.LocalMips - before.LocalMips}, source preparations={after.SourcePreparations - before.SourcePreparations}, pixels identical");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Damage(string path, string damage)
    {
        if (damage == "deleted") { File.Delete(path); return; }
        var bytes = File.ReadAllBytes(path);
        var length = BitConverter.ToInt32(bytes, 8);
        if (damage == "pixels")
        {
            bytes[44 + length] ^= 0x7f;
            File.WriteAllBytes(path, bytes);
            return;
        }
        var manifest = JsonNode.Parse(bytes.AsSpan(44, length))!;
        var entries = manifest["Entries"]!.AsArray();
        var firstLength = entries[0]!["Length"]!.GetValue<int>();
        var data = bytes[(44 + length)..].ToList();
        data.Insert(firstLength, 0x42);
        entries[0]!["Length"] = firstLength + 1;
        foreach (var entry in entries.Skip(1)) entry!["Offset"] = entry["Offset"]!.GetValue<int>() + 1;
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("MAPDRV01"u8); writer.Write(json.Length); writer.Write(SHA256.HashData(json));
        writer.Write(json); writer.Write(data.ToArray());
    }
}
