using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Editing;

public sealed class Stage4StampCommandTests
{
    [Fact]
    public void Scatter_and_asset_reference_are_one_undoable_transaction()
    {
        var layerId = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var stamps = Enumerable.Range(0, 100).Select(index => new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, $"tree {index}",
            index * 10, 100, 48, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(asset.Sha256),
        }).ToList();
        var session = Session(layerId);

        session.Execute(new PlaceAssetObjectsCommand(layerId, asset, stamps));

        Assert.Equal(1, session.UndoDepth);
        Assert.Equal(100, ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Count);
        Assert.Single(session.Document.Assets);
        Assert.True(session.Undo());
        Assert.Empty(((ObjectLayer)session.Document.FindLayer(layerId)!).Objects);
        Assert.Empty(session.Document.Assets);
        Assert.True(session.Redo());
        Assert.Equal(stamps.Select(item => item.Id),
            ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Select(item => item.Id));
    }

    [Fact]
    public void Consecutive_scatter_batches_extend_the_revision_index_without_losing_old_objects()
    {
        var layerId = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var session = Session(layerId);
        _ = DocumentRevisionIndex.For(session.Document);

        var first = Stamps(asset, 256, "first");
        var second = Stamps(asset, 384, "second");
        session.Execute(new PlaceAssetObjectsCommand(layerId, asset, first));
        session.Execute(new PlaceAssetObjectsCommand(layerId, asset, second));

        var index = DocumentRevisionIndex.For(session.Document);
        Assert.Equal(640, index.ObjectCount);
        Assert.Same(first[0], index.FindObject(first[0].Id));
        Assert.Same(second[^1], index.FindObject(second[^1].Id));

        Assert.True(session.Undo());
        var afterUndo = DocumentRevisionIndex.For(session.Document);
        Assert.Equal(256, afterUndo.ObjectCount);
        Assert.Null(afterUndo.FindObject(second[0].Id));
        Assert.True(session.Redo());
        Assert.Equal(640, DocumentRevisionIndex.For(session.Document).ObjectCount);
    }

    [Fact]
    public void Append_delta_rejects_an_existing_object_id_and_a_conflicting_group_name()
    {
        var layerId = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var groupId = Guid.NewGuid();
        var first = Stamps(asset, 1, "first")[0] with
        {
            GroupId = groupId,
            GroupName = "forest",
        };
        var session = Session(layerId);
        session.Execute(new PlaceAssetObjectsCommand(layerId, asset, [first]));

        Assert.Throws<ArgumentException>(() =>
            session.Execute(new PlaceAssetObjectsCommand(layerId, asset, [first])));

        var conflicting = Stamps(asset, 1, "second")[0] with
        {
            GroupId = groupId,
            GroupName = "mountains",
        };
        Assert.Throws<ArgumentException>(() =>
            session.Execute(new PlaceAssetObjectsCommand(layerId, asset, [conflicting])));
    }

    [Fact]
    public void Append_after_a_replacement_uses_current_nested_identities_not_stale_root_data()
    {
        var layerId = LayerId.New();
        var asset = Embedded(new string('a', 64), "tree");
        var releasedChildId = ObjectId.New();
        var currentChildId = ObjectId.New();
        var holder = Composition(releasedChildId);
        var document = Session(layerId).Document.WithLayer(layerId, layer =>
            ((ObjectLayer)layer) with { Objects = [holder] });
        var session = new EditingSession(document);
        _ = DocumentRevisionIndex.For(session.Document);

        var replacement = holder with
        {
            Composition = holder.Composition! with
            {
                Items = [Rectangle(currentChildId, "current child")],
            },
        };
        session.Execute(new ReplaceObjectsCommand([holder], [replacement], "Replace composition child"));

        var releasedIdStamp = Stamps(asset, 1, "released")[0] with { Id = releasedChildId };
        session.Execute(new PlaceAssetObjectsCommand(layerId, asset, [releasedIdStamp]));
        Assert.Equal(2, ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Count);

        var currentIdStamp = Stamps(asset, 1, "collision")[0] with { Id = currentChildId };
        Assert.Throws<ArgumentException>(() =>
            session.Execute(new PlaceAssetObjectsCommand(layerId, asset, [currentIdStamp])));
    }

    [Fact]
    public void Explicit_replacement_changes_hash_and_undo_restores_original_reference()
    {
        var layerId = LayerId.New();
        var original = Embedded(new string('a', 64), "old");
        var replacement = Embedded(new string('b', 64), "new");
        var stamp = new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, "old", 0, 0, 100, 100, 0,
            "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(original.Sha256),
        };
        var document = Session(layerId).Document
            .WithAssets([original])
            .WithLayer(layerId, layer => ((ObjectLayer)layer) with { Objects = [stamp] });
        var session = new EditingSession(document);

        session.Execute(new ReplaceStampAssetCommand(stamp, replacement));

        Assert.Equal(replacement.Sha256, session.SelectedObject?.Stamp?.AssetSha256
            ?? ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Single().Stamp!.AssetSha256);
        Assert.Equal(2, session.Document.Assets.Count);
        Assert.True(session.Undo());
        Assert.Equal(original.Sha256,
            ((ObjectLayer)session.Document.FindLayer(layerId)!).Objects.Single().Stamp!.AssetSha256);
        Assert.Single(session.Document.Assets);
    }

    private static EditingSession Session(LayerId layerId) => new(new MapDocument(
        DocumentId.New(), "stamps", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(65_536, 65_536, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1, "#FFFFFFFF"),
            new ObjectLayer(layerId, "objects", true, false, 1, []),
        ]));

    private static MapAssetReference Embedded(string hash, string name) => new(
        hash, name, "image/png", "png", ProjectAssetStorage.Embedded,
        DateTimeOffset.UnixEpoch, "test", "test", [1, 2, 3]);

    private static IReadOnlyList<SceneObject> Stamps(
        MapAssetReference asset, int count, string prefix) =>
        [.. Enumerable.Range(0, count).Select(index => new SceneObject(
            ObjectId.New(), SceneObjectKind.Stamp, $"{prefix} {index}",
            index, index, 48, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false)
        {
            Stamp = new StampStyle(asset.Sha256),
        })];

    private static SceneObject Composition(ObjectId childId) => new(
        ObjectId.New(), SceneObjectKind.Composition, "composition",
        0, 0, 48, 48, 0, "#FFFFFFFF", "#00000000", 0, true, false)
    {
        Composition = new EditableComposition(
            CompositionId.New(), DocumentId.New(), "parts", 48, 48,
            [Rectangle(childId, "released child")]),
    };

    private static SceneObject Rectangle(ObjectId id, string name) => new(
        id, SceneObjectKind.Rectangle, name,
        0, 0, 12, 12, 0, "#FFFFFFFF", "#00000000", 0, true, false);
}
