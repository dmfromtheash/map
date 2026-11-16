using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Rendering;
using Map.Tests;

namespace Map.Presentation.Tests;

/// <summary>
/// The whole-map chunk pictures live next to the prepared pixels (owner 2026-11-16, «кеш всегда горячий»): the
/// catalog source with a machine cache hands the overview worker its disk tier and names each asset's pixels by the
/// remembered source stamps, so a later run - another store reading the same folder - names them the same; the
/// derived-cache janitor, which owns only its v1..vN folders, leaves the chunk folder alone.
/// </summary>
public sealed class OverviewChunkStoreWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-overview-wiring", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static MapDocument Empty() => new(DocumentId.New(), "wiring", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"), [new BaseColorLayer(LayerId.New(), "Base", true, false, 1, "#FFFFFFFF")], []);

    [Fact]
    public async Task The_catalog_source_keeps_chunks_under_its_derived_root_and_names_pixels_across_runs()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var derived = Path.Combine(_root, "derived");
        var (record, _) = ColorCacheFixture.Seed(catalog, derived);
        string? first;
        await using (var writer = new StampDerivedAssetStore(catalog, derived, startWatcher: _ => { }))
        {
            Assert.NotNull(await writer.Request(record.Sha256));
            first = writer.PixelIdentity(record.Sha256);
        }
        Assert.NotNull(first);
        await using (var other = new StampDerivedAssetStore(catalog, Path.Combine(_root, "other"), startWatcher: _ => { }))
            Assert.Null(other.PixelIdentity(record.Sha256));

        // A second run: the identity comes from the remembered colours file alone, without any header read.
        await using (var again = new StampDerivedAssetStore(catalog, derived, startWatcher: _ => { }))
        {
            Assert.Equal(first, again.PixelIdentity(record.Sha256));
            Assert.Equal(0, again.MetadataEntries);
        }

        await using var source = new CatalogStampImageSource(catalog, Empty, () => null, derivedRoot: derived);
        var store = Assert.IsType<OverviewChunkDiskCache>(source.OverviewChunks);
        Assert.Equal(Path.Combine(derived, OverviewChunkDiskCache.FolderName, "v1"), store.Folder);
        Assert.Equal(first, source.PixelIdentity(record.Sha256));
        await using var plain = new CatalogStampImageSource(catalog, Empty, () => null);
        Assert.Null(plain.OverviewChunks);
        Assert.Null(plain.PixelIdentity(record.Sha256));

        var key = new byte[32];
        key[0] = 7;
        store.Save(key, 2, 2, new byte[16]);
        Assert.True(store.Flush(TimeSpan.FromSeconds(10)));
        var file = Path.Combine(store.Folder, Convert.ToHexStringLower(key) + ".chunk");
        Assert.True(File.Exists(file));
        var report = await new DerivedCacheJanitor(derived, new DerivedCacheJanitorOptions { MaximumBytes = 1, MinimumFreeBytes = 0 })
            .CleanAsync();
        Assert.True(report.LockAcquired);
        Assert.True(File.Exists(file), "the janitor deleted a chunk picture it does not own");
        using var image = store.TryLoad(key, 2, 2);
        Assert.NotNull(image);
    }
}
