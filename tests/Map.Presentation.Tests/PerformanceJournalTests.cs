using System.Diagnostics;
using System.Text.Json;
using Map.Assets;
using Map.Presentation.Services;
using Xunit.Abstractions;

namespace Map.Presentation.Tests;

public sealed class PerformanceJournalTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-performance-journal-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Epoch = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private string Diagnostics => Path.Combine(_root, "diagnostics");
    private PerformanceJournal Journal() => new(_root, false, 0, Epoch);
    private JsonElement[] Rows() => Directory.GetFiles(Diagnostics, "perf-*.jsonl")
        .Order(StringComparer.Ordinal).SelectMany(File.ReadAllLines)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

    [Fact]
    public void PerformanceJournal_exact_percentiles_tail_fps_counters_and_single_session_worst()
    {
        using (var journal = Journal())
        {
            // 200 distinct values distinguish p99 from max and the slowest-1% mean from p99 FPS.
            for (var i = 1; i <= 200; i++)
                journal.RecordFrame(new(i, i, PerformanceGesture.Pan, 3, 1, 2, i, i + 1, i + 2,
                    new(i, i + 1, i + 2, i + 3, i + 4), i == 200, i + 5, i + 6), Ticks(i * .01));
            journal.Drain(Ticks(10), 123456789);
            journal.RecordFrame(new(2, 10, PerformanceGesture.Edit), Ticks(11));
            journal.Drain(Ticks(12), 100);
        }
        var rows = Rows();
        Assert.Equal(3, rows.Length);
        var window = rows[0];
        Assert.Equal("window", window.GetProperty("kind").GetString());
        Assert.False(window.GetProperty("partial").GetBoolean());
        Assert.Equal(200, window.GetProperty("frames").GetInt32());
        Assert.Equal(100, window.GetProperty("p50WorkMs").GetDouble());
        Assert.Equal(190, window.GetProperty("p95WorkMs").GetDouble());
        Assert.Equal(198, window.GetProperty("p99WorkMs").GetDouble());
        Assert.Equal(200, window.GetProperty("maxWorkMs").GetDouble());
        Assert.Equal(1000 / 199.5, window.GetProperty("onePercentLowFps").GetDouble(), 10);
        Assert.Equal(184, window.GetProperty("over16_7Ms").GetInt32());
        Assert.Equal(167, window.GetProperty("over33Ms").GetInt32());
        Assert.Equal(.75, window.GetProperty("cacheHitRate").GetDouble());
        Assert.Equal(600, window.GetProperty("cacheHits").GetInt32());
        Assert.Equal(200, window.GetProperty("cacheMisses").GetInt32());
        var memory = window.GetProperty("memory");
        Assert.True(memory.GetProperty("cacheBudgetBytes").GetInt64() >= 256L * 1024 * 1024);
        Assert.Equal(JsonValueKind.Array, memory.GetProperty("caches").ValueKind);
        Assert.Contains(memory.GetProperty("pressure").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal(400, window.GetProperty("cacheInvalidations").GetInt32());
        Assert.Equal(200, window.GetProperty("maxDetailTilesPending").GetInt32());
        Assert.Equal(201, window.GetProperty("maxUnresolvedImages").GetInt32());
        Assert.Equal(202, window.GetProperty("maxMissingAssets").GetInt32());
        Assert.Equal(205, window.GetProperty("maxTerrainRefinementsPending").GetInt32());
        Assert.Equal(206, window.GetProperty("maxHexChunksPending").GetInt32());
        Assert.Equal(123456789, window.GetProperty("peakWorkingSetBytes").GetInt64());
        Assert.Equal(200, window.GetProperty("gestures").GetProperty("pan").GetInt32());
        Assert.Equal(1, window.GetProperty("queueSnapshotsUnavailable").GetInt32());
        Assert.Equal(new[] { 200, 201, 202, 203, 204 }, window.GetProperty("maxQueues").EnumerateObject()
            .Select(property => property.Value.GetInt32()).ToArray());
        Assert.True(rows[1].GetProperty("partial").GetBoolean());
        Assert.Equal("worstMoment", rows[2].GetProperty("kind").GetString());
        var worst = rows[2].GetProperty("moment");
        Assert.Equal(Epoch.AddSeconds(2), worst.GetProperty("utc").GetDateTimeOffset());
        Assert.Equal(200, worst.GetProperty("frame").GetProperty("workMilliseconds").GetDouble());
        Assert.Equal("Pan", worst.GetProperty("frame").GetProperty("gesture").GetString());
        Assert.Equal(200, worst.GetProperty("frame").GetProperty("queues").GetProperty("metadata").GetInt32());
    }

    [Fact]
    public void PerformanceJournal_one_percent_low_counts_only_gesture_intervals()
    {
        using (var journal = Journal())
        {
            // A two-second idle pause must not read as a 0.5 FPS frame (DEC-109 is about frames during gestures).
            journal.RecordFrame(new(2, 2000, PerformanceGesture.Idle), Ticks(1));
            journal.RecordFrame(new(3, 20, PerformanceGesture.Pan), Ticks(2));
            journal.RecordFrame(new(3, 40, PerformanceGesture.Pan), Ticks(3));
            journal.Drain(Ticks(10), 0);
        }
        var window = Rows()[0];
        Assert.Equal(2, window.GetProperty("intervalSamples").GetInt32());
        Assert.Equal(25, window.GetProperty("onePercentLowFps").GetDouble());
        Assert.Equal(2, window.GetProperty("gestureIntervalOver16_7Ms").GetInt32());
        Assert.Equal(1, window.GetProperty("gestureIntervalOver33Ms").GetInt32());
        Assert.Equal(40, window.GetProperty("maxGestureIntervalMs").GetDouble());
    }

    [Fact]
    public void PerformanceJournal_strict_thresholds_first_interval_and_no_cache_requests()
    {
        using (var journal = Journal())
        {
            journal.RecordFrame(new(16.7, 0, PerformanceGesture.Idle), Ticks(1));
            journal.RecordFrame(new(33, 20, PerformanceGesture.Zoom), Ticks(2));
            journal.RecordFrame(new(33.001, 40, PerformanceGesture.Edit), Ticks(3));
            journal.Drain(Ticks(10), 0);
        }
        var window = Rows()[0];
        Assert.Equal(2, window.GetProperty("over16_7Ms").GetInt32());
        Assert.Equal(1, window.GetProperty("over33Ms").GetInt32());
        Assert.Equal(2, window.GetProperty("intervalSamples").GetInt32());
        Assert.Equal(25, window.GetProperty("onePercentLowFps").GetDouble());
        Assert.Equal(JsonValueKind.Null, window.GetProperty("cacheHitRate").ValueKind);
    }

    [Fact]
    public void PerformanceJournal_empty_and_invalid_samples_do_not_write_and_dispose_is_idempotent()
    {
        var journal = Journal();
        journal.RecordFrame(new(double.NaN, 10, PerformanceGesture.Idle), 0);
        journal.RecordFrame(new(1, double.PositiveInfinity, PerformanceGesture.Idle), 0);
        journal.RecordFrame(new(-1, 10, PerformanceGesture.Idle), 0);
        journal.RecordFrame(new(1, -1, PerformanceGesture.Idle), 0);
        journal.Drain(Ticks(1000000), 100);
        journal.Dispose();
        journal.Dispose();
        journal.RecordFrame(new(1, 1, PerformanceGesture.Idle));
        Assert.False(Directory.Exists(Diagnostics));
    }

    [Fact]
    public void PerformanceJournal_windows_use_frame_timestamps_skip_empty_time_and_rotate_at_utc_midnight()
    {
        using (var journal = new PerformanceJournal(_root, false, 0, Epoch.AddSeconds(-10)))
        {
            journal.RecordFrame(new(1, 10, PerformanceGesture.Idle), Ticks(1));
            journal.RecordFrame(new(2, 20, PerformanceGesture.Zoom), Ticks(10));
            journal.Drain(Ticks(20), 20);
            journal.RecordFrame(new(3, 30, PerformanceGesture.Edit), Ticks(3 * 86400 + 1));
            journal.Drain(Ticks(3 * 86400 + 10), 30);
        }
        var windows = Rows().Where(row => row.GetProperty("kind").GetString() == "window").ToArray();
        Assert.Equal(3, windows.Length);
        Assert.All(windows, window => Assert.Equal(1, window.GetProperty("frames").GetInt32()));
        Assert.Equal(new[] { 1d, 2d, 3d }, windows.Select(row => row.GetProperty("maxWorkMs").GetDouble()));
        Assert.Equal(2, Directory.GetFiles(Diagnostics).Length);
        Assert.True(File.Exists(Path.Combine(Diagnostics, "perf-20261101.jsonl")));
        Assert.True(File.Exists(Path.Combine(Diagnostics, "perf-20261104.jsonl")));
    }

    [Fact]
    public void PerformanceJournal_retention_keeps_seven_files_and_leaves_other_names_untouched()
    {
        Directory.CreateDirectory(Diagnostics);
        for (var i = 1; i <= 10; i++) File.WriteAllText(Path.Combine(Diagnostics, $"perf-202610{i:00}.jsonl"), "old\n");
        var protectedNames = new[] { "perf-not-ours.jsonl", "perf-20261399.jsonl", "perf-20261001.jsonl.bak", "other.jsonl" };
        foreach (var name in protectedNames) File.WriteAllText(Path.Combine(Diagnostics, name), "keep");
        using (var journal = Journal())
        {
            journal.RecordFrame(new(1, 1, PerformanceGesture.Idle), Ticks(1));
            journal.Drain(Ticks(10), 0);
        }
        Assert.Equal(7 + protectedNames.Length, Directory.GetFiles(Diagnostics).Length);
        Assert.False(File.Exists(Path.Combine(Diagnostics, "perf-20261004.jsonl")));
        Assert.True(File.Exists(Path.Combine(Diagnostics, "perf-20261005.jsonl")));
        foreach (var name in protectedNames) Assert.Equal("keep", File.ReadAllText(Path.Combine(Diagnostics, name)));
    }

    [Fact]
    public void PerformanceJournal_retention_enforces_twenty_megabytes_even_for_current_day_file()
    {
        Directory.CreateDirectory(Diagnostics);
        using (var large = File.Create(Path.Combine(Diagnostics, "perf-20261101.jsonl")))
            large.SetLength(PerformanceJournal.DiskLimitBytes);
        using (var journal = Journal())
        {
            journal.RecordFrame(new(1, 1, PerformanceGesture.Idle), Ticks(1));
            journal.Drain(Ticks(10), 0);
        }
        Assert.InRange(new DirectoryInfo(Diagnostics).GetFiles().Sum(file => file.Length), 1, PerformanceJournal.DiskLimitBytes);
        Assert.Equal(2, Rows().Length); // Oversized file replaced; both remaining lines are complete JSON.
    }

    [Fact]
    public void PerformanceJournal_io_failures_are_counted_without_throwing_or_spinning()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Diagnostics, "a file blocks directory creation");
        var journal = Journal();
        journal.RecordFrame(new(1, 1, PerformanceGesture.Idle), Ticks(1));
        journal.Drain(Ticks(10), 0);
        Assert.Equal(1, journal.IoErrors);
        journal.Drain(Ticks(20), 0);
        Assert.Equal(1, journal.IoErrors); // No retry of the failed row.
        journal.Dispose();
        Assert.Equal(2, journal.IoErrors); // One separate worstMoment attempt.
    }

    [Fact]
    public void PerformanceJournal_full_ring_is_nonblocking_and_preserves_dropped_slowest_frame()
    {
        using (var journal = Journal())
        {
            for (var i = 0; i < PerformanceJournal.Capacity; i++)
                journal.RecordFrame(new(i, 1, PerformanceGesture.Edit), Ticks(1));
            Assert.Equal(1, journal.DroppedFrames);
            journal.Drain(Ticks(10), 0);
        }
        var rows = Rows();
        Assert.Equal(PerformanceJournal.Capacity - 1, rows[0].GetProperty("frames").GetInt32());
        Assert.Equal(1, rows[0].GetProperty("droppedFramesSession").GetInt32());
        Assert.Equal(PerformanceJournal.Capacity - 1,
            rows[1].GetProperty("moment").GetProperty("frame").GetProperty("workMilliseconds").GetDouble());
    }

    [Fact]
    public void PerformanceJournal_sample_capacity_is_explicit_without_losing_aggregate_counts()
    {
        using (var journal = Journal())
        {
            for (var batch = 0; batch < 2; batch++)
            {
                for (var i = 0; i < 10000; i++) journal.RecordFrame(new(20, 20, PerformanceGesture.Pan), Ticks(1));
                journal.Drain(Ticks(2), 1);
            }
            journal.Drain(Ticks(10), 1);
        }
        var window = Rows()[0];
        Assert.Equal(20000, window.GetProperty("frames").GetInt32());
        Assert.Equal(20000 - PerformanceJournal.Capacity, window.GetProperty("percentileSamplesOmitted").GetInt32());
        Assert.Equal(20000, window.GetProperty("over16_7Ms").GetInt32());
        Assert.Equal(0, window.GetProperty("droppedFramesSession").GetInt32());
    }

    [Fact]
    public async Task PerformanceJournal_background_worker_writes_and_flushes_one_final_worst()
    {
        var started = Stopwatch.GetTimestamp() - Ticks(10);
        using (var journal = new PerformanceJournal(_root, true, started, Epoch))
        {
            journal.RecordFrame(new(12, 16, PerformanceGesture.Zoom), started + Ticks(1));
            var deadline = Stopwatch.GetTimestamp() + Ticks(5);
            while ((!Directory.Exists(Diagnostics) || Directory.GetFiles(Diagnostics).Length == 0)
                && Stopwatch.GetTimestamp() < deadline) await Task.Delay(25);
            Assert.True(Directory.Exists(Diagnostics));
        }
        Assert.Equal(new[] { "window", "worstMoment" }, Rows().Select(row => row.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task PerformanceJournal_failed_pixel_work_is_counted_until_take_or_forget()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "assets"));
        await using var store = new StampDerivedAssetStore(catalog, Path.Combine(_root, "derived"));
        // Queue accounting must also cover unavailable inputs; no image decode is needed here.
        var sha = new string('a', 64);
        var stamp = new StampDerivedAssetStore.FileStamp(Path.Combine(_root, "missing.png"), 0, 0);
        var header = new StampDerivedAssetStore.Header(1, sha, 1, 1, 0, stamp, stamp, 0, 1, "");
        var request = store.RequestMip(header, header.FirstMip);
        Assert.Equal(1, store.PerformanceQueueLengths.Pixels);
        await request.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, store.PerformanceQueueLengths.Pixels); // Ready data still awaits adoption.
        Assert.True(store.TryTakeMip(sha, header.FirstMip, out var mip));
        mip?.Image?.Dispose();
        Assert.Equal(0, store.PerformanceQueueLengths.Pixels);
        store.RequestMip(header, header.FirstMip);
        store.Forget();
        Assert.Equal(0, store.PerformanceQueueLengths.Pixels);
    }

    [Fact]
    public async Task PerformanceJournal_queue_snapshot_is_allocation_free_and_never_waits_for_busy_source()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "assets"));
        await using var source = new CatalogStampImageSource(catalog, () => throw new InvalidOperationException(), () => null,
            derivedRoot: Path.Combine(_root, "derived"));
        Assert.True(source.TryReadPerformanceQueues(out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var successes = 0;
        for (var i = 0; i < 800000; i++) if (source.TryReadPerformanceQueues(out _)) successes++;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Queue snapshot: 800,000 calls, {bytes} bytes, {elapsed / 800000:F8} ms/call.");
        Assert.Equal(800000, successes);
        Assert.Equal(0, bytes);
        Assert.True(elapsed / 800000 < .05);
        var gate = typeof(CatalogStampImageSource).GetField("_gate", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.GetValue(source)!;
        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (gate) { locked.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        Assert.True(locked.Wait(TimeSpan.FromSeconds(5)));
        try { Assert.False(source.TryReadPerformanceQueues(out _)); }
        finally { release.Set(); await holder; }
    }

    [Fact]
    public void PerformanceJournal_per_frame_call_allocates_zero_bytes_and_costs_under_005_ms()
    {
        using var journal = new PerformanceJournal(_root, false, Stopwatch.GetTimestamp(), Epoch);
        var frame = new PerformanceFrame(12, 16, PerformanceGesture.Pan, 100, 10, 1, 5, 2, 0, new(2, 3, 4, 5, 6));
        const int batchSize = 8000;
        // Warm the JIT, both producer branches and serializer before measuring only the producer.
        for (var i = 0; i < batchSize; i++) journal.RecordFrame(frame);
        journal.Drain(Stopwatch.GetTimestamp(), 0);
        long allocated = 0, elapsed = 0;
        double maxBatchMsPerFrame = 0;
        const int batches = 100;
        for (var batch = 0; batch < batches; batch++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < batchSize; i++)
                journal.RecordFrame(frame with { WorkMilliseconds = 13 + batch * batchSize + i });
            var ticks = Stopwatch.GetTimestamp() - start;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            elapsed += ticks;
            maxBatchMsPerFrame = Math.Max(maxBatchMsPerFrame, 1000d * ticks / Stopwatch.Frequency / batchSize);
            journal.Drain(Stopwatch.GetTimestamp(), 0);
        }
        var msPerFrame = 1000d * elapsed / Stopwatch.Frequency / (batches * batchSize);
        output.WriteLine($"RecordFrame: {batches * batchSize:N0} calls, {allocated} bytes, mean {msPerFrame:F8} ms/frame, max batch {maxBatchMsPerFrame:F8} ms/frame.");
        Assert.Equal(0, allocated);
        Assert.Equal(0, journal.DroppedFrames);
        Assert.True(msPerFrame < .05, $"RecordFrame {msPerFrame:F8} ms/frame");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
