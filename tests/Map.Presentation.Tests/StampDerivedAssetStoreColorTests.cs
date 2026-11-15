using System.Collections.Concurrent;
using System.Reflection;
using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Services;
using Map.Tests;
using SkiaSharp;

namespace Map.Presentation.Tests;

public sealed class StampDerivedAssetStoreColorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "map-color-review", Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_root, "derived");
    private string Colors => Path.Combine(Cache, "v1", "overview-colors.bin");

    [Fact]
    public async Task Header_failure_wakes_the_view_when_it_removes_a_visible_remembered_color()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, _) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var writer = Store(catalog)) Assert.NotNull(await writer.Request(record.Sha256));
        await using var store = Store(catalog);
        await Private<Task>(store, "_colorValidation");
        Assert.NotNull(store.KnownColor(record.Sha256));
        var notices = 0;
        store.OverviewChanged = () => Interlocked.Increment(ref notices);
        var completion = new TaskCompletionSource<StampDerivedAssetStore.Header?>();
        Private<ConcurrentDictionary<string, TaskCompletionSource<StampDerivedAssetStore.Header?>>>(store, "_requests")[record.Sha256] = completion;
        Call(store, "CompleteHeader", record.Sha256, completion, null);
        Assert.Null(store.KnownColor(record.Sha256));
        Assert.Equal(1, Volatile.Read(ref notices));
    }

    [Fact]
    public async Task Remembered_source_validation_notifies_outside_the_request_gate()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, _) = ColorCacheFixture.Seed(catalog, Cache);
        await using var store = Store(catalog);
        await Private<Task>(store, "_colorValidation");
        Assert.NotNull(await store.Request(record.Sha256));
        var requestGate = Private<object>(store, "_requestsGate");
        var notices = 0;
        store.SourceChanged = () =>
        {
            Assert.False(Monitor.IsEntered(requestGate), "SourceChanged can re-enter an image source that holds its own gate.");
            notices++;
        };
        File.WriteAllBytes(catalog.SourcePath(record)!, [0, 1, 2, 3]);
        Call(store, "ValidateKnownColorSources");
        Assert.Equal(1, notices);
        Assert.Null(store.KnownColor(record.Sha256));
    }

    [Fact]
    public async Task Shutdown_does_not_persist_a_portable_color_awaiting_source_verification()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, local) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var store = Store(catalog))
        {
            var completion = new TaskCompletionSource<StampDerivedAssetStore.Header?>();
            Private<ConcurrentDictionary<string, TaskCompletionSource<StampDerivedAssetStore.Header?>>>(store, "_requests")[record.Sha256] = completion;
            // The portable metadata worker can finish before its independent trust worker.
            // Shutdown cancels queued verification, so this colour is provisional until proved.
            Call(store, "CompleteHeader", record.Sha256, completion,
                local with { PortablePath = Path.Combine(_root, "provisional.mapderived") });
            Assert.NotNull(await completion.Task);
            Assert.Equal(new SKColor(local.Color), store.KnownColor(record.Sha256));
        }
        await using var reopened = Store(catalog);
        Assert.Null(reopened.KnownColor(record.Sha256));
    }

    [Fact]
    public async Task Portable_color_becomes_persistent_when_its_source_verification_finishes()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, local) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var store = Store(catalog))
        {
            var completion = new TaskCompletionSource<StampDerivedAssetStore.Header?>();
            Private<ConcurrentDictionary<string, TaskCompletionSource<StampDerivedAssetStore.Header?>>>(store, "_requests")[record.Sha256] = completion;
            Call(store, "CompleteHeader", record.Sha256, completion,
                local with { PortablePath = Path.Combine(_root, "provisional.mapderived") });
            Assert.NotNull(await completion.Task);
            var trustType = typeof(StampDerivedAssetStore).GetNestedType("PortableTrust", BindingFlags.NonPublic)!;
            var trust = Activator.CreateInstance(trustType,
                new object[] { new[] { local.Original }, new[] { record.Sha256 } })!;
            Call(store, "RememberPortableTrust", record.Sha256, trust);
        }
        await using var reopened = Store(catalog);
        Assert.Equal(new SKColor(local.Color), reopened.KnownColor(record.Sha256));
        Assert.Equal(0, reopened.MetadataEntries);
    }

    [Theory]
    [InlineData(32)] // SHA payload: still a syntactically valid, different SHA.
    [InlineData(48)] // Colour payload: valid layout, wrong colour.
    public async Task Damaged_color_payload_is_rejected_before_any_entry_is_used(int offset)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, _) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var writer = Store(catalog)) Assert.NotNull(await writer.Request(record.Sha256));
        var bytes = File.ReadAllBytes(Colors);
        bytes[offset] ^= 1;
        File.WriteAllBytes(Colors, bytes);
        await using var reader = Store(catalog);
        Assert.Equal(0, reader.KnownColorCount);
        Assert.Null(reader.KnownColor(record.Sha256));
        Assert.Equal(0, reader.MetadataEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remembered_colors_validate_and_watch_sources_without_reading_headers(bool changedWhileClosed)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, _) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var writer = Store(catalog)) Assert.NotNull(await writer.Request(record.Sha256));
        var path = catalog.SourcePath(record)!;
        if (changedWhileClosed) File.WriteAllBytes(path, [0, 1, 2, 3]);
        await using (var reader = Store(catalog))
        {
            if (!changedWhileClosed)
            {
                Assert.NotNull(reader.KnownColor(record.Sha256));
                await Eventually(() => Private<ConcurrentDictionary<string, string>>(reader, "_watchedFiles").ContainsKey(path));
                File.WriteAllBytes(path, [0, 1, 2, 3]);
                Call(reader, "FileChanged", path); // The same callback as a native watcher, deterministic on this Mac.
            }
            await Eventually(() => reader.KnownColor(record.Sha256) is null);
            Assert.Equal(0, reader.MetadataEntries);
            Assert.Equal(0, reader.DiskHits);
            Assert.Equal(0, reader.Builds);
        }
        await using var reopened = Store(catalog);
        Assert.Null(reopened.KnownColor(record.Sha256));
    }

    [Fact]
    public async Task Invalidated_header_cannot_resurrect_a_remembered_color()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, header) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var writer = Store(catalog)) Assert.NotNull(await writer.Request(record.Sha256));
        await using (var store = Store(catalog))
        {
            Assert.NotNull(store.KnownColor(record.Sha256));
            // Deterministic worker interleaving: a header has been read; the watcher
            // invalidates its request before CompleteHeader publishes the result.
            var completion = new TaskCompletionSource<StampDerivedAssetStore.Header?>();
            Private<ConcurrentDictionary<string, TaskCompletionSource<StampDerivedAssetStore.Header?>>>(store, "_requests")[record.Sha256] = completion;
            Call(store, "Watch", record, catalog.SourcePath(record)!);
            Call(store, "FileChanged", catalog.SourcePath(record)!);
            Assert.Null(store.KnownColor(record.Sha256));
            Call(store, "CompleteHeader", record.Sha256, completion, header);
            Assert.Null(store.KnownColor(record.Sha256));
            Assert.Null(await completion.Task);
        }
        await using var reopened = Store(catalog);
        Assert.Null(reopened.KnownColor(record.Sha256));
    }

    [Fact]
    public async Task Retired_failure_cannot_forget_the_newer_successful_color()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, _) = ColorCacheFixture.Seed(catalog, Cache);
        await using var store = Store(catalog);
        Assert.NotNull(await store.Request(record.Sha256));
        var expected = store.KnownColor(record.Sha256);
        Assert.NotNull(expected);
        Call(store, "CompleteHeader", record.Sha256, new TaskCompletionSource<StampDerivedAssetStore.Header?>(), null);
        Assert.Equal(expected, store.KnownColor(record.Sha256));
    }

    [Fact]
    public async Task Failed_color_save_is_retried_on_async_shutdown()
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, _) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var writer = Store(catalog)) Assert.NotNull(await writer.Request(record.Sha256));
        await using (var store = Store(catalog))
        {
            Assert.NotNull(store.KnownColor(record.Sha256));
            Call(store, "ForgetColor", record.Sha256);
            File.Move(Colors, Colors + ".held");
            Directory.CreateDirectory(Colors); // A directory cannot be replaced by the atomic file move.
            try
            {
                Call(store, "SaveKnownColors");
                Assert.Equal(1, store.Failures);
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Colors)!, "*.tmp"));
            }
            finally
            {
                Directory.Delete(Colors);
                File.Move(Colors + ".held", Colors);
            }
        }
        await using var reopened = Store(catalog);
        Assert.Null(reopened.KnownColor(record.Sha256));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Image_source_observes_header_correction_and_failure(bool missing)
    {
        await using var catalog = new AssetCatalog(Path.Combine(_root, "catalog"));
        var (record, header) = ColorCacheFixture.Seed(catalog, Cache);
        await using (var writer = Store(catalog)) Assert.NotNull(await writer.Request(record.Sha256));
        var document = new MapDocument(DocumentId.New(), "colors", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(1024, 1024, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "paper", true, false, 1, "#FFFFFFFF")], []);
        await using var source = new CatalogStampImageSource(catalog, () => document, () => null,
            derivedRoot: Cache, startWatcher: _ => { });
        Assert.True(source.PrepareOverview([record.Sha256], () => { }));
        Assert.Equal(new SKColor(header.Color), source.TryGetOverviewColor(record.Sha256));
        var derived = Private<StampDerivedAssetStore>(source, "_derived");
        var completion = new TaskCompletionSource<StampDerivedAssetStore.Header?>();
        Private<ConcurrentDictionary<string, TaskCompletionSource<StampDerivedAssetStore.Header?>>>(derived, "_requests")[record.Sha256] = completion;
        var corrected = header with { Color = (uint)SKColors.Blue };
        Call(derived, "CompleteHeader", record.Sha256, completion, missing ? null : corrected);
        // No watcher event: portable validation/header failure also changes the store's palette.
        Assert.Equal(missing ? (SKColor?)null : SKColors.Blue, source.TryGetOverviewColor(record.Sha256));
    }

    private StampDerivedAssetStore Store(AssetCatalog catalog) => new(catalog, Cache, startWatcher: _ => { });
    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(condition(), "The remembered source was never watched or invalidated.");
    }
    private static T Private<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Call(object target, string method, params object?[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
