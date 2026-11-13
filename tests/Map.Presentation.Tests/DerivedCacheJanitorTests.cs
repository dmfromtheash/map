using Map.Presentation.Services;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

public sealed class DerivedCacheJanitorTests(ITestOutputHelper output) : IDisposable
{
    private const long GB = 1_000_000_000;
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
        { Skip = "OS does not permit directory/file symlinks: " + ex.Message; }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (File.Exists(fileLink)) File.Delete(fileLink);
            Directory.Delete(probe, recursive: true);
        }
    }
}
