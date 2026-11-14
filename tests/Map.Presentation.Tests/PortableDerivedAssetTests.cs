using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Map.Assets;
using Map.Performance;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class PortableDerivedAssetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-portable-tests", Guid.NewGuid().ToString("N"));

    private static AssetRecord ImportOwner(AssetCatalog catalog, int number = 1)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Map.slnx"))) root = root.Parent;
        return catalog.Import(Path.Combine(root!.FullName, "tests", "fixtures", "assets", $"owner-mountain-{number:00}.png"), new(), DateTimeOffset.UnixEpoch);
    }

    private async Task<JsonElement> RunProbe(string catalog, bool dryRun = false)
    {
        var options = new PerformanceOptions("derived-build", 1, 1, 1, 5, Path.Combine(_root, "report.json"))
        { Catalogs = [catalog], DryRun = dryRun };
        var report = JsonSerializer.SerializeToElement(await DerivedBuildProbe.RunAsync(options));
        Assert.Empty(Directory.GetDirectories(_root, "derived-build-work-*"));
        return report.GetProperty("catalogs")[0];
    }

    [Theory]
    [InlineData("current")]
    [InlineData("missing")]
    [InlineData("source-time")]
    [InlineData("manifest")]
    [InlineData("pixels")]
    [InlineData("truncated")]
    public async Task Portable_derived_probe_skips_only_current_assets_and_dry_run_does_not_write(string state)
    {
        var root = Path.Combine(_root, "catalog");
        string sha;
        string path;
        await using (var catalog = new AssetCatalog(root))
        {
            var record = ImportOwner(catalog);
            sha = record.Sha256;
            await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache"));
            path = await store.BuildPortableAsync(sha);
            Assert.True(store.HasCurrentPortable(sha));
            if (state == "missing") File.Delete(path);
            else if (state == "source-time")
            {
                var source = catalog.SourcePath(record)!;
                File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(2));
            }
            else if (state is "manifest" or "pixels" or "truncated")
            {
                var bytes = File.ReadAllBytes(path);
                if (state == "manifest") bytes[44] ^= 1;
                else if (state == "pixels") bytes[44 + BitConverter.ToInt32(bytes, 8)] ^= 0x7f;
                else bytes = bytes[..^3];
                File.WriteAllBytes(path, bytes);
            }
            Assert.Equal(state == "current", store.HasCurrentPortable(sha));
            Assert.False(store.HasCurrentPortable(new string('0', 64)));
            Assert.Equal(0, store.Failures);
            await catalog.FlushMetadataAsync();
        }
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var written = File.GetLastWriteTimeUtc(path);
        var current = state == "current";
        var dry = await RunProbe(root, dryRun: true);
        Assert.Equal(current ? 0 : 1, dry.GetProperty("built").GetInt32());
        Assert.Equal(current ? 1 : 0, dry.GetProperty("skipped").GetInt32());
        Assert.Equal(0, dry.GetProperty("failed").GetInt32());
        Assert.Equal(0, dry.GetProperty("bytes").GetInt64());
        Assert.Equal(before, File.Exists(path) ? File.ReadAllBytes(path) : null);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        var result = await RunProbe(root);
        Assert.Equal(1, result.GetProperty("images").GetInt32());
        Assert.Equal(current ? 0 : 1, result.GetProperty("built").GetInt32());
        Assert.Equal(current ? 1 : 0, result.GetProperty("skipped").GetInt32());
        Assert.Equal(0, result.GetProperty("failed").GetInt32());
        Assert.Equal(current ? 0 : new FileInfo(path).Length, result.GetProperty("bytes").GetInt64());
        Assert.True(result.GetProperty("elapsedMs").GetDouble() >= 0);
        Assert.Empty(result.GetProperty("failures").EnumerateArray());
        if (current) Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        await using var reopened = new AssetCatalog(root);
        await using var reader = new StampDerivedAssetStore(reopened, Path.Combine(_root, "read-cache"));
        Assert.True(reader.HasCurrentPortable(sha));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    public async Task Portable_derived_probe_reports_bad_sha_and_continues_with_the_next_asset(string damage)
    {
        var root = Path.Combine(_root, "catalog");
        string bad;
        string good;
        await using (var catalog = new AssetCatalog(root))
        {
            var records = new[] { ImportOwner(catalog), ImportOwner(catalog, 2) }
                .OrderBy(record => record.Sha256, StringComparer.Ordinal).ToArray();
            bad = records[0].Sha256;
            good = records[1].Sha256;
            await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache"));
            await store.BuildPortableAsync(bad);
            var source = catalog.SourcePath(records[0])!;
            if (damage == "missing") File.Delete(source);
            else
            {
                File.Copy(catalog.SourcePath(records[1])!, source, overwrite: true);
                File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
            }
            Assert.False(store.HasCurrentPortable(bad));
            await catalog.FlushMetadataAsync();
        }
        var result = await RunProbe(root);
        Assert.Equal(2, result.GetProperty("images").GetInt32());
        Assert.Equal(1, result.GetProperty("built").GetInt32());
        Assert.Equal(0, result.GetProperty("skipped").GetInt32());
        Assert.Equal(1, result.GetProperty("failed").GetInt32());
        var error = Assert.Single(result.GetProperty("failures").EnumerateArray());
        Assert.Equal(bad, error.GetProperty("sha").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("reason").GetString()));
        await using var reopened = new AssetCatalog(root);
        await using var reader = new StampDerivedAssetStore(reopened, Path.Combine(_root, "read-cache"));
        Assert.False(reader.HasCurrentPortable(bad));
        Assert.True(reader.HasCurrentPortable(good));
    }

    [Fact]
    public async Task Moving_a_catalog_keeps_portable_pixels_exact_with_an_empty_machine_cache()
    {
        var original = Path.Combine(_root, "original");
        AssetRecord record;
        await using (var catalog = new AssetCatalog(original))
        {
            record = ImportOwner(catalog);
            await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache"));
            await store.BuildPortableAsync(record.Sha256);
            await catalog.FlushMetadataAsync();
        }
        var moved = Path.Combine(_root, "moved");
        Directory.Move(original, moved);
        await using var reopened = new AssetCatalog(moved);
        await using var reader = new StampDerivedAssetStore(reopened, Path.Combine(_root, "empty-cache"));
        var header = (await reader.Request(record.Sha256))!;
        Assert.NotNull(header.PortablePath);
        Assert.StartsWith(moved, header.Original.Path, StringComparison.OrdinalIgnoreCase);
        var before = File.GetLastWriteTimeUtc(header.PortablePath);
        using var expected = new AssetImageCache();
        expected.Get(record.Sha256, reopened.SourcePath(record));
        foreach (var level in new[] { 0, 1, 2, 3, 5, 8 })
        {
            await reader.RequestPixels(header, 0, level).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reader.TryTakePixels(new(record.Sha256, 0, level), out var ready));
            using var image = ready!.Image;
            Assert.NotNull(image);
            using var actual = SKBitmap.FromImage(image);
            using var baseline = SKBitmap.FromImage(expected.GetForPixelSize(record.Sha256,
                record.Width / Math.Pow(2, level), record.Height / Math.Pow(2, level))!);
            Assert.Equal(baseline.Bytes, actual.Bytes);
        }
        Assert.Equal(0, reader.Builds);
        Assert.Equal(0, reader.Failures);
        Assert.Equal(before, File.GetLastWriteTimeUtc(header.PortablePath));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("pixels")]
    [InlineData("truncated")]
    public async Task Corrupt_portable_data_recovers_without_writing_the_library(string damage)
    {
        using var errors = new StringWriter();
        using var listener = new System.Diagnostics.TextWriterTraceListener(errors);
        System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog);
        string path;
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache")))
            path = await builder.BuildPortableAsync(record.Sha256);
        var corrupted = File.ReadAllBytes(path);
        if (damage == "manifest") corrupted[44] ^= 1;
        else if (damage == "truncated") corrupted = corrupted[..^3];
        else
        {
            var headerLength = BitConverter.ToInt32(corrupted, 8);
            using var json = JsonDocument.Parse(corrupted.AsMemory(44, headerLength));
            var entry = json.RootElement.GetProperty("Entries")[0];
            corrupted[44 + headerLength + entry.GetProperty("Offset").GetInt32()] ^= 0x7f;
        }
        File.WriteAllBytes(path, corrupted);
        await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(_root, "empty-cache"));
        var header = (await reader.Request(record.Sha256))!;
        Assert.NotNull(header);
        await reader.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reader.TryTakePixels(new(record.Sha256, 0, 0), out var ready));
        using var image = ready!.Image;
        Assert.True(image is not null, errors.ToString());
        using var baselineCache = new AssetImageCache();
        using var actual = SKBitmap.FromImage(image);
        using var baseline = SKBitmap.FromImage(baselineCache.Get(record.Sha256, catalog.SourcePath(record))!);
        Assert.Equal(baseline.Bytes, actual.Bytes);
        Assert.Equal(corrupted, File.ReadAllBytes(path));
        }
        finally { System.Diagnostics.Trace.Listeners.Remove(listener); }
    }

    [Fact]
    public async Task Portable_ladder_keeps_each_step_pixels_and_invalidates_when_a_later_step_changes()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var owner = ImportOwner(catalog);
        using var original = SKBitmap.Decode(catalog.SourcePath(owner));
        using var surface = SKSurface.Create(new SKImageInfo(2049, 1025, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var image = SKImage.FromBitmap(original)) surface.Canvas.DrawImage(image, new SKRect(0, 0, 2049, 1025));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        var scaledPath = Path.Combine(_root, "owner-scaled.png");
        File.WriteAllBytes(scaledPath, encoded.ToArray());
        var record = catalog.Import(scaledPath, new(), DateTimeOffset.UnixEpoch);
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "build-cache")))
            await builder.BuildPortableAsync(record.Sha256);
        await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(_root, "empty-cache"));
        var header = (await reader.Request(record.Sha256))!;
        Assert.NotNull(header.PortablePath);
        foreach (var (step, level) in new[] { (1, 0), (2, 0), (2, 3) })
        {
            Assert.True(catalog.TryDetailLevelPath(record, step, out var ladder));
            using var expected = new AssetImageCache();
            var source = expected.Get("step", ladder)!;
            await reader.RequestPixels(header, step, level).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reader.TryTakePixels(new(record.Sha256, step, level), out var ready));
            using var actual = ready!.Image!;
            using var actualBitmap = SKBitmap.FromImage(actual);
            using var baseline = SKBitmap.FromImage(expected.GetForPixelSize("step", source.Width / Math.Pow(2, level), source.Height / Math.Pow(2, level))!);
            Assert.Equal(baseline.Bytes, actualBitmap.Bytes);
        }
        Assert.Equal(0, reader.Builds);
        Assert.Equal(0, reader.Failures);
        Assert.True(catalog.TryDetailLevelPath(record, 2, out var changed));
        var generation = reader.Generation;
        File.SetLastWriteTimeUtc(changed!, File.GetLastWriteTimeUtc(changed!).AddSeconds(1));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (reader.Generation == generation && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(reader.Generation > generation);
        // Metadata-only changes reverify the exact bytes instead of rejecting a moved library.
        Assert.NotNull((await reader.Request(record.Sha256))!.PortablePath);
    }

    [Fact]
    public async Task Missing_portable_is_a_quiet_miss()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog);
        await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(_root, "reader"));
        Assert.Null((await reader.Request(record.Sha256))!.PortablePath);
        Assert.Equal(0, reader.Failures);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("first-mip")]
    [InlineData("mip-count")]
    [InlineData("dimensions")]
    [InlineData("offset")]
    [InlineData("length")]
    public async Task Invalid_manifest_recovers_in_machine_cache(string damage)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog);
        string path;
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "builder")))
            path = await builder.BuildPortableAsync(record.Sha256);
        RewriteManifest(path, node =>
        {
            var entry = node["Entries"]![0]!;
            switch (damage)
            {
                case "version": node["Version"] = 99; break;
                case "first-mip": node["Header"]!["FirstMip"] = 16; break;
                case "mip-count": node["Header"]!["MipCount"] = 17; break;
                case "dimensions": entry["Width"] = entry["Width"]!.GetValue<int>() - 1; break;
                case "offset": entry["Offset"] = int.MaxValue; break;
                case "length": entry["Length"] = int.MaxValue; break;
            }
        });
        var before = File.ReadAllBytes(path);
        await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(_root, "reader"));
        Assert.Null((await reader.Request(record.Sha256))!.PortablePath);
        Assert.Equal(1, reader.Builds);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_original_is_not_trusted_even_with_same_size_and_timestamp(bool preserveTimestamp)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog);
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "builder")))
            await builder.BuildPortableAsync(record.Sha256);
        var path = catalog.SourcePath(record);
        var timestamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        if (preserveTimestamp) File.SetLastWriteTimeUtc(path, timestamp);
        await using var reader = new StampDerivedAssetStore(catalog, Path.Combine(_root, "reader"));
        Assert.Null(await reader.Request(record.Sha256));
    }

    [Fact]
    public async Task Copied_source_with_new_timestamp_reuses_portable_after_hash_verification()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog);
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "builder")))
            await builder.BuildPortableAsync(record.Sha256);
        File.SetLastWriteTimeUtc(catalog.SourcePath(record), DateTime.UtcNow.AddDays(-1));
        var cache = Path.Combine(_root, "reader");
        await using (var reader = new StampDerivedAssetStore(catalog, cache))
        {
            Assert.NotNull((await reader.Request(record.Sha256))!.PortablePath);
            Assert.Equal(0, reader.Builds);
            Assert.True(reader.DiskBytes > 0);
        }
        Assert.Single(Directory.GetFiles(cache, "*.trust", SearchOption.AllDirectories));
        await using var reopened = new StampDerivedAssetStore(catalog, cache);
        Assert.NotNull((await reopened.Request(record.Sha256))!.PortablePath);
        Assert.Equal(0, reopened.Builds);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("pixels")]
    [InlineData("brotli-tail")]
    public async Task Broken_portable_after_header_recovers_once_and_repeated_requests_use_local_mips(string damage)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var record = ImportOwner(catalog);
        string path;
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "builder")))
            path = await builder.BuildPortableAsync(record.Sha256);
        var cache = Path.Combine(_root, "reader");
        await using var reader = new StampDerivedAssetStore(catalog, cache, startWatcher: _ => { });
        var header = (await reader.Request(record.Sha256))!;
        Assert.NotNull(header.PortablePath);
        if (damage == "deleted") File.Delete(path);
        else if (damage == "pixels")
        {
            var bytes = File.ReadAllBytes(path);
            bytes[44 + BitConverter.ToInt32(bytes, 8)] ^= 0x7f;
            File.WriteAllBytes(path, bytes);
        }
        else
        {
            var bytes = File.ReadAllBytes(path);
            var length = BitConverter.ToInt32(bytes, 8);
            var node = JsonNode.Parse(bytes.AsSpan(44, length))!;
            var entries = node["Entries"]!.AsArray();
            var firstLength = entries[0]!["Length"]!.GetValue<int>();
            var data = bytes[(44 + length)..].ToList(); data.Insert(firstLength, 0x42);
            entries[0]!["Length"] = firstLength + 1;
            foreach (var entry in entries.Skip(1)) entry!["Offset"] = entry["Offset"]!.GetValue<int>() + 1;
            WritePortable(path, node, data.ToArray());
        }
        await reader.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(reader.TryTakePixels(new(record.Sha256, 0, 0), out var ready));
        Assert.True(ready!.Image is not null || ready.Pixels is not null);
        ready.Image?.Dispose();
        var failures = reader.Failures;
        Assert.Equal(1, failures);
        var repaired = (await reader.Request(record.Sha256))!;
        Assert.Null(repaired.PortablePath);
        Assert.Single(Directory.GetFiles(cache, "*.pixels", SearchOption.AllDirectories));
        // Even a caller retaining the old header must not reopen the broken portable.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await reader.RequestPixels(header, 0, header.FirstMip).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(reader.TryTakePixels(new(record.Sha256, 0, header.FirstMip), out ready));
            Assert.NotNull(ready!.Pixels);
            ready.Image?.Dispose();
        }
        Assert.Equal(failures, reader.Failures);
    }

    private static void RewriteManifest(string path, Action<JsonNode> mutate)
    {
        var bytes = File.ReadAllBytes(path);
        var length = BitConverter.ToInt32(bytes, 8);
        var node = JsonNode.Parse(bytes.AsSpan(44, length))!;
        mutate(node);
        WritePortable(path, node, bytes[(44 + length)..]);
    }

    private static void WritePortable(string path, JsonNode node, byte[] data)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(node);
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write("MAPDRV01"u8); writer.Write(json.Length); writer.Write(SHA256.HashData(json));
        writer.Write(json); writer.Write(data);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
