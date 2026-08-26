using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;

namespace Map.Assets.Tests;

/// <summary>
/// Reliability and cost of the local library: a ten thousand card profile that must stay
/// metadata-only, and the fault paths where a write is interrupted or the index is damaged.
/// </summary>
public sealed class AssetLibraryReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "map-library-reliability", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ten_thousand_logical_sets_load_and_filter_without_decoding_a_single_original()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Seed(catalogRoot, 10_000);

        var loadTimer = Stopwatch.StartNew();
        var catalog = new AssetCatalog(catalogRoot);
        loadTimer.Stop();

        Assert.Equal(10_000, catalog.Sets.Count);
        _ = catalog.Query(new AssetQuery { Text = "card-09999" });

        var samples = new List<double>();
        for (var run = 0; run < 30; run++)
        {
            var timer = Stopwatch.StartNew();
            var result = catalog.Query(new AssetQuery
            {
                Tag = "forest",
                CollectionId = AssetIdentity.CollectionId("even"),
                Sort = AssetSortOrder.NameAscending,
            });
            timer.Stop();
            samples.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(1_000, result.TotalSets);
        }

        // Every tenth card is tagged forest and every tenth index is even, so the collection part
        // of the filter would be invisible above; this proves it is really applied.
        Assert.Empty(catalog.Query(new AssetQuery
        {
            Tag = "forest",
            CollectionId = AssetIdentity.CollectionId("odd"),
        }).Sets);

        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        Assert.True(loadTimer.Elapsed < TimeSpan.FromSeconds(2), $"Load: {loadTimer.Elapsed}");
        Assert.True(p95 <= 100, $"Query P95: {p95:F3} ms");

        // Nothing above ever opened an image: no originals directory was even created.
        Assert.False(Directory.Exists(Path.Combine(catalogRoot, "originals")));
    }

    [Fact]
    public void A_visible_window_over_ten_thousand_sets_realises_only_the_visible_tiles()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Seed(catalogRoot, 10_000);
        var catalog = new AssetCatalog(catalogRoot);
        using var thumbnails = new AssetImageCache(capacity: 64);

        var all = catalog.Query(new AssetQuery { Sort = AssetSortOrder.NameAscending }).Sets;
        var window = AssetTileWindow.Slice(all, firstIndex: 2_500, count: 48);
        foreach (var tile in window.Tiles)
        {
            // Exactly what a realised container does: ask the bounded cache for its own cover.
            _ = thumbnails.Get(tile.CoverSha256, Path.Combine(catalogRoot, tile.CoverThumbnailRelativePath));
        }

        Assert.Equal(10_000, all.Count);
        Assert.Equal(48, window.Count);
        Assert.True(thumbnails.Count <= 64, $"Cache held {thumbnails.Count} images.");
        Assert.True(window.Tiles.Count < all.Count / 100);
    }

    [Fact]
    public void An_index_write_that_cannot_be_validated_leaves_the_previous_index_untouched()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        var indexPath = Path.Combine(catalogRoot, "catalog.json");
        var catalog = new AssetCatalog(catalogRoot);
        catalog.Import(Source("keep", SKColors.Olive), new AssetImportOptions(), DateTimeOffset.UnixEpoch);
        var before = File.ReadAllBytes(indexPath);

        // A document whose set points at a variant that does not exist must never be published.
        var broken = new AssetLibraryDocument
        {
            Sets =
            [
                new AssetSet
                {
                    Id = "set-broken",
                    Name = "broken",
                    VariantIds = ["var-missing"],
                    CoverVariantId = "var-missing",
                    ImportedUtc = DateTimeOffset.UnixEpoch,
                },
            ],
        };

        Assert.Throws<ArgumentException>(() => catalog.ReplaceLibrary(broken));
        Assert.Equal(before, File.ReadAllBytes(indexPath));
        Assert.Empty(Directory.GetFiles(catalogRoot, "catalog.json.tmp-*"));
        Assert.Single(new AssetCatalog(catalogRoot).Sets);
    }

    [Fact]
    public void A_migration_interrupted_before_the_new_index_was_published_still_migrates_on_reopen()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        var indexPath = Path.Combine(catalogRoot, "catalog.json");
        var legacy = JsonSerializer.SerializeToUtf8Bytes(new[] { Legacy(1), Legacy(2) });
        File.WriteAllBytes(indexPath, legacy);

        // The exact state a kill between "copy the old file" and "replace the live file" leaves:
        // a backup on disk and the original v1 index still in place.
        File.WriteAllBytes(Path.Combine(catalogRoot, "catalog.v1.19700101000000000.json"), legacy);
        File.WriteAllBytes(Path.Combine(catalogRoot, "catalog.json.tmp-abandoned"), "{ broken"u8.ToArray());

        var catalog = new AssetCatalog(catalogRoot);

        Assert.Equal(AssetLibraryStatus.Migrated, catalog.Status);
        Assert.Equal(2, catalog.Sets.Count);
        Assert.Equal(2, catalog.Records.Count);
        foreach (var backup in Directory.GetFiles(catalogRoot, "catalog.v1.*.json"))
        {
            Assert.Equal(2, JsonSerializer.Deserialize<List<AssetRecord>>(File.ReadAllBytes(backup))!.Count);
        }
    }

    [Fact]
    public void A_damaged_index_refuses_every_write_and_keeps_its_bytes()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        var indexPath = Path.Combine(catalogRoot, "catalog.json");
        var damaged = "{ \"schemaVersion\": 2, \"sets\": [ {"u8.ToArray();
        File.WriteAllBytes(indexPath, damaged);
        var catalog = new AssetCatalog(catalogRoot);
        var source = Source("new", SKColors.Purple);

        Assert.Equal(AssetLibraryStatus.Damaged, catalog.Status);
        Assert.NotEmpty(catalog.Diagnostic);
        Assert.Throws<InvalidOperationException>(
            () => catalog.Import(source, new AssetImportOptions(), DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidOperationException>(() => catalog.ReplaceIndex([]));
        Assert.Equal(damaged, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public void An_index_written_by_a_newer_schema_is_reported_rather_than_silently_downgraded()
    {
        var catalogRoot = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        var indexPath = Path.Combine(catalogRoot, "catalog.json");
        var future = "{ \"schemaVersion\": 99, \"records\": [], \"sets\": [] }"u8.ToArray();
        File.WriteAllBytes(indexPath, future);

        var catalog = new AssetCatalog(catalogRoot);

        Assert.Equal(AssetLibraryStatus.Damaged, catalog.Status);
        Assert.Contains("99", catalog.Diagnostic);
        Assert.Equal(future, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public void The_catalog_root_is_only_redirected_when_the_sandbox_is_asked_for_unambiguously()
    {
        var requested = Path.Combine(_root, "real");
        var sandbox = Path.Combine(_root, "sandbox");

        Assert.Equal(Path.GetFullPath(requested), AssetCatalogRoots.Resolve(requested));

        using (new EnvironmentScope(AssetCatalogRoots.SandboxRootVariable, sandbox))
        {
            // Without the test-mode switch a stray variable must change nothing.
            Assert.Equal(Path.GetFullPath(requested), AssetCatalogRoots.Resolve(requested));

            using (new EnvironmentScope(AssetCatalogRoots.TestModeVariable, "1"))
            {
                Assert.Equal(Path.GetFullPath(sandbox), AssetCatalogRoots.Resolve(requested));
            }
        }

        using (new EnvironmentScope(AssetCatalogRoots.TestModeVariable, "1"))
        using (new EnvironmentScope(AssetCatalogRoots.SandboxRootVariable, "not-absolute"))
        {
            Assert.Equal(Path.GetFullPath(requested), AssetCatalogRoots.Resolve(requested));
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private static void Seed(string catalogRoot, int count)
    {
        var document = new AssetLibraryDocument();
        AssetLibraryStore.EnsureCollection(document, "even");
        AssetLibraryStore.EnsureCollection(document, "odd");
        for (var index = 0; index < count; index++)
        {
            var pack = index % 2 == 0 ? "even" : "odd";
            var sha = index.ToString("x64");
            document.Records.Add(new AssetRecord
            {
                Sha256 = sha,
                DisplayName = $"card-{index:D5}",
                OriginalFileName = $"card-{index:D5}.png",
                MediaType = "image/png",
                Extension = "png",
                Width = 512,
                Height = 512,
                HasAlpha = true,
                ByteLength = 4_096,
                ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
                Pack = pack,
                Tags = index % 10 == 0 ? ["forest"] : ["other"],
                Provenance = new AssetProvenance(),
                ThumbnailRelativePath = $"thumbnails/{sha}.png",
            });
            var variantId = AssetIdentity.VariantIdForContent(sha);
            document.Variants.Add(new AssetVariant { Id = variantId, Sha256 = sha, Label = $"card-{index:D5}" });
            document.Sets.Add(new AssetSet
            {
                Id = AssetIdentity.SetIdForContent(sha),
                Name = $"card-{index:D5}",
                VariantIds = [variantId],
                CoverVariantId = variantId,
                CollectionId = AssetIdentity.CollectionId(pack),
                Tags = index % 10 == 0 ? ["forest"] : ["other"],
                ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
            });
        }

        new AssetCatalog(catalogRoot).ReplaceLibrary(document);
    }

    private static AssetRecord Legacy(int index) => new()
    {
        Sha256 = index.ToString("x64"),
        DisplayName = $"legacy-{index}",
        OriginalFileName = $"legacy-{index}.png",
        MediaType = "image/png",
        Extension = "png",
        Width = 64,
        Height = 64,
        HasAlpha = true,
        ByteLength = 256,
        ImportedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index),
        Provenance = new AssetProvenance(),
        ThumbnailRelativePath = $"thumbnails/{index:x64}.png",
    };

    private string Source(string name, SKColor color)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".png");
        using var surface = SKSurface.Create(new SKImageInfo(40, 40))!;
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
