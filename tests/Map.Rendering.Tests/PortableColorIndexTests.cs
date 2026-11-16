using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using SkiaSharp;

namespace Map.Rendering.Tests;

// test-change: the optional library palette must be expendable and obey existing portable trust/invalidation.
public sealed class PortableColorIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-portable-colors-" + Guid.NewGuid().ToString("N"));
    private string Library => Path.Combine(_root, "library");
    private string Cache => Path.Combine(_root, "empty-machine");
    private string Index => Path.Combine(Library, "derived", "v1", "overview-colors.bin");

    [Fact]
    public async Task Explicit_preparation_indexes_existing_portables_and_a_new_machine_needs_no_headers_for_colours()
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 6);
        await using var catalog = new AssetCatalog(Library);
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "builder"), startWatcher: _ => { }))
        {
            Assert.Equal(6, await builder.WritePortableColorIndexAsync(records.Select(r => r.Sha256)));
            Assert.Equal(6, builder.IoDiagnostics.PortableHeaders);
            Assert.Equal(0, builder.IoDiagnostics.SourcePreparations);
            Assert.Equal(0, builder.IoDiagnostics.SourceVerifications);
            Assert.Equal(0, builder.TrustStatistics.Queued);
        }
        Assert.True(File.Exists(Index));
        Assert.False(Directory.Exists(Cache));
        await using (var source = Source(catalog))
        {
            await Colors(source, records);
            Assert.Equal(0, source.DerivedStatistics.MetadataEntries);
            Assert.Equal(0, source.DerivedIoDiagnostics.PortableHeaders);
            Assert.Equal(1, source.DerivedIoDiagnostics.PortableColorIndexReads);
            Assert.Equal(6, source.DerivedIoDiagnostics.PortableColorIndexHits);
            var store = Store(source);
            await Eventually(() => store.TrustStatistics.Pending == 0);
            Assert.Equal(0, store.TrustStatistics.Rejected);
        }
        // Only verified colours may become the existing machine palette on close.
        await using var reopened = new StampDerivedAssetStore(catalog, Cache, startWatcher: _ => { });
        Assert.Equal(6, reopened.KnownColorCount);
        Assert.Equal(default, reopened.IoDiagnostics);
    }

    [Fact]
    public async Task Palette_only_dry_run_does_not_change_existing_index_or_portable_files()
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 6, indexed: true);
        var before = Directory.GetFiles(Path.Combine(Library, "derived", "v1")).ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
        await using var catalog = new AssetCatalog(Library);
        await using (var builder = new StampDerivedAssetStore(catalog, Path.Combine(_root, "dry-run"), startWatcher: _ => { }))
        {
            Assert.Equal(6, await builder.WritePortableColorIndexAsync(records.Select(r => r.Sha256), write: false));
            Assert.Equal(0, builder.TrustStatistics.Queued);
            Assert.Equal(0, builder.Builds);
        }
        foreach (var (path, original) in before)
        {
            Assert.Equal(original.Bytes, File.ReadAllBytes(path));
            Assert.Equal(original.Written, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("truncated")]
    [InlineData("version")]
    public async Task A_broken_index_falls_back_to_valid_per_asset_headers(string damage)
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 6, indexed: true);
        var bytes = File.ReadAllBytes(Index);
        if (damage == "hash") bytes[48] ^= 1;
        else if (damage == "version") bytes[8]++;
        else bytes = bytes[..^1];
        File.WriteAllBytes(Index, bytes);
        await using var catalog = new AssetCatalog(Library);
        await using var source = Source(catalog);
        await Colors(source, records);
        Assert.Equal(6, source.DerivedStatistics.MetadataEntries);
        Assert.Equal(6, source.DerivedIoDiagnostics.PortableHeaders);
        Assert.Equal(0, source.DerivedIoDiagnostics.PortableColorIndexHits);
        Assert.Equal(0, source.DerivedStatistics.Builds);
    }

    [Fact]
    public async Task Copying_a_library_and_changing_its_timestamps_preserves_the_palette()
    {
        var previous = Path.Combine(_root, "before-copy");
        var records = ColdStartPipelineTests.SeedLibrary(previous, 6, indexed: true);
        Directory.Move(previous, Library);
        await using var catalog = new AssetCatalog(Library);
        foreach (var record in records) File.SetLastWriteTimeUtc(catalog.SourcePath(record)!, DateTime.UtcNow.AddDays(-3));
        await using var source = Source(catalog);
        await Colors(source, records);
        Assert.Equal(0, source.DerivedStatistics.MetadataEntries);
        Assert.Equal(6, source.DerivedIoDiagnostics.PortableColorIndexHits);
        await Eventually(() => Store(source).TrustStatistics.Pending == 0);
        Assert.Equal(0, Store(source).TrustStatistics.Rejected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_same_length_changed_source_cannot_persist_an_index_colour(bool preserveTimestamp)
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 1, indexed: true);
        await using var catalog = new AssetCatalog(Library);
        var path = catalog.SourcePath(records[0])!;
        var timestamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        if (preserveTimestamp) File.SetLastWriteTimeUtc(path, timestamp);
        await using (var source = Source(catalog))
        {
            await Colors(source, records); // provisional, exactly as a portable header before its trust check
            await Eventually(() => Store(source).TrustStatistics.Pending == 0);
            Assert.Equal(1, Store(source).TrustStatistics.Rejected);
            Assert.Null(Store(source).KnownColor(records[0].Sha256));
        }
        await using var reopened = new StampDerivedAssetStore(catalog, Cache, startWatcher: _ => { });
        Assert.Null(reopened.KnownColor(records[0].Sha256));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("source-list")]
    [InlineData("sha")]
    public async Task Index_only_colours_are_invalidated_when_the_portable_proof_is_unusable(string damage)
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 1, indexed: true);
        var portable = Path.Combine(Library, "derived", "v1", records[0].Sha256 + ".mapderived");
        if (damage == "missing") File.Delete(portable);
        else
        {
            var bytes = File.ReadAllBytes(portable);
            var oldLength = BitConverter.ToInt32(bytes, 8);
            var manifest = JsonNode.Parse(bytes.AsSpan(44, oldLength))!;
            if (damage == "sha") manifest["Header"]!["Sha"] = new string('f', 64);
            else
            {
                manifest["Sources"]!.AsArray().Clear();
                manifest["SourceHashes"]!.AsArray().Clear();
            }
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
            using var file = File.Create(portable);
            using var writer = new BinaryWriter(file);
            writer.Write("MAPDRV01"u8); writer.Write(json.Length); writer.Write(SHA256.HashData(json));
            writer.Write(json); writer.Write(bytes.AsSpan(44 + oldLength));
        }
        await using var catalog = new AssetCatalog(Library);
        await using var source = Source(catalog);
        await Colors(source, records);
        await Eventually(() => Store(source).TrustStatistics.Pending == 0);
        Assert.Null(Store(source).KnownColor(records[0].Sha256));
    }

    [Fact]
    public async Task Async_close_drains_colour_work_before_the_profile_can_be_deleted()
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 64, indexed: true);
        await using var catalog = new AssetCatalog(Library);
        var source = Source(catalog);
        source.PrepareOverview(records.Select(r => r.Sha256), static () => { });
        await source.DisposeAsync();
        if (Directory.Exists(Cache)) Directory.Delete(Cache, true);
        Assert.True(Store(source).Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task An_unchanged_header_does_not_restart_the_index_palette()
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 1, indexed: true);
        await using var catalog = new AssetCatalog(Library);
        await using var source = Source(catalog);
        await Colors(source, records);
        await Task.Delay(30); // let both palette-completion notifications run before observing stability
        var revision = source.OverviewColorRevision;
        Assert.NotNull(await Store(source).Request(records[0].Sha256));
        Assert.Equal(revision, source.OverviewColorRevision);
    }

    [Fact]
    public async Task A_watched_index_colour_is_invalidated_and_rechecked_through_a_header()
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 1, indexed: true);
        await using var catalog = new AssetCatalog(Library);
        await using var source = Source(catalog);
        await Colors(source, records);
        Assert.Equal(0, source.DerivedStatistics.MetadataEntries);
        var store = Store(source);
        typeof(StampDerivedAssetStore).GetMethod("FileChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [catalog.SourcePath(records[0])]);
        Assert.Null(store.KnownColor(records[0].Sha256));
        await Colors(source, records);
        Assert.Equal(1, source.DerivedStatistics.MetadataEntries);
    }

    [Fact]
    public async Task First_view_raster_work_defers_source_hashes_without_blocking_headers_or_mips()
    {
        var records = ColdStartPipelineTests.SeedLibrary(Library, 6, indexed: true);
        await using var catalog = new AssetCatalog(Library);
        await using var source = Source(catalog);
        source.SetViewRefining(true);
        await Colors(source, records);
        var store = Store(source);
        var header = await store.Request(records[0].Sha256).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(header);
        await store.RequestPixels(header, 0, 4).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.TryTakePixels(new(header.Sha, 0, 4), out var mip));
        Assert.NotNull(mip!.Image);
        mip.Image.Dispose();
        await Task.Delay(350); // past the normal 250 ms idle delay; the first raster is still pending
        Assert.Equal(0, store.IoDiagnostics.SourceVerifications);
        Assert.Equal(6, store.TrustStatistics.Pending);
        source.SetViewRefining(false);
        await Eventually(() => store.TrustStatistics.Pending == 0);
        Assert.Equal(6, store.IoDiagnostics.SourceVerifications);
        Assert.Equal(0, store.TrustStatistics.Rejected);
    }

    private CatalogStampImageSource Source(AssetCatalog catalog)
    {
        var doc = new MapDocument(DocumentId.New(), "colours", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF")]);
        return new(catalog, () => doc, () => null, derivedRoot: Cache, startWatcher: _ => { });
    }

    private static async Task Colors(CatalogStampImageSource source, AssetRecord[] records)
    {
        await Eventually(() => source.PrepareOverview(records.Select(r => r.Sha256), static () => { }));
        foreach (var record in records) Assert.NotNull(source.TryGetOverviewColor(record.Sha256));
    }

    private static StampDerivedAssetStore Store(CatalogStampImageSource source) =>
        (StampDerivedAssetStore)typeof(CatalogStampImageSource).GetField("_derived", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(5);
        Assert.True(condition());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
