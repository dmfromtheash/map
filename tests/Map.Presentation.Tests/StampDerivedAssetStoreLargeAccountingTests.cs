using System.Diagnostics;
using System.Reflection;
using Map.Assets;
using Map.Presentation.Services;
using Map.Rendering;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class StampDerivedAssetStoreLargeAccountingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-large-accounting", Guid.NewGuid().ToString("N"));
    private AssetCatalog _catalog = null!;
    // test-change: keep the former 128 MiB boundary and this host's legacy lane count,
    // preserving the same parallel decode/cancellation coverage with the adaptive gate.
    private readonly MachineProfileMonitor _machine = new(() => new(Environment.ProcessorCount,
        8 * MachineProfile.GiB, 6 * MachineProfile.GiB), key => key switch
        {
            "MAP_BUDGET_LARGE_WAITING_BYTES" => (128 * MachineProfile.MiB).ToString(),
            "MAP_BUDGET_LARGE_LANES" => Math.Clamp(Environment.ProcessorCount - 2, 1, 4).ToString(),
            _ => null
        });

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Leaving_finished_large_mips_unadopted_does_not_starve_the_new_camera()
    {
        var records = ImportOwners(9);
        await using var store = CreateStore();
        var headers = await Headers(store, records);
        var oldKeys = headers.Take(8).Select(Key).ToArray();
        var oldResults = await Task.WhenAll(headers.Take(8).Select(h => store.RequestPixels(h, 0, 0)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(oldResults, mip => Assert.NotNull(mip.Image));
        var heldBytes = records.Take(8).Sum(Bytes);
        Assert.Equal(heldBytes, store.LargeStatistics.WaitingBytes);
        Assert.InRange(heldBytes, 1, _machine.Limits.LargeWaitingBytes);
        Assert.True(heldBytes + Bytes(records[8]) > _machine.Limits.LargeWaitingBytes);

        // The old camera's results are finished, but no canvas ever takes them. The next
        // master cannot fit until the obsolete results return their real byte reservations.
        var nextKey = Key(headers[8]);
        var next = store.RequestPixels(headers[8], 0, 0);
        store.CancelUnwantedPixels(new HashSet<StampDerivedAssetStore.PixelKey> { nextKey });
        var ready = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(ready.Image);
        await WaitingBytes(store, Bytes(records[8]));
        foreach (var key in oldKeys) Assert.False(store.TryTakePixels(key, out _));
        Assert.True(store.TryTakePixels(nextKey, out var taken));
        Assert.Same(ready, taken);
        taken!.Image!.Dispose();
        Assert.False(store.TryTakePixels(nextKey, out _));
        Assert.Equal(0, store.LargeStatistics.WaitingBytes);
        Assert.Equal(0, store.PendingMips);
        Console.WriteLine($"LARGE_CAMERA heldBefore={heldBytes} heldAfter=0 pending=0 adopted=1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_large_preparation_releases_bytes_without_waiting_for_adoption(bool corrupt)
    {
        var records = ImportOwners(1);
        await using var store = CreateStore();
        var header = (await Headers(store, records))[0];
        var path = _catalog.SourcePath(records[0])!;
        if (corrupt)
        {
            using var file = File.OpenWrite(path);
            file.SetLength(32); // Truncated owner bytes, never a synthetic image.
        }
        else File.Delete(path);
        var mip = await store.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(mip.Image);
        Assert.False(mip.LargeReserved);
        Assert.Equal(0, store.LargeStatistics.WaitingBytes);
        Assert.True(store.TryTakePixels(Key(header), out _));
        Assert.False(store.TryTakePixels(Key(header), out _));
        Assert.Equal(0, store.LargeStatistics.WaitingBytes);
        AssertLanes(store);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("pixels")]
    [InlineData("missing-source")]
    public async Task Portable_large_handoff_and_recovery_keep_exact_pixels_and_release_the_reservation(string state)
    {
        var records = ImportOwners(1);
        string portable;
        await using (var builder = CreateStore())
            portable = await builder.BuildPortableAsync(records[0].Sha256).WaitAsync(TimeSpan.FromSeconds(30));
        await using var store = CreateStore();
        var header = (await Headers(store, records))[0];
        Assert.NotNull(header.PortablePath);
        await Field<Task>(store, "_colorValidation").WaitAsync(TimeSpan.FromSeconds(5));
        var trustWait = Stopwatch.StartNew();
        while (store.TrustStatistics.Pending > 0 && trustWait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(1);
        Assert.Equal(0, store.TrustStatistics.Pending);
        var source = _catalog.SourcePath(records[0])!;
        using var reference = new AssetImageCache();
        Assert.NotNull(reference.Get(header.Sha, source));
        if (state == "pixels")
        {
            var bytes = File.ReadAllBytes(portable);
            bytes[44 + BitConverter.ToInt32(bytes, 8)] ^= 0x7f;
            File.WriteAllBytes(portable, bytes);
        }
        else if (state == "missing-source") File.Delete(source);
        // The portable file includes level 1 (1024 px); level 0 exceeds its entry limit.
        var key = new StampDerivedAssetStore.PixelKey(header.Sha, 0, 1);
        var mip = await store.RequestPixels(header, 0, 1).WaitAsync(TimeSpan.FromSeconds(5));
        if (state == "missing-source")
        {
            Assert.Null(mip.Image);
            Assert.False(mip.LargeReserved);
            Assert.Equal(0, store.LargeStatistics.WaitingBytes);
        }
        else
        {
            Assert.NotNull(mip.Image);
            Assert.True(mip.LargeReserved);
            Assert.Equal((long)mip.Width * mip.Height * 4, store.LargeStatistics.WaitingBytes);
            using var expected = SKBitmap.FromImage(reference.GetForPixelSize(header.Sha, 1024, 1024)!);
            using var actual = SKBitmap.FromImage(mip.Image);
            Assert.Equal(expected.Bytes, actual.Bytes);
        }
        Assert.True(store.TryTakePixels(key, out var taken));
        taken!.Image?.Dispose();
        Assert.False(store.TryTakePixels(key, out _));
        Assert.Equal(0, store.LargeStatistics.WaitingBytes);
        AssertLanes(store);
    }

    [Theory]
    [InlineData("unwanted")]
    [InlineData("forget")]
    [InlineData("dispose")]
    public async Task Cancellation_after_reserve_before_decode_releases_exactly_once(string terminal)
    {
        var records = ImportOwners(1);
        await using var store = CreateStore();
        var header = (await Headers(store, records))[0];
        Task<StampDerivedAssetStore.Mip> task;
        // The worker can reserve bytes but cannot mark Started while this gate is held.
        // This makes cancellation in the reserve-to-decode window deterministic.
        lock (Field<object>(store, "_requestsGate"))
        {
            task = store.RequestPixels(header, 0, 0);
            Assert.True(SpinWait.SpinUntil(() => store.LargeStatistics.WaitingBytes == Bytes(records[0]), TimeSpan.FromSeconds(5)));
            if (terminal == "unwanted") store.CancelUnwantedPixels(new HashSet<StampDerivedAssetStore.PixelKey>());
            else if (terminal == "forget") store.Forget();
            else store.Dispose();
        }
        var cancelled = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Cancelled);
        await WaitingBytes(store, 0);
        Assert.False(store.TryTakePixels(Key(header), out _));
        Assert.Equal(0, store.PendingMips);
        if (terminal == "dispose") await store.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        else
        {
            AssertLanes(store);
            var next = await store.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(next.Image);
            Assert.Equal(Bytes(records[0]), store.LargeStatistics.WaitingBytes);
            Assert.True(store.TryTakePixels(Key(header), out var taken));
            taken!.Image!.Dispose();
            Assert.Equal(0, store.LargeStatistics.WaitingBytes);
        }
    }

    [Fact]
    public async Task Cancellation_before_a_large_lane_and_a_compact_source_reuses_both_gates()
    {
        var records = ImportOwners(1, side: 2051);
        await using var store = CreateStore();
        var header = (await Headers(store, records))[0];
        // Level 0 uses a large lane; level 3 is compact and needs the source gate.
        // test-change: large lanes now use AdaptiveWorkGate; exercise the same exhausted
        // admission/cancellation/reuse sequence for both gates, retaining every assertion.
        var large = Field<AdaptiveWorkGate>(store, "_largeLanes");
        var compact = Field<SemaphoreSlim>(store, "_compactSourceReader");
        foreach (var (wait, release, level, count) in new (Func<Task>, Action, int, int)[]
            { (() => large.WaitAsync(), large.Release, 0, _machine.Limits.LargeLanes),
              (() => compact.WaitAsync(), () => compact.Release(), 3, 1) })
        {
            for (var i = 0; i < count; i++) await wait();
            try
            {
                var pending = store.RequestPixels(header, 0, level);
                store.CancelUnwantedPixels(new HashSet<StampDerivedAssetStore.PixelKey>());
                var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(cancelled.Cancelled);
                Assert.Equal(0, store.LargeStatistics.WaitingBytes);
                Assert.True(store.TryTakePixels(new(header.Sha, 0, level), out _));
            }
            finally { for (var i = 0; i < count; i++) release(); }
            var next = await store.RequestPixels(header, 0, level).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(next.Image);
            Assert.True(store.TryTakePixels(new(header.Sha, 0, level), out var taken));
            taken!.Image!.Dispose();
            Assert.Equal(0, store.LargeStatistics.WaitingBytes);
            AssertLanes(store);
        }
    }

    [Theory]
    [InlineData("forget")]
    [InlineData("source-change")]
    [InlineData("dispose")]
    public async Task Retiring_finished_large_results_disposes_them_and_returns_the_budget(string terminal)
    {
        var records = ImportOwners(1);
        await using var store = CreateStore();
        var header = (await Headers(store, records))[0];
        var mip = await store.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(mip.Image);
        Assert.Equal(Bytes(records[0]), store.LargeStatistics.WaitingBytes);
        if (terminal == "forget") store.Forget();
        else if (terminal == "dispose") store.Dispose();
        else
        {
            var path = _catalog.SourcePath(records[0])!;
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
            // Deliver the actual source change directly, without depending on OS watcher timing.
            typeof(StampDerivedAssetStore).GetMethod("FileChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(store, [path]);
        }
        await WaitingBytes(store, 0);
        Assert.False(store.TryTakePixels(Key(header), out _));
        Assert.Equal(0, store.PendingMips);
        // Await native cleanup as well as the byte notification.
        if (terminal == "dispose") await store.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        else
        {
            var watch = Stopwatch.StartNew();
            while (mip.Image.Handle != IntPtr.Zero && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(1);
        }
        Assert.Equal(IntPtr.Zero, mip.Image.Handle);
    }

    [Fact]
    public async Task Cancelling_a_budget_waiter_preserves_other_reservations_and_wakes_the_next_request()
    {
        var records = ImportOwners(9);
        await using var store = CreateStore();
        var headers = await Headers(store, records);
        var wanted = headers.Take(8).Select(Key).ToHashSet();
        await Task.WhenAll(headers.Take(8).Select(h => store.RequestPixels(h, 0, 0))).WaitAsync(TimeSpan.FromSeconds(30));
        var held = records.Take(8).Sum(Bytes);
        var waiting = store.RequestPixels(headers[8], 0, 0);
        var lanes = Field<AdaptiveWorkGate>(store, "_largeLanes");
        Assert.True(SpinWait.SpinUntil(() => lanes.CurrentCount < _machine.Limits.LargeLanes, TimeSpan.FromSeconds(5)));
        Assert.False(waiting.IsCompleted);
        Assert.Equal(held, store.LargeStatistics.WaitingBytes);
        store.CancelUnwantedPixels(wanted);
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Cancelled);
        Assert.Equal(held, store.LargeStatistics.WaitingBytes);
        Assert.True(store.TryTakePixels(Key(headers[8]), out _));
        var next = store.RequestPixels(headers[8], 0, 0);
        store.Forget(); // Cancel the new waiter and retire the eight existing reservations.
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitingBytes(store, 0);
        AssertLanes(store);
        var ready = await store.RequestPixels(headers[8], 0, 0).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(ready.Image);
        Assert.Equal(Bytes(records[8]), store.LargeStatistics.WaitingBytes);
        Assert.True(store.TryTakePixels(Key(headers[8]), out var taken));
        taken!.Image!.Dispose();
        Assert.Equal(0, store.LargeStatistics.WaitingBytes);
    }

    [Fact]
    public async Task Concurrent_takes_retirement_and_cancellation_never_double_adopt_or_release()
    {
        var records = ImportOwners(1);
        await using var store = CreateStore();
        var header = (await Headers(store, records))[0];
        var key = Key(header);
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var ready = await store.RequestPixels(header, 0, 0).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(ready.Image);
            Assert.Equal(Bytes(records[0]), store.LargeStatistics.WaitingBytes);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var adopted = 0;
            async Task Take()
            {
                await start.Task;
                if (store.TryTakePixels(key, out var taken))
                {
                    Interlocked.Increment(ref adopted);
                    taken!.Image!.Dispose();
                }
                Assert.True(store.LargeStatistics.WaitingBytes >= 0);
            }
            async Task Cancel()
            {
                await start.Task;
                store.CancelUnwantedPixels(new HashSet<StampDerivedAssetStore.PixelKey>());
                store.Forget(); // Repeated retirement must not release an adopted/removed request.
                Assert.True(store.LargeStatistics.WaitingBytes >= 0);
            }
            var contenders = new[] { Take(), Take(), Cancel() };
            start.SetResult();
            await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.InRange(adopted, 0, 1);
            Assert.False(store.TryTakePixels(key, out _));
            await WaitingBytes(store, 0);
            Assert.Equal(0, store.PendingMips);
            AssertLanes(store);
        }
        Console.WriteLine("LARGE_RACE iterations=24 negativeBytes=0 doubleAdoptions=0 heldAfter=0");
    }

    private static T Field<T>(StampDerivedAssetStore store, string name) =>
        (T)typeof(StampDerivedAssetStore).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private void AssertLanes(StampDerivedAssetStore store)
    {
        Assert.Equal(_machine.Limits.LargeLanes, Field<AdaptiveWorkGate>(store, "_largeLanes").CurrentCount);
        Assert.Equal(1, Field<SemaphoreSlim>(store, "_pressureLane").CurrentCount);
        Assert.Equal(1, Field<SemaphoreSlim>(store, "_compactSourceReader").CurrentCount);
    }

    private static async Task WaitingBytes(StampDerivedAssetStore store, long expected)
    {
        var watch = Stopwatch.StartNew();
        while (store.LargeStatistics.WaitingBytes != expected && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Assert.True(store.LargeStatistics.WaitingBytes >= 0);
            await Task.Delay(1);
        }
        Assert.Equal(expected, store.LargeStatistics.WaitingBytes);
    }

    private StampDerivedAssetStore CreateStore() => new(_catalog, Path.Combine(_root, "derived"),
        preparedSourceByteCapacity: 0, startWatcher: _ => { }, machine: _machine);

    private static StampDerivedAssetStore.PixelKey Key(StampDerivedAssetStore.Header header) => new(header.Sha.ToLowerInvariant(), 0, 0);
    private static long Bytes(AssetRecord record) => (long)record.Width * record.Height * 4;

    private static async Task<StampDerivedAssetStore.Header[]> Headers(StampDerivedAssetStore store, AssetRecord[] records)
        => (await Task.WhenAll(records.Select(r => store.Request(r.Sha256))).WaitAsync(TimeSpan.FromSeconds(30)))
            .Select(h => { Assert.NotNull(h); return h!; }).ToArray();

    private AssetRecord[] ImportOwners(int count, int side = 2048)
    {
        var fixture = new DirectoryInfo(AppContext.BaseDirectory);
        while (fixture is not null && !File.Exists(Path.Combine(fixture.FullName, "Map.slnx"))) fixture = fixture.Parent;
        Assert.NotNull(fixture);
        var records = new AssetRecord[count];
        for (var i = 0; i < count; i++)
        {
            using var owner = SKBitmap.Decode(Path.Combine(fixture.FullName, "tests", "fixtures", "assets", $"owner-mountain-{1 + i % 6:00}.png"));
            // Only resample owner pixels. Distinct dimensions keep content keys unique;
            // eight masters fit together, but a ninth exceeds the unmodified 128 MiB budget.
            using var resized = owner.Resize(new SKImageInfo(side, side - i), new SKSamplingOptions(SKFilterMode.Linear));
            var path = Path.Combine(_root, $"owner-{i}.png");
            using (var png = resized.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(path)) png.SaveTo(file);
            records[i] = _catalog.Import(path, new(), DateTimeOffset.UnixEpoch);
            File.Delete(path);
        }
        return records;
    }

    public async Task DisposeAsync()
    {
        await _catalog.DisposeAsync();
        _machine.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
