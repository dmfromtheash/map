using System.Reflection;
using System.Collections.Concurrent;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;

namespace Map.Presentation.Tests;

public sealed class ImageProtectionFrameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-protection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Protection_switch_uses_the_same_gate_as_admission()
    {
        await using var catalog = new AssetCatalog(_root);
        using var source = new CatalogStampImageSource(catalog, () => null!, () => null);
        var gate = Field<object>(source, "_gate");
        Check(() => source.ProtectNeededMips = false);
        Check(() => _ = source.ProtectNeededMips);

        void Check(Action access)
        {
            using var started = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() => { started.Set(); access(); finished.Set(); });
            bool escaped;
            lock (gate)
            {
                thread.Start();
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                escaped = finished.Wait(TimeSpan.FromMilliseconds(100));
            }
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.False(escaped, "Protection policy raced with an admission holding the source gate.");
        }
    }

    [Fact]
    public async Task Empty_canvas_frames_advance_both_shelves_once_and_leases_do_not()
    {
        await using var catalog = new AssetCatalog(_root);
        using var source = new CatalogStampImageSource(catalog, () => new MapDocument(DocumentId.New(), "Frame",
            DateTimeOffset.UnixEpoch, new CanvasDefinition(1, 1, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "Page", true, false, 1, "#FFFFFFFF")]), () => null);
        var cache = Field<AssetImageCache>(source, "_cache");
        var samples = Field<AssetImageCache>(source, "_samples");
        for (var frame = 1; frame <= 3; frame++)
        {
            var scope = source.BeginFrame()!;
            Assert.Equal(frame, cache.Frame);
            Assert.Equal(frame, samples.Frame);
            scope.Dispose();
            scope.Dispose();
            using var lease = source.Lease();
            Assert.Null(lease.TryGetImage("missing"));
            Assert.Equal(frame, cache.Frame);
            Assert.Equal(frame, samples.Frame);
        }
    }

    internal static T Field<T>(object source, string name) =>
        (T)source.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source)!;

    [Fact]
    public async Task A_waiting_plan_keeps_its_resident_subset_protected_until_the_remaining_decode_finishes()
    {
        await using var catalog = new AssetCatalog(_root);
        await using var source = new CatalogStampImageSource(catalog, () => null!, () => null,
            cacheBytes: 4L * 64 * 64 * 4, derivedRoot: Path.Combine(_root, "derived"));
        source.ProtectNeededMips = true;
        var plan = source.CreatePreparationPlan()!;
        var resident = new StampDerivedAssetStore.PixelKey(new string('a', 64), 0, 0);
        var waiting = new StampDerivedAssetStore.PixelKey(new string('b', 64), 0, 0);
        Field<HashSet<StampDerivedAssetStore.PixelKey>>(plan, "Keys").UnionWith([resident, waiting]);
        var store = Field<StampDerivedAssetStore>(source, "_derived");
        var headers = Field<ConcurrentDictionary<string, TaskCompletionSource<StampDerivedAssetStore.Header?>>>(store, "_requests");
        var completion = new TaskCompletionSource<StampDerivedAssetStore.Header?>();
        Assert.True(headers.TryAdd(waiting.Sha, completion));
        var samples = Field<AssetImageCache>(source, "_samples");
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(64, 64))!;
        try
        {
            using (source.BeginFrame())
            {
                Assert.NotNull(samples.AdoptPixels(resident.Sha + "#0", surface.Snapshot()));
                Assert.False(plan.Prepare(() => { }));
            }
            for (var frame = 0; frame < 4; frame++)
            {
                using var scope = source.BeginFrame();
                Assert.False(plan.Prepare(() => { }));
                Assert.Equal(AssetImageCache.AdoptResult.Refused,
                    samples.TryAdoptPixels("competing", surface.Snapshot(), out _));
                Assert.True(samples.Contains(resident.Sha + "#0"));
                Assert.Equal(0, samples.Evictions);
            }
        }
        finally { completion.TrySetResult(null); }
    }

    [Fact]
    public async Task A_released_background_interest_is_retained_for_N_more_canvas_frames()
    {
        await using var catalog = new AssetCatalog(_root);
        using var source = new CatalogStampImageSource(catalog, () => null!, () => null);
        var interests = Field<Dictionary<StampDerivedAssetStore.PixelKey, int>>(source, "_leaseWanted");
        var key = new StampDerivedAssetStore.PixelKey("waiting", 0, 0);
        using (source.BeginFrame()) interests[key] = 1;
        interests.Clear();
        for (var i = 0; i < CatalogStampImageSource.RecentMipTuning.Frames; i++)
        {
            using (source.BeginFrame()) { }
            Assert.Equal(1, source.LastWantedMipCount);
        }
        using (source.BeginFrame()) { }
        Assert.Equal(0, source.LastWantedMipCount);
        Assert.Equal(0, source.CancelledStillWanted);
    }

    [Fact]
    public async Task Skipped_by_time_budget_is_not_cancelled_and_a_long_unwanted_key_is_cancelled()
    {
        await using var catalog = new AssetCatalog(_root);
        await using var source = new CatalogStampImageSource(catalog, () => null!, () => null,
            derivedRoot: Path.Combine(_root, "derived"));
        var store = Field<StampDerivedAssetStore>(source, "_derived");
        var pending = Field<object>(store, "_mips");
        var requestType = typeof(StampDerivedAssetStore).GetNestedType("MipRequest", BindingFlags.NonPublic)!;
        var stop = new CancellationTokenSource(); // ownership transfers to the store's request
        var completion = new TaskCompletionSource<StampDerivedAssetStore.Mip>();
        using var cancellation = stop.Token.Register(() => completion.TrySetResult(new(null, 0, 0, false, Cancelled: true)));
        var request = Activator.CreateInstance(requestType, stop)!;
        requestType.GetProperty("Task")!.SetValue(request, completion.Task);
        var key = new StampDerivedAssetStore.PixelKey("edge", 0, 0);
        pending.GetType().GetProperty("Item")!.SetValue(pending, request, [key]);
        using (source.BeginFrame()) Field<HashSet<StampDerivedAssetStore.PixelKey>>(source, "_wantedMips").Add(key);
        for (var skipped = 1; skipped <= CatalogStampImageSource.RecentMipTuning.Frames; skipped++)
        {
            using (source.BeginFrame()) { } // the plan had no time/request/tile budget
            Assert.Equal(1, source.LastWantedMipCount);
            Assert.Equal(0, source.DerivedStatistics.DiscardedMips);
            Assert.False(stop.IsCancellationRequested);
        }
        using (source.BeginFrame()) { }
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal(1, source.DerivedStatistics.DiscardedMips);
        Assert.Equal(0, source.LastWantedMipCount);
        Assert.Equal(0, source.CancelledStillWanted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
