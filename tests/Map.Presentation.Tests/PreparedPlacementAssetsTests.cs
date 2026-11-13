using Map.Assets;
using Map.Core.Documents;
using Map.Presentation.Tests.Support;
using Map.Presentation.ViewModels;

namespace Map.Presentation.Tests;

public sealed class PreparedPlacementAssetsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prepared-assets-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssetCatalog> _catalogs = [];

    [Fact]
    public async Task Pending_preparation_refuses_atomically_and_ready_payload_is_reused_by_commit_undo_redo()
    {
        var (editor, catalog, resolver) = Create();
        resolver.Block = true;
        editor.SelectAssetSet(catalog.Sets[0].Id);
        var preparation = editor.PreparePlacementAssetsAsync();
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(editor.PlaceCurrentVariantAt(200, 200));
            Assert.Empty(editor.Session.Document.Assets);
            Assert.Equal(0, editor.Session.UndoDepth);
        }
        finally { resolver.Release.Set(); }
        await preparation.WaitAsync(TimeSpan.FromSeconds(10));
        var reads = resolver.Calls;
        Assert.True(editor.PlaceCurrentVariantAt(200, 200));
        var reference = editor.Session.Document.Assets.Single();
        Assert.Same(resolver.LastReference, reference);
        editor.Undo(); Assert.Empty(editor.Session.Document.Assets);
        editor.Redo(); Assert.Same(reference, editor.Session.Document.Assets.Single());
        editor.Undo();
        Assert.True(editor.PlaceCurrentVariantAt(300, 300));
        Assert.Same(reference, editor.Session.Document.Assets.Single());
        Assert.Equal(reads, resolver.Calls);
    }

    [Fact]
    public async Task Explicit_linked_action_waits_for_worker_then_commits_ready_reference()
    {
        var (editor, catalog, resolver) = Create();
        resolver.Block = true;
        var pending = editor.PlaceStampAsync(catalog.Records[0], embedded: false, x: 100, y: 100);
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.Empty(editor.Session.Document.Assets); Assert.Equal(0, editor.Session.UndoDepth); }
        finally { resolver.Release.Set(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        var reference = editor.Session.Document.Assets.Single();
        Assert.Equal(ProjectAssetStorage.Linked, reference.Storage);
        Assert.Null(reference.EmbeddedContent);
        Assert.Equal(1, resolver.Calls);
        editor.Undo(); Assert.Empty(editor.Session.Document.Assets);
        editor.Redo(); Assert.Same(reference, editor.Session.Document.Assets.Single());
    }

    [Fact]
    public async Task Mask_and_stamp_are_both_prepared_and_commit_performs_no_resolver_reads()
    {
        var (editor, catalog, resolver) = Create(mask: true);
        editor.SelectAssetSet(catalog.Sets[0].Id);
        await editor.PreparePlacementAssetsAsync();
        Assert.Equal(2, resolver.Calls);
        Assert.True(editor.PlaceCurrentVariantAt(200, 200));
        Assert.Equal(2, editor.Session.Document.Assets.Count);
        Assert.Equal(2, resolver.Calls);
        editor.Undo(); Assert.Empty(editor.Session.Document.Assets);
        editor.Redo(); Assert.Equal(2, editor.Session.Document.Assets.Count);
    }

    [Fact]
    public async Task Failed_resolution_never_publishes_a_partial_document()
    {
        var (editor, catalog, resolver) = Create(mask: true);
        resolver.Failure = catalog.MaskRecordForVariant(catalog.FindSet(catalog.Sets[0].Id)!.VariantIds[0])!.Sha256;
        editor.SelectAssetSet(catalog.Sets[0].Id);
        await editor.PreparePlacementAssetsAsync();
        var before = editor.Session.Document;
        Assert.False(editor.PlaceCurrentVariantAt(200, 200));
        Assert.Same(before, editor.Session.Document);
        Assert.Equal(0, editor.Session.UndoDepth);
    }

    [Fact]
    public async Task Changing_card_during_preparation_cannot_commit_the_previous_cards_reference()
    {
        var (editor, catalog, resolver) = Create();
        var firstSet = catalog.Sets[0].Id;
        var second = catalog.Import(Fixture("owner-mountain-02.png"), new(), DateTimeOffset.UnixEpoch);
        editor.RefreshLibrary();
        resolver.Block = true;
        editor.SelectAssetSet(firstSet);
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var previousHash = editor.CurrentVariant!.Sha256;
        try
        {
            editor.SelectAssetSet(catalog.FindSetForContent(second.Sha256)!.Id);
            Assert.False(editor.PlaceCurrentVariantAt(200, 200));
        }
        finally { resolver.Release.Set(); }
        await editor.PreparePlacementAssetsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(editor.PlaceCurrentVariantAt(200, 200));
        Assert.DoesNotContain(editor.Session.Document.Assets, asset => asset.Sha256 == previousHash);
        Assert.Equal(editor.CurrentVariant!.Sha256, editor.Session.Document.Assets.Single().Sha256);
    }

    private (EditorViewModel Editor, AssetCatalog Catalog, Resolver Resolver) Create(bool mask = false)
    {
        var catalog = new AssetCatalog(Path.Combine(_root, Guid.NewGuid().ToString("N"))); _catalogs.Add(catalog);
        catalog.ImportSet([Fixture("owner-mountain-01.png")], "Гора", new(), DateTimeOffset.UnixEpoch);
        if (mask) catalog.AssignVariantMask(catalog.FindSet(catalog.Sets[0].Id)!.VariantIds[0],
            Fixture("owner-mountain-02.png"), DateTimeOffset.UnixEpoch);
        var resolver = new Resolver(new CatalogAssetContentResolver(catalog));
        var document = new MapDocument(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
            new(1000, 1000, "px", "#FFFFFFFF"),
            [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
             new ObjectLayer(LayerId.New(), "objects", true, false, 1, [])]);
        var editor = new EditorViewModel(document, null, false, new FakeProjectStore(), new FakeRecentMaps(),
            new FakeFilePicker(), new FakePrompts(), new FakeLog(), assetCatalog: catalog, assetResolver: resolver)
            { AllowsBackgroundAssetPreparation = true };
        return (editor, catalog, resolver);
    }
    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Map.slnx"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "tests", "fixtures", "assets", name);
    }
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        foreach (var catalog in _catalogs) await catalog.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    private sealed class Resolver(IAssetContentResolver inner) : IAssetContentResolver
    {
        public int Calls;
        public bool Block;
        public string? Failure;
        public MapAssetReference? LastReference;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public AssetContentResolution Resolve(AssetRecord record)
        {
            Interlocked.Increment(ref Calls); Started.TrySetResult();
            if (Block && !Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test resolver gate.");
            var result = record.Sha256 == Failure ? AssetContentResolution.Failed(AssetAvailability.HashMismatch) : inner.Resolve(record);
            LastReference = result.Reference;
            return result;
        }
        public AssetAvailability Check(AssetRecord record) => inner.Check(record);
    }
}
