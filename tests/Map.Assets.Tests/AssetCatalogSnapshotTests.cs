using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace Map.Assets.Tests;

public sealed class AssetCatalogSnapshotTests(ITestOutputHelper output) : IDisposable
{
    // Keep every generated catalog inside this worktree, including the metadata-only large one.
    private readonly string _temp = Path.Combine(AppContext.BaseDirectory, "snapshot-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Own_and_routed_favorite_edits_with_40000_drive_records_take_less_than_50_ms()
    {
        // Keep each JSON within the existing per-catalog byte/token budget. Together the two
        // drive catalogs contain 40 000 records, variants and cards (no original image files).
        string[] driveRoots = [Seed("drive-a", 20_000), Seed("drive-b", 20_000, start: 20_000)];
        await using var own = new AssetCatalog(Seed("own", 1, start: 50_000));
        var ownSet = Assert.Single(own.Sets);
        // Warm the same mutation/routing code with a small, separate catalog, not the measured drive.
        var warmRoot = Seed("warm", 1, start: 60_000);
        own.SetMounts([warmRoot]);
        own.SetSetFavorite(ownSet.Id, true);
        own.SetSetFavorite("set-60000", true);
        await own.FlushMetadataAsync();
        own.SetMounts(driveRoots);
        Assert.Equal(2, own.Mounts.Count);
        Assert.Equal(40_001, own.Records.Count);

        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var ownTimer = Stopwatch.StartNew();
        own.SetSetFavorite(ownSet.Id, false);
        ownTimer.Stop();
        var ownBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var driveTimer = Stopwatch.StartNew();
        own.SetSetFavorite("set-39999", true);
        driveTimer.Stop();
        output.WriteLine($"40 000 drive records: own {ownTimer.Elapsed.TotalMilliseconds:F3} ms, " +
            $"routed {driveTimer.Elapsed.TotalMilliseconds:F3} ms; own allocation {ownBytes} bytes.");

        Assert.True(ownTimer.Elapsed.TotalMilliseconds < 50, $"Own mutation: {ownTimer.Elapsed.TotalMilliseconds:F3} ms");
        // A routed favourite is written to that drive's index before the call returns (the owner's choice
        // must survive a restart), so it includes one write of a 20 000-card index; the bound only rules
        // out rebuilding the merged 40 000-card view, not the disk write itself.
        Assert.True(driveTimer.Elapsed.TotalMilliseconds < 1000, $"Routed favorite: {driveTimer.Elapsed.TotalMilliseconds:F3} ms");
        // This catches a merged rebuild even on a fast machine where it happens to take <50 ms.
        Assert.True(ownBytes < 128_000, $"Own mutation allocated {ownBytes} bytes for one local card.");
        Assert.False(own.FindSet(ownSet.Id)!.Favorite);
        Assert.True(own.FindSet("set-39999")!.Favorite);
        Assert.True(own.Find(39_999.ToString("x64"))!.Favorite);
        await own.FlushMetadataAsync();
        await using var reopened = new AssetCatalog(driveRoots[1]);
        Assert.True(reopened.FindSet("set-39999")!.Favorite);
        Assert.All(driveRoots, root => Assert.False(Directory.Exists(Path.Combine(root, "originals"))));
    }

    [Fact]
    public async Task Reader_can_resolve_while_writer_attaches_detaches_and_imports()
    {
        await using var drive = AssetCatalog.OpenMounted(Seed("drive", 500))!;
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        var first = own.Import(Fixture(1), new(), DateTimeOffset.UnixEpoch);
        var loops = 0;
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                Assert.NotNull(own.Find(first.Sha256));
                Assert.Equal(own.Root, own.RootFor(first));
                var sha = (loops % 500).ToString("x64");
                if (own.Find(sha) is { } record)
                {
                    Assert.Contains(own.RootFor(record), new[] { own.Root, drive.Root });
                    _ = own.ThumbnailPath(record);
                    _ = own.TryThumbnailPath(record, out _);
                    _ = own.TryDetailLevelPath(record, 1, out _);
                    _ = own.ReadVerified(record); // synthetic records have no originals
                }
                var variant = own.FindVariant($"var-{loops % 500}");
                if (variant is not null) _ = own.RecordForVariant(variant.Id);
                _ = own.RecordForVariant(AssetIdentity.VariantIdForContent(first.Sha256));
                Interlocked.Increment(ref loops);
            }
        });

        started.Wait();
        try
        {
            for (var i = 0; i < 24; i++)
            {
                own.AttachMounts([drive]);
                own.Import(Fixture(2 + i % 3), new(), DateTimeOffset.UnixEpoch.AddSeconds(i));
                own.ImportSets([new AssetSetImport
                {
                    StableKey = "concurrent", Name = "Горы", Paths = [Fixture(1), Fixture(2)],
                    Labels = ["Первая", "Вторая"],
                }], DateTimeOffset.UnixEpoch);
                own.AttachMounts([]);
            }
        }
        finally
        {
            stop.Cancel();
            await reader; // propagate every reader exception, including assertion failures
        }
        output.WriteLine($"Concurrent reader: {loops} iterations, 24 attach/import/detach cycles.");
        Assert.True(loops > 0);
        Assert.Equal(4, own.Records.Count);
    }

    [Fact]
    public async Task QueryAsync_captures_the_callers_view_before_any_later_mutation()
    {
        await using var drive = AssetCatalog.OpenMounted(Seed("drive", 20))!;
        await using var own = new AssetCatalog(Seed("own", 1, start: 100));
        own.AttachMounts([drive]);
        var oldRecords = own.Records;
        Assert.Same(oldRecords, own.Records); // merged view is cached within its snapshot
        var before = own.QueryAsync(new(), CancellationToken.None);
        var noFavorites = own.QueryAsync(new() { FavoritesOnly = true }, CancellationToken.None);
        own.SetSetFavorite("set-0", true);
        own.AttachMounts([]);
        Assert.Equal(21, (await before).TotalSets);
        Assert.Empty((await noFavorites).Sets);
        Assert.Equal(21, oldRecords.Count);
        Assert.Single(own.Records);
    }

    [Fact]
    public async Task Layered_lookups_keep_own_then_first_mount_precedence_for_every_identity()
    {
        var firstRoot = Seed("first", 2);
        var secondRoot = Seed("second", 2);
        await using var own = new AssetCatalog(Seed("own", 1));
        own.SetMounts([firstRoot, secondRoot]);
        Assert.Equal(2, own.Records.Count);
        Assert.Equal(2, own.Sets.Count);
        var ownRecord = own.Find(0.ToString("x64"))!;
        Assert.Equal(own.Root, own.RootFor(ownRecord));
        Assert.Null(own.MountOfSet("set-0"));
        Assert.Equal(firstRoot, own.RootFor(own.RecordForVariant("var-1")!));
        Assert.Equal(firstRoot, own.MountOfSet("set-1")!.Root);
        own.SetSetFavorite("set-1", true);
        Assert.True(own.Mounts[0].FindSet("set-1")!.Favorite);
        Assert.False(own.Mounts[1].FindSet("set-1")!.Favorite);
        own.SetMounts([secondRoot]);
        Assert.False(own.FindSet("set-1")!.Favorite);
        Assert.Equal(secondRoot, own.RootFor(own.RecordForVariant("var-1")!));
    }

    [Fact]
    public async Task Failed_import_keeps_published_lists_and_lookups_unchanged()
    {
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        var first = own.Import(Fixture(1), new(), DateTimeOffset.UnixEpoch);
        var records = own.Records;
        var sets = own.Sets;
        Assert.Throws<FileNotFoundException>(() => own.ImportSets([new AssetSetImport
        {
            StableKey = "failed", Name = "Набор", Paths = [Fixture(2), Path.Combine(_temp, "missing.png")],
            Labels = ["Первая", "Вторая"],
        }], DateTimeOffset.UnixEpoch));
        Assert.Same(records, own.Records);
        Assert.Same(sets, own.Sets);
        Assert.Same(first, own.Find(first.Sha256));
        Assert.Single(own.Records);
        Assert.Single(own.Variants);
    }

    [Fact]
    public async Task Favorite_write_failure_rolls_back_published_favorite_and_record_together()
    {
        await using var own = new AssetCatalog(Seed("own", 1));
        var before = own.FindSet("set-0");
        // The owner's library writes a favourite before returning: a failed write is reported at once
        // and the card and its record stay as they were.
        using (var held = new FileStream(Path.Combine(own.Root, "catalog.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => own.SetSetFavorite("set-0", true));
        }
        Assert.False(own.FindSet("set-0")!.Favorite);
        Assert.False(own.Find(0.ToString("x64"))!.Favorite);
        own.SetSetFavorite("set-0", true); // a retry clears the writer error
        await own.FlushMetadataAsync();
        Assert.True(own.FindSet(before!.Id)!.Favorite);
    }

    [Fact]
    public async Task Mounted_favorite_failure_refreshes_the_parent_layer_after_rollback()
    {
        var driveRoot = Seed("drive", 1);
        await using var own = new AssetCatalog(Path.Combine(_temp, "own"));
        own.SetMounts([driveRoot]);
        var mount = Assert.Single(own.Mounts);
        using (var held = new FileStream(Path.Combine(driveRoot, "catalog.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            own.SetSetFavorite("set-0", true);
            var revision = own.ContentRevision;
            Assert.True(own.FindSet("set-0")!.Favorite);
            await own.FlushMetadataAsync(); // existing policy: mounted IO failures are reported by the mount
            Assert.True(own.ContentRevision > revision);
            Assert.NotNull(mount.MetadataSaveError);
        }
        Assert.False(own.FindSet("set-0")!.Favorite);
        Assert.False(own.Find(0.ToString("x64"))!.Favorite);
        own.SetSetFavorite("set-0", true);
        await own.FlushMetadataAsync();
        Assert.Null(mount.MetadataSaveError);
        Assert.True(own.FindSet("set-0")!.Favorite);
    }

    private string Seed(string name, int count, int start = 0)
    {
        var root = Path.Combine(_temp, name);
        Directory.CreateDirectory(root);
        var document = new AssetLibraryDocument();
        for (var i = start; i < start + count; i++)
        {
            var sha = i.ToString("x64");
            document.Records.Add(new AssetRecord
            {
                Sha256 = sha, DisplayName = $"Гора {i}", OriginalFileName = $"mountain-{i}.png",
                MediaType = "image/png", Extension = "png", Width = 64, Height = 64,
                HasAlpha = true, ByteLength = 256, ImportedUtc = DateTimeOffset.UnixEpoch,
                Provenance = new(), StoredRelativePath = $"originals/{sha}.png",
                ThumbnailRelativePath = $"thumbnails/{sha}.png",
            });
            document.Variants.Add(new() { Id = $"var-{i}", Sha256 = sha, Label = $"Гора {i}" });
            document.Sets.Add(new()
            {
                Id = $"set-{i}", Name = $"Гора {i}", VariantIds = [$"var-{i}"],
                CoverVariantId = $"var-{i}", ImportedUtc = DateTimeOffset.UnixEpoch,
            });
        }
        // Generate JSON directly: setup never invokes the slow import pipeline or creates images.
        File.WriteAllBytes(Path.Combine(root, "catalog.json"), JsonSerializer.SerializeToUtf8Bytes(document, AssetLibraryStore.JsonOptions));
        return root;
    }

    private static string Fixture(int index)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Map.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "assets", $"owner-mountain-0{index}.png");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }
}
