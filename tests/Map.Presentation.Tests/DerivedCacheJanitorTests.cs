using Map.Presentation.Services;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

public sealed class DerivedCacheJanitorTests(ITestOutputHelper output) : IDisposable
{
    // The policy is in binary GiB, the unit Windows Explorer shows as «ГБ» (PreparedDataBudget).
    private const long GB = 1024L * 1024 * 1024;
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "derived-janitor-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_sandbox, "derived");
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(100, 0, 5)]
    [InlineData(20, 0, 2)]
    [InlineData(10, 0, 0)]
    [InlineData(9, 5, 0.9)]
    [InlineData(9, 0.5, 0)]
    [InlineData(0, 20, 0)]
    public void Default_ceiling_honours_absolute_fraction_and_free_space_reserve(double free, double cache, double expected)
    {
        var actual = DerivedCacheJanitor.CalculateCeiling(new(), (long)(free * GB), (long)(cache * GB));
        Assert.Equal((long)(expected * GB), actual);
    }

    [Fact]
    public void Ceiling_uses_options_without_overflow_and_validates_them()
    {
        var options = new DerivedCacheJanitorOptions { MaximumBytes = 400, MinimumFreeBytes = 1000, FreeSpaceFraction = .1m };
        Assert.Equal(400, DerivedCacheJanitor.CalculateCeiling(options, 10000));
        Assert.Equal(200, DerivedCacheJanitor.CalculateCeiling(options, 2000));
        Assert.Equal(50, DerivedCacheJanitor.CalculateCeiling(options, 990, 60));
        Assert.Equal(0, DerivedCacheJanitor.CalculateCeiling(options, 990, 5));
        Assert.Equal(long.MaxValue, DerivedCacheJanitor.CalculateCeiling(options with
            { MaximumBytes = long.MaxValue, FreeSpaceFraction = 1 }, long.MaxValue, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => DerivedCacheJanitor.CalculateCeiling(options, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DerivedCacheJanitor(Root, options with { FreeSpaceFraction = 2 }));
    }

    [Fact]
    public async Task Actual_drive_info_is_used_on_the_background_worker()
    {
        Pair(1, 1, 20);
        var report = await new DerivedCacheJanitor(Root).CleanAsync();
        Assert.True(report.LockAcquired);
        Assert.True(report.ScanComplete);
        Assert.Empty(report.Errors);
        Assert.True(report.AvailableFreeBytes > 0);
        Assert.Equal(DerivedCacheJanitor.CalculateCeiling(new(), report.AvailableFreeBytes, 20), report.CeilingBytes);
        output.WriteLine($"DriveInfo free={report.AvailableFreeBytes}, ceiling={report.CeilingBytes}, kept={report.BytesKept}");
    }

    [Theory]
    [InlineData(900, false, 900)]
    [InlineData(902, true, 602)]
    [InlineData(1000, true, 700)]
    public async Task Hysteresis_starts_strictly_above_90_percent_and_stops_at_70(int bytes, bool triggered, long kept)
    {
        for (var i = 1; i <= 9; i++) Pair(1, i, 100, Old.AddMinutes(i));
        if (bytes > 900) Pair(1, 10, bytes - 900, Old.AddDays(1));
        var report = await Cleaner(1000).CleanAsync();
        Assert.Equal(triggered, report.CleanupTriggered);
        Assert.Equal(kept, report.BytesKept);
        Assert.Equal(bytes - kept, report.BytesDeleted);
        Assert.True(report.CeilingSatisfied);
        Assert.Empty(report.Errors);
        AssertPairs();
        // Surviving newest item proves ordering, rather than only byte accounting.
        Assert.True(File.Exists(Stem(1, bytes > 900 ? 10 : 9) + ".json"));
    }

    [Fact]
    public async Task Disk_hit_header_timestamp_keeps_an_old_pixel_file_recent()
    {
        var first = Pair(1, 1, 100, Old);
        Pair(1, 2, 100, Old.AddDays(1));
        File.SetLastWriteTimeUtc(first + ".json", Old.AddDays(2));
        var report = await Cleaner(150).CleanAsync();
        Assert.Equal(100, report.BytesKept);
        Assert.True(File.Exists(first + ".pixels"));
        Assert.False(File.Exists(Stem(1, 2) + ".json"));
        AssertPairs();
    }

    [Fact]
    public async Task Foreign_root_files_folders_nested_files_and_near_matching_names_are_kept()
    {
        Pair(1, 1, 100);
        var foreign = new[]
        {
            Path.Combine(Root, "notes.txt"),
            Path.Combine(Root, new string('a', 64) + ".json"),
            Path.Combine(Root, "foreign", new string('a', 64) + ".pixels"),
            Path.Combine(Root, "v01", new string('b', 64) + ".json"),
            Path.Combine(Root, "v1-backup", new string('b', 64) + ".pixels"),
            Path.Combine(Root, "v1", "foreign", new string('c', 64) + ".json"),
            Path.Combine(Root, "v1", new string('g', 64) + ".json"),
            Path.Combine(Root, "v1", new string('a', 64) + "." + new string('z', 32) + ".tmp"),
            Path.Combine(Root, "v1", new string('a', 64) + "x" + new string('b', 32) + ".tmp"),
            Path.Combine(Root, "v1", new string('a', 64) + ".pixels.bak")
        };
        foreach (var path in foreign) Write(path, 33, Old);
        Pair(2, 2, 200); // Future schema is not ours to interpret or evict.
        var report = await Cleaner(0).CleanAsync();
        Assert.Equal(100, report.BytesBefore);
        Assert.Equal(100, report.BytesDeleted);
        Assert.Equal(0, report.BytesKept);
        Assert.All(foreign, path => Assert.Equal(33, new FileInfo(path).Length));
        Assert.True(File.Exists(Stem(2, 2) + ".pixels"));
    }

    [DerivedCacheSymlinkFact]
    public async Task Directory_file_and_root_links_are_never_followed()
    {
        var outside = Path.Combine(_sandbox, "outside");
        var victim = Path.Combine(outside, new string('a', 64) + ".json");
        Write(victim, 19, Old);
        var outsidePixels = Path.ChangeExtension(victim, ".pixels");
        Write(outsidePixels, 21, Old);
        Directory.CreateDirectory(Root);
        Directory.CreateSymbolicLink(Path.Combine(Root, "v1"), outside);
        var first = await Cleaner(0).CleanAsync();
        Assert.False(first.ScanComplete);
        Assert.Contains(first.Errors, e => e.Contains("Reparse point", StringComparison.Ordinal));
        Directory.Delete(Path.Combine(Root, "v1"));
        Directory.CreateDirectory(Path.Combine(Root, "v1"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "v1", "nested"), outside);
        File.CreateSymbolicLink(Path.Combine(Root, "v1", Path.GetFileName(victim)), victim);
        // A reparse member protects its ordinary counterpart as well.
        Write(Path.Combine(Root, "v1", Path.GetFileName(outsidePixels)), 23, Old);
        var second = await Cleaner(0).CleanAsync();
        Assert.Equal(0, second.FilesDeleted);
        Assert.Equal(23, new FileInfo(Path.Combine(Root, "v1", Path.GetFileName(outsidePixels))).Length);
        Assert.Equal(19, new FileInfo(victim).Length);
        Assert.Equal(21, new FileInfo(outsidePixels).Length);
        var alias = Path.Combine(_sandbox, "alias");
        Directory.CreateSymbolicLink(alias, Root);
        var third = await new DerivedCacheJanitor(Path.Combine(alias, "v1")).CleanAsync();
        Assert.False(third.LockAcquired);
        Assert.False(File.Exists(Path.Combine(Root, "v1", DerivedCacheJanitor.LockFileName)));
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".pixels")]
    public async Task Locked_member_keeps_both_files_and_reports_the_skip(string extension)
    {
        var pair = Pair(1, 1, 100);
        using (var held = new FileStream(pair + extension, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await Cleaner(0).CleanAsync();
            Assert.Equal(0, report.FilesDeleted);
            Assert.Equal(100, report.BytesKept);
            Assert.False(report.CeilingSatisfied);
            Assert.NotEmpty(report.Errors);
            AssertPairs();
        }
        Assert.Equal(2, (await Cleaner(0).CleanAsync()).FilesDeleted);
        AssertPairs();
    }

    [Fact]
    public async Task Readonly_second_member_does_not_split_a_pair()
    {
        var pair = Pair(1, 1, 100);
        File.SetAttributes(pair + ".pixels", FileAttributes.ReadOnly);
        try
        {
            var report = await Cleaner(0).CleanAsync();
            Assert.Equal(0, report.FilesDeleted);
            AssertPairs();
        }
        finally { File.SetAttributes(pair + ".pixels", FileAttributes.Normal); }
    }

    [Fact]
    public async Task Concurrent_cleaners_serialize_and_do_not_double_count_deletions()
    {
        for (var i = 1; i <= 10; i++) Pair(1, i, 100, Old.AddMinutes(i));
        var held = new FileStream(Path.Combine(Root, DerivedCacheJanitor.LockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<DerivedCacheJanitorReport>[] jobs;
        try
        {
            jobs = new[] { Cleaner(400).CleanAsync(), Cleaner(400).CleanAsync() };
            await Task.Delay(80);
            Assert.All(jobs, job => Assert.False(job.IsCompleted));
        }
        finally { held.Dispose(); }
        var reports = await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(reports, r => { Assert.True(r.LockAcquired); Assert.Empty(r.Errors); });
        Assert.Equal(800, reports.Sum(r => r.BytesDeleted));
        Assert.Equal(16, reports.Sum(r => r.FilesDeleted));
        Assert.Equal(200, Directory.GetFiles(Path.Combine(Root, "v1")).Sum(p => new FileInfo(p).Length));
        AssertPairs();
    }

    [Fact]
    public async Task Lock_timeout_is_reported_and_cancellation_interrupts_wait()
    {
        Pair(1, 1, 100);
        using var held = new FileStream(Path.Combine(Root, DerivedCacheJanitor.LockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var options = Options(0) with { LockTimeout = TimeSpan.FromMilliseconds(50) };
        var report = await new DerivedCacheJanitor(Root, options, () => 10000).CleanAsync();
        Assert.False(report.LockAcquired);
        Assert.Contains(report.Errors, e => e.Contains("timeout", StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cleaner(0).CleanAsync(cancellation.Token));
        AssertPairs();
    }

    [Fact]
    public async Task Older_versions_removed_even_without_pressure_but_foreign_contents_protect_the_folder()
    {
        Pair(1, 1, 100);
        Pair(2, 2, 100);
        var foreign = Path.Combine(Root, "v2", "keep.txt");
        Write(foreign, 17, Old);
        Pair(3, 3, 100);
        var report = await Cleaner(1000, version: 3).CleanAsync();
        Assert.False(report.CleanupTriggered);
        Assert.Equal(1, report.VersionFoldersDeleted);
        Assert.False(Directory.Exists(Path.Combine(Root, "v1")));
        Assert.Equal(new[] { foreign }, Directory.GetFiles(Path.Combine(Root, "v2")));
        Assert.Equal(200, report.BytesDeleted);
        Assert.Equal(100, report.BytesKept);
        AssertPairs();
    }

    [Fact]
    public async Task Stale_temp_and_orphan_removed_young_temp_counted_and_its_pair_protected()
    {
        var pair = Pair(1, 1, 100);
        var young = pair + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Write(young, 11, DateTime.UtcNow);
        var stale = Stem(1, 2) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Write(stale, 13, Old);
        Write(Stem(1, 3) + ".json", 17, Old);
        var report = await Cleaner(0).CleanAsync();
        Assert.Equal(2, report.FilesDeleted);
        Assert.Equal(30, report.BytesDeleted);
        Assert.Equal(3, report.FilesKept);
        Assert.Equal(111, report.BytesKept);
        Assert.True(File.Exists(young));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(Stem(1, 3) + ".json"));
        AssertPairs();
    }

    [Fact]
    public async Task Unrecoverable_reserve_shortfall_is_reported_even_after_all_owned_data_is_deleted()
    {
        Pair(1, 1, 100);
        var report = await new DerivedCacheJanitor(Root, Options(1000) with { MinimumFreeBytes = 1000 }, () => 500).CleanAsync();
        Assert.Equal(0, report.CeilingBytes);
        Assert.Equal(0, report.BytesKept);
        Assert.Equal(100, report.BytesDeleted);
        Assert.False(report.ReserveSatisfied);
        Assert.False(report.CeilingSatisfied);
    }

    [Fact]
    public async Task Reserve_deficit_is_recovered_by_deleting_enough_existing_cache()
    {
        Pair(1, 1, 50, Old);
        Pair(1, 2, 50, Old.AddDays(1));
        long Free() => 990 + 100 - Directory.GetFiles(Path.Combine(Root, "v1")).Sum(p => new FileInfo(p).Length);
        var report = await new DerivedCacheJanitor(Root, Options(1000) with { MinimumFreeBytes = 1000 }, Free).CleanAsync();
        Assert.Equal(90, report.CeilingBytes);
        Assert.Equal(63, report.TargetBytes);
        Assert.Equal(50, report.BytesKept);
        Assert.True(report.ReserveSatisfied);
        Assert.True(report.CeilingSatisfied);
        AssertPairs();
    }

    // ---------------------------------------------------------------- DEC-111: order, purge, shared budget

    [Fact]
    public async Task Removed_then_long_unplugged_content_goes_before_least_recently_used()
    {
        Pair(1, 1, 100, Old);                // live, oldest
        Pair(1, 2, 100, Old.AddDays(30));    // removed from its catalog, newest
        Pair(1, 3, 100, Old.AddDays(20));    // library unplugged for weeks
        Pair(1, 4, 100, Old.AddDays(10));    // live
        var classes = new Dictionary<string, DerivedCacheEntryClass>
        {
            [Id(1)] = DerivedCacheEntryClass.Live, [Id(2)] = DerivedCacheEntryClass.Unreferenced,
            [Id(3)] = DerivedCacheEntryClass.UnmountedStale, [Id(4)] = DerivedCacheEntryClass.Live,
        };
        var report = await new DerivedCacheJanitor(Root, Options(300) with { Classify = e => classes[e.Sha] }, () => 10000).CleanAsync();
        Assert.True(report.CleanupTriggered);
        Assert.Equal(1, report.EvictedUnreferenced);
        Assert.Equal(1, report.EvictedUnmounted);
        Assert.Equal(0, report.EvictedLive);
        Assert.True(File.Exists(Stem(1, 1) + ".json"));
        Assert.True(File.Exists(Stem(1, 4) + ".json"));
        Assert.False(File.Exists(Stem(1, 2) + ".json"));
        Assert.False(File.Exists(Stem(1, 3) + ".pixels"));
        AssertPairs();
    }

    [Fact]
    public async Task Classifier_sees_the_original_path_of_the_header_and_is_not_asked_without_pressure()
    {
        var stem = Stem(1, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllText(stem + ".json", """{"Version":1,"Original":{"Path":"X:\\MapLibrary\\refs\\originals\\a.png","Length":1,"WrittenTicks":1}}""");
        File.WriteAllBytes(stem + ".pixels", new byte[50]);
        var asked = new List<string?>();
        DerivedCacheEntryClass Classify(DerivedCacheEntry e) { asked.Add(e.OriginalPath); return DerivedCacheEntryClass.Live; }
        await new DerivedCacheJanitor(Root, Options(100000) with { Classify = Classify }, () => 10000).CleanAsync();
        Assert.Empty(asked);
        await new DerivedCacheJanitor(Root, Options(10) with { Classify = Classify }, () => 10000).CleanAsync();
        Assert.Equal([@"X:\MapLibrary\refs\originals\a.png"], asked);
    }

    [Fact]
    public async Task Purge_removes_exactly_the_selected_entries_whatever_the_ceiling()
    {
        Pair(1, 1, 100); Pair(1, 2, 100); Pair(1, 3, 100);
        var foreign = Path.Combine(Root, "v1", "notes.txt");
        Write(foreign, 7, Old);
        var report = await Cleaner(1_000_000).PurgeAsync(e => e.Sha == Id(2));
        Assert.True(report.LockAcquired);
        Assert.Equal(1, report.PurgedEntries);
        Assert.Equal(2, report.FilesDeleted);
        Assert.Equal(100, report.BytesDeleted);
        Assert.Equal(0, report.PurgeSkipped);
        Assert.Equal(200, report.BytesKept);
        Assert.False(File.Exists(Stem(1, 2) + ".json"));
        Assert.True(File.Exists(Stem(1, 1) + ".pixels"));
        Assert.True(File.Exists(Stem(1, 3) + ".pixels"));
        Assert.True(File.Exists(foreign));
        AssertPairs();
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".pixels")]
    public async Task Purge_keeps_an_entry_a_reader_holds_and_names_it_for_a_retry(string extension)
    {
        var pair = Pair(1, 1, 100);
        Pair(1, 2, 100);
        // The store's readers open with read + delete sharing (ReadCompressedPixels, header reads).
        using (new FileStream(pair + extension, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var held = await Cleaner(0).PurgeAsync(_ => true);
            Assert.Equal(1, held.PurgedEntries);
            Assert.Equal(1, held.PurgeSkipped);
            Assert.Equal([Id(1)], held.SkippedShas);
            Assert.True(File.Exists(pair + ".json"));
            Assert.True(File.Exists(pair + ".pixels"));
            AssertPairs();
        }
        var retry = await Cleaner(0).PurgeAsync(e => e.Sha == Id(1));
        Assert.Equal(1, retry.PurgedEntries);
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "v1")).Where(p => !p.EndsWith(".lock", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Readers_racing_a_purge_see_a_whole_pair_or_none_and_never_crash()
    {
        for (var i = 1; i <= 40; i++) Pair(1, i, 4096);
        using var stop = new CancellationTokenSource();
        var unexpected = new List<Exception>();
        long reads = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var random = new Random();
            while (!stop.IsCancellationRequested)
            {
                var stem = Stem(1, random.Next(1, 41));
                try
                {
                    // Header first, then pixels, the order of the store's readers.
                    using (new FileStream(stem + ".json", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }
                    using var pixels = new FileStream(stem + ".pixels", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    pixels.ReadByte();
                    Interlocked.Increment(ref reads);
                }
                catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) { }
                catch (Exception ex) { lock (unexpected) unexpected.Add(ex); }
            }
        })).ToArray();
        var removed = 0;
        for (var attempt = 0; attempt < 200 && removed < 40; attempt++)
            removed += (await Cleaner(0).PurgeAsync(_ => true)).PurgedEntries;
        stop.Cancel();
        await Task.WhenAll(readers);
        Assert.Empty(unexpected);
        Assert.Equal(40, removed);
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "v1"), "*.json"));
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "v1"), "*.pixels"));
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "v1"), "*.tmp"));
        output.WriteLine($"reads during purge: {reads}");
    }

    [Fact]
    public async Task Shared_budget_share_sets_the_ceiling_and_receives_the_kept_bytes()
    {
        Pair(1, 1, 400, Old); Pair(1, 2, 400, Old.AddDays(1));
        var budget = new Map.Core.Resources.PreparedDataBudget(1000, 1, 0);
        budget.ReportUsage(Map.Core.Resources.PreparedDataBudget.DocumentPyramid, 500);
        var options = Options(long.MaxValue) with { Budget = budget };
        // Pyramid holds 500 of 1000: the assets keep their 60 % guarantee (600), so 800 is over 90 %.
        Assert.Equal(600, DerivedCacheJanitor.CalculateCeiling(options, 10000, 800));
        var report = await new DerivedCacheJanitor(Root, options, () => 10000).CleanAsync();
        Assert.Equal(600, report.CeilingBytes);
        Assert.Equal(400, report.BytesKept);
        Assert.Equal(400, budget.UsageOf(Map.Core.Resources.PreparedDataBudget.DerivedAssets));
        Assert.True(File.Exists(Stem(1, 2) + ".json"));
    }

    [Fact]
    public async Task Default_policy_clamps_by_measured_free_space()
    {
        Pair(1, 1, 100);
        // 30 GiB free: 10 % = 3 GiB is below the 5 GiB ceiling.
        var report = await new DerivedCacheJanitor(Root, new DerivedCacheJanitorOptions(), () => 30 * GB).CleanAsync();
        Assert.Equal(3 * GB, report.CeilingBytes);
        Assert.False(report.CleanupTriggered);
        // 10.5 GiB free: only what keeps 10 GiB free may stay.
        var tight = await new DerivedCacheJanitor(Root, new DerivedCacheJanitorOptions(), () => 10 * GB + GB / 2).CleanAsync();
        Assert.Equal(GB / 2 + 100, tight.CeilingBytes);
    }

    private static string Id(int id) => id.ToString("x64");

    private DerivedCacheJanitor Cleaner(long ceiling, int version = 1) =>
        new(Root, Options(ceiling) with { CurrentVersion = version }, () => 10000);

    private static DerivedCacheJanitorOptions Options(long ceiling) => new()
    {
        MaximumBytes = ceiling, MinimumFreeBytes = 0, FreeSpaceFraction = 1,
        LockTimeout = TimeSpan.FromSeconds(5)
    };

    private string Stem(int version, int id) => Path.Combine(Root, "v" + version, id.ToString("x64"));

    private string Pair(int version, int id, int bytes, DateTime? used = null)
    {
        var stem = Stem(version, id);
        Write(stem + ".json", bytes / 2, used ?? Old);
        Write(stem + ".pixels", bytes - bytes / 2, used ?? Old);
        return stem;
    }

    private static void Write(string path, int bytes, DateTime written)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written);
    }

    private void AssertPairs()
    {
        foreach (var folder in Directory.GetDirectories(Root))
        {
            foreach (var file in Directory.GetFiles(folder, "*.json")) Assert.True(File.Exists(Path.ChangeExtension(file, ".pixels")));
            foreach (var file in Directory.GetFiles(folder, "*.pixels")) Assert.True(File.Exists(Path.ChangeExtension(file, ".json")));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true);
    }
}

// xUnit v2 has no runtime skip support. Probe at discovery so unsupported hosts produce an
// actual skipped test with a reason, rather than a passing test which did no assertions.
public sealed class DerivedCacheSymlinkFactAttribute : FactAttribute
{
    public DerivedCacheSymlinkFactAttribute()
    {
        var probe = Path.Combine(Path.GetTempPath(), "derived-janitor-link-probe-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(probe, "link");
        var fileLink = Path.Combine(probe, "file-link");
        Directory.CreateDirectory(probe);
        try
        {
            Directory.CreateSymbolicLink(link, probe);
            var target = Path.Combine(probe, "target");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(fileLink, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Skip = "defect: symlink cache-safety checks need OS directory/file symlink permission: " + ex.Message; }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (File.Exists(fileLink)) File.Delete(fileLink);
            Directory.Delete(probe, recursive: true);
        }
    }
}
