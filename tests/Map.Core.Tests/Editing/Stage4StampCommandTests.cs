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
}
