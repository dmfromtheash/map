using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class AssetUsageDeltaTests
{
    private static readonly string A = new('a', 64), B = new('b', 64);
    private static SceneObject Stamp(string hash, string? mask = null) => new(ObjectId.New(), SceneObjectKind.Stamp,
        "stamp", 10, 10, 30, 30, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        { Stamp = new StampStyle(hash) { MaskAssetSha256 = mask } };
    private static SceneObject Composition(params SceneObject[] children) => new(ObjectId.New(), SceneObjectKind.Composition,
        "nested", 0, 0, 100, 100, 0, "#00000000", "#00000000", 0, true, false)
        { Composition = new(CompositionId.New(), DocumentId.New(), "nested", 100, 100, children) };
    private static MapAssetReference Asset(string hash) => new(hash, "asset", "image/png", ".png",
        ProjectAssetStorage.Linked, DateTimeOffset.UnixEpoch, "test", "user-owned", catalogId: hash);
    private static MapDocument Doc(params SceneObject[] items) => new(DocumentId.New(), "test", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1000, 1000, "px", "#FFFFFFFF"),
        [new BaseColorLayer(LayerId.New(), "base", true, false, 1, "#FFFFFFFF"),
         new ObjectLayer(LayerId.New(), "objects", true, false, 1, items)]);
    private static ObjectLayer Layer(MapDocument doc) => doc.Layers.OfType<ObjectLayer>().Single();

    [Fact]
    public void Nested_repeated_masks_and_filter_references_count_each_slot_case_insensitively()
    {
        var nested = Composition(Stamp(A, A), Composition(Stamp(B, A)));
        nested = nested with { Composition = nested.Composition! with { Filters = [new() { TextureAssetSha256 = B }] } };
        var doc = Doc(nested, Stamp(A.ToUpperInvariant()));
        Assert.Equal(4, AssetUsageIndex.Count(doc, A));
        Assert.Equal(2, AssetUsageIndex.Count(doc, B.ToUpperInvariant()));
        Assert.Equal(1, AssetUsageIndex.For(Layer(doc)).StampCount);
    }

    [Fact]
    public void Replacing_nested_contents_updates_parent_delta_and_undo_redo_counts()
    {
        var original = Composition(Composition(Stamp(A, B)));
        var changed = original with { Composition = original.Composition! with { Items = [Composition(Stamp(B, B))] } };
        var doc = Doc(original); var session = new EditingSession(doc);
        session.Execute(new ReplaceObjectsCommand([original], [changed], "nested edit"));
        Assert.Equal(0, AssetUsageIndex.Count(session.Document, A));
        Assert.Equal(2, AssetUsageIndex.Count(session.Document, B));
        Assert.True(session.Undo());
        Assert.Equal(1, AssetUsageIndex.Count(session.Document, A));
        Assert.Equal(1, AssetUsageIndex.Count(session.Document, B));
        Assert.True(session.Redo());
        Assert.Equal(2, AssetUsageIndex.Count(session.Document, B));
        Assert.Equal(1, AssetUsageIndex.Count(doc, A));
    }

    [Fact]
    public void Append_replace_delete_undo_redo_leave_original_counts_unchanged()
    {
        var first = Stamp(A, B); var doc = Doc(first); var session = new EditingSession(doc);
        var added = Stamp(B, B);
        session.Execute(new AddObjectCommand(Layer(doc).Id, added));
        Assert.Equal(3, AssetUsageIndex.Count(session.Document, B));
        session.Execute(new ReplaceObjectsCommand([first], [first with { Stamp = new StampStyle(B) }], "replace"));
        Assert.Equal(0, AssetUsageIndex.Count(session.Document, A));
        Assert.Equal(3, AssetUsageIndex.Count(session.Document, B));
        session.Execute(new DeleteObjectsCommand(session.Document, [added.Id]));
        Assert.Equal(1, AssetUsageIndex.Count(session.Document, B));
        for (var i = 0; i < 3; i++) Assert.True(session.Undo());
        Assert.Equal(1, AssetUsageIndex.Count(session.Document, A));
        Assert.Equal(1, AssetUsageIndex.Count(session.Document, B));
        for (var i = 0; i < 3; i++) Assert.True(session.Redo());
        Assert.Equal(1, AssetUsageIndex.Count(session.Document, B));
        Assert.Equal(1, AssetUsageIndex.Count(doc, A));
        Assert.Equal(1, AssetUsageIndex.Count(doc, B));
    }

    [Fact]
    public void Undo_new_reference_preserves_nested_survivor_and_old_snapshot_then_redo_works()
    {
        var doc = Doc(Composition(Composition(Stamp(B, A))));
        var command = new PlaceAssetObjectsCommand(Layer(doc).Id, Asset(A), [Stamp(A)]);
        var added = command.Apply(doc);
        var reverted = command.Revert(added);
        Assert.Same(added.Assets.Single(), reverted.Assets.Single());
        Assert.Equal(1, AssetUsageIndex.Count(reverted, A));
        Assert.Equal(2, AssetUsageIndex.Count(added, A));
        var redone = command.Apply(reverted);
        Assert.Single(redone.Assets);
        Assert.Equal(2, AssetUsageIndex.Count(redone, A));
    }

    [Fact]
    public void New_payload_is_withdrawn_only_after_last_use_and_history_reuses_reference()
    {
        var doc = Doc(); var asset = Asset(A); var session = new EditingSession(doc);
        session.Execute(new PlaceAssetObjectsCommand(Layer(doc).Id, asset, [Stamp(A, A)]));
        Assert.Equal(2, AssetUsageIndex.Count(session.Document, A));
        Assert.True(session.Undo());
        Assert.Empty(session.Document.Assets);
        Assert.True(session.Redo());
        Assert.Same(asset, session.Document.Assets.Single());
        Assert.Equal(2, AssetUsageIndex.Count(session.Document, A));
    }

    [Fact]
    public void Arbitrary_layer_replacement_and_layer_deletion_have_safe_counts()
    {
        var doc = Doc(Stamp(A)); var old = Layer(doc);
        var replaced = doc.WithLayer(old.Id, _ => old with { Objects = [Composition(Stamp(B, B))] });
        Assert.Equal(0, AssetUsageIndex.Count(replaced, A));
        Assert.Equal(2, AssetUsageIndex.Count(replaced, B));
        var removed = replaced.WithLayers([replaced.BaseColor]);
        Assert.Equal(0, AssetUsageIndex.Count(removed, B));
        Assert.Equal(1, AssetUsageIndex.Count(doc, A));
    }

    [Fact]
    public void New_reference_undo_keeps_filter_and_background_uses()
    {
        var doc = Doc();
        doc = doc.WithLayer(doc.BaseColor.Id, layer => ((BaseColorLayer)layer) with { TextureId = "asset:" + A });
        doc = doc.WithLayers([.. doc.Layers, new FilterLayer(LayerId.New(), "filter", true, false, 1,
            new() { TextureAssetSha256 = A })]);
        var command = new PlaceAssetObjectsCommand(Layer(doc).Id, Asset(A), [Stamp(A)]);
        var reverted = command.Revert(command.Apply(doc));
        Assert.Single(reverted.Assets);
        Assert.Equal(2, AssetUsageIndex.Count(reverted, A));
    }

    [Fact]
    public void Composition_paste_undo_cannot_remove_a_payload_used_by_another_object()
    {
        var doc = Doc(Stamp(B, A));
        var command = new PasteCompositionCommand(Layer(doc).Id, Composition(Stamp(A)), [Asset(A)]);
        var reverted = command.Revert(command.Apply(doc));
        Assert.Single(reverted.Assets);
        Assert.Equal(1, AssetUsageIndex.Count(reverted, A));
    }
}
