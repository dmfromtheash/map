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
    private static T Private<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Call(object target, string method, params object?[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
